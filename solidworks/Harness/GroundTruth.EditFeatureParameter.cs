using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT extrude-depth read — shares NO code with EditFeatureParameter. Its own tree walk for the sole
        // Extrusion feature, reading IExtrudeFeatureData2.GetDepth(true). The harness proves (run0 vs run1) the depth
        // moved to the requested value and (run2) stayed there (idempotent).
        public static JObject MeasureEditFeatureParameter(IModelDoc2 model)
        {
            var res = new JObject();
            int count = 0; string name = null; double depthMm = -1;
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    string tn = ""; try { tn = f.GetTypeName2(); } catch { }
                    if (tn == "Extrusion")
                    {
                        count++;
                        if (name == null)
                        {
                            try { name = f.Name; } catch { }
                            try { var d = f.GetDefinition() as IExtrudeFeatureData2; if (d != null) depthMm = Math.Round(d.GetDepth(true) * 1000.0, 4); } catch { }
                        }
                    }
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch { }
            res["extrudeCount"] = count;
            res["firstExtrudeName"] = name;
            res["firstExtrudeDepthMm"] = depthMm;
            return res;
        }
    }
}
