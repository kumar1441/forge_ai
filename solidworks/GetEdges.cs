using System;
using System.Globalization;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class GetEdgesResult
    {
        public int EdgeCount;
        public double LongestMm = -1;
        public double TotalMm;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 24 — get_edge_length (READ). Edge inventory of a part: how many edges, the LONGEST edge (mm), and total
    /// edge length. Each edge's length comes from its curve (Curve.GetLength3 over the edge's parameter range).
    /// Answers "how long is the longest edge", "total edge length". Read-only; own curve read.
    /// </summary>
    public static class GetEdges
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            return System.Text.RegularExpressions.Regex.IsMatch(c, @"\bedge(s)?\b") &&
                   System.Text.RegularExpressions.Regex.IsMatch(c, @"\b(length|longest|how long|total|measure|list|count|how many)\b");
        }

        public static async Task<GetEdgesResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new GetEdgesResult();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { res.Error = "Open a part to measure its edges."; return res; }

            await emit("Ruler", "measuring edges", "run", null);
            try
            {
                var bodies = (model as PartDoc).GetBodies2((int)swBodyType_e.swSolidBody, false) as object[];
                foreach (var bo in bodies ?? new object[0])
                {
                    var body = bo as Body2; if (body == null) continue;
                    foreach (var eo in (body.GetEdges() as object[]) ?? new object[0])
                    {
                        var edge = eo as Edge; if (edge == null) continue;
                        double len = EdgeLength(edge);
                        if (len <= 0) continue;
                        double mm = len * 1000.0;
                        res.EdgeCount++;
                        res.TotalMm += mm;
                        if (mm > res.LongestMm) res.LongestMm = mm;
                    }
                }
            }
            catch (Exception ex) { res.Error = "Edge read failed (" + ex.GetType().Name + ")."; return res; }

            await emit("Ruler", null, "done", res.EdgeCount + " edges · longest " + res.LongestMm.ToString("0.###", CultureInfo.InvariantCulture) + " mm");
            if (res.EdgeCount == 0) { res.Error = "No measurable edges (an imported dumb solid, or empty part)."; return res; }

            res.Info = res.EdgeCount + " edges. Longest " + res.LongestMm.ToString("0.###", CultureInfo.InvariantCulture) +
                       " mm, total edge length " + res.TotalMm.ToString("0.#", CultureInfo.InvariantCulture) + " mm.";
            return res;
        }

        private static double EdgeLength(Edge edge)
        {
            try
            {
                var cp = edge.GetCurveParams2() as double[];   // [sx,sy,sz, ex,ey,ez, sParam, eParam]
                var curve = edge.GetCurve() as Curve;
                if (curve != null && cp != null && cp.Length >= 8)
                {
                    double L = curve.GetLength3(cp[6], cp[7]);
                    if (L > 0) return L;
                }
                if (cp != null && cp.Length >= 6)   // straight-edge fallback: endpoint distance
                {
                    double dx = cp[3] - cp[0], dy = cp[4] - cp[1], dz = cp[5] - cp[2];
                    return Math.Sqrt(dx * dx + dy * dy + dz * dz);
                }
            }
            catch { }
            return -1;
        }
    }
}
