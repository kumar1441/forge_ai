using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class SetDraftingStandardResult
    {
        public string Target;
        public int TargetCode = -1;
        public string Before;
        public string After;
        public bool AlreadySet;
        public bool Verified;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 232 — set_document_properties (WRITE, display), DRAFTING STANDARD slice. Units/precision are already
    /// covered by SetDocumentUnits/SetDecimalPlaces/SetAngularUnits — this handler covers the genuinely uncovered
    /// piece: the dimensioning/drafting standard (ANSI/ISO/DIN/JIS/BS/GOST/GB) via
    /// IModelDocExtension.SetUserPreferenceInteger(swDetailingDimensionStandard). Enum values DUMPED from the
    /// interop, not guessed (swDetailingStandard_e: ANSI=1, ISO=2, DIN=3, JIS=4, BS=5, GOST=6, GB=7, UserDefined=8) —
    /// same discipline as the swSuppressionChangeOk=2 / swDimensionDriven=1 landmines. Verifies fail-closed by an
    /// INDEPENDENT read-back. Idempotent (already that standard → no-op). Display-only; never saves.
    /// </summary>
    public static class SetDraftingStandard
    {
        private static readonly (string word, swDetailingStandard_e code)[] Standards =
        {
            ("ansi", swDetailingStandard_e.swDetailingStandardANSI),
            ("iso", swDetailingStandard_e.swDetailingStandardISO),
            ("din", swDetailingStandard_e.swDetailingStandardDIN),
            ("jis", swDetailingStandard_e.swDetailingStandardJIS),
            ("bs", swDetailingStandard_e.swDetailingStandardBS),
            ("british standard", swDetailingStandard_e.swDetailingStandardBS),
            ("gost", swDetailingStandard_e.swDetailingStandardGOST),
            ("gb", swDetailingStandard_e.swDetailingStandardGB),
        };

        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            bool verb = Regex.IsMatch(c, @"\b(set|switch|change|use|make it|convert)\b");
            bool noun = Regex.IsMatch(c, @"\b(drafting|dimensioning|dimension)\s+standard\b") || Regex.IsMatch(c, @"\bstandard\s+to\b");
            bool hasStd = FindStandard(c).HasValue;
            return hasStd && (verb || noun);
        }

        public static async Task<SetDraftingStandardResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new SetDraftingStandardResult();
            if (model == null) { res.Error = "Open a part, assembly, or drawing to set the drafting standard."; return res; }
            string c = (intent ?? "").ToLowerInvariant();

            var pick = FindStandard(c);
            if (!pick.HasValue) { res.Error = "Which drafting standard? e.g. \"set the drafting standard to ISO\" (ANSI/ISO/DIN/JIS/BS/GOST/GB)."; return res; }
            res.Target = pick.Value.word.ToUpperInvariant();
            res.TargetCode = (int)pick.Value.code;

            res.Before = ReadStandard(model);
            if (string.Equals(res.Before, res.Target, StringComparison.OrdinalIgnoreCase))
            {
                res.AlreadySet = true; res.Verified = true; res.After = res.Before;
                res.Info = "Drafting standard is already " + res.Target + " — nothing to do.";
                await emit("Sentinel", null, "done", "already " + res.Target + " — no-op");
                return res;
            }

            await emit("Scribe", "setting drafting standard to " + res.Target, "run", null);
            try { model.Extension.SetUserPreferenceInteger((int)swUserPreferenceIntegerValue_e.swDetailingDimensionStandard, (int)swUserPreferenceOption_e.swDetailingNoOptionSpecified, res.TargetCode); }
            catch (Exception ex) { res.Error = "Couldn't set drafting standard (" + ex.GetType().Name + ") — unchanged."; await emit("Scribe", null, "fail", res.Error); return res; }

            await emit("Sentinel", "verifying by read-back", "run", null);
            res.After = ReadStandard(model);
            res.Verified = string.Equals(res.After, res.Target, StringComparison.OrdinalIgnoreCase);
            if (!res.Verified) { res.Error = "Read-back standard (" + res.After + ") != target " + res.Target + " — change didn't stick."; await emit("Sentinel", null, "fail", res.Error); return res; }

            await emit("Sentinel", null, "done", "drafting standard now " + res.Target + " (verified)");
            res.Info = "Set the drafting/dimensioning standard to " + res.Target + " (display only). Forge didn't save.";
            return res;
        }

        private static (string word, swDetailingStandard_e code)? FindStandard(string c)
        {
            foreach (var s in Standards)
                if (Regex.IsMatch(c, @"\b" + Regex.Escape(s.word) + @"\b")) return s;
            return null;
        }

        private static string ReadStandard(IModelDoc2 model)
        {
            try
            {
                int v = model.Extension.GetUserPreferenceInteger((int)swUserPreferenceIntegerValue_e.swDetailingDimensionStandard, (int)swUserPreferenceOption_e.swDetailingNoOptionSpecified);
                foreach (var s in Standards) if ((int)s.code == v) return s.word.ToUpperInvariant();
                return "code" + v;
            }
            catch { return "?"; }
        }
    }
}
