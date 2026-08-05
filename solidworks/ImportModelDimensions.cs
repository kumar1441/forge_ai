using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class ImportModelDimensionsResult
    {
        public bool Verified;
        public bool AlreadyDone;
        public bool NeedsConfirm;
        public string Question;
        public string DrawingPath;
        public int DimensionCountBefore;
        public int DimensionCountAfter;
        public int RebuildErrors;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// ImportModelDimensions — tool 108 (WRITE). "Import the model dimensions into the drawing" / "pull the
    /// model's dimensions onto the view". Brings every dimension the MODEL already carries (marked-for-drawing or
    /// not) onto the current drawing sheet, via IDrawingDoc.InsertModelAnnotations3(swImportModelItemsFromEntireModel,
    /// swInsertDimensionsMarkedForDrawing|swInsertDimensionsNotMarkedForDrawing, true, false, false, false) — the
    /// same call already proven live in DrawingGenerator.cs's CreateFromTemplate fixture recipe (Create3rdAngleViews2
    /// then InsertModelAnnotations3), just wired here as a callable handler instead of a fixture-only helper.
    ///
    /// If no drawing is open yet, bootstraps one via InsertStandardViews.Run (tool 102, same reuse-not-duplicate
    /// pattern SetViewScale/AddNote already use) — dimensions need VIEWS to attach to, so a bare CreateDrawing sheet
    /// isn't enough. Matcher requires the explicit "model" word alongside a dimension noun and an import-flavored
    /// verb (import/pull/bring) — deliberately excludes "show"/"display"/"list" so it can never collide with the
    /// already-live GetDimensions (a part-scoped READ that owns those verbs).
    ///
    /// IDEMPOTENT (Rule #5): if the sheet already shows ANY dimensions, treated as already-imported — never
    /// re-runs InsertModelAnnotations3 against a populated sheet (its duplicate-insert behavior on this build is
    /// unconfirmed, so the safe default is to skip). FAIL CLOSED (Rule #6): re-counts every view's own
    /// GetDisplayDimensions() after the call and after a rebuild — verified only when the count actually rose.
    /// Never saves — same as every other WRITE handler in this family.
    /// </summary>
    public static class ImportModelDimensions
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (Regex.IsMatch(c, @"\b(dangling|broken|repair|reattach)\b")) return false;
            bool verb = Regex.IsMatch(c, @"\b(import|pull|bring)\b");
            if (!verb) return false;
            bool dimWord = Regex.IsMatch(c, @"\bdim\b|\bdims\b|\bdimension\b|\bdimensions\b");
            if (!dimWord) return false;
            return Regex.IsMatch(c, @"\bmodel\b");
        }

        public static async Task<ImportModelDimensionsResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new ImportModelDimensionsResult();
            if (model == null) { res.Error = "Open a part, assembly, or drawing first."; return res; }

            // Same stale-handle fix InsertView/DeleteView needed: the passed model can lag the true active
            // document (e.g. a prior chained command already bootstrapped a drawing and made it active).
            bool modelIsDrawing = false;
            try { modelIsDrawing = (int)model.GetType() == (int)swDocumentTypes_e.swDocDRAWING; } catch { }
            IModelDoc2 activeDrawing = null;
            if (!modelIsDrawing)
            {
                try
                {
                    var active = app.IActiveDoc2 as IModelDoc2;
                    if (active != null && (int)active.GetType() == (int)swDocumentTypes_e.swDocDRAWING) activeDrawing = active;
                }
                catch { }
            }

            IModelDoc2 drawingDoc;
            if (modelIsDrawing || activeDrawing != null)
            {
                drawingDoc = modelIsDrawing ? model : activeDrawing;
            }
            else
            {
                string sourcePath = null; try { sourcePath = model.GetPathName(); } catch { }
                if (string.IsNullOrEmpty(sourcePath))
                { res.Error = "This model has never been saved — it has no file path for the drawing to reference. Save it first."; return res; }

                await emit("Drafter", "no drawing open — creating one with standard views first", "run", null);
                var iv = await InsertStandardViews.Run(app, model, intent, emit);
                if (iv.NeedsConfirm) { res.NeedsConfirm = true; res.Question = iv.Question; return res; }
                if (iv.Error != null) { res.Error = "Couldn't set up a drawing to import dimensions onto: " + iv.Error; return res; }
                drawingDoc = app.IActiveDoc2 as IModelDoc2;
                if (drawingDoc == null || (int)drawingDoc.GetType() != (int)swDocumentTypes_e.swDocDRAWING)
                { res.Error = "A drawing was reported created, but it isn't the active document — can't import dimensions."; return res; }
            }

            var dd = drawingDoc as DrawingDoc;
            if (dd == null) { res.Error = "The target document isn't a drawing."; return res; }
            try { res.DrawingPath = drawingDoc.GetPathName(); } catch { }

            if (CountViews(dd) == 0)
            { res.Error = "This drawing sheet has no views yet — nothing to attach dimensions to."; return res; }

            res.DimensionCountBefore = CountDisplayDimensions(dd);

            // ---- IDEMPOTENT (Rule #5): a sheet already showing dimensions is left alone ----
            if (res.DimensionCountBefore > 0)
            {
                res.AlreadyDone = true;
                res.Verified = true;
                res.DimensionCountAfter = res.DimensionCountBefore;
                res.Info = "This drawing already shows " + res.DimensionCountBefore + " dimension(s) — not re-importing.";
                await emit("Drafter", null, "done", res.Info);
                return res;
            }

            await emit("Drafter", "importing the model's own dimensions", "run", null);
            int dimTypes = (int)swInsertAnnotation_e.swInsertDimensionsMarkedForDrawing |
                           (int)swInsertAnnotation_e.swInsertDimensionsNotMarkedForDrawing;
            try
            {
                dd.InsertModelAnnotations3((int)swImportModelItemsSource_e.swImportModelItemsFromEntireModel,
                    dimTypes, true, false, false, false);
            }
            catch (Exception ex) { res.Error = "Failed to import model dimensions: " + ex.Message; return res; }

            try { drawingDoc.ForceRebuild3(false); } catch { }

            // ---- FAIL CLOSED (Rule #6): re-count every view's own display dimensions, never trust the call alone ----
            await emit("Sentinel", "verifying", "run", null);
            res.DimensionCountAfter = CountDisplayDimensions(dd);
            try { res.RebuildErrors = drawingDoc.Extension.GetWhatsWrongCount(); } catch { }
            res.Verified = res.DimensionCountAfter > res.DimensionCountBefore;

            if (!res.Verified)
            {
                res.Error = "Import ran but no new dimensions appeared on the sheet (" + res.DimensionCountBefore +
                            " -> " + res.DimensionCountAfter + ") — this model may have no dimensioned features.";
                await emit("Sentinel", null, "fail", res.Error);
                return res;
            }

            res.Info = "Imported " + (res.DimensionCountAfter - res.DimensionCountBefore) + " model dimension(s) onto the drawing (" +
                       res.DimensionCountBefore + " -> " + res.DimensionCountAfter + " total). Forge didn't save.";
            await emit("Sentinel", null, "done", res.Info);
            return res;
        }

        private static int CountViews(DrawingDoc dd)
        {
            int count = 0;
            object[] perSheet = null;
            try { perSheet = dd.GetViews() as object[]; } catch { return 0; }
            if (perSheet == null) return 0;
            foreach (var so in perSheet)
            {
                var group = so as object[];
                if (group == null) continue;
                count += Math.Max(0, group.Length - 1);
            }
            return count;
        }

        private static int CountDisplayDimensions(DrawingDoc dd)
        {
            int count = 0;
            object[] perSheet = null;
            try { perSheet = dd.GetViews() as object[]; } catch { return 0; }
            if (perSheet == null) return 0;
            foreach (var so in perSheet)
            {
                var group = so as object[];
                if (group == null) continue;
                for (int k = 1; k < group.Length; k++)
                {
                    var v = group[k] as View;
                    if (v == null) continue;
                    object[] dims = null;
                    try { dims = v.GetDisplayDimensions() as object[]; } catch { }
                    if (dims != null) count += dims.Length;
                }
            }
            return count;
        }
    }
}
