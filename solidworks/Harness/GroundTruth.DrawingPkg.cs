using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground-truth for the drawing-package handler (demo #9). Shares NOTHING with DrawingPkg.cs:
    /// its own file discovery, its own drawing open, its own view/dimension traversal, its own rebuild.
    ///
    /// Because the handler intentionally NEVER saves the source .SLDDRW (Rule #7), the dangling dims still live
    /// in the file on disk. So this re-derives the truth the same honest way any observer would: open each
    /// drawing fresh, count dangling before a rebuild, rebuild in-memory, count what a rebuild could NOT
    /// re-attach ("needs eyes"). It also counts the PDFs the handler actually wrote to disk — the one output
    /// that IS persisted — so the harness can assert PDFs-written == drawings-found.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureDrawingPkg(ISldWorks app, IModelDoc2 model)
        {
            var mo = new JObject();
            string modelPath = null; try { modelPath = model.GetPathName(); } catch { }
            if (string.IsNullOrEmpty(modelPath)) { mo["applicable"] = false; return mo; }

            string dir = null; try { dir = Path.GetDirectoryName(modelPath); } catch { }
            var drawings = DpFindDrawings(dir, modelPath);
            mo["drawingsFound"] = drawings.Count;
            if (drawings.Count == 0) { mo["applicable"] = false; return mo; }
            mo["applicable"] = true;

            int danglingBefore = 0, danglingAfter = 0, rebuildErrors = 0, opened = 0, openFailed = 0;
            foreach (var path in drawings)
            {
                bool wasOpen; IModelDoc2 doc = DpOpenDrawing(app, path, out wasOpen);
                if (doc == null) { openFailed++; continue; }
                opened++;
                var drw = doc as IDrawingDoc;
                danglingBefore += DpCountDangling(drw);
                try { doc.ForceRebuild3(false); } catch { }
                danglingAfter += DpCountDangling(drw);
                try { rebuildErrors += doc.Extension.GetWhatsWrongCount(); } catch { }
                if (!wasOpen) { try { app.CloseDoc(path); } catch { } }
            }
            mo["drawingsOpened"] = opened;
            mo["drawingsOpenFailed"] = openFailed;
            mo["danglingBeforeRebuild"] = danglingBefore;   // total dangling as they sit on disk
            mo["danglingAfterRebuild"] = danglingAfter;      // what a rebuild could NOT reattach = the true "needs eyes"
            mo["danglingRepairable"] = danglingBefore - danglingAfter;
            mo["rebuildErrors"] = rebuildErrors;

            // the ONE persisted output: count the PDFs actually written to the handler's output folder
            int pdfsOnDisk = 0; string outDir = dir != null ? Path.Combine(dir, "Forge-PDF") : null;
            try { if (outDir != null && Directory.Exists(outDir)) pdfsOnDisk = Directory.GetFiles(outDir, "*.pdf").Length; } catch { }
            mo["outputDir"] = outDir;
            mo["pdfsOnDisk"] = pdfsOnDisk;
            return mo;
        }

        // own sibling-.SLDDRW discovery (independent of DrawingPkg.FindSiblingDrawings)
        private static List<string> DpFindDrawings(string dir, string modelPath)
        {
            var list = new List<string>();
            try
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return list;
                foreach (var f in Directory.GetFiles(dir, "*.slddrw")) list.Add(f);
            }
            catch { }
            return list;
        }

        private static IModelDoc2 DpOpenDrawing(ISldWorks app, string path, out bool wasOpen)
        {
            wasOpen = false;
            try
            {
                var existing = app.GetOpenDocumentByName(path) as IModelDoc2;
                if (existing != null) { wasOpen = true; return existing; }
            }
            catch { }
            int err = 0, warn = 0;
            try
            {
                return app.OpenDoc6(path, (int)swDocumentTypes_e.swDocDRAWING,
                    (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref err, ref warn) as IModelDoc2;
            }
            catch { return null; }
        }

        // own view/dimension traversal (independent of DrawingPkg.CountDangling)
        private static int DpCountDangling(IDrawingDoc drw)
        {
            int n = 0;
            if (drw == null) return 0;
            try
            {
                var view = drw.GetFirstView() as IView;
                while (view != null)
                {
                    object[] dims = null;
                    try { dims = view.GetDisplayDimensions() as object[]; } catch { }
                    if (dims != null)
                        foreach (var o in dims)
                        {
                            var dd = o as DisplayDimension; if (dd == null) continue;
                            bool d = false;
                            try { var ann = dd.GetAnnotation() as IAnnotation; if (ann != null) d = ann.IsDangling(); } catch { }
                            if (d) n++;
                        }
                    view = view.GetNextView() as IView;
                }
            }
            catch { }
            return n;
        }
    }
}
