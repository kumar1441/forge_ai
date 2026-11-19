using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class CompareBodiesResult
    {
        public int TotalBodies;
        public int DuplicateGroups;     // fingerprint groups with >= 2 members
        public int BodiesInDupGroups;   // bodies that have at least one exact duplicate
        public int UniqueShapes;        // distinct geometry fingerprints
        public int OverlappingPairs;    // body pairs with a positive solid-intersection volume
        public string OverlapMethod;    // "solid-intersection" | "unavailable"
        public List<string> Duplicates = new List<string>();
        public List<string> Overlaps = new List<string>();
        public string Diag;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 170 — compare_bodies (READ, within one part). The body-level sibling of find_duplicate_components: finds
    /// EXACT-duplicate solid bodies by a geometry fingerprint (volume + surface area + sorted bounding-box dims) and
    /// OVERLAPPING bodies by a real solid intersection (IBody2.Operations2 SWBODYINTERSECT on temp copies), reporting
    /// the overlap volume % of the smaller body per pair. Read-only: it copies bodies to temporaries and never edits the
    /// model. Fail-closed on the overlap half — if the boolean op is unavailable headless it says so rather than
    /// silently reporting zero overlaps.
    /// </summary>
    public static class CompareBodies
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            return System.Text.RegularExpressions.Regex.IsMatch(c, @"\b(compare|duplicate|duplicates|identical|overlap|overlapping|interfer)\w*\b") &&
                   System.Text.RegularExpressions.Regex.IsMatch(c, @"\bbodies\b|\bbody\b|\bsolids?\b");
        }

        private sealed class B { public Body2 Body; public string Name; public double Vol; public double Area; public double[] Box; public string Fp; }

        public static async Task<CompareBodiesResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new CompareBodiesResult();
            var part = model as PartDoc;
            if (part == null) { res.Error = "Open a part (.SLDPRT) to compare its bodies."; return res; }

            await emit("Sentinel", "reading the solid bodies", "run", null);

            var list = new List<B>();
            foreach (var o in (part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]) ?? new object[0])
            {
                var body = o as Body2; if (body == null) continue;
                double vol, area; MassOf(body, out vol, out area);
                double[] box = null; try { box = body.GetBodyBox() as double[]; } catch { }
                double[] dims = BoxDims(box);
                string fp = R(vol) + "|" + R(area) + "|" + R(dims[0]) + "|" + R(dims[1]) + "|" + R(dims[2]);
                string nm = null; try { nm = body.Name; } catch { }
                list.Add(new B { Body = body, Name = nm ?? "Body", Vol = vol, Area = area, Box = box, Fp = fp });
            }
            res.TotalBodies = list.Count;

            // ---- exact duplicates: group by fingerprint ----
            var groups = new Dictionary<string, List<B>>();
            foreach (var b in list) { if (!groups.ContainsKey(b.Fp)) groups[b.Fp] = new List<B>(); groups[b.Fp].Add(b); }
            res.UniqueShapes = groups.Count;
            foreach (var kv in groups)
            {
                if (kv.Value.Count < 2) continue;
                res.DuplicateGroups++;
                res.BodiesInDupGroups += kv.Value.Count;
                var names = new List<string>(); foreach (var b in kv.Value) names.Add(b.Name);
                res.Duplicates.Add(string.Join(" = ", names) + "  (" + (kv.Value[0].Vol * 1e9).ToString("F0", CultureInfo.InvariantCulture) + " mm3 each)");
            }

            // ---- overlaps: real solid intersection on temp copies ----
            int okOps = 0, errOps = 0, lastErr = 0;
            for (int i = 0; i < list.Count; i++)
                for (int j = i + 1; j < list.Count; j++)
                {
                    double iv; int err; bool computed = IntersectVolume(list[i].Body, list[j].Body, out iv, out err);
                    if (computed) okOps++; else { errOps++; lastErr = err; }
                    if (computed && iv > 1e-12)
                    {
                        res.OverlappingPairs++;
                        double smaller = Math.Min(list[i].Vol, list[j].Vol);
                        double pct = smaller > 0 ? (iv / smaller) * 100.0 : 0;
                        res.Overlaps.Add(list[i].Name + " ∩ " + list[j].Name + "  " + pct.ToString("F0", CultureInfo.InvariantCulture) + "% of the smaller body");
                    }
                }
            res.OverlapMethod = (okOps > 0) ? "solid-intersection" : "unavailable";
            res.Diag = "bodies=" + res.TotalBodies + " dupGroups=" + res.DuplicateGroups + " uniqueShapes=" + res.UniqueShapes +
                       " overlapPairs=" + res.OverlappingPairs + " intersectOk=" + okOps + " intersectErr=" + errOps + " lastErr=" + lastErr;

            await emit("Sentinel", null, "done", res.TotalBodies + " bodies · " + res.DuplicateGroups + " duplicate group(s) · " + res.OverlappingPairs + " overlapping pair(s)");

            if (res.TotalBodies == 0) { res.Error = "This part has no solid bodies."; return res; }
            var sb = new StringBuilder(res.TotalBodies + " solid bodies — " + res.UniqueShapes + " unique shape" + (res.UniqueShapes == 1 ? "" : "s") + ".");
            if (res.DuplicateGroups > 0) { sb.Append("\nExact duplicates:"); foreach (var d in res.Duplicates) sb.Append("\n• " + d); }
            else sb.Append("\nNo exact-duplicate bodies.");
            if (res.OverlapMethod == "unavailable") sb.Append("\nOverlap check unavailable on this build (solid-intersection API did not run).");
            else if (res.OverlappingPairs > 0) { sb.Append("\nOverlapping (interfering) bodies:"); foreach (var ov in res.Overlaps) sb.Append("\n• " + ov); }
            else sb.Append("\nNo overlapping bodies.");
            res.Info = sb.ToString();
            return res;
        }

        private static bool IntersectVolume(Body2 a, Body2 b, out double vol, out int err)
        {
            vol = 0; err = 0;
            try
            {
                var ca = a.Copy() as Body2; var cb = b.Copy() as Body2;
                if (ca == null || cb == null) { err = -99; return false; }
                object resObj = ca.Operations2((int)swBodyOperationType_e.SWBODYINTERSECT, cb, out err);
                if (err == (int)swBodyOperationError_e.swBodyOperationNoIntersect) { vol = 0; return true; }
                if (err != (int)swBodyOperationError_e.swBodyOperationNoError) return false;
                foreach (var o in (resObj as object[]) ?? new object[0])
                {
                    var rb = o as Body2; if (rb == null) continue;
                    double v, ar; MassOf(rb, out v, out ar); vol += v;
                }
                return true;
            }
            catch { err = -98; return false; }
        }

        private static void MassOf(Body2 body, out double vol, out double area)
        {
            vol = 0; area = 0;
            try
            {
                var mp = body.GetMassProperties(0) as double[];   // [3]=volume, [4]=surface area (SI)
                if (mp != null && mp.Length >= 5) { vol = mp[3]; area = mp[4]; }
            }
            catch { }
        }

        private static double[] BoxDims(double[] box)
        {
            if (box == null || box.Length < 6) return new double[] { 0, 0, 0 };
            double[] d = { Math.Abs(box[3] - box[0]), Math.Abs(box[4] - box[1]), Math.Abs(box[5] - box[2]) };
            Array.Sort(d);
            return d;
        }

        private static string R(double v) { return Math.Round(v, 9).ToString("0.#########", CultureInfo.InvariantCulture); }
    }
}
