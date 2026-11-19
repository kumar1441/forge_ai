using System.IO;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT check for import_file. Deliberately does NOT trust the handler's own Imported/Volume report —
        // re-derives the source path from the SAME intent text via ImportFile.ExtractPath (not the handler's result
        // object), re-derives the expected "<source-folder>\forge-imported\<name>.SLDPRT" output path the same way
        // BatchConvertFiles' GT re-lists forge-converted/, then independently re-measures the SAVED PART — reusing
        // it if the handler left it open (the documented convention), or opening it fresh via the proven-live native
        // OpenDoc6 path (NOT LoadFile4 — this is now a plain .SLDPRT, no neutral-format reopen landmine applies) if
        // it isn't found open, closing what it opened itself so the GT probe leaves no extra side effect.
        public static JObject MeasureImportFile(ISldWorks app, string intent)
        {
            var res = new JObject();
            string srcPath = ImportFile.ExtractPath(intent);
            res["sourcePath"] = srcPath;
            if (string.IsNullOrEmpty(srcPath)) { res["found"] = false; return res; }

            string folder = Path.GetDirectoryName(srcPath);
            string outPath = Path.Combine(folder, "forge-imported", Path.GetFileNameWithoutExtension(srcPath) + "-imported.SLDPRT");
            res["expectedOutputPath"] = outPath;

            bool onDisk = File.Exists(outPath);
            res["onDisk"] = onDisk;
            if (!onDisk) { res["found"] = false; return res; }
            try { res["bytes"] = new FileInfo(outPath).Length; } catch { }

            IModelDoc2 doc = null;
            bool openedHere = false;
            try { doc = app.GetOpenDocumentByName(outPath) as IModelDoc2; } catch { }
            if (doc == null)
            {
                try
                {
                    int errs = 0, warns = 0;
                    doc = app.OpenDoc6(outPath, (int)swDocumentTypes_e.swDocPART, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref errs, ref warns) as IModelDoc2;
                    openedHere = doc != null;
                }
                catch { }
            }
            res["found"] = doc != null;
            if (doc == null) return res;

            try { res["title"] = doc.GetTitle(); } catch { }
            try { res["volume"] = doc.Extension.CreateMassProperty().Volume; } catch { }
            if (openedHere) { try { app.CloseDoc(doc.GetTitle()); } catch { } }
            return res;
        }
    }
}
