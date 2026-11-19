using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT pattern inventory — shares NO code with GetPatternInfo. Own tree walk + own definition read of
        // the instance count. The pattern fixture is generated with a KNOWN 3-instance linear pattern.
        public static JObject MeasureGetPatternInfo(IModelDoc2 model)
        {
            var res = new JObject();
            int patterns = 0, totalInstances = 0;
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    string tn = null; try { tn = f.GetTypeName2(); } catch { }
                    if (tn != null && (tn.IndexOf("LPattern", StringComparison.OrdinalIgnoreCase) >= 0 || tn.IndexOf("CirPattern", StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        patterns++;
                        int inst = 0;
                        try
                        {
                            object def = f.GetDefinition();
                            var lin = def as ILinearPatternFeatureData; if (lin != null) { try { inst = lin.D1TotalInstances; } catch { } try { res["d1SpacingMm"] = lin.D1Spacing * 1000.0; } catch { } }
                            var cir = def as ICircularPatternFeatureData; if (cir != null) { try { inst = cir.TotalInstances; } catch { } }
                        }
                        catch { }
                        totalInstances += inst;
                    }
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch { }
            res["patternCount"] = patterns;
            res["totalInstances"] = totalInstances;
            return res;
        }
    }
}
