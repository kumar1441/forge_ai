using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for CreateSketch. Shares no code with the handler: recurses into EVERY feature AND
    /// its sub-features (the handler only walks the top-level tree) counting ProfileFeature-type sketches and
    /// specifically the "Forge-Sketch" tagged one — a different traversal over the same live state, same shape as
    /// GroundTruth.MeasureAddSketchEntity.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureCreateSketch(ISldWorks app, IModelDoc2 model)
        {
            var d = new JObject();
            int totalSketches = 0, forgeSketchCount = 0;
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                CsWalk(f, ref totalSketches, ref forgeSketchCount);
                f = f.GetNextFeature() as Feature;
            }
            int rebuildErrors = 0; try { rebuildErrors = model.Extension.GetWhatsWrongCount(); } catch { }

            d["totalSketches"] = totalSketches;
            d["forgeSketchCount"] = forgeSketchCount;
            d["rebuildErrors"] = rebuildErrors;
            d["fingerprint"] = new JObject { ["totalSketches"] = totalSketches };
            return d;
        }

        private static void CsWalk(Feature f, ref int totalSketches, ref int forgeSketchCount)
        {
            if (f == null) return;
            string tn = null; try { tn = f.GetTypeName2(); } catch { }
            if (tn == "ProfileFeature")
            {
                totalSketches++;
                string nm = null; try { nm = f.Name; } catch { }
                if (nm != null && nm.Equals("Forge-Sketch", StringComparison.OrdinalIgnoreCase)) forgeSketchCount++;
            }
            var sub = f.GetFirstSubFeature() as Feature;
            while (sub != null) { CsWalk(sub, ref totalSketches, ref forgeSketchCount); sub = sub.GetNextSubFeature() as Feature; }
        }
    }
}
