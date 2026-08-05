using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class DetectFileHealthResult
    {
        public string Verdict;        // "safe" | "caution" | "do-not-touch"
        public int RebuildProblems;   // GetWhatsWrong enumeration length (errors + warnings)
        public int HardErrors;        // isWarning == false
        public int Warnings;          // isWarning == true
        public int UnknownTypes;      // features whose GetTypeName2 is null/empty/unknown
        public int FrozenFeatures;    // GetFreezeLocation (features frozen from the top)
        public List<string> Reasons = new List<string>();
        public string Diag;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 239 — detect_file_health (READ). Pre-flight before ANY write op on an unknown file: reports whether the model
    /// is safe / caution / do-not-touch based on rock-solid LIVE signals on this build — rebuild errors (hard errors vs
    /// warnings, via IModelDocExtension.GetWhatsWrong), unrecognised/unreadable feature types (GetTypeName2 null/empty),
    /// and a set freeze bar (IFeatureManager.GetFreezeLocation, part of the tree locked). Read-only. The independent GT
    /// re-derives the problem count from a per-feature IFeature.GetErrorCode2 walk (a genuinely different API path).
    /// Verdict: do-not-touch if hard errors OR unknown feature types; caution if warnings-only OR frozen features; else safe.
    /// Distinct from get_rebuild_errors (lists the errors) and the assembly doctor (a full consistency scan).
    /// </summary>
    public static class DetectFileHealth
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            // pre-flight / health / safe-to-touch vocabulary. Excludes the "rebuild errors" list (GetRebuildErrors) and
            // the assembly doctor's consistency scan by requiring a health/safety/preflight noun, not an error-list ask.
            if (Regex.IsMatch(c, @"\b(file health|health check|healthy|health of|corrupt|corruption|pre-?flight|preflight)\b")) return true;
            if (Regex.IsMatch(c, @"\bis (this|the) (file|model|part|assembly|doc|document)\b") &&
                Regex.IsMatch(c, @"\b(safe|healthy|ok to (touch|edit|work|open)|corrupt)\b")) return true;
            if (Regex.IsMatch(c, @"\b(safe|ok|okay) to (touch|edit|work on|open|modify)\b")) return true;
            // test-loop wrong-route fix (hull-diagnostic-check): "run error check on the boat hull model, looking for
            // bad geometry" names the same rebuild-error/unknown-feature-type/freeze-bar check as the health-noun
            // phrasing above, just with "error"/"geometry" vocabulary instead of "health". Doesn't require "rebuild"
            // or "feature" (unlike GetRebuildErrors.IsIntent), so it can't shadow that tool's narrower error-LIST ask.
            if (Regex.IsMatch(c, @"\b(error check|check for errors|bad geometry|geometry check|geometry error)\b")) return true;
            return false;
        }

        public static async Task<DetectFileHealthResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new DetectFileHealthResult();
            if (model == null) { res.Error = "Open a part or assembly to check its health."; return res; }
            try { model.ForceRebuild3(false); } catch { }

            await emit("Sentinel", "running a pre-flight health check", "run", null);

            // 1) Rebuild errors/warnings — enumerate names+codes+warnings (also gives the error/warning split for the verdict).
            object feats = null, codes = null, warns = null;
            bool apiRet = false;
            try { apiRet = model.Extension.GetWhatsWrong(out feats, out codes, out warns); }
            catch (Exception ex) { res.Diag = "GetWhatsWrong threw: " + ex.Message; }
            object[] fa = feats as object[];
            object[] wa = warns as object[];
            int n = fa == null ? 0 : fa.Length;
            for (int i = 0; i < n; i++)
            {
                bool warn = false; try { if (wa != null && i < wa.Length && wa[i] != null) warn = Convert.ToBoolean(wa[i]); } catch { }
                if (warn) res.Warnings++; else res.HardErrors++;
            }
            res.RebuildProblems = n;

            // 2) Unknown / unreadable feature types — a feature SolidWorks can't classify is unsafe to operate around.
            int unknown = 0;
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    if (IsUnknownType(f)) unknown++;
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch { }
            res.UnknownTypes = unknown;

            // 3) Freeze bar — GetFreezeLocation returns the last frozen feature (null if none). A set freeze bar means
            // part of the tree is locked (a partial-edit state). Presence only (0/1) — the interop has no frozen count.
            int frozen = 0;
            try { if (model.FeatureManager.GetFreezeLocation() != null) frozen = 1; } catch { frozen = 0; }
            res.FrozenFeatures = frozen;

            // Verdict.
            if (res.HardErrors > 0) res.Reasons.Add(res.HardErrors + " feature(s) fail to rebuild");
            if (res.UnknownTypes > 0) res.Reasons.Add(res.UnknownTypes + " unrecognised feature type(s)");
            if (res.Warnings > 0) res.Reasons.Add(res.Warnings + " rebuild warning(s)");
            if (res.FrozenFeatures > 0) res.Reasons.Add("the freeze bar is set (part of the tree is locked)");

            if (res.HardErrors > 0 || res.UnknownTypes > 0) res.Verdict = "do-not-touch";
            else if (res.Warnings > 0 || res.FrozenFeatures > 0) res.Verdict = "caution";
            else res.Verdict = "safe";

            res.Diag = "verdict=" + res.Verdict + " getWhatsWrong=" + n + " hardErr=" + res.HardErrors +
                       " warn=" + res.Warnings + " unknownTypes=" + unknown + " frozen=" + frozen + " apiRet=" + apiRet;

            await emit("Sentinel", null, "done", res.Verdict + (res.Reasons.Count > 0 ? " - " + string.Join(", ", res.Reasons) : ""));

            res.Info = BuildInfo(res);
            return res;
        }

        private static bool IsUnknownType(Feature f)
        {
            string tn = null;
            try { tn = f.GetTypeName2(); } catch { return true; }   // unreadable == unknown
            if (string.IsNullOrEmpty(tn)) return true;
            return tn.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ||
                   tn.Equals("UnknownFeature", StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildInfo(DetectFileHealthResult r)
        {
            string head;
            if (r.Verdict == "safe") head = "Safe to work on - the model rebuilds clean with no unreadable features.";
            else if (r.Verdict == "caution") head = "Caution - operable, but: " + string.Join("; ", r.Reasons) + ".";
            else head = "Do not touch - " + string.Join("; ", r.Reasons) + ". Fix these before any write.";
            var sb = new StringBuilder(head);
            sb.Append("\nrebuild errors: " + r.HardErrors + " | warnings: " + r.Warnings +
                      " | unreadable features: " + r.UnknownTypes + " | frozen: " + r.FrozenFeatures);
            return sb.ToString();
        }
    }
}
