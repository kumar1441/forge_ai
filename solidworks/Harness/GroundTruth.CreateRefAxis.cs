using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT reference-axis count — shares NO code with CreateRefAxis. Its own tree traversal counting
        // RefAxis-type features, so the harness proves (run0 vs run1) the axis count rose by exactly 1 and (run2)
        // that a rerun doesn't keep stacking axes.
        public static JObject MeasureCreateRefAxis(IModelDoc2 model)
        {
            var res = new JObject();
            int axes = 0, total = 0;
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    total++;
                    string tn = null; try { tn = f.GetTypeName2(); } catch { }
                    if (tn == "RefAxis") axes++;
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch { }
            res["axes"] = axes;
            res["totalFeatures"] = total;
            return res;
        }
    }
}
