using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class CreateConfigurationResult
    {
        public string Name;
        public int ConfigsBefore = -1;
        public int ConfigsAfter = -1;
        public bool AlreadyExists;
        public bool Verified;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 87 — create_configuration (WRITE). Adds a NEW configuration to the active part/assembly, e.g. "create a
    /// config called Variant-A". Uses IModelDoc2.AddConfiguration3. Fail-closed (Rule #6): re-reads the configuration
    /// list and confirms the count rose by exactly 1 and the new name is present. Idempotent (name exists → report,
    /// don't duplicate). Undoable; Forge never saves. The engine print-prep/Simplify uses the same primitive.
    /// </summary>
    public static class CreateConfiguration
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            return Regex.IsMatch(c, @"\b(create|add|make|new)\b") &&
                   Regex.IsMatch(c, @"\b(config|configuration|configs|configurations)\b") &&
                   !Regex.IsMatch(c, @"\b(list|show|how many|which)\b");
        }

        public static async Task<CreateConfigurationResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new CreateConfigurationResult();
            if (model == null) { res.Error = "Open a part or assembly to add a configuration."; return res; }

            // parse the name: "called X" / "named X" / the last token
            string name = null;
            var m = Regex.Match(intent ?? "", @"(?:called|named)\s+([A-Za-z0-9_\-]+)", RegexOptions.IgnoreCase);
            if (m.Success) name = m.Groups[1].Value;
            if (name == null) { var q = Regex.Match(intent ?? "", "[\"']([^\"']+)[\"']"); if (q.Success) name = q.Groups[1].Value.Trim(); }
            if (name == null) name = "Forge-Config";
            res.Name = name;

            var names0 = model.GetConfigurationNames() as string[];
            res.ConfigsBefore = names0?.Length ?? 0;
            if (names0 != null) foreach (var n in names0) if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
            {
                res.AlreadyExists = true; res.Verified = true; res.ConfigsAfter = res.ConfigsBefore;
                res.Info = "A configuration named '" + name + "' already exists — nothing to do.";
                await emit("Scribe", null, "done", "'" + name + "' already exists");
                return res;
            }

            await emit("Scribe", "creating configuration '" + name + "'", "run", null);
            try
            {
                model.AddConfiguration3(name, "created by Forge", "", 0);
            }
            catch (Exception ex) { res.Error = "Couldn't create the configuration (" + ex.GetType().Name + ") — unchanged."; await emit("Scribe", null, "fail", res.Error); return res; }
            try { model.ForceRebuild3(false); } catch { }

            // ---- Sentinel: independent re-read (fail closed) ----
            await emit("Sentinel", "verifying", "run", null);
            var names1 = model.GetConfigurationNames() as string[];
            res.ConfigsAfter = names1?.Length ?? 0;
            bool present = false; if (names1 != null) foreach (var n in names1) if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) present = true;
            res.Verified = present && res.ConfigsAfter == res.ConfigsBefore + 1;
            if (!res.Verified)
            {
                res.Error = !present ? "The new configuration isn't in the list — creation didn't take."
                          : "Config count didn't rise by 1 (" + res.ConfigsBefore + " → " + res.ConfigsAfter + ").";
                await emit("Sentinel", null, "fail", res.Error);
                return res;
            }

            await emit("Sentinel", null, "done", "config '" + name + "' created (" + res.ConfigsBefore + " → " + res.ConfigsAfter + ")");
            res.Info = "Created configuration '" + name + "' (" + res.ConfigsBefore + " → " + res.ConfigsAfter + " configs). One Ctrl+Z undoes it; Forge didn't save.";
            return res;
        }
    }
}
