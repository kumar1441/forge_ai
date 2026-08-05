using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class CountGearTeethResult
    {
        public bool Verified;
        public Dictionary<string, int> Counts = new Dictionary<string, int>();
        public List<string> Unresolved = new List<string>();
        public string Diag;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// READ (missing-capability gap diagnosed cycle 13): "check tooth count on both bevel gears", "sun gear teeth
    /// count?" — no handler anywhere counted gear teeth; ClassifyKind buckets everything gear-shaped into one
    /// generic "gear" kind with no tooth-count reasoning. Real test-loop gear models are imported/dumb solids (no
    /// feature-tree pattern, no driving dimension), so this counts PURELY FROM GEOMETRY: every tooth-tip land is a
    /// small residual patch of the gear's original outer surface, and cutting the tooth spaces around the rim
    /// splits that single surface into N separate, same-radius, evenly-spaced faces. Finds the repeating face
    /// group (grouped by face type + radius + axis-line identity, independent of which point-on-axis SolidWorks
    /// happens to report), verifies the members are evenly spaced around that axis within tolerance, and reports
    /// the group size as the tooth count. Only handles CYLINDRICAL tooth-tip bands (spur/helical gears) — a bevel
    /// gear's conical tip band is a documented, honestly-refused gap (no ConeParams index layout proven live on
    /// this build yet), not a silent wrong guess. Never writes; a "change tooth count" ask stays routed elsewhere
    /// (SetDimension's existing honesty path), this only ever counts.
    /// </summary>
    public static class CountGearTeeth
    {
        private static readonly Regex TeethWord = new Regex(@"\bteeth\b|\btooth\b", RegexOptions.IgnoreCase);
        private static readonly Regex CountWord = new Regex(@"\b(how many|count|number of|tooth count|teeth count)\b", RegexOptions.IgnoreCase);
        // "update the tooth count to 24" / "change ... teeth ... to 20" is a WRITE — stays on SetDimension's route.
        private static readonly Regex WriteVerb = new Regex(@"\b(change|set|update|adjust|make)\b.*\bto\s*\d+\b", RegexOptions.IgnoreCase);

        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            if (!TeethWord.IsMatch(cmd)) return false;
            if (!CountWord.IsMatch(cmd)) return false;
            if (WriteVerb.IsMatch(cmd)) return false;
            return true;
        }

        // Descriptive gear qualifiers pulled straight from the user's words ("bevel", "sun", "planet", "ring",
        // "pinion", "spur") — no hardcoded per-model vocabulary beyond the generic gear-family nouns themselves.
        private static readonly string[] Qualifiers = { "bevel", "sun", "planet", "ring", "pinion", "spur", "helical", "worm", "idler", "sprocket" };

        public static List<string> ExtractQualifiers(string cmd)
        {
            var c = (cmd ?? "").ToLowerInvariant();
            return Qualifiers.Where(q => c.Contains(q)).ToList();
        }

        public static async Task<CountGearTeethResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new CountGearTeethResult();
            if (model == null) { res.Error = "Open a part or assembly first."; return res; }

            await emit("Gauge", "locating the gear body/bodies", "run", null);
            var targets = new List<Tuple<string, Body2>>();
            int docType = (int)model.GetType();

            if (docType == (int)swDocumentTypes_e.swDocASSEMBLY)
            {
                var asm = model as AssemblyDoc;
                var quals = ExtractQualifiers(intent).Select(q => q.ToLowerInvariant()).ToList();
                foreach (var o in (asm.GetComponents(false) as object[]) ?? new object[0])
                {
                    var c = o as Component2; if (c == null) continue;
                    bool sup = false; try { sup = c.IsSuppressed(); } catch { }
                    if (sup) continue;
                    string name = null; try { name = c.Name2; } catch { }
                    string file = null;
                    try { var p = c.GetPathName(); file = string.IsNullOrEmpty(p) ? null : System.IO.Path.GetFileNameWithoutExtension(p); } catch { }
                    string probe = ((name ?? "") + " " + (file ?? "")).ToLowerInvariant();
                    if (!Regex.IsMatch(probe, @"\bgear\b|\bpinion\b|\bsprocket\b")) continue;
                    if (quals.Count > 0 && !quals.Any(q => probe.Contains(q))) continue;

                    object bi;
                    var bodies = c.GetBodies3((int)swBodyType_e.swSolidBody, out bi) as object[];
                    if (bodies == null || bodies.Length == 0) continue;
                    Body2 best = null; double bestVol = -1;
                    foreach (var bo in bodies)
                    {
                        var b = bo as Body2; if (b == null) continue;
                        double v = 0; try { var mp = b.GetMassProperties(0) as double[]; if (mp != null && mp.Length >= 4) v = mp[3]; } catch { }
                        if (v > bestVol) { bestVol = v; best = b; }
                    }
                    if (best != null) targets.Add(Tuple.Create(name ?? file ?? "gear", best));
                }
                if (targets.Count == 0)
                { res.Error = "Couldn't find a gear-named component in this assembly to count teeth on."; return res; }
            }
            else if (docType == (int)swDocumentTypes_e.swDocPART)
            {
                var part = model as PartDoc;
                Body2 best = null; double bestVol = -1;
                foreach (var o in (part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]) ?? new object[0])
                {
                    var b = o as Body2; if (b == null) continue;
                    double v = 0; try { var mp = b.GetMassProperties(0) as double[]; if (mp != null && mp.Length >= 4) v = mp[3]; } catch { }
                    if (v > bestVol) { bestVol = v; best = b; }
                }
                if (best == null) { res.Error = "This part has no solid body to count teeth on."; return res; }
                string title = null; try { title = model.GetTitle(); } catch { }
                targets.Add(Tuple.Create(title ?? "part", best));
            }
            else { res.Error = "Open a part or assembly to count gear teeth."; return res; }

            await emit("Scribe", "scanning tooth-tip faces", "run", null);
            var lines = new List<string>();
            var diagLines = new List<string>();
            foreach (var t in targets)
            {
                int count; string diag;
                bool ok = TryCountTeeth(t.Item2, out count, out diag);
                diagLines.Add(t.Item1 + ": " + diag);
                if (ok) { res.Counts[t.Item1] = count; lines.Add(t.Item1 + ": " + count + " teeth"); }
                else res.Unresolved.Add(t.Item1);
            }
            res.Diag = string.Join(" | ", diagLines);

            if (res.Counts.Count == 0)
            {
                res.Error = "Couldn't confidently count teeth on " + (targets.Count == 1 ? "this gear" : "these gears") +
                            " from geometry alone (no evenly-spaced repeating tip-face group found — likely a bevel/conical " +
                            "tooth tip, which this build can't yet distinguish from noise). Diag: " + res.Diag;
                return res;
            }

            res.Verified = true;
            var sb = new StringBuilder(string.Join("; ", lines));
            if (res.Unresolved.Count > 0)
                sb.Append("\nCouldn't confidently count: " + string.Join(", ", res.Unresolved) + " (no clean repeating tip-face group — possibly a bevel/conical tip).");
            res.Info = sb.ToString();
            await emit("Scribe", null, "done", string.Join("; ", lines));
            return res;
        }

        // Counts teeth from geometry alone: groups cylindrical faces by (radius, axis-line identity), picks the
        // largest-radius group with >=3 members (the tooth-tip band), and verifies the members are evenly spaced
        // in angle around that axis before trusting the count. Fails closed (returns false) rather than guess.
        private static bool TryCountTeeth(Body2 body, out int count, out string diag)
        {
            count = 0; diag = "";
            var faces = (body.GetFaces() as object[]) ?? new object[0];
            // Fail closed on cost, not correctness: a real imported gear body can carry thousands of small B-rep
            // faces (fine tessellated bevel/helical flanks), and every face costs a blocking COM round-trip
            // (GetSurface + IsCylinder + CylinderParams). This is the exact shape of the original test-loop "crashed"
            // findings (count-gear-teeth / change-dual-bevel-gear-teeth both timed out around 200s on the real
            // "Brain Gear Mechanism") — grinding through everything can take minutes. Cap it: past this many faces,
            // refuse fast and honestly rather than hang.
            const int MaxFacesToScan = 600;
            if (faces.Length > MaxFacesToScan)
            { diag = "body has " + faces.Length + " faces (> " + MaxFacesToScan + ") — too complex to scan face-by-face in this build, refusing rather than hang"; return false; }
            var groups = new Dictionary<string, List<Face2>>();
            var groupAxis = new Dictionary<string, double[]>(); // key -> {px,py,pz,dx,dy,dz}

            foreach (var fo in faces)
            {
                var face = fo as Face2; if (face == null) continue;
                Surface surf = null; try { surf = face.GetSurface() as Surface; } catch { }
                if (surf == null) continue;
                bool isCyl = false; try { isCyl = surf.IsCylinder(); } catch { }
                if (!isCyl) continue;
                double[] cp = null; try { cp = surf.CylinderParams as double[]; } catch { }
                if (cp == null || cp.Length < 7) continue;

                double r = cp[6];
                string axisKey = AxisLineKey(cp);
                string key = r.ToString("F2", CultureInfo.InvariantCulture) + "|" + axisKey;
                if (!groups.ContainsKey(key)) { groups[key] = new List<Face2>(); groupAxis[key] = new double[] { cp[0], cp[1], cp[2], cp[3], cp[4], cp[5] }; }
                groups[key].Add(face);
            }

            if (groups.Count == 0) { diag = "no cylindrical faces on this body"; return false; }

            string bestKey = null; double bestR = -1;
            foreach (var kv in groups)
            {
                if (kv.Value.Count < 3) continue;
                double r = double.Parse(kv.Key.Substring(0, kv.Key.IndexOf('|')), CultureInfo.InvariantCulture);
                if (r > bestR) { bestR = r; bestKey = kv.Key; }
            }
            if (bestKey == null)
            { diag = "no repeating cylindrical group with >=3 members (need an evenly-cut tooth band)"; return false; }

            var group = groups[bestKey];
            var axis = groupAxis[bestKey];
            string spacingDiag;
            if (!VerifyEvenSpacing(group, axis, out spacingDiag))
            { diag = "candidate band r=" + (bestR * 1000).ToString("F1", CultureInfo.InvariantCulture) + "mm n=" + group.Count + " — " + spacingDiag; return false; }

            count = group.Count;
            diag = "outer tooth band r=" + (bestR * 1000).ToString("F1", CultureInfo.InvariantCulture) + "mm, " + count + " evenly-spaced faces — " + spacingDiag;
            return true;
        }

        // Line identity independent of which point-on-axis SolidWorks happens to report: direction (sign-normalized)
        // plus the perpendicular distance from the ORIGIN to the infinite axis line (a property of the line itself).
        private static string AxisLineKey(double[] cp)
        {
            double px = cp[0], py = cp[1], pz = cp[2];
            double dx = cp[3], dy = cp[4], dz = cp[5];
            double dn = Math.Sqrt(dx * dx + dy * dy + dz * dz); if (dn < 1e-9) dn = 1;
            dx /= dn; dy /= dn; dz /= dn;
            if (dx < -1e-6 || (Math.Abs(dx) < 1e-6 && dy < -1e-6) || (Math.Abs(dx) < 1e-6 && Math.Abs(dy) < 1e-6 && dz < -1e-6))
            { dx = -dx; dy = -dy; dz = -dz; }
            double t = px * dx + py * dy + pz * dz;
            double cx = px - t * dx, cy = py - t * dy, cz = pz - t * dz;
            double perp = Math.Sqrt(cx * cx + cy * cy + cz * cz);
            return dx.ToString("F3", CultureInfo.InvariantCulture) + "," + dy.ToString("F3", CultureInfo.InvariantCulture) + "," +
                   dz.ToString("F3", CultureInfo.InvariantCulture) + "@" + perp.ToString("F4", CultureInfo.InvariantCulture);
        }

        private static bool VerifyEvenSpacing(List<Face2> group, double[] axisPD, out string diag)
        {
            diag = "";
            int n = group.Count;
            double px = axisPD[0], py = axisPD[1], pz = axisPD[2], dx = axisPD[3], dy = axisPD[4], dz = axisPD[5];
            double dn = Math.Sqrt(dx * dx + dy * dy + dz * dz); if (dn < 1e-9) { diag = "degenerate axis"; return false; }
            dx /= dn; dy /= dn; dz /= dn;

            // any vector not parallel to the axis, then Gram-Schmidt it into a perpendicular basis u,v.
            double ux = 1, uy = 0, uz = 0;
            if (Math.Abs(dx) > 0.9) { ux = 0; uy = 1; uz = 0; }
            double dot = ux * dx + uy * dy + uz * dz;
            ux -= dot * dx; uy -= dot * dy; uz -= dot * dz;
            double un = Math.Sqrt(ux * ux + uy * uy + uz * uz); if (un < 1e-9) { diag = "degenerate basis"; return false; }
            ux /= un; uy /= un; uz /= un;
            double vx = dy * uz - dz * uy, vy = dz * ux - dx * uz, vz = dx * uy - dy * ux;

            var angles = new List<double>();
            foreach (var f in group)
            {
                double[] box = null; try { box = f.GetBox() as double[]; } catch { }
                if (box == null || box.Length < 6) { diag = "face bounding box unavailable"; return false; }
                double cx = (box[0] + box[3]) / 2, cy = (box[1] + box[4]) / 2, cz = (box[2] + box[5]) / 2;
                double rx = cx - px, ry = cy - py, rz = cz - pz;
                double t = rx * dx + ry * dy + rz * dz;
                double px2 = rx - t * dx, py2 = ry - t * dy, pz2 = rz - t * dz;
                double uComp = px2 * ux + py2 * uy + pz2 * uz;
                double vComp = px2 * vx + py2 * vy + pz2 * vz;
                angles.Add(Math.Atan2(vComp, uComp));
            }
            angles.Sort();
            var gaps = new List<double>();
            for (int i = 0; i < n; i++)
            {
                double a1 = angles[i], a2 = (i + 1 < n) ? angles[i + 1] : angles[0] + 2 * Math.PI;
                gaps.Add(a2 - a1);
            }
            double avg = 2 * Math.PI / n;
            double maxDevFrac = 0;
            foreach (var g in gaps) { double dev = Math.Abs(g - avg) / avg; if (dev > maxDevFrac) maxDevFrac = dev; }
            diag = "avgGapDeg=" + (avg * 180 / Math.PI).ToString("F1", CultureInfo.InvariantCulture) + " maxDevFrac=" + maxDevFrac.ToString("F2", CultureInfo.InvariantCulture);
            if (maxDevFrac > 0.35) { diag += " -- NOT evenly spaced, refusing to guess"; return false; }
            return true;
        }
    }
}
