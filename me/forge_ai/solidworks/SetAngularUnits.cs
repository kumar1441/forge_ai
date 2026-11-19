using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class SetAngularUnitsResult
    {
        public string TargetLabel;
        public int UnitIntBefore = -999;
        public int UnitIntAfter = -999;
        public bool AlreadySet;
        public bool Verified;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool — set_angular_units (WRITE, display). Sets the document's ANGULAR unit (degrees / radians) via
    /// IModelDocExtension.SetUserPreferenceInteger(swUnitsAngular). "set the angular units to radians". The angular
    /// sibling of set_document_units. Verifies fail-closed by an INDEPENDENT read-back (write return code not trusted).
    /// Idempotent (already that unit → no-op). Enum ints resolved from swAngleUnit_e at runtime. Never saves.
    /// </summary>
    public static class SetAngularUnits
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            return Regex.IsMatch(c, @"\b(set|change|switch|convert)\b") &&
                   Regex.IsMatch(c, @"\bangular\b") &&
                   Regex.IsMatch(c, @"\b(deg(?:rees?)?|rad(?:ians?)?)\b");
        }

        public static async Task<SetAngularUnitsResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new SetAngularUnitsResult();
            if (model == null) { res.Error = "Open a part, assembly, or drawing to set its angular units."; return res; }
            string c = (intent ?? "").ToLowerInvariant();

            int target;
            if (Regex.IsMatch(c, @"\brad(?:ians?)?\b")) { target = (int)swAngleUnit_e.swRADIANS; res.TargetLabel = "radians"; }
            else if (Regex.IsMatch(c, @"\bdeg(?:rees?)?\b")) { target = (int)swAngleUnit_e.swDEGREES; res.TargetLabel = "degrees"; }
            else { res.Error = "Which angular unit? e.g. \"set the angular units to radians\" (degrees or radians)."; return res; }

            res.UnitIntBefore = ReadAngular(model);
            if (res.UnitIntBefore == target)
            { res.AlreadySet = true; res.Verified = true; res.UnitIntAfter = target; res.Info = "Angular units are already " + res.TargetLabel + " — nothing to do."; await emit("Sentinel", null, "done", "already " + res.TargetLabel + " — no-op"); return res; }

            await emit("Scribe", "setting angular units to " + res.TargetLabel, "run", null);
            try { model.Extension.SetUserPreferenceInteger((int)swUserPreferenceIntegerValue_e.swUnitsAngular, (int)swUserPreferenceOption_e.swDetailingNoOptionSpecified, target); }
            catch (Exception ex) { res.Error = "Couldn't set angular units (" + ex.GetType().Name + ") — unchanged."; await emit("Scribe", null, "fail", res.Error); return res; }

            await emit("Sentinel", "verifying by read-back", "run", null);
            res.UnitIntAfter = ReadAngular(model);
            res.Verified = res.UnitIntAfter == target;
            if (!res.Verified) { res.Error = "Read-back angular unit (" + res.UnitIntAfter + ") != target " + res.TargetLabel + " (" + target + ") — change didn't stick."; await emit("Sentinel", null, "fail", res.Error); return res; }

            await emit("Sentinel", null, "done", "angular units now " + res.TargetLabel + " (verified)");
            res.Info = "Set angular units to " + res.TargetLabel + " (display only). Forge didn't save.";
            return res;
        }

        private static int ReadAngular(IModelDoc2 model)
        {
            try { return model.Extension.GetUserPreferenceInteger((int)swUserPreferenceIntegerValue_e.swUnitsAngular, (int)swUserPreferenceOption_e.swDetailingNoOptionSpecified); }
            catch { return -999; }
        }
    }
}
