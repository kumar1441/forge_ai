using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT explode-step coverage census for repair_exploded_view (tool 193). Shares NO code with
        // RepairExplodedView — its own step walk (IConfiguration.GetNumberOfExplodeSteps/IGetExplodeStep) and its
        // own live top-level component list, never the handler's own OrphanNames/RepairedNames. Publishes the
        // uncovered-component name list directly so the harness can assert it shrinks to empty after the repair.
        public static JObject MeasureRepairExplodedView(IModelDoc2 model)
        {
            var res = new JObject();
            var asm = model as AssemblyDoc;
            if (asm == null) { res["error"] = "not an assembly"; return res; }

            var config = model.GetActiveConfiguration() as Configuration;
            if (config == null) { res["error"] = "no active configuration"; return res; }

            int stepCount = 0; try { stepCount = config.GetNumberOfExplodeSteps(); } catch { }
            var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < stepCount; i++)
            {
                ExplodeStep st = null; try { st = config.IGetExplodeStep(i); } catch { }
                if (st == null) continue;
                int nc = 0; try { nc = st.GetNumOfComponents(); } catch { }
                for (int j = 0; j < nc; j++) { string cn = null; try { cn = st.GetComponentName(j); } catch { } if (cn != null) covered.Add(cn); }
            }

            var liveNames = new List<string>();
            var uncovered = new JArray();
            foreach (var o in (asm.GetComponents(true) as object[]) ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                string nm = null; try { nm = c.Name2; } catch { }
                if (nm == null) continue;
                liveNames.Add(nm);
                if (!covered.Contains(nm)) uncovered.Add(nm);
            }

            res["stepCount"] = stepCount;
            res["liveComponents"] = liveNames.Count;
            res["uncovered"] = uncovered;
            res["uncoveredCount"] = uncovered.Count;
            return res;
        }
    }
}
