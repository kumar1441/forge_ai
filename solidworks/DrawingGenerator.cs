using System;
using System.IO;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public static class DrawingGenerator
    {
        /// <summary>
        /// Creates a drawing for a variant part.
        /// Strategy 1: if a reference drawing exists alongside the base part, copy it and relink all views.
        /// Strategy 2: no reference drawing — create a new one from the default template with 4 standard views.
        /// Returns the saved .SLDDRW path.
        /// </summary>
        // closePart: close the source part after drawing it. Pass false when drawing the base part
        // (it's the user's open document — closing it would yank their active part).
        public static string Generate(ISldWorks swApp, string basePartPath, string variantPartPath, bool closePart = true)
        {
            string dir = Path.GetDirectoryName(variantPartPath);
            string variantBase = Path.GetFileNameWithoutExtension(variantPartPath);
            string drawingPath = Path.Combine(dir, variantBase + ".SLDDRW");

            // Always generate the drawing FRESH from the variant part, so its dimensions reflect the
            // new geometry. Cloning the original drawing and relinking it keeps the old dimension
            // VALUES (e.g. bore stuck at 25 instead of 60) — that's the dangling-dimension problem.
            // (Cloning a user's existing dimensioned drawing is a separate feature that needs a real
            // dimension-refresh; not used for auto-generated variant drawings.)
            CreateFromTemplate(swApp, variantPartPath, drawingPath, closePart);
            return drawingPath;
        }

        // No reference drawing — create a new one from the default template with 3 standard views
        // and all model dimensions imported. This recipe (open part loaded -> Create3rdAngleViews2
        // -> InsertModelAnnotations3) was validated to produce a fully-dimensioned shop drawing.
        private static void CreateFromTemplate(ISldWorks swApp, string variantPartPath, string outPath, bool closePart = true)
        {
            string template = swApp.GetUserPreferenceStringValue(
                (int)swUserPreferenceStringValue_e.swDefaultTemplateDrawing);

            if (string.IsNullOrEmpty(template) || !File.Exists(template))
                throw new Exception("No default drawing template set in SolidWorks. Set one in Tools → Options → Default Templates.");

            // Open the variant part so views project from real geometry. (It must be LOADED — an
            // unopened path yields empty views.)
            int openErr = 0, openWarn = 0;
            IModelDoc2 part = swApp.OpenDoc6(
                variantPartPath,
                (int)swDocumentTypes_e.swDocPART,
                (int)swOpenDocOptions_e.swOpenDocOptions_Silent,
                "", ref openErr, ref openWarn);

            IModelDoc2 doc = (IModelDoc2)swApp.NewDocument(template, 0, 0, 0);
            if (doc == null) throw new Exception("Could not create drawing document.");

            IDrawingDoc drw = (IDrawingDoc)doc;

            // Front / top / right, auto-projected from the model.
            drw.Create3rdAngleViews2(variantPartPath);

            // Import the model's dimensions into all views (marked + not-marked for drawing).
            int dimTypes = (int)swInsertAnnotation_e.swInsertDimensionsMarkedForDrawing
                         | (int)swInsertAnnotation_e.swInsertDimensionsNotMarkedForDrawing;
            drw.InsertModelAnnotations3(
                (int)swImportModelItemsSource_e.swImportModelItemsFromEntireModel,
                dimTypes, true, false, false, false);

            doc.ViewZoomtofit2();
            doc.ForceRebuild3(false);

            int saveErr = 0, saveWarn = 0;
            bool ok = doc.Extension.SaveAs(
                outPath,
                (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                (int)swSaveAsOptions_e.swSaveAsOptions_Silent,
                null, ref saveErr, ref saveWarn);

            swApp.CloseDoc(outPath);
            if (closePart && part != null) swApp.CloseDoc(variantPartPath);

            if (!ok) throw new Exception("Drawing SaveAs failed (err " + saveErr + ")");
        }
    }
}
