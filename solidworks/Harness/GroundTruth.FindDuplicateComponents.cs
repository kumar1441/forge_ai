using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for find_duplicate_components (tool 136). Its OWN component traversal + its own
    /// file-grouping (shares no code with the handler), so the harness proves the duplicate accounting rather than
    /// echo it. Known truth on flange-suppressed (1 plate + 6 bolts): 7 components, 2 unique files, 1 duplicate group
    /// (the bolt, x6), largest group = 6. Read-only.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureFindDuplicateComponents(IModelDoc2 model)
        {
            var mo = new JObject();
            var asm = model as AssemblyDoc;
            if (asm == null) { mo["total"] = -1; return mo; }

            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int total = 0;
            try
            {
                foreach (var o in (asm.GetComponents(false) as object[]) ?? new object[0])
                {
                    var c = o as Component2; if (c == null) continue;
                    total++;
                    string p = null; try { p = c.GetPathName(); } catch { }
                    if (string.IsNullOrEmpty(p)) continue;
                    counts[p] = counts.TryGetValue(p, out int n) ? n + 1 : 1;
                }
            }
            catch { }

            int dupGroups = 0, largest = 0, instancesInDup = 0;
            foreach (var kv in counts.Values)
            {
                if (kv > 1) { dupGroups++; instancesInDup += kv; }
                if (kv > largest) largest = kv;
            }

            mo["total"] = total;
            mo["uniqueFiles"] = counts.Count;
            mo["duplicateGroups"] = dupGroups;
            mo["largestGroupSize"] = largest;
            mo["instancesInDupGroups"] = instancesInDup;
            return mo;
        }
    }
}
