using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for trim_extend (tool 201). Own feature walk: counts Forge-Trim sketches
    /// (run0=0 -> run1=1 -> run2=1 proves write + idempotency) and independently sums non-construction segment
    /// LENGTH (mm) — the known truth is that a real trim removed ~50mm (two 100mm crossing lines -> ~150mm total),
    /// so trimLenMm must be < 200 and > 0. Plus a rebuild-clean read. Read-only.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureTrimExtendSketch(IModelDoc2 model)
        {
            var mo = new JObject();
            if (model == null) { mo["error"] = "no model"; return mo; }

            int trimSketches = 0, segments = 0; double lenMm = 0; bool hasForge = false;
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    string nm = null; try { nm = f.Name; } catch { }
                    string tn = ""; try { tn = f.GetTypeName2(); } catch { }
                    if (tn == "ProfileFeature" && nm != null && nm.StartsWith("Forge-Trim", StringComparison.OrdinalIgnoreCase))
                    {
                        trimSketches++; hasForge = true;
                        var sk = f.GetSpecificFeature2() as Sketch;
                        if (sk != null)
                            foreach (var o in (sk.GetSketchSegments() as object[]) ?? new object[0])
                            {
                                var seg = o as SketchSegment; if (seg == null) continue;
                                bool constr = false; try { constr = seg.ConstructionGeometry; } catch { }
                                if (constr) continue;
                                segments++;
                                try { lenMm += seg.GetLength() * 1000.0; } catch { }
                            }
                    }
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch { }

            int rw = 0; try { rw = model.Extension.GetWhatsWrongCount(); } catch { }

            mo["trimSketches"] = trimSketches;
            mo["trimSegments"] = segments;
            mo["trimLenMm"] = Math.Round(lenMm, 2);
            mo["hasForgeTrim"] = hasForge;
            mo["rebuildErrors"] = rw;
            return mo;
        }
    }
}
