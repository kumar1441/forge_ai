using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT load-state census for set_component_lightweight, CROSSED rather than parallel: the per-component
        // census (GetSuppression2) is cross-checked against IAssemblyDoc.GetLightWeightComponentCount() — a completely
        // different, ASSEMBLY-level API the handler never calls. If the two disagree, one of them is lying and the
        // harness says so instead of believing either.
        //
        // MEASURED ON THIS R2026x BUILD: IComponent2.IsSuppressed() returns TRUE for a LIGHTWEIGHT component. It is not
        // a suppression test — only GetSuppression2() == swComponentSuppressed(0) is. So the census classifies by the
        // STATE ENUM and keeps the IsSuppressed tally as a separate diagnostic field rather than a verdict.
        public static JObject MeasureSetComponentLightweight(IModelDoc2 model)
        {
            var res = new JObject();
            var rows = new JArray();
            int lightweight = 0, resolved = 0, suppressed = 0, total = 0, isSuppressedFlag = 0;
            var asm = model as AssemblyDoc;
            if (asm == null) { res["rows"] = rows; res["total"] = 0; return res; }
            try
            {
                foreach (var o in (asm.GetComponents(false) as object[]) ?? new object[0])
                {
                    var c = o as Component2; if (c == null) continue;
                    total++;
                    string nm = null; try { nm = c.Name2; } catch { }
                    int st = -1; try { st = c.GetSuppression2(); } catch { }
                    bool sup = false; try { sup = c.IsSuppressed(); } catch { }
                    if (sup) isSuppressedFlag++;
                    if (st == (int)swComponentSuppressionState_e.swComponentSuppressed) suppressed++;
                    // this build exposes TWO lightweight values (1 = swComponentLightweight, 4 = swComponentFullyLightweight)
                    else if (st == (int)swComponentSuppressionState_e.swComponentLightweight ||
                             st == (int)swComponentSuppressionState_e.swComponentFullyLightweight) lightweight++;
                    else resolved++;
                    rows.Add(new JObject { ["name"] = nm, ["state"] = st, ["suppressed"] = sup });
                }
            }
            catch { }

            int aggregate = -1;   // -1 = the assembly-level API is dead on this build (record it, don't hide it)
            try { aggregate = asm.GetLightWeightComponentCount(); } catch { }

            res["rows"] = rows;
            res["total"] = total;
            res["lightweight"] = lightweight;
            res["resolved"] = resolved;
            res["suppressed"] = suppressed;                  // GetSuppression2 == 0, the only honest suppression test here
            res["isSuppressedFlagCount"] = isSuppressedFlag; // diagnostic: IsSuppressed() also fires on LIGHTWEIGHT on this build
            res["aggregateLightweight"] = aggregate;
            return res;
        }
    }
}
