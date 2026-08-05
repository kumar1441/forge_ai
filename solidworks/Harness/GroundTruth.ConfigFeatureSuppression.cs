using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT per-config feature-suppression census for set_config_feature_suppression. The handler reads/writes a
        // single config via IsSuppressed2/SetSuppression2(swSpecifyConfiguration, {name}); this ground truth reads EVERY
        // feature's suppression across ALL configs in ONE call, IsSuppressed2(swAllConfiguration, null) -> a bool array in
        // GetConfigurationNames order — a different config-opt path. The harness looks up the target feature + config and
        // asserts the target config flipped while the others held. Publishing the raw per-config array (not a verdict)
        // keeps the ground truth blind to which feature/config the handler chose.
        public static JObject MeasureConfigFeatureSuppression(IModelDoc2 model)
        {
            var res = new JObject();
            var cfgArr = new JArray();
            var feats = new JObject();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { res["configs"] = cfgArr; res["features"] = feats; return res; }

            string[] names = null; try { names = model.GetConfigurationNames() as string[]; } catch { }
            names = names ?? new string[0];
            foreach (var n in names) cfgArr.Add(n);

            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    string nm = null; try { nm = f.Name; } catch { }
                    if (!string.IsNullOrEmpty(nm) && !feats.ContainsKey(nm))
                    {
                        var row = new JArray();
                        object r = null; try { r = f.IsSuppressed2((int)swInConfigurationOpts_e.swAllConfiguration, null); } catch { }
                        if (r is System.Array a) { foreach (var v in a) { bool b = false; try { b = Convert.ToBoolean(v); } catch { } row.Add(b); } }
                        else if (r is bool bb) row.Add(bb);
                        feats[nm] = row;
                    }
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch { }

            res["configs"] = cfgArr;
            res["features"] = feats;   // name -> [suppressed per config, in `configs` order]
            return res;
        }
    }
}
