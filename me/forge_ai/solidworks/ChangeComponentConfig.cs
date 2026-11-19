using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class ChangeComponentConfigResult
    {
        public string TargetConfig;      // the config every matched instance was switched INTO
        public string TargetFilter;      // what was targeted (all / a kind / a name)
        public int Matched;              // instances resolved from the command
        public int Changed;              // instances whose referenced config flipped AND read back as the target
        public int AlreadyInState;       // instances already on the target config
        public int NoConfigOnPart;       // instances whose part has no such config (skipped, reported honestly)
        public int Failed;               // instances that should have flipped but didn't verify
        public bool Verified;            // every instance that needed changing reads back on the target config
        public int RebuildErrors;
        public bool NeedsConfirm;
        public string Question;
        public string Diag;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// ChangeComponentConfig (tool #39 change_component_config, WRITE) — switch which CONFIGURATION a component instance
    /// references, per instance. "switch all the bolts to the M8x30 configuration", "use the Variant-2 config for the
    /// housing", "change every screw to its M8 config". DISTINCT from set_active_configuration (tool 88), which activates
    /// a config on the ACTIVE document (a doc-level state); this targets one or more COMPONENTS inside an assembly.
    /// Distinct too from upsize (demo #6), which is size-AWARE (finds the next-larger size config); this is the generic
    /// primitive that switches named instances to a named config.
    ///
    /// API (proven-live on this build via Upsize demo #6): Component2.ReferencedConfiguration is a settable property;
    /// the instance re-references the named config on its part after a rebuild. FAIL CLOSED (Rule #6): each changed
    /// instance is re-read from ReferencedConfiguration and must equal the target. IDEMPOTENT (Rule #5) — a rerun finds
    /// every instance already on the target and changes nothing. UNDO is sacred (Rule #7); Forge never saves.
    /// </summary>
    public static class ChangeComponentConfig
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            // two bare M-sizes ("M6 ... M8") is a size upsize, not a named-config switch — let Upsize take it
            if (Regex.IsMatch(c, @"\bm\d+\b.*\bm\d+\b")) return false;
            if (!Regex.IsMatch(c, @"\b(switch|change|set|use|put|make)\b")) return false;
            if (!Regex.IsMatch(c, @"\b(config|configuration|configurations)\b")) return false;
            if (!Regex.IsMatch(c, @"\bto\b|\bonto\b|\buse\b")) return false;
            // a numeric target is a per-config DIMENSION edit (tool 90), not a config switch
            if (Regex.IsMatch(c, @"\bto\s+\d")) return false;
            if (Regex.IsMatch(c, @"\b(create|add|make a|new|delete|remove|list|activate|how many|suppress|rename)\b")) return false;
            // MUST name a component — this is what separates it from set_active_configuration (doc-level)
            return Regex.IsMatch(c, @"\b(component|components|part|parts|instance|instances|bolt|bolts|nut|nuts|screw|screws|washer|washers|fastener|fasteners|housing|base|shaft|gear|each|every)\b");
        }

        public static async Task<ChangeComponentConfigResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new ChangeComponentConfigResult();
            var asm = model as AssemblyDoc;
            if (asm == null) { res.Error = "Open the assembly (.SLDASM) to switch a component's configuration."; return res; }

            string cmd = (intent ?? "").ToLowerInvariant();
            await emit("Gauge", "resolving the components and the target config", "run", null);

            // ---- live top-level component set (skip suppressed — they have no part to re-config) ----
            var comps = new List<Component2>();
            foreach (var o in (asm.GetComponents(true) as object[]) ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { }
                if (!sup) comps.Add(c);
            }
            if (comps.Count == 0) { res.Error = "No resolved components in this assembly."; return res; }

            var targets = ResolveTargets(comps, cmd, out string filter);
            res.TargetFilter = filter;
            if (targets.Count == 0)
            {
                res.NeedsConfirm = true;
                res.Question = "I couldn't tell which components to re-configure. Name a kind (bolts / nuts) or say 'all components'.";
                await emit("Gauge", null, "ask", "no target matched");
                return res;
            }
            res.Matched = targets.Count;

            // ---- the target config: the one NAMED config (across the targets' parts) that appears in the command ----
            var candidates = new List<string>();
            foreach (var c in targets)
                foreach (var n in ConfigNamesOf(c))
                    if (!candidates.Contains(n)) candidates.Add(n);
            string want = candidates.FirstOrDefault(n => Regex.IsMatch(cmd, @"\b" + Regex.Escape(n.ToLowerInvariant()) + @"\b"));
            if (want == null) want = candidates.FirstOrDefault(n => cmd.Contains(n.ToLowerInvariant()));
            if (want == null)
            {
                res.NeedsConfirm = true;
                res.Question = "Which configuration? The target part(s) offer: " + string.Join(", ", candidates.Distinct().Take(8)) + ".";
                await emit("Gauge", null, "ask", "config not named");
                return res;
            }
            res.TargetConfig = want;

            await emit("Gauge", null, "done", "switching " + targets.Count + " instance(s) to '" + want + "'");
            await emit("Scribe", "re-referencing the configuration", "run", null);

            var toVerify = new List<Component2>();
            foreach (var c in targets)
            {
                var names = ConfigNamesOf(c);
                string match = names.FirstOrDefault(n => string.Equals(n, want, StringComparison.OrdinalIgnoreCase));
                if (match == null) { res.NoConfigOnPart++; continue; }   // this part has no such config — never fake it
                string cur = null; try { cur = c.ReferencedConfiguration; } catch { }
                if (string.Equals(cur, match, StringComparison.OrdinalIgnoreCase)) { res.AlreadyInState++; continue; }
                try { c.ReferencedConfiguration = match; toVerify.Add(c); } catch { res.Failed++; }
            }

            if (toVerify.Count == 0)
            {
                // nothing needed changing (all already on target) OR none of the parts carry the config
                res.Verified = res.NoConfigOnPart == 0 && res.Failed == 0;
                res.Diag = "matched=" + res.Matched + " already=" + res.AlreadyInState + " noConfig=" + res.NoConfigOnPart + " failed=" + res.Failed;
                if (res.AlreadyInState == res.Matched)
                    res.Info = "All " + res.Matched + " target component(s) already reference '" + want + "' — nothing to change.";
                else if (res.NoConfigOnPart > 0)
                    res.Error = res.NoConfigOnPart + " of " + res.Matched + " target part(s) have no '" + want + "' configuration to switch into.";
                await emit("Scribe", null, res.Verified ? "done" : "fail", res.Diag);
                return res;
            }

            try { model.EditRebuild3(); } catch { try { model.ForceRebuild3(false); } catch { } }
            res.RebuildErrors = SafeWhatsWrong(model);

            // ---- Sentinel: FAIL CLOSED — independently re-read each switched instance ----
            await emit("Sentinel", "verifying every switched instance references the target config", "run", null);
            int confirmed = 0;
            foreach (var c in toVerify)
            {
                string now = null; try { now = c.ReferencedConfiguration; } catch { }
                if (string.Equals(now, want, StringComparison.OrdinalIgnoreCase)) confirmed++;
            }
            res.Changed = confirmed;
            res.Failed += toVerify.Count - confirmed;
            res.Verified = res.Failed == 0 && res.NoConfigOnPart == 0 && res.RebuildErrors == 0 && confirmed == toVerify.Count;
            res.Diag = "matched=" + res.Matched + " changed=" + res.Changed + " already=" + res.AlreadyInState +
                       " noConfig=" + res.NoConfigOnPart + " failed=" + res.Failed + " rebuildErr=" + res.RebuildErrors;

            if (!res.Verified)
            {
                res.Error = res.NoConfigOnPart > 0 ? res.NoConfigOnPart + " target part(s) had no '" + want + "' config. " + res.Diag
                          : res.RebuildErrors > 0 ? "The switch left " + res.RebuildErrors + " rebuild error(s). " + res.Diag
                          : res.Failed + " instance(s) didn't take the config. " + res.Diag;
                await emit("Sentinel", null, "fail", res.Error);
                return res;
            }

            res.Info = "Switched " + res.Changed + " of " + res.Matched + " component(s) to the '" + want + "' configuration" +
                       (res.AlreadyInState > 0 ? " (" + res.AlreadyInState + " already there)" : "") +
                       ". One Ctrl+Z restores it; Forge didn't save.";
            await emit("Sentinel", null, "done", res.Changed + "/" + res.Matched + " now on '" + want + "' · clean");
            return res;
        }

        private static List<Component2> ResolveTargets(List<Component2> comps, string cmd, out string filter)
        {
            filter = "all";
            // a KIND word wins: "all the bolts" is every BOLT, not every component. Only treat the command as
            // all-components when no kind is named (a bare "switch all components / all parts to X").
            string kind = null;
            foreach (var kw in new[] { "bolt", "nut", "washer", "screw", "fastener", "shaft", "gear", "housing", "base" })
                if (cmd.Contains(kw)) { kind = kw; break; }

            if (kind != null)
            {
                filter = kind;
                var byKind = new List<Component2>();
                foreach (var c in comps)
                {
                    string nm = null; try { nm = (c.Name2 ?? "").ToLowerInvariant(); } catch { }
                    if (nm != null && MatchesKind(nm, kind)) byKind.Add(c);
                }
                return byKind;
            }

            // no kind -> "all / everything / the components / the parts" means every component
            if (Regex.IsMatch(cmd, @"\b(everything|all|whole|entire|every)\b") ||
                Regex.IsMatch(cmd, @"\b(component|components|part|parts|instance|instances)\b"))
            { filter = "all"; return comps; }

            return new List<Component2>();
        }

        // "fastener" is a family word — match any bolt/nut/washer/screw name
        private static bool MatchesKind(string nm, string kind)
        {
            if (kind == "fastener") return nm.Contains("bolt") || nm.Contains("nut") || nm.Contains("screw") || nm.Contains("washer") || nm.Contains("hcs") || nm.Contains("hex");
            if (kind == "bolt" || kind == "screw") { if (nm.Contains("nut") || nm.Contains("washer") || nm.Contains("plate")) return false; return nm.Contains(kind) || nm.Contains("hcs") || nm.Contains("hex") || nm.Contains("screw") || nm.Contains("bolt"); }
            return nm.Contains(kind);
        }

        private static List<string> ConfigNamesOf(Component2 c)
        {
            try { var md = c.GetModelDoc2() as IModelDoc2; if (md != null) { var ns = md.GetConfigurationNames() as string[]; if (ns != null) return ns.ToList(); } }
            catch { }
            return new List<string>();
        }

        private static int SafeWhatsWrong(IModelDoc2 model) { try { return model.Extension.GetWhatsWrongCount(); } catch { return 0; } }
    }
}
