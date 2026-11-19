using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground-truth for the set_dimension handler (tool #63). Shares NO code with SetDimension.cs — it does
    /// its OWN feature-tree traversal of every display dimension.
    ///
    /// set_dimension is a WRITE that moves ONE named dimension, so the harness compares a BASELINE read (run0, before
    /// the write) against the post-write read (run1) and asserts:
    ///
    ///   1. the named target dim's valueMm changed to the requested value   (the write landed)
    ///   2. rebuildErrors == 0                                              (the change rebuilt clean)
    ///   ... and run2 == run1 (idempotent — the rerun finds it already at the value and moves nothing).
    ///
    /// The grader locates the target dim by name in the `dims` array (names are FullNames like "D1@Sketch1@Part"),
    /// reads its valueMm on each run, and checks the delta. `dims` is capped (~40) so a heavily-dimensioned model
    /// doesn't bloat the report — the target dim is what matters. hasSolid lets the grader demand an honest refusal on
    /// a body-less/imported part with no editable driving dimensions.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureSetDimension(ISldWorks app, IModelDoc2 model)
        {
            var mo = new JObject();
            if (model == null) { mo["applicable"] = false; mo["reason"] = "no active document"; return mo; }
            mo["applicable"] = true;

            var dims = new JArray();
            var seen = new HashSet<string>();
            int total = 0;
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    var dd = f.GetFirstDisplayDimension() as DisplayDimension;
                    while (dd != null)
                    {
                        var d = dd.GetDimension2(0) as Dimension;
                        if (d != null)
                        {
                            string fn = null; try { fn = d.FullName; } catch { }
                            if (!string.IsNullOrEmpty(fn) && seen.Add(fn))
                            {
                                total++;
                                if (dims.Count < 40)
                                {
                                    double v = 0; try { v = d.SystemValue; } catch { }
                                    dims.Add(new JObject
                                    {
                                        ["name"] = fn,                                  // FullName — the grader finds the target here
                                        ["valueMm"] = Math.Round(v * 1000.0, 6)         // SystemValue is metres
                                    });
                                }
                            }
                        }
                        dd = f.GetNextDisplayDimension(dd) as DisplayDimension;
                    }
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch (Exception ex) { mo["error"] = ex.GetType().Name + ": " + ex.Message; }

            int rebuild = 0; try { rebuild = model.Extension.GetWhatsWrongCount(); } catch { }

            // hasSolid: a body-less/imported part with no editable dims => the handler MUST refuse honestly, not invent a set.
            bool hasSolid = false;
            try
            {
                var part = model as PartDoc;
                if (part != null)
                {
                    var bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[];
                    hasSolid = bodies != null && bodies.Length > 0;
                }
                else { hasSolid = true; } // assemblies/other docs: not the body-less-refusal case
            }
            catch { }

            mo["dimCount"] = total;               // total driving dims found (independent of the capped list)
            mo["dims"] = dims;                     // per-dim {name, valueMm}; the grader reads the target's value per run
            mo["rebuildErrors"] = rebuild;         // must be 0 after a good set
            mo["hasSolid"] = hasSolid;

            // fingerprint the grader diffs run0 -> run1 (the target dim's value moves; count + rebuild stay put)
            var fp = new JObject();
            fp["dimCount"] = total;
            fp["rebuildErrors"] = rebuild;
            mo["fingerprint"] = fp;
            return mo;
        }
    }
}
