using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for AddSketchDimension. Shares no code with the handler: recurses into EVERY
    /// feature AND its sub-features (the handler only walks the top-level tree) counting display DIMENSIONS via
    /// GetFirstDisplayDimension/GetNextDisplayDimension (the SetDimension.ReadDims traversal family, a completely
    /// different path than the handler's own SketchSegment-endpoint re-measure) and reads the tagged
    /// "Forge-SketchDim" sketch's own dimension value straight off the Dimension object.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureAddSketchDimension(IModelDoc2 model)
        {
            var d = new JObject();
            int dimCount = 0, forgeCount = 0;
            double taggedValueMm = -1;
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                AsdWalk(f, ref dimCount, ref forgeCount, ref taggedValueMm);
                f = f.GetNextFeature() as Feature;
            }
            int rebuildErrors = 0; try { rebuildErrors = model.Extension.GetWhatsWrongCount(); } catch { }

            d["totalDimensions"] = dimCount;
            d["forgeSketchDimCount"] = forgeCount;
            d["taggedDimValueMm"] = taggedValueMm;
            d["rebuildErrors"] = rebuildErrors;
            d["fingerprint"] = new JObject { ["totalDimensions"] = dimCount, ["taggedDimValueMm"] = Math.Round(taggedValueMm, 3) };
            return d;
        }

        private static void AsdWalk(Feature f, ref int dimCount, ref int forgeCount, ref double taggedValueMm)
        {
            if (f == null) return;
            string nm = null; try { nm = f.Name; } catch { }
            bool isTagged = nm != null && nm.Equals("Forge-SketchDim", StringComparison.OrdinalIgnoreCase);
            if (isTagged) forgeCount++;

            var dd = f.GetFirstDisplayDimension() as DisplayDimension;
            while (dd != null)
            {
                dimCount++;
                if (isTagged)
                {
                    var dim = dd.GetDimension2(0) as Dimension;
                    if (dim != null) { try { taggedValueMm = dim.SystemValue * 1000.0; } catch { } }
                }
                dd = f.GetNextDisplayDimension(dd) as DisplayDimension;
            }

            var sub = f.GetFirstSubFeature() as Feature;
            while (sub != null) { AsdWalk(sub, ref dimCount, ref forgeCount, ref taggedValueMm); sub = sub.GetNextSubFeature() as Feature; }
        }
    }
}
