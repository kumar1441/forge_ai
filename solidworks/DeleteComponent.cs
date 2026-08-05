using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class DeleteComponentResult
    {
        public string TargetFilter;
        public int Matched;
        public int Deleted;              // components gone from the tree afterwards (independent recount)
        public int Failed;
        public bool AlreadyDone;         // nothing matched — already deleted (idempotent rerun)
        public bool Verified;
        public int RebuildErrors;
        public int BeforeTotal;
        public int AfterTotal;
        public bool NeedsConfirm;
        public string Question;
        public string Diag;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// DeleteComponent (tool #30 delete_component, WRITE) — remove component instances (and their mates) from an assembly.
    /// "delete the bolts", "remove the washer", "get rid of all the nuts". Naturally IDEMPOTENT (Rule #5): a rerun finds
    /// the targets already gone and reports nothing to do. Deleting a mated component takes its mates with it (SW cascade),
    /// so the remaining components are left clean. FAIL CLOSED (Rule #6): verified by an independent tree recount — the
    /// total must drop by exactly the matched count. UNDO is sacred (Rule #7); Forge never saves.
    /// </summary>
    public static class DeleteComponent
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (!Regex.IsMatch(c, @"\b(delete|remove|get rid of|kill|drop|take out)\b")) return false;
            // a FEATURE / MATE / CONFIG / PROPERTY delete is a different tool. test-loop-adjacent bug (found
            // regression-sweeping GeometryDefeature, not itself a test-loop finding): most of these words were
            // singular-only, so a PLURAL phrasing ("remove the small holes", "remove the fillets") slipped past
            // the exclusion and — combined with a generic "part"/"parts" word elsewhere in the same sentence
            // satisfying the component-kind check below — DeleteComponent silently hijacked a geometry_defeature
            // request. All made plural-aware (s? suffix) so this can't shadow again for any of these words.
            if (Regex.IsMatch(c, @"\b(fillets?|chamfers?|holes?|cuts?|pockets?|boss(?:es)?|extrudes?|extrusions?|ribs?|drafts?|shells?|threads?|mirrors?|features?|mates?|configs?|configurations?|properties|property|equations?|dimensions?)\b")) return false;
            // MUST name a component kind
            return Regex.IsMatch(c, @"\b(component|components|part|parts|instance|instances|bolt|bolts|nut|nuts|screw|screws|washer|washers|fastener|fasteners|housing|shaft|gear|flange|flanges)\b");
        }

        public static async Task<DeleteComponentResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new DeleteComponentResult();
            var asm = model as AssemblyDoc;
            if (asm == null) { res.Error = "Open the assembly (.SLDASM) to delete components."; return res; }

            string cmd = (intent ?? "").ToLowerInvariant();
            await emit("Gauge", "resolving which components to delete", "run", null);

            var comps = new List<Component2>();
            foreach (var o in (asm.GetComponents(true) as object[]) ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { }
                if (!sup) comps.Add(c);
            }
            res.BeforeTotal = comps.Count;

            var targets = ResolveTargets(comps, cmd, out string filter);
            res.TargetFilter = filter;
            res.Matched = targets.Count;

            if (targets.Count == 0)
            {
                // nothing matched — either an ambiguous ask or an idempotent rerun (already deleted)
                res.AlreadyDone = true; res.Verified = true; res.AfterTotal = comps.Count;
                res.Info = "No matching components to delete — nothing to do (they may already be gone).";
                res.Diag = "matched=0 total=" + comps.Count;
                await emit("Gauge", null, "done", "nothing matched — already deleted");
                return res;
            }

            await emit("Gauge", null, "done", "deleting " + targets.Count + " component(s): " + filter);
            await emit("Scribe", "removing the components", "run", null);

            try { model.ClearSelection2(true); } catch { }
            int sel = 0;
            foreach (var c in targets) { try { if (c.Select4(sel > 0, null, false)) sel++; } catch { } }
            try { model.Extension.DeleteSelection2((int)swDeleteSelectionOptions_e.swDelete_Children); }
            catch (Exception ex) { res.Error = "SolidWorks refused to delete the components (" + ex.GetType().Name + ")."; try { model.ClearSelection2(true); } catch { } return res; }
            try { model.ClearSelection2(true); } catch { }
            try { model.EditRebuild3(); } catch { try { model.ForceRebuild3(false); } catch { } }
            res.RebuildErrors = SafeWhatsWrong(model);

            // ---- Sentinel: FAIL CLOSED — recount the tree independently ----
            await emit("Sentinel", "verifying the components are gone", "run", null);
            var after = new List<Component2>();
            foreach (var o in (asm.GetComponents(true) as object[]) ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { }
                if (!sup) after.Add(c);
            }
            res.AfterTotal = after.Count;
            res.Deleted = res.BeforeTotal - res.AfterTotal;
            res.Failed = res.Matched - res.Deleted;
            res.Verified = res.Deleted == res.Matched && res.RebuildErrors == 0;
            res.Diag = "before=" + res.BeforeTotal + " after=" + res.AfterTotal + " matched=" + res.Matched + " deleted=" + res.Deleted + " rebuildErr=" + res.RebuildErrors;

            if (!res.Verified)
            {
                res.Error = res.RebuildErrors > 0 ? "The delete left " + res.RebuildErrors + " rebuild error(s). " + res.Diag
                          : "Deleted " + res.Deleted + " of " + res.Matched + " (count didn't drop as expected). " + res.Diag;
                await emit("Sentinel", null, "fail", res.Error);
                return res;
            }

            res.Info = "Deleted " + res.Deleted + " component(s) (" + filter + ") and their mates. One Ctrl+Z restores them; Forge didn't save.";
            await emit("Sentinel", null, "done", res.Deleted + " gone · " + res.AfterTotal + " left · clean");
            return res;
        }

        private static List<Component2> ResolveTargets(List<Component2> comps, string cmd, out string filter)
        {
            filter = "all";
            string kind = null;
            foreach (var kw in new[] { "bolt", "nut", "washer", "screw", "fastener", "shaft", "gear", "housing", "flange" })
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
            // "delete all components / all parts" -> everything (rare; guarded by the preview in a real UI)
            if (Regex.IsMatch(cmd, @"\b(everything|all components|all parts|all the components|all the parts)\b")) { filter = "all"; return comps; }

            // test-loop hedge fix (delete-tamper): the request names an actual COMPONENT by its own name ("delete the
            // tamper component"), not one of the fixed kind keywords above. Fall back to a fuzzy name match (same
            // Stop-word/Tokens/MatchesAny shape as SuppressComponents.Resolve's "specific named part" branch) instead
            // of reporting "nothing to do" on a real, doable delete.
            var mName = Regex.Match(cmd, @"\b(?:delete|remove|get rid of|kill|drop|take out)\b\s+(.+)$");
            string phrase = mName.Success ? mName.Groups[1].Value.Trim() : null;
            var tokens = Tokens(phrase);
            if (tokens.Count > 0)
            {
                var byName = new List<Component2>();
                foreach (var c in comps)
                {
                    string nm = NameOf(c);
                    if (nm != null && MatchesAny(nm, tokens)) byName.Add(c);
                }
                if (byName.Count > 0) { filter = phrase; return byName; }
            }
            return new List<Component2>();
        }

        private static string NameOf(Component2 c) { try { return c.Name2; } catch { return null; } }

        private static readonly HashSet<string> Stop = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "the", "a", "an", "and", "all", "part", "parts", "component", "components", "instance", "instances", "one", "ones", "them", "it", "everything", "other", "than", "just", "only" };

        private static List<string> Tokens(string phrase)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(phrase)) return list;
            foreach (Match m in Regex.Matches(phrase.ToLowerInvariant(), @"[a-z0-9]+"))
            {
                string w = m.Value;
                if (w.Length < 2 || Stop.Contains(w)) continue;
                list.Add(w);
            }
            return list;
        }

        // fuzzy: component name contains a token, or the token's singular (drop trailing 's')
        private static bool MatchesAny(string name, List<string> tokens)
        {
            if (string.IsNullOrEmpty(name) || tokens == null || tokens.Count == 0) return false;
            string n = name.ToLowerInvariant();
            foreach (var tk in tokens)
            {
                if (n.Contains(tk)) return true;
                if (tk.Length > 3 && tk.EndsWith("s") && n.Contains(tk.Substring(0, tk.Length - 1))) return true;
            }
            return false;
        }

        // ---- broad, definition-changing preview (Rule #3): only when the set is >3 and unambiguous, else null (execute directly) ----
        public static string PreviewLine(IModelDoc2 model, string intent)
        {
            var asm = model as AssemblyDoc; if (asm == null) return null;
            var comps = new List<Component2>();
            foreach (var o in (asm.GetComponents(true) as object[]) ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { }
                if (!sup) comps.Add(c);
            }
            string cmd = (intent ?? "").ToLowerInvariant();
            var targets = ResolveTargets(comps, cmd, out string filter);
            if (targets.Count <= 3) return null;   // small unambiguous op -> run directly
            return "Deleting " + targets.Count + " of " + comps.Count + " components (" + filter + ") and their mates — reversible (one Ctrl+Z, Forge never saves)";
        }

        private static bool MatchesKind(string nm, string kind)
        {
            if (kind == "fastener") return nm.Contains("bolt") || nm.Contains("nut") || nm.Contains("screw") || nm.Contains("washer") || nm.Contains("hcs") || nm.Contains("hex");
            if (kind == "bolt" || kind == "screw") { if (nm.Contains("nut") || nm.Contains("washer") || nm.Contains("plate")) return false; return nm.Contains("bolt") || nm.Contains("screw") || nm.Contains("hcs") || nm.Contains("hex"); }
            return nm.Contains(kind);
        }

        private static int SafeWhatsWrong(IModelDoc2 model) { try { return model.Extension.GetWhatsWrongCount(); } catch { return 0; } }
    }
}
