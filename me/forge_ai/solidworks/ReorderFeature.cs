using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class ReorderFeatureResult
    {
        public string MoveName;      // the feature that was moved (resolved from the tree)
        public string TargetName;    // the anchor feature it was moved relative to
        public string Position;      // "before" | "after"
        public bool Reordered;       // ReorderFeature returned true
        public int ApiReturn;        // raw ReorderFeature return (1 = true, 0 = false) — instrument-first
        public int RebuildErrors;    // GetWhatsWrongCount post-rebuild
        public bool AlreadyDone;     // idempotent: the move feature already sits on the requested side of the target
        public bool NeedsConfirm;    // a feature couldn't be resolved → ask ONE question, wrote nothing
        public string Question;
        public bool Verified;        // fail closed: independent re-read confirms the order, count unchanged, rebuild clean
        public string Diag;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// ReorderFeature (tool #147 reorder_feature) — move a feature earlier/later in the tree, with a dependency-safe,
    /// verified result. "move Hole-2 before Seed-Hole", "reorder the shell after the fillet", "put Boss1 ahead of Cut3".
    /// A real workflow: fixing build order so a later feature sees the geometry it needs, or grouping like features.
    ///
    /// API (dumped from the interop, not guessed): IModelDocExtension.ReorderFeature(featureToMove, targetFeature,
    /// swMoveLocation_e) → bool, with swMoveBefore=2 / swMoveAfter=3. SolidWorks itself enforces dependency order (it
    /// refuses to move a child before its parent and returns false), so a false return is an honest "can't — dependency".
    /// Named crew:
    ///   Gauge — parse "&lt;move&gt; before|after &lt;target&gt;"; resolve BOTH features by name (exact then fuzzy). Either
    ///           unresolved → ask ONE question (Rule #2), touch nothing.
    ///   Scribe — call ReorderFeature; one ForceRebuild3.
    ///   Sentinel — FAIL CLOSED (Rule #6): INDEPENDENTLY re-traverse the tree and confirm the move feature now sits on the
    ///           requested side of the target, the TOTAL feature count is unchanged (a reorder is not an add/delete), and
    ///           the rebuild is clean. The raw API return is recorded either way (instrument before theorising).
    ///
    /// IDEMPOTENT (Rule #5): if the move feature already sits on the requested side of the target, report "already in
    /// order". UNDO is sacred (Rule #7): one Ctrl+Z restores the order; Forge never saves.
    /// </summary>
    public static class ReorderFeature
    {
        public static bool IsReorderFeatureIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            // this is a FEATURE reorder — a component/dimension/config reorder is a different concern.
            if (Regex.IsMatch(c, @"\b(component|components|part|assembly|dimension|dim|configuration|config|file|body|bodies)\b")) return false;
            bool positional = Regex.IsMatch(c, @"\b(before|after|ahead of|ahead|behind|above|below|in front of|earlier than|later than|prior to|underneath|under)\b");
            // "reorder" is specific enough on its own; "move/put/place/drag" only qualifies WITH a positional word so it
            // can't swallow "move the face" (move_face) or a component move.
            bool reorderWord = Regex.IsMatch(c, @"\breorder\b");
            bool moveVerb = Regex.IsMatch(c, @"\b(move|put|place|drag|shift|relocate)\b");
            return reorderWord || (moveVerb && positional);
        }

        public static async Task<ReorderFeatureResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new ReorderFeatureResult();

            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { res.Error = "Reordering a feature works on a single part — open the .SLDPRT."; return res; }

            await emit("Gauge", "resolving the two features and the direction", "run", null);

            // ---- parse move / position / target ----
            string movePhrase, targetPhrase; bool before;
            if (!ParseReorder(intent, out movePhrase, out targetPhrase, out before))
            { res.Error = "Tell me what to move and where, e.g. 'move Hole-2 before Seed-Hole'."; await emit("Gauge", null, "fail", "couldn't parse move/target"); return res; }
            res.Position = before ? "before" : "after";

            // ---- all features (independent traversal, order preserved) ----
            var all = new List<Feature>();
            var f0 = model.FirstFeature() as Feature;
            while (f0 != null) { all.Add(f0); f0 = f0.GetNextFeature() as Feature; }
            if (all.Count == 0)
            { res.Error = "This part has no feature tree (an imported dumb solid or empty document) — nothing to reorder."; await emit("Gauge", null, "fail", "no feature tree"); return res; }

            Feature mv = Resolve(all, movePhrase);
            Feature tg = Resolve(all, targetPhrase);
            if (mv == null || tg == null || ReferenceEquals(mv, tg))
            {
                res.NeedsConfirm = true;
                var present = all.Select(SafeName).Where(n => !string.IsNullOrEmpty(n)).Take(10);
                res.Question = "Couldn't resolve " + (mv == null ? "'" + movePhrase + "'" : "'" + targetPhrase + "'") +
                               ". Features here include: " + string.Join(", ", present) + ". Which ones?";
                await emit("Gauge", null, "fail", "feature not resolved — asking");
                return res;
            }
            res.MoveName = SafeName(mv);
            res.TargetName = SafeName(tg);

            int idxMove = all.FindIndex(x => ReferenceEquals(x, mv));
            int idxTarget = all.FindIndex(x => ReferenceEquals(x, tg));

            // ---- IDEMPOTENT (Rule #5): already on the requested side ----
            bool satisfied = before ? (idxMove < idxTarget) : (idxMove > idxTarget);
            if (satisfied)
            {
                res.AlreadyDone = true;
                res.Verified = true;
                res.Info = "'" + res.MoveName + "' is already " + res.Position + " '" + res.TargetName + "' — nothing to do.";
                await emit("Scribe", null, "done", "already " + res.Position + " — no change");
                return res;
            }

            await emit("Gauge", null, "done", "moving '" + res.MoveName + "' " + res.Position + " '" + res.TargetName + "'");

            int totalBefore = all.Count;
            int errBefore = SafeWhatsWrong(model);

            // ---- Scribe: reorder ----
            await emit("Scribe", "reordering the tree", "run", null);
            int loc = before ? (int)swMoveLocation_e.swMoveBefore : (int)swMoveLocation_e.swMoveAfter;
            bool ok = false;
            try { ok = model.Extension.ReorderFeature(res.MoveName, res.TargetName, loc); }
            catch (Exception ex) { res.Error = "ReorderFeature threw (" + ex.GetType().Name + ") — the part is unchanged."; await emit("Scribe", null, "fail", res.Error); return res; }
            res.ApiReturn = ok ? 1 : 0;
            res.Reordered = ok;
            res.Diag = "ReorderFeature('" + res.MoveName + "','" + res.TargetName + "'," + (before ? "Before" : "After") + ")=" + ok;
            try { model.ForceRebuild3(false); } catch { }
            res.RebuildErrors = SafeWhatsWrong(model);

            if (!ok)
            {
                // SW refuses a dependency-violating move — an honest "can't", not a crash.
                res.Error = "SolidWorks refused the move — '" + res.MoveName + "' can't go " + res.Position + " '" + res.TargetName +
                            "' without breaking a dependency. Nothing changed.";
                await emit("Scribe", null, "fail", res.Diag);
                return res;
            }

            // ---- Sentinel: FAIL CLOSED — independent re-traversal ----
            await emit("Sentinel", "verifying the new order", "run", null);
            var after = new List<Feature>();
            var f1 = model.FirstFeature() as Feature;
            while (f1 != null) { after.Add(f1); f1 = f1.GetNextFeature() as Feature; }
            int idxMoveAfter = after.FindIndex(x => string.Equals(SafeName(x), res.MoveName, StringComparison.OrdinalIgnoreCase));
            int idxTargetAfter = after.FindIndex(x => string.Equals(SafeName(x), res.TargetName, StringComparison.OrdinalIgnoreCase));
            bool orderOk = (idxMoveAfter >= 0 && idxTargetAfter >= 0) && (before ? idxMoveAfter < idxTargetAfter : idxMoveAfter > idxTargetAfter);
            bool countSame = after.Count == totalBefore;
            bool clean = res.RebuildErrors <= errBefore;

            res.Verified = orderOk && countSame && clean;
            if (!res.Verified)
            {
                res.Error = !orderOk ? "The reorder claimed success but the tree order didn't move — the part is effectively unchanged."
                          : !countSame ? "The feature count changed during the reorder — check the part."
                          : "The reorder introduced " + (res.RebuildErrors - errBefore) + " rebuild error(s) — check the part.";
                await emit("Sentinel", null, "fail", res.Error + " [" + res.Diag + "]");
                return res;
            }

            await emit("Sentinel", null, "done", "'" + res.MoveName + "' now " + res.Position + " '" + res.TargetName + "', tree intact, rebuild clean");
            res.Info = "Moved '" + res.MoveName + "' " + res.Position + " '" + res.TargetName + "' (feature count unchanged, rebuild clean). One Ctrl+Z restores the order; Forge didn't save.";
            return res;
        }

        // ================= parsing =================

        // "reorder|move|put|place|drag <move> before|after <target>"; the position word decides the side.
        private static bool ParseReorder(string intent, out string move, out string target, out bool before)
        {
            move = null; target = null; before = true;
            string c = (intent ?? "").Trim();
            var m = Regex.Match(c,
                @"\b(?:reorder|move|put|place|drag|shift|relocate)\b\s+(.+?)\s+\b(before|after|ahead of|ahead|behind|above|below|in front of|earlier than|later than|prior to|underneath|under)\b\s+(.+)$",
                RegexOptions.IgnoreCase);
            if (!m.Success) return false;
            move = Clean(m.Groups[1].Value);
            string pos = m.Groups[2].Value.ToLowerInvariant();
            target = Clean(m.Groups[3].Value);
            // "before / ahead of / above / in front of / earlier than / prior to" → earlier in the tree.
            before = Regex.IsMatch(pos, @"before|ahead|above|in front of|earlier|prior");
            return !string.IsNullOrWhiteSpace(move) && !string.IsNullOrWhiteSpace(target);
        }

        private static string Clean(string s)
        {
            string p = (s ?? "").Trim().Trim('"', '\'', '`', '.', ' ');
            p = Regex.Replace(p, @"^(the|this|that|its|my)\s+", "", RegexOptions.IgnoreCase).Trim();
            p = Regex.Replace(p, @"\s+feature$", "", RegexOptions.IgnoreCase).Trim();
            return p;
        }

        // resolve a feature by exact name, else case-insensitive contains.
        private static Feature Resolve(List<Feature> all, string phrase)
        {
            if (string.IsNullOrWhiteSpace(phrase)) return null;
            var hit = all.FirstOrDefault(fe => string.Equals(SafeName(fe), phrase, StringComparison.OrdinalIgnoreCase));
            if (hit == null) hit = all.FirstOrDefault(fe => (SafeName(fe) ?? "").ToLowerInvariant().Contains(phrase.ToLowerInvariant()));
            return hit;
        }

        private static string SafeName(Feature f) { try { return f?.Name; } catch { return null; } }
        private static int SafeWhatsWrong(IModelDoc2 model) { try { return model.Extension.GetWhatsWrongCount(); } catch { return 0; } }
    }
}
