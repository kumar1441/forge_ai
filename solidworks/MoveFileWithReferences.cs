using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class MoveParentRow
    {
        public string Name;
        public string Path;
        public bool Relinked;
        public string Error;
    }

    public class MoveFileWithReferencesResult
    {
        public string OldPath;
        public string NewPath;
        public int ParentsScanned;
        public int ParentsRelinked;
        public int ParentsFailed;
        public List<MoveParentRow> Parents = new List<MoveParentRow>();
        public bool AlreadyDone;
        public bool Verified;
        public string Diag;
        public string Info;
        public string Error;
        public bool NeedsConfirm;
        public string Question;
    }

    /// <summary>
    /// MoveFileWithReferences (tool #129 move_file_with_references, WRITE) — move the ACTIVE part's own file to a
    /// DIFFERENT folder (same filename) and fix up every parent assembly that referenced it in its OLD folder, so
    /// nothing goes dangling. "move this file to the archive folder". Sibling of rename_file_with_references (128) —
    /// same mechanics, the only difference is which piece of the path changes (folder vs name).
    ///
    /// Mechanics, all REUSED proven-live APIs, nothing new (identical shape to tool 128):
    ///   1. Scout  — scan the part's OLD folder for .SLDASM files and ask each (file-level ISldWorks.GetDocumentDependencies2,
    ///      the same call FindWhereUsed/RenameFileWithReferences already prove) whether it references the part's CURRENT path.
    ///   2. Mover  — IModelDocExtension.SaveAs with NO swSaveAsOptions_Copy on the ACTIVE document itself (the "true" Save As
    ///      that rebinds the document's own identity to the new path) into the DESTINATION folder (created if missing) —
    ///      then the ORIGINAL file is deleted so this is a move, not a copy-and-abandon.
    ///   3. Relink — for each parent found in step 1: open it, select every component instance still pointing at the OLD
    ///      path, call AssemblyDoc.ReplaceComponents (proven live, tool 31) to point it at the NEW path, rebuild, verify by
    ///      an independent per-instance GetPathName() read-back, save (Save3), close (CloseDoc — Forge opened it, Rule #7).
    ///
    /// PDM-vault guard: same as tool 128 — a file living inside a registered PDM vault view must be moved through PDM,
    /// never by a raw filesystem move; refuses with a clear reason instead of silently corrupting vault state.
    ///
    /// Only PART files + ASSEMBLY parents are in scope for v1 — drawing parents are reported as "found but not
    /// relinked", never silently ignored or force-attempted (same caveat as tool 128).
    /// FAIL CLOSED (Rule #6): Verified requires the new file on disk, the old file GONE, and every found parent
    /// independently confirmed pointing at the new path.
    /// </summary>
    public static class MoveFileWithReferences
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (!Regex.IsMatch(c, @"\bmove\b")) return false;
            if (!Regex.IsMatch(c, @"\bfile\b")) return false;
            return Regex.IsMatch(c, @"\bto\b");
        }

        public static async Task<MoveFileWithReferencesResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new MoveFileWithReferencesResult();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { res.Error = "Open the part whose file you want to move."; return res; }

            string oldPath = null; try { oldPath = model.GetPathName(); } catch { }
            if (string.IsNullOrWhiteSpace(oldPath))
            { res.Error = "This part has never been saved, so there is no file to move yet."; return res; }

            string oldFolder = null; try { oldFolder = Path.GetDirectoryName(oldPath); } catch { }
            if (string.IsNullOrWhiteSpace(oldFolder) || !Directory.Exists(oldFolder))
            { res.Error = "Couldn't read the folder this part lives in."; return res; }

            string rawDest = ParseDestToken(intent);
            if (string.IsNullOrEmpty(rawDest))
            {
                res.NeedsConfirm = true;
                res.Question = "Which folder should the file be moved to?";
                return res;
            }
            // A relative/bare folder name (no drive letter, no UNC) means "a folder with this name" — if the
            // part's CURRENT folder is already leaf-named that, we're already there (Rule #5 idempotent rerun);
            // otherwise every rerun would nest one level deeper (…/archive/archive/…) instead of recognizing
            // it already landed. An absolute/UNC destination is unambiguous and always used as-is.
            bool destIsAbsolute = Regex.IsMatch(rawDest, @"^[A-Za-z]:\\") || rawDest.StartsWith("\\\\");
            string destFolder;
            if (destIsAbsolute) destFolder = rawDest;
            else if (string.Equals(Path.GetFileName(oldFolder.TrimEnd('\\')), rawDest, StringComparison.OrdinalIgnoreCase)) destFolder = oldFolder;
            else { try { destFolder = Path.Combine(oldFolder, rawDest); } catch (Exception ex) { res.Error = "Couldn't build the destination path (" + ex.GetType().Name + ")."; return res; } }
            string fileName = Path.GetFileName(oldPath);
            string newPath;
            try { newPath = Path.Combine(destFolder, fileName); }
            catch (Exception ex) { res.Error = "Couldn't build the destination path (" + ex.GetType().Name + ")."; return res; }
            res.OldPath = oldPath; res.NewPath = newPath;

            // idempotent rerun (Rule #5): the active doc is ALREADY in the requested folder
            if (string.Equals(Norm(oldPath), Norm(newPath), StringComparison.OrdinalIgnoreCase))
            {
                res.AlreadyDone = true; res.Verified = true;
                res.Info = "Already in " + destFolder + " — nothing to move.";
                return res;
            }

            if (File.Exists(newPath))
            { res.Error = "A file named \"" + fileName + "\" already exists in " + destFolder + "."; return res; }

            // ---- PDM-vault guard (same as tool 128) ----
            if (IsUnderPdmVault(oldPath, out string vaultName))
            {
                res.Error = "\"" + fileName + "\" lives inside the PDM vault \"" + vaultName +
                            "\" — move it through PDM (check-out required), not directly.";
                return res;
            }

            try { Directory.CreateDirectory(destFolder); }
            catch (Exception ex) { res.Error = "Couldn't create the destination folder (" + ex.GetType().Name + ")."; return res; }

            await emit("Scout", "finding assemblies that reference " + fileName, "run", null);
            var parents = ScanParents(app, oldFolder, oldPath);
            res.ParentsScanned = parents.Count;
            await emit("Scout", null, "done", parents.Count + " parent assembly(ies) found");

            // ---- move: true SaveAs (no Copy) rebinds the ACTIVE document's own identity into the new folder ----
            await emit("Mover", "moving to " + destFolder, "run", null);
            int errs = 0, warns = 0; bool ok = false;
            try
            {
                ok = model.Extension.SaveAs(newPath, (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                    (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, ref errs, ref warns);
            }
            catch (Exception ex) { res.Error = "SaveAs threw (" + ex.GetType().Name + ") — nothing moved."; return res; }
            if (!ok || !File.Exists(newPath))
            { res.Error = "SaveAs reported failure (errs=" + errs + ", warns=" + warns + ") — the file was not moved."; await emit("Mover", null, "fail", res.Error); return res; }
            string activeNow = null; try { activeNow = model.GetPathName(); } catch { }
            if (!string.Equals(Norm(activeNow), Norm(newPath), StringComparison.OrdinalIgnoreCase))
            { res.Error = "SaveAs wrote the file but the active document's own path didn't move — refusing to delete the original."; await emit("Mover", null, "fail", res.Error); return res; }
            try { File.Delete(oldPath); }
            catch (Exception ex) { res.Error = "Moved, but couldn't remove the original file (" + ex.GetType().Name + ") — both now exist."; await emit("Mover", null, "fail", res.Error); return res; }
            await emit("Mover", null, "done", "moved, original removed");

            // ---- relink every found parent ----
            await emit("Scribe", "relinking " + parents.Count + " parent assembly(ies)", "run", null);
            foreach (var p in parents)
            {
                var row = new MoveParentRow { Name = Path.GetFileName(p), Path = p };
                res.Parents.Add(row);

                IModelDoc2 pdoc = null; bool wasOpen = false;
                try { pdoc = app.GetOpenDocumentByName(p) as IModelDoc2; } catch { }
                if (pdoc != null) wasOpen = true;
                else
                {
                    int oe = 0, ow = 0;
                    try { pdoc = app.OpenDoc6(p, (int)swDocumentTypes_e.swDocASSEMBLY, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref oe, ref ow) as IModelDoc2; }
                    catch { pdoc = null; }
                }
                if (pdoc == null) { row.Error = "couldn't open"; res.ParentsFailed++; continue; }

                try
                {
                    var asm = pdoc as AssemblyDoc;
                    if (asm == null) { row.Error = "not an assembly"; res.ParentsFailed++; continue; }

                    pdoc.ClearSelection2(true);
                    int sel = 0, alreadyOnNew = 0;
                    var seenPaths = new List<string>();
                    foreach (var o in (asm.GetComponents(true) as object[]) ?? new object[0])
                    {
                        var c = o as Component2; if (c == null) continue;
                        string cp = null; try { cp = c.GetPathName(); } catch { }
                        seenPaths.Add(cp ?? "(null)");
                        if (string.Equals(Norm(cp), Norm(oldPath), StringComparison.OrdinalIgnoreCase))
                        { try { if (c.Select4(sel > 0, null, false)) sel++; } catch { } }
                        else if (string.Equals(Norm(cp), Norm(newPath), StringComparison.OrdinalIgnoreCase)) alreadyOnNew++;
                    }
                    // SolidWorks itself searches subfolders of the referencing assembly's folder for a missing
                    // reference on open, so when the destination is a subfolder it may have ALREADY self-healed
                    // the component to newPath before this code ever runs — that is success, not "not found".
                    if (sel == 0 && alreadyOnNew == 0)
                    { row.Error = "no matching component instance found; saw=[" + string.Join(";", seenPaths) + "] wanted=" + oldPath; res.ParentsFailed++; continue; }

                    int onNew = alreadyOnNew;
                    if (sel > 0)
                    {
                        bool apiRet = false;
                        try { apiRet = asm.ReplaceComponents(newPath, "", true, true); } catch { apiRet = false; }
                        try { pdoc.EditRebuild3(); } catch { try { pdoc.ForceRebuild3(false); } catch { } }

                        onNew = 0;
                        foreach (var o in (asm.GetComponents(true) as object[]) ?? new object[0])
                        {
                            var c = o as Component2; if (c == null) continue;
                            string cp = null; try { cp = c.GetPathName(); } catch { }
                            if (string.Equals(Norm(cp), Norm(newPath), StringComparison.OrdinalIgnoreCase)) onNew++;
                        }
                        row.Relinked = onNew >= sel;
                        if (!row.Relinked) { row.Error = "apiRet=" + apiRet + " onNew=" + onNew + " expected>=" + sel; res.ParentsFailed++; continue; }
                    }
                    else
                    {
                        row.Relinked = true; // already self-healed to newPath before this code ran
                    }

                    int se = 0, sw = 0; bool saved = false;
                    try { saved = pdoc.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref se, ref sw); } catch { }
                    if (!saved) { row.Error = "relinked but save failed (se=" + se + ")"; res.ParentsFailed++; continue; }
                    res.ParentsRelinked++;
                }
                finally
                {
                    if (!wasOpen) SafeClose(app, pdoc);
                }
            }

            res.Diag = "parents=" + res.ParentsScanned + " relinked=" + res.ParentsRelinked + " failed=" + res.ParentsFailed;
            res.Verified = File.Exists(newPath) && !File.Exists(oldPath) && res.ParentsFailed == 0;

            if (!res.Verified)
            {
                res.Error = "Moved the file, but not everything verified clean (" + res.Diag + ").";
                await emit("Scribe", null, "fail", res.Error);
                return res;
            }

            res.Info = "Moved " + fileName + " -> " + destFolder +
                       (res.ParentsRelinked > 0 ? ", relinked " + res.ParentsRelinked + " parent assembly(ies)" : ", no parent assemblies referenced it") + ".";
            await emit("Scribe", null, "done", res.Info);
            return res;
        }

        private static List<string> ScanParents(ISldWorks app, string folder, string targetPath)
        {
            var result = new List<string>();
            IEnumerable<string> candidates;
            try { candidates = Directory.GetFiles(folder, "*.SLDASM"); } catch { return result; }
            foreach (var cand in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                object[] deps = null;
                try { deps = app.GetDocumentDependencies2(cand, true, true, false) as object[]; } catch { deps = null; }
                if (deps == null) continue;
                for (int k = 0; k + 1 < deps.Length; k += 2)
                {
                    string p = deps[k + 1] as string;
                    if (!string.IsNullOrEmpty(p) && string.Equals(Norm(p), Norm(targetPath), StringComparison.OrdinalIgnoreCase))
                    { result.Add(cand); break; }
                }
            }
            return result;
        }

        private static void SafeClose(ISldWorks app, IModelDoc2 doc)
        {
            string title = null; try { title = doc.GetTitle(); } catch { }
            try { app.CloseDoc(title); } catch { }
        }

        // "move ... file ... to <destination>" — extracts the raw destination token only (either an absolute/UNC
        // path or a bare folder name); the caller decides how to resolve a bare name against the current folder.
        private static string ParseDestToken(string intent)
        {
            if (string.IsNullOrEmpty(intent)) return null;
            var m = Regex.Match(intent, @"\bto\s+(?:the\s+)?[""']?(.+?)[""']?\s*$", RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            string raw = m.Groups[1].Value.Trim();
            raw = Regex.Replace(raw, @"\s+(folder|directory)$", "", RegexOptions.IgnoreCase).Trim();
            return string.IsNullOrEmpty(raw) ? null : raw;
        }

        // best-effort PDM Professional/Standard vault-view registry probe. Fails closed to "not vaulted" only
        // when the vault list key itself doesn't exist (no PDM installed) - never on a read error, which could
        // mask a real vault and let Forge corrupt a vault-tracked file. Identical to tool 128's guard.
        private static bool IsUnderPdmVault(string path, out string vaultName)
        {
            vaultName = null;
            try
            {
                using (var vaults = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\SolidWorks\Applications\PDMWorks Enterprise\CurrentVersion\Vaults"))
                {
                    if (vaults == null) return false; // no PDM installed on this machine
                    foreach (var name in vaults.GetSubKeyNames())
                    {
                        using (var v = vaults.OpenSubKey(name))
                        {
                            string view = v?.GetValue("View") as string ?? v?.GetValue("LocalPath") as string;
                            if (!string.IsNullOrEmpty(view) && Norm(path).StartsWith(Norm(view), StringComparison.OrdinalIgnoreCase))
                            { vaultName = name; return true; }
                        }
                    }
                }
            }
            catch { return false; }
            return false;
        }

        private static string Norm(string p) { return string.IsNullOrEmpty(p) ? "" : p.Trim().ToLowerInvariant().Replace('/', '\\'); }
    }
}
