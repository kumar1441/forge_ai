using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for the SetFixed (fix_component / float_component) WRITE handler. Shares NO code with
    /// SetFixed.cs — it re-reads every top-level component's Component2.IsFixed on its own traversal and tallies the
    /// fixed vs floating counts. The harness diffs run0 (baseline) vs run1: the fixed count moves by exactly the
    /// handler's reported Changed (up for "fix", down for "float"). Idempotent rerun => run2 == run1.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureSetFixed(ISldWorks app, IModelDoc2 model)
        {
            var d = new JObject();
            var asm = model as AssemblyDoc;
            if (asm == null) { d["applicable"] = false; d["reason"] = "active doc is not an assembly"; return d; }
            d["applicable"] = true;

            object[] top = asm.GetComponents(true) as object[];
            int total = 0, fixedN = 0, floatingN = 0;
            foreach (var o in top ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { }
                if (sup) continue;
                total++;
                bool isFixed = false; try { isFixed = c.IsFixed(); } catch { }
                if (isFixed) fixedN++; else floatingN++;
            }

            int rb = 0; try { rb = model.Extension.GetWhatsWrongCount(); } catch { }

            d["totalComponents"] = total;
            d["fixedComponents"] = fixedN;
            d["floatingComponents"] = floatingN;
            d["rebuildErrors"] = rb;
            d["fingerprint"] = new JObject
            {
                ["totalComponents"] = total,
                ["fixedComponents"] = fixedN,
                ["rebuildErrors"] = rb
            };
            return d;
        }
    }
}
