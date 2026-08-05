using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT custom-property count — shares NO code with GetProperties. Same reality (the doc's + each unique
        // part's CustomPropertyManager) but its own enumeration and its own dedupe, so a handler that double-counts a
        // file-vs-config name or misses a component's file shows up as a total mismatch.
        public static JObject MeasureGetProperties(IModelDoc2 model)
        {
            var res = new JObject();
            if (model == null) { res["error"] = "no doc"; return res; }
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int total = 0;

            string activeCfg = ""; try { activeCfg = ((Configuration)model.GetActiveConfiguration()).Name; } catch { }
            total += CountProps(model, activeCfg);
            try { files.Add(model.GetPathName() ?? "active"); } catch { }

            var asm = model as AssemblyDoc;
            if (asm != null)
            {
                foreach (var o in (asm.GetComponents(false) as object[]) ?? new object[0])
                {
                    var c = o as Component2; if (c == null) continue;
                    bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                    string path = null; try { path = c.GetPathName(); } catch { }
                    if (string.IsNullOrEmpty(path) || !files.Add(path)) continue;
                    var pd = c.GetModelDoc2() as IModelDoc2; if (pd == null) continue;
                    total += CountProps(pd, "");
                }
            }

            res["total"] = total;
            res["sources"] = files.Count;
            return res;
        }

        // count distinct property NAMES across file-level "" and the given config (deduped) — mirrors the handler's
        // scope set but counts independently.
        private static int CountProps(IModelDoc2 doc, string cfg)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var scope in new[] { "", cfg })
            {
                if (scope == null) continue;
                CustomPropertyManager cpm = null;
                try { cpm = doc.Extension.get_CustomPropertyManager(scope); } catch { }
                if (cpm == null) continue;
                var names = null as string[];
                try { names = cpm.GetNames() as string[]; } catch { }
                if (names == null) continue;
                foreach (var n in names) if (!string.IsNullOrEmpty(n)) seen.Add(n);
            }
            return seen.Count;
        }
    }
}
