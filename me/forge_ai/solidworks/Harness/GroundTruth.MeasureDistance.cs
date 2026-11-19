using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT centre-to-centre distance between the two named components — shares NO code with MeasureDistance.
        // Its own traversal + its own GetBox read + its own distance math. The harness passes the SAME two name
        // fragments (via the handler intent) so both resolve the same pair; a handler mis-read shows as a distance
        // mismatch. Returns -1 if either fragment is missing/ambiguous (the handler would have asked, so no assertion).
        public static JObject MeasureMeasureDistance(ISldWorks app, IModelDoc2 model, string fragA, string fragB)
        {
            var res = new JObject();
            var asm = model as AssemblyDoc;
            if (asm == null || string.IsNullOrEmpty(fragA) || string.IsNullOrEmpty(fragB)) { res["distanceMm"] = -1; return res; }
            var mu = (MathUtility)app.GetMathUtility();

            double[] ca = null, cb = null; int na = 0, nb = 0;
            foreach (var o in (asm.GetComponents(true) as object[]) ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                string nm = null; try { nm = c.Name2; } catch { }
                if (nm == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                if (nm.IndexOf(fragA, StringComparison.OrdinalIgnoreCase) >= 0) { na++; ca = OriginPos(mu, c); }
                if (nm.IndexOf(fragB, StringComparison.OrdinalIgnoreCase) >= 0) { nb++; cb = OriginPos(mu, c); }
            }
            if (na != 1 || nb != 1 || ca == null || cb == null) { res["distanceMm"] = -1; res["na"] = na; res["nb"] = nb; return res; }

            double dx = (cb[0] - ca[0]) * 1000.0, dy = (cb[1] - ca[1]) * 1000.0, dz = (cb[2] - ca[2]) * 1000.0;
            res["distanceMm"] = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            return res;
        }

        // parse the two component fragments from the intent (its own parse; the distance math below is what's being
        // independently verified, resolution is just to reach the same pair the handler measured).
        public static void ParseDistFrags(string intent, out string a, out string b)
        {
            a = null; b = null;
            if (string.IsNullOrEmpty(intent)) return;
            var m = System.Text.RegularExpressions.Regex.Match(intent, @"between\s+(.+?)\s+and\s+(.+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success) return;
            a = Clean(m.Groups[1].Value); b = Clean(m.Groups[2].Value);
        }

        private static string Clean(string s)
        {
            s = (s ?? "").Trim().Trim('"', '\'', '.', '?', ' ');
            s = System.Text.RegularExpressions.Regex.Replace(s, @"^(the|a|an|component|part)\s+", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return s.Trim();
        }

        // INDEPENDENT position: transform the part ORIGIN point through the component transform (MultiplyTransform) —
        // a different extraction than the handler's raw array-slot read, so a mis-read shows as a distance mismatch.
        private static double[] OriginPos(MathUtility mu, Component2 c)
        {
            try
            {
                var xf = c.Transform2 as MathTransform; if (xf == null) return null;
                var w = (mu.CreatePoint(new double[] { 0, 0, 0 }) as MathPoint).MultiplyTransform(xf) as MathPoint;
                var a = w.ArrayData as double[];
                return (a != null && a.Length >= 3) ? new[] { a[0], a[1], a[2] } : null;
            }
            catch { return null; }
        }
    }
}
