using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT check for insert_library_feature (tool 218). Own recursive walk of the feature tree,
        // sharing no code with the handler's own FirstFeature/GetNextFeature counting loop — separate variable,
        // separate call site, so a handler that miscounts its own tree can't agree with itself.
        public static JObject MeasureInsertLibraryFeature(IModelDoc2 model)
        {
            var res = new JObject();
            int count = 0;
            string lastName = null;
            try
            {
                var f = model.FirstFeature() as IFeature;
                while (f != null)
                {
                    count++;
                    try { lastName = f.Name; } catch { }
                    f = f.GetNextFeature() as IFeature;
                }
            }
            catch { }
            res["featureCount"] = count;
            res["lastFeatureName"] = lastName;
            return res;
        }
    }
}
