using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT verdict for skip_pattern_instance. The handler proves the skip through the pattern DEFINITION
        // (SkippedItemArray count). This ground truth proves it through GEOMETRY, a different API family entirely: it
        // counts the solid body's CYLINDRICAL faces — on a plain block every through-hole bore is exactly one cylinder,
        // so the cylindrical-face count IS the live hole count. Skipping one pattern instance removes one bore, so the
        // count must fall by one from run0 to run1. It also reports the pattern's own instance/skipped counts so the
        // harness can sanity-check them, but the cylindrical-face drop is the handler-blind check.
        public static JObject MeasureSkipPatternInstance(IModelDoc2 model)
        {
            var res = new JObject();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { res["cylindrical"] = 0; res["totalInstances"] = 0; res["skippedCount"] = 0; return res; }

            int cyl = 0, planar = 0, total = 0;
            try
            {
                var bodies = (model as PartDoc).GetBodies2((int)swBodyType_e.swSolidBody, false) as object[];
                foreach (var bo in bodies ?? new object[0])
                {
                    var body = bo as Body2; if (body == null) continue;
                    foreach (var fo in (body.GetFaces() as object[]) ?? new object[0])
                    {
                        var face = fo as Face2; if (face == null) continue;
                        total++;
                        var s = face.GetSurface() as Surface; if (s == null) continue;
                        if (s.IsPlane()) planar++;
                        else if (s.IsCylinder()) cyl++;
                    }
                }
            }
            catch { }
            res["cylindrical"] = cyl;
            res["planar"] = planar;
            res["totalFaces"] = total;

            int instances = 0, skipped = 0;
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    string tn = null; try { tn = f.GetTypeName2(); } catch { }
                    if (tn != null && tn.IndexOf("LPattern", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var d = f.GetDefinition() as ILinearPatternFeatureData;
                        if (d != null) { try { instances = d.D1TotalInstances; } catch { } try { skipped = d.GetSkippedItemCount(); } catch { } }
                        break;
                    }
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch { }
            res["totalInstances"] = instances;
            res["skippedCount"] = skipped;
            return res;
        }
    }
}
