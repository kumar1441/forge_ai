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
    public class DeleteFeatureResult
    {
        public string TargetType;      // the resolved target label ("fillets", "chamfers", a feature name…)
        public int Matched;            // features the target resolved to (before the protect-the-base filter)
        public int Deleted;            // features CONFIRMED gone from the tree after the delete + rebuild (fail closed)
        public int Skipped;            // matched but PROTECTED (base solid feature / reference geometry / origin) — never deleted
        public int RebuildErrors;      // GetWhatsWrongCount after the single post-delete rebuild
        public bool StillHasSolid;     // a solid body survives — the delete did NOT destroy the part
        public bool NeedsConfirm;      // unrecognized target → ask ONE question, deleted nothing
        public string Question;        // the one clarifying question when NeedsConfirm
        public bool Verified;          // fail closed: every matched feature independently re-reads GONE + clean rebuild + solid survives
        public string Info;            // verdict-first panel line
        public string Error;           // honest failure text (assembly handed in, no tree, base-only match, broke the rebuild)
    }

    /// <summary>
    /// DeleteFeature (tool #76 "delete_feature") — a PERMANENT removal of FEATURES from a PART's tree. "delete all the
    /// fillets", "delete the chamfers", "delete Fillet3", "remove the cosmetic threads". The permanent-removal sibling of
    /// suppress_feature (which REVERSIBLY suppresses in the active config) and of geometry_defeature (which deletes FACES on
    /// a dumb solid). This one deletes real modeling FEATURES — by TYPE (fillet/chamfer/hole/cut/pattern/boss/cosmetic-thread…)
    /// or by exact NAME — and they do not come back (one Ctrl+Z still undoes the run; Forge never saves).
    ///
    /// HEADLESS SAFETY: the SW "delete with dependents?" confirmation dialog HANGS a headless add-in (the same class of hang
    /// the isolate/visibility incident cost us). So this handler NEVER uses a prompting delete. It SELECTS the target features
    /// (IFeature.Select2) then calls IModelDocExtension.DeleteSelection2(swDelete_Children | swDelete_Absorbed) — the no-prompt
    /// form that deletes the CURRENT SELECTION silently, absorbed sketches and dependent children included, with NO modal.
    ///
    /// Approach (named crew):
    ///   Gauge — walk the feature tree; resolve the target to a set of features by TYPE (GetTypeName2: fillets→"Fillet",
    ///           chamfers→"Chamfer", holes→"…Hole…", cuts→"…Cut…", patterns→"…Pattern…", bosses→"Extrusion"/"Boss",
    ///           cosmetic threads→"CosmeticThread") OR by an exact feature NAME. Zero matches on a RECOGNIZED type → a clean
    ///           idempotent no-op ("no fillets to delete — nothing to do"); an UNRECOGNIZED target → ask ONE question naming
    ///           the feature TYPES actually present (Rule #2), delete nothing. NEVER matches the base/first solid feature,
    ///           reference geometry, or the origin — those are PROTECTED and skipped so the delete can't destroy the part.
    ///   Reaper — select every deletable feature, DeleteSelection2 (no prompt), ONE ForceRebuild3.
    ///   Sentinel — FAIL CLOSED (Rule #6): INDEPENDENTLY re-traverse; a feature counts as Deleted only when its NAME is gone
    ///           from the tree; Verified only when ALL matched are gone AND the rebuild is clean AND a solid body still exists.
    ///           If a survivor depended on a deleted feature the rebuild breaks → Verified=false, honest Error (the harness
    ///           closes WITHOUT saving, so the on-disk part is safe).
    /// </summary>
    public static class DeleteFeature
    {
        // no-prompt delete of the current selection: absorbed sketches + dependent children, NO "delete with dependents?" modal
        private const int DELETE_NOPROMPT =
            (int)swDeleteSelectionOptions_e.swDelete_Children | (int)swDeleteSelectionOptions_e.swDelete_Absorbed;

        // "delete/remove <a feature type or name>" — a PART FEATURE removal. Requires BOTH a delete-verb AND a feature
        // reference (a type word or the word "feature"), so it never swallows a component delete or a face defeature.
        // Placed PART-guarded in Dispatch (an assembly delete is a different command).
        public static bool IsDeleteFeatureIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            bool verb = Regex.IsMatch(c, @"\b(delete|remove|get rid of|strip out|erase)\b");
            if (!verb) return false;
            bool target = Regex.IsMatch(c, @"\b(fillet|round|chamfer|bevel|hole|cut|pocket|pattern|boss|extrude|extrusion|rib|draft|shell|cosmetic|thread|mirror|feature)s?\b");
            return target;
        }

        private class Plan
        {
            public string Label;                                     // human target label
            public bool RecognizedType;                              // the target was a KNOWN feature-type word (fillets/chamfers/…)
            public bool ByName;                                      // matched an exact/fuzzy feature name, not a type
            public List<Feature> Feats = new List<Feature>();        // the resolved feature set (pre protect-the-base filter)
            public List<Feature> Deletable = new List<Feature>();    // resolved MINUS protected (base solid / ref-geo / origin)
            public int Protected;                                    // matched-but-protected count
            public bool HasAnyFeatures;                              // the tree has at least one feature
            public List<string> PresentTypes = new List<string>();   // top by-type tally, for the no-match question
        }

        public static async Task<DeleteFeatureResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new DeleteFeatureResult();

            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { res.Error = "Delete features works on a single part — open the .SLDPRT whose features you want removed (deleting a component from an assembly is a separate command)."; return res; }

            await emit("Gauge", "resolving the target features", "run", null);
            var plan = Resolve(model, intent);
            res.TargetType = plan.Label;

            if (!plan.HasAnyFeatures)
            { res.Error = "This part has no feature tree (an imported dumb solid, or an empty document) — there are no features to delete."; await emit("Gauge", null, "fail", "no feature tree"); return res; }

            res.Matched = plan.Feats.Count;
            res.Skipped = plan.Protected;

            // ---- zero matches ----
            if (plan.Feats.Count == 0)
            {
                if (plan.RecognizedType)
                {
                    // a KNOWN type with no instances → naturally idempotent no-op (once gone, gone) — never a fake
                    // ask. BUT (found regression-sweeping cut-smooth-chain-live, same class of bug as
                    // GeometryDefeature's zero-target branch): the cloud can dispatch action=delete_feature for a
                    // request that isn't a feature-delete AT ALL (e.g. "cut the left arm off at the shoulder" — a
                    // semantic body-part cut, a genuine capability gap, NOT "delete the cut features"). The word
                    // "cut" alone made Resolve() recognize a type, but the SENTENCE never actually asked to delete
                    // anything (no delete/remove/strip-out/erase verb) — cross-check the handler's OWN narrow verb
                    // requirement (IsDeleteFeatureIntent) before claiming a verified idempotent success.
                    bool looksLikeDelete = IsDeleteFeatureIntent(intent);
                    res.Verified = looksLikeDelete;
                    res.StillHasSolid = HasSolid(model as PartDoc);
                    res.Info = looksLikeDelete
                        ? "No " + plan.Label + " in this part — nothing to delete."
                        : "No " + plan.Label + " in this part, and this doesn't read as a delete request in the first place " +
                          "(no delete/remove verb) — if you meant something else (a semantic body-part cut), this handler can't do that. The part is unchanged.";
                    await emit("Gauge", null, "done", looksLikeDelete ? "no " + plan.Label + " — nothing to do" : "no " + plan.Label + " — and this wasn't a delete request");
                    return res;
                }
                // an UNRECOGNIZED target word / unknown name → ask ONE question naming the TYPES present (Rule #2)
                res.NeedsConfirm = true;
                res.Question = "I couldn't find " + plan.Label + " to delete in this part. It has: " + string.Join(", ", plan.PresentTypes) + ". Which feature (type or exact name) should I delete?";
                await emit("Gauge", null, "fail", "no match for " + plan.Label + " — asking");
                return res;
            }

            // ---- every match is a PROTECTED base/reference feature → refuse honestly, delete nothing ----
            if (plan.Deletable.Count == 0)
            {
                res.Error = "The only " + plan.Label + " here is the part's base/reference geometry — deleting it would destroy the part, so I left it alone. Name a leaf feature (a fillet, chamfer, cut…) if that's what you meant.";
                await emit("Gauge", null, "fail", "base/reference only — protected");
                return res;
            }

            if (plan.Protected > 0)
                await emit("Gauge", null, "run", plan.Protected + " protected (base/reference) — skipping " + (plan.Protected == 1 ? "it" : "them"));
            await emit("Gauge", null, "done", plan.Deletable.Count + " " + plan.Label + " found — deleting");

            int solidBefore = SolidCount(model as PartDoc);
            int errBefore = SafeWhatsWrong(model);

            // capture the deletable NAMES up front — the Feature COM objects go invalid after DeleteSelection2, but the
            // Sentinel verifies purely by re-reading which names survive the tree.
            var targetNames = new List<string>();
            foreach (var f in plan.Deletable) { string nm = SafeName(f); if (nm != null) targetNames.Add(nm); }

            // ---- Reaper: select every deletable feature, then the NO-PROMPT DeleteSelection2 (never EditDelete — it can modal) ----
            await emit("Reaper", "selecting and deleting", "run", null);
            try { model.ClearSelection2(true); } catch { }
            int selected = 0;
            foreach (var f in plan.Deletable)
            { bool ok = false; try { ok = f.Select2(true, 0); } catch { ok = false; } if (ok) selected++; }

            bool deleteCall = false;
            if (selected > 0)
            { try { deleteCall = model.Extension.DeleteSelection2(DELETE_NOPROMPT); } catch { deleteCall = false; } }
            try { model.ClearSelection2(true); } catch { }
            try { model.ForceRebuild3(false); } catch { }
            int errAfter = SafeWhatsWrong(model);
            await emit("Reaper", null, "done", selected + " selected · DeleteSelection2 " + (deleteCall ? "ok" : "returned false"));

            // ---- Sentinel — FAIL CLOSED: re-traverse independently; a feature counts as Deleted only if its NAME is GONE ----
            await emit("Sentinel", "verifying removal + solid survives", "run", null);
            var survivingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var g = model.FirstFeature() as Feature;
            while (g != null) { string nm = SafeName(g); if (nm != null) survivingNames.Add(nm); g = g.GetNextFeature() as Feature; }
            int gone = targetNames.Count(nm => !survivingNames.Contains(nm));

            res.Deleted = gone;
            res.RebuildErrors = errAfter;
            res.StillHasSolid = HasSolid(model as PartDoc);
            int solidAfter = SolidCount(model as PartDoc);

            bool brokeRebuild = errAfter > errBefore || errAfter > 0;
            bool destroyedSolid = !res.StillHasSolid || solidAfter == 0;

            res.Verified = gone == plan.Deletable.Count && res.RebuildErrors == 0 && res.StillHasSolid && !destroyedSolid;

            if (destroyedSolid)
                res.Error = "Deleting the " + plan.Label + " removed the last solid body — the part would be destroyed. Not verified; nothing was saved.";
            else if (brokeRebuild)
                res.Error = "Deleting the " + plan.Label + " broke the rebuild — a later feature depended on " + (plan.Deletable.Count == 1 ? "it" : "one of them") + ". " + gone + " removed, but " + res.RebuildErrors + " rebuild error(s) remain. Nothing was saved — one Ctrl+Z restores the part.";
            else if (gone < plan.Deletable.Count)
                res.Error = (plan.Deletable.Count - gone) + " of " + plan.Deletable.Count + " " + plan.Label + " could not be removed (still in the tree) — left for review.";

            await emit("Sentinel", null, res.Verified ? "done" : "fail",
                gone + " gone · solid " + solidBefore + "→" + solidAfter + " · rebuild " + (res.RebuildErrors == 0 ? "clean" : res.RebuildErrors + " flag(s)"));

            res.Info = BuildInfo(res);
            return res;
        }

        // ---- read-only preview line (Rule #3): "6 fillets found — deleting"; null for a small/ambiguous set (run directly) ----
        public static string PreviewLine(IModelDoc2 model, string intent)
        {
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART) return null;
            var plan = Resolve(model, intent);
            if (!plan.HasAnyFeatures || plan.Deletable.Count == 0) return null;   // let Run() no-op / ask / report honestly
            if (plan.Deletable.Count <= 3) return null;                            // small unambiguous op → run directly
            return plan.Deletable.Count + " " + plan.Label + " found — deleting permanently" +
                   (plan.Protected > 0 ? " (" + plan.Protected + " base/reference skipped)" : "") +
                   " (one Ctrl+Z restores, Forge won't save)";
        }

        // ================= resolution (read-only; safe to call from PreviewLine and Run) =================

        private static Plan Resolve(IModelDoc2 model, string intent)
        {
            var p = new Plan();
            string cmd = (intent ?? "").ToLowerInvariant();

            // by-type tally (for the "what's present" question) + the raw feature list; also flag the base/first solid feature
            var all = new List<Feature>();
            var byType = new Dictionary<string, int>();
            string baseName = null;
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                all.Add(f);
                string tn = null; try { tn = f.GetTypeName2(); } catch { }
                if (string.IsNullOrEmpty(tn)) tn = "Unknown";
                int n; byType.TryGetValue(tn, out n); byType[tn] = n + 1;
                if (baseName == null && IsBodyCreatingType(tn)) baseName = SafeName(f);   // first body-maker = the base
                f = f.GetNextFeature() as Feature;
            }
            p.HasAnyFeatures = all.Count > 0;
            p.PresentTypes = byType.OrderByDescending(kv => kv.Value).Take(6).Select(kv => kv.Key + " ×" + kv.Value).ToList();

            // ---- TYPE match first (fillets/chamfers/holes/cuts/patterns/bosses/cosmetic-threads/…) ----
            string label; Func<string, bool> typeMatch = ResolveTypePredicate(cmd, out label);
            if (typeMatch != null)
            {
                p.Label = label; p.RecognizedType = true;
                foreach (var feat in all)
                {
                    string tn = null; try { tn = feat.GetTypeName2(); } catch { }
                    if (tn != null && typeMatch(tn)) p.Feats.Add(feat);
                }
            }
            else
            {
                // ---- exact / fuzzy feature NAME ----
                string phrase = ParseNamePhrase(cmd);
                if (!string.IsNullOrWhiteSpace(phrase))
                {
                    p.ByName = true;
                    p.Label = "\"" + phrase.Trim() + "\"";
                    foreach (var feat in all)
                    {
                        string nm = SafeName(feat);
                        if (nm != null && string.Equals(nm.Trim(), phrase.Trim(), StringComparison.OrdinalIgnoreCase)) p.Feats.Add(feat);
                    }
                    if (p.Feats.Count == 0)
                        foreach (var feat in all)
                        {
                            string nm = SafeName(feat);
                            if (nm != null && nm.ToLowerInvariant().Contains(phrase.Trim())) p.Feats.Add(feat);
                        }
                }
                else p.Label = "features";
            }

            // ---- PROTECT the part: never delete the base/first solid feature, reference geometry, or the origin ----
            foreach (var feat in p.Feats)
            {
                string tn = null; try { tn = feat.GetTypeName2(); } catch { }
                string nm = SafeName(feat);
                bool protectedFeat = IsProtectedType(tn) || (baseName != null && string.Equals(nm, baseName, StringComparison.OrdinalIgnoreCase));
                if (protectedFeat) p.Protected++; else p.Deletable.Add(feat);
            }
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
            // cut-extrudes are GetTypeName2=="ICE" on this R2026x build (not "Cut") — match it too, or "delete the cuts"
            // silently matches zero cut-extrudes. Same fix proven green in SuppressFeature (unsuppress-the-cut test).
            if (Regex.IsMatch(cmd, @"\b(cut|cuts|pocket|pockets)\b")) { label = "cuts"; return tn => tn.IndexOf("Cut", StringComparison.OrdinalIgnoreCase) >= 0 || string.Equals(tn, "ICE", StringComparison.OrdinalIgnoreCase); }
            if (Regex.IsMatch(cmd, @"\b(pattern|patterns)\b")) { label = "patterns"; return tn => tn.IndexOf("Pattern", StringComparison.OrdinalIgnoreCase) >= 0; }
            if (Regex.IsMatch(cmd, @"\b(boss|bosses|extrude|extrudes|extrusion|extrusions|pad|pads)\b")) { label = "bosses"; return tn => tn == "Extrusion" || tn.IndexOf("Boss", StringComparison.OrdinalIgnoreCase) >= 0; }
            if (Regex.IsMatch(cmd, @"\b(rib|ribs)\b")) { label = "ribs"; return tn => tn == "Rib"; }
            if (Regex.IsMatch(cmd, @"\b(draft|drafts)\b")) { label = "drafts"; return tn => tn == "Draft"; }
            if (Regex.IsMatch(cmd, @"\b(shell|shells)\b")) { label = "shells"; return tn => tn == "Shell"; }
            if (Regex.IsMatch(cmd, @"\b(mirror|mirrors)\b")) { label = "mirror features"; return tn => tn.IndexOf("Mirror", StringComparison.OrdinalIgnoreCase) >= 0; }
            return null;
        }

        // strip the verb + fillers, return the trailing name phrase ("delete Fillet3" → "fillet3")
        private static string ParseNamePhrase(string cmd)
        {
            var m = Regex.Match(cmd,
                @"\b(?:delete|remove|get rid of|strip out|erase)\b\s+(?:all\s+the\s+|all\s+|the\s+|both\s+the\s+)?(.+)$");
            if (!m.Success) return null;
            string phrase = m.Groups[1].Value.Trim();
            phrase = Regex.Replace(phrase, @"\b(feature|features)\b", "").Trim();
            return phrase.Length == 0 ? null : phrase;
        }

        // ================= info + helpers =================

        // verdict first (Character #3), the number not the adjective (Character #2), honest about what was VERIFIED.
        private static string BuildInfo(DeleteFeatureResult r)
        {
            if (r.Matched > 0 && r.Deleted == 0 && r.Error == null)
                return "No " + r.TargetType + " to delete — nothing to do.";

            var sb = new StringBuilder();
            sb.Append(r.Verified ? "Deleted " : "Partial delete — ");
            sb.Append(r.Deleted + " " + r.TargetType + " removed from the tree" + (r.Verified ? " (verified gone)" : "") + ".");
            if (r.Skipped > 0) sb.Append(" " + r.Skipped + " base/reference feature" + (r.Skipped == 1 ? "" : "s") + " left intact.");
            sb.Append(" Solid body " + (r.StillHasSolid ? "intact" : "MISSING") + ", rebuild " + (r.RebuildErrors == 0 ? "clean" : r.RebuildErrors + " error(s)") + ".");
            sb.Append(" One Ctrl+Z restores everything; Forge didn't save.");
            return sb.ToString();
        }

        // body-creating feature types — the FIRST one in the tree is the base body and must never be deleted
        private static bool IsBodyCreatingType(string tn)
        {
            if (string.IsNullOrEmpty(tn)) return false;
            switch (tn)
            {
                case "Extrusion":
                case "Revolution":
                case "Loft":
                case "Sweep":
                case "Boundary":
                case "Thicken":
                case "ImportedFeature":
                case "Import3D":
                case "FeatDrawn":
                    return true;
                default:
                    return false;
            }
        }

        // reference geometry / origin — never a modeling feature, never deletable by this handler
        private static bool IsProtectedType(string tn)
        {
            if (string.IsNullOrEmpty(tn)) return false;
            switch (tn)
            {
                case "RefPlane":
                case "RefAxis":
                case "RefPoint":
                case "CoordSys":
                case "OriginProfileFeature":
                    return true;
                default:
                    return false;
            }
        }

        private static string SafeName(Feature f) { try { return f.Name; } catch { return null; } }
        private static bool HasSolid(PartDoc part) { return SolidCount(part) > 0; }
        private static int SolidCount(PartDoc part)
        {
            if (part == null) return 0;
            try { var b = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]; return b == null ? 0 : b.Length; }
            catch { return 0; }
        }
        private static int SafeWhatsWrong(IModelDoc2 model) { try { return model.Extension.GetWhatsWrongCount(); } catch { return 0; } }
    }
}
