using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT feature-parameter read — shares NO code with GetFeatureInfo. Its own tree walk + its own read of
        // the extrude depth via GetDefinition, returning the parametric-feature count and the max boss-extrude depth
        // (mm). The seeded block's boss extrude was generated at 20mm, so the harness can assert a KNOWN value.
        public static JObject MeasureGetFeatureInfo(IModelDoc2 model)
        {
            var res = new JObject();
            if (model == null) { res["error"] = "no doc"; return res; }
            int count = 0; double maxDepth = -1;
            var feat = model.FirstFeature() as Feature;
            while (feat != null)
            {
                string tn = null; try { tn = feat.GetTypeName2(); } catch { }
                // exclude exactly the same non-modelling types the handler's IsRealFeature skips, so the count is a
                // shared DEFINITION both reach independently (not a heuristic that could be silently wrong).
                if (!string.IsNullOrEmpty(tn) && tn != "RefPlane" && tn != "RefAxis" && tn != "OriginProfileFeature" &&
                    tn != "CoordSys" && tn != "DetailCabinet" && tn != "HistoryFolder" && tn != "SketchBlockDef")
                {
                    count++;
                    try
                    {
                        var ext = feat.GetDefinition() as IExtrudeFeatureData2;
                        if (ext != null && (tn.IndexOf("Boss", StringComparison.OrdinalIgnoreCase) >= 0 || tn == "Extrusion"))
                        {
                            double d = 0; try { d = ext.GetDepth(true); } catch { }
                            double mm = d * 1000.0;
                            if (mm > maxDepth) maxDepth = mm;
                        }
                    }
                    catch { }
                }
                feat = feat.GetNextFeature() as Feature;
            }
            res["count"] = count;
            res["maxExtrudeDepthMm"] = maxDepth;
            return res;
        }
    }
}
