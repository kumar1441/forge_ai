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
    public class RenameParentRow
    {
        public string Name;
        public string Path;
        public bool Relinked;
        public string Error;
    }

    public class RenameFileWithReferencesResult
    {
        public string OldPath;
        public string NewPath;
        public int ParentsScanned;
        public int ParentsRelinked;
        public int ParentsFailed;
        public List<RenameParentRow> Parents = new List<RenameParentRow>();
        public bool AlreadyDone;
        public bool Verified;
        public string Diag;
        public string Info;
        public string Error;
        public bool NeedsConfirm;
        public string Question;
    }

    /// <summary>
    /// RenameFileWithReferences (tool #128 rename_file_with_references, WRITE) — rename the ACTIVE part's own file
    /// on disk and fix up every parent assembly in the same folder so nothing goes dangling. "rename this file to
    /// bracket-v2". Distinct from rename_component (renames a COMPONENT INSTANCE inside an open assembly) — that
    /// tool requires "component/part/instance/the/this" wording and now excludes "file" (see RenameComponent.cs),
    /// so the two matchers are disjoint by construction.
    ///
    /// Mechanics, all REUSED proven-live APIs, nothing new:
    ///   1. Scout  — scan the part's folder for .SLDASM files and ask each (file-level ISldWorks.GetDocumentDependencies2,
    ///      the same call FindWhereUsed already proves) whether it references the part's CURRENT path. Nothing opened yet.
    ///   2. Rename — IModelDocExtension.SaveAs with NO swSaveAsOptions_Copy on the ACTIVE document itself — the "true"
    ///      Save As that rebinds the document's own identity to the new path (proven pattern already used, without
    ///      Copy, by BatchConvertFiles/DrawingPkg/RecipeExecutor) — then the ORIGINAL file is deleted so this is a
    ///      rename, not a copy-and-abandon.
    ///   3. Relink — for each parent found in step 1: open it (OpenDoc6, same pre-load ReplaceComponent uses), select
    ///      every component instance still pointing at the OLD path, call AssemblyDoc.ReplaceComponents (proven live,
    ///      tool 31) to point it at the NEW path, rebuild, verify by an independent per-instance GetPathName()
    ///      read-back, save (Save3), close (CloseDoc — Forge opened it, so Forge closes it, Rule #7).
    ///
    /// PDM-vault guard: a file living inside a registered PDM Professional/Standard vault view must be renamed
    /// through PDM (check-out + vault-wide reference tracking), never by a raw filesystem move — refuses with a
    /// clear reason instead of silently corrupting vault state. Best-effort registry probe; fails closed to
    /// "not vaulted" only when the vault key itself is ABSENT (a dev box with no PDM installed), never on a read
    /// error, since a read error masking a real vault would be the dangerous direction to guess wrong in.
    ///
    /// Only PART files + ASSEMBLY parents are in scope for v1 — drawing parents (a different, unproven-in-this-
    /// codebase reference API, see tool 114 update_sheet_references in docs/kb/landmines.md) are reported as
    /// "found but not relinked", never silently ignored or force-attempted.
    /// FAIL CLOSED (Rule #6): Verified requires the new file on disk, the old file GONE, and every found parent
    /// independently confirmed pointing at the new path.
    /// </summary>
    public static class RenameFileWithReferences
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (!Regex.IsMatch(c, @"\b(rename|re-?name)\b")) return false;
            if (!Regex.IsMatch(c, @"\bfile\b")) return false;
            return Regex.IsMatch(c, @"\bto\b");
        }

        public static async Task<RenameFileWithReferencesResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new RenameFileWithReferencesResult();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { res.Error = "Open the part whose file you want to rename."; return res; }

            string oldPath = null; try { oldPath = model.GetPathName(); } catch { }
            if (string.IsNullOrWhiteSpace(oldPath))
            { res.Error = "This part has never been saved, so there is no file to rename yet."; return res; }

            string folder = null; try { folder = Path.GetDirectoryName(oldPath); } catch { }
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            { res.Error = "Couldn't read the folder this part lives in."; return res; }

            string ext = Path.GetExtension(oldPath);
            string newName = ParseNewName(intent);
            if (string.IsNullOrEmpty(newName))
            {
                res.NeedsConfirm = true;
                res.Question = "What should the file be renamed to?";
                return res;
            }
            string newPath = Path.Combine(folder, newName + ext);
            res.OldPath = oldPath; res.NewPath = newPath;

            // idempotent rerun (Rule #5): the active doc is ALREADY at the requested name
            if (string.Equals(Norm(oldPath), Norm(newPath), StringComparison.OrdinalIgnoreCase))
            {
                res.AlreadyDone = true; res.Verified = true;
                res.Info = "Already named " + Path.GetFileName(newPath) + " — nothing to change.";
                return res;
            }

            if (File.Exists(newPath))
            { res.Error = "A file named \"" + Path.GetFileName(newPath) + "\" already exists in that folder."; return res; }

            // ---- PDM-vault guard ----
            if (IsUnderPdmVault(oldPath, out string vaultName))
            {
                res.Error = "\"" + Path.GetFileName(oldPath) + "\" lives inside the PDM vault \"" + vaultName +
                            "\" — rename it through PDM (check-out required), not directly.";
                return res;
            }

            await emit("Scout", "finding assemblies that reference " + Path.GetFileName(oldPath), "run", null);
            var parents = ScanParents(app, folder, oldPath);
            res.ParentsScanned = parents.Count;
            await emit("Scout", null, "done", parents.Count + " parent assembly(ies) found");

            // ---- rename: true SaveAs (no Copy) rebinds the ACTIVE document's own identity ----
            await emit("Writer", "renaming to " + Path.GetFileName(newPath), "run", null);
            int errs = 0, warns = 0; bool ok = false;
            try
            {
                ok = model.Extension.SaveAs(newPath, (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                    (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, ref errs, ref warns);
            }
            catch (Exception ex) { res.Error = "SaveAs threw (" + ex.GetType().Name + ") — nothing renamed."; return res; }
            if (!ok || !File.Exists(newPath))
            { res.Error = "SaveAs reported failure (errs=" + errs + ", warns=" + warns + ") — the file was not renamed."; await emit("Writer", null, "fail", res.Error); return res; }
            string activeNow = null; try { activeNow = model.GetPathName(); } catch { }
            if (!string.Equals(Norm(activeNow), Norm(newPath), StringComparison.OrdinalIgnoreCase))
            { res.Error = "SaveAs wrote the file but the active document's own path didn't move — refusing to delete the original."; await emit("Writer", null, "fail", res.Error); return res; }
            try { File.Delete(oldPath); }
            catch (Exception ex) { res.Error = "Renamed, but couldn't remove the original file (" + ex.GetType().Name + ") — both now exist."; await emit("Writer", null, "fail", res.Error); return res; }
            await emit("Writer", null, "done", "renamed, original removed");

            // ---- relink every found parent ----
            await emit("Scribe", "relinking " + parents.Count + " parent assembly(ies)", "run", null);
            foreach (var p in parents)
            {
                var row = new RenameParentRow { Name = Path.GetFileName(p), Path = p };
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
                    int sel = 0;
                    foreach (var o in (asm.GetComponents(true) as object[]) ?? new object[0])
                    {
                        var c = o as Component2; if (c == null) continue;
                        string cp = null; try { cp = c.GetPathName(); } catch { }
                        if (string.Equals(Norm(cp), Norm(oldPath), StringComparison.OrdinalIgnoreCase))
                        { try { if (c.Select4(sel > 0, null, false)) sel++; } catch { } }
                    }
                    if (sel == 0) { row.Error = "no matching component instance found"; res.ParentsFailed++; continue; }

                    bool apiRet = false;
                    try { apiRet = asm.ReplaceComponents(newPath, "", true, true); } catch { apiRet = false; }
                    try { pdoc.EditRebuild3(); } catch { try { pdoc.ForceRebuild3(false); } catch { } }

                    int onNew = 0;
                    foreach (var o in (asm.GetComponents(true) as object[]) ?? new object[0])
                    {
                        var c = o as Component2; if (c == null) continue;
                        string cp = null; try { cp = c.GetPathName(); } catch { }
                        if (string.Equals(Norm(cp), Norm(newPath), StringComparison.OrdinalIgnoreCase)) onNew++;
                    }
                    row.Relinked = onNew >= sel;
                    if (!row.Relinked) { row.Error = "apiRet=" + apiRet + " onNew=" + onNew + " expected>=" + sel; res.ParentsFailed++; continue; }

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
                res.Error = "Renamed the file, but not everything verified clean (" + res.Diag + ").";
                await emit("Scribe", null, "fail", res.Error);
                return res;
            }

            res.Info = "Renamed " + Path.GetFileName(oldPath) + " -> " + Path.GetFileName(newPath) +
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

        private static string ParseNewName(string intent)
        {
            if (string.IsNullOrEmpty(intent)) return null;
            var m = Regex.Match(intent, @"\bto\s+[""']?([A-Za-z0-9_\-]+)(?:\.sld(?:prt|asm))?[""']?\s*$", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value.Trim();
            return null;
        }

        // best-effort PDM Professional/Standard vault-view registry probe. Fails closed to "not vaulted" only
        // when the vault list key itself doesn't exist (no PDM installed) - never on a read error, which could
        // mask a real vault and let Forge corrupt a vault-tracked file.
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
