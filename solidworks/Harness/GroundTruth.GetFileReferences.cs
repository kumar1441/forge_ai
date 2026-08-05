using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT reference inventory, from a completely different API than the handler's: the handler asks
        // ISldWorks.GetDocumentDependencies2 (a FILE-level query that never opens the tree); this walks the live
        // COMPONENT tree and collects Component2.GetPathName(). Neither can confirm the other's reading. Suppressed
        // components are INCLUDED on purpose — a suppressed instance still binds the assembly to that file, and a
        // reference list that drops it is exactly how a pack-and-go arrives at the vendor incomplete.
        public static JObject MeasureGetFileReferences(IModelDoc2 model)
        {
            var res = new JObject();
            var paths = new JArray();
            int missing = 0;
            if (model == null) { res["paths"] = paths; res["uniqueFiles"] = 0; res["missing"] = 0; return res; }

            string root = null; try { root = model.GetPathName(); } catch { }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var asm = model as AssemblyDoc;
            if (asm != null)
            {
                try
                {
                    foreach (var o in (asm.GetComponents(false) as object[]) ?? new object[0])
                    {
                        var c = o as Component2; if (c == null) continue;
                        string p = null; try { p = c.GetPathName(); } catch { }
                        if (string.IsNullOrWhiteSpace(p)) continue;
                        if (!string.IsNullOrEmpty(root) && string.Equals(p, root, StringComparison.OrdinalIgnoreCase)) continue;
                        if (!seen.Add(p)) continue;
                        bool exists = false; try { exists = File.Exists(p); } catch { }
                        if (!exists) missing++;
                        paths.Add(p);
                    }
                }
                catch { }
            }

            res["rootPath"] = root;
            res["paths"] = paths;
            res["uniqueFiles"] = paths.Count;
            res["missing"] = missing;
            return res;
        }
    }
}
