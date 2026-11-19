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
    public class RepairMateRow
    {
        public string MateName;
        public string MateType;
        public string MissingPath;
        public string ReplacementPath;
        public bool Repaired;
        public string Error;
    }

    public class RepairMateResult
    {
        public int MatesChecked = -1;
        public int BrokenFound;
        public int Repaired;
        public bool AlreadyDone;
        public bool Verified;
        public List<RepairMateRow> Rows = new List<RepairMateRow>();
        public string Diag;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// RepairMate (tool #62 repair_mate, WRITE) — for the ACTIVE assembly, find mates whose referenced component's
    /// file no longer resolves on disk (the classic "a bolt got renamed/moved and its mate went dangling" state) and
    /// re-attach them to a RESOLVED REPLACEMENT file, reporting per-mate before/after health. "repair the broken
    /// mate", "fix the dangling mate on the bracket", "reattach the mate to the new part".
    ///
    /// Distinct from repair_missing_references (tool 132, WRITE): 132 operates at the COMPONENT level and only
    /// trusts an EXACT filename match found elsewhere (never guesses a differently-named file). This tool operates
    /// at the MATE level and resolves a differently-named replacement by fuzzy prefix match against the missing
    /// file's base name — the real-world case where the replacement part was saved under a new name, not just moved.
    /// The two matchers are naturally disjoint: 132 requires a reference/component/file noun, this one requires
    /// "mate" explicitly, so "repair the broken mate" never reaches 132's regex.
    ///
    /// Detection (proven-live signal, not IFeature.GetErrorCode2 — this build does not surface a per-mate error code
    /// for a missing-file reference; File.Exists on the stored path is the one signal that survives the auto-suppress
    /// this build applies to components whose file can't be found, same finding RepairMissingReferences.cs already
    /// documents): walk MateGroup -> Mate2 sub-features (IFeature.GetSpecificFeature2() as Mate2), read each mate's
    /// entities via Mate2.MateEntity(i).ReferenceComponent, and flag the mate BROKEN if any referenced component's
    /// Component2.GetPathName() does not exist on disk.
    ///
    /// Repair: same proven `AssemblyDoc.ReplaceComponents(path, cfg, true, true)` swap tools 31/128/129/132 already
    /// use, followed by the same select+EditUnsuppress2 step 132 uses (components auto-suppressed purely because
    /// their file was missing do not un-suppress on their own when the reference resolves — proven by 132).
    ///
    /// FAIL CLOSED: Verified requires an INDEPENDENT re-walk of the same named mates to confirm every referenced
    /// component's file now exists — not just that ReplaceComponents returned true. IDEMPOTENT (Rule #5): a rerun
    /// with nothing broken reports AlreadyDone. Forge never saves; one Ctrl+Z (per swap) restores.
    /// </summary>
    public static class RepairMate
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (!Regex.IsMatch(c, @"\b(repair|fix|reattach|re-?attach|reconnect|re-?point)\b")) return false;
            return Regex.IsMatch(c, @"\bmates?\b");
        }

        public static async Task<RepairMateResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new RepairMateResult();
            var asm = model as AssemblyDoc;
            if (asm == null) { res.Error = "Open the assembly with the broken mate you want to repair."; return res; }

            string rootPath = null; try { rootPath = model.GetPathName(); } catch { }
            if (string.IsNullOrWhiteSpace(rootPath))
            { res.Error = "This assembly has never been saved, so there is no folder to search from."; return res; }
            string rootFolder = null; try { rootFolder = Path.GetDirectoryName(rootPath); } catch { }
            if (string.IsNullOrWhiteSpace(rootFolder) || !Directory.Exists(rootFolder))
            { res.Error = "Couldn't read the folder this assembly lives in."; return res; }

            var searchFolders = new List<string> { rootFolder };
            try { foreach (var sub in Directory.GetDirectories(rootFolder)) if (!searchFolders.Contains(sub, StringComparer.OrdinalIgnoreCase)) searchFolders.Add(sub); } catch { }

            await emit("Scout", "walking mates for dangling component references", "run", null);
            var broken = WalkBrokenMates(model);
            res.MatesChecked = broken.checkedCount;
            res.BrokenFound = broken.rows.Count;
            res.Rows = broken.rows;
            await emit("Scout", null, "done", res.MatesChecked + " mate(s) checked, " + res.BrokenFound + " broken");

            if (res.Rows.Count == 0)
            {
                res.AlreadyDone = true; res.Verified = true;
                res.Info = "No broken mates found — nothing to repair.";
                return res;
            }

            await emit("Scribe", "resolving replacement file(s) and re-attaching", "run", null);
            var byMissingPath = res.Rows.GroupBy(r => r.MissingPath, StringComparer.OrdinalIgnoreCase);
            foreach (var grp in byMissingPath)
            {
                string missingPath = grp.Key;
                string missingBase = StripSuffix(Path.GetFileNameWithoutExtension(missingPath));
                string candidate = FindReplacement(searchFolders, missingPath, missingBase, out string ambiguity);
                if (candidate == null)
                {
                    foreach (var row in grp) row.Error = ambiguity ?? ("no resolvable replacement found for " + Path.GetFileName(missingPath));
                    continue;
                }

                // select every Component2 instance currently pointing at the missing path
                model.ClearSelection2(true);
                int sel = 0;
                var targets = new List<Component2>();
                foreach (var o in (asm.GetComponents(true) as object[]) ?? new object[0])
                {
                    var c = o as Component2; if (c == null) continue;
                    string cp = null; try { cp = c.GetPathName(); } catch { }
                    if (string.Equals(Norm(cp), Norm(missingPath), StringComparison.OrdinalIgnoreCase))
                    { try { if (c.Select4(sel > 0, null, false)) { sel++; targets.Add(c); } } catch { } }
                }
                if (sel == 0) { foreach (var row in grp) row.Error = "component(s) no longer selectable"; continue; }

                bool apiRet = false;
                try { apiRet = asm.ReplaceComponents(candidate, "", true, true); } catch { apiRet = false; }

                try
                {
                    model.ClearSelection2(true);
                    int usel = 0;
                    foreach (var c in targets) { try { if (c.Select4(usel > 0, null, false)) usel++; } catch { } }
                    if (usel > 0) { try { model.EditUnsuppress2(); } catch { } }
                }
                catch { }
                try { model.ForceRebuild3(false); } catch { }

                foreach (var row in grp) { row.ReplacementPath = candidate; row.Repaired = apiRet; }
            }

            // ---- Sentinel: FAIL CLOSED — independent re-walk of the SAME named mates ----
            await emit("Sentinel", "verifying the repair took", "run", null);
            var after = WalkBrokenMates(model);
            var stillBrokenNames = new HashSet<string>(after.rows.Select(r => r.MateName), StringComparer.OrdinalIgnoreCase);
            foreach (var row in res.Rows)
            {
                if (row.Error != null) continue;
                if (stillBrokenNames.Contains(row.MateName)) { row.Repaired = false; row.Error = row.Error ?? "still broken after repair"; }
                else res.Repaired++;
            }

            res.Diag = "broken=" + res.BrokenFound + " repaired=" + res.Repaired + " stillBroken=" + after.rows.Count;
            res.Verified = res.Repaired == res.BrokenFound && after.rows.Count == 0;
            await emit("Sentinel", null, res.Verified ? "done" : "fail",
                res.Verified ? "repaired " + res.Repaired + " mate(s)" : res.Diag);

            if (!res.Verified)
            { res.Error = "Repaired some, but not everything verified clean (" + res.Diag + ")."; return res; }

            res.Info = "Repaired " + res.Repaired + " broken mate(s). One Ctrl+Z (per swap) restores; Forge didn't save.";
            return res;
        }

        private class BrokenScan { public List<RepairMateRow> rows = new List<RepairMateRow>(); public int checkedCount; }

        private static BrokenScan WalkBrokenMates(IModelDoc2 model)
        {
            var scan = new BrokenScan();
            var mf = model.FirstFeature() as Feature;
            while (mf != null)
            {
                string mtn = null; try { mtn = mf.GetTypeName2(); } catch { }
                if (mtn == "MateGroup")
                {
                    var ms = mf.GetFirstSubFeature() as Feature;
                    while (ms != null)
                    {
                        try
                        {
                            var mate = ms.GetSpecificFeature2() as Mate2;
                            if (mate != null)
                            {
                                scan.checkedCount++;
                                string missing = null;
                                int mn = 0; try { mn = mate.GetMateEntityCount(); } catch { }
                                for (int i = 0; i < mn; i++)
                                {
                                    MateEntity2 me = null; try { me = mate.MateEntity(i) as MateEntity2; } catch { }
                                    var mc = me == null ? null : (me.ReferenceComponent as Component2);
                                    if (mc == null) continue;
                                    string cp = null; try { cp = mc.GetPathName(); } catch { }
                                    if (string.IsNullOrEmpty(cp)) continue;
                                    bool exists = false; try { exists = File.Exists(cp); } catch { }
                                    if (!exists) { missing = cp; break; }
                                }
                                if (missing != null)
                                {
                                    scan.rows.Add(new RepairMateRow
                                    {
                                        MateName = ms.Name,
                                        MateType = NounOf(mate.Type),
                                        MissingPath = missing
                                    });
                                }
                            }
                        }
                        catch { }
                        ms = ms.GetNextSubFeature() as Feature;
                    }
                }
                mf = mf.GetNextFeature() as Feature;
            }
            return scan;
        }

        // strip common "this is a copy/renamed variant" suffixes so a fuzzy match survives real-world naming: trailing
        // " - Copy", "(1)", "-MOVED", "-OLD", "_broken", or a bare trailing run of digits.
        private static string StripSuffix(string baseName)
        {
            if (string.IsNullOrEmpty(baseName)) return baseName;
            string s = baseName;
            s = Regex.Replace(s, @"\s*-\s*(copy|moved|old|backup)\s*$", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\s*\(\d+\)\s*$", "");
            s = Regex.Replace(s, @"[_-]?\d+\s*$", "");
            return s.Trim();
        }

        // finds a resolvable file whose (suffix-stripped) base name shares a prefix with the missing file's
        // (suffix-stripped) base name; only auto-applies when exactly one such candidate exists — an ambiguous or
        // absent match is reported, never guessed.
        private static string FindReplacement(List<string> searchFolders, string missingPath, string missingBaseStripped, out string ambiguity)
        {
            ambiguity = null;
            string ext = Path.GetExtension(missingPath);
            var found = new List<string>();
            foreach (var folder in searchFolders)
            {
                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(folder, "*" + ext); } catch { continue; }
                foreach (var f in files)
                {
                    if (string.Equals(Norm(f), Norm(missingPath), StringComparison.OrdinalIgnoreCase)) continue; // the missing file itself
                    string cand = StripSuffix(Path.GetFileNameWithoutExtension(f));
                    if (string.IsNullOrEmpty(cand) || string.IsNullOrEmpty(missingBaseStripped)) continue;
                    if (cand.StartsWith(missingBaseStripped, StringComparison.OrdinalIgnoreCase) ||
                        missingBaseStripped.StartsWith(cand, StringComparison.OrdinalIgnoreCase))
                        found.Add(f);
                }
            }
            var distinct = found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (distinct.Count == 1) return distinct[0];
            if (distinct.Count > 1) ambiguity = "ambiguous — " + distinct.Count + " candidate replacements found: [" + string.Join("; ", distinct.Select(Path.GetFileName)) + "]";
            return null;
        }

        private static string Norm(string p) { return string.IsNullOrEmpty(p) ? "" : p.Trim().ToLowerInvariant().Replace('/', '\\'); }

        private static string NounOf(int type)
        {
            switch ((swMateType_e)type)
            {
                case swMateType_e.swMateCOINCIDENT: return "coincident";
                case swMateType_e.swMateCONCENTRIC: return "concentric";
                case swMateType_e.swMatePERPENDICULAR: return "perpendicular";
                case swMateType_e.swMatePARALLEL: return "parallel";
                case swMateType_e.swMateTANGENT: return "tangent";
                case swMateType_e.swMateDISTANCE: return "distance";
                case swMateType_e.swMateANGLE: return "angle";
                case swMateType_e.swMateWIDTH: return "width";
                default: return "type" + type;
            }
        }
    }
}
