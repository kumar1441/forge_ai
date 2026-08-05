using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for CreateLayoutSketch (tool 231). Shares no code with the handler: recurses into
    /// EVERY feature AND its sub-features (the handler only walks the top-level tree) counting ProfileFeature-type
    /// sketches and specifically the "Forge-LayoutSketch" tagged one — same shape as GroundTruth.MeasureCreateSketch.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureCreateLayoutSketch(IModelDoc2 model)
        {
            var d = new JObject();
            int totalSketches = 0, layoutSketchCount = 0;
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                ClsWalk(f, ref totalSketches, ref layoutSketchCount);
                f = f.GetNextFeature() as Feature;
            }
            int rebuildErrors = 0; try { rebuildErrors = model.Extension.GetWhatsWrongCount(); } catch { }

            d["totalSketches"] = totalSketches;
            d["layoutSketchCount"] = layoutSketchCount;
            d["rebuildErrors"] = rebuildErrors;
            d["fingerprint"] = new JObject { ["totalSketches"] = totalSketches };
            return d;
        }

        private static void ClsWalk(Feature f, ref int totalSketches, ref int layoutSketchCount)
        {
            if (f == null) return;
            string tn = null; try { tn = f.GetTypeName2(); } catch { }
            if (tn == "ProfileFeature")
            {
                totalSketches++;
                string nm = null; try { nm = f.Name; } catch { }
                if (nm != null && nm.Equals("Forge-LayoutSketch", StringComparison.OrdinalIgnoreCase)) layoutSketchCount++;
            }
            var sub = f.GetFirstSubFeature() as Feature;
            while (sub != null) { ClsWalk(sub, ref totalSketches, ref layoutSketchCount); sub = sub.GetNextSubFeature() as Feature; }
        }
    }
}
