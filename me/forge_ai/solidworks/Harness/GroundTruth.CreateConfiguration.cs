using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT configuration inventory — shares NO code with CreateConfiguration. Its own GetConfigurationNames
        // read, returning the count + names, so the harness proves (run0 vs run1) the config count rose by exactly 1
        // and the new name is present, and (run2) a rerun doesn't duplicate it.
        public static JObject MeasureCreateConfiguration(IModelDoc2 model)
        {
            var res = new JObject();
            if (model == null) { res["count"] = -1; return res; }
            var names = model.GetConfigurationNames() as string[];
            res["count"] = names?.Length ?? 0;
            var arr = new JArray();
            if (names != null) foreach (var n in names) arr.Add(n);
            res["names"] = arr;
            return res;
        }
    }
}
