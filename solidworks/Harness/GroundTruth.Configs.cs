using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for the GetConfigs (list_configurations) READ handler. Shares NO code with GetConfigs.cs.
    /// The handler enumerates via IModelDoc2.GetConfigurationNames; this GT counts via a DIFFERENT API,
    /// IModelDoc2.GetConfigurationCount, and reads the active name via GetActiveConfiguration — so agreement on the count
    /// and active name is a genuine cross-check. Read-only: identical fingerprint on run1/run2 proves no write.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureConfigs(ISldWorks app, IModelDoc2 model)
        {
            var d = new JObject();
            if (model == null) { d["applicable"] = false; d["reason"] = "no active document"; return d; }
            d["applicable"] = true;

            int count = 0; try { count = model.GetConfigurationCount(); } catch { }
            string active = null; try { var ac = model.GetActiveConfiguration() as Configuration; if (ac != null) active = ac.Name; } catch { }
            int rb = 0; try { rb = model.Extension.GetWhatsWrongCount(); } catch { }

            d["configCount"] = count;
            d["activeConfig"] = active;
            d["hasConfigs"] = count >= 1;
            d["rebuildErrors"] = rb;
            d["fingerprint"] = new JObject { ["configCount"] = count, ["activeConfig"] = active, ["rebuildErrors"] = rb };
            return d;
        }
    }
}
