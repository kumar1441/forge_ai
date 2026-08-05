using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class ProfilerResult
    {
        public string TopFeature;        // name of the slowest feature/pattern
        public string TopFeatureType;    // its SW type name (e.g. LPattern, CirPattern)
        public double TopPercent;        // its share of total rebuild time (%)
        public double TopTimeMs;          // its own rebuild time (ms)
        public double TotalTimeMs;        // total rebuild time (ms)
        public int FeatureCount;
        public bool SuppressConfirmed;    // a suppress-test actually ran and produced a measured share
        public double SuppressDeltaPercent; // share of rebuild time recovered by suppressing the suspect (measured, independent of stats)
        public bool Reverted = true;      // the model was restored to its pre-test state (Rule #7)
        public string Info;
        public string Error;
        public List<string> Diag = new List<string>();
    }

    /// <summary>
    /// Profiler — READ-ONLY rebuild-time profiler (demo #7 "why is this model slow?"). Reads per-feature rebuild
    /// timing from IFeatureManager.FeatureStatistics (the Performance Evaluation API — IFeature.GetTimingData does
    /// NOT exist on this 3DEXPERIENCE build), finds the single feature/pattern consuming the largest share of the
    /// rebuild, and OPTIONALLY confirms it with a suppress-test (suppress the suspect, time a rebuild, compare) that
    /// is ALWAYS reverted. Net effect is read-only: no feature is added/removed, no suppression state is left changed,
    /// the document is never saved. GroundTruth.MeasureProfiler independently proves the model is unchanged.
    /// </summary>
    public static class Profiler
    {
        public static bool IsProfileIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            return Regex.IsMatch(cmd,
                @"\b(why.*(slow|sluggish|laggy)|what.?s? (slowing|taking so long)|slow (rebuild|model|part|assembly)|rebuild (time|slow|speed|profile|performance)|profile.*rebuild|bottleneck|speed (this|it) up|performance)\b");
        }

        public static async Task<ProfilerResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new ProfilerResult();
            if (model == null) { res.Error = "Open a part or assembly to profile."; return res; }

            await emit("Profiler", "timing the rebuild", "run", null);

            // INSTRUMENT BEFORE THEORISING (the tool-157 lesson): capture WHY the timing API failed rather than
            // reporting a generic "not available" that could mean null, an exception, or an empty result.
            IFeatureStatistics stats = null;
            int docType = 0; try { docType = (int)model.GetType(); } catch { }
            res.Diag.Add("docType=" + docType);
            try { stats = model.FeatureManager.FeatureStatistics; }
            catch (Exception ex) { res.Diag.Add("FeatureStatistics threw: " + ex.Message); }
            if (stats == null)
            {
                res.Diag.Add("FeatureStatistics returned NULL");
                res.Error = "Rebuild timing isn't available on this model.";
                return res;
            }
            try { stats.Refresh(); }             // gathers per-feature timing (an internal rebuild; features untouched)
            catch (Exception ex) { res.Diag.Add("Refresh threw: " + ex.Message); }

            string[] names = ToS(TryGet(() => stats.FeatureNames));
            string[] types = ToS(TryGet(() => stats.FeatureTypes));
            double[] times = ToD(TryGet(() => stats.FeatureUpdateTimes));            // seconds
            double[] pcts = ToD(TryGet(() => stats.FeatureUpdatePercentageTimes));   // % of total
            object[] feats = ToObjArr(TryGet(() => stats.Features));
            double totalSec = 0; try { totalSec = stats.TotalRebuildTime; } catch { }
            try { res.FeatureCount = stats.FeatureCount; } catch { }
            res.TotalTimeMs = totalSec * 1000.0;

            int n = names == null ? 0 : names.Length;
            res.Diag.Add("stats: names=" + (names == null ? "null" : names.Length.ToString()) +
                         " types=" + (types == null ? "null" : types.Length.ToString()) +
                         " times=" + (times == null ? "null" : times.Length.ToString()) +
                         " pcts=" + (pcts == null ? "null" : pcts.Length.ToString()) +
                         " feats=" + (feats == null ? "null" : feats.Length.ToString()) +
                         " totalSec=" + totalSec.ToString("F4") + " featureCount=" + res.FeatureCount);
            if (n == 0) { res.Error = "No timed features in this model — nothing to profile."; return res; }

            // top feature = largest share. Prefer SW's own percentage; fall back to time/total.
            int topIdx = -1; double topScore = -1;
            for (int i = 0; i < n; i++)
            {
                double score = (pcts != null && i < pcts.Length) ? pcts[i]
                             : (times != null && i < times.Length && totalSec > 0 ? times[i] / totalSec * 100.0 : 0);
                if (score > topScore) { topScore = score; topIdx = i; }
            }
            if (topIdx < 0) { res.Error = "Couldn't rank the features by rebuild time."; return res; }

            res.TopFeature = names[topIdx];
            // FeatureTypes comes back as INTEGER type codes on this build ("22"), which is meaningless in a report.
            // Prefer the feature's own GetTypeName2() ("LPattern") and keep the numeric code only as a fallback.
            res.TopFeatureType = (types != null && topIdx < types.Length) ? types[topIdx] : "";
            {
                Feature tf = (feats != null && topIdx < feats.Length) ? feats[topIdx] as Feature : null;
                if (tf == null) { try { tf = FindFeatureByName(model, res.TopFeature); } catch { } }
                string tn = null; try { if (tf != null) tn = tf.GetTypeName2(); } catch { }
                if (!string.IsNullOrEmpty(tn)) res.TopFeatureType = tn;
            }
            res.TopPercent = (pcts != null && topIdx < pcts.Length) ? pcts[topIdx] : topScore;
            res.TopTimeMs = (times != null && topIdx < times.Length) ? times[topIdx] * 1000.0
                          : res.TotalTimeMs * res.TopPercent / 100.0;

            await emit("Profiler", null, "done",
                res.TopFeature + " = " + res.TopPercent.ToString("F0") + "% of rebuild (" +
                FmtT(res.TopTimeMs) + " of " + FmtT(res.TotalTimeMs) + ")");

            // top-3 breakdown as separate diagnostic lines (null agent => each renders on its own)
            foreach (var line in TopN(names, times, pcts, totalSec, 3))
            { res.Diag.Add(line); await emit(null, null, "done", "▸ " + line); }

            // ---- suppress-test confirmation (Rule #6 verify with real behaviour), ALWAYS reverted (Rule #7) ----
            Feature topF = (feats != null && topIdx < feats.Length) ? feats[topIdx] as Feature : null;
            if (topF == null) { try { topF = FindFeatureByName(model, res.TopFeature); } catch { } }
            if (topF != null && res.TopPercent >= 5.0)
            {
                await emit("Sentinel", "confirming with a suppress-test", "run", null);
                await SuppressConfirm(model, topF, res);
                if (res.SuppressConfirmed)
                    await emit("Sentinel", null, "done",
                        "suppressing it recovers " + res.SuppressDeltaPercent.ToString("F0") + "% of rebuild time" +
                        (res.Reverted ? " · restored, model unchanged" : " · WARNING: restore check failed"));
                else
                    await emit("Sentinel", null, "done", "suppress-test skipped (feature not suppressible)" + (res.Reverted ? "" : " · restore check failed"));
            }

            res.Info = "Slowest: '" + res.TopFeature + "'" + (string.IsNullOrEmpty(res.TopFeatureType) ? "" : " (" + res.TopFeatureType + ")") +
                       " = " + res.TopPercent.ToString("F0") + "% of the " + FmtT(res.TotalTimeMs) + " rebuild (" + FmtT(res.TopTimeMs) + ")." +
                       (res.SuppressConfirmed ? " Suppress-test confirms (" + res.SuppressDeltaPercent.ToString("F0") + "% recovered)." : "") +
                       (res.Reverted ? "" : " NOTE: suppress-test restore could not be verified — reopen the model to be safe.");
            return res;
        }

        // Suppress the suspect, time a full rebuild, compare to a baseline rebuild time; then ALWAYS restore the
        // original suppression state and re-verify it. Independent of FeatureStatistics' own numbers.
        private static async Task SuppressConfirm(IModelDoc2 model, Feature topF, ProfilerResult res)
        {
            bool orig;
            try { orig = topF.IsSuppressed(); } catch { res.Reverted = false; return; }
            if (orig) return;   // already suppressed — a suppress-test would tell us nothing and could change other state

            try
            {
                var b = System.Diagnostics.Stopwatch.StartNew();
                try { model.ForceRebuild3(false); } catch { }
                b.Stop(); double baseMs = b.Elapsed.TotalMilliseconds;

                bool sup = false;
                try { sup = topF.SetSuppression2((int)swFeatureSuppressionAction_e.swSuppressFeature, (int)swInConfigurationOpts_e.swThisConfiguration, null); }
                catch { }
                if (sup)
                {
                    var s = System.Diagnostics.Stopwatch.StartNew();
                    try { model.ForceRebuild3(false); } catch { }
                    s.Stop(); double supMs = s.Elapsed.TotalMilliseconds;
                    if (baseMs > 0)
                    {
                        res.SuppressDeltaPercent = Math.Max(0, (baseMs - supMs) / baseMs * 100.0);
                        res.SuppressConfirmed = true;
                    }
                }
            }
            catch { }
            finally
            {
                // RESTORE — leave the model exactly as found (Rule #7). Never save.
                try { topF.SetSuppression2((int)swFeatureSuppressionAction_e.swUnSuppressFeature, (int)swInConfigurationOpts_e.swThisConfiguration, null); } catch { }
                try { model.ForceRebuild3(false); } catch { }
                bool now = true; try { now = topF.IsSuppressed(); } catch { }
                res.Reverted = now == orig;
            }
            await Task.CompletedTask;
        }

        // top-N features by share, as human lines
        private static List<string> TopN(string[] names, double[] times, double[] pcts, double totalSec, int k)
        {
            var idx = new List<int>();
            for (int i = 0; i < names.Length; i++) idx.Add(i);
            idx.Sort((a, c) => Score(c, times, pcts, totalSec).CompareTo(Score(a, times, pcts, totalSec)));
            var outp = new List<string>();
            for (int i = 0; i < Math.Min(k, idx.Count); i++)
            {
                int j = idx[i];
                double p = Score(j, times, pcts, totalSec);
                double tms = (times != null && j < times.Length) ? times[j] * 1000.0 : totalSec * 1000.0 * p / 100.0;
                outp.Add(names[j] + " — " + p.ToString("F0") + "% (" + FmtT(tms) + ")");
            }
            return outp;
        }
        private static double Score(int i, double[] times, double[] pcts, double totalSec)
        {
            if (pcts != null && i < pcts.Length) return pcts[i];
            if (times != null && i < times.Length && totalSec > 0) return times[i] / totalSec * 100.0;
            return 0;
        }

        private static Feature FindFeatureByName(IModelDoc2 model, string name)
        {
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                string nm = null; try { nm = f.Name; } catch { }
                if (nm == name) return f;
                f = f.GetNextFeature() as Feature;
            }
            return null;
        }

        private static string FmtT(double ms)
        { return ms >= 1000.0 ? (ms / 1000.0).ToString("F2") + "s" : ms.ToString("F0") + "ms"; }

        private static object TryGet(Func<object> g) { try { return g(); } catch { return null; } }

        private static double[] ToD(object o)
        {
            if (o is double[] d) return d;
            var oa = ToObjArr(o); if (oa == null) return null;
            var r = new double[oa.Length];
            for (int i = 0; i < oa.Length; i++) { try { r[i] = Convert.ToDouble(oa[i]); } catch { } }
            return r;
        }
        private static string[] ToS(object o)
        {
            if (o is string[] s) return s;
            var oa = ToObjArr(o); if (oa == null) return null;
            var r = new string[oa.Length];
            for (int i = 0; i < oa.Length; i++) r[i] = oa[i]?.ToString();
            return r;
        }
        private static object[] ToObjArr(object o)
        {
            if (o == null) return null;
            if (o is object[] oa) return oa;
            var arr = o as Array; if (arr == null) return null;
            var r = new object[arr.Length];
            for (int i = 0; i < arr.Length; i++) r[i] = arr.GetValue(i);
            return r;
        }
    }
}
