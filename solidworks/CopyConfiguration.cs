using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class CopyConfigurationResult
    {
        public string Source;
        public string NewName;
        public int CountBefore = -1;
        public int CountAfter = -1;
        public string ActiveBefore;
        public string ActiveAfter;
        public bool Created;
        public bool AlreadyExisted;
        public bool Verified;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 92 — copy_configuration (WRITE). Duplicates a NAMED configuration under a new name, which is what
    /// "make me a variant of the 500mm one" actually means — distinct from create_configuration (tool 87), which only
    /// ever copies whatever happens to be active.
    ///
    /// Landmine this handler exists to survive: AddConfiguration3 leaves the NEWLY ADDED configuration active. Copying
    /// a config must not silently switch the user out of the one they were working in, so the active configuration is
    /// captured first and restored afterwards, and the restore is verified by read-back. Idempotent; never saves.
    /// </summary>
    public static class CopyConfiguration
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            return Regex.IsMatch(c, @"\b(copy|duplicate|clone)\b") &&
                   Regex.IsMatch(c, @"\b(config|configuration|configs|configurations)\b");
        }

        public static async Task<CopyConfigurationResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new CopyConfigurationResult();
            if (model == null) { res.Error = "Open a document to copy a configuration."; return res; }

            var names = new List<string>((model.GetConfigurationNames() as string[]) ?? new string[0]);
            if (names.Count == 0) { res.Error = "This document has no configurations to copy."; return res; }
            res.CountBefore = names.Count;
            try { res.ActiveBefore = (model.ConfigurationManager.ActiveConfiguration as Configuration).Name; } catch { }

            string c = (intent ?? "").ToLowerInvariant();
            res.NewName = ExtractNewName(intent);
            if (string.IsNullOrWhiteSpace(res.NewName)) { res.Error = "What should the copy be called? Say: copy <config> as <new name>."; return res; }

            // source = the named config if the user named one, otherwise the active one (stated, never silent)
            foreach (var n in names)
                if (c.Contains(n.ToLowerInvariant()) && !string.Equals(n, res.NewName, StringComparison.OrdinalIgnoreCase))
                { if (res.Source == null || n.Length > res.Source.Length) res.Source = n; }
            if (res.Source == null) res.Source = res.ActiveBefore;
            if (string.IsNullOrEmpty(res.Source)) { res.Error = "Which configuration should I copy? This document has: " + string.Join(", ", names.ToArray()) + "."; return res; }

            // idempotent: the copy is already there
            if (names.Exists(n => string.Equals(n, res.NewName, StringComparison.OrdinalIgnoreCase)))
            {
                res.AlreadyExisted = true; res.Verified = true; res.CountAfter = res.CountBefore; res.ActiveAfter = res.ActiveBefore;
                res.Info = "'" + res.NewName + "' already exists — nothing to do.";
                await emit("Scribe", null, "done", "already exists");
                return res;
            }

            await emit("Scribe", "copying '" + res.Source + "' to '" + res.NewName + "'", "run", null);
            try
            {
                if (!string.Equals(res.Source, res.ActiveBefore, StringComparison.OrdinalIgnoreCase))
                { model.ShowConfiguration2(res.Source); model.ForceRebuild3(false); }

                model.AddConfiguration3(res.NewName, "Copy of " + res.Source + " (Forge)", "", 0);
                model.ForceRebuild3(false);

                // AddConfiguration3 leaves the NEW config active — put the user back where they were
                if (!string.IsNullOrEmpty(res.ActiveBefore))
                { model.ShowConfiguration2(res.ActiveBefore); model.ForceRebuild3(false); }
            }
            catch (Exception ex) { res.Error = "Copy failed: " + ex.Message; }

            // fail closed: independent read-back, not the call's return value
            var after = new List<string>((model.GetConfigurationNames() as string[]) ?? new string[0]);
            res.CountAfter = after.Count;
            res.Created = after.Exists(n => string.Equals(n, res.NewName, StringComparison.OrdinalIgnoreCase));
            try { res.ActiveAfter = (model.ConfigurationManager.ActiveConfiguration as Configuration).Name; } catch { }

            bool sourceSurvived = after.Exists(n => string.Equals(n, res.Source, StringComparison.OrdinalIgnoreCase));
            bool activeRestored = string.Equals(res.ActiveAfter, res.ActiveBefore, StringComparison.OrdinalIgnoreCase);
            res.Verified = res.Created && sourceSurvived && res.CountAfter == res.CountBefore + 1 && activeRestored;

            if (!res.Verified && res.Error == null)
            {
                if (!res.Created) res.Error = "SolidWorks reported no error but '" + res.NewName + "' isn't in the configuration list — nothing was copied.";
                else if (!sourceSurvived) res.Error = "'" + res.NewName + "' was created but the source '" + res.Source + "' is gone — that's not a copy.";
                else if (res.CountAfter != res.CountBefore + 1) res.Error = "Configuration count went " + res.CountBefore + " → " + res.CountAfter + ", expected " + (res.CountBefore + 1) + ".";
                else res.Error = "Copy made, but the active configuration is '" + res.ActiveAfter + "' instead of '" + res.ActiveBefore + "'.";
            }

            await emit("Scribe", null, res.Verified ? "done" : "fail",
                       res.Verified ? res.CountBefore + " → " + res.CountAfter + " configs, still in '" + res.ActiveAfter + "'" : res.Error);

            if (res.Verified)
                res.Info = "Copied '" + res.Source + "' to '" + res.NewName + "' — " + res.CountAfter + " configurations now, still in '" +
                           res.ActiveAfter + "'. Nothing saved; Ctrl+Z reverts it.";
            return res;
        }

        private static string ExtractNewName(string intent)
        {
            string s = (intent ?? "").Trim();
            var m = Regex.Match(s, "[\"'“”‘’]([^\"'“”‘’]+)[\"'“”‘’]");
            if (m.Success) return m.Groups[1].Value.Trim();
            m = Regex.Match(s, @"\b(?:as|to|called|named)\s+([A-Za-z0-9_\-\.]+)", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value.Trim();
            return null;
        }
    }
}
