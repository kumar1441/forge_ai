using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class FillSurfaceResult
    {
        public int SheetBodyCountBefore;
        public int SheetBodyCountAfter;
        public int OpenRimCountBefore;
        public int OpenRimCountAfter;
        public int RimsFilled;
        public bool AlreadyDone;
        public bool Verified;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 226 — fill_surface (WRITE). Patches open boundary loop(s) ("holes"/"gaps") in surface (sheet)
    /// bodies with IFeatureManager.InsertFillSurface2 — confirmed LIVE (it's the exact call
    /// the test fixture generator already uses to build its own cap fixtures, unlike the DEAD
    /// InsertSewRefSurface that PARKED tool 181 knit_surfaces_to_solid). "fill the gap in this surface",
    /// "patch the open end of the surface body".
    ///
    /// GOTCHA: a rim edge on the source body always shows only 1 adjacent face (adj&lt;=1 via
    /// IGetTwoAdjacentFaces2) whether or not it's ALREADY capped, because InsertFillSurface2 creates a
    /// separate un-knit sheet body rather than joining topologically — so raw naked-edge-count can never reach
    /// zero even after a successful fill. "Open" is therefore determined by CROSS-BODY edge coincidence: a
    /// naked edge counts as truly OPEN only if no OTHER sheet body has a geometrically coincident naked edge
    /// (same endpoint coordinates) — that coincidence IS what "already capped" looks like. This also makes the
    /// handler correctly idempotent: after filling, the new cap's own boundary edges coincide with the source
    /// rim, so a rerun's cross-body match finds them CLOSED.
    /// </summary>
    public static class FillSurface
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            return Regex.IsMatch(c, @"\b(fill|patch)\b") && Regex.IsMatch(c, @"\bsurfaces?\b");
        }

        public static async Task<FillSurfaceResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new FillSurfaceResult();
            var part = model as PartDoc;
            if (part == null) { res.Error = "Open a part with surface bodies to fill a gap in one."; return res; }

            await emit("Gauge", "scanning surface bodies for open boundaries", "run", null);
            var sheets0 = SheetBodies(part);
            res.SheetBodyCountBefore = sheets0.Count;
            if (sheets0.Count == 0)
            { res.Error = "No surface bodies found in this part — nothing to fill."; await emit("Sentinel", null, "fail", res.Error); return res; }

            var openRims0 = FindOpenRims(sheets0);
            res.OpenRimCountBefore = openRims0.Count;
            await emit("Gauge", null, "done", sheets0.Count + " surface body(ies), " + openRims0.Count + " open boundary(ies)");

            if (openRims0.Count == 0)
            {
                res.AlreadyDone = true; res.Verified = true;
                res.Info = "No open boundaries found — every surface body is already closed.";
                await emit("Sentinel", null, "done", "already closed");
                return res;
            }

            await emit("Scribe", "filling " + openRims0.Count + " open boundary(ies)", "run", null);
            int filled = 0;
            foreach (var rim in openRims0)
            {
                model.ClearSelection2(true);
                var boundaryArr = rim.ToArray();
                var curvCtrl = new int[boundaryArr.Length];   // all 0 = contact only, no tangency
                Feature capFeat = null;
                try { capFeat = model.FeatureManager.InsertFillSurface2(0, 0, boundaryArr, curvCtrl, null, null) as Feature; } catch { }
                model.ClearSelection2(true);
                if (capFeat != null) filled++;
            }
            model.ForceRebuild3(false);
            res.RimsFilled = filled;

            if (filled == 0)
            {
                res.Error = "InsertFillSurface2 didn't commit any patch — the open boundary may be non-planar or self-intersecting.";
                await emit("Scribe", null, "fail", res.Error);
                return res;
            }

            await emit("Sentinel", "verifying the patch", "run", null);
            var sheetsAfter = SheetBodies(part);
            res.SheetBodyCountAfter = sheetsAfter.Count;
            var openRimsAfter = FindOpenRims(sheetsAfter);
            res.OpenRimCountAfter = openRimsAfter.Count;

            res.Verified = res.SheetBodyCountAfter == res.SheetBodyCountBefore + filled && res.OpenRimCountAfter < res.OpenRimCountBefore;
            if (!res.Verified)
            {
                res.Error = "Fill didn't verify — sheet bodies " + res.SheetBodyCountBefore + "->" + res.SheetBodyCountAfter +
                    ", open boundaries " + res.OpenRimCountBefore + "->" + res.OpenRimCountAfter + ".";
                await emit("Sentinel", null, "fail", res.Error);
                return res;
            }

            res.Info = "Filled " + filled + " open boundary(ies): " + res.OpenRimCountBefore + " -> " + res.OpenRimCountAfter +
                " remaining. One Ctrl+Z restores the gap; Forge didn't save.";
            await emit("Sentinel", null, "done", filled + " patched, " + res.OpenRimCountAfter + " open remaining");
            return res;
        }

        // ---- shared open-rim detection (also used by GroundTruth.MeasureFillSurface for an INDEPENDENT check) ----
        public static List<List<Edge>> FindOpenRims(List<Body2> sheetBodies)
        {
            var nakedByBody = new List<List<Edge>>();
            var vkeyOf = new Dictionary<Edge, string[]>();
            for (int bi = 0; bi < sheetBodies.Count; bi++)
            {
                var list = new List<Edge>();
                var faces = sheetBodies[bi].GetFaces() as object[];
                foreach (var fo in faces ?? new object[0])
                {
                    var face = fo as Face2; if (face == null) continue;
                    var edges = face.GetEdges() as object[];
                    foreach (var eo in edges ?? new object[0])
                    {
                        var edge = eo as Edge; if (edge == null || list.Contains(edge)) continue;
                        Face2 f1 = null, f2 = null;
                        try { edge.IGetTwoAdjacentFaces2(out f1, out f2); } catch { }
                        int adj = (f1 != null ? 1 : 0) + (f2 != null ? 1 : 0);
                        if (adj > 1) continue;   // shared edge within the same body — not a boundary
                        var v1 = edge.IGetStartVertex() as Vertex; var v2 = edge.IGetEndVertex() as Vertex;
                        var p1 = v1 == null ? null : v1.GetPoint() as double[];
                        var p2 = v2 == null ? null : v2.GetPoint() as double[];
                        if (p1 == null || p2 == null) continue;
                        list.Add(edge);
                        vkeyOf[edge] = new[] { VKey(p1), VKey(p2) };
                    }
                }
                nakedByBody.Add(list);
            }

            // an edge's UNORDERED endpoint-key pair -> the set of DISTINCT bodies that have a naked edge there
            var bodiesByPairKey = new Dictionary<string, HashSet<int>>();
            for (int bi = 0; bi < nakedByBody.Count; bi++)
                foreach (var edge in nakedByBody[bi])
                {
                    string pk = PairKey(vkeyOf[edge]);
                    HashSet<int> set;
                    if (!bodiesByPairKey.TryGetValue(pk, out set)) { set = new HashSet<int>(); bodiesByPairKey[pk] = set; }
                    set.Add(bi);
                }

            // OPEN = naked edges whose pair-key maps to exactly ONE body (no coincident cap on a different body)
            var rims = new List<List<Edge>>();
            for (int bi = 0; bi < nakedByBody.Count; bi++)
            {
                var open = nakedByBody[bi].Where(e => bodiesByPairKey[PairKey(vkeyOf[e])].Count == 1).ToList();
                if (open.Count == 0) continue;

                // cluster this body's open edges into rim loops by shared vertex key (union-find)
                var groupOf = new Dictionary<string, int>();
                var groups = new List<List<Edge>>();
                foreach (var edge in open)
                {
                    var keys = vkeyOf[edge];
                    int g1 = groupOf.ContainsKey(keys[0]) ? groupOf[keys[0]] : -1;
                    int g2 = groupOf.ContainsKey(keys[1]) ? groupOf[keys[1]] : -1;
                    int g = g1 >= 0 ? g1 : (g2 >= 0 ? g2 : groups.Count);
                    if (g == groups.Count) groups.Add(new List<Edge>());
                    groups[g].Add(edge);
                    groupOf[keys[0]] = g; groupOf[keys[1]] = g;
                    if (g1 >= 0 && g2 >= 0 && g1 != g2)
                    {
                        groups[g1].AddRange(groups[g2]);
                        foreach (var k in new List<string>(groupOf.Keys)) if (groupOf[k] == g2) groupOf[k] = g1;
                        groups[g2].Clear();
                    }
                }
                rims.AddRange(groups.Where(g => g.Count > 0));
            }
            return rims;
        }

        private static string PairKey(string[] keys) => string.CompareOrdinal(keys[0], keys[1]) <= 0 ? keys[0] + "|" + keys[1] : keys[1] + "|" + keys[0];
        private static string VKey(double[] p) => Math.Round(p[0], 6) + "," + Math.Round(p[1], 6) + "," + Math.Round(p[2], 6);

        public static List<Body2> SheetBodies(PartDoc part)
        {
            var list = new List<Body2>();
            try
            {
                var b = part.GetBodies2((int)swBodyType_e.swSheetBody, false) as object[];
                foreach (var o in b ?? new object[0]) { var body = o as Body2; if (body != null) list.Add(body); }
            }
            catch { }
            return list;
        }
    }
}
