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
    public class FindDuplicateComponentsResult
    {
        public bool Success;
        public int TotalComponents;
        public int UniqueFiles;
        public int DuplicateGroups;      // distinct files appearing more than once
        public int LargestGroupSize;     // instances of the most-repeated file
        public int InstancesInDupGroups; // total instances that belong to a duplicate group
        public List<string> Duplicates = new List<string>();  // "bolt.SLDPRT x6"
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 136 — find_duplicate_components (READ, within one assembly). The component-level sibling of compare_bodies:
    /// groups every component by its BACKING FILE (GetPathName) and reports which files back more than one instance —
    /// the "you placed the same part 6 times, is that intended / should it be a pattern?" check. Suppressed instances
    /// still count (a suppressed bolt still references the bolt file). Purely read-only; cross-checked against an
    /// INDEPENDENT GT traversal. Distinct from compare_bodies (bodies within a part) by requiring a component/part noun.
    /// </summary>
    public static class FindDuplicateComponents
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            return Regex.IsMatch(c, @"\b(duplicate|duplicates|repeated|identical|copies)\b")
                   && Regex.IsMatch(c, @"\b(component|components|part|parts|instance|instances)\b")
                   && !Regex.IsMatch(c, @"\bbod(y|ies)\b")
                   && !Regex.IsMatch(c, @"\b(path|paths|location|locations|folder|folders|network)\b");
        }

        public static async Task<FindDuplicateComponentsResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new FindDuplicateComponentsResult();
            var asm = model as AssemblyDoc;
            if (asm == null) { res.Error = "Open an assembly (.SLDASM) to find duplicate components."; return res; }

            await emit("Scout", "grouping components by backing file", "run", null);

            var byFile = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var o in (asm.GetComponents(false) as object[]) ?? new object[0])
                {
                    var c = o as Component2; if (c == null) continue;
                    res.TotalComponents++;
                    string p = null; try { p = c.GetPathName(); } catch { }
                    if (string.IsNullOrEmpty(p)) continue;
                    byFile[p] = byFile.TryGetValue(p, out int n) ? n + 1 : 1;
                }
            }
            catch (Exception ex) { res.Error = "Traversal failed: " + ex.Message; return res; }

            res.UniqueFiles = byFile.Count;
            foreach (var kv in byFile.OrderByDescending(k => k.Value))
            {
                if (kv.Value > 1)
                {
                    res.DuplicateGroups++;
                    res.InstancesInDupGroups += kv.Value;
                    if (kv.Value > res.LargestGroupSize) res.LargestGroupSize = kv.Value;
                    res.Duplicates.Add(FileName(kv.Key) + " x" + kv.Value);
                }
            }
            res.Success = true;

            await emit("Scout", null, "done", res.DuplicateGroups + " duplicate group(s) across " + res.UniqueFiles + " unique file(s)");

            var sb = new StringBuilder();
            if (res.DuplicateGroups == 0)
                sb.Append("No duplicate components — all " + res.UniqueFiles + " parts are placed once.");
            else
            {
                sb.Append(res.DuplicateGroups + " part" + (res.DuplicateGroups == 1 ? "" : "s") + " placed more than once ("
                          + res.InstancesInDupGroups + " of " + res.TotalComponents + " instances):");
                foreach (var d in res.Duplicates) sb.Append("\n- " + d);
            }
            res.Info = sb.ToString();
            return res;
        }

        private static string FileName(string p)
        { try { return Path.GetFileName(p); } catch { return p; } }
    }
}
