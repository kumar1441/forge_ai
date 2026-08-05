using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class SetDecimalPlacesResult
    {
        public int Target = -1;
        public int Before = -999;
        public int After = -999;
        public bool AlreadySet;
        public bool Verified;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool — set_decimal_places (WRITE, display). Sets the linear-unit display precision (decimal places) via
    /// IModelDocExtension.SetUserPreferenceInteger(swUnitsLinearDecimalPlaces). "show 4 decimal places", "set precision
    /// to 3 decimals". Verifies fail-closed by an INDEPENDENT read-back. Idempotent (already that precision → no-op).
    /// Display-only; never saves.
    /// </summary>
    public static class SetDecimalPlaces
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            return Regex.IsMatch(c, @"\b(decimal|decimals|precision|dp)\b") &&
                   Regex.IsMatch(c, @"\b(set|show|change|use|display|to)\b") &&
                   Regex.IsMatch(c, @"\b\d+\b");
        }

        public static async Task<SetDecimalPlacesResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new SetDecimalPlacesResult();
            if (model == null) { res.Error = "Open a part, assembly, or drawing to set display precision."; return res; }
            string c = (intent ?? "");

            var m = Regex.Match(c, @"\b(\d+)\b");
            if (!m.Success) { res.Error = "How many decimal places? e.g. \"show 4 decimal places\"."; return res; }
            int target = int.Parse(m.Groups[1].Value);
            if (target < 0 || target > 8) { res.Error = "Decimal places must be between 0 and 8 (got " + target + ")."; return res; }
            res.Target = target;

            res.Before = ReadDp(model);
            if (res.Before == target)
            { res.AlreadySet = true; res.Verified = true; res.After = target; res.Info = "Display precision is already " + target + " decimals — nothing to do."; await emit("Sentinel", null, "done", "already " + target + " dp — no-op"); return res; }

            await emit("Scribe", "setting display precision to " + target + " decimals", "run", null);
            try { model.Extension.SetUserPreferenceInteger((int)swUserPreferenceIntegerValue_e.swUnitsLinearDecimalPlaces, (int)swUserPreferenceOption_e.swDetailingNoOptionSpecified, target); }
            catch (Exception ex) { res.Error = "Couldn't set precision (" + ex.GetType().Name + ") — unchanged."; await emit("Scribe", null, "fail", res.Error); return res; }

            await emit("Sentinel", "verifying by read-back", "run", null);
            res.After = ReadDp(model);
            res.Verified = res.After == target;
            if (!res.Verified) { res.Error = "Read-back precision (" + res.After + ") != target " + target + " — change didn't stick."; await emit("Sentinel", null, "fail", res.Error); return res; }

            await emit("Sentinel", null, "done", "precision now " + target + " decimals (verified)");
            res.Info = "Set linear display precision to " + target + " decimal places (display only). Forge didn't save.";
            return res;
        }

        private static int ReadDp(IModelDoc2 model)
        {
            try { return model.Extension.GetUserPreferenceInteger((int)swUserPreferenceIntegerValue_e.swUnitsLinearDecimalPlaces, (int)swUserPreferenceOption_e.swDetailingNoOptionSpecified); }
            catch { return -999; }
        }
    }
}
