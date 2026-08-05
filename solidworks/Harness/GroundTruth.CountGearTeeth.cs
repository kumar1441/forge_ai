using System;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        /// <summary>
        /// INDEPENDENT count for count_gear_teeth (READ). Shares NO code with CountGearTeeth.cs's face-geometry
        /// grouping: walks the feature tree to the rim-cut feature ("Cut" or "ICE" — cut-extrudes report "ICE" on
        /// this build) and counts the ARC segments in its own sketch (absorbed as a sub-feature of the consuming
        /// cut, per the DetectSharedSketches convention) — the design-intent tooth count, straight from the sketch
        /// that cut the tooth spaces, not the resulting solid's faces. Only meaningful on the GENERATED gear
        /// fixture (a feature-tree/sketch-based part); on real imported/dumb-solid gear assemblies this reports
        /// applicable=false (no sketch to read) — those are validated live, not GT-asserted.
        /// </summary>
        public static JObject MeasureCountGearTeeth(IModelDoc2 model, string intent)
        {
            var res = new JObject();
            if ((int)model.GetType() == (int)swDocumentTypes_e.swDocPART)
            {
                int n = CountRimCutCircles(model);
                res["applicable"] = n >= 0;
                res["teeth"] = n;
                res["fingerprint"] = new JObject { ["teeth"] = n };
                return res;
            }
            var asm = model as AssemblyDoc;
            if (asm == null) { res["applicable"] = false; return res; }
            var counts = new JObject();
            foreach (var o in (asm.GetComponents(false) as object[]) ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                string name = null; try { name = c.Name2; } catch { }
                if (string.IsNullOrEmpty(name) || !Regex.IsMatch(name, "gear|pinion|sprocket", RegexOptions.IgnoreCase)) continue;
                IModelDoc2 cm = null; try { cm = c.GetModelDoc2() as IModelDoc2; } catch { }
                if (cm == null) continue;
                int n = CountRimCutCircles(cm);
                if (n >= 0) counts[name] = n;
            }
            res["applicable"] = counts.Count > 0;
            res["teeth"] = counts;
            return res;
        }

        // Finds the LAST cut-type feature and counts the (non-construction) arc segments in its own absorbed
        // sketch. Returns -1 if no such cut/sketch is found (real dumb-solid models: expected, not an error).
        private static int CountRimCutCircles(IModelDoc2 model)
        {
            try
            {
                Feature cutFeat = null;
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    string tn = null; try { tn = f.GetTypeName2(); } catch { }
                    if (tn != null && (tn.IndexOf("Cut", StringComparison.OrdinalIgnoreCase) >= 0 || tn == "ICE")) cutFeat = f;
                    f = f.GetNextFeature() as Feature;
                }
                if (cutFeat == null) return -1;

                Feature sketchFeat = null;
                var sub = cutFeat.GetFirstSubFeature() as Feature;
                while (sub != null)
                {
                    string stn = null; try { stn = sub.GetTypeName2(); } catch { }
                    if (stn == "ProfileFeature" || stn == "3DProfileFeature") sketchFeat = sub;
                    sub = sub.GetNextSubFeature() as Feature;
                }
                if (sketchFeat == null) return -1;

                var sk = sketchFeat.GetSpecificFeature2() as Sketch;
                if (sk == null) return -1;

                int arcs = 0;
                foreach (var o in (sk.GetSketchSegments() as object[]) ?? new object[0])
                {
                    var seg = o as SketchSegment; if (seg == null) continue;
                    bool constr = false; try { constr = seg.ConstructionGeometry; } catch { }
                    if (constr) continue;
                    int t = -1; try { t = seg.GetType(); } catch { }
                    if (t == (int)swSketchSegments_e.swSketchARC) arcs++;
                }
                return arcs;
            }
            catch { return -1; }
        }
    }
}
