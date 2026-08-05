using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class SaveDocumentAsResult
    {
        public bool Saved;
        public string SourcePath;
        public string OutputPath;
        public int Errors;
        public int Warnings;
        public bool ReopenVerified;
        public string Error;
        public bool NeedsConfirm;
        public string Question;
    }

    /// <summary>
    /// SaveDocumentAs — tool 126. Saves the ACTIVE document to a new name/path in the SAME native format
    /// (.SLDPRT/.SLDASM) via IModelDocExtension.SaveAs with swSaveAsOptions_Copy, which writes the new file
    /// WITHOUT rebinding the active document's own identity to it (proven pattern, same as
    /// DrawingGenerator/VariantGenerator's variant-copy SaveAs calls) — the source stays untouched and stays
    /// the active document throughout. Neutral-format export (STEP/IGES/Parasolid) is batch_convert_files'
    /// (135) territory, excluded here so the two never collide.
    ///
    /// Fails closed (Rule #6): a `true` SaveAs return is not trusted. The written copy is a NATIVE file (not
    /// a SolidWorks-self-exported neutral format, so the reopen landmine in docs/kb/landmines.md does not
    /// apply), so it is independently reopened read-only, its doc type checked, then closed again — the
    /// strongest verification available, unlike batch_convert_files' text-content fallback.
    /// </summary>
    public static class SaveDocumentAs
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            // batch_convert_files (135) / flat_dxf / drawing PDF export own these nouns outright.
            if (Regex.IsMatch(c, @"\b(step|stp|iges|igs|parasolid|x_t|x_b|stl|dxf|dwg|pdf)\b")) return false;
            return Regex.IsMatch(c, @"\bsave\b.*\bas\b") || Regex.IsMatch(c, @"\bsave\b.*\bcopy\b");
        }

        public static async Task<SaveDocumentAsResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new SaveDocumentAsResult();
            if (model == null) { res.Error = "Open a document first."; return res; }
            string srcPath = null; try { srcPath = model.GetPathName(); } catch { }
            if (string.IsNullOrEmpty(srcPath)) { res.Error = "The active document has no path yet — save it once normally first."; return res; }
            res.SourcePath = srcPath;

            await emit("Scribe", "finding the new name", "run", null);
            string outPath = ResolveOutputPath(intent, srcPath);
            if (outPath == null)
            {
                await emit("Scribe", null, "done", "no name resolved");
                res.NeedsConfirm = true;
                res.Question = "What should I save it as? Give me a new name or path.";
                return res;
            }
            if (string.Equals(outPath, srcPath, StringComparison.OrdinalIgnoreCase))
            {
                res.Error = "That's the same file — give me a different name to save as.";
                return res;
            }
            res.OutputPath = outPath;
            await emit("Scribe", null, "done", Path.GetFileName(outPath));

            DateTime srcMtimeBefore = DateTime.MinValue;
            try { srcMtimeBefore = File.GetLastWriteTimeUtc(srcPath); } catch { }

            await emit("Writer", "saving " + Path.GetFileName(outPath), "run", null);
            int errs = 0, warns = 0;
            bool ok = false;
            try
            {
                ok = model.Extension.SaveAs(outPath, (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                    (int)swSaveAsOptions_e.swSaveAsOptions_Silent | (int)swSaveAsOptions_e.swSaveAsOptions_Copy,
                    null, ref errs, ref warns);
            }
            catch (Exception ex) { res.Error = ex.Message; return res; }
            res.Errors = errs;
            res.Warnings = warns;
            if (!ok || !File.Exists(outPath))
            {
                res.Error = "SaveAs reported failure (errs=" + errs + ", warns=" + warns + ").";
                await emit("Writer", null, "done", "save failed");
                return res;
            }
            await emit("Writer", null, "done", "written");

            // fail-closed: reopen the COPY (native format — no reopen landmine here) and confirm the API can
            // actually make sense of it, never a stub/truncated write.
            await emit("Sentinel", "verifying the copy", "run", null);
            try
            {
                int docType = outPath.EndsWith(".sldasm", StringComparison.OrdinalIgnoreCase)
                    ? (int)swDocumentTypes_e.swDocASSEMBLY : (int)swDocumentTypes_e.swDocPART;
                int rErrs = 0, rWarns = 0;
                var copy = app.OpenDoc6(outPath, docType,
                    (int)swOpenDocOptions_e.swOpenDocOptions_Silent | (int)swOpenDocOptions_e.swOpenDocOptions_ReadOnly,
                    "", ref rErrs, ref rWarns) as IModelDoc2;
                if (copy != null)
                {
                    res.ReopenVerified = true;
                    string title = null; try { title = copy.GetTitle(); } catch { }
                    try { app.CloseDoc(title); } catch { }
                }
            }
            catch { }
            await emit("Sentinel", null, "done", res.ReopenVerified ? "verified" : "unverified");

            if (srcMtimeBefore != DateTime.MinValue)
            {
                DateTime after = DateTime.MinValue;
                try { after = File.GetLastWriteTimeUtc(srcPath); } catch { }
                if (after != srcMtimeBefore)
                {
                    res.Error = "Source file was modified by the save — this should never happen with a copy save.";
                    return res;
                }
            }

            res.Saved = true;
            return res;
        }

        // shared with GroundTruth.MeasureSaveDocumentAs, which independently re-lists the disk rather than
        // trusting the handler's own OutputPath.
        public static string ResolveOutputPath(string intent, string srcPath)
        {
            if (string.IsNullOrEmpty(intent)) return null;
            string ext = Path.GetExtension(srcPath);

            // an explicit full path in the command.
            var full = Regex.Match(intent, @"([a-zA-Z]:\\.+?\.(?:sldasm|sldprt))", RegexOptions.IgnoreCase);
            if (full.Success) return full.Groups[1].Value.Trim();

            // "save (this|it|a copy) as <name>" — a bare filename, quoted or not, optionally with a native extension.
            var m = Regex.Match(intent, @"\bas\s+[""']?([a-zA-Z0-9_\-]+)(\.(?:sldasm|sldprt))?[""']?", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                string name = m.Groups[1].Value.Trim();
                string useExt = m.Groups[2].Success ? m.Groups[2].Value : ext;
                return Path.Combine(Path.GetDirectoryName(srcPath), name + useExt);
            }
            return null;
        }
    }
}
