using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for Create3DSketch. Shares no code with the handler: recurses into EVERY feature
    /// AND its sub-features (the handler only walks the top-level tree) counting "3DProfileFeature"-type sketches
    /// and specifically the "Forge-3DSketch" tagged one, plus reads the tagged sketch's own point count directly
    /// off ISketch — a different traversal over the same live state, same shape as GroundTruth.MeasureCreateSketch.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureCreate3DSketch(ISldWorks app, IModelDoc2 model)
        {
            var d = new JObject();
            int total3DSketches = 0, forge3DSketchCount = 0, taggedPointCount = 0;
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                C3Walk(f, ref total3DSketches, ref forge3DSketchCount, ref taggedPointCount);
                f = f.GetNextFeature() as Feature;
            }
            int rebuildErrors = 0; try { rebuildErrors = model.Extension.GetWhatsWrongCount(); } catch { }

            d["total3DSketches"] = total3DSketches;
            d["forge3DSketchCount"] = forge3DSketchCount;
            d["taggedPointCount"] = taggedPointCount;
            d["rebuildErrors"] = rebuildErrors;
            d["fingerprint"] = new JObject { ["total3DSketches"] = total3DSketches };
            return d;
        }

        private static void C3Walk(Feature f, ref int total3DSketches, ref int forge3DSketchCount, ref int taggedPointCount)
        {
            if (f == null) return;
            string tn = null; try { tn = f.GetTypeName2(); } catch { }
            if (tn == "3DProfileFeature")
            {
                total3DSketches++;
                string nm = null; try { nm = f.Name; } catch { }
                if (nm != null && nm.Equals("Forge-3DSketch", StringComparison.OrdinalIgnoreCase))
                {
                    forge3DSketchCount++;
                    try
                    {
                        var sk = f.GetSpecificFeature2() as Sketch;
                        if (sk != null) taggedPointCount = sk.GetSketchPointsCount2();
                    }
                    catch { }
                }
            }
            var sub = f.GetFirstSubFeature() as Feature;
            while (sub != null) { C3Walk(sub, ref total3DSketches, ref forge3DSketchCount, ref taggedPointCount); sub = sub.GetNextSubFeature() as Feature; }
        }
    }
}
