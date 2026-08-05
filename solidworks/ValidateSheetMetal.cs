using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class ValidateSheetMetalResult
    {
        public bool IsSheetMetal;
        public double ThicknessMm = -1;
        public double BendRadiusMm = -1;
        public double RadiusToThickness = -1;
        public int BendCount = -1;
        public bool HasFlatPattern;
        public int RebuildErrors = -1;
        public int ChecksRun;
        public int Violations;
        public List<string> Findings = new List<string>();
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 173 — validate_sheet_metal (READ). The pre-brake lint: is the bend radius survivable for this thickness,
    /// does a flat pattern exist to cut from, and does the part rebuild clean. A shop finds these out at the press;
    /// this finds them at the desk. Every finding carries the number that produced it (Forge Character rule 2), and
    /// a clean part is reported as clean rather than padded with warnings.
    ///
    /// Read-only: the flat pattern is inspected, never toggled — un-suppressing it to "test" regeneration would be a
    /// write to the user's document.
    /// </summary>
    public static class ValidateSheetMetal
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (!Regex.IsMatch(c, @"sheet\s*metal|\bbend(s|ing)?\b|\bflat pattern\b|\bbrake\b")) return false;
            return Regex.IsMatch(c, @"\b(validate|check|verify|lint|problems?|issues?|manufacturable|bendable|will this bend|safe to)\b");
        }

        public static async Task<ValidateSheetMetalResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new ValidateSheetMetalResult();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { res.Error = "Open a sheet-metal part to validate it."; return res; }

            await emit("Sentinel", "checking the sheet-metal setup", "run", null);

            int bends = 0;
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                string tn = null; try { tn = f.GetTypeName2(); } catch { }
                if (!string.IsNullOrEmpty(tn))
                {
                    if (tn.IndexOf("FlatPattern", StringComparison.OrdinalIgnoreCase) >= 0) { res.IsSheetMetal = true; res.HasFlatPattern = true; }
                    if (tn.IndexOf("SheetMetal", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        tn.IndexOf("SMBaseFlange", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        tn.StartsWith("SM", StringComparison.Ordinal))
                    {
                        res.IsSheetMetal = true;
                        object def = null; try { def = f.GetDefinition(); } catch { }
                        var sm = def as ISheetMetalFeatureData;
                        if (sm != null)
                        {
                            try { if (res.ThicknessMm <= 0) res.ThicknessMm = sm.Thickness * 1000.0; } catch { }
                            try { if (res.BendRadiusMm <= 0) res.BendRadiusMm = sm.BendRadius * 1000.0; } catch { }
                        }
                        var bf = def as IBaseFlangeFeatureData;
                        if (bf != null)
                        {
                            try { if (res.ThicknessMm <= 0) res.ThicknessMm = bf.Thickness * 1000.0; } catch { }
                            try { if (res.BendRadiusMm <= 0) res.BendRadiusMm = bf.BendRadius * 1000.0; } catch { }
                        }
                    }
                    if (tn.IndexOf("Bend", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        tn.IndexOf("EdgeFlange", StringComparison.OrdinalIgnoreCase) >= 0) bends++;
                }
                f = f.GetNextFeature() as Feature;
            }

            if (!res.IsSheetMetal)
            {
                res.Error = "This part has no sheet-metal features — there's nothing to validate. Convert it to sheet metal first.";
                await emit("Sentinel", null, "fail", "not a sheet-metal part");
                return res;
            }
            res.BendCount = bends;
            try { res.RebuildErrors = model.Extension.GetWhatsWrongCount(); } catch { }

            // --- check 1: bend radius vs thickness. Below 1x thickness a mild-steel bend cracks on the outer fibre. ---
            res.ChecksRun++;
            if (res.ThicknessMm > 0 && res.BendRadiusMm > 0)
            {
                res.RadiusToThickness = res.BendRadiusMm / res.ThicknessMm;
                if (res.RadiusToThickness < 1.0)
                {
                    res.Violations++;
                    res.Findings.Add("Bend radius " + Mm(res.BendRadiusMm) + " is only " + res.RadiusToThickness.ToString("0.##", CultureInfo.InvariantCulture) +
                                     "x the " + Mm(res.ThicknessMm) + " thickness — under 1x the outer fibre cracks on mild steel. Open it to " + Mm(res.ThicknessMm) + " or softer material.");
                }
            }
            else res.Findings.Add("Thickness or bend radius wouldn't read back — can't judge the bend ratio. Needs your eyes.");

            // --- check 2: a flat pattern to cut from ---
            res.ChecksRun++;
            if (!res.HasFlatPattern) { res.Violations++; res.Findings.Add("No flat-pattern feature — the laser/punch has nothing to cut from."); }

            // --- check 3: the part rebuilds clean ---
            res.ChecksRun++;
            if (res.RebuildErrors > 0) { res.Violations++; res.Findings.Add(res.RebuildErrors + " rebuild error" + (res.RebuildErrors == 1 ? "" : "s") + " — the flat pattern can't be trusted until they're cleared."); }

            await emit("Sentinel", null, "done", res.Violations + " of " + res.ChecksRun + " checks failed");

            var sb = new StringBuilder();
            if (res.Violations == 0)
                sb.Append("Sheet metal is sound — " + res.ChecksRun + " checks, 0 violations. " + Mm(res.ThicknessMm) + " thick, bend radius " +
                          Mm(res.BendRadiusMm) + " (" + res.RadiusToThickness.ToString("0.##", CultureInfo.InvariantCulture) + "x thickness), " +
                          res.BendCount + " bend" + (res.BendCount == 1 ? "" : "s") + ", flat pattern present.");
            else
            {
                sb.Append(res.Violations + " of " + res.ChecksRun + " checks failed:");
                foreach (var v in res.Findings) sb.Append("\n• " + v);
            }
            res.Info = sb.ToString();
            return res;
        }

        private static string Mm(double v) { return v > 0 ? v.ToString("0.###", CultureInfo.InvariantCulture) + "mm" : "?"; }
    }
}
