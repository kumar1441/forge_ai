using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class BatchRenameRow
    {
        public string Old;
        public string New;
        public bool Applied;
        public string Skipped;   // reason, when Applied is false
    }

    public class BatchRenameFeaturesResult
    {
        public string Mode;            // "prefix" | "basename"
        public string Prefix;          // prefix mode: the string prepended
        public string BaseName;        // basename mode: the stem the matched features are numbered from
        public string Scope;           // "every feature" | "fillets" | "holes" | ...
        public int TotalFeatures;      // real (non-scaffold) features in the tree
        public int Planned;            // rows the plan intended to rename
        public int Renamed;            // rows that actually took
        public int SkippedCount;       // rows deliberately not renamed (collision)
        public List<BatchRenameRow> Rows = new List<BatchRenameRow>();
        public bool AlreadyDone;       // idempotent: every target already carries the new naming
        public bool NeedsConfirm;      // nothing resolvable / no target name → one question, wrote nothing
        public string Question;
        public int RebuildErrors;
        public bool Verified;          // fail closed: independent re-traversal agrees
        public string Info;
        public string Error;
    }

    /// <summary>
    /// BatchRenameFeatures (tool #146 batch_rename_features) — the PLURAL of rename_feature: give a whole tree of
    /// auto-named features (Fillet1..Fillet40, Cut-Extrude7, Sketch12) meaningful names in one pass.
    /// "rename every feature with the prefix BRK-", "rename all the fillets to EdgeFillet".
    ///
    /// Two modes, both metadata-only (no geometry, no rebuild-affecting change):
    ///   prefix   — prepend a token to every feature in scope, PRESERVING the existing name (Seed-Hole → BRK-Seed-Hole).
    ///              The shop convention "every feature in this part carries the part code".
    ///   basename — a TYPE scope renamed to one stem + a sequence (Fillet1/Fillet2 → EdgeFillet1/EdgeFillet2).
    ///
    /// Named crew:
    ///   Gauge   — parse the mode + token; walk the tree; scope to REAL features (the 11 *Folder scaffold entries, the
    ///             default planes/origin and other reference geometry are NOT the user's features and are left alone);
    ///             build the full old→new plan and drop any row whose new name would COLLIDE with a name that already
    ///             exists or with another row (a silent collision is how a batch rename quietly loses features).
    ///   Scribe  — one-line plan, then IFeature.Name per row, each row independent: one refusal doesn't abort the rest
    ///             (Rule #4 — partial success beats total failure).
    ///   Sentinel— FAIL CLOSED (Rule #6): INDEPENDENTLY re-traverse the tree and confirm every new name is present, no
    ///             renamed old name survives, the feature COUNT is unchanged (a rename is not an add/delete), and the
    ///             rebuild is no worse than it started.
    ///
    /// IDEMPOTENT (Rule #5): a second run finds every target already carrying the prefix/stem and does nothing — which
    /// is the assertion that matters here, because the naive implementation produces BRK-BRK-Seed-Hole on rerun.
    /// UNDO is sacred (Rule #7): the renames are ordinary tree edits; Forge never saves.
    /// </summary>
    public static class BatchRenameFeatures
    {
        // NARROW + specific-first: needs a rename/prefix VERB **and** a batch scope word **and** a feature noun, and
        // bails on the nouns other rename handlers own (component / dimension / configuration / file / property).
        // "rename Fillet1 to TopFillet" has no batch word → stays with rename_feature (tool 145).
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (Regex.IsMatch(c, @"\b(component|components|part number|assembly|file|files|dimension|dimensions|dim|configuration|config|configs|propert(y|ies)|mate|mates|sheet|drawing)\b")) return false;
            bool verb = Regex.IsMatch(c, @"\b(rename|re-name|renaming|prefix|prefixes|suffix)\b");
            bool batch = Regex.IsMatch(c, @"\b(all|every|each|bulk|batch|everything)\b");
            bool feat = Regex.IsMatch(c, @"\b(feature|features|fillet|fillets|round|rounds|chamfer|chamfers|hole|holes|cut|cuts|pocket|pockets|boss|bosses|extrude|extrudes|extrusion|extrusions|sketch|sketches|pattern|patterns|shell|shells|rib|ribs|draft|drafts)\b");
            return verb && batch && feat;
        }

        public static async Task<BatchRenameFeaturesResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new BatchRenameFeaturesResult();

            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { res.Error = "Batch-renaming features works on a single part — open the .SLDPRT."; return res; }

            await emit("Gauge", "reading the feature tree and building the rename plan", "run", null);

            // ---- the real features (scaffold folders + reference geometry are not the user's features) ----
            var all = new List<Feature>();
            var f0 = model.FirstFeature() as Feature;
            while (f0 != null)
            {
                string tn = SafeType(f0);
                if (!string.IsNullOrEmpty(tn) && !IsScaffold(tn)) all.Add(f0);
                f0 = f0.GetNextFeature() as Feature;
            }
            res.TotalFeatures = all.Count;
            if (all.Count == 0)
            { res.Error = "This part has no feature tree (an imported dumb solid or an empty document) — there is nothing to rename."; await emit("Gauge", null, "fail", "no feature tree"); return res; }

            var existing = new HashSet<string>(all.Select(SafeName).Where(n => !string.IsNullOrEmpty(n)), StringComparer.OrdinalIgnoreCase);

            string prefix = ParsePrefix(intent);
            string baseName; string typeLabel; Func<string, bool> typePred;
            bool baseMode = ParseBaseName(intent, all, out baseName, out typeLabel, out typePred);

            List<Feature> targets;
            if (!string.IsNullOrEmpty(prefix))
            {
                res.Mode = "prefix"; res.Prefix = prefix;
                // a type word narrows the scope even in prefix mode ("prefix all the fillets with BRK-")
                Func<string, bool> pred = ResolveTypePredicate(intent, out typeLabel);
                targets = pred == null ? all : all.Where(fe => { var tn = SafeType(fe); return tn != null && pred(tn); }).ToList();
                res.Scope = pred == null ? "every feature" : typeLabel;
            }
            else if (baseMode)
            {
                res.Mode = "basename"; res.BaseName = baseName;
                targets = typePred == null ? all : all.Where(fe => { var tn = SafeType(fe); return tn != null && typePred(tn); }).ToList();
                res.Scope = typePred == null ? "every feature" : typeLabel;
            }
            else
            {
                res.NeedsConfirm = true;
                res.Question = "Tell me the naming to apply, e.g. 'rename every feature with the prefix BRK-' or 'rename all the fillets to EdgeFillet'.";
                await emit("Gauge", null, "fail", "no naming rule given — asking");
                return res;
            }

            if (targets.Count == 0)
            {
                res.NeedsConfirm = true;
                res.Question = "No " + (res.Scope ?? "feature") + " to rename here. The tree has: " + string.Join(", ", all.Take(8).Select(SafeName)) + ". Which ones did you mean?";
                await emit("Gauge", null, "fail", "scope matched nothing — asking");
                return res;
            }

            // ---- the plan: old → new, skipping anything already correct and anything that would collide ----
            var plan = new List<BatchRenameRow>();
            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int seq = 0;
            foreach (var fe in targets)
            {
                string old = SafeName(fe);
                if (string.IsNullOrEmpty(old)) continue;
                string nw = res.Mode == "prefix" ? prefix + old : baseName + (++seq);
                if (string.Equals(old, nw, StringComparison.Ordinal)) continue;                 // already correct
                if (res.Mode == "prefix" && old.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;  // already prefixed — never BRK-BRK-
                plan.Add(new BatchRenameRow { Old = old, New = nw });
            }
            // collision guard: a new name that already exists on a feature we are NOT renaming, or that two rows share
            var beingRenamed = new HashSet<string>(plan.Select(p => p.Old), StringComparer.OrdinalIgnoreCase);
            foreach (var row in plan)
            {
                if (claimed.Contains(row.New)) { row.Skipped = "another feature is already being renamed to '" + row.New + "'"; continue; }
                if (existing.Contains(row.New) && !beingRenamed.Contains(row.New)) { row.Skipped = "a feature named '" + row.New + "' already exists"; continue; }
                claimed.Add(row.New);
            }

            res.Rows = plan;
            res.Planned = plan.Count(p => p.Skipped == null);
            res.SkippedCount = plan.Count(p => p.Skipped != null);

            if (res.Planned == 0)
            {
                res.AlreadyDone = true;
                res.Verified = true;
                res.Info = res.Mode == "prefix"
                    ? "Every " + res.Scope + " already carries the prefix '" + prefix + "' — nothing to do."
                    : "Every " + res.Scope + " is already named from '" + baseName + "' — nothing to do.";
                await emit("Scribe", null, "done", "already done — nothing to rename");
                return res;
            }

            int errBefore = SafeWhatsWrong(model);
            int countBefore = all.Count;

            // ---- Scribe: the plan first (Rule #3), then one rename per row, each independent (Rule #4) ----
            var preview = string.Join(", ", plan.Where(p => p.Skipped == null).Take(4).Select(p => p.Old + "→" + p.New));
            await emit("Scribe", "renaming " + res.Planned + " of " + res.TotalFeatures + " features: " + preview + (res.Planned > 4 ? ", …" : ""), "run", null);

            var byOld = new Dictionary<string, Feature>(StringComparer.OrdinalIgnoreCase);
            foreach (var fe in targets) { var n = SafeName(fe); if (!string.IsNullOrEmpty(n) && !byOld.ContainsKey(n)) byOld[n] = fe; }

            foreach (var row in plan)
            {
                if (row.Skipped != null) continue;
                Feature fe; if (!byOld.TryGetValue(row.Old, out fe)) { row.Skipped = "the feature vanished from the tree mid-run"; res.SkippedCount++; continue; }
                try { fe.Name = row.New; row.Applied = true; res.Renamed++; }
                catch (Exception ex) { row.Skipped = "SolidWorks refused the name (" + ex.GetType().Name + ")"; res.SkippedCount++; }
            }
            try { model.ForceRebuild3(false); } catch { }
            res.RebuildErrors = SafeWhatsWrong(model);

            // ---- Sentinel: FAIL CLOSED — independent re-traversal, never the setter's word ----
            await emit("Sentinel", "verifying every rename against a fresh tree read", "run", null);
            var after = new List<string>();
            var f1 = model.FirstFeature() as Feature;
            while (f1 != null)
            {
                string tn = SafeType(f1);
                if (!string.IsNullOrEmpty(tn) && !IsScaffold(tn)) { var n = SafeName(f1); if (!string.IsNullOrEmpty(n)) after.Add(n); }
                f1 = f1.GetNextFeature() as Feature;
            }
            var afterSet = new HashSet<string>(after, StringComparer.OrdinalIgnoreCase);

            var missing = plan.Where(p => p.Applied && !afterSet.Contains(p.New)).Select(p => p.New).ToList();
            var survivors = plan.Where(p => p.Applied && afterSet.Contains(p.Old)).Select(p => p.Old).ToList();
            bool countSame = after.Count == countBefore;
            bool clean = res.RebuildErrors <= errBefore;

            res.Verified = missing.Count == 0 && survivors.Count == 0 && countSame && clean && res.Renamed > 0;
            if (!res.Verified)
            {
                res.Error = missing.Count > 0 ? missing.Count + " rename(s) didn't take (" + string.Join(", ", missing.Take(3)) + ") — check the part."
                          : survivors.Count > 0 ? survivors.Count + " old name(s) are still in the tree (" + string.Join(", ", survivors.Take(3)) + ")."
                          : !countSame ? "The feature count changed during the rename (" + countBefore + " → " + after.Count + ") — check the part."
                          : !clean ? "The rename introduced " + (res.RebuildErrors - errBefore) + " rebuild error(s) — check the part."
                          : "Nothing was renamed.";
                await emit("Sentinel", null, "fail", res.Error);
                return res;
            }

            await emit("Sentinel", null, "done", res.Renamed + " renamed, tree intact (" + after.Count + " features), rebuild clean");
            res.Info = "Renamed " + res.Renamed + " of " + res.TotalFeatures + " features"
                     + (res.Mode == "prefix" ? " with the prefix '" + prefix + "'" : " from the stem '" + baseName + "'")
                     + (res.SkippedCount > 0 ? ", " + res.SkippedCount + " skipped (name collision)" : "")
                     + ". Tree intact, rebuild clean. Ctrl+Z restores the old names; Forge didn't save.";
            return res;
        }

        // ================= parsing =================

        private static readonly string[] Stop = { "the", "a", "an", "all", "every", "each", "feature", "features", "them", "it", "this", "part" };

        // "rename every feature with the prefix BRK-" | "add the prefix BRK- to every feature" | "prefix everything with BRK-"
        private static string ParsePrefix(string intent)
        {
            string c = (intent ?? "").Trim();
            var pats = new[]
            {
                @"\b(?:with|using)\s+(?:the\s+)?(?:prefix|prefixes)\s+[""'`]?([^\s""'`]+)",
                @"\bprefix\b\s+[""'`]?([^\s""'`]+?)[""'`]?\s+\b(?:to|on|onto|for)\b",
                @"\bprefix\b.*?\b(?:with|using)\s+[""'`]?([^\s""'`]+)",
            };
            foreach (var p in pats)
            {
                var m = Regex.Match(c, p, RegexOptions.IgnoreCase);
                if (!m.Success) continue;
                string tok = m.Groups[1].Value.Trim().Trim('"', '\'', '`', ',', '.');
                if (tok.Length == 0) continue;
                if (Stop.Contains(tok.ToLowerInvariant())) continue;
                return tok;
            }
            return null;
        }

        // "rename all the fillets to EdgeFillet"
        private static bool ParseBaseName(string intent, List<Feature> all, out string baseName, out string typeLabel, out Func<string, bool> pred)
        {
            baseName = null; typeLabel = null; pred = null;
            string c = (intent ?? "").Trim();
            var m = Regex.Match(c, @"\b(?:rename|re-name)\b\s+(?:all|every|each)\s+(?:of\s+)?(?:the\s+)?.*?\b(?:to|as)\b\s+(.+)$", RegexOptions.IgnoreCase);
            if (!m.Success) return false;
            string nm = m.Groups[1].Value.Trim().Trim('"', '\'', '`', '.', ' ');
            nm = Regex.Replace(nm, @"\s+", "");
            if (nm.Length == 0 || Stop.Contains(nm.ToLowerInvariant())) return false;
            baseName = nm;
            pred = ResolveTypePredicate(intent, out typeLabel);
            return true;
        }

        // a type word in the command → a GetTypeName2 predicate. Mirrors RenameFeature/SuppressFeature, including the
        // "ICE" cut-extrude type name this R2026x build reports (see docs/SOLIDWORKS-GOTCHAS.md).
        private static Func<string, bool> ResolveTypePredicate(string cmd, out string label)
        {
            label = null;
            string c = (cmd ?? "").ToLowerInvariant();
            if (Regex.IsMatch(c, @"\b(fillet|round)s?\b")) { label = "fillets"; return tn => tn == "Fillet"; }
            if (Regex.IsMatch(c, @"\b(chamfer|bevel)s?\b")) { label = "chamfers"; return tn => tn == "Chamfer"; }
            if (Regex.IsMatch(c, @"\b(hole)s?\b")) { label = "holes"; return tn => tn.IndexOf("Hole", StringComparison.OrdinalIgnoreCase) >= 0 || string.Equals(tn, "ICE", StringComparison.OrdinalIgnoreCase); }
            if (Regex.IsMatch(c, @"\b(cut|pocket|slot)s?\b")) { label = "cuts"; return tn => tn.IndexOf("Cut", StringComparison.OrdinalIgnoreCase) >= 0 || string.Equals(tn, "ICE", StringComparison.OrdinalIgnoreCase); }
            if (Regex.IsMatch(c, @"\b(sketch|sketches)\b")) { label = "sketches"; return tn => tn == "ProfileFeature"; }
            if (Regex.IsMatch(c, @"\b(pattern)s?\b")) { label = "patterns"; return tn => tn.IndexOf("Pattern", StringComparison.OrdinalIgnoreCase) >= 0; }
            if (Regex.IsMatch(c, @"\b(boss|extrude|extrusion|pad)e?s?\b")) { label = "bosses"; return tn => tn == "Extrusion" || tn.IndexOf("Boss", StringComparison.OrdinalIgnoreCase) >= 0; }
            if (Regex.IsMatch(c, @"\b(shell)s?\b")) { label = "shells"; return tn => tn == "Shell"; }
            if (Regex.IsMatch(c, @"\b(rib)s?\b")) { label = "ribs"; return tn => tn == "Rib"; }
            if (Regex.IsMatch(c, @"\b(draft)s?\b")) { label = "drafts"; return tn => tn == "Draft"; }
            return null;
        }

        // the 11 empty container folders this build walks, plus default reference geometry — none of these are the
        // user's features and a batch rename must never touch them (docs/SOLIDWORKS-GOTCHAS.md landmine).
        private static bool IsScaffold(string tn)
        {
            if (tn.EndsWith("Folder", StringComparison.OrdinalIgnoreCase)) return true;
            switch (tn)
            {
                case "RefPlane": case "RefAxis": case "OriginProfileFeature": case "CoordSys":
                case "DetailCabinet": case "HistoryFolder": case "SketchBlockDef": return true;
                default: return false;
            }
        }

        private static string SafeName(Feature f) { try { return f?.Name; } catch { return null; } }
        private static string SafeType(Feature f) { try { return f?.GetTypeName2(); } catch { return null; } }
        private static int SafeWhatsWrong(IModelDoc2 model) { try { return model.Extension.GetWhatsWrongCount(); } catch { return 0; } }
    }
}
