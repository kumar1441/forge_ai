using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class ConfigFeatureSuppressionResult
    {
        public string FeatureName;
        public string ConfigName;
        public string Action = "suppress";     // "suppress" | "unsuppress"
        public bool BeforeInConfig;            // feature suppressed in the TARGET config before the write
        public bool AfterInConfig;             // suppressed in the target config after
        public bool OtherConfigsUnchanged;     // the key proof: only the target config moved (per-config scoping held)
        public bool AlreadyInState;
        public bool NeedsConfirm;
        public string Question;
        public bool Verified;
        public int RebuildErrors;
        public string Diag;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// ConfigFeatureSuppression (tool #91 set_config_feature_suppression, WRITE) — suppress/unsuppress a feature in ONE
    /// named configuration, leaving the others exactly as they were. The variant/print-prep primitive: "suppress the hole
    /// in the Lightweight config", "turn the fillet off in Variant-1 only". DISTINCT from suppress_feature (tool 74),
    /// which toggles the ACTIVE config.
    ///
    /// API (proven-live family, per-config scoped): IFeature.SetSuppression2(action, swSpecifyConfiguration, string[] names)
    /// writes only the named configs; IFeature.IsSuppressed2(swSpecifyConfiguration, string[] names) reads a config's state
    /// WITHOUT switching the active configuration. NOTE the SAFEARRAY lesson (tool 52): the config-names argument must be a
    /// real string[], never a boxed object[].
    ///
    /// Named crew:
    ///   Gauge — resolve the target CONFIG (a real GetConfigurationNames entry named in the command) and the target FEATURE
    ///           (by name or type). Either unresolved → ask ONE question (Rule #2), touch nothing.
    ///   Scribe — snapshot the per-config suppression across ALL configs, set the ONE config, ForceRebuild.
    ///   Sentinel — FAIL CLOSED (Rule #6): re-read the target config's state (must equal requested) AND confirm every
    ///           OTHER config is byte-for-byte unchanged (per-config scoping actually scoped — didn't bleed to swAll).
    ///
    /// IDEMPOTENT (Rule #5); UNDO is sacred (Rule #7) — one Ctrl+Z restores it; Forge never saves.
    /// </summary>
    public static class ConfigFeatureSuppression
    {
        // NARROW: a suppress verb + a feature target + an EXPLICIT config reference. The config word is what separates this
        // from suppress_feature (active config) — placed BEFORE it in dispatch, so "suppress the hole in Variant-1" lands
        // here while "suppress the hole" stays with suppress_feature.
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            bool verb = Regex.IsMatch(c, @"\b(unsuppress|un-suppress|suppress|turn (off|on|back on)|switch (off|on)|disable|enable|restore|reactivate|deactivate)\b");
            if (!verb) return false;
            bool target = Regex.IsMatch(c, @"\b(fillet|round|chamfer|bevel|hole|cut|pocket|pattern|boss|extrude|extrusion|rib|draft|shell|cosmetic|thread|mirror|feature)s?\b");
            if (!target) return false;
            return Regex.IsMatch(c, @"\b(config|configuration|configurations)\b");
        }

        public static async Task<ConfigFeatureSuppressionResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new ConfigFeatureSuppressionResult();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { res.Error = "Per-config feature suppression works on a single part — open the .SLDPRT."; return res; }

            bool unsuppress = Regex.IsMatch((intent ?? "").ToLowerInvariant(), @"\b(unsuppress|un-suppress|turn (on|back on)|switch on|enable|restore|reactivate|bring back)\b");
            res.Action = unsuppress ? "unsuppress" : "suppress";

            await emit("Gauge", "resolving the config and the feature", "run", null);

            var configNames = (model.GetConfigurationNames() as string[]) ?? new string[0];
            if (configNames.Length < 2)
            { res.Error = "This part has only one configuration — use suppress_feature for the active config."; await emit("Gauge", null, "fail", "single config"); return res; }

            // ---- resolve the target config: a real config name that appears in the command ----
            string lc = (intent ?? "").ToLowerInvariant();
            string cfg = configNames.FirstOrDefault(n => lc.Contains(n.ToLowerInvariant()));
            if (cfg == null)
            {
                res.NeedsConfirm = true;
                res.Question = "Which configuration? This part has: " + string.Join(", ", configNames) + ".";
                await emit("Gauge", null, "fail", "config not named — asking");
                return res;
            }
            res.ConfigName = cfg;

            // ---- resolve the target feature (name-contains first, then a type keyword) ----
            var all = new List<Feature>();
            var f0 = model.FirstFeature() as Feature;
            while (f0 != null) { all.Add(f0); f0 = f0.GetNextFeature() as Feature; }
            Feature feat = ResolveFeature(all, lc);
            if (feat == null)
            {
                res.NeedsConfirm = true;
                var names = all.Select(SafeName).Where(n => !string.IsNullOrEmpty(n)).Take(10);
                res.Question = "Which feature? This part has: " + string.Join(", ", names) + ".";
                await emit("Gauge", null, "fail", "feature not resolved — asking");
                return res;
            }
            res.FeatureName = SafeName(feat);

            // ---- snapshot per-config suppression across ALL configs (for the "others unchanged" proof) ----
            var before = ReadAllConfigs(feat, configNames);
            res.BeforeInConfig = before.TryGetValue(cfg, out var bv) && bv;

            // ---- IDEMPOTENT ----
            if (res.BeforeInConfig == !unsuppress)
            {
                res.AlreadyInState = true; res.Verified = true; res.AfterInConfig = res.BeforeInConfig; res.OtherConfigsUnchanged = true;
                res.Info = "'" + res.FeatureName + "' is already " + (unsuppress ? "unsuppressed" : "suppressed") + " in " + cfg + " — nothing to do.";
                await emit("Scribe", null, "done", "already " + res.Action + "ed in " + cfg);
                return res;
            }

            await emit("Gauge", null, "done", res.Action + "ing '" + res.FeatureName + "' in " + cfg + " only");
            await emit("Scribe", "writing the per-config suppression", "run", null);

            int action = unsuppress ? (int)swFeatureSuppressionAction_e.swUnSuppressFeature : (int)swFeatureSuppressionAction_e.swSuppressFeature;
            bool applied = false;
            try { applied = feat.SetSuppression2(action, (int)swInConfigurationOpts_e.swSpecifyConfiguration, new string[] { cfg }); }
            catch (Exception ex) { res.Error = "SetSuppression2 threw (" + ex.GetType().Name + ") — the part is unchanged."; await emit("Scribe", null, "fail", res.Error); return res; }
            try { model.ForceRebuild3(false); } catch { }
            res.RebuildErrors = SafeWhatsWrong(model);
            res.Diag = "applied=" + applied + " ";

            // ---- Sentinel: FAIL CLOSED ----
            await emit("Sentinel", "verifying the target config moved and the others didn't", "run", null);
            var after = ReadAllConfigs(feat, configNames);
            res.AfterInConfig = after.TryGetValue(cfg, out var av) && av;
            res.OtherConfigsUnchanged = configNames.Where(n => n != cfg).All(n => before[n] == after[n]);
            res.Diag += "before=[" + string.Join(",", configNames.Select(n => n + ":" + (before[n] ? 1 : 0))) + "] after=[" + string.Join(",", configNames.Select(n => n + ":" + (after[n] ? 1 : 0))) + "]";

            bool targetOk = res.AfterInConfig == !unsuppress;
            res.Verified = targetOk && res.OtherConfigsUnchanged && res.RebuildErrors == 0;
            if (!res.Verified)
            {
                res.Error = !targetOk ? "The " + res.Action + " didn't take in " + cfg + " (" + res.Diag + ")."
                          : !res.OtherConfigsUnchanged ? "The change bled into other configurations — per-config scoping failed (" + res.Diag + ")."
                          : "The write left " + res.RebuildErrors + " rebuild error(s) (" + res.Diag + ").";
                await emit("Sentinel", null, "fail", res.Error);
                return res;
            }

            await emit("Sentinel", null, "done", "'" + res.FeatureName + "' " + res.Action + "ed in " + cfg + " only · others unchanged · rebuild clean");
            res.Info = (unsuppress ? "Unsuppressed" : "Suppressed") + " '" + res.FeatureName + "' in " + cfg + " only (the other " +
                       (configNames.Length - 1) + " config" + (configNames.Length - 1 == 1 ? "" : "s") + " unchanged). One Ctrl+Z restores it; Forge didn't save.";
            return res;
        }

        // per-config suppression map via IsSuppressed2(swSpecifyConfiguration, {name}) — reads each config WITHOUT switching active
        private static Dictionary<string, bool> ReadAllConfigs(Feature feat, string[] configNames)
        {
            var map = new Dictionary<string, bool>();
            foreach (var n in configNames)
            {
                bool sup = false;
                try
                {
                    var r = feat.IsSuppressed2((int)swInConfigurationOpts_e.swSpecifyConfiguration, new string[] { n });
                    if (r is System.Array a && a.Length > 0) sup = Convert.ToBoolean(a.GetValue(0));
                    else if (r is bool b) sup = b;
                }
                catch { }
                map[n] = sup;
            }
            return map;
        }

        private static Feature ResolveFeature(List<Feature> all, string lc)
        {
            // an explicit feature name token ("seed-hole", "fillet1") wins
            var m = Regex.Match(lc, @"\b([a-z][\w\-]*\d[\w\-]*|seed-hole|[a-z]+-hole)\b");
            if (m.Success)
            {
                string tok = m.Value;
                var byName = all.FirstOrDefault(fe => string.Equals(SafeName(fe), tok, StringComparison.OrdinalIgnoreCase))
                          ?? all.FirstOrDefault(fe => (SafeName(fe) ?? "").ToLowerInvariant().Contains(tok));
                if (byName != null) return byName;
            }
            // else a type keyword
            if (Regex.IsMatch(lc, @"\b(hole|holes)\b")) return all.FirstOrDefault(fe => (SafeName(fe) ?? "").IndexOf("hole", StringComparison.OrdinalIgnoreCase) >= 0 || TypeOf(fe) == "ICE");
            if (Regex.IsMatch(lc, @"\b(fillet|round)s?\b")) return all.FirstOrDefault(fe => TypeOf(fe) == "Fillet");
            if (Regex.IsMatch(lc, @"\b(chamfer|bevel)s?\b")) return all.FirstOrDefault(fe => TypeOf(fe) == "Chamfer");
            if (Regex.IsMatch(lc, @"\b(pattern)s?\b")) return all.FirstOrDefault(fe => TypeOf(fe).IndexOf("Pattern", StringComparison.OrdinalIgnoreCase) >= 0);
            if (Regex.IsMatch(lc, @"\b(boss|extrude|extrusion)s?\b")) return all.FirstOrDefault(fe => TypeOf(fe) == "Extrusion");
            return null;
        }

        private static string TypeOf(Feature f) { try { return f.GetTypeName2() ?? ""; } catch { return ""; } }
        private static string SafeName(Feature f) { try { return f?.Name; } catch { return null; } }
        private static int SafeWhatsWrong(IModelDoc2 model) { try { return model.Extension.GetWhatsWrongCount(); } catch { return 0; } }
    }
}
