using System;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class MassPropsResult
    {
        public double MassKg = -1;            // total mass (kg) — only trustworthy when MaterialAssigned
        public double VolumeMm3 = -1;
        public double SurfaceAreaMm2 = -1;
        public double[] CenterOfMassMm;       // {x,y,z} in mm
        public double DensityKgM3 = -1;
        public bool MaterialAssigned;         // false => no material on this doc/its components (Rule #4: don't fake a weight)
        public bool Verified;                 // a solid was found and a positive mass computed
        public string Info;                   // verdict-first panel line
        public string Error;
    }

    /// <summary>
    /// GetMassProps (tool #22) — READ-ONLY: mass, volume, surface area and centre of mass of the active part or
    /// assembly. "what does this weigh", "mass properties", "how heavy is it", "where's the centre of mass / CG".
    /// Never writes. Uses IModelDocExtension.CreateMassProperty (the whole-model path); the harness cross-checks the
    /// numbers against an INDEPENDENT per-body sum (GroundTruth.MeasureMassProps), which shares no code.
    /// </summary>
    public static class GetMassProps
    {
        public static bool IsMassPropsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            return System.Text.RegularExpressions.Regex.IsMatch(cmd,
                @"\b(mass\s*propert|how\s*(much|heavy)|what.*weigh|weigh|weight|\bvolume\b|surface\s+area|center\s*of\s*mass|centre\s*of\s*mass|\bcg\b|center\s*of\s*gravity|centre\s*of\s*gravity)\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        public static async Task<MassPropsResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new MassPropsResult();
            if (model == null) { res.Error = "Open a part or assembly first — there's nothing to weigh."; return res; }

            await emit("Scale", "computing mass properties", "run", null);
            try
            {
                var mp = model.Extension.CreateMassProperty();
                if (mp == null) { res.Error = "SolidWorks couldn't compute mass properties on this model."; await emit("Scale", null, "fail", "no mass property"); return res; }
                res.VolumeMm3 = mp.Volume * 1e9;          // m^3 -> mm^3
                res.SurfaceAreaMm2 = mp.SurfaceArea * 1e6; // m^2 -> mm^2
                var com = mp.CenterOfMass as double[];
                if (com != null && com.Length >= 3)
                    res.CenterOfMassMm = new[] { com[0] * 1000.0, com[1] * 1000.0, com[2] * 1000.0 };
                double volM3 = res.VolumeMm3 / 1e9;
                // mp.Mass does NOT apply the assigned material's density on this build (landmines.md) — it silently
                // computes as if density were 1 g/cm3 (water). mp.Density is the reliable field; derive mass from it.
                res.DensityKgM3 = mp.Density;
                res.MaterialAssigned = AnyMaterialAssigned(model);
                res.MassKg = res.MaterialAssigned && res.DensityKgM3 > 0 ? res.DensityKgM3 * volM3 : -1;
            }
            catch (Exception ex)
            {
                res.Error = "Mass properties failed (" + ex.GetType().Name + ") — the model may have no solid body.";
                await emit("Scale", null, "fail", ex.GetType().Name);
                return res;
            }

            if (res.VolumeMm3 <= 0)
            {
                res.Error = "No computable mass — this model has no solid body (a sketch/surface part, or an empty assembly).";
                await emit("Scale", null, "done", "no solid mass to report");
                return res;
            }

            res.Verified = true;
            res.Info = BuildInfo(res);
            await emit("Scale", null, "done",
                (res.MaterialAssigned ? Grams(res.MassKg) : "mass unknown (no material)") + " · " + Trim(res.VolumeMm3) + " mm³");
            return res;
        }

        // mass is only real once a material is assigned AND that material name actually resolves to a database
        // entry — mp.Density silently defaults to water (1000 kg/m3) both when nothing is set AND when a material
        // NAME is set but isn't linked to any material database (Database comes back empty), same trap as mp.Mass.
        // A real test-loop model (AlEx.SLDPRT, "Aluminum Extrusion") had exactly this: a material name string present
        // but Database empty, so mp.Density silently fell back to 1000 kg/m3 and the handler reported a fabricated
        // "542.697 g" shipping weight instead of admitting the density couldn't be resolved (scenario_id weight-estimate).
        private static bool AnyMaterialAssigned(IModelDoc2 model)
        {
            var pd = model as PartDoc;
            if (pd != null)
            {
                string db = null; string mat = null; try { mat = pd.GetMaterialPropertyName2("", out db); } catch { }
                return !string.IsNullOrWhiteSpace(mat) && !string.IsNullOrWhiteSpace(db);
            }
            var asm = model as AssemblyDoc;
            if (asm != null)
            {
                bool any = false;
                foreach (var o in (asm.GetComponents(true) as object[]) ?? new object[0])
                {
                    var c = o as Component2; if (c == null) continue;
                    bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                    PartDoc cpd = null; try { cpd = c.GetModelDoc2() as PartDoc; } catch { }
                    if (cpd == null) continue;
                    string db = null; string mat = null; try { mat = cpd.GetMaterialPropertyName2("", out db); } catch { }
                    if (!string.IsNullOrWhiteSpace(mat) && !string.IsNullOrWhiteSpace(db)) { any = true; break; }
                }
                return any;
            }
            return false;
        }

        private static string BuildInfo(MassPropsResult r)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("Mass ").Append(r.MaterialAssigned ? Grams(r.MassKg) : "unknown (no material assigned — would otherwise be a fake water-density guess)")
              .Append(" · volume ").Append(Trim(r.VolumeMm3)).Append(" mm³")
              .Append(" · surface ").Append(Trim(r.SurfaceAreaMm2)).Append(" mm²");
            if (r.MaterialAssigned && r.DensityKgM3 > 0) sb.Append(" · density ").Append(Trim(r.DensityKgM3)).Append(" kg/m³");
            if (r.CenterOfMassMm != null)
                sb.Append(". Centre of mass (").Append(Trim(r.CenterOfMassMm[0])).Append(", ")
                  .Append(Trim(r.CenterOfMassMm[1])).Append(", ").Append(Trim(r.CenterOfMassMm[2])).Append(") mm.");
            return sb.ToString();
        }

        // grams under 1 kg, else kg — the number, not the adjective (Character #2)
        private static string Grams(double kg) => kg < 1.0 ? Trim(kg * 1000.0) + " g" : Trim(kg) + " kg";
        private static string Trim(double v) => v.ToString("0.###");
    }
}
