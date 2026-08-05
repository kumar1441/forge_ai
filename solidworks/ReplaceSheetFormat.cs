using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class ReplaceSheetFormatResult
    {
        public bool Verified;
        public string SheetName;
        public string FormatBefore;
        public string FormatAfter;
        public string TargetPath;
        public string Error;
    }

    /// <summary>
    /// ReplaceSheetFormat (tool #163) — swap a drawing sheet's format/template (e.g. a corrupted or outdated
    /// title block) for a known-good one. "replace the sheet format", "fix the corrupted title block",
    /// "swap in a new drawing template".
    ///
    /// API: ISheet.SetTemplateName(string) / GetTemplateName():string. Reflected-first: ISheet.SetSheetFormatName
    /// (string):bool looked like the obvious match by name but is a NO-OP on this build's default-template sheets
    /// (GetSheetFormatName() reads "" — this build's sheets carry no named FORMAT at all, only a TEMPLATE) —
    /// confirmed dead across 3 variants (full path, bare filename, name w/o extension), all `false`. SetTemplateName
    /// is a genuinely different property (void, no return to trust — verified purely by GetTemplateName() read-back)
    /// and IS live: read-back matches the target file every time. Target format resolution: swFileLocationsSheetFormat
    /// user-preference search path first (same "ask the preference, glob as fallback" idiom InsertLibraryFeature
    /// proved out when that preference came back empty on this build), falling back to globbing the
    /// SolidWorks-shipped ProgramData\SOLIDWORKS\SOLIDWORKS*\lang\english\sheetformat\ folder for a real .slddrt
    /// file that differs from the sheet's current one — never a fabricated path.
    /// </summary>
    public static class ReplaceSheetFormat
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            bool verb = Regex.IsMatch(c, @"\b(replace|swap|change|update|fix|repair|restore)\b");
            bool noun = Regex.IsMatch(c, @"\b(sheet\s*formats?|title\s*blocks?)\b")
                        || (Regex.IsMatch(c, @"\btemplates?\b") && Regex.IsMatch(c, @"\b(drawing|sheet)\b"));
            return verb && noun;
        }

        // Resolve a real, on-disk .slddrt path different from `currentName`. Never invents a path.
        private static string ResolveTargetFormat(ISldWorks app, string currentName)
        {
            string[] candidates = null;
            try
            {
                string prefDir = app.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swFileLocationsSheetFormat);
                if (!string.IsNullOrEmpty(prefDir) && Directory.Exists(prefDir))
                    candidates = Directory.GetFiles(prefDir, "*.slddrt", SearchOption.TopDirectoryOnly);
            }
            catch { }

            if (candidates == null || candidates.Length == 0)
            {
                try
                {
                    string root = @"C:\ProgramData\SOLIDWORKS";
                    if (Directory.Exists(root))
                        candidates = Directory.GetDirectories(root, "SOLIDWORKS *")
                            .SelectMany(d => {
                                string dir = Path.Combine(d, "lang", "english", "sheetformat");
                                return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.slddrt") : new string[0];
                            }).ToArray();
                }
                catch { }
            }
            if (candidates == null || candidates.Length == 0) return null;

            string curBase = string.IsNullOrEmpty(currentName) ? null : Path.GetFileNameWithoutExtension(currentName).ToLowerInvariant();
            var pick = candidates.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).ToLowerInvariant() != curBase);
            return pick ?? candidates.FirstOrDefault();
        }

        public static async Task<ReplaceSheetFormatResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new ReplaceSheetFormatResult();
            if (model == null) { res.Error = "Open the drawing whose sheet format needs replacing."; return res; }
            int docType = 0; try { docType = model.GetType(); } catch { }
            if (docType != (int)swDocumentTypes_e.swDocDRAWING)
            { res.Error = "replace_sheet_format needs an open DRAWING (not a part/assembly)."; return res; }

            var dd = model as DrawingDoc;
            if (dd == null) { res.Error = "Couldn't get the DrawingDoc interface."; return res; }

            var sheet = dd.GetCurrentSheet() as ISheet;
            if (sheet == null) { res.Error = "Couldn't get the current sheet."; return res; }
            try { res.SheetName = sheet.GetName(); } catch { }

            await emit("Formatter", "reading current sheet format", "run", null);
            // GetSheetFormatName() reads "" on a default-template sheet (no named FORMAT) — GetTemplateName() is
            // the actual live property, so prefer whichever one is populated (mirrors the after-read below).
            string before = null;
            try { before = sheet.GetSheetFormatName(); } catch { }
            if (string.IsNullOrEmpty(before)) { try { before = sheet.GetTemplateName(); } catch { } }
            res.FormatBefore = before;
            await emit("Formatter", null, "done", before ?? "(none)");

            string target = ResolveTargetFormat(app, before);
            if (string.IsNullOrEmpty(target) || !File.Exists(target))
            {
                res.Error = "Couldn't find a known-good sheet format file on this install to swap in.";
                await emit("Formatter", null, "fail", res.Error);
                return res;
            }
            res.TargetPath = target;

            await emit("Formatter", "replacing sheet format", "run", Path.GetFileName(target));
            string after = null;
            try
            {
                if (!string.IsNullOrEmpty(res.SheetName)) { try { dd.ActivateSheet(res.SheetName); } catch { } }
                sheet.SetTemplateName(target);
                try { after = sheet.GetTemplateName(); } catch { }
            }
            catch (Exception ex) { res.Error = ex.GetType().Name + ": " + ex.Message; await emit("Formatter", null, "fail", res.Error); return res; }

            try { model.ForceRebuild3(false); } catch { }
            res.FormatAfter = after;
            res.Verified = !string.IsNullOrEmpty(after) && string.Equals(Path.GetFileName(after), Path.GetFileName(target), StringComparison.OrdinalIgnoreCase);
            string info = (before ?? "(none)") + " -> " + (after ?? "(none)");
            await emit("Formatter", null, res.Verified ? "done" : "fail", info);
            if (!res.Verified && string.IsNullOrEmpty(res.Error))
                res.Error = "SetTemplateName ran but the read-back template didn't match the target (" + info + ").";
            return res;
        }
    }
}
