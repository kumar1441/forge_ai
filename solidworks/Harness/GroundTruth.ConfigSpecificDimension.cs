using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT per-config dimension-value census for set_config_specific_dimension. On this build the per-config
        // value getters (GetSystemValue2/GetSystemValue3(swAllConfiguration)) read 0 for INACTIVE configs of a still-shared
        // dim, so the only honest read is ACTIVATE-the-config + SystemValue. This walks every config, rebuilds, reads each
        // display dim's resolved value, then restores the original active config. Keyed by the DOC-STRIPPED dim name (the
        // FullName's last @-segment is the owning document; strip it, per the DimKey landmine). It reaches the value a
        // different way than the handler (fresh model.Parameter lookup per config here vs the handler's own map), and the
        // harness asserts the target config moved to the requested mm while every other config held. Values in MILLIMETRES.
        public static JObject MeasureConfigSpecificDimension(IModelDoc2 model)
        {
            var res = new JObject();
            var cfgArr = new JArray();
            var dimsObj = new JObject();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { res["configs"] = cfgArr; res["dims"] = dimsObj; return res; }

            string[] names = null; try { names = model.GetConfigurationNames() as string[]; } catch { }
            names = names ?? new string[0];
            foreach (var n in names) cfgArr.Add(n);

            string origActive = null; try { origActive = model.ConfigurationManager?.ActiveConfiguration?.Name; } catch { }

            // collect the dim keys once (from the current active config)
            var keys = new System.Collections.Generic.List<string>();
            try
            {
                var feat = model.FirstFeature() as Feature;
                while (feat != null)
                {
                    var dd = feat.GetFirstDisplayDimension() as DisplayDimension;
                    while (dd != null)
                    {
                        var d = dd.GetDimension2(0) as Dimension;
                        if (d != null) { string full = null; try { full = d.FullName; } catch { } string key = DimKey(full); if (!string.IsNullOrEmpty(key) && !keys.Contains(key)) keys.Add(key); }
                        dd = feat.GetNextDisplayDimension(dd) as DisplayDimension;
                    }
                    feat = feat.GetNextFeature() as Feature;
                }
            }
            catch { }

            // per config: activate, rebuild, read each key's resolved value
            var perKey = new System.Collections.Generic.Dictionary<string, JArray>();
            foreach (var k in keys) perKey[k] = new JArray();
            foreach (var n in names)
            {
                try { model.ShowConfiguration2(n); model.ForceRebuild3(false); } catch { }
                foreach (var k in keys)
                {
                    double mm = -1;
                    // resolve by matching the doc-stripped key against each display dim's FullName in this config
                    try
                    {
                        var feat = model.FirstFeature() as Feature;
                        while (feat != null && mm < 0)
                        {
                            var dd = feat.GetFirstDisplayDimension() as DisplayDimension;
                            while (dd != null)
                            {
                                var d = dd.GetDimension2(0) as Dimension;
                                if (d != null) { string full = null; try { full = d.FullName; } catch { } if (DimKey(full) == k) { try { mm = d.SystemValue * 1000.0; } catch { } break; } }
                                dd = feat.GetNextDisplayDimension(dd) as DisplayDimension;
                            }
                            feat = feat.GetNextFeature() as Feature;
                        }
                    }
                    catch { }
                    perKey[k].Add(Math.Round(mm, 4));
                }
            }
            try { if (!string.IsNullOrEmpty(origActive)) { model.ShowConfiguration2(origActive); model.ForceRebuild3(false); } } catch { }

            foreach (var k in keys) dimsObj[k] = perKey[k];
            res["configs"] = cfgArr;
            res["dims"] = dimsObj;   // docStrippedKey -> [value mm per config, in `configs` order]
            return res;
        }

        // strip the OWNING-DOCUMENT last @-segment: "D1@Boss-Extrude1@multiconfig-block" -> "D1@Boss-Extrude1"
        private static string DimKey(string full)
        {
            if (string.IsNullOrEmpty(full)) return full;
            var segs = full.Split('@');
            return segs.Length <= 1 ? full : string.Join("@", segs, 0, segs.Length - 1);
        }
    }
}
