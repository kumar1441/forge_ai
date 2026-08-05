using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for mirror_sketch_entities (tool 202). Own feature walk: counts Forge-Mirror sketches
    /// (run0=0 -> run1=1 -> run2=1 proves write + idempotency), independently recounts non-construction segments
    /// (known truth = 2: the seed circle + its mirror), and independently sums the arc X-centers (known truth ~0:
    /// +30mm and -30mm are symmetric about the Y centerline). Plus a rebuild-clean read. Read-only.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureMirrorSketchEntities(IModelDoc2 model)
        {
            var mo = new JObject();
            if (model == null) { mo["error"] = "no model"; return mo; }

            int mirrorSketches = 0, segments = 0; double centerXSumMm = 0; bool hasForge = false;
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    string nm = null; try { nm = f.Name; } catch { }
                    string tn = ""; try { tn = f.GetTypeName2(); } catch { }
                    if (tn == "ProfileFeature" && nm != null && nm.StartsWith("Forge-Mirror", StringComparison.OrdinalIgnoreCase))
                    {
                        mirrorSketches++; hasForge = true;
                        var sk = f.GetSpecificFeature2() as Sketch;
                        if (sk != null)
                            foreach (var o in (sk.GetSketchSegments() as object[]) ?? new object[0])
                            {
                                var seg = o as SketchSegment; if (seg == null) continue;
                                bool constr = false; try { constr = seg.ConstructionGeometry; } catch { }
                                if (constr) continue;
                                segments++;
                                var arc = seg as SketchArc;
                                if (arc != null)
                                {
                                    var c = arc.GetCenterPoint2() as double[];
                                    if (c != null && c.Length >= 1) centerXSumMm += c[0] * 1000.0;
                                }
                            }
                    }
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch { }

            int rw = 0; try { rw = model.Extension.GetWhatsWrongCount(); } catch { }

            mo["mirrorSketches"] = mirrorSketches;
            mo["mirrorSegments"] = segments;
            mo["mirrorCenterXSumMm"] = Math.Round(centerXSumMm, 3);
            mo["hasForgeMirror"] = hasForge;
            mo["rebuildErrors"] = rw;
            return mo;
        }
    }
}
