using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground-truth for the unsuppress_components handler. Shares NO code with UnsuppressComponents.cs.
    ///
    /// Unsuppressing is a reversible STATE change, so the harness asserts:
    ///   1. suppressedComponents DROPS  (run1 &lt; run0 — components were re-activated)
    ///   2. totalComponents UNCHANGED   (a state change, not an add/delete)
    ///   3. rebuildErrors == 0
    /// and the rerun is idempotent (run2 == run1 — nothing left to unsuppress). It re-reads every component's
    /// IsSuppressed() from its own GetComponents traversal.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureUnsuppressComponents(ISldWorks app, IModelDoc2 model)
        {
            var mo = new JObject();
            var asm = model as AssemblyDoc;
            if (asm == null) { mo["applicable"] = false; mo["reason"] = "active doc is not an assembly"; return mo; }
            mo["applicable"] = true;

            int total = 0, suppressed = 0, active = 0;
            try
            {
                object[] comps = asm.GetComponents(false) as object[];   // all levels, our own read
                foreach (var o in comps ?? new object[0])
                {
                    var c = o as Component2; if (c == null) continue;
                    total++;
                    bool sup = false; try { sup = c.IsSuppressed(); } catch { }
                    if (sup) suppressed++; else active++;
                }
            }
            catch (Exception ex) { mo["error"] = ex.GetType().Name + ": " + ex.Message; }

            int rb = 0; try { rb = model.Extension.GetWhatsWrongCount(); } catch { }

            mo["totalComponents"] = total;
            mo["suppressedComponents"] = suppressed;   // the delta target: run1 < run0
            mo["activeComponents"] = active;
            mo["rebuildErrors"] = rb;
            mo["hasComponents"] = total > 0;
            mo["fingerprint"] = new JObject { ["totalComponents"] = total, ["suppressedComponents"] = suppressed, ["rebuildErrors"] = rb };
            return mo;
        }
    }
}
