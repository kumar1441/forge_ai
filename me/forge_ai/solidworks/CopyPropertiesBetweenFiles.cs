using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class CopiedPropertyRow
    {
        public string Name;
        public string SourceValue;
        public string TargetBefore;
        public bool WasPresent;
        public bool AlreadyEqual;
        public bool Verified;
    }

    public class CopyPropertiesBetweenFilesResult
    {
        public bool Verified;
        public bool NeedsConfirm;
        public string Question;
        public string SourcePath;
        public string TargetPath;
        public int TotalSourceProps;
        public int Copied;
        public int AlreadyEqualCount;
        public int Failed;
        public List<CopiedPropertyRow> Rows = new List<CopiedPropertyRow>();
        public string Info;
        public string Error;
    }

    /// <summary>
    /// CopyPropertiesBetweenFiles — tool 142 (WRITE). "Copy the properties from Template.SLDPRT" — template
    /// propagation: reads every file-scope custom property off a SOURCE document (CustomPropertyManager.GetNames()
    /// + Get4, the proven-live read pattern GetCustomProperty.cs/SetCustomProperty.cs already use) and writes each
    /// one onto the currently-open TARGET document via CustomPropertyManager.Add3 (the proven-live write
    /// SetCustomProperty.cs already uses) — no new API surface, just the same two calls aimed at two documents
    /// instead of one.
    ///
    /// TARGET is always the currently active document (Rule: an attach button, not a typed target path — matches
    /// every other in-panel WRITE handler). SOURCE is resolved from an explicit path in the command text (the one
    /// thing Forge cannot guess, same as AddNote's quoted text or OpenDocument's path). If the source is already
    /// open, it's reused (Rule #7 — never disturb what the user already has open); if Forge opened it itself, it's
    /// closed again afterward without saving.
    ///
    /// Per-property IDEMPOTENT (Rule #5): a property already equal on the target is skipped, never rewritten.
    /// FAIL CLOSED (Rule #6): every copied property is independently read back off the TARGET's own
    /// CustomPropertyManager afterward — Add3's return is never trusted alone. Never saves either document.
    /// </summary>
    public static class CopyPropertiesBetweenFiles
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (!Regex.IsMatch(c, @"\bcopy\b")) return false;
            if (!Regex.IsMatch(c, @"\b(custom )?propert(y|ies)\b")) return false;
            return Regex.IsMatch(c, @"\bfrom\b");
        }

        public static async Task<CopyPropertiesBetweenFilesResult> Run(ISldWorks app, IModelDoc2 model, string intent, string attachedFile, Func<string, string, string, string, Task> emit)
        {
            var res = new CopyPropertiesBetweenFilesResult();
            if (model == null) { res.Error = "Open the part or assembly that should receive the properties first."; return res; }

            string sourcePath = ExtractPath(intent);
            if (sourcePath == null && !string.IsNullOrEmpty(attachedFile)) sourcePath = attachedFile;
            if (sourcePath == null)
            {
                res.NeedsConfirm = true;
                res.Question = "Which file should the properties be copied FROM? Attach it or give me the full path.";
                return res;
            }
            if (!File.Exists(sourcePath))
            { res.Error = "Couldn't find \"" + sourcePath + "\"."; return res; }
            res.SourcePath = sourcePath;
            try { res.TargetPath = model.GetPathName(); } catch { }

            // ---- resolve the SOURCE document: reuse if already open (Rule #7), else open it ourselves ----
            bool sourceWasOpen = false;
            IModelDoc2 sourceDoc = null;
            try { sourceDoc = app.GetOpenDocumentByName(sourcePath) as IModelDoc2; } catch { }
            if (sourceDoc != null) sourceWasOpen = true;
            else
            {
                await emit("Reader", "opening the source file to read its properties", "run", null);
                int docType = sourcePath.EndsWith(".sldasm", StringComparison.OrdinalIgnoreCase)
                    ? (int)swDocumentTypes_e.swDocASSEMBLY : (int)swDocumentTypes_e.swDocPART;
                int errs = 0, warns = 0;
                try { sourceDoc = app.OpenDoc6(sourcePath, docType, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref errs, ref warns) as IModelDoc2; }
                catch { sourceDoc = null; }
                if (sourceDoc == null) { res.Error = "Couldn't open the source file \"" + Path.GetFileName(sourcePath) + "\" (errs=" + errs + ")."; return res; }
            }

            try
            {
                CustomPropertyManager srcCpm = null;
                try { srcCpm = sourceDoc.Extension.get_CustomPropertyManager(""); } catch { }
                if (srcCpm == null) { res.Error = "Couldn't reach the source document's custom-property manager."; return res; }

                string[] names = null;
                try { names = srcCpm.GetNames() as string[]; } catch { }
                if (names == null || names.Length == 0)
                { res.Error = Path.GetFileName(sourcePath) + " has no custom properties to copy."; return res; }
                res.TotalSourceProps = names.Length;

                CustomPropertyManager tgtCpm = null;
                try { tgtCpm = model.Extension.get_CustomPropertyManager(""); } catch { }
                if (tgtCpm == null) { res.Error = "Couldn't reach the target document's custom-property manager."; return res; }

                await emit("Scribe", "copying " + names.Length + " propert" + (names.Length == 1 ? "y" : "ies") + " from " + Path.GetFileName(sourcePath), "run", null);
                foreach (var name in names)
                {
                    string srcVal = null, srcResolved = null;
                    try { srcCpm.Get4(name, false, out srcVal, out srcResolved); } catch { }
                    string sourceValue = string.IsNullOrEmpty(srcResolved) ? srcVal : srcResolved;

                    var row = new CopiedPropertyRow { Name = name, SourceValue = sourceValue };
                    row.TargetBefore = ReadResolved(tgtCpm, name, out row.WasPresent);

                    if (row.WasPresent && string.Equals(row.TargetBefore, sourceValue, StringComparison.Ordinal))
                    { row.AlreadyEqual = true; row.Verified = true; res.AlreadyEqualCount++; res.Rows.Add(row); continue; }

                    try { tgtCpm.Add3(name, (int)swCustomInfoType_e.swCustomInfoText, sourceValue ?? "", (int)swCustomPropertyAddOption_e.swCustomPropertyReplaceValue); }
                    catch { res.Rows.Add(row); res.Failed++; continue; }

                    bool present2; string after = ReadResolved(tgtCpm, name, out present2);
                    row.Verified = present2 && string.Equals(after, sourceValue, StringComparison.Ordinal);
                    if (row.Verified) res.Copied++; else res.Failed++;
                    res.Rows.Add(row);
                }
            }
            finally
            {
                if (!sourceWasOpen && sourceDoc != null)
                { try { app.CloseDoc(sourcePath); } catch { } }
            }

            res.Verified = res.Failed == 0 && (res.Copied + res.AlreadyEqualCount) == res.TotalSourceProps;

            if (!res.Verified)
            {
                res.Error = res.Failed + " of " + res.TotalSourceProps + " propert" + (res.TotalSourceProps == 1 ? "y" : "ies") +
                            " failed to copy or verify.";
                await emit("Scribe", null, "fail", res.Error);
                return res;
            }

            res.Info = res.Copied + " propert" + (res.Copied == 1 ? "y" : "ies") + " copied from " + Path.GetFileName(sourcePath) +
                       (res.AlreadyEqualCount > 0 ? " (" + res.AlreadyEqualCount + " already matched)" : "") +
                       ". Forge didn't save either document.";
            await emit("Scribe", null, "done", res.Info);
            return res;
        }

        private static string ReadResolved(CustomPropertyManager cpm, string name, out bool present)
        {
            present = false;
            try
            {
                var names = cpm.GetNames() as string[];
                if (names != null) foreach (var n in names) if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) { present = true; break; }
            }
            catch { }
            if (!present) return null;
            string val = null, resolved = null;
            try { cpm.Get4(name, false, out val, out resolved); } catch { }
            return string.IsNullOrEmpty(resolved) ? val : resolved;
        }

        // shared shape with OpenDocument.ExtractPath, kept as an independent copy per this codebase's convention.
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
