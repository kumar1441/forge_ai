using System;
using System.IO;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT recount + per-file census for insert_component (tool 29). Shares no code with the handler: it walks
        // the assembly's non-suppressed top-level components, totals them, and counts how many reference each file (base
        // name). insert_component is NOT idempotent — the harness keeps the same in-memory doc across run0/run1/run2, so the
        // total (and the inserted file's instance count) must rise by exactly one each run. from/insert file come from
        // test-config, so the GT never agrees by construction.
        public static JObject MeasureInsertComponent(IModelDoc2 model)
        {
            var mo = new JObject();
            var asm = model as AssemblyDoc;
            var byFile = new JObject();
            if (asm == null) { mo["error"] = "active doc is not an assembly"; mo["total"] = 0; mo["byFile"] = byFile; return mo; }

            int total = 0;
            foreach (var o in (asm.GetComponents(true) as object[]) ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                string path = null; try { path = c.GetPathName(); } catch { }
                string file = string.IsNullOrEmpty(path) ? "(none)" : Path.GetFileName(path).ToLowerInvariant();
                total++;
                byFile[file] = (byFile[file] != null ? (int)byFile[file] : 0) + 1;
            }
            mo["total"] = total;
            mo["byFile"] = byFile;
            return mo;
        }
    }
}
