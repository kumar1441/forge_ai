using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class DeleteConfigurationResult
    {
        public string Name;
        public int ConfigsBefore = -1;
        public int ConfigsAfter = -1;
        public bool NotFound;
        public bool RefusedLastOrActive;
        public bool Verified;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool — delete_configuration (WRITE). Removes a named configuration. "delete the Variant-1 configuration". Won't
    /// delete the only config or the currently-active one (guards against leaving the doc invalid). Verifies by an
    /// INDEPENDENT re-read: the config count fell by 1 and the name is gone. Idempotent (name absent → nothing). Undoable.
    /// </summary>
    public static class DeleteConfiguration
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            return Regex.IsMatch(c, @"\b(delete|remove|drop)\b") &&
                   Regex.IsMatch(c, @"\b(config|configuration|configs|configurations)\b");
        }

        public static async Task<DeleteConfigurationResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new DeleteConfigurationResult();
            if (model == null) { res.Error = "Open a part or assembly to delete a configuration."; return res; }

            string name = null;
            var m = Regex.Match(intent ?? "", @"(?:delete|remove|drop)\s+(?:the\s+)?([A-Za-z0-9_\-]+)", RegexOptions.IgnoreCase);
            if (m.Success && !m.Groups[1].Value.Equals("config", StringComparison.OrdinalIgnoreCase) && !m.Groups[1].Value.Equals("configuration", StringComparison.OrdinalIgnoreCase))
                name = m.Groups[1].Value;
            var q = Regex.Match(intent ?? "", "[\"']([^\"']+)[\"']"); if (q.Success) name = q.Groups[1].Value.Trim();
            if (string.IsNullOrWhiteSpace(name)) { res.Error = "Which configuration? e.g. \"delete the Variant-1 configuration\"."; return res; }
            res.Name = name;

            var names0 = model.GetConfigurationNames() as string[];
            res.ConfigsBefore = names0?.Length ?? 0;
            bool exists = false; if (names0 != null) foreach (var n in names0) if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) exists = true;
            if (!exists)
            {
                res.NotFound = true; res.Verified = true; res.ConfigsAfter = res.ConfigsBefore;
                res.Info = "No configuration named '" + name + "' — nothing to delete.";
                await emit("Sentinel", null, "done", "not present — nothing to do");
                return res;
            }
            if (res.ConfigsBefore <= 1) { res.RefusedLastOrActive = true; res.Error = "Won't delete the only configuration — a document must keep at least one."; return res; }
            string active = null; try { active = ((Configuration)model.GetActiveConfiguration()).Name; } catch { }
            if (string.Equals(active, name, StringComparison.OrdinalIgnoreCase))
            { res.RefusedLastOrActive = true; res.Error = "'" + name + "' is the active configuration — switch to another before deleting it."; return res; }

            await emit("Scribe", "deleting configuration '" + name + "'", "run", null);
            bool ok = false;
            try { ok = model.DeleteConfiguration2(name); } catch (Exception ex) { res.Error = "Couldn't delete (" + ex.GetType().Name + ") — unchanged."; await emit("Scribe", null, "fail", res.Error); return res; }
            try { model.ForceRebuild3(false); } catch { }

            // ---- Sentinel: independent re-read (fail closed) ----
            await emit("Sentinel", "verifying", "run", null);
            var names1 = model.GetConfigurationNames() as string[];
            res.ConfigsAfter = names1?.Length ?? 0;
            bool stillThere = false; if (names1 != null) foreach (var n in names1) if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) stillThere = true;
            res.Verified = !stillThere && res.ConfigsAfter == res.ConfigsBefore - 1;
            if (!res.Verified)
            {
                res.Error = stillThere ? "The configuration is still present — delete didn't apply." : "Config count didn't fall by 1 (" + res.ConfigsBefore + " → " + res.ConfigsAfter + ").";
                await emit("Sentinel", null, "fail", res.Error);
                return res;
            }

            await emit("Sentinel", null, "done", "'" + name + "' deleted (" + res.ConfigsBefore + " → " + res.ConfigsAfter + ")");
            res.Info = "Deleted configuration '" + name + "' (" + res.ConfigsBefore + " → " + res.ConfigsAfter + " configs). One Ctrl+Z restores it; Forge didn't save.";
            return res;
        }
    }
}
