using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class GetSheetMetalPropsResult
    {
        public bool IsSheetMetal;
        public double ThicknessMm = -1;
        public double BendRadiusMm = -1;
        public double KFactor = -1;
        public int BendCount = -1;
        public bool HasFlatPattern;
        public string Source;            // which feature the numbers came from
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 171 — get_sheet_metal_properties (READ). Thickness, default bend radius, K-factor and bend count — the
    /// four numbers a shop asks for before quoting or cutting, and the inputs tools 172–174 (set thickness, validate,
    /// export flat pattern) all depend on. Numbers come from the sheet-metal feature's own definition; ground truth
    /// measures the sheet thickness from the SOLID (distance between the parallel faces of the wall), so an API
    /// parameter that disagrees with the physical wall is caught rather than trusted.
    ///
    /// On a part with no sheet-metal features it says so plainly and names what would make it work (Forge Character
    /// rule 4) rather than reporting a fake zero.
    /// </summary>
    public static class GetSheetMetalProps
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            // "sheet metal" alone is NOT enough — FlatDxf (tool 174) owns "export every sheet metal part as a flat
            // pattern dxf", and tool 172 owns "set the sheet metal thickness". This is the READ, so any export or
            // write verb hands the command straight back.
            if (Regex.IsMatch(c, @"\b(export|dxf|dwg|save|write|set|change|convert|make|flatten|unfold)\b")) return false;
            if (Regex.IsMatch(c, @"sheet\s*metal|\bk.?factor\b|\bbend radius\b|\bbend allowance\b")) return true;
            // "how thick is the gauge/sheet" — but never plain wall-thickness checks (tool 182 owns those)
            return Regex.IsMatch(c, @"\b(how thick|thickness|gauge)\b") && Regex.IsMatch(c, @"\b(sheet|metal|gauge|bend|flange)\b");
        }

        public static async Task<GetSheetMetalPropsResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new GetSheetMetalPropsResult();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { res.Error = "Open a sheet-metal part to read its properties."; return res; }

            await emit("Reader", "looking for sheet-metal features", "run", null);

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
                        ReadParams(f, tn, res);
                    }
                    if (tn.IndexOf("Bend", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        tn.IndexOf("EdgeFlange", StringComparison.OrdinalIgnoreCase) >= 0) bends++;
                }
                f = f.GetNextFeature() as Feature;
            }
            res.BendCount = res.IsSheetMetal ? bends : -1;

            if (!res.IsSheetMetal)
            {
                res.Error = "This part has no sheet-metal features — it's a solid part, so there's no thickness, bend radius or flat pattern to read. Convert it with Insert > Sheet Metal > Convert to Sheet Metal first.";
                await emit("Reader", null, "fail", "not a sheet-metal part");
                return res;
            }

            await emit("Reader", null, "done",
                (res.ThicknessMm > 0 ? res.ThicknessMm.ToString("0.###", CultureInfo.InvariantCulture) + "mm thick" : "thickness unreadable") +
                " · " + res.BendCount + " bend" + (res.BendCount == 1 ? "" : "s"));

            var sb = new StringBuilder("Sheet metal");
            if (res.ThicknessMm > 0) sb.Append(" — " + res.ThicknessMm.ToString("0.###", CultureInfo.InvariantCulture) + "mm thick");
            if (res.BendRadiusMm > 0) sb.Append(", default bend radius " + res.BendRadiusMm.ToString("0.###", CultureInfo.InvariantCulture) + "mm");
            if (res.KFactor > 0) sb.Append(", K-factor " + res.KFactor.ToString("0.###", CultureInfo.InvariantCulture));
            sb.Append(". " + res.BendCount + " bend" + (res.BendCount == 1 ? "" : "s") +
                      (res.HasFlatPattern ? ", flat pattern present." : ", no flat pattern feature."));
            if (res.ThicknessMm <= 0) sb.Append("\nThickness didn't read back from the sheet-metal feature — needs your eyes before anyone cuts from it.");
            res.Info = sb.ToString();
            return res;
        }

        private static void ReadParams(Feature f, string tn, GetSheetMetalPropsResult res)
        {
            object def = null; try { def = f.GetDefinition(); } catch { }
            if (def == null) return;

            var sm = def as ISheetMetalFeatureData;
            if (sm != null)
            {
                try { if (res.ThicknessMm <= 0) res.ThicknessMm = sm.Thickness * 1000.0; } catch { }
                try { if (res.BendRadiusMm <= 0) res.BendRadiusMm = sm.BendRadius * 1000.0; } catch { }
                try { if (res.KFactor <= 0) res.KFactor = sm.KFactor; } catch { }
                if (res.Source == null) res.Source = tn;
                return;
            }
            var bf = def as IBaseFlangeFeatureData;
            if (bf != null)
            {
                try { if (res.ThicknessMm <= 0) res.ThicknessMm = bf.Thickness * 1000.0; } catch { }
                try { if (res.BendRadiusMm <= 0) res.BendRadiusMm = bf.BendRadius * 1000.0; } catch { }
                if (res.Source == null) res.Source = tn;
            }
        }
    }
}
