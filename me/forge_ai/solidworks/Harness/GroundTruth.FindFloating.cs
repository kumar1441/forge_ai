using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT floating-component count — shares NO code with FindFloating. Own traversal + GetConstrainedStatus
        // + IsFixed read. Returns floating (under-constrained, not fixed) / fixed / total.
        public static JObject MeasureFindFloating(IModelDoc2 model)
        {
            var res = new JObject();
            var asm = model as AssemblyDoc;
            if (asm == null) { res["error"] = "not an assembly"; return res; }
            int total = 0, floating = 0, fixedC = 0, fully = 0;
            foreach (var o in (asm.GetComponents(true) as object[]) ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                total++;
                bool fx = false; try { fx = c.IsFixed(); } catch { }
                int st = (int)swConstrainedStatus_e.swUnderConstrained; try { st = c.GetConstrainedStatus(); } catch { }
                if (fx) { fixedC++; continue; }
                if (st == (int)swConstrainedStatus_e.swFullyConstrained) fully++;
                else if (st == (int)swConstrainedStatus_e.swUnderConstrained) floating++;
            }
            res["total"] = total; res["floating"] = floating; res["fixed"] = fixedC; res["fullyConstrained"] = fully;
            return res;
        }
    }
}
