using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT document-context read — shares NO code with GetActiveDocument. Its own reads of doc type, config
        // count, and (for assemblies) component count, so the harness can confirm the handler surfaced the real context.
        public static JObject MeasureGetActiveDocument(IModelDoc2 model)
        {
            var res = new JObject();
            if (model == null) { res["error"] = "no doc"; return res; }
            int t = (int)model.GetType();
            res["docType"] = t == (int)swDocumentTypes_e.swDocPART ? "part"
                           : t == (int)swDocumentTypes_e.swDocASSEMBLY ? "assembly"
                           : t == (int)swDocumentTypes_e.swDocDRAWING ? "drawing" : "unknown";
            int cfgCount = 0; try { var c = model.GetConfigurationNames() as string[]; cfgCount = c?.Length ?? 0; } catch { }
            res["configCount"] = cfgCount;
            string active = null; try { active = ((Configuration)model.GetActiveConfiguration()).Name; } catch { }
            res["activeConfig"] = active;
            int comps = -1;
            if (t == (int)swDocumentTypes_e.swDocASSEMBLY)
            { try { var a = (model as AssemblyDoc).GetComponents(true) as object[]; comps = a?.Length ?? 0; } catch { } }
            res["topLevelComponents"] = comps;
            return res;
        }
    }
}
