using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for the GetFeatureTree (list_features) READ handler. Shares NO code with GetFeatureTree.cs.
    /// It does its OWN feature-tree traversal (independent tally) AND records IModelDoc2.GetFeatureCount (a different API)
    /// as a second cross-reference. The harness asserts handler.TotalFeatures == this independent traversal count and that
    /// the per-type tallies agree. Read-only: identical fingerprint on run1/run2 proves the handler wrote nothing.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureFeatureTree(ISldWorks app, IModelDoc2 model)
        {
            var d = new JObject();
            if (model == null) { d["applicable"] = false; d["reason"] = "no active document"; return d; }
            d["applicable"] = true;

            int total = 0, suppressed = 0;
            var byType = new JObject();
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    total++;
                    string tn = null; try { tn = f.GetTypeName2(); } catch { }
                    if (string.IsNullOrEmpty(tn)) tn = "Unknown";
                    byType[tn] = ((int?)byType[tn] ?? 0) + 1;
                    bool sup = false; try { sup = f.IsSuppressed(); } catch { }
                    if (sup) suppressed++;
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch (Exception ex) { d["error"] = ex.GetType().Name + ": " + ex.Message; }

            int apiCount = -1; try { apiCount = model.GetFeatureCount(); } catch { }
            int rb = 0; try { rb = model.Extension.GetWhatsWrongCount(); } catch { }

            d["featureCount"] = total;             // independent traversal count (the cross-check target)
            d["apiFeatureCount"] = apiCount;       // IModelDoc2.GetFeatureCount, a different API (informational)
            d["suppressed"] = suppressed;
            d["distinctTypes"] = byType.Count;
            d["byType"] = byType;
            d["hasFeatures"] = total > 0;
            d["rebuildErrors"] = rb;
            d["fingerprint"] = new JObject { ["featureCount"] = total, ["distinctTypes"] = byType.Count, ["rebuildErrors"] = rb };
            return d;
        }
    }
}
