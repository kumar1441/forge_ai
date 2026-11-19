using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class SuppressFeatureResult
    {
        public string Action = "suppress";   // "suppress" | "unsuppress" — what was actually requested
        public string TargetType;             // the resolved target label ("fillets", "chamfers", a feature name…)
        public int Matched;                   // features in the ACTIVE config the target resolved to
        public int Changed;                   // features NEWLY put in the requested state AND independently confirmed
        public int AlreadyInState;            // matched but already in the requested state at run start (idempotency)
        public int Failed;                    // attempted but NOT confirmed in the requested state afterward (fail closed)
        public int RebuildErrors;             // GetWhatsWrongCount after the single post-write rebuild
        public bool RolledBack;               // suppressing broke a dependent rebuild → reverted, part restored
        public bool NeedsConfirm;             // zero matches → ask ONE question, wrote nothing
        public string Question;               // the one clarifying question when NeedsConfirm
        public bool Verified;                 // fail closed: every target independently reads back in the requested state + clean rebuild
        public string Info;                   // verdict-first panel line
        public string Error;                  // honest failure text (assembly handed in, no feature tree, rolled back)
    }

    /// <summary>
    /// SuppressFeature (tools #74/#75 suppress_feature / unsuppress_feature) — a reversible FEATURE state change on a
    /// PART, in its ACTIVE configuration. "suppress all the fillets", "suppress the chamfers", "suppress &lt;FeatureName&gt;",
    /// "unsuppress the fillets", "turn off the cosmetic threads". A real FEA / print-prep primitive.
    ///
    /// DISTINCT from the Simplify handler (which suppresses cosmetic features into a NEW 'Forge-Simplified' config, leaving
    /// the active config untouched). This one suppresses the USER-SPECIFIED features in the ACTIVE config — no new config.
    /// Also distinct from suppress_components (which suppresses COMPONENTS in an assembly).
    ///
    /// Approach (named crew):
    ///   Gauge — walk the feature tree; resolve the target to a set of features by TYPE (GetTypeName2: fillets→"Fillet",
    ///           chamfers→"Chamfer", holes→"…Hole…", cuts→"…Cut…", patterns→"…Pattern…", bosses→"Extrusion"/"Boss",
    ///           threads→"CosmeticThread") OR by an exact feature NAME. Zero matches → ask ONE question naming the feature
    ///           TYPES actually present (Rule #2), suppress nothing.
    ///   Switch — per-feature try/continue: a feature already in the requested state is SKIPPED (idempotent, Rule #5);
    ///           otherwise SetSuppression2(swSuppressFeature|swUnSuppressFeature, swThisConfiguration) — ACTIVE config.
    ///           One ForceRebuild3 at the end. If suppressing broke the rebuild (a child depends on it) the whole batch is
    ///           REVERTED (each changed feature restored to its prior state) and reported honestly (Rule #4/#6).
    ///   Sentinel — FAIL CLOSED (Rule #6): after the rebuild, INDEPENDENTLY re-read each targeted feature's IsSuppressed()
    ///           and confirm it is in the requested state; Verified only when ALL confirmed AND the rebuild is clean.
    ///
    /// UNDO is sacred (Rule #7): a state change, one Ctrl+Z restores it; Forge never saves.
    /// </summary>
    public static class SuppressFeature
    {
        // "suppress/unsuppress/turn off/turn on/restore … &lt;feature type or name&gt;" — a FEATURE toggle on a PART.
        // Requires BOTH a suppress-verb AND a feature reference (a type word or the word "feature"), so it never swallows
        // component-suppress ("suppress the bolts/components" → suppress_components) or print-simplify ("simplify / print
        // prep" — no suppress verb → Simplify). Placed BEFORE suppress_components/Simplifier in Dispatch and PART-guarded.
        public static bool IsSuppressFeatureIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            bool verb = Regex.IsMatch(c, @"\b(unsuppress|un-suppress|suppress|turn (off|on|back on)|switch (off|on)|disable|enable|restore|reactivate|deactivate)\b");
            if (!verb) return false;
            bool target = Regex.IsMatch(c, @"\b(fillet|round|chamfer|bevel|hole|cut|pocket|pattern|boss|extrude|extrusion|rib|draft|shell|cosmetic|thread|mirror|feature)s?\b");
            return target;
        }

        private class Plan
        {
            public bool Suppress = true;                       // false => unsuppress
            public string Label;                               // human target label
            public bool ByName;                                // matched an exact feature name, not a type
            public List<Feature> Feats = new List<Feature>();  // the resolved feature set
            public bool HasAnyFeatures;                        // the tree has at least one feature
            public List<string> PresentTypes = new List<string>(); // top by-type tally, for the no-match question
        }

        public static async Task<SuppressFeatureResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new SuppressFeatureResult();

            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { res.Error = "Suppress/unsuppress features works on a single part — open the .SLDPRT whose features you want toggled (component suppression on an assembly is a separate command)."; return res; }

            bool unsuppress = ParseIsUnsuppress(intent);
            res.Action = unsuppress ? "unsuppress" : "suppress";

            await emit("Gauge", "resolving the target features", "run", null);
            var plan = Resolve(model, intent, unsuppress);
            res.TargetType = plan.Label;

            if (!plan.HasAnyFeatures)
            { res.Error = "This part has no feature tree (an imported dumb solid, or an empty document) — there are no features to " + res.Action + "."; await emit("Gauge", null, "fail", "no feature tree"); return res; }

            // ---- zero matches → ask ONE question naming the TYPES actually present (Rule #2), suppress nothing ----
            if (plan.Feats.Count == 0)
            {
                res.NeedsConfirm = true;
                res.Question = "No " + plan.Label + " to " + res.Action + " in this part. It has: " + string.Join(", ", plan.PresentTypes) + ". Which should I " + res.Action + "?";
                await emit("Gauge", null, "fail", "no " + plan.Label + " matched — asking");
                return res;
            }
            res.Matched = plan.Feats.Count;
            await emit("Gauge", null, "done", res.Matched + " " + plan.Label + " found — " + res.Action + "ing");

            int errBefore = SafeWhatsWrong(model);

            // ---- Switch: per-feature try/continue; skip anything already in the requested state (idempotent) ----
            await emit("Switch", res.Action + "ing features in the active config", "run", null);
            int wantAction = unsuppress
                ? (int)swFeatureSuppressionAction_e.swUnSuppressFeature
                : (int)swFeatureSuppressionAction_e.swSuppressFeature;
            var changed = new List<Feature>();       // features we actually toggled (were NOT already in-state)
            foreach (var f in plan.Feats)
            {
                bool cur = IsSup(f);
                // already in the requested state? (suppress wants cur==true; unsuppress wants cur==false → cur == !unsuppress)
                if (cur == !unsuppress) { res.AlreadyInState++; continue; }
                bool ok = false;
                try { ok = f.SetSuppression2(wantAction, (int)swInConfigurationOpts_e.swThisConfiguration, null); }
                catch { ok = false; }
                if (ok) changed.Add(f); else res.Failed++;
            }
            try { model.ForceRebuild3(false); } catch { }
            int errAfter = SafeWhatsWrong(model);
            await emit("Switch", null, "done", changed.Count + " toggled · " + res.AlreadyInState + " already " + StateWord(unsuppress));

            // ---- ROLLBACK (Rule #4/#6): the write broke a dependent rebuild → revert every feature we just toggled ----
            if (changed.Count > 0 && errAfter > errBefore)
            {
                await emit("Sentinel", "rebuild broke — reverting", "run", null);
                int revertAction = unsuppress
                    ? (int)swFeatureSuppressionAction_e.swSuppressFeature
                    : (int)swFeatureSuppressionAction_e.swUnSuppressFeature;
                foreach (var f in changed)
                { try { f.SetSuppression2(revertAction, (int)swInConfigurationOpts_e.swThisConfiguration, null); } catch { } }
                try { model.ForceRebuild3(false); } catch { }
                res.RolledBack = true;
                res.Changed = 0;
                res.RebuildErrors = SafeWhatsWrong(model);
                res.Error = res.Action == "suppress"
                    ? res.Action.Substring(0, 1).ToUpper() + res.Action.Substring(1) + "ing the " + plan.Label + " broke the rebuild — reverted; other features depend on " + (changed.Count == 1 ? "it" : "them") + ". The part is unchanged."
                    : res.Action.Substring(0, 1).ToUpper() + res.Action.Substring(1) + "ing the " + plan.Label + " broke the rebuild — reverted; the part is unchanged.";
                await emit("Sentinel", null, "fail", "reverted — part restored");
                return res;
            }

            // ---- FAIL CLOSED (Rule #6): re-read each toggled feature; it counts only if CONFIRMED in the requested state ----
            await emit("Sentinel", "verifying suppression state", "run", null);
            foreach (var f in changed)
            {
                if (IsSup(f) == !unsuppress) res.Changed++; else res.Failed++;
            }
            res.RebuildErrors = errAfter;
            res.Verified = res.Failed == 0 && res.RebuildErrors == 0
                           && (res.Changed > 0 || (res.AlreadyInState == res.Matched && res.Matched > 0));
            await emit("Sentinel", null, "done",
                res.Changed + " confirmed " + StateWord(unsuppress) +
                (res.Failed > 0 ? " · " + res.Failed + " unconfirmed" : "") +
                (res.RebuildErrors == 0 ? " · rebuild clean" : " · " + res.RebuildErrors + " rebuild flag(s)"));

            res.Info = BuildInfo(res);
            return res;
        }

        // ---- read-only preview line (Rule #3): "13 fillets found — suppressing"; null for a small/ambiguous set (run directly) ----
        public static string PreviewLine(IModelDoc2 model, string intent)
        {
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART) return null;
            bool unsuppress = ParseIsUnsuppress(intent);
            var plan = Resolve(model, intent, unsuppress);
            if (!plan.HasAnyFeatures || plan.Feats.Count == 0) return null;   // let Run() ask / report honestly
            int toDo = 0; foreach (var f in plan.Feats) if (IsSup(f) == unsuppress) toDo++;   // not-yet-in-state
            if (toDo <= 3) return null;                                       // small unambiguous op → run directly
            return toDo + " " + plan.Label + " found — " + (unsuppress ? "unsuppressing" : "suppressing") + " (in the active config; one Ctrl+Z restores, Forge won't save)";
        }

        // ================= resolution (read-only; safe to call from PreviewLine and Run) =================

        private static Plan Resolve(IModelDoc2 model, string intent, bool unsuppress)
        {
            var p = new Plan { Suppress = !unsuppress };
            string cmd = (intent ?? "").ToLowerInvariant();

            // by-type tally (for the "what's present" question) + the raw feature list
            var all = new List<Feature>();
            var byType = new Dictionary<string, int>();
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                all.Add(f);
                string tn = null; try { tn = f.GetTypeName2(); } catch { }
                if (string.IsNullOrEmpty(tn)) tn = "Unknown";
                int n; byType.TryGetValue(tn, out n); byType[tn] = n + 1;
                f = f.GetNextFeature() as Feature;
            }
            p.HasAnyFeatures = all.Count > 0;
            p.PresentTypes = byType.OrderByDescending(kv => kv.Value).Take(6).Select(kv => kv.Key + " ×" + kv.Value).ToList();

            // ---- TYPE match first (fillets/chamfers/holes/cuts/patterns/bosses/threads/…) ----
            string label; Func<string, bool> typeMatch = ResolveTypePredicate(cmd, out label);
            if (typeMatch != null)
            {
                p.Label = label;
                foreach (var feat in all)
                {
                    string tn = null; try { tn = feat.GetTypeName2(); } catch { }
                    if (tn != null && typeMatch(tn)) p.Feats.Add(feat);
                }
                return p;
            }

            // ---- exact / fuzzy feature NAME ----
            string phrase = ParseNamePhrase(cmd);
            if (!string.IsNullOrWhiteSpace(phrase))
            {
                p.ByName = true;
                p.Label = "\"" + phrase.Trim() + "\"";
                // exact (case-insensitive) name first
                foreach (var feat in all)
                {
                    string nm = null; try { nm = feat.Name; } catch { }
                    if (nm != null && string.Equals(nm.Trim(), phrase.Trim(), StringComparison.OrdinalIgnoreCase)) p.Feats.Add(feat);
                }
                // else fuzzy contains
                if (p.Feats.Count == 0)
                {
                    foreach (var feat in all)
                    {
                        string nm = null; try { nm = feat.Name; } catch { }
                        if (nm != null && nm.ToLowerInvariant().Contains(phrase.Trim())) p.Feats.Add(feat);
                    }
                }
                return p;
            }

            p.Label = "features";
            return p;
        }

        // Map a target keyword in the command to a GetTypeName2 predicate. Specific words checked first (cosmetic thread
        // before hole; fillet/chamfer before generic). Returns null if the command names no known feature type.
        private static Func<string, bool> ResolveTypePredicate(string cmd, out string label)
        {
            label = null;
            if (Regex.IsMatch(cmd, @"\b(fillet|fillets|round|rounds)\b")) { label = "fillets"; return tn => tn == "Fillet"; }
            if (Regex.IsMatch(cmd, @"\b(chamfer|chamfers|bevel|bevels)\b")) { label = "chamfers"; return tn => tn == "Chamfer"; }
            if (Regex.IsMatch(cmd, @"\b(cosmetic thread|cosmetic threads|thread|threads|tap|tapped)\b")) { label = "cosmetic threads"; return tn => tn == "CosmeticThread"; }
            if (Regex.IsMatch(cmd, @"\b(hole|holes)\b")) { label = "holes"; return tn => tn.IndexOf("Hole", StringComparison.OrdinalIgnoreCase) >= 0; }
            // NOTE: on this 3DEXPERIENCE R2026x build a cut-extrude's GetTypeName2 is "ICE" (not "Cut"/"CutExtrusion") —
            // proven by GT byType on the seeded fixture. Match it too, or "suppress/unsuppress the cuts" silently misses
            // every cut-extrude on this build (boss-extrudes remain "Extrusion").
            if (Regex.IsMatch(cmd, @"\b(cut|cuts|pocket|pockets)\b")) { label = "cuts"; return tn => tn.IndexOf("Cut", StringComparison.OrdinalIgnoreCase) >= 0 || string.Equals(tn, "ICE", StringComparison.OrdinalIgnoreCase); }
            if (Regex.IsMatch(cmd, @"\b(pattern|patterns)\b")) { label = "patterns"; return tn => tn.IndexOf("Pattern", StringComparison.OrdinalIgnoreCase) >= 0; }
            if (Regex.IsMatch(cmd, @"\b(boss|bosses|extrude|extrudes|extrusion|extrusions|pad|pads)\b")) { label = "bosses"; return tn => tn == "Extrusion" || tn.IndexOf("Boss", StringComparison.OrdinalIgnoreCase) >= 0; }
            if (Regex.IsMatch(cmd, @"\b(rib|ribs)\b")) { label = "ribs"; return tn => tn == "Rib"; }
            if (Regex.IsMatch(cmd, @"\b(draft|drafts)\b")) { label = "drafts"; return tn => tn == "Draft"; }
            if (Regex.IsMatch(cmd, @"\b(shell|shells)\b")) { label = "shells"; return tn => tn == "Shell"; }
            if (Regex.IsMatch(cmd, @"\b(mirror|mirrors)\b")) { label = "mirror features"; return tn => tn.IndexOf("Mirror", StringComparison.OrdinalIgnoreCase) >= 0; }
            return null;
        }

        // strip the verb + fillers, return the trailing name phrase ("suppress Fillet3" → "fillet3")
        private static string ParseNamePhrase(string cmd)
        {
            var m = Regex.Match(cmd,
                @"\b(?:unsuppress|un-suppress|suppress|turn (?:off|on|back on)|switch (?:off|on)|disable|enable|restore|reactivate|deactivate)\b\s+(?:all\s+the\s+|all\s+|the\s+|both\s+the\s+)?(.+)$");
            if (!m.Success) return null;
            string phrase = m.Groups[1].Value.Trim();
            phrase = Regex.Replace(phrase, @"\b(feature|features)\b", "").Trim();
            return phrase.Length == 0 ? null : phrase;
        }

        private static bool ParseIsUnsuppress(string intent)
        {
            string c = (intent ?? "").ToLowerInvariant();
            // "unsuppress" / "turn on" / "turn back on" / "restore" / "enable" / "reactivate" — but NOT "turn off".
            return Regex.IsMatch(c, @"\b(unsuppress|un-suppress|turn (on|back on)|switch on|enable|restore|reactivate|bring back)\b");
        }

        // ================= info + helpers =================

        // verdict first (Character #3), the number not the adjective (Character #2), honest about what was VERIFIED.
        private static string BuildInfo(SuppressFeatureResult r)
        {
            string state = StateWord(r.Action == "unsuppress");
            if (r.Matched > 0 && r.Changed == 0 && r.Failed == 0 && r.AlreadyInState == r.Matched)
                return "Already " + state + " all " + r.Matched + " " + r.TargetType + " — nothing to do.";

            var sb = new StringBuilder();
            string verb = r.Action == "unsuppress" ? "Unsuppressed" : "Suppressed";
            sb.Append(verb + " " + r.Changed + " " + r.TargetType + " in the active config.");
            if (r.AlreadyInState > 0) sb.Append(" " + r.AlreadyInState + " already " + state + ".");
            if (r.Failed > 0) sb.Append(" " + r.Failed + " couldn't be confirmed — left for review.");
            if (r.RebuildErrors > 0) sb.Append(" " + r.RebuildErrors + " rebuild flag(s) after — check the part.");
            sb.Append(" One Ctrl+Z restores the prior state; Forge didn't save.");
            return sb.ToString();
        }

        private static string StateWord(bool unsuppress) => unsuppress ? "unsuppressed" : "suppressed";
        private static bool IsSup(Feature f) { try { return f.IsSuppressed(); } catch { return false; } }
        private static int SafeWhatsWrong(IModelDoc2 model) { try { return model.Extension.GetWhatsWrongCount(); } catch { return 0; } }
    }
}
