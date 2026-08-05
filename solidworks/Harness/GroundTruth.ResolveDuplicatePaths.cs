using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for resolve_duplicate_paths (tool 241). Same traversal primitive as
    /// `find_duplicate_components`'s own GT (this codebase's established convention for this handler class —
    /// `AssemblyDoc.GetComponents(false)`), but its OWN separate leaf-name grouping + its OWN separate
    /// newest-file-wins canonical pick (shares no code with the handler). Never opens either candidate file in
    /// SolidWorks (same OS-level-only rule the handler follows, since OpenDoc6 collides on same-leaf-name docs).
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureResolveDuplicatePaths(IModelDoc2 model)
        {
            var mo = new JObject();
            var asm = model as AssemblyDoc;
            if (asm == null) { mo["groupCount"] = -1; return mo; }

            var byLeaf = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            int total = 0;
            try
            {
                foreach (var o in (asm.GetComponents(false) as object[]) ?? new object[0])
                {
                    var c = o as Component2; if (c == null) continue;
                    total++;
                    string p = null; try { p = c.GetPathName(); } catch { }
                    if (string.IsNullOrEmpty(p)) continue;
                    string leaf = null; try { leaf = Path.GetFileName(p); } catch { }
                    if (string.IsNullOrEmpty(leaf)) continue;
                    if (!byLeaf.TryGetValue(leaf, out var list)) { list = new List<string>(); byLeaf[leaf] = list; }
                    list.Add(p);
                }
            }
            catch { }

            int groupCount = 0;
            var groups = new JArray();
            foreach (var kv in byLeaf)
            {
                var distinctPaths = kv.Value.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (distinctPaths.Count < 2) continue;

                string canonical = null; DateTime canonicalTime = DateTime.MinValue;
                foreach (var p in distinctPaths)
                {
                    DateTime mod = DateTime.MinValue;
                    try { if (File.Exists(p)) mod = File.GetLastWriteTimeUtc(p); } catch { }
                    if (mod > canonicalTime) { canonicalTime = mod; canonical = p; }
                }
                if (canonical == null) continue;

                groupCount++;
                groups.Add(new JObject { ["leaf"] = kv.Key, ["canonical"] = canonical, ["instanceCount"] = kv.Value.Count });
            }

            mo["totalComponents"] = total;
            mo["groupCount"] = groupCount;
            mo["groups"] = groups;
            return mo;
        }
    }
}
