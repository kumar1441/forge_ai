using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for add_sketch_arc (tool 117). Own feature walk: counts Forge-Arc sketches (run0=0 ->
    /// run1=1 -> run2=1 proves write + idempotency) and independently recounts arc segments, plus a rebuild-clean read.
    /// Read-only.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureAddSketchArc(IModelDoc2 model)
        {
            var mo = new JObject();
            if (model == null) { mo["error"] = "no model"; return mo; }

            int arcSketches = 0, arcs = 0; bool hasForge = false;
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    string nm = null; try { nm = f.Name; } catch { }
                    string tn = ""; try { tn = f.GetTypeName2(); } catch { }
                    if (tn == "ProfileFeature" && nm != null && nm.StartsWith("Forge-Arc", StringComparison.OrdinalIgnoreCase))
                    {
                        arcSketches++; hasForge = true;
                        var sk = f.GetSpecificFeature2() as Sketch;
                        if (sk != null)
                            foreach (var o in (sk.GetSketchSegments() as object[]) ?? new object[0])
                            {
                                var seg = o as SketchSegment; if (seg == null) continue;
                                bool constr = false; try { constr = seg.ConstructionGeometry; } catch { }
                                if (constr) continue;
                                int t = -1; try { t = seg.GetType(); } catch { }
                                if (t == (int)swSketchSegments_e.swSketchARC) arcs++;
                            }
                    }
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch { }

            int rw = 0; try { rw = model.Extension.GetWhatsWrongCount(); } catch { }

            mo["arcSketches"] = arcSketches;
            mo["arcSegments"] = arcs;
            mo["hasForgeArc"] = hasForge;
            mo["rebuildErrors"] = rw;
            return mo;
        }
    }
}
