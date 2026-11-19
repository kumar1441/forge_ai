using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for add_sketch_polygon (tool 196) — its own feature walk, shares no code with the
    /// handler. Counts Forge-Polygon sketch features (so run0=0, run1=1, run2=1 proves the write AND idempotency) and
    /// independently recounts the created polygon's straight sides, plus a rebuild-clean read. Read-only.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureAddSketchPolygon(IModelDoc2 model)
        {
            var mo = new JObject();
            if (model == null) { mo["error"] = "no model"; return mo; }

            int polygonSketches = 0, lineSegments = 0; bool hasForge = false;
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    string nm = null; try { nm = f.Name; } catch { }
                    string tn = ""; try { tn = f.GetTypeName2(); } catch { }
                    if (tn == "ProfileFeature" && nm != null && nm.StartsWith("Forge-Polygon", StringComparison.OrdinalIgnoreCase))
                    {
                        polygonSketches++; hasForge = true;
                        var sk = f.GetSpecificFeature2() as Sketch;
                        if (sk != null)
                            foreach (var o in (sk.GetSketchSegments() as object[]) ?? new object[0])
                            {
                                var seg = o as SketchSegment; if (seg == null) continue;
                                int t = -1; try { t = seg.GetType(); } catch { }
                                bool constr = false; try { constr = seg.ConstructionGeometry; } catch { }
                                if (t == (int)swSketchSegments_e.swSketchLINE && !constr) lineSegments++;
                            }
                    }
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch { }

            int rw = 0; try { rw = model.Extension.GetWhatsWrongCount(); } catch { }

            mo["polygonSketches"] = polygonSketches;
            mo["lineSegments"] = lineSegments;
            mo["hasForgePolygon"] = hasForge;
            mo["rebuildErrors"] = rw;
            return mo;
        }
    }
}
