using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public class HighlightEntitiesResult
    {
        public bool Success;
        public string Criterion;     // "top" | "bottom" | "left" | "right" | "largest" | "hole"
        public string ShapeType;     // "planar" | "cylindrical"
        public double AreaMm2 = -1;
        public double DiameterMm = -1; // hole only
        public string Info;
        public string Error;
    }

    /// <summary>
    /// HighlightEntities (tool 238, WRITE-of-state) — flashes/selects the target entity on screen and zooms to
    /// it, so the user SEES exactly what a follow-up command is about to touch before confirming. Pure selection
    /// state (IModelDoc2.ClearSelection2 + Entity.Select4 + ViewZoomToSelection) — never a permanent color write
    /// (that's apply_appearance, tool 233); one ClearSelection2 undoes it, nothing to Ctrl+Z.
    ///
    /// v1 scope: part-doc face targeting only, reusing the two already-proven face-finding primitives so
    /// "highlight the top face" and "select the top face" / "describe the top face" always agree on which face
    /// that is — planar criteria (top/bottom/left/right/largest) delegate to SelectFace.Run verbatim; "hole"/
    /// "bore" reuses DescribeGeometry.FindLargestConcaveCylindricalFace (largest size-bounded concave cylindrical
    /// face). Always zooms to the selection (SelectFace does not) since the whole point here is visibility.
    /// Distinct from change_impact's automatic post-answer flash (ForgePanel.HighlightFeatures, named-feature
    /// only, fires as a side effect of a prior answer): this is a user-invoked, criterion-driven preview tool.
    /// </summary>
    public static class HighlightEntities
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            bool verb = Regex.IsMatch(c, @"\b(highlight|flash|light\s*up)\b");
            if (!verb) return false;
            return Regex.IsMatch(c, @"\b(face|hole|bore)\b");
        }

        public static async Task<HighlightEntitiesResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new HighlightEntitiesResult();
            if (model == null) { res.Error = "Open a part to highlight geometry on."; return res; }
            var part = model as PartDoc;
            if (part == null) { res.Error = "Highlighting geometry works on a part — open the .SLDPRT, not an assembly."; return res; }

            string crit = ParseCriterion(intent);
            if (crit == null)
            { res.Error = "Say what to highlight: the top/bottom/left/right/largest face, or the hole/bore."; return res; }

            await emit("Spotlight", "locating the " + crit, "run", null);

            Face2 face;
            if (crit == "hole")
            {
                face = DescribeGeometry.FindLargestConcaveCylindricalFace(part);
                if (face == null)
                { res.Error = "Couldn't find a concave cylindrical hole face on this part."; await emit("Spotlight", null, "fail", res.Error); return res; }
                model.ClearSelection2(true);
                bool sel = false; try { sel = ((Entity)face).Select4(false, null); } catch (Exception ex) { res.Error = "Select4 threw: " + ex.Message; return res; }
                if (!sel)
                { res.Error = "Found the hole but SolidWorks refused the selection."; await emit("Spotlight", null, "fail", res.Error); return res; }
            }
            else
            {
                var sf = await SelectFace.Run(app, model, "select the " + crit + " face", (a, b, c2, d) => Task.CompletedTask);
                if (!sf.Success)
                { res.Error = "Couldn't find the " + crit + " face: " + sf.Error; await emit("Spotlight", null, "fail", res.Error); return res; }
                var sm = model.SelectionManager as SelectionMgr;
                object sel0 = null; try { sel0 = sm.GetSelectedObject6(1, -1); } catch { }
                face = sel0 as Face2;
                if (face == null)
                { res.Error = "Found the " + crit + " face but couldn't recover it from the selection."; return res; }
            }

            try { model.ViewZoomToSelection(); } catch { }

            double area = 0; try { area = face.GetArea(); } catch { }
            res.AreaMm2 = area * 1e6;
            res.Criterion = crit;

            if (crit == "hole")
            {
                res.ShapeType = "cylindrical";
                Surface surf = null; try { surf = face.GetSurface() as Surface; } catch { }
                double[] cp = null; try { cp = surf != null ? surf.CylinderParams as double[] : null; } catch { }
                if (cp != null && cp.Length >= 7) res.DiameterMm = cp[6] * 2.0 * 1000.0;
                res.Info = "Highlighted the hole" + (res.DiameterMm > 0 ? " (Ø" + Math.Round(res.DiameterMm, 2) + "mm)" : "") +
                    " and zoomed in — confirm before I touch it.";
            }
            else
            {
                res.ShapeType = "planar";
                res.Info = "Highlighted the " + crit + " face (" + Math.Round(res.AreaMm2, 1) + " mm²) and zoomed in — confirm before I touch it.";
            }

            await emit("Spotlight", null, "done", res.Info);
            res.Success = true;
            return res;
        }

        private static string ParseCriterion(string intent)
        {
            string c = (intent ?? "").ToLowerInvariant();
            if (Regex.IsMatch(c, @"\b(hole|bore)\b")) return "hole";
            if (!Regex.IsMatch(c, @"\bface\b")) return null;
            if (Regex.IsMatch(c, @"\b(largest|biggest)\b")) return "largest";
            if (Regex.IsMatch(c, @"\btop\b")) return "top";
            if (Regex.IsMatch(c, @"\bbottom\b")) return "bottom";
            if (Regex.IsMatch(c, @"\bleft\b")) return "left";
            if (Regex.IsMatch(c, @"\bright\b")) return "right";
            return null;
        }
    }
}
