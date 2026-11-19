using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for convert_entities (tool 200). Own feature walk: counts Forge-Convert sketches
    /// (run0=0 -> run1=1 -> run2=1 proves write + idempotency) and independently recounts non-construction segments
    /// (expect 1 = the projected edge), plus a rebuild-clean read. Read-only. ConvertEntities returns void, so this
    /// crossed segment recount is the only honest proof the op actually projected geometry (fail-closed cross-check
    /// against the FullyDefineSketch-class no-op risk).
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureConvertEntities(IModelDoc2 model)
        {
            var mo = new JObject();
            if (model == null) { mo["error"] = "no model"; return mo; }

            int convertSketches = 0, segments = 0; bool hasForge = false;
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    string nm = null; try { nm = f.Name; } catch { }
                    string tn = ""; try { tn = f.GetTypeName2(); } catch { }
                    if (tn == "ProfileFeature" && nm != null && nm.StartsWith("Forge-Convert", StringComparison.OrdinalIgnoreCase))
                    {
                        convertSketches++; hasForge = true;
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

            mo["convertSketches"] = convertSketches;
            mo["convertSegments"] = segments;
            mo["hasForgeConvert"] = hasForge;
            mo["rebuildErrors"] = rw;
            return mo;
        }
    }
}
