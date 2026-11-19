using System;
using System.Globalization;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public class GetMaterialDensityResult
    {
        public double DensityKgM3 = -1;   // computed mass / volume
        public string MaterialName;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool — get_material_density (READ). Reports a part's material density (kg/m³) — the check engineers do to confirm
    /// a model actually carries the material they think ("is this steel or did it default to water?"). Reads
    /// IMassProperty.Density (kg/m³) and reports the assigned material name alongside. Read-only; ground truth is the
    /// KNOWN physical density of the assigned material (e.g. 2700 for 6061 aluminium) — an external constant, not the
    /// handler's own math. (NOTE: mass÷volume is NOT usable here — IMassProperty.Mass does not apply material density on
    /// this 3DEXPERIENCE build, so mass/volume returns ~1; .Density is the correct path. See docs/SOLIDWORKS-GOTCHAS.md landmine.)
    /// </summary>
    public static class GetMaterialDensity
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            return System.Text.RegularExpressions.Regex.IsMatch(c, @"\bdensit(y|ies)\b") ||
                   (System.Text.RegularExpressions.Regex.IsMatch(c, @"\bmaterial\b") && System.Text.RegularExpressions.Regex.IsMatch(c, @"\b(density|heavy|kg|weight per)\b"));
        }

        public static async Task<GetMaterialDensityResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new GetMaterialDensityResult();
            var part = model as PartDoc;
            if (part == null) { res.Error = "Open a part (.SLDPRT) to read its material density."; return res; }

            await emit("Gauge", "reading material + density", "run", null);
            try { string db; res.MaterialName = part.GetMaterialPropertyName2("", out db); } catch { }
            if (string.IsNullOrWhiteSpace(res.MaterialName)) res.MaterialName = "(not assigned)";

            try
            {
                var mp = model.Extension.CreateMassProperty();
                if (mp != null) { try { mp.UseSystemUnits = true; } catch { } res.DensityKgM3 = mp.Density; }   // kg/m^3 (SI)
            }
            catch { }

            if (res.DensityKgM3 <= 0) { res.Error = "Couldn't compute density (no solid body or zero volume)."; await emit("Gauge", null, "fail", res.Error); return res; }

            await emit("Gauge", null, "done", res.MaterialName + " · " + res.DensityKgM3.ToString("0", CultureInfo.InvariantCulture) + " kg/m^3");
            res.Info = "Material '" + res.MaterialName + "' — density " + res.DensityKgM3.ToString("0", CultureInfo.InvariantCulture) + " kg/m^3.";
            return res;
        }
    }
}
