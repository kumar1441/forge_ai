using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class DrawingPkgResult
    {
        public int DrawingsFound;      // sibling .SLDDRW files discovered next to the model
        public int Processed;          // drawings opened + rebuilt without throwing
        public int CleanDrawings;      // had zero dangling dims to begin with
        public int RepairedDrawings;   // had dangling dims, ALL cleared by rebuild
        public int NeedsEyesDrawings;  // still has dangling dims a rebuild couldn't reattach
        public int DimsRepaired;       // total dims that stopped dangling after rebuild
        public int DimsNeedsEyes;      // total dims still dangling across all drawings
        public int PdfsWritten;        // PDFs actually verified on disk
        public int Failed;             // drawings that threw (open/rebuild/export)
        public string OutputDir;       // where the PDFs landed
        public string Info;
        public string Error;
        public List<string> Diag = new List<string>();
    }

    /// <summary>
    /// Drawing package — "rebuild all drawings, fix dangling dims, export PDFs" (demo #9). Finds every sibling
    /// .SLDDRW next to the open model, opens each, rebuilds it (which re-attaches dangling dims whose geometry
    /// came back), counts what stayed dangling ("needs eyes"), and exports a PDF to a dedicated output folder.
    ///
    /// WRITES files (PDFs) but NEVER touches source: the .SLDDRW is opened, rebuilt IN MEMORY, exported to a
    /// SEPARATE .pdf, then closed WITHOUT saving — so the source drawing on disk is untouched (Rule #7). Per-drawing
    /// try/catch so one broken drawing can't stop the batch (Rule #4). Verified INDEPENDENTLY by GroundTruth
    /// (MeasureDrawingPkg re-counts drawings, remaining dangling, and PDFs on disk with its own code).
    /// </summary>
    public static class DrawingPkg
    {
        public static bool IsDrawingPkgIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            cmd = cmd.ToLowerInvariant();
            // Not a drawing intent if it's clearly sheet-metal flat-pattern DXF (that's FlatDxf's job) — "sheet metal"
            // and dxf/flat-pattern/laser must not be swallowed by the drawing-"sheet" overlap.
            if (cmd.Contains("sheet metal") || cmd.Contains("sheetmetal") ||
                Regex.IsMatch(cmd, @"\b(dxf|dwg|flat[- ]?pattern|laser|nest)\b")) return false;
            bool drw = Regex.IsMatch(cmd, @"\b(drawing|drawings|drw|slddrw|sheet|sheets)\b");
            bool op = Regex.IsMatch(cmd, @"\b(rebuild|pdf|pdfs|export|dangling|dim|dims|dimension|dimensions|package|batch)\b");
            bool phrase = Regex.IsMatch(cmd, @"\b(drawing package|drawings? package|export (the )?drawings?|pdf (the )?drawings?)\b");
            return (drw && op) || phrase;
        }

        public static async Task<DrawingPkgResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new DrawingPkgResult();

            string modelPath = null; try { modelPath = model.GetPathName(); } catch { }
            if (string.IsNullOrEmpty(modelPath))
            { res.Error = "Save the model first — Forge finds drawings next to it on disk."; return res; }

            await emit("Ledger", "finding drawings next to the model", "run", null);
            var drawings = FindSiblingDrawings(modelPath);
            res.DrawingsFound = drawings.Count;
            await emit("Ledger", null, "done", res.DrawingsFound == 0
                ? "no .SLDDRW files found in " + Path.GetDirectoryName(modelPath)
                : res.DrawingsFound + " drawing" + (res.DrawingsFound == 1 ? "" : "s") + " found");
            if (res.DrawingsFound == 0)
            { res.Error = "No drawings (.SLDDRW) found next to this model. Nothing to package."; return res; }

            string outDir = Path.Combine(Path.GetDirectoryName(modelPath), "Forge-PDF");
            try { Directory.CreateDirectory(outDir); } catch { }
            res.OutputDir = outDir;

            bool big = res.DrawingsFound > 10;
            await emit("Mender", "rebuilding drawings and repairing dangling dims", "run", null);
            int i = 0;
            foreach (var path in drawings)
            {
                i++;
                if (big && (i == 1 || i % 5 == 0 || i == drawings.Count))
                    await emit("Mender", "processing " + i + "/" + drawings.Count, "run", null);

                string baseName = Path.GetFileNameWithoutExtension(path);
                try
                {
                    bool wasOpen; IModelDoc2 doc = OpenDrawing(app, path, out wasOpen);
                    if (doc == null) { res.Failed++; res.Diag.Add(baseName + ": could not open"); continue; }

                    var drw = doc as IDrawingDoc;
                    int before = CountDangling(drw);
                    try { doc.ForceRebuild3(false); } catch { }
                    int after = CountDangling(drw);

                    int repaired = before - after; if (repaired < 0) repaired = 0;
                    res.DimsRepaired += repaired;
                    res.DimsNeedsEyes += after;
                    if (before == 0) res.CleanDrawings++;
                    else if (after == 0) res.RepairedDrawings++;
                    else res.NeedsEyesDrawings++;
                    res.Processed++;

                    // export the PDF to a SEPARATE file — source .SLDDRW is never written
                    string pdfPath = Path.Combine(outDir, baseName + ".pdf");
                    bool exported = ExportPdf(doc, pdfPath);
                    if (exported && File.Exists(pdfPath)) res.PdfsWritten++;
                    else res.Diag.Add(baseName + ": PDF export failed");

                    res.Diag.Add(baseName + ": " + (before == 0 ? "clean" :
                        (after == 0 ? repaired + " dim(s) repaired" : repaired + " repaired, " + after + " still dangling")) +
                        (exported ? " -> PDF" : " -> PDF FAILED"));

                    // CloseDoc discards the in-memory rebuild (we never saved) — source untouched. Never close a
                    // drawing the user already had open.
                    if (!wasOpen) { try { app.CloseDoc(path); } catch { } }
                }
                catch (Exception ex)
                {
                    res.Failed++;
                    res.Diag.Add(baseName + ": " + ex.GetType().Name);
                }
            }
            await emit("Mender", null, "done",
                res.CleanDrawings + " clean · " + res.DimsRepaired + " dims repaired · " +
                res.NeedsEyesDrawings + " need eyes" + (res.Failed > 0 ? " · " + res.Failed + " failed" : ""));
            foreach (var d in res.Diag) await emit(null, null, "done", "▸ " + d);

            await emit("Sentinel", "confirming PDFs landed on disk", "run", null);
            int onDisk = 0;
            try { onDisk = Directory.GetFiles(outDir, "*.pdf").Length; } catch { }
            await emit("Sentinel", null, "done", res.PdfsWritten + " PDF" + (res.PdfsWritten == 1 ? "" : "s") +
                " written to " + outDir + " (" + onDisk + " total in folder)");

            res.Info = res.CleanDrawings + " clean, " + res.DimsRepaired + " dim" + (res.DimsRepaired == 1 ? "" : "s") +
                " auto-repaired" +
                (res.NeedsEyesDrawings > 0 ? ", " + res.NeedsEyesDrawings + " drawing" + (res.NeedsEyesDrawings == 1 ? "" : "s") + " need your eyes (" + res.DimsNeedsEyes + " dims)" : "") +
                (res.Failed > 0 ? ", " + res.Failed + " failed to process" : "") +
                " - " + res.PdfsWritten + " PDF" + (res.PdfsWritten == 1 ? "" : "s") + " written to " + outDir +
                ". Source drawings untouched.";
            return res;
        }

        // ---- every sibling .SLDDRW next to the model (the project's drawing set). Case-insensitive on Windows. ----
        private static List<string> FindSiblingDrawings(string modelPath)
        {
            var list = new List<string>();
            try
            {
                string dir = Path.GetDirectoryName(modelPath);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return list;
                // exact-name sibling first (the model's own drawing), then the rest of the folder's drawings
                string ownDrw = Path.Combine(dir, Path.GetFileNameWithoutExtension(modelPath) + ".SLDDRW");
                foreach (var f in Directory.GetFiles(dir, "*.slddrw"))
                {
                    // skip SolidWorks/Office lock files (~$name.slddrw) that exist only while a doc is open
                    if (Path.GetFileName(f).StartsWith("~$")) continue;
                    if (string.Equals(f, ownDrw, StringComparison.OrdinalIgnoreCase)) continue;
                    list.Add(f);
                }
                if (File.Exists(ownDrw)) list.Insert(0, ownDrw);
            }
            catch { }
            return list;
        }

        // Open a drawing silently. wasOpen=true if the user already had it open (so we must NOT close it).
        private static IModelDoc2 OpenDrawing(ISldWorks app, string path, out bool wasOpen)
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

        // Count dangling display dimensions across every view of the drawing.
        private static int CountDangling(IDrawingDoc drw)
        {
            int dangling = 0;
            if (drw == null) return 0;
            try
            {
                var view = drw.GetFirstView() as IView;
                while (view != null)
                {
                    object[] dims = null;
                    try { dims = view.GetDisplayDimensions() as object[]; } catch { }
                    if (dims != null)
                    {
                        foreach (var o in dims)
                        {
                            var dd = o as DisplayDimension; if (dd == null) continue;
                            bool d = false;
                            try { var ann = dd.GetAnnotation() as IAnnotation; if (ann != null) d = ann.IsDangling(); } catch { }
                            if (d) dangling++;
                        }
                    }
                    view = view.GetNextView() as IView;
                }
            }
            catch { }
            return dangling;
        }

        // Export the whole drawing to PDF. A null ExportData + a .pdf filename makes SW export PDF; the source
        // .SLDDRW is never modified by this.
        private static bool ExportPdf(IModelDoc2 doc, string pdfPath)
        {
            int err = 0, warn = 0;
            try
            {
                try { if (File.Exists(pdfPath)) File.Delete(pdfPath); } catch { }
                return doc.Extension.SaveAs(pdfPath, (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                    (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, ref err, ref warn);
            }
            catch { return false; }
        }
    }
}
