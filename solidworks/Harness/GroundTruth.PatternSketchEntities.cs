using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for pattern_sketch_entities (tool 203). Own feature walk: counts Forge-SketchPattern
    /// sketches (run0=0 -> run1=1 -> run2=1 proves write + idempotency) and independently recounts non-construction
    /// segments (known truth = 3: the seed circle + 2 linear copies). Plus a rebuild-clean read. Read-only.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasurePatternSketchEntities(IModelDoc2 model)
        {
            var mo = new JObject();
            if (model == null) { mo["error"] = "no model"; return mo; }

            int patternSketches = 0, segments = 0; bool hasForge = false;
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    string nm = null; try { nm = f.Name; } catch { }
                    string tn = ""; try { tn = f.GetTypeName2(); } catch { }
                    if (tn == "ProfileFeature" && nm != null && nm.StartsWith("Forge-SketchPattern", StringComparison.OrdinalIgnoreCase))
                    {
                        patternSketches++; hasForge = true;
                        var sk = f.GetSpecificFeature2() as Sketch;
                        if (sk != null)
                            foreach (var o in (sk.GetSketchSegments() as object[]) ?? new object[0])
                            {
                                var seg = o as SketchSegment; if (seg == null) continue;
                                bool constr = false; try { constr = seg.ConstructionGeometry; } catch { }
                                if (constr) continue;
                                segments++;
                            }
                    }
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch { }

            int rw = 0; try { rw = model.Extension.GetWhatsWrongCount(); } catch { }

            mo["patternSketches"] = patternSketches;
            mo["patternSegments"] = segments;
            mo["hasForgeSketchPattern"] = hasForge;
            mo["rebuildErrors"] = rw;
            return mo;
        }
    }
}
