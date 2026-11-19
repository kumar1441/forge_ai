using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT component-flag counts — shares NO code with GetComponentInfo. Its own traversal + its own flag
        // reads, so a handler that mis-reads a suppression state or a toolbox path shows up as a count mismatch.
        public static JObject MeasureGetComponentInfo(IModelDoc2 model)
        {
            var res = new JObject();
            var asm = model as AssemblyDoc;
            if (asm == null) { res["error"] = "not an assembly"; return res; }

            int total = 0, toolbox = 0, virt = 0, suppressed = 0, lightweight = 0, fixedC = 0;
            foreach (var o in (asm.GetComponents(true) as object[]) ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                total++;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { }
                if (sup) suppressed++;
                bool v = false; try { v = c.IsVirtual; } catch { }
                if (v) virt++;
                bool fx = false; try { fx = c.IsFixed(); } catch { }
                if (fx) fixedC++;
                int ss = -1; try { ss = c.GetSuppression2(); } catch { }
                if (ss == (int)swComponentSuppressionState_e.swComponentLightweight ||
                    ss == (int)swComponentSuppressionState_e.swComponentFullyLightweight) lightweight++;
                string p = null; try { p = c.GetPathName(); } catch { }
                // segment match "\toolbox\", not a bare substring (see handler note re the "…& toolbox config" folder)
                if (!string.IsNullOrEmpty(p) &&
                    System.Text.RegularExpressions.Regex.IsMatch(p, @"[\\/]toolbox[\\/]", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    toolbox++;
            }

            res["total"] = total;
            res["toolbox"] = toolbox;
            res["virtual"] = virt;
            res["suppressed"] = suppressed;
            res["lightweight"] = lightweight;
            res["fixed"] = fixedC;
            return res;
        }
    }
}
