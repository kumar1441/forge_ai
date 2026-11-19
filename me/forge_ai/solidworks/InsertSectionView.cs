using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class InsertSectionViewResult
    {
        public bool Verified;
        public bool AlreadyDone;
        public string DrawingPath;
        public string BaseViewName;
        public string SectionViewName;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// InsertSectionView — tool 104. Cuts a section view from an existing base view on the active drawing's sheet:
    /// sketches a vertical cut line through the base view's horizontal midpoint (spanning its outline, via
    /// IView.GetOutline()), selects it, then IDrawingDoc.CreateSectionViewAt5(X, Y, Z, Label, Options,
    /// ExcludedComponents, SectionDepth) — confirmed live via redist DLL reflection (the classic SW API recipe:
    /// ActivateView, sketch+select a line, call CreateSectionViewAt*; the line itself is never a real named
    /// sketch entity the user sees, it's consumed as the cut line by the create call).
    ///
    /// IDEMPOTENT (Rule #5): if the sheet already carries a section view, left alone — reports already-done
    /// instead of stacking a second cut. FAIL CLOSED (Rule #6): after creating, re-walks the sheet's OWN view
    /// list (GetFirstView/GetNextView) rather than trusting the CreateSectionViewAt5 return object alone, and
    /// checks the new view's Type == swDrawingSectionView. Never saves — same as every other WRITE handler.
    /// </summary>
    public static class InsertSectionView
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            // drawing_package/flat_dxf own rebuild/export/pdf/dangling/dimension/package/batch/dxf/dwg phrasing.
            if (Regex.IsMatch(c, @"\b(package|pdf|export|rebuild|dangling|batch|dxf|dwg|flat[- ]?pattern)\b")) return false;
            if (!Regex.IsMatch(c, @"\bsection\b")) return false;
            if (Regex.IsMatch(c, @"\bdetail\b")) return false; // detail view is tool 105, a separate not-yet-built tool
            bool viewWord = Regex.IsMatch(c, @"\b(view|views)\b");
            bool verb = Regex.IsMatch(c, @"\b(insert|add|create|make|generate|cut|put|slice)\b");
            return viewWord && verb;
        }

        public static async Task<InsertSectionViewResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new InsertSectionViewResult();
            if (model == null) { res.Error = "Open a drawing with at least one view first."; return res; }

            bool isDrawing = false;
            try { isDrawing = (int)model.GetType() == (int)swDocumentTypes_e.swDocDRAWING; } catch { }
            if (!isDrawing) { res.Error = "This isn't a drawing — open the .SLDDRW with the view you want to section."; return res; }

            var dd = model as IDrawingDoc;
            if (dd == null) { res.Error = "Couldn't access the drawing."; return res; }
            try { res.DrawingPath = model.GetPathName(); } catch { }

            await emit("Sectioner", "finding a view to cut", "run", null);

            // ---- walk the sheet's views: remember the first cuttable (non-sheet, non-section, non-detail) view,
            // and bail early (IDEMPOTENT) if a section view is already there ----
            IView baseView = null;
            var v = dd.GetFirstView() as IView;
            while (v != null)
            {
                int t = -1; try { t = v.Type; } catch { }
                if (t == (int)swDrawingViewTypes_e.swDrawingSectionView)
                {
                    res.AlreadyDone = true;
                    try { res.SectionViewName = v.Name; } catch { }
                }
                else if (baseView == null && t != (int)swDrawingViewTypes_e.swDrawingSheet && t != (int)swDrawingViewTypes_e.swDrawingDetailView)
                {
                    baseView = v;
                }
                v = v.GetNextView() as IView;
            }

            if (res.AlreadyDone)
            {
                res.Verified = true;
                res.Info = "This sheet already has a section view (" + res.SectionViewName + ") — not stacking a duplicate.";
                await emit("Sectioner", null, "done", res.Info);
                return res;
            }

            if (baseView == null) { res.Error = "No view on this sheet to cut a section from — insert a standard view first."; return res; }
            try { res.BaseViewName = baseView.Name; } catch { }

            // ---- activate the base view and sketch a vertical cut line through its horizontal midpoint ----
            bool activated = false;
            try { activated = dd.ActivateView(res.BaseViewName); } catch { }
            if (!activated) { res.Error = "Couldn't activate view '" + res.BaseViewName + "' to cut it."; return res; }

            double[] outline = null;
            try { outline = baseView.GetOutline() as double[]; } catch { }
            if (outline == null || outline.Length < 4) { res.Error = "Couldn't read view '" + res.BaseViewName + "'s outline to place the cut line."; return res; }
            double xMid = (outline[0] + outline[2]) / 2.0;
            double yLo = outline[1], yHi = outline[3];
            double pad = Math.Max((yHi - yLo) * 0.1, 0.005);

            SketchSegment line = null;
            try
            {
                model.ClearSelection2(true);
                model.SketchManager.InsertSketch(true);
                line = model.SketchManager.CreateLine(xMid, yLo - pad, 0, xMid, yHi + pad, 0) as SketchSegment;
                model.SketchManager.InsertSketch(true); // exit sketch — the line becomes the cut-line annotation input
            }
            catch (Exception ex) { res.Error = "Couldn't sketch the cut line: " + ex.Message; return res; }
            if (line == null) { res.Error = "The cut line wasn't created."; return res; }

            try { line.Select4(false, null); } catch { }

            await emit("Sectioner", "cutting the section", "run", null);
            View created = null;
            try
            {
                double placeX = outline[2] + 0.06; // to the right of the base view
                double placeY = (yLo + yHi) / 2.0;
                created = dd.CreateSectionViewAt5(placeX, placeY, 0, "Section", 0, null, 0) as View;
            }
            catch (Exception ex) { res.Error = "CreateSectionViewAt5 failed: " + ex.Message; }

            if (created == null)
            {
                try { model.ClearSelection2(true); } catch { }
                if (res.Error == null) res.Error = "SolidWorks didn't create a section view — the cut line may not intersect the model.";
                await emit("Sectioner", null, "fail", res.Error);
                return res;
            }

            try { model.ForceRebuild3(false); } catch { }
            try { res.SectionViewName = created.Name; } catch { }

            // ---- FAIL CLOSED: re-walk the sheet's OWN view list, don't trust the CreateSectionViewAt5 return alone ----
            bool found = false;
            var fv = dd.GetFirstView() as IView;
            while (fv != null)
            {
                int t = -1; try { t = fv.Type; } catch { }
                if (t == (int)swDrawingViewTypes_e.swDrawingSectionView) { found = true; break; }
                fv = fv.GetNextView() as IView;
            }

            res.Verified = found;
            if (!res.Verified)
            {
                res.Error = "A section view object was returned, but the sheet's view list doesn't show it after rebuild.";
                await emit("Sectioner", null, "fail", res.Error);
                return res;
            }

            res.Info = "Cut a section view (" + res.SectionViewName + ") through '" + res.BaseViewName + "'. Forge didn't save.";
            await emit("Sectioner", null, "done", res.Info);
            return res;
        }
    }
}
