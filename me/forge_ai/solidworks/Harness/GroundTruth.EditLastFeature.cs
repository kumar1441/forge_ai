using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT "last feature" read for tool 236 edit_last_feature — shares NO code with EditLastFeature.cs. Its
        // own tree walk (FirstFeature/GetNextFeature, creation order) filtered by its own IsRealFeature copy, keeping
        // the LAST entry (the most recently created real feature). If that feature is an Extrusion, its depth is read
        // straight off IExtrudeFeatureData2.GetDepth — a different path than the handler's own GetDefinition read.
        public static JObject MeasureEditLastFeature(IModelDoc2 model)
        {
            var res = new JObject();
            string lastName = null, lastType = null; double depthMm = -1; int realCount = 0;
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    string tn = ""; try { tn = f.GetTypeName2(); } catch { }
                    if (!string.IsNullOrEmpty(tn) && IsRealFeatureGT(tn))
                    {
                        realCount++;
                        try { lastName = f.Name; } catch { }
                        lastType = tn;
                    }
                    f = f.GetNextFeature() as Feature;
                }
                if (lastType == "Extrusion" && lastName != null)
                {
                    var f2 = model.FirstFeature() as Feature;
                    while (f2 != null)
                    {
                        string n2 = null; try { n2 = f2.Name; } catch { }
                        if (n2 == lastName)
                        {
                            try { var d = f2.GetDefinition() as IExtrudeFeatureData2; if (d != null) depthMm = Math.Round(d.GetDepth(true) * 1000.0, 4); } catch { }
                            break;
                        }
                        f2 = f2.GetNextFeature() as Feature;
                    }
                }
            }
            catch { }
            res["realFeatureCount"] = realCount;
            res["lastFeatureName"] = lastName;
            res["lastFeatureType"] = lastType;
            res["lastFeatureDepthMm"] = depthMm;
            return res;
        }

        // own copy of the scaffold-exclusion rule (find_features_by_type's IsRealFeature) — independent of the handler.
        private static bool IsRealFeatureGT(string tn)
        {
            if (tn.EndsWith("Folder", StringComparison.OrdinalIgnoreCase)) return false;
            switch (tn)
            {
                case "RefPlane": case "RefAxis": case "OriginProfileFeature": case "CoordSys":
                case "DetailCabinet": case "HistoryFolder": case "SketchBlockDef": return false;
                default: return true;
            }
        }
    }
}
