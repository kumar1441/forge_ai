using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class InsertDetailViewResult
    {
        public bool Verified;
        public bool AlreadyDone;
        public string DrawingPath;
        public string BaseViewName;
        public string DetailViewName;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// InsertDetailView — tool 105. Circles a region on an existing base view and blows it up into a separate,
    /// scaled detail view: sketches a circle centered on the base view (radius = 1/4 of its smaller outline
    /// dimension, via IView.GetOutline()), selects it, then IDrawingDoc.CreateDetailViewAt3(X, Y, Z, Style,
    /// Scale1, Scale2, LabelIn, Showtype, FullOutline) — confirmed live via redist DLL reflection. Same recipe
    /// shape as InsertSectionView (tool 104): the create call consumes whatever sketch entity is CURRENTLY
    /// SELECTED, not geometry passed as parameters (ActivateView, sketch+select, then call).
    ///
    /// IDEMPOTENT (Rule #5): if the sheet already carries a detail view, left alone. FAIL CLOSED (Rule #6): after
    /// creating, re-walks the sheet's OWN view list (GetFirstView/GetNextView) and checks the new view's
    /// Type == swDrawingDetailView rather than trusting the create call's return object alone. Never saves.
    /// </summary>
    public static class InsertDetailView
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (Regex.IsMatch(c, @"\b(package|pdf|export|rebuild|dangling|batch|dxf|dwg|flat[- ]?pattern)\b")) return false;
            if (!Regex.IsMatch(c, @"\bdetail\b")) return false;
            bool viewWord = Regex.IsMatch(c, @"\b(view|views)\b");
            bool verb = Regex.IsMatch(c, @"\b(insert|add|create|make|generate|circle|blow\s*up|zoom|put)\b");
            return viewWord && verb;
        }

        public static async Task<InsertDetailViewResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new InsertDetailViewResult();
            if (model == null) { res.Error = "Open a drawing with at least one view first."; return res; }

            bool isDrawing = false;
            try { isDrawing = (int)model.GetType() == (int)swDocumentTypes_e.swDocDRAWING; } catch { }
            if (!isDrawing) { res.Error = "This isn't a drawing — open the .SLDDRW with the view you want to detail."; return res; }

            var dd = model as IDrawingDoc;
            if (dd == null) { res.Error = "Couldn't access the drawing."; return res; }
            try { res.DrawingPath = model.GetPathName(); } catch { }

            await emit("Detailer", "finding a view to circle", "run", null);

            // ---- walk the sheet's views: remember the first cuttable (non-sheet, non-section, non-detail) view,
            // and bail early (IDEMPOTENT) if a detail view is already there ----
            IView baseView = null;
            var v = dd.GetFirstView() as IView;
            while (v != null)
            {
                int t = -1; try { t = v.Type; } catch { }
                if (t == (int)swDrawingViewTypes_e.swDrawingDetailView)
                {
                    res.AlreadyDone = true;
                    try { res.DetailViewName = v.Name; } catch { }
                }
                else if (baseView == null && t != (int)swDrawingViewTypes_e.swDrawingSheet && t != (int)swDrawingViewTypes_e.swDrawingSectionView)
                {
                    baseView = v;
                }
                v = v.GetNextView() as IView;
            }

            if (res.AlreadyDone)
            {
                res.Verified = true;
                res.Info = "This sheet already has a detail view (" + res.DetailViewName + ") — not stacking a duplicate.";
                await emit("Detailer", null, "done", res.Info);
                return res;
            }

            if (baseView == null) { res.Error = "No view on this sheet to circle a detail from — insert a standard view first."; return res; }
            try { res.BaseViewName = baseView.Name; } catch { }

            // ---- activate the base view and sketch a circle centered on it ----
            bool activated = false;
            try { activated = dd.ActivateView(res.BaseViewName); } catch { }
            if (!activated) { res.Error = "Couldn't activate view '" + res.BaseViewName + "' to circle it."; return res; }

            double[] outline = null;
            try { outline = baseView.GetOutline() as double[]; } catch { }
            if (outline == null || outline.Length < 4) { res.Error = "Couldn't read view '" + res.BaseViewName + "'s outline to place the detail circle."; return res; }
            double cx = (outline[0] + outline[2]) / 2.0;
            double cy = (outline[1] + outline[3]) / 2.0;
            double w = outline[2] - outline[0], h = outline[3] - outline[1];
            double radius = Math.Max(Math.Min(w, h) * 0.25, 0.003);

            SketchSegment circle = null;
            try
            {
                model.ClearSelection2(true);
                model.SketchManager.InsertSketch(true);
                circle = model.SketchManager.CreateCircleByRadius(cx, cy, 0, radius) as SketchSegment;
                model.SketchManager.InsertSketch(true); // exit sketch — the circle becomes the detail boundary input
            }
            catch (Exception ex) { res.Error = "Couldn't sketch the detail circle: " + ex.Message; return res; }
            if (circle == null) { res.Error = "The detail circle wasn't created."; return res; }

            try { circle.Select4(false, null); } catch { }

            await emit("Detailer", "blowing up the detail", "run", null);
            View created = null;
            try
            {
                double placeX = outline[2] + 0.06; // to the right of the base view
                double placeY = cy;
                created = dd.CreateDetailViewAt3(placeX, placeY, 0, (int)swDetViewStyle_e.swDetViewSTANDARD,
                    2.0, 1.0, "Detail", (int)swDetCircleShowType_e.swDetCircleCIRCLE, true) as View;
            }
            catch (Exception ex) { res.Error = "CreateDetailViewAt3 failed: " + ex.Message; }

            if (created == null)
            {
                try { model.ClearSelection2(true); } catch { }
                if (res.Error == null) res.Error = "SolidWorks didn't create a detail view — the circled region may not overlap the view.";
                await emit("Detailer", null, "fail", res.Error);
                return res;
            }

            try { model.ForceRebuild3(false); } catch { }
            try { res.DetailViewName = created.Name; } catch { }

            // ---- FAIL CLOSED: re-walk the sheet's OWN view list, don't trust the CreateDetailViewAt3 return alone ----
            bool found = false;
            var fv = dd.GetFirstView() as IView;
            while (fv != null)
            {
                int t = -1; try { t = fv.Type; } catch { }
                if (t == (int)swDrawingViewTypes_e.swDrawingDetailView) { found = true; break; }
                fv = fv.GetNextView() as IView;
            }

            res.Verified = found;
            if (!res.Verified)
            {
                res.Error = "A detail view object was returned, but the sheet's view list doesn't show it after rebuild.";
                await emit("Detailer", null, "fail", res.Error);
                return res;
            }

            res.Info = "Circled a detail view (" + res.DetailViewName + ") from '" + res.BaseViewName + "'. Forge didn't save.";
            await emit("Detailer", null, "done", res.Info);
            return res;
        }
    }
}
