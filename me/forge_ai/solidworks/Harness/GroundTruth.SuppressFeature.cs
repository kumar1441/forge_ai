using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for the SuppressFeature (suppress_feature / unsuppress_feature) WRITE handler. Shares NO
    /// code with SuppressFeature.cs. It does its OWN feature-tree traversal and its OWN IsSuppressed() read in the ACTIVE
    /// configuration, tallying total features, how many are suppressed, and per-type breakdowns.
    ///
    /// The harness asserts the suppressed-feature-count DELTA (run1 − run0) == handler.Changed for a suppress (or −Changed
    /// for an unsuppress). Because suppression is a state change (not a delete), totalFeatures must be UNCHANGED across the
    /// run — a second independent invariant. run1/run2 identical suppressedFeatures proves the idempotent rerun wrote nothing.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureSuppressFeature(ISldWorks app, IModelDoc2 model)
        {
            var d = new JObject();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { d["applicable"] = false; d["reason"] = model == null ? "no active document" : "not a part"; return d; }
            d["applicable"] = true;

            int total = 0, suppressed = 0;
            var byType = new JObject();
            var byTypeSuppressed = new JObject();
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
                    if (sup) { suppressed++; byTypeSuppressed[tn] = ((int?)byTypeSuppressed[tn] ?? 0) + 1; }
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch (Exception ex) { d["error"] = ex.GetType().Name + ": " + ex.Message; }

            int rb = 0; try { rb = model.Extension.GetWhatsWrongCount(); } catch { }

            d["totalFeatures"] = total;                     // suppression is a state change, so this must NOT move across a run
            d["suppressedFeatures"] = suppressed;           // the delta target: run1 − run0 == handler.Changed (or −Changed for unsuppress)
            d["byTypeSuppressed"] = byTypeSuppressed;        // per-type suppressed tally (independent read-back)
            d["byType"] = byType;                            // per-type total tally
            d["rebuildErrors"] = rb;
            d["hasFeatures"] = total > 0;
            d["fingerprint"] = new JObject { ["totalFeatures"] = total, ["suppressedFeatures"] = suppressed, ["rebuildErrors"] = rb };
            return d;
        }
    }
}
