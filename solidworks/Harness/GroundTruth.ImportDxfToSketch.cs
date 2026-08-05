using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for ImportDxfToSketch. Shares no code with the handler: recurses into EVERY feature
    /// AND its sub-features (the handler only walks the top-level tree) looking for "ProfileFeature"-type sketches,
    /// tallies the "Forge-DxfImport" tagged one specifically, and re-counts its own non-construction segments off
    /// ISketch directly — a different traversal over the same live state, same shape as GroundTruth.MeasureAddSketchEntity.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureImportDxfToSketch(ISldWorks app, IModelDoc2 model)
        {
            var d = new JObject();
            int totalSketches = 0, forgeImportCount = 0, taggedSegments = 0;
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                IdWalk(f, ref totalSketches, ref forgeImportCount, ref taggedSegments);
                f = f.GetNextFeature() as Feature;
            }
            int rebuildErrors = 0; try { rebuildErrors = model.Extension.GetWhatsWrongCount(); } catch { }

            d["totalSketches"] = totalSketches;
            d["forgeImportCount"] = forgeImportCount;
            d["taggedSegments"] = taggedSegments;
            d["rebuildErrors"] = rebuildErrors;
            d["fingerprint"] = new JObject { ["totalSketches"] = totalSketches, ["taggedSegments"] = taggedSegments };
            return d;
        }

        private static void IdWalk(Feature f, ref int totalSketches, ref int forgeImportCount, ref int taggedSegments)
        {
            if (f == null) return;
            string tn = null; try { tn = f.GetTypeName2(); } catch { }
            if (tn == "ProfileFeature")
            {
                totalSketches++;
                string nm = null; try { nm = f.Name; } catch { }
                if (nm != null && nm.Equals("Forge-DxfImport", StringComparison.OrdinalIgnoreCase))
                {
                    forgeImportCount++;
                    try
                    {
                        var sk = f.GetSpecificFeature2() as Sketch;
                        if (sk != null)
                            foreach (var o in (sk.GetSketchSegments() as object[]) ?? new object[0])
                            {
                                var seg = o as SketchSegment; if (seg == null) continue;
                                bool constr = false; try { constr = seg.ConstructionGeometry; } catch { }
                                if (!constr) taggedSegments++;
                            }
                    }
                    catch { }
                }
            }
            var sub = f.GetFirstSubFeature() as Feature;
            while (sub != null) { IdWalk(sub, ref totalSketches, ref forgeImportCount, ref taggedSegments); sub = sub.GetNextSubFeature() as Feature; }
        }
    }
}
