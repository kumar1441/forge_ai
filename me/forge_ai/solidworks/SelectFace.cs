using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class SelectFaceResult
    {
        public bool Success;
        public string Criterion;      // "top" | "bottom" | "left" | "right" | "largest"
        public double AreaMm2 = -1;
        public double[] Normal;       // outward normal, part/body space
        public string Info;
        public string Error;
    }

    /// <summary>
    /// SelectFace (tool 13) — WRITE-of-state: selects one planar face by criteria ("select the top face",
    /// "select the largest flat face") so it's ready for a follow-up command, and reports back what was picked.
    /// Never modifies geometry. Part-doc only (component-scoped assembly selection is a future extension).
    ///
    /// Direction resolution reuses AddHole.cs's proven fix (test-loop no-change finding add-drain-hole): "top"/
    /// "bottom" mean the part's OWN vertical axis (whichever of Y/Z has the larger overall bounding-box span,
    /// from every solid body's IBody2.GetBodyBox), never an assumed fixed axis — a side wall whose normal
    /// happens to read +Y can't masquerade as a Z-axis cap. "left"/"right" use the X axis (the one axis a
    /// vertical-axis block never claims). "front"/"back" have no reliable axis without a defined view direction,
    /// so they're refused honestly (Rule #6: fail closed) rather than guessed.
    /// </summary>
    public static class SelectFace
    {
        private class PFace { public Face2 Face; public double AreaMm2; public double[] Normal; }

        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (!Regex.IsMatch(c, @"\bselect\b")) return false;
            if (!Regex.IsMatch(c, @"\bface\b")) return false;
            return true;
        }

        public static async Task<SelectFaceResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new SelectFaceResult();
            if (model == null) { res.Error = "Open a part to select a face on."; return res; }
            var part = model as PartDoc;
            if (part == null) { res.Error = "Select a face on a part — open the .SLDPRT, not an assembly."; return res; }

            string crit = ParseCriterion(intent);
            if (crit == null)
            { res.Error = "Say which face: top, bottom, left, right, or the largest flat face."; return res; }
            res.Criterion = crit;

            await emit("Finder", "scanning planar faces", "run", null);
            object[] bodies = null;
            try { bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]; }
            catch (Exception ex) { res.Error = "Couldn't read the part's solid bodies: " + ex.Message; return res; }
            var planars = CollectPlanarFaces(bodies);
            if (planars.Count == 0)
            { res.Error = "No planar faces found on this part."; await emit("Finder", null, "fail", res.Error); return res; }

            PFace pick = null;
            if (crit == "largest")
            {
                foreach (var p in planars) if (pick == null || p.AreaMm2 > pick.AreaMm2) pick = p;
            }
            else
            {
                int axis = crit == "left" || crit == "right" ? 0 : VerticalAxisIndex(bodies);
                bool wantPos = crit == "top" || crit == "right";
                double bestScore = wantPos ? -2 : 2;
                foreach (var p in planars)
                {
                    if (p.Normal == null || p.Normal.Length < 3) continue;
                    double score = p.Normal[axis];
                    if (wantPos ? (score > bestScore) : (score < bestScore)) { bestScore = score; pick = p; }
                }
                bool clear = wantPos ? bestScore > 0.5 : bestScore < -0.5;
                if (!clear) pick = null;
            }

            if (pick == null)
            { res.Error = "Couldn't find a clear '" + crit + "' face on this part."; await emit("Finder", null, "fail", res.Error); return res; }

            model.ClearSelection2(true);
            bool selected = false;
            try { selected = ((Entity)pick.Face).Select4(false, null); } catch (Exception ex) { res.Error = "Select4 threw: " + ex.Message; return res; }
            if (!selected)
            { res.Error = "Found the " + crit + " face but SolidWorks refused the selection."; await emit("Finder", null, "fail", res.Error); return res; }

            res.Success = true;
            res.AreaMm2 = pick.AreaMm2;
            res.Normal = pick.Normal;
            res.Info = "Selected the " + crit + " face (" + Math.Round(pick.AreaMm2, 1) + " mm², normal " + FmtN(pick.Normal) + ").";
            await emit("Finder", null, "done", crit + " face selected, " + Math.Round(pick.AreaMm2, 1) + " mm²");
            return res;
        }

        private static string ParseCriterion(string intent)
        {
            string c = (intent ?? "").ToLowerInvariant();
            if (Regex.IsMatch(c, @"\b(largest|biggest|largest\s*flat|main)\b")) return "largest";
            if (Regex.IsMatch(c, @"\btop\b")) return "top";
            if (Regex.IsMatch(c, @"\bbottom\b")) return "bottom";
            if (Regex.IsMatch(c, @"\bleft\b")) return "left";
            if (Regex.IsMatch(c, @"\bright\b")) return "right";
            return null;
        }

        // whichever of Y (index 1) or Z (index 2) has the LARGER span across every solid body's bounding box —
        // same fix as AddHole.cs's VerticalAxisIndex (test-loop no-change finding add-drain-hole).
        internal static int VerticalAxisIndex(object[] bodies)
        {
            double[] box = null;
            foreach (var bo in bodies ?? new object[0])
            {
                var body = bo as Body2; if (body == null) continue;
                double[] b = null; try { b = body.GetBodyBox() as double[]; } catch { }
                if (b == null || b.Length < 6) continue;
                box = box == null ? b : new[]
                {
                    Math.Min(box[0], b[0]), Math.Min(box[1], b[1]), Math.Min(box[2], b[2]),
                    Math.Max(box[3], b[3]), Math.Max(box[4], b[4]), Math.Max(box[5], b[5])
                };
            }
            if (box == null || box.Length < 6) return 2;
            double ySpan = box[4] - box[1], zSpan = box[5] - box[2];
            return zSpan >= ySpan ? 2 : 1;
        }

        private static List<PFace> CollectPlanarFaces(object[] bodies)
        {
            var planars = new List<PFace>();
            foreach (var bo in bodies ?? new object[0])
            {
                var body = bo as Body2; if (body == null) continue;
                object[] faces = null; try { faces = body.GetFaces() as object[]; } catch { }
                foreach (var fo in faces ?? new object[0])
                {
                    var face = fo as Face2; if (face == null) continue;
                    Surface s = null; try { s = face.GetSurface() as Surface; } catch { }
                    bool plane = false; try { plane = s != null && s.IsPlane(); } catch { }
                    if (!plane) continue;
                    double area = 0; try { area = face.GetArea(); } catch { }
                    if (area <= 0) continue;
                    double[] n = null; try { n = face.Normal as double[]; } catch { }
                    planars.Add(new PFace { Face = face, AreaMm2 = area * 1e6, Normal = n });
                }
            }
            return planars;
        }

        private static string FmtN(double[] n) => n == null || n.Length < 3 ? "?" :
            "(" + n[0].ToString("0.00") + "," + n[1].ToString("0.00") + "," + n[2].ToString("0.00") + ")";
    }
}
