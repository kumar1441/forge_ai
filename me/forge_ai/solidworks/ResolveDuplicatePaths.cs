using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public class ResolveDuplicatePathsGroup
    {
        public string LeafName;
        public string CanonicalPath;      // most-recently-modified file on disk among the group
        public DateTime CanonicalModifiedUtc;
        public List<string> DuplicatePaths = new List<string>();   // the OTHER (older / stale) path(s)
        public int ComponentCount;        // total instances across every path in this leaf-name group
    }

    public class ResolveDuplicatePathsResult
    {
        public bool Success;
        public int TotalComponents;
        public int GroupCount;   // leaf-filename groups that resolve to MORE THAN ONE distinct full path
        public List<ResolveDuplicatePathsGroup> Groups = new List<ResolveDuplicatePathsGroup>();
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 241 — resolve_duplicate_paths (READ, assembly). "Same part inserted from two different paths (network
    /// + local copy): detect, pick canonical, report — batch ops double-count these silently." Distinct from the
    /// already-shipped `find_duplicate_components` (tool 136), which groups by EXACT path match — the normal,
    /// correct "same file placed N times" case. This tool instead groups by LEAF FILENAME and flags a group only
    /// when it resolves to MORE THAN ONE DISTINCT full path — the real "looks like the same part, actually two
    /// different files" trap 136 cannot see (136 would just report each path separately, unique, no warning).
    ///
    /// **Live-probed 2026-08-01, not theorised — resolves a gap flagged unresolved across 6+ prior sessions:**
    /// `ISldWorks.OpenDoc6` on a second path sharing an already-open document's leaf filename returns null with
    /// the real, named error `swFileWithSameTitleAlreadyOpen` (65536) — SolidWorks itself cannot have two open
    /// documents with the same title at once, confirmed live (this is why the fixture was hard to build: a naive
    /// approach that opens both files collides exactly like this handler must never do). Detection therefore
    /// NEVER opens either file in SolidWorks — it reads `Component2.GetPathName()` (already resolved, no open
    /// needed) plus OS-level `File.Exists`/`GetLastWriteTimeUtc` only, the same safe convention `recover_autosave`
    /// (253) already established for exactly this reason. "Pick canonical" = the most-recently-modified file on
    /// disk (the likely actively-maintained copy); the other path(s) are reported as the suspect duplicate(s).
    /// Pure report — this tool does not repoint components (that is `repair_mate`/`repair_missing_references`'s
    /// job on an explicit ask); v1 scope is honest detection + report only.
    /// </summary>
    public static class ResolveDuplicatePaths
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            return Regex.IsMatch(c, @"\b(duplicate|duplicates|same|multiple|two|different)\b")
                   && Regex.IsMatch(c, @"\b(path|paths|location|locations|folder|folders|network)\b");
        }

        public static async Task<ResolveDuplicatePathsResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new ResolveDuplicatePathsResult();
            var asm = model as AssemblyDoc;
            if (asm == null) { res.Error = "Open an assembly (.SLDASM) to resolve duplicate paths."; return res; }

            await emit("Scout", "grouping components by leaf filename, watching for the same name resolving to different folders", "run", null);

            var byLeaf = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var o in (asm.GetComponents(false) as object[]) ?? new object[0])
                {
                    var c = o as Component2; if (c == null) continue;
                    res.TotalComponents++;
                    string p = null; try { p = c.GetPathName(); } catch { }
                    if (string.IsNullOrEmpty(p)) continue;
                    string leaf = null; try { leaf = Path.GetFileName(p); } catch { }
                    if (string.IsNullOrEmpty(leaf)) continue;
                    if (!byLeaf.TryGetValue(leaf, out var list)) { list = new List<string>(); byLeaf[leaf] = list; }
                    list.Add(p);
                }
            }
            catch (Exception ex) { res.Error = "Traversal failed: " + ex.Message; return res; }

            foreach (var kv in byLeaf)
            {
                var distinctPaths = kv.Value.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (distinctPaths.Count < 2) continue;   // same file placed N times — that is tool 136's job, not this one

                string canonical = null; DateTime canonicalTime = DateTime.MinValue;
                var others = new List<string>();
                foreach (var p in distinctPaths)
                {
                    DateTime mod = DateTime.MinValue;
                    try { if (File.Exists(p)) mod = File.GetLastWriteTimeUtc(p); } catch { }
                    if (mod > canonicalTime) { canonicalTime = mod; canonical = p; }
                }
                foreach (var p in distinctPaths) if (!string.Equals(p, canonical, StringComparison.OrdinalIgnoreCase)) others.Add(p);
                if (canonical == null) continue;   // neither path exists on disk anymore — a different tool's problem (missing reference)

                res.Groups.Add(new ResolveDuplicatePathsGroup
                {
                    LeafName = kv.Key,
                    CanonicalPath = canonical,
                    CanonicalModifiedUtc = canonicalTime,
                    DuplicatePaths = others,
                    ComponentCount = kv.Value.Count
                });
            }
            res.GroupCount = res.Groups.Count;
            res.Success = true;

            await emit("Scout", null, "done", res.GroupCount + " duplicate-path group(s) found");

            var sb = new StringBuilder();
            if (res.GroupCount == 0)
                sb.Append("No duplicate-path parts — every filename in this assembly resolves to exactly one folder.");
            else
            {
                sb.Append(res.GroupCount + " part name" + (res.GroupCount == 1 ? "" : "s") + " resolving to more than one folder ("
                          + "batch operations that key by filename alone will double-count these):");
                foreach (var g in res.Groups)
                {
                    sb.Append("\n- " + g.LeafName + " (" + g.ComponentCount + " instance" + (g.ComponentCount == 1 ? "" : "s") + "): canonical (newest, "
                              + g.CanonicalModifiedUtc.ToString("yyyy-MM-dd") + ") = " + g.CanonicalPath);
                    foreach (var d in g.DuplicatePaths) sb.Append("; duplicate = " + d);
                }
            }
            res.Info = sb.ToString();
            return res;
        }
    }
}
