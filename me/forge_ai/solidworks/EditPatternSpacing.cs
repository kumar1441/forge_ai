using System;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class EditPatternSpacingResult
    {
        public string PatternName;
        public double SpacingBeforeMm = -1;
        public double SpacingAfterMm = -1;
        public double RequestedMm;
        public bool AlreadyAtSpacing;
        public bool Verified;
        public string Diag;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 48 — edit_pattern_spacing (WRITE). Changes a linear pattern's spacing. "set the pattern spacing to 30mm",
    /// "space them 25 apart". Reads ILinearPatternFeatureData, sets D1Spacing, applies via ModifyDefinition, verifies
    /// by INDEPENDENT re-read (fail closed). A step toward the spec's "killer" pattern-spacing-linking tool. Undoable.
    /// </summary>
    public static class EditPatternSpacing
    {
        private const double MM = 0.001;

        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            return Regex.IsMatch(c, @"\bpattern\b") &&
                   Regex.IsMatch(c, @"\b(spacing|space|apart|pitch|gap)\b") &&
                   Regex.IsMatch(c, @"\d");
        }

        public static async Task<EditPatternSpacingResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new EditPatternSpacingResult();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { res.Error = "Open a part to edit its pattern spacing."; return res; }

            var vm = Regex.Match(intent ?? "", @"(\d+(?:\.\d+)?)\s*(?:mm)?");
            if (!vm.Success || !double.TryParse(vm.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double wantMm) || wantMm <= 0)
            { res.Error = "Tell me the new spacing, e.g. \"set the pattern spacing to 30mm\"."; return res; }
            res.RequestedMm = wantMm;

            await emit("Gauge", "finding the pattern", "run", null);
            Feature pat = FindLPattern(model);
            if (pat == null) { res.Error = "No linear pattern on this part to edit."; await emit("Gauge", null, "fail", "no pattern"); return res; }
            try { res.PatternName = pat.Name; } catch { }

            var def = pat.GetDefinition() as ILinearPatternFeatureData;
            if (def == null) { res.Error = "Couldn't read the pattern definition."; return res; }
            try { res.SpacingBeforeMm = def.D1Spacing * 1000.0; } catch { }
            if (Math.Abs(res.SpacingBeforeMm - wantMm) < 0.001)
            {
                res.AlreadyAtSpacing = true; res.Verified = true; res.SpacingAfterMm = wantMm;
                res.Info = "The pattern spacing is already " + wantMm + "mm — nothing to do.";
                await emit("Sentinel", null, "done", "already " + wantMm + "mm");
                return res;
            }

            await emit("Scribe", "setting spacing to " + wantMm + "mm", "run", null);
            string diag = "";
            try { def.AccessSelections(model, null); } catch { }
            try { def.D1Spacing = wantMm * MM; } catch (Exception ex) { diag += "set:EX(" + ex.GetType().Name + ") "; }
            bool applied = false;
            try { applied = pat.ModifyDefinition(def, model, null); } catch (Exception ex) { diag += "modify:EX(" + ex.GetType().Name + ") "; }
            diag += "applied=" + applied + " ";
            try { model.ForceRebuild3(false); } catch { }
            res.Diag = diag;

            await emit("Sentinel", "verifying", "run", null);
            var d2 = FindLPattern(model)?.GetDefinition() as ILinearPatternFeatureData;
            double after = -1; if (d2 != null) { try { after = d2.D1Spacing * 1000.0; } catch { } }
            res.SpacingAfterMm = after;
            res.Verified = Math.Abs(after - wantMm) < 0.001;
            if (!res.Verified)
            {
                res.Error = "The spacing didn't change to " + wantMm + "mm (" + res.SpacingBeforeMm.ToString("0.##") + " → " + after.ToString("0.##") + "). Diag: " + diag;
                await emit("Sentinel", null, "fail", "spacing didn't take");
                return res;
            }

            await emit("Sentinel", null, "done", "spacing now " + wantMm + "mm (" + res.SpacingBeforeMm.ToString("0.##") + " → " + after.ToString("0.##") + ")");
            res.Info = "Set the pattern spacing to " + wantMm + "mm (" + res.SpacingBeforeMm.ToString("0.##") + " → " + after.ToString("0.##") + "mm). One Ctrl+Z undoes it; Forge didn't save.";
            return res;
        }

        private static Feature FindLPattern(IModelDoc2 model)
        {
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                string tn = null; try { tn = f.GetTypeName2(); } catch { }
                if (tn != null && tn.IndexOf("LPattern", StringComparison.OrdinalIgnoreCase) >= 0) return f;
                f = f.GetNextFeature() as Feature;
            }
            return null;
        }
    }
}
