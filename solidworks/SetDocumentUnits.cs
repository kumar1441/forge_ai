using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class SetDocumentUnitsResult
    {
        public string TargetLabel;
        public int UnitIntBefore = -999;
        public int UnitIntAfter = -999;
        public bool AlreadySet;
        public bool Verified;      // fail closed: an independent read-back returns exactly the target unit int
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool — set_document_units (WRITE, display). Sets the document's LINEAR unit system (mm / cm / m / inch / ft) via
    /// IModelDocExtension.SetUserPreferenceInteger(swUnitsLinear). "set the units to inches", "switch this model to mm".
    /// Common on imported models that come in the wrong display unit. Verifies fail-closed by an INDEPENDENT read-back of
    /// the linear-unit pref (the write's return code is NOT trusted). Idempotent (already that unit → no-op). Never saves.
    /// Enum ints are resolved from swLengthUnit_e at runtime, never hard-coded.
    /// </summary>
    public static class SetDocumentUnits
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            return Regex.IsMatch(c, @"\b(set|change|switch|convert)\b") &&
                   Regex.IsMatch(c, @"\bunits?\b") &&
                   Regex.IsMatch(c, @"\b(mm|millimet(?:er|re)s?|cm|centimet(?:er|re)s?|met(?:er|re)s?|inch(?:es)?|in|ft|feet|foot)\b");
        }

        public static async Task<SetDocumentUnitsResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new SetDocumentUnitsResult();
            if (model == null) { res.Error = "Open a part, assembly, or drawing to set its units."; return res; }
            string c = (intent ?? "").ToLowerInvariant();

            int target = TargetUnitInt(c, out res.TargetLabel);
            if (target == int.MinValue) { res.Error = "Which units? e.g. \"set the units to inches\" (mm, cm, m, inch, ft)."; return res; }

            res.UnitIntBefore = ReadLinearUnit(model);
            if (res.UnitIntBefore == target)
            { res.AlreadySet = true; res.Verified = true; res.UnitIntAfter = target; res.Info = "Document units are already " + res.TargetLabel + " — nothing to do."; await emit("Sentinel", null, "done", "already " + res.TargetLabel + " — no-op"); return res; }

            await emit("Scribe", "setting document units to " + res.TargetLabel, "run", null);
            bool ok = false;
            try { ok = model.Extension.SetUserPreferenceInteger((int)swUserPreferenceIntegerValue_e.swUnitsLinear, (int)swUserPreferenceOption_e.swDetailingNoOptionSpecified, target); }
            catch (Exception ex) { res.Error = "Couldn't set units (" + ex.GetType().Name + ") — unchanged."; await emit("Scribe", null, "fail", res.Error); return res; }
            try { model.GraphicsRedraw2(); } catch { }

            // ---- Sentinel: independent read-back (fail closed) ----
            await emit("Sentinel", "verifying by read-back", "run", null);
            res.UnitIntAfter = ReadLinearUnit(model);
            res.Verified = res.UnitIntAfter == target;
            if (!res.Verified) { res.Error = "Read-back unit (" + res.UnitIntAfter + ") != target " + res.TargetLabel + " (" + target + ") — change didn't stick."; await emit("Sentinel", null, "fail", res.Error); return res; }

            await emit("Sentinel", null, "done", "units now " + res.TargetLabel + " (verified by read-back)");
            res.Info = "Set document units to " + res.TargetLabel + " (display only — geometry is unchanged). Forge didn't save.";
            return res;
        }

        private static int ReadLinearUnit(IModelDoc2 model)
        {
            try { return model.Extension.GetUserPreferenceInteger((int)swUserPreferenceIntegerValue_e.swUnitsLinear, (int)swUserPreferenceOption_e.swDetailingNoOptionSpecified); }
            catch { return -999; }
        }

        // map words -> swLengthUnit_e int (resolved at runtime). Returns int.MinValue if no unit recognized.
        private static int TargetUnitInt(string c, out string label)
        {
            label = null;
            if (Regex.IsMatch(c, @"\b(millimet(?:er|re)s?|mm)\b")) { label = "mm"; return (int)swLengthUnit_e.swMM; }
            if (Regex.IsMatch(c, @"\b(centimet(?:er|re)s?|cm)\b")) { label = "cm"; return (int)swLengthUnit_e.swCM; }
            if (Regex.IsMatch(c, @"\b(inch(?:es)?|in)\b")) { label = "inches"; return (int)swLengthUnit_e.swINCHES; }
            if (Regex.IsMatch(c, @"\b(ft|feet|foot)\b")) { label = "feet"; return (int)swLengthUnit_e.swFEET; }
            if (Regex.IsMatch(c, @"\bmet(?:er|re)s?\b")) { label = "meters"; return (int)swLengthUnit_e.swMETER; }
            return int.MinValue;
        }
    }
}
