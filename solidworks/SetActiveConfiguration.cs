using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class SetActiveConfigurationResult
    {
        public string Name;
        public string Before;
        public string After;
        public bool AlreadyActive;
        public bool Verified;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool — set_active_configuration (WRITE, state). Switches the active configuration. "switch to Variant-2",
    /// "activate the Default config", "show configuration X". Uses IModelDoc2.ShowConfiguration2 and verifies by
    /// reading the active configuration name back (fail closed). Idempotent (already active → nothing). Never saves.
    /// </summary>
    public static class SetActiveConfiguration
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            return (Regex.IsMatch(c, @"\b(switch|activate|show|change|set|go)\b") &&
                    Regex.IsMatch(c, @"\b(config|configuration|configs|configurations)\b") &&
                    Regex.IsMatch(c, @"\bto\b|\bactive\b|\bactivate\b")) &&
                   // "set ... to <number> ... config" is a per-config dimension edit (set_config_specific_dimension), not a
                   // config switch — a switch targets a config NAME, never a numeric value. Also exclude suppress (per-config
                   // feature suppression owns that).
                   !Regex.IsMatch(c, @"\bto\s+\d") &&
                   // a config switch that NAMES a component (bolts / a part / all components) is change_component_config
                   // (tool 39), which switches that instance's referenced config — not a doc-level active-config switch.
                   !Regex.IsMatch(c, @"\b(component|components|part|parts|instance|instances|bolt|bolts|nut|nuts|screw|screws|washer|washers|fastener|fasteners)\b") &&
                   !Regex.IsMatch(c, @"\b(create|add|make|new|delete|remove|list|how many|suppress)\b") &&
                   // handle_config_explosion (tool 255) owns "activate ALL/EVERY configuration(s)" bulk phrasing —
                   // a config-explosion guard question on a huge-config-count file, not a single named-config switch.
                   !Regex.IsMatch(c, @"\b(all|every)\b");
        }

        public static async Task<SetActiveConfigurationResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new SetActiveConfigurationResult();
            if (model == null) { res.Error = "Open a part or assembly to switch configuration."; return res; }

            // parse the config name: after to/activate/show, or quoted
            string name = null;
            var m = Regex.Match(intent ?? "", @"(?:to|activate|show|switch to)\s+(?:the\s+)?([A-Za-z0-9_\-]+)", RegexOptions.IgnoreCase);
            if (m.Success && !m.Groups[1].Value.Equals("config", StringComparison.OrdinalIgnoreCase) && !m.Groups[1].Value.Equals("configuration", StringComparison.OrdinalIgnoreCase)) name = m.Groups[1].Value;
            var q = Regex.Match(intent ?? "", "[\"']([^\"']+)[\"']"); if (q.Success) name = q.Groups[1].Value.Trim();
            if (string.IsNullOrWhiteSpace(name)) { res.Error = "Which configuration? e.g. \"switch to Variant-2\"."; return res; }
            res.Name = name;

            try { res.Before = ((Configuration)model.GetActiveConfiguration()).Name; } catch { }

            // resolve against the real list (case-insensitive)
            var names = model.GetConfigurationNames() as string[];
            string exact = null; if (names != null) foreach (var n in names) if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) exact = n;
            if (exact == null) { res.Error = "No configuration named '" + name + "'. Available: " + (names != null ? string.Join(", ", names) : "none") + "."; await emit("Gauge", null, "fail", "no config"); return res; }

            if (string.Equals(res.Before, exact, StringComparison.OrdinalIgnoreCase))
            {
                res.AlreadyActive = true; res.Verified = true; res.After = exact;
                res.Info = "'" + exact + "' is already the active configuration — nothing to do.";
                await emit("Sentinel", null, "done", "already active");
                return res;
            }

            await emit("Scribe", "switching to '" + exact + "'", "run", null);
            try { model.ShowConfiguration2(exact); } catch (Exception ex) { res.Error = "Couldn't switch (" + ex.GetType().Name + ") — unchanged."; await emit("Scribe", null, "fail", res.Error); return res; }
            try { model.EditRebuild3(); } catch { }

            await emit("Sentinel", "verifying", "run", null);
            try { res.After = ((Configuration)model.GetActiveConfiguration()).Name; } catch { }
            res.Verified = string.Equals(res.After, exact, StringComparison.OrdinalIgnoreCase);
            if (!res.Verified)
            {
                res.Error = "The active configuration didn't change to '" + exact + "' (still '" + res.After + "').";
                await emit("Sentinel", null, "fail", res.Error);
                return res;
            }

            await emit("Sentinel", null, "done", "active config: '" + res.Before + "' → '" + res.After + "'");
            res.Info = "Switched the active configuration '" + res.Before + "' → '" + res.After + "'. Forge didn't save.";
            return res;
        }
    }
}
