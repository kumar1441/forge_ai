using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class RenameFeatureResult
    {
        public string OldName;       // the feature that was renamed (its name at run start)
        public string NewName;       // the requested new name
        public string ResolvedBy;    // "name" | "type" — how the source feature was found
        public bool Renamed;         // the rename was applied (before verify)
        public int RebuildErrors;    // GetWhatsWrongCount post-rebuild
        public bool AlreadyDone;     // idempotent: a feature already carries NewName → nothing to do
        public bool NeedsConfirm;    // zero/ambiguous source, or a name collision → ask ONE question, wrote nothing
        public string Question;
        public bool Verified;        // fail closed: a feature named NewName exists, OldName is gone, total count unchanged, rebuild clean
        public string Info;
        public string Error;
    }

    /// <summary>
    /// RenameFeature (tool #145 rename_feature) — a safe METADATA WRITE on a PART: give a feature a meaningful name.
    /// "rename Fillet1 to TopFillet", "rename the shell to WallShell", "call the last cut Slot". A real workflow — an LLM
    /// (or an engineer) turning cryptic auto-names (Fillet1..40, Cut-Extrude3) into names later commands can reference.
    ///
    /// This touches ONLY the feature's Name (no geometry, no rebuild-affecting change), so it is inherently low-risk and
    /// cleanly reversible. Named crew:
    ///   Gauge — parse "rename &lt;source&gt; to &lt;newname&gt;"; resolve the SOURCE feature — first by exact/fuzzy NAME, else by
    ///           TYPE word (fillet/chamfer/hole/cut/boss/shell/pattern/…) picking the LAST such feature (the one the user
    ///           most likely means). Zero matches, multiple type matches, or a NewName collision → ask ONE question
    ///           (Rule #2), rename nothing.
    ///   Scribe — set IFeature.Name = NewName. One ForceRebuild3 (a rename shouldn't error the tree, but verify anyway).
    ///   Sentinel — FAIL CLOSED (Rule #6): INDEPENDENTLY re-traverse the tree and confirm a feature named NewName now
    ///           exists, the OldName is gone, the TOTAL feature count is unchanged (a rename is not an add/delete), and the
    ///           rebuild is clean.
    ///
    /// IDEMPOTENT (Rule #5): if a feature already carries NewName, report "already named that". UNDO is sacred (Rule #7):
    /// one Ctrl+Z restores the old name; Forge never saves.
    /// </summary>
    public static class RenameFeature
    {
        public static bool IsRenameFeatureIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            // must be a rename VERB + a "to <name>" / "as <name>" target, and reference a FEATURE (a type word or the word
            // feature). "rename the part/component/file" is a different handler (rename_component / rename_file).
            if (Regex.IsMatch(c, @"\b(component|components|part|assembly|file|dimension|dim|configuration|config)\b")) return false;
            bool verb = Regex.IsMatch(c, @"\b(rename|call|name)\b");
            bool hasTo = Regex.IsMatch(c, @"\b(to|as)\b|→");
            bool feat = Regex.IsMatch(c, @"\b(feature|fillet|round|chamfer|hole|cut|pocket|boss|extrude|extrusion|shell|pattern|mirror|rib|draft|plane|thread|it|this|that|the)\b");
            return verb && hasTo && feat;
        }

        public static async Task<RenameFeatureResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new RenameFeatureResult();

            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { res.Error = "Renaming a feature works on a single part — open the .SLDPRT (renaming a component in an assembly is a separate command)."; return res; }

            await emit("Gauge", "resolving the feature and the new name", "run", null);

            string newName = ParseNewName(intent);
            if (string.IsNullOrWhiteSpace(newName))
            { res.Error = "Tell me the new name, e.g. 'rename Fillet1 to TopFillet'."; await emit("Gauge", null, "fail", "no target name"); return res; }
            res.NewName = newName;

            // ---- all features (independent traversal) ----
            var all = new List<Feature>();
            var f0 = model.FirstFeature() as Feature;
            while (f0 != null) { all.Add(f0); f0 = f0.GetNextFeature() as Feature; }
            if (all.Count == 0)
            { res.Error = "This part has no feature tree (an imported dumb solid or an empty document) — there is nothing to rename."; await emit("Gauge", null, "fail", "no feature tree"); return res; }

            // ---- IDEMPOTENT (Rule #5): a feature already carries NewName ----
            if (all.Any(fe => string.Equals(SafeName(fe), newName, StringComparison.OrdinalIgnoreCase)))
            {
                res.AlreadyDone = true;
                res.Verified = true;
                res.OldName = newName;
                res.Info = "A feature is already named '" + newName + "' — nothing to do.";
                await emit("Scribe", null, "done", "already named '" + newName + "'");
                return res;
            }

            // ---- resolve the SOURCE feature ----
            string srcPhrase = ParseSourcePhrase(intent);
            Feature src = null; string how = null;

            // (a) exact/fuzzy NAME
            if (!string.IsNullOrWhiteSpace(srcPhrase))
            {
                src = all.FirstOrDefault(fe => string.Equals(SafeName(fe), srcPhrase, StringComparison.OrdinalIgnoreCase));
                if (src == null) src = all.FirstOrDefault(fe => (SafeName(fe) ?? "").ToLowerInvariant().Contains(srcPhrase.ToLowerInvariant()));
                if (src != null) how = "name";
            }

            // (b) TYPE word → the LAST feature of that type (most recent = most likely intended)
            if (src == null)
            {
                var typePred = ResolveTypePredicate(intent, out string typeLabel, out int typeCount, all);
                if (typePred != null)
                {
                    var matches = all.Where(fe => { var tn = SafeType(fe); return tn != null && typePred(tn); }).ToList();
                    if (matches.Count == 1) { src = matches[0]; how = "type"; }
                    else if (matches.Count > 1)
                    {
                        // multiple → the LAST one, but SAY so (Rule #2 — don't silently guess among many)
                        src = matches.Last(); how = "type";
                        await emit("Gauge", null, "run", matches.Count + " " + typeLabel + " — renaming the most recent (" + SafeName(src) + ")");
                    }
                }
            }

            if (src == null)
            {
                res.NeedsConfirm = true;
                var present = all.Select(fe => SafeName(fe)).Where(n => !string.IsNullOrEmpty(n)).Take(8);
                res.Question = "Couldn't find the feature to rename" + (srcPhrase != null ? " ('" + srcPhrase + "')" : "") +
                               ". Features here include: " + string.Join(", ", present) + ". Which one?";
                await emit("Gauge", null, "fail", "source not resolved — asking");
                return res;
            }

            res.OldName = SafeName(src);
            res.ResolvedBy = how;
            await emit("Gauge", null, "done", "renaming '" + res.OldName + "' → '" + newName + "'");

            int totalBefore = all.Count;
            int errBefore = SafeWhatsWrong(model);

            // ---- Scribe: set the name ----
            await emit("Scribe", "applying the new name", "run", null);
            try { src.Name = newName; res.Renamed = true; }
            catch (Exception ex) { res.Error = "Couldn't set the name (" + ex.GetType().Name + ") — the part is unchanged."; await emit("Scribe", null, "fail", res.Error); return res; }
            try { model.ForceRebuild3(false); } catch { }
            res.RebuildErrors = SafeWhatsWrong(model);

            // ---- Sentinel: FAIL CLOSED — independent re-traversal ----
            await emit("Sentinel", "verifying the rename", "run", null);
            var after = new List<Feature>();
            var f1 = model.FirstFeature() as Feature;
            while (f1 != null) { after.Add(f1); f1 = f1.GetNextFeature() as Feature; }
            bool newPresent = after.Any(fe => string.Equals(SafeName(fe), newName, StringComparison.OrdinalIgnoreCase));
            bool oldGone = !after.Any(fe => string.Equals(SafeName(fe), res.OldName, StringComparison.OrdinalIgnoreCase));
            bool countSame = after.Count == totalBefore;
            bool clean = res.RebuildErrors <= errBefore;   // a rename must not INTRODUCE rebuild errors

            res.Verified = newPresent && oldGone && countSame && clean;
            if (!res.Verified)
            {
                res.Error = !newPresent ? "The new name didn't take — the part is unchanged."
                          : !countSame ? "The feature count changed unexpectedly during the rename — check the part."
                          : !clean ? "The rename introduced " + (res.RebuildErrors - errBefore) + " rebuild error(s) — check the part."
                          : "The old name is still present — the rename may not have applied cleanly.";
                await emit("Sentinel", null, "fail", res.Error);
                return res;
            }

            await emit("Sentinel", null, "done", "renamed: '" + res.OldName + "' → '" + newName + "', tree intact, rebuild clean");
            res.Info = "Renamed '" + res.OldName + "' to '" + newName + "' (feature tree intact, rebuild clean). One Ctrl+Z restores the old name; Forge didn't save.";
            return res;
        }

        // ================= parsing =================

        // the new name: after "to"/"as"/"→". Strip surrounding quotes/whitespace. Rejects a trailing "feature" filler.
        private static string ParseNewName(string intent)
        {
            string c = (intent ?? "").Trim();
            var m = Regex.Match(c, @"\b(?:to|as)\b\s+(.+)$", RegexOptions.IgnoreCase);
            if (!m.Success) m = Regex.Match(c, @"→\s*(.+)$");
            if (!m.Success) return null;
            string name = m.Groups[1].Value.Trim().Trim('"', '\'', '`', '.', ' ');
            name = Regex.Replace(name, @"\s+feature$", "", RegexOptions.IgnoreCase).Trim();
            return name.Length == 0 ? null : name;
        }

        // the source phrase: between the verb and "to"/"as". "rename Fillet1 to X" → "fillet1". Strips "the".
        private static string ParseSourcePhrase(string intent)
        {
            string c = (intent ?? "").Trim();
            var m = Regex.Match(c, @"\b(?:rename|call|name)\b\s+(.+?)\s+\b(?:to|as)\b", RegexOptions.IgnoreCase);
            if (!m.Success) m = Regex.Match(c, @"\b(?:rename|call|name)\b\s+(.+?)\s*→", RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            string p = m.Groups[1].Value.Trim();
            p = Regex.Replace(p, @"^(the|this|that|its|my)\s+", "", RegexOptions.IgnoreCase).Trim();
            p = Regex.Replace(p, @"\s+feature$", "", RegexOptions.IgnoreCase).Trim();
            // a bare "it/this/that" is not a name phrase → fall through to type resolution
            if (Regex.IsMatch(p, @"^(it|this|that|the last one|last one|last)$", RegexOptions.IgnoreCase)) return null;
            return p.Length == 0 ? null : p;
        }

        // map a type word in the command to a GetTypeName2 predicate (mirrors SuppressFeature's mapping, incl. the ICE
        // cut-extrude type on this R2026x build).
        private static Func<string, bool> ResolveTypePredicate(string cmd, out string label, out int count, List<Feature> all)
        {
            label = null; count = 0;
            string c = (cmd ?? "").ToLowerInvariant();
            Func<string, bool> pred = null;
            if (Regex.IsMatch(c, @"\b(fillet|round)s?\b")) { label = "fillets"; pred = tn => tn == "Fillet"; }
            else if (Regex.IsMatch(c, @"\b(chamfer|bevel)s?\b")) { label = "chamfers"; pred = tn => tn == "Chamfer"; }
            else if (Regex.IsMatch(c, @"\b(cosmetic thread|thread|tap)s?\b")) { label = "threads"; pred = tn => tn == "CosmeticThread"; }
            else if (Regex.IsMatch(c, @"\b(hole)s?\b")) { label = "holes"; pred = tn => tn.IndexOf("Hole", StringComparison.OrdinalIgnoreCase) >= 0; }
            else if (Regex.IsMatch(c, @"\b(cut|pocket|slot)s?\b")) { label = "cuts"; pred = tn => tn.IndexOf("Cut", StringComparison.OrdinalIgnoreCase) >= 0 || string.Equals(tn, "ICE", StringComparison.OrdinalIgnoreCase); }
            else if (Regex.IsMatch(c, @"\b(pattern)s?\b")) { label = "patterns"; pred = tn => tn.IndexOf("Pattern", StringComparison.OrdinalIgnoreCase) >= 0; }
            else if (Regex.IsMatch(c, @"\b(boss|extrude|extrusion|pad)s?\b")) { label = "bosses"; pred = tn => tn == "Extrusion" || tn.IndexOf("Boss", StringComparison.OrdinalIgnoreCase) >= 0; }
            else if (Regex.IsMatch(c, @"\b(shell)s?\b")) { label = "shells"; pred = tn => tn == "Shell"; }
            else if (Regex.IsMatch(c, @"\b(rib)s?\b")) { label = "ribs"; pred = tn => tn == "Rib"; }
            else if (Regex.IsMatch(c, @"\b(draft)s?\b")) { label = "drafts"; pred = tn => tn == "Draft"; }
            else if (Regex.IsMatch(c, @"\b(plane)s?\b")) { label = "planes"; pred = tn => tn == "RefPlane"; }
            if (pred != null) count = all.Count(fe => { var tn = SafeType(fe); return tn != null && pred(tn); });
            return pred;
        }

        private static string SafeName(Feature f) { try { return f?.Name; } catch { return null; } }
        private static string SafeType(Feature f) { try { return f?.GetTypeName2(); } catch { return null; } }
        private static int SafeWhatsWrong(IModelDoc2 model) { try { return model.Extension.GetWhatsWrongCount(); } catch { return 0; } }
    }
}
