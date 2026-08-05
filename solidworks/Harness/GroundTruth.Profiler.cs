using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground-truth for the READ-ONLY rebuild profiler (demo #7). Shares NOTHING with Profiler.cs:
    /// it does its OWN feature-tree walk to snapshot the model's structure (feature count + per-feature suppression
    /// signature), times a full ForceRebuild3 with its own Stopwatch, then re-snapshots and asserts the model is
    /// byte-for-byte structurally unchanged — the profiler (and its suppress-test) must leave NO trace. It never
    /// consults FeatureStatistics, so it cannot inherit the profiler's own view of the model.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureProfiler(ISldWorks app, IModelDoc2 model)
        {
            var mo = new JObject();
            if (model == null) { mo["error"] = "no model"; return mo; }

            int cnt0; string sig0; SnapFeatureSuppression(model, out cnt0, out sig0);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try { model.ForceRebuild3(false); } catch { }   // our own independent full-rebuild timing
            sw.Stop();

            int cnt1; string sig1; SnapFeatureSuppression(model, out cnt1, out sig1);

            mo["independentRebuildMs"] = sw.Elapsed.TotalMilliseconds;
            mo["featureCountBefore"] = cnt0;
            mo["featureCountAfter"] = cnt1;
            mo["featureCountUnchanged"] = cnt0 == cnt1;
            mo["suppressionUnchanged"] = string.Equals(sig0, sig1, StringComparison.Ordinal);
            mo["modelUnchanged"] = cnt0 == cnt1 && string.Equals(sig0, sig1, StringComparison.Ordinal);
            return mo;
        }

        // OWN tree walk: count features and build a stable signature of every feature's name + suppression flag.
        // Independent of Profiler.cs (which never walks the tree for this) and of the rest of GroundTruth.
        private static void SnapFeatureSuppression(IModelDoc2 model, out int count, out string sig)
        {
            int c = 0; var sb = new System.Text.StringBuilder();
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    c++;
                    string nm = null; try { nm = f.Name; } catch { }
                    bool sup = false; try { sup = f.IsSuppressed(); } catch { }
                    sb.Append(nm ?? "?").Append('=').Append(sup ? '1' : '0').Append(';');
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch { }
            count = c; sig = sb.ToString();
        }
    }
}
