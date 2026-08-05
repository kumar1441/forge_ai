using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class SetRebuildVerificationResult
    {
        public bool Requested;        // the state asked for
        public bool Before;
        public bool After;
        public string Route;          // "ISldWorks" | "ModelDocExtension" — which scope actually held the value
        public bool DocToggleBefore;  // diagnostics: the two scopes kept APART, never OR'd into the verdict
        public bool DocToggleAfter;
        public bool AppToggleBefore;
        public bool AppToggleAfter;
        public bool AlreadyDone;
        public bool Verified;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// SetRebuildVerification (tool #159 set_rebuild_verification) — the "Verification on rebuild" performance switch.
    /// ON makes SolidWorks check every new face against every existing face (catching bad geometry a normal rebuild
    /// misses, at several times the rebuild cost); OFF is the default and what shops run day to day. The honest use is a
    /// pair: turn it ON before trusting a suspect import, then straight back OFF.
    ///
    ///   Switch  — set the toggle, then READ IT BACK. MEASURED on this R2026x build: the DOCUMENT-scoped route
    ///             (IModelDocExtension.SetUserPreferenceToggle) is a SILENT NO-OP for this preference — the call
    ///             returns, the read-back never moves. The APPLICATION scope (ISldWorks) is the one that holds it, so
    ///             it is tried FIRST and the document scope is only a fallback. The two scopes are never OR'd into the
    ///             verdict: an OR reads "doc says OFF" as success for an OFF request while the application scope is
    ///             still ON, which is exactly how the first version of this handler claimed the wrong route.
    ///   Sentinel— FAIL CLOSED (Rule #6): the run is verified only if the read-back equals what was asked for.
    ///
    /// IDEMPOTENT (Rule #5): already in the requested state → "nothing to do". No geometry is touched and Forge never
    /// saves; this is a setting, so it is reported with its BEFORE value so the change is reversible by hand.
    /// </summary>
    public static class SetRebuildVerification
    {
        // NARROW: needs the verification-on-rebuild vocabulary. "rebuild the drawings" (DrawingPkg) has no verification
        // word; "fix the rebuild errors" (RedWave) is excluded outright.
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (Regex.IsMatch(c, @"\b(error|errors|red|dangling|drawing|drawings|dimension)\b")) return false;
            bool verif = Regex.IsMatch(c, @"\bverif(y|ication|ied)\b");
            bool rebuild = Regex.IsMatch(c, @"\brebuild(s|ing)?\b");
            return verif && rebuild;
        }

        public static async Task<SetRebuildVerificationResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new SetRebuildVerificationResult();
            if (model == null) { res.Error = "Open a document first."; return res; }

            string c = (intent ?? "").ToLowerInvariant();
            // the setting's own NAME contains the word "on" ("verification ON rebuild"), so strip the phrase before
            // reading the direction — otherwise every prompt looks like it says both ON and OFF.
            string scan = Regex.Replace(c, @"\bverification\s+on\s+rebuild\b|\bon\s+rebuild\b|\bon\s+every\s+rebuild\b", " ");
            bool off = Regex.IsMatch(scan, @"\b(off|disable|disabled|stop|skip|without|no)\b");
            bool on = Regex.IsMatch(scan, @"\b(on|enable|enabled|full|strict)\b");
            if (!off && !on) { res.Error = "Say whether verification on rebuild should be ON or OFF."; return res; }
            res.Requested = off ? false : true;   // an explicit OFF wins: "turn it off" is never a request to enable

            const int pref = (int)swUserPreferenceToggle_e.swPerformanceVerifyOnRebuild;

            await emit("Switch", "reading the current verification-on-rebuild setting", "run", null);
            res.DocToggleBefore = ReadDoc(model, pref);
            res.AppToggleBefore = ReadApp(app, pref);
            // EFFECTIVE state = verification is on if EITHER scope has it on. That is an honest reading of "is my
            // rebuild being verified", and it is only ever used as the BEFORE picture — never as proof of a write.
            res.Before = res.DocToggleBefore || res.AppToggleBefore;

            if (res.Before == res.Requested)
            {
                res.AlreadyDone = true;
                res.After = res.Before;
                res.Verified = true;
                res.Info = "Verification on rebuild is already " + (res.Requested ? "ON" : "OFF") + " — nothing to do.";
                await emit("Switch", null, "done", "already " + (res.Requested ? "ON" : "OFF"));
                return res;
            }

            await emit("Switch", "setting verification on rebuild " + (res.Requested ? "ON" : "OFF"), "run", null);
            // A route only counts if the scope it wrote MOVED to the requested value — a scope that already read the
            // requested value proves nothing about the write, so the read-back is compared against the scope's own
            // BEFORE value too. Application scope first: it is the one measured to hold this preference.
            try { app.SetUserPreferenceToggle(pref, res.Requested); } catch { }
            if (ReadApp(app, pref) == res.Requested) res.Route = "ISldWorks";
            if (res.Route == null || ReadDoc(model, pref) != res.Requested)
            {
                try { model.Extension.SetUserPreferenceToggle(pref, (int)swUserPreferenceOption_e.swDetailingNoOptionSpecified, res.Requested); } catch { }
                if (res.Route == null && ReadDoc(model, pref) == res.Requested) res.Route = "ModelDocExtension";
            }

            await emit("Sentinel", "reading the setting back", "run", null);
            res.DocToggleAfter = ReadDoc(model, pref);
            res.AppToggleAfter = ReadApp(app, pref);
            res.After = res.DocToggleAfter || res.AppToggleAfter;
            res.Verified = res.Route != null && res.After == res.Requested;
            if (!res.Verified)
            {
                res.Error = "SolidWorks accepted the call but the setting still reads " + (res.After ? "ON" : "OFF") +
                            " — both the document and application routes were tried and neither held the value.";
                await emit("Sentinel", null, "fail", res.Error);
                return res;
            }

            await emit("Sentinel", null, "done", "verification on rebuild: " + (res.Before ? "ON" : "OFF") + " → " + (res.After ? "ON" : "OFF") + " (via " + res.Route + ")");
            res.Info = "Verification on rebuild is now " + (res.Requested ? "ON — rebuilds get slower but every new face is checked against every existing one" : "OFF — the normal, fast setting") +
                       ". It was " + (res.Before ? "ON" : "OFF") + " before. Setting only: no geometry changed and nothing was saved.";
            return res;
        }

        private static bool ReadDoc(IModelDoc2 m, int pref)
        { try { return m.Extension.GetUserPreferenceToggle(pref, (int)swUserPreferenceOption_e.swDetailingNoOptionSpecified); } catch { return false; } }

        private static bool ReadApp(ISldWorks a, int pref)
        { try { return a.GetUserPreferenceToggle(pref); } catch { return false; } }
    }
}
