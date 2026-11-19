using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class SelectEdgeResult
    {
        public bool Success;
        public string TypeCriterion;   // "linear" | "circular" | null (any type)
        public string Extreme;         // "longest" | "shortest"
        public double LengthMm = -1;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// SelectEdge (tool 14) — WRITE-of-state: selects one edge by criteria ("select the longest edge",
    /// "select the shortest circular edge") so it's ready for a follow-up command. Never modifies geometry.
    /// Part-doc only, same scope as SelectFace.cs (component-scoped assembly selection is a future extension
    /// there too). Requires an EXTREME (longest/shortest) — an unqualified "select an edge" with no length or
    /// type criterion is genuinely ambiguous on a part with dozens of edges, so it's refused honestly (Rule
    /// #2) rather than guessed. Parent-face restriction ("...on the top face") is NOT yet implemented — out
    /// of scope for this pass, same honest-scope-limit shape as SelectFace's part-only note.
    /// </summary>
    public static class SelectEdge
    {
        private class PEdge { public Edge Edge; public double LenMm; public bool IsLine; public bool IsCircle; }

        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (!Regex.IsMatch(c, @"\bselect\b")) return false;
            if (!Regex.IsMatch(c, @"\bedge(s)?\b")) return false;
            return Regex.IsMatch(c, @"\b(longest|shortest|biggest|smallest|largest)\b");
        }

        public static async Task<SelectEdgeResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new SelectEdgeResult();
            if (model == null) { res.Error = "Open a part to select an edge on."; return res; }
            var part = model as PartDoc;
            if (part == null) { res.Error = "Select an edge on a part — open the .SLDPRT, not an assembly."; return res; }

            string extreme = ParseExtreme(intent);
            if (extreme == null)
            { res.Error = "Say which edge: longest or shortest (optionally linear/straight or circular/round)."; return res; }
            res.Extreme = extreme;
            res.TypeCriterion = ParseTypeCriterion(intent);

            await emit("Finder", "scanning edges", "run", null);
            object[] bodies = null;
            try { bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]; }
            catch (Exception ex) { res.Error = "Couldn't read the part's solid bodies: " + ex.Message; return res; }
            var edges = CollectEdges(bodies);
            if (res.TypeCriterion != null)
                edges.RemoveAll(e => res.TypeCriterion == "linear" ? !e.IsLine : !e.IsCircle);
            if (edges.Count == 0)
            {
                res.Error = "No" + (res.TypeCriterion != null ? " " + res.TypeCriterion : "") + " edges found on this part.";
                await emit("Finder", null, "fail", res.Error); return res;
            }

            PEdge pick = null;
            foreach (var e in edges)
            {
                if (pick == null) { pick = e; continue; }
                bool better = extreme == "longest" ? e.LenMm > pick.LenMm : e.LenMm < pick.LenMm;
                if (better) pick = e;
            }

            model.ClearSelection2(true);
            bool selected = false;
            try { selected = ((Entity)pick.Edge).Select4(false, null); } catch (Exception ex) { res.Error = "Select4 threw: " + ex.Message; return res; }
            if (!selected)
            { res.Error = "Found the " + extreme + " edge but SolidWorks refused the selection."; await emit("Finder", null, "fail", res.Error); return res; }

            res.Success = true;
            res.LengthMm = pick.LenMm;
            res.Info = "Selected the " + extreme + (res.TypeCriterion != null ? " " + res.TypeCriterion : "") +
                       " edge (" + Math.Round(pick.LenMm, 2) + " mm).";
            await emit("Finder", null, "done", extreme + " edge selected, " + Math.Round(pick.LenMm, 2) + " mm");
            return res;
        }

        private static string ParseExtreme(string intent)
        {
            string c = (intent ?? "").ToLowerInvariant();
            if (Regex.IsMatch(c, @"\b(longest|biggest|largest)\b")) return "longest";
            if (Regex.IsMatch(c, @"\b(shortest|smallest)\b")) return "shortest";
            return null;
        }

        private static string ParseTypeCriterion(string intent)
        {
            string c = (intent ?? "").ToLowerInvariant();
            if (Regex.IsMatch(c, @"\b(linear|straight|line)\b")) return "linear";
            if (Regex.IsMatch(c, @"\b(circular|round|curved|arc)\b")) return "circular";
            return null;
        }

        private static List<PEdge> CollectEdges(object[] bodies)
        {
            var edges = new List<PEdge>();
            foreach (var bo in bodies ?? new object[0])
            {
                var body = bo as Body2; if (body == null) continue;
                object[] eos = null; try { eos = body.GetEdges() as object[]; } catch { }
                foreach (var eo in eos ?? new object[0])
                {
                    var edge = eo as Edge; if (edge == null) continue;
                    double lenMm = EdgeLengthMm(edge);
                    if (lenMm <= 0) continue;
                    bool isLine = false, isCircle = false;
                    try { var cv = edge.GetCurve() as Curve; if (cv != null) { isLine = cv.IsLine(); isCircle = cv.IsCircle(); } } catch { }
                    edges.Add(new PEdge { Edge = edge, LenMm = lenMm, IsLine = isLine, IsCircle = isCircle });
                }
            }
            return edges;
        }

        // same shape as GetEdges.cs's proven EdgeLength: curve-length over the edge's own param range, with a
        // straight-edge endpoint-distance fallback when the curve read comes back empty.
        internal static double EdgeLengthMm(Edge edge)
        {
            try
            {
                var cp = edge.GetCurveParams2() as double[];
                var curve = edge.GetCurve() as Curve;
                if (curve != null && cp != null && cp.Length >= 8)
                {
                    double L = curve.GetLength3(cp[6], cp[7]);
                    if (L > 0) return L * 1000.0;
                }
                if (cp != null && cp.Length >= 6)
                {
                    double dx = cp[3] - cp[0], dy = cp[4] - cp[1], dz = cp[5] - cp[2];
                    return Math.Sqrt(dx * dx + dy * dy + dz * dz) * 1000.0;
                }
            }
            catch { }
            return -1;
        }
    }
}
