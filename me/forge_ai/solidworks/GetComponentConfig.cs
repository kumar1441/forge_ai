using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public class GetComponentConfigResult
    {
        public int Total;
        public int DistinctConfigs;
        public Dictionary<string, int> ByConfig = new Dictionary<string, int>();
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool — get_component_config (READ). Reports which CONFIGURATION each component instance references, and the
    /// spread (how many distinct configs are in use across the assembly). Feeds config-switch workflows and BOM checks
    /// ("are all the bolts on the same size config"). Read-only; own referenced-config read per component.
    /// </summary>
    public static class GetComponentConfig
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            return System.Text.RegularExpressions.Regex.IsMatch(c, @"\b(which|what|list|show)\b") &&
                   System.Text.RegularExpressions.Regex.IsMatch(c, @"\bconfig(uration)?s?\b") &&
                   System.Text.RegularExpressions.Regex.IsMatch(c, @"\b(component|components|part|parts|each|instance|using|use)\b") &&
                   !System.Text.RegularExpressions.Regex.IsMatch(c, @"\b(create|add|delete|switch|activate|set|how many config)\b");
        }

        public static async Task<GetComponentConfigResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new GetComponentConfigResult();
            var asm = model as AssemblyDoc;
            if (asm == null) { res.Error = "Open the assembly (.SLDASM) to read component configurations."; return res; }

            await emit("Reader", "reading component configs", "run", null);
            foreach (var o in (asm.GetComponents(true) as object[]) ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                res.Total++;
                string cfg = null; try { cfg = c.ReferencedConfiguration; } catch { }
                cfg = string.IsNullOrEmpty(cfg) ? "(none)" : cfg;
                if (!res.ByConfig.ContainsKey(cfg)) res.ByConfig[cfg] = 0;
                res.ByConfig[cfg]++;
            }
            res.DistinctConfigs = res.ByConfig.Count;

            await emit("Reader", null, "done", res.Total + " components across " + res.DistinctConfigs + " distinct config(s)");
            if (res.Total == 0) { res.Error = "No components in this assembly."; return res; }

            var parts = new List<string>();
            foreach (var kv in res.ByConfig) parts.Add(kv.Value + "× '" + kv.Key + "'");
            res.Info = res.Total + " components use " + res.DistinctConfigs + " distinct configuration" + (res.DistinctConfigs == 1 ? "" : "s") + ": " + string.Join(", ", parts.ToArray()) + ".";
            return res;
        }
    }
}
