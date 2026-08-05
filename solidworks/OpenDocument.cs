using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class OpenDocumentResult
    {
        public bool Opened;
        public bool AlreadyOpen;
        public string Path;
        public string Title;
        public string DocType;   // "part" | "assembly"
        public string LoadMode;  // "resolved" | "lightweight" | "viewonly"
        public int Errors;
        public int Warnings;
        public string Error;
        public bool NeedsConfirm;
        public string Question;
    }

    /// <summary>
    /// OpenDocument — tool 124. Opens a part/assembly at an explicit path (typed in the prompt, or attached via
    /// the 📎 button — a real user never types a PC path) with an optional load mode: lightweight, or read-only
    /// "Large Design Review". Reuses an already-open document instead of reopening it (Rule #7 — never disturb
    /// what the user already has open). Proven OpenDoc6 path (used throughout the codebase for native
    /// .SLDPRT/.SLDASM files); reopening a SolidWorks-self-exported neutral file is a SEPARATE dead end
    /// (docs/kb/landmines.md) not exercised here.
    /// </summary>
    public static class OpenDocument
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            // DetectFileHealth owns "is it safe/ok to open this" — never a real open command.
            if (Regex.IsMatch(cmd, @"\b(safe|healthy|ok|okay|corrupt|should i)\b", RegexOptions.IgnoreCase)) return false;
            if (!Regex.IsMatch(cmd, @"\bopen\b", RegexOptions.IgnoreCase)) return false;
            // require a file-ish target: an explicit path/extension, or an open-VERB followed by a file/document
            // noun — keeps this off DiagnoseSketch's "open contour/profile" and GetActiveDocument's "what
            // document is open" (noun precedes the verb there, so the ordered match below never fires).
            return Regex.IsMatch(cmd, @"\.(sldprt|sldasm)\b", RegexOptions.IgnoreCase)
                || Regex.IsMatch(cmd, @"\bopen\b.*\b(file|document|part|assembly)\b", RegexOptions.IgnoreCase);
        }

        public static async Task<OpenDocumentResult> Run(ISldWorks app, IModelDoc2 model, string intent, string attachedFile, Func<string, string, string, string, Task> emit)
        {
            var res = new OpenDocumentResult();
            await emit("Ledger", "finding the file to open", "run", null);

            string path = ExtractPath(intent);
            if (path == null && !string.IsNullOrEmpty(attachedFile)) path = attachedFile;
            if (path == null)
            {
                await emit("Ledger", null, "done", "no file resolved");
                res.NeedsConfirm = true;
                res.Question = "Which file should I open? Attach it or give me the full path.";
                return res;
            }
            if (!File.Exists(path))
            {
                await emit("Ledger", null, "done", "not found");
                res.Error = "Couldn't find \"" + path + "\".";
                return res;
            }
            res.Path = path;
            await emit("Ledger", null, "done", Path.GetFileName(path));

            int docType = path.EndsWith(".sldasm", StringComparison.OrdinalIgnoreCase)
                ? (int)swDocumentTypes_e.swDocASSEMBLY : (int)swDocumentTypes_e.swDocPART;
            res.DocType = docType == (int)swDocumentTypes_e.swDocASSEMBLY ? "assembly" : "part";

            int options = (int)swOpenDocOptions_e.swOpenDocOptions_Silent;
            string cl = (intent ?? "").ToLowerInvariant();
            if (Regex.IsMatch(cl, @"\blightweight\b"))
            {
                options |= (int)swOpenDocOptions_e.swOpenDocOptions_LoadLightweight
                         | (int)swOpenDocOptions_e.swOpenDocOptions_OverrideDefaultLoadLightweight;
                res.LoadMode = "lightweight";
            }
            else if (Regex.IsMatch(cl, @"\b(large design review|view.?only|read.?only)\b"))
            {
                options |= (int)swOpenDocOptions_e.swOpenDocOptions_ViewOnly;
                res.LoadMode = "viewonly";
            }
            else
            {
                res.LoadMode = "resolved";
            }

            await emit("Opener", "opening " + Path.GetFileName(path), "run", null);
            try
            {
                IModelDoc2 already = null;
                try { already = app.GetOpenDocumentByName(path) as IModelDoc2; } catch { }
                if (already != null)
                {
                    res.AlreadyOpen = true;
                    res.Opened = true;
                    try { res.Title = already.GetTitle(); } catch { }
                    await emit("Opener", null, "done", "already open");
                    return res;
                }

                int errs = 0, warns = 0;
                var doc = app.OpenDoc6(path, docType, options, "", ref errs, ref warns) as IModelDoc2;
                res.Errors = errs;
                res.Warnings = warns;
                if (doc == null)
                {
                    res.Error = "Couldn't open " + Path.GetFileName(path) + " (errs=" + errs + ", warns=" + warns + ").";
                    await emit("Opener", null, "done", "open failed");
                    return res;
                }
                res.Opened = true;
                try { res.Title = doc.GetTitle(); } catch { }
                await emit("Opener", null, "done", "opened " + res.Title);
            }
            catch (Exception ex) { res.Error = ex.Message; }
            return res;
        }

        // shared with GroundTruth.MeasureOpenDocument, which re-derives the SAME target independently rather
        // than trusting the handler's own reported path.
        public static string ExtractPath(string intent)
        {
            if (string.IsNullOrEmpty(intent)) return null;
            var q = Regex.Match(intent, "[\"']([^\"']+\\.(?:sldasm|sldprt))[\"']", RegexOptions.IgnoreCase);
            if (q.Success) return q.Groups[1].Value.Trim();
            var m = Regex.Match(intent, @"([a-zA-Z]:\\.+?\.(?:sldasm|sldprt)|\\\\[^\s].+?\.(?:sldasm|sldprt))", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value.Trim();
            return null;
        }
    }
}
