using System;
using System.IO;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT for repair_missing_references (tool 132). Shares no code with the handler: a plain per-
        // component File.Exists re-check on Component2.GetPathName() (the stored reference survives suppression
        // either way — see the handler's own doc comment), not the handler's grouped ReplaceComponents path.
        // Called both before (run0, missing count must be >0) and after (run1, must be 0) the repair.
        public static JObject MeasureRepairMissingReferences(IModelDoc2 model)
        {
            var res = new JObject();
            var missing = new JArray();
            int total = 0;
            var asm = model as AssemblyDoc;
            if (asm == null) { res["totalComponents"] = 0; res["missingCount"] = 0; res["missingPaths"] = missing; return res; }

            foreach (var o in (asm.GetComponents(true) as object[]) ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                total++;
                string cp = null; try { cp = c.GetPathName(); } catch { }
                if (string.IsNullOrEmpty(cp)) continue;
                bool exists = false; try { exists = File.Exists(cp); } catch { }
                if (!exists) missing.Add(cp);
            }
            res["totalComponents"] = total;
            res["missingCount"] = missing.Count;
            res["missingPaths"] = missing;
            return res;
        }
    }
}
