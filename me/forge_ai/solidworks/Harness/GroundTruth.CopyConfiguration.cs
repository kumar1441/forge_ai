using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT configuration state — shares NO code with CopyConfiguration. Its own GetConfigurationNames read
        // plus its own ACTIVE-configuration read, because the whole point of tool 92 is that copying a config must not
        // move the user out of the one they were in (AddConfiguration3 leaves the new config active). Capturing the
        // active name here, on the GT side, is what lets the harness prove the restore actually happened rather than
        // taking the handler's word for it.
        public static JObject MeasureCopyConfiguration(IModelDoc2 model)
        {
            var res = new JObject();
            if (model == null) { res["count"] = -1; return res; }
            var names = model.GetConfigurationNames() as string[];
            res["count"] = names?.Length ?? 0;
            var arr = new JArray();
            if (names != null) foreach (var n in names) arr.Add(n);
            res["names"] = arr;
            string active = null;
            try { active = (model.ConfigurationManager.ActiveConfiguration as Configuration).Name; } catch { }
            res["active"] = active;
            return res;
        }
    }
}
