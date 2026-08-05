using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for AddSketchEntity. Shares no code with the handler: recurses into EVERY feature
    /// AND its sub-features (the handler only walks the top-level tree) looking for anything whose
    /// GetSpecificFeature2() is a Sketch — a different traversal over the same live state — and tallies
    /// non-construction segments + sketch points across all of them, plus counts how many "Forge-SketchEntity-N"
    /// sketches exist (the handler's own idempotency-by-repeat-count marker).
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureAddSketchEntity(ISldWorks app, IModelDoc2 model)
        {
            var d = new JObject();
            int segs = 0, pts = 0, forgeSketchCount = 0;
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                AseWalk(f, ref segs, ref pts, ref forgeSketchCount);
                f = f.GetNextFeature() as Feature;
            }
            int rebuildErrors = 0; try { rebuildErrors = model.Extension.GetWhatsWrongCount(); } catch { }

            d["totalSegments"] = segs;
            d["totalPoints"] = pts;
            d["forgeSketchEntityCount"] = forgeSketchCount;
            d["rebuildErrors"] = rebuildErrors;
            d["fingerprint"] = new JObject { ["totalSegments"] = segs, ["totalPoints"] = pts };
            return d;
        }

        private static void AseWalk(Feature f, ref int segs, ref int pts, ref int forgeSketchCount)
        {
            if (f == null) return;
            Sketch sk = null; try { sk = f.GetSpecificFeature2() as Sketch; } catch { }
            if (sk != null)
            {
                foreach (var o in (sk.GetSketchSegments() as object[]) ?? new object[0])
                {
                    var seg = o as SketchSegment; if (seg == null) continue;
                    bool constr = false; try { constr = seg.ConstructionGeometry; } catch { }
                    if (!constr) segs++;
                }
                try { pts += sk.GetSketchPointsCount2(); } catch { }
                string nm = null; try { nm = f.Name; } catch { }
                if (nm != null && nm.Equals("Forge-SketchEntity", StringComparison.OrdinalIgnoreCase)) forgeSketchCount++;
            }
            var sub = f.GetFirstSubFeature() as Feature;
            while (sub != null) { AseWalk(sub, ref segs, ref pts, ref forgeSketchCount); sub = sub.GetNextSubFeature() as Feature; }
        }
    }
}
