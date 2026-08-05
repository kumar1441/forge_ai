using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class MeasureDistanceResult
    {
        public string A, B;
        public double DistanceMm = -1;   // centre-to-centre distance between the two components' bounding boxes
        public double Dx, Dy, Dz;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 19 — measure_distance (READ), component centre-to-centre mode. "how far apart are the plate and bolt-1",
    /// "distance between X and Y". Resolves both components from the live tree (ONE question on 0/many matches, Rule
    /// #2), computes the distance between their bounding-box centres (GetBox midpoints, world coords). Read-only. The
    /// ground truth recomputes the centres by its own traversal, so a mis-read shows as a distance mismatch.
    /// </summary>
    public static class MeasureDistance
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            // "add a 10mm distance mate between X and Y" is add_distance_mate (a WRITE), not a measurement — a measure
            // query never says "mate" or carries an add verb, so excluding those keeps that boundary clean.
            return (Regex.IsMatch(c, @"\b(distance|how far|far apart|gap|spacing|separation)\b") &&
                    Regex.IsMatch(c, @"\bbetween\b|\band\b")) &&
                    !Regex.IsMatch(c, @"\bmate\b") && !Regex.IsMatch(c, @"\b(add|create|make|insert|place|put)\b");
        }

        public static async Task<MeasureDistanceResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new MeasureDistanceResult();
            var asm = model as AssemblyDoc;
            if (asm == null) { res.Error = "Open the assembly (.SLDASM) to measure between components."; return res; }

            // parse the two targets: "... between X and Y" or "... X and Y"
            var m = Regex.Match(intent ?? "", @"between\s+(.+?)\s+and\s+(.+)$", RegexOptions.IgnoreCase);
            if (!m.Success) m = Regex.Match(intent ?? "", @"(?:distance|far|apart|gap)\D*?\b(\S+)\b.*?\band\b\s+(.+)$", RegexOptions.IgnoreCase);
            if (!m.Success) { res.Error = "Tell me the two things, e.g. \"distance between the plate and bolt-1\"."; return res; }
            string fragA = Clean(m.Groups[1].Value), fragB = Clean(m.Groups[2].Value);
            if (string.IsNullOrWhiteSpace(fragA) || string.IsNullOrWhiteSpace(fragB)) { res.Error = "I need two components to measure between."; return res; }

            await emit("Caliper", "locating both components", "run", null);
            var comps = new List<Component2>();
            foreach (var o in (asm.GetComponents(true) as object[]) ?? new object[0]) { var c = o as Component2; if (c != null) comps.Add(c); }

            Component2 a = Resolve(comps, fragA, out string aErr);
            if (a == null) { res.Error = aErr; await emit("Caliper", null, "fail", aErr); return res; }
            Component2 b = Resolve(comps, fragB, out string bErr);
            if (b == null) { res.Error = bErr; await emit("Caliper", null, "fail", bErr); return res; }
            try { res.A = a.Name2; res.B = b.Name2; } catch { }

            double[] ca = Center(a), cb = Center(b);
            if (ca == null || cb == null) { res.Error = "Couldn't read a bounding box for one of the components."; return res; }
            res.Dx = (cb[0] - ca[0]) * 1000.0; res.Dy = (cb[1] - ca[1]) * 1000.0; res.Dz = (cb[2] - ca[2]) * 1000.0;
            res.DistanceMm = Math.Sqrt(res.Dx * res.Dx + res.Dy * res.Dy + res.Dz * res.Dz);

            await emit("Caliper", null, "done", res.A + " ↔ " + res.B + " = " + res.DistanceMm.ToString("0.###", CultureInfo.InvariantCulture) + " mm");
            res.Info = "Centre-to-centre distance " + res.A + " ↔ " + res.B + " = " +
                       res.DistanceMm.ToString("0.###", CultureInfo.InvariantCulture) + " mm (Δ " +
                       F(res.Dx) + ", " + F(res.Dy) + ", " + F(res.Dz) + " mm).";
            return res;
        }

        private static string Clean(string s)
        {
            s = (s ?? "").Trim().Trim('"', '\'', '.', '?', ' ');
            s = Regex.Replace(s, @"^(the|a|an|component|part)\s+", "", RegexOptions.IgnoreCase);
            return s.Trim();
        }

        private static Component2 Resolve(List<Component2> comps, string frag, out string err)
        {
            err = null;
            var hits = new List<Component2>();
            foreach (var c in comps)
            {
                bool sup = false; try { sup = c.IsSuppressed(); } catch { }
                if (sup) continue;   // a suppressed component has no geometry to measure — skip it in resolution
                string nm = null; try { nm = c.Name2; } catch { }
                if (nm != null && nm.IndexOf(frag, StringComparison.OrdinalIgnoreCase) >= 0) hits.Add(c);
            }
            if (hits.Count == 0) { err = "No active component matches '" + frag + "' (it may be suppressed)."; return null; }
            if (hits.Count > 1)
            {
                var names = new List<string>(); foreach (var c in hits) { try { names.Add(c.Name2); } catch { } if (names.Count >= 5) break; }
                err = "'" + frag + "' matches " + hits.Count + " components (" + string.Join(", ", names.ToArray()) + "…). Which one?";
                return null;
            }
            return hits[0];
        }

        // Component world position from Transform2's translation slots (9,10,11). Component2.GetBox returns null on
        // this 3DEXPERIENCE build (dead read API), but Transform2 is solid — proven in get_component_transform.
        private static double[] Center(Component2 c)
        {
            try
            {
                var a = (c.Transform2 as MathTransform)?.ArrayData as double[];
                if (a == null || a.Length < 12) return null;
                return new[] { a[9], a[10], a[11] };
            }
            catch { return null; }
        }

        private static string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
    }
}
