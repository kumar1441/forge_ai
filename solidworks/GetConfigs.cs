using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public class ConfigsResult
    {
        public int Count = -1;
        public List<string> Names = new List<string>();
        public string Active;
        public bool Verified;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// GetConfigs (tool #6 list_configurations) — READ-ONLY: the configurations of the active part or assembly.
    /// "list the configurations", "what configs does this have", "how many configurations", "which config is active".
    /// Never writes. Uses IModelDoc2.GetConfigurationNames; the harness cross-checks the count against an INDEPENDENT
    /// IModelDoc2.GetConfigurationCount (a different API) and the active name against GetActiveConfiguration.
    /// </summary>
    public static class GetConfigs
    {
        public static bool IsConfigsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            return System.Text.RegularExpressions.Regex.IsMatch(cmd,
                @"\b(configuration|config|configs|variant)s?\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                && System.Text.RegularExpressions.Regex.IsMatch(cmd,
                @"\b(list|show|what|which|how\s*many|are\s*there|does\s*(this|it)\s*have|active|current)\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        public static async Task<ConfigsResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new ConfigsResult();
            if (model == null) { res.Error = "Open a part or assembly to list its configurations."; return res; }

            await emit("Ledger", "reading configurations", "run", null);
            try
            {
                var names = model.GetConfigurationNames() as string[];
                if (names != null) foreach (var n in names) res.Names.Add(n);
                res.Count = res.Names.Count;
                try { var ac = model.GetActiveConfiguration() as Configuration; if (ac != null) res.Active = ac.Name; } catch { }
            }
            catch (Exception ex) { res.Error = "Couldn't read configurations (" + ex.GetType().Name + ")."; return res; }

            if (res.Count <= 0) { res.Error = "This model reports no configurations."; await emit("Ledger", null, "done", "no configurations"); return res; }

            res.Verified = res.Count >= 1;
            res.Info = res.Count + " configuration" + (res.Count == 1 ? "" : "s") +
                       (res.Active != null ? " (active: " + res.Active + ")" : "") + ": " + string.Join(", ", res.Names) + ".";
            await emit("Ledger", null, "done", res.Count + " config" + (res.Count == 1 ? "" : "s") + (res.Active != null ? " · active " + res.Active : ""));
            return res;
        }
    }
}
