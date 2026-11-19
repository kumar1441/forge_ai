using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class CloseDocumentResult
    {
        public bool Closed;
        public bool WasOpen;
        public bool Saved;
        public string Path;
        public string Error;
        public bool NeedsConfirm;
        public string Question;
    }

    /// <summary>
    /// CloseDocument — tool 127. Closes an EXPLICITLY-NAMED document (never the currently-active one Forge is
    /// running inside of — that would pull the rug out from under the very session the user is talking to, and
    /// break the panel's own context). Requires a path/extension or a file/document noun (Rule #2, ask rather
    /// than guess which one). If the target isn't open yet, opens it first via the proven OpenDoc6 path (same
    /// as OpenDocument, tool 124) so there is something concrete to close — functionally identical to closing
    /// a document a user already had open, and avoids a silent no-op that never touches the real API.
    /// </summary>
    public static class CloseDocument
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            if (!Regex.IsMatch(cmd, @"\bclose\b", RegexOptions.IgnoreCase)) return false;
            return Regex.IsMatch(cmd, @"\.(sldprt|sldasm)\b", RegexOptions.IgnoreCase)
                || Regex.IsMatch(cmd, @"\bclose\b.*\b(file|document|part|assembly)\b", RegexOptions.IgnoreCase);
        }

        public static async Task<CloseDocumentResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new CloseDocumentResult();
            await emit("Ledger", "finding the document to close", "run", null);

            string path = OpenDocument.ExtractPath(intent);
            if (path == null)
            {
                await emit("Ledger", null, "done", "no file resolved");
                res.NeedsConfirm = true;
                res.Question = "Which document should I close? Give me the full path.";
                return res;
            }
            string activePath = null; try { activePath = model?.GetPathName(); } catch { }
            if (!string.IsNullOrEmpty(activePath) && string.Equals(path, activePath, StringComparison.OrdinalIgnoreCase))
            {
                res.Error = "That's the document Forge is currently working in — I can't close it out from under you.";
                return res;
            }
            res.Path = path;
            await emit("Ledger", null, "done", Path.GetFileName(path));

            try
            {
                IModelDoc2 doc = null;
                try { doc = app.GetOpenDocumentByName(path) as IModelDoc2; } catch { }
                res.WasOpen = doc != null;

                if (doc == null)
                {
                    if (!File.Exists(path)) { res.Error = "Couldn't find \"" + path + "\"."; return res; }
                    int docType = path.EndsWith(".sldasm", StringComparison.OrdinalIgnoreCase)
                        ? (int)swDocumentTypes_e.swDocASSEMBLY : (int)swDocumentTypes_e.swDocPART;
                    int errs = 0, warns = 0;
                    doc = app.OpenDoc6(path, docType, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref errs, ref warns) as IModelDoc2;
                    if (doc == null) { res.Error = "Couldn't open \"" + Path.GetFileName(path) + "\" to close it (errs=" + errs + ")."; return res; }
                }

                bool wantsSave = Regex.IsMatch(intent ?? "", @"\b(and save|with save|save it|save first|saving)\b", RegexOptions.IgnoreCase);
                string title = null; try { title = doc.GetTitle(); } catch { }

                if (wantsSave)
                {
                    await emit("Writer", "saving before close", "run", null);
                    int se = 0, sw = 0;
                    try { res.Saved = doc.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref se, ref sw); } catch { }
                    await emit("Writer", null, "done", res.Saved ? "saved" : "save failed");
                }

                await emit("Closer", "closing " + Path.GetFileName(path), "run", null);
                app.CloseDoc(title);

                IModelDoc2 stillOpen = null;
                try { stillOpen = app.GetOpenDocumentByName(path) as IModelDoc2; } catch { }
                res.Closed = stillOpen == null;
                await emit("Closer", null, "done", res.Closed ? "closed" : "still open");
                if (!res.Closed) res.Error = "CloseDoc was called but the document is still showing as open.";
            }
            catch (Exception ex) { res.Error = ex.Message; }
            return res;
        }
    }
}
