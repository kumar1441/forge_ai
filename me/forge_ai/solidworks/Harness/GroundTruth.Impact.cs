using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground-truth for the change-impact handler (demo #3). Impact is READ-ONLY, so the only thing
    /// worth asserting is that it changed NOTHING. This shares NO code with Impact.cs: it takes its own fingerprint
    /// of the model — feature count + every dimension's value + rebuild-error count — with its own tree walk, so the
    /// harness can diff run0 vs run1 vs run2 and prove the fingerprint is byte-for-byte identical (a read-only handler
    /// that leaves ANY dim moved or feature added/removed has failed, no matter what it reported).
    ///
    /// It deliberately does NOT re-derive Impact's dependency counts: dependency tracing is the handler's judgement,
    /// and re-implementing GetChildren here would just be the same call twice, not an independent check. The
    /// invariant we CAN verify independently is immutability — that's what this measures.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureImpact(ISldWorks app, IModelDoc2 model)
        {
            var mo = new JObject();
            if (model == null) { mo["applicable"] = false; return mo; }
            mo["applicable"] = true;

            int featureCount = 0;
            var dims = new JArray();
            var seen = new HashSet<string>();
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    featureCount++;
                    var dd = f.GetFirstDisplayDimension() as DisplayDimension;
                    while (dd != null)
                    {
                        var d = dd.GetDimension2(0) as Dimension;
                        if (d != null)
                        {
                            string fn = null; try { fn = d.FullName; } catch { }
                            if (fn != null && seen.Add(fn))
                            {
                                double v = 0; try { v = d.SystemValue; } catch { }
                                dims.Add(new JObject { ["name"] = fn, ["valueMm"] = Math.Round(v * 1000.0, 6) });
                            }
                        }
                        dd = f.GetNextDisplayDimension(dd) as DisplayDimension;
                    }
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch (Exception ex) { mo["error"] = ex.GetType().Name + ": " + ex.Message; }

            int rebuild = 0; try { rebuild = model.Extension.GetWhatsWrongCount(); } catch { }

            mo["featureCount"] = featureCount;   // added/removed feature => handler was NOT read-only
            mo["dimCount"] = dims.Count;
            mo["dims"] = dims;                    // per-dim value; diff run1 vs run0 must be empty
            mo["rebuildErrors"] = rebuild;        // Impact never rebuilds geometry, so this must not move
            return mo;
        }
    }
}
