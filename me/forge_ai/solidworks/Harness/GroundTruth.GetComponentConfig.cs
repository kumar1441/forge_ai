using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT component-config spread — shares NO code with GetComponentConfig. Own referenced-config read.
        public static JObject MeasureGetComponentConfig(IModelDoc2 model)
        {
            var res = new JObject();
            var asm = model as AssemblyDoc;
            if (asm == null) { res["total"] = -1; return res; }
            int total = 0; var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var o in (asm.GetComponents(true) as object[]) ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                total++;
                string cfg = null; try { cfg = c.ReferencedConfiguration; } catch { }
                seen.Add(string.IsNullOrEmpty(cfg) ? "(none)" : cfg);
            }
            res["total"] = total;
            res["distinctConfigs"] = seen.Count;
            return res;
        }
    }
}
