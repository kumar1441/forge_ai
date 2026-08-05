using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public class RenameConfigurationResult
    {
        public string OldName;
        public string NewName;
        public int ConfigCount = -1;
        public bool NotFound;
        public bool Collision;
        public bool AlreadyNamed;
        public bool Verified;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool — rename_configuration (WRITE). Renames a named configuration via IConfiguration.Name (a working setter on
    /// this build, unlike Component2.Name2). "rename the Variant-1 configuration to Prototype". Completes the config
    /// family (create/delete/switch/rename). Guards: unknown source name → ask; target name already taken → refuse;
    /// source already == target → idempotent no-op. Verifies by INDEPENDENT re-read (new name present, old gone, count
    /// unchanged). Undoable; never saves.
    /// </summary>
    public static class RenameConfiguration
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            return Regex.IsMatch(c, @"\b(rename|re-?name)\b") &&
                   Regex.IsMatch(c, @"\b(config|configuration|configs|configurations)\b");
        }

        public static async Task<RenameConfigurationResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new RenameConfigurationResult();
            if (model == null) { res.Error = "Open a part or assembly to rename a configuration."; return res; }
            string txt = intent ?? "";

            // Parse "rename <old> to <new>" / "rename config <old> to <new>", honoring quoted names first.
            string oldName = null, newName = null;
            var q = Regex.Matches(txt, "[\"']([^\"']+)[\"']");
            if (q.Count >= 2) { oldName = q[0].Groups[1].Value.Trim(); newName = q[1].Groups[1].Value.Trim(); }
            else
            {
                var m = Regex.Match(txt, @"rename\s+(?:the\s+)?(?:config(?:uration)?\s+)?([A-Za-z0-9_\-]+)(?:\s+config(?:uration)?)?\s+to\s+(?:the\s+)?([A-Za-z0-9_\-]+)", RegexOptions.IgnoreCase);
                if (m.Success) { oldName = m.Groups[1].Value.Trim(); newName = m.Groups[2].Value.Trim(); }
            }
            if (string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName))
            { res.Error = "Which configuration, and to what? e.g. \"rename the Variant-1 configuration to Prototype\"."; return res; }
            res.OldName = oldName; res.NewName = newName;

            var names0 = model.GetConfigurationNames() as string[];
            res.ConfigCount = names0?.Length ?? 0;
            bool srcExists = false, dstExists = false, srcIsDst = string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase);
            if (names0 != null) foreach (var n in names0)
            {
                if (string.Equals(n, oldName, StringComparison.OrdinalIgnoreCase)) srcExists = true;
                if (string.Equals(n, newName, StringComparison.OrdinalIgnoreCase)) dstExists = true;
            }

            if (srcIsDst) { res.AlreadyNamed = true; res.Verified = true; res.Info = "Configuration is already named '" + newName + "' — nothing to do."; await emit("Sentinel", null, "done", "already named — no-op"); return res; }
            // Idempotent: a second run sees the source already renamed to the target — nothing to do (not an error).
            if (!srcExists && dstExists) { res.AlreadyNamed = true; res.Verified = true; res.Info = "'" + oldName + "' was already renamed to '" + newName + "' — nothing to do."; await emit("Sentinel", null, "done", "already renamed — no-op"); return res; }
            if (!srcExists)
            {
                var list = names0 != null ? string.Join(", ", names0) : "";
                res.NotFound = true; res.Error = "No configuration named '" + oldName + "'. Present: " + list + ".";
                await emit("Sentinel", null, "fail", res.Error); return res;
            }
            if (dstExists) { res.Collision = true; res.Error = "A configuration named '" + newName + "' already exists — pick another name."; await emit("Sentinel", null, "fail", res.Error); return res; }

            await emit("Scribe", "renaming '" + oldName + "' → '" + newName + "'", "run", null);
            try
            {
                var cfg = model.GetConfigurationByName(oldName) as Configuration;
                if (cfg == null) { res.Error = "Couldn't open configuration '" + oldName + "' to rename."; await emit("Scribe", null, "fail", res.Error); return res; }
                cfg.Name = newName;
            }
            catch (Exception ex) { res.Error = "Couldn't rename (" + ex.GetType().Name + ") — unchanged."; await emit("Scribe", null, "fail", res.Error); return res; }
            try { model.ForceRebuild3(false); } catch { }

            // ---- Sentinel: independent re-read (fail closed) ----
            await emit("Sentinel", "verifying", "run", null);
            var names1 = model.GetConfigurationNames() as string[];
            int after = names1?.Length ?? 0;
            bool newPresent = false, oldGone = true;
            if (names1 != null) foreach (var n in names1)
            {
                if (string.Equals(n, newName, StringComparison.OrdinalIgnoreCase)) newPresent = true;
                if (string.Equals(n, oldName, StringComparison.OrdinalIgnoreCase)) oldGone = false;
            }
            res.Verified = newPresent && oldGone && after == res.ConfigCount;
            if (!res.Verified)
            {
                res.Error = !newPresent ? "The new name '" + newName + "' didn't appear — rename didn't apply."
                          : !oldGone ? "The old name '" + oldName + "' is still present — rename didn't apply."
                          : "Config count changed unexpectedly (" + res.ConfigCount + " → " + after + ").";
                await emit("Sentinel", null, "fail", res.Error); return res;
            }

            await emit("Sentinel", null, "done", "'" + oldName + "' → '" + newName + "' (" + after + " configs, unchanged count)");
            res.Info = "Renamed configuration '" + oldName + "' to '" + newName + "'. One Ctrl+Z reverts it; Forge didn't save.";
            return res;
        }
    }
}
