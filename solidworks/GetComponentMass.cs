using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public class GetComponentMassResult
    {
        public int UniqueParts;
        public double TotalMassKg;
        public string HeaviestPart;
        public double HeaviestKg = -1;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool — get_component_mass (READ). Per-UNIQUE-part mass in an assembly (kg), the total, and the heaviest part —
    /// the BOM-weight question, distinct from get_mass_properties (whole model). Reads each unique part's mass via its
    /// IMassProperty. Read-only; independent GT re-reads each part's mass by its own traversal.
    /// </summary>
    public static class GetComponentMass
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            // per-part semantics only (each/per/every/which/plural) — bare singular "the part" is get_mass_properties.
            return System.Text.RegularExpressions.Regex.IsMatch(c, @"\b(mass|weight|heavy|heaviest|weigh)\b") &&
                   System.Text.RegularExpressions.Regex.IsMatch(c, @"\b(each|per|every|which|components|parts)\b");
        }

        public static async Task<GetComponentMassResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new GetComponentMassResult();
            var asm = model as AssemblyDoc;
            if (asm == null) { res.Error = "Open the assembly (.SLDASM) for per-part mass."; return res; }

            await emit("Scale", "weighing each unique part", "run", null);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var o in (asm.GetComponents(false) as object[]) ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                string path = null; try { path = c.GetPathName(); } catch { }
                string key = string.IsNullOrEmpty(path) ? (c.Name2 ?? "") : path;
                if (!seen.Add(key)) continue;
                var pd = c.GetModelDoc2() as IModelDoc2; if (pd == null) continue;
                double kg = PartMassKg(pd);
                if (kg <= 0) continue;
                res.UniqueParts++;
                res.TotalMassKg += kg;
                if (kg > res.HeaviestKg) { res.HeaviestKg = kg; res.HeaviestPart = System.IO.Path.GetFileNameWithoutExtension(key); }
            }

            await emit("Scale", null, "done", res.UniqueParts + " unique parts · total " + res.TotalMassKg.ToString("0.###", CultureInfo.InvariantCulture) + " kg");
            if (res.UniqueParts == 0) { res.Error = "Couldn't read any part masses (no material/geometry?)."; return res; }

            res.Info = res.UniqueParts + " unique parts, total " + res.TotalMassKg.ToString("0.###", CultureInfo.InvariantCulture) +
                       " kg. Heaviest: " + res.HeaviestPart + " at " + res.HeaviestKg.ToString("0.###", CultureInfo.InvariantCulture) + " kg.";
            return res;
        }

        private static double PartMassKg(IModelDoc2 pd)
        {
            try
            {
                var mp = pd.Extension.CreateMassProperty();
                if (mp == null) return -1;
                double m = mp.Mass;
                return m;
            }
            catch { return -1; }
        }
    }
}
