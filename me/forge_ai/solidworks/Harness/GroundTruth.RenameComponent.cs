using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT component-name inventory — shares NO code with RenameComponent. Its own traversal collects every
        // component instance name + the total count, so the harness can prove (run0 vs run1) that EXACTLY one name
        // changed to the target, the old name is gone, and the component count is unchanged (nothing added/lost).
        public static JObject MeasureRenameComponent(IModelDoc2 model)
        {
            var res = new JObject();
            var asm = model as AssemblyDoc;
            if (asm == null) { res["error"] = "not an assembly"; return res; }
            int total = 0;
            var names = new JArray();
            foreach (var o in (asm.GetComponents(true) as object[]) ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                total++;
                string nm = null; try { nm = c.Name2; } catch { }
                names.Add(nm ?? "");
            }
            res["total"] = total;
            res["names"] = names;
            return res;
        }
    }
}
