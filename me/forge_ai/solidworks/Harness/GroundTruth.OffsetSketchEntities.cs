using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for offset_sketch_entities (tool 199). Own feature walk: counts Forge-Offset sketches
    /// (run0=0 -> run1=1 -> run2=1 proves write + idempotency) and independently recounts non-construction segments
    /// (expect 2 = seed circle + its offset), plus a rebuild-clean read. Read-only.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureOffsetSketchEntities(IModelDoc2 model)
        {
            var mo = new JObject();
            if (model == null) { mo["error"] = "no model"; return mo; }

            int offsetSketches = 0, segments = 0; bool hasForge = false;
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    string nm = null; try { nm = f.Name; } catch { }
                    string tn = ""; try { tn = f.GetTypeName2(); } catch { }
                    if (tn == "ProfileFeature" && nm != null && nm.StartsWith("Forge-Offset", StringComparison.OrdinalIgnoreCase))
                    {
                        offsetSketches++; hasForge = true;
                        var sk = f.GetSpecificFeature2() as Sketch;
                        if (sk != null)
                            foreach (var o in (sk.GetSketchSegments() as object[]) ?? new object[0])
                            {
                                var seg = o as SketchSegment; if (seg == null) continue;
                                bool constr = false; try { constr = seg.ConstructionGeometry; } catch { }
                                if (!constr) segments++;
                            }
                    }
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch { }

            int rw = 0; try { rw = model.Extension.GetWhatsWrongCount(); } catch { }

            mo["offsetSketches"] = offsetSketches;
            mo["offsetSegments"] = segments;
            mo["hasForgeOffset"] = hasForge;
            mo["rebuildErrors"] = rw;
            return mo;
        }
    }
}
