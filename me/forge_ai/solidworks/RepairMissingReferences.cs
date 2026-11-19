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
    public class MissingRefRow
    {
        public string StoredPath;
        public int InstanceCount;
        public string FoundPath;
        public bool Repaired;
        public string Error;
    }

    public class RepairMissingReferencesResult
    {
        public int TotalComponents;
        public int MissingFound;
        public int Repaired;
        public int StillMissing;
        public List<MissingRefRow> Rows = new List<MissingRefRow>();
        public bool AlreadyDone;
        public bool Verified;
        public string Diag;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// RepairMissingReferences (tool #132 repair_missing_references, WRITE) — for the ACTIVE assembly, find every
    /// component whose stored file reference no longer resolves on disk ("cannot find document" the classic SW
    /// dialog) and re-point it at a resolved path, same proven `AssemblyDoc.ReplaceComponents` swap 128/129 use.
    /// "repair the missing references", "fix broken file references using files from C:\Library".
    ///
    /// Detection: on this build, opening an assembly with a genuinely absent referenced file auto-SUPPRESSES that
    /// component rather than leaving it "unresolved but present" — so suppression state alone is not a reliable
    /// missing-file signal (a real user-suppressed component looks identical). `Component2.GetPathName()` still
    /// returns the STORED path either way, so `File.Exists` on that stored path is the one signal that survives
    /// both cases and is exactly what get_file_references (tool 130) already uses at the flat-dependency level —
    /// here it is checked per COMPONENT INSTANCE because ReplaceComponents needs actual Component2 selections.
    ///
    /// Search: the missing file's ORIGINAL filename is looked up in, in order — an explicit folder named in the
    /// request ("...using files from C:\Library"), the assembly's own folder, and the assembly's own folder's
    /// immediate subfolders (SolidWorks itself already searches those on open — see tool 129 — so checking them
    /// here costs nothing and covers the same case if the auto-heal didn't fire for some component).
    ///
    /// Every component still auto-suppressed purely because its file was missing is un-suppressed after a
    /// successful repair (Rule #6 honesty — "repaired but still hidden" would look like nothing changed).
    /// FAIL CLOSED: Verified requires every found-missing component to be independently confirmed re-pointed at
    /// its resolved path. IDEMPOTENT (Rule #5): a rerun with nothing missing reports AlreadyDone.
    /// </summary>
    public static class RepairMissingReferences
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (!Regex.IsMatch(c, @"\b(repair|fix|resolve|relink|reconnect|re-?point|reattach)\b")) return false;
            if (!Regex.IsMatch(c, @"\b(missing|broken|unresolved|can(no)?t\s*find|cannot\s*find)\b")) return false;
            return Regex.IsMatch(c, @"\b(reference|references|refs|link|links|component|components|document|documents|file|files)\b");
        }

        public static async Task<RepairMissingReferencesResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new RepairMissingReferencesResult();
            var asm = model as AssemblyDoc;
            if (asm == null) { res.Error = "Open the assembly with missing references you want to repair."; return res; }

            string rootPath = null; try { rootPath = model.GetPathName(); } catch { }
            if (string.IsNullOrWhiteSpace(rootPath))
            { res.Error = "This assembly has never been saved, so there is no folder to search from."; return res; }
            string rootFolder = null; try { rootFolder = Path.GetDirectoryName(rootPath); } catch { }
            if (string.IsNullOrWhiteSpace(rootFolder) || !Directory.Exists(rootFolder))
            { res.Error = "Couldn't read the folder this assembly lives in."; return res; }

            var searchFolders = new List<string>();
            string explicitFolder = ParseSearchFolder(intent);
            if (!string.IsNullOrEmpty(explicitFolder) && Directory.Exists(explicitFolder)) searchFolders.Add(explicitFolder);
            searchFolders.Add(rootFolder);
            try
            {
                foreach (var sub in Directory.GetDirectories(rootFolder))
                    if (!searchFolders.Contains(sub, StringComparer.OrdinalIgnoreCase)) searchFolders.Add(sub);
            }
            catch { }

            await emit("Scout", "checking every component's stored file against disk", "run", null);
            var groups = new Dictionary<string, List<Component2>>(StringComparer.OrdinalIgnoreCase);
            int total = 0;
            foreach (var o in (asm.GetComponents(true) as object[]) ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                total++;
                string cp = null; try { cp = c.GetPathName(); } catch { }
                if (string.IsNullOrEmpty(cp)) continue;
                bool exists = false; try { exists = File.Exists(cp); } catch { }
                if (exists) continue;
                string key = Norm(cp);
                if (!groups.TryGetValue(key, out var list)) { list = new List<Component2>(); groups[key] = list; }
                list.Add(c);
            }
            res.TotalComponents = total;
            res.MissingFound = groups.Sum(g => g.Value.Count);
            await emit("Scout", null, "done", groups.Count + " distinct missing file(s), " + res.MissingFound + " component instance(s)");

            if (groups.Count == 0)
            {
                res.AlreadyDone = true; res.Verified = true;
                res.Info = "No missing references found — nothing to repair.";
                return res;
            }

            await emit("Scribe", "searching " + searchFolders.Count + " folder(s) for the missing file(s)", "run", null);
            foreach (var kv in groups)
            {
                string storedPath = kv.Value[0].GetPathName();
                var row = new MissingRefRow { StoredPath = storedPath, InstanceCount = kv.Value.Count };
                res.Rows.Add(row);

                string fileName = Path.GetFileName(storedPath);
                string candidate = null;
                foreach (var folder in searchFolders)
                {
                    string tryPath = null; try { tryPath = Path.Combine(folder, fileName); } catch { continue; }
                    if (File.Exists(tryPath)) { candidate = tryPath; break; }
                }
                if (candidate == null)
                { row.Error = "no candidate found for " + fileName + " (searched " + searchFolders.Count + " folder(s))"; res.StillMissing += kv.Value.Count; continue; }
                row.FoundPath = candidate;

                try
                {
                    model.ClearSelection2(true);
                    int sel = 0;
                    foreach (var c in kv.Value) { try { if (c.Select4(sel > 0, null, false)) sel++; } catch { } }
                    if (sel == 0) { row.Error = "components no longer selectable"; res.StillMissing += kv.Value.Count; continue; }

                    bool apiRet = false;
                    try { apiRet = asm.ReplaceComponents(candidate, "", true, true); } catch { apiRet = false; }

                    // un-suppress BEFORE rebuilding/re-checking: these were auto-suppressed purely because the file
                    // was unresolvable, and a suppressed component's GetPathName() does not refresh to reflect the
                    // just-swapped reference until SW actually attempts to load it — which a rebuild skips entirely
                    // for anything still suppressed. The SAME selection ReplaceComponents just used is still valid
                    // (nothing has rebuilt yet to go stale).
                    try
                    {
                        model.ClearSelection2(true);
                        int usel = 0;
                        foreach (var c in kv.Value) { try { if (c.Select4(usel > 0, null, false)) usel++; } catch { } }
                        if (usel > 0) { try { model.EditUnsuppress2(); } catch { } }
                    }
                    catch { }

                    try { model.ForceRebuild3(false); } catch { }

                    // re-enumerate FRESH components — the Component2 refs captured in kv.Value can go stale across
                    // ReplaceComponents/unsuppress/rebuild (the same lesson 128/129 already applied), so identity
                    // comparisons on the old objects are unreliable; match by CURRENT path instead.
                    var freshOnNew = new List<Component2>();
                    var freshSeen = new List<string>();
                    foreach (var o in (asm.GetComponents(true) as object[]) ?? new object[0])
                    {
                        var c = o as Component2; if (c == null) continue;
                        string cp = null; try { cp = c.GetPathName(); } catch { }
                        freshSeen.Add(cp ?? "(null)");
                        if (string.Equals(Norm(cp), Norm(candidate), StringComparison.OrdinalIgnoreCase)) freshOnNew.Add(c);
                    }
                    if (freshOnNew.Count < sel)
                    { row.Error = "apiRet=" + apiRet + " sel=" + sel + " onNew=" + freshOnNew.Count + " expected>=" + sel + " candidate=" + candidate + " seen=[" + string.Join(";", freshSeen) + "]"; res.StillMissing += kv.Value.Count; continue; }

                    row.Repaired = true;
                    res.Repaired += kv.Value.Count;
                }
                catch (Exception ex) { row.Error = "threw (" + ex.GetType().Name + ")"; res.StillMissing += kv.Value.Count; }
            }

            int se = 0, sw = 0; bool saved = false;
            try { saved = model.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref se, ref sw); } catch { }

            res.Diag = "missing=" + res.MissingFound + " repaired=" + res.Repaired + " stillMissing=" + res.StillMissing + " saved=" + saved;
            res.Verified = res.StillMissing == 0 && res.Repaired == res.MissingFound && saved;
            await emit("Scribe", null, res.Verified ? "done" : "fail",
                res.Verified ? "repaired " + res.Repaired + " component instance(s) across " + groups.Count + " missing file(s)" : res.Diag);

            if (!res.Verified)
            { res.Error = "Repaired some, but not everything verified clean (" + res.Diag + ")."; return res; }

            res.Info = "Repaired " + res.Repaired + " component instance(s) across " + groups.Count + " missing file(s).";
            return res;
        }

        // an EXPLICIT absolute/UNC folder named in the request ("...using files from C:\Library"); a bare/relative
        // name is deliberately NOT supported here (unlike move_file_with_references) since a wrong guess would
        // silently relink to the wrong part — only an unambiguous path is trusted, otherwise fall back to the
        // assembly's own folder + immediate subfolders.
        private static string ParseSearchFolder(string intent)
        {
            if (string.IsNullOrEmpty(intent)) return null;
            var m = Regex.Match(intent, @"\b(?:from|in)\s+(?:the\s+)?[""']?([A-Za-z]:\\[^""']+|\\\\[^""']+)[""']?\s*$", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }

        private static string Norm(string p) { return string.IsNullOrEmpty(p) ? "" : p.Trim().ToLowerInvariant().Replace('/', '\\'); }
    }
}
