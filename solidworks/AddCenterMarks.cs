using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class AddCenterMarksResult
    {
        public bool Verified;
        public int ViewsProcessed = -1;
        public int CenterMarksBefore = -1;
        public int CenterMarksAfter = -1;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// AddCenterMarks (tool #162) — restore/add center marks to every hole in a drawing view.
    /// "add center marks to this view", "restore center marks to all the holes".
    ///
    /// PARKED — see docs/kb/landmines.md. `IView.AutoInsertCenterMarks` is a SILENT FALSE-SUCCESS headless on this
    /// R2026x build: it returns `true` for every view across a 3-variant sweep (default style/size, InsertOption=0,
    /// explicit Size+ExtendedLines) yet `IView.GetCenterMarkCount()` never grows (0->0), confirmed by an independent
    /// GT walk. Kept fail-closed (Verified only if the count actually grew) and dormant — do NOT re-attempt blind.
    /// </summary>
    public static class AddCenterMarks
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            bool verb = Regex.IsMatch(c, @"\b(add|insert|restore|put|show)\b");
            bool noun = Regex.IsMatch(c, @"\bcenter\s*marks?\b");
            return verb && noun;
        }

        public static async Task<AddCenterMarksResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new AddCenterMarksResult();
            if (model == null) { res.Error = "Open the drawing you want center marks added to."; return res; }
            int docType = 0; try { docType = model.GetType(); } catch { }
            if (docType != (int)swDocumentTypes_e.swDocDRAWING)
            { res.Error = "add_center_marks needs an open DRAWING (not a part/assembly)."; return res; }

            var dd = model as DrawingDoc;
            if (dd == null) { res.Error = "Couldn't get the DrawingDoc interface."; return res; }

            await emit("Marker", "counting existing center marks", "run", null);
            int before = 0, processed = 0;
            try
            {
                var v = dd.GetFirstView() as IView;
                v = v?.GetNextView() as IView; // skip the sheet's own "view" (first) — start at the first real drawing view
                while (v != null)
                {
                    try { before += v.GetCenterMarkCount(); } catch { }
                    v = v.GetNextView() as IView;
                }
            }
            catch { }
            res.CenterMarksBefore = before;
            await emit("Marker", null, "done", before + " existing center mark(s)");

            await emit("Marker", "auto-inserting center marks on holes in every view", "run", null);
            try
            {
                var v = dd.GetFirstView() as IView;
                v = v?.GetNextView() as IView;
                while (v != null)
                {
                    processed++;
                    string vName = null; try { vName = v.Name; } catch { }
                    try
                    {
                        // AutoInsertCenterMarks operates on the ACTIVE view, same ActivateView-first idiom
                        // InsertSectionView/InsertDetailView/AddDrawingDimension already proved live. Returns
                        // true on this build regardless — see the class doc, the return value is NOT trusted.
                        if (!string.IsNullOrEmpty(vName)) dd.ActivateView(vName);
                        v.AutoInsertCenterMarks(
                            (int)swAutoInsertCenterMarkTypes_e.swAutoInsertCenterMarkType_Hole,
                            0, false, false, false, 0.003, true, false, 0);
                    }
                    catch { }
                    v = v.GetNextView() as IView;
                }
                res.ViewsProcessed = processed;
            }
            catch (Exception ex) { res.Error = ex.GetType().Name + ": " + ex.Message; await emit("Marker", null, "fail", res.Error); return res; }

            try { model.ForceRebuild3(false); } catch { }
            int after = 0;
            try
            {
                var v = dd.GetFirstView() as IView;
                v = v?.GetNextView() as IView;
                while (v != null)
                {
                    try { after += v.GetCenterMarkCount(); } catch { }
                    v = v.GetNextView() as IView;
                }
            }
            catch { }
            res.CenterMarksAfter = after;
            res.Verified = after > before;
            res.Info = res.ViewsProcessed + " view(s) processed, center marks " + before + " -> " + after;
            await emit("Marker", null, res.Verified ? "done" : "fail", res.Info);
            if (!res.Verified && string.IsNullOrEmpty(res.Error))
                res.Error = "AutoInsertCenterMarks ran but the center mark count didn't grow (" + before + " -> " + after + ").";
            return res;
        }
    }
}
