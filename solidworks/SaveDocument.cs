using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class SaveDocumentResult
    {
        public bool Saved;
        public string Path;
        public int Errors;
        public int Warnings;
        public bool MtimeChanged;
        public string Error;
    }

    /// <summary>
    /// SaveDocument — tool 125. Saves the ACTIVE document IN PLACE via IModelDoc2.Save3 — the one tool in the
    /// catalog that deliberately persists to disk; every other handler leaves changes in the open document only
    /// (see the "Forge didn't save" info strings throughout). Save3 is otherwise UNPROVEN on this build (no other
    /// handler calls it — everything else either never saves or uses SaveAs/SaveAs+Copy for a NEW file).
    ///
    /// Fails closed (Rule #6): a `true` Save3 return is not trusted alone — the file's own mtime is read before
    /// and after the call, and only a real, observed disk write counts as Saved.
    /// </summary>
    public static class SaveDocument
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            // batch_convert_files (135) / flat_dxf / drawing PDF export own these nouns outright.
            if (Regex.IsMatch(c, @"\b(step|stp|iges|igs|parasolid|x_t|x_b|stl|dxf|dwg|pdf)\b")) return false;
            if (Regex.IsMatch(c, @"\b(flat[- ]?pattern|flatten|laser|nest)\b")) return false;
            // save_document_as (126) owns "save ... as" / "save ... a copy".
            if (Regex.IsMatch(c, @"\bas\b|\bcopy\b")) return false;
            // SuppressComponents' "suppress everything, save for the corner bolt" exception idiom — not a save.
            if (Regex.IsMatch(c, @"\bsave\s+for\b")) return false;
            return Regex.IsMatch(c, @"\bsave\b");
        }

        public static async Task<SaveDocumentResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new SaveDocumentResult();
            if (model == null) { res.Error = "Open a document first."; return res; }
            string path = null; try { path = model.GetPathName(); } catch { }
            if (string.IsNullOrEmpty(path)) { res.Error = "This document has no path yet — save it once as a new file first."; return res; }
            res.Path = path;

            DateTime before = DateTime.MinValue;
            try { before = File.GetLastWriteTimeUtc(path); } catch { }

            await emit("Writer", "saving " + Path.GetFileName(path), "run", null);
            int errs = 0, warns = 0;
            bool ok;
            try
            {
                ok = model.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref errs, ref warns);
            }
            catch (Exception ex) { res.Error = ex.Message; return res; }
            res.Errors = errs;
            res.Warnings = warns;
            if (!ok)
            {
                res.Error = "Save3 reported failure (errs=" + errs + ", warns=" + warns + ").";
                await emit("Writer", null, "done", "save failed");
                return res;
            }

            DateTime after = DateTime.MinValue;
            try { after = File.GetLastWriteTimeUtc(path); } catch { }
            res.MtimeChanged = before != DateTime.MinValue && after != DateTime.MinValue && after != before;
            res.Saved = res.MtimeChanged;
            await emit("Writer", null, "done", res.Saved ? "saved" : "no observed write");
            if (!res.Saved) res.Error = "Save3 returned success but the file's mtime never changed — treating as unverified.";
            return res;
        }
    }
}
