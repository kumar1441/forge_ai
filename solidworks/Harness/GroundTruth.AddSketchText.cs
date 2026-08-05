using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for add_sketch_text (tool 198). Own feature walk: counts Forge-Text sketches
    /// (run0=0 -> run1=1 -> run2=1 proves write + idempotency) and independently recounts text segments via
    /// ISketch.GetSketchTextSegments, plus a rebuild-clean read. Read-only.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureAddSketchText(IModelDoc2 model)
        {
            var mo = new JObject();
            if (model == null) { mo["error"] = "no model"; return mo; }

            int textSketches = 0, texts = 0; bool hasForge = false;
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    string nm = null; try { nm = f.Name; } catch { }
                    string tn = ""; try { tn = f.GetTypeName2(); } catch { }
                    if (tn == "ProfileFeature" && nm != null && nm.StartsWith("Forge-Text", StringComparison.OrdinalIgnoreCase))
                    {
                        textSketches++; hasForge = true;
                        var sk = f.GetSpecificFeature2() as Sketch;
                        if (sk != null)
                        {
                            var arr = sk.GetSketchTextSegments() as object[];
                            if (arr != null) texts += arr.Length;
                        }
                    }
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch { }

            int rw = 0; try { rw = model.Extension.GetWhatsWrongCount(); } catch { }

            mo["textSketches"] = textSketches;
            mo["textSegments"] = texts;
            mo["hasForgeText"] = hasForge;
            mo["rebuildErrors"] = rw;
            return mo;
        }
    }
}
