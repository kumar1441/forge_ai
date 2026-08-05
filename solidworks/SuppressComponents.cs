using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class SuppressComponentsResult
    {
        public string TargetKind;       // what the user asked to suppress ("fasteners", "everything except housing", …)
        public int Matched;             // components the target resolved to in the live model
        public int Suppressed;          // newly suppressed AND independently confirmed suppressed (read-back)
        public int AlreadySuppressed;   // matched but already suppressed at run start (idempotency)
        public int Failed;              // attempted but NOT confirmed suppressed afterward (fail closed)
        public int RebuildErrors;       // GetWhatsWrongCount after the single post-suppress rebuild
        public string Info;             // verdict-first one-liner
        public string Error;            // set => nothing was suppressed (ambiguity / wrong doc)
    }

    /// <summary>
    /// SuppressComponents (tool "strip an assembly" / "suppress the fasteners"). A WRITE handler that SUPPRESSES a
    /// resolved set of components in the active assembly. Suppression is a reversible STATE change — no geometry is
    /// edited — so it is inherently undoable (one Ctrl+Z per component) and Forge never saves the document.
    ///
    /// Target resolution is grounded in the LIVE model (Rule #8):
    ///   • "fasteners"/"hardware"/"bolts"/"nuts"/"screws"/"washers" → components classified as that kind by name.
    ///   • "everything except <name>" / "all but <name>" / "strip (it down) to <name>" → every TOP-LEVEL component
    ///     whose name does NOT fuzzy-match <name> (the named part(s) are kept).
    ///   • a specific named part → fuzzy match.
    /// Zero matches → it ASKS one question naming the kinds that ARE present (Rule #2), suppressing nothing.
    /// Per-component try/continue with a post-rebuild read-back: Suppressed counts only components INDEPENDENTLY
    /// confirmed suppressed via IsSuppressed() — never the SetSuppression2 return code (Rule #6). Idempotent: a
    /// component already suppressed is skipped; a rerun reports "already suppressed N, nothing to do" (Rule #5).
    /// </summary>
    public static class SuppressComponents
    {
        // "suppress …" / "strip …" / "hide …" — NOT "unsuppress" (\bsuppress\b won't match inside "unsuppress").
        // "hide" added for test-loop hedged finding suppress-measure-unsuppress-endseal ("hide the endseal, measure
        // ..., then show it again") — the cloud already maps "hide" to the suppress_components ACTION (it
        // understands the synonym semantically), but Resolve()'s own target-phrase extraction below required the
        // LITERAL word "suppress" in the text, which a "hide"-phrased chain never contains, so it always asked
        // "I couldn't tell what to suppress" even though the cloud had already correctly routed it. No collision:
        // Isolate.IsIntent only matches the more specific "hide the rest"; Simplify.IsIntent requires "hide" AND a
        // fillet/hole/cosmetic word together — neither fires on a bare "hide <component name>".
        // "kill"/"remove"/"get rid of" added for test-loop hedged finding remove-15kw-and-create-subassembly ("kill
        // the 15kw motor then group the 11 and 7.5 into a subassembly") — same bug class as "hide": the cloud
        // correctly routed to suppress_components (action=suppress_components in the finding's own panel_messages),
        // but Resolve()'s phrase-extraction below only recognized "suppress"/"hide", so it asked "I couldn't tell
        // what to suppress" on a request that named the target plainly. No collision found (grepped the codebase):
        // no other handler's IsIntent matches kill/remove/"get rid of", and DeleteFeature (the one PART-permanent-
        // delete tool that could plausibly claim "remove") is PART-guarded so it never competes on an assembly.
        public static bool IsSuppressIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            return Regex.IsMatch(cmd, @"\bsuppress\b|\bstrip\b|\bhide\b|\bkill\b|\bremove\b|\bget\s+rid\s+of\b", RegexOptions.IgnoreCase);
        }

        private class Target { public string Kind; public List<Component2> Comps = new List<Component2>(); public string Ask; }

        // ---- read-only target resolution: shared by PreviewLine and Run (calling it twice is safe — it writes nothing) ----
        private static Target Resolve(AssemblyDoc asm, string intent)
        {
            var t = new Target();
            string cmd = (intent ?? "").ToLowerInvariant();
            object[] all = asm.GetComponents(false) as object[];
            object[] top = asm.GetComponents(true) as object[];

            // ---- "everything except X" / "all but X" / "strip (it down) to X" → keep the named part(s), suppress the rest ----
            string keepPhrase = null;
            var mEx = Regex.Match(cmd, @"\b(?:except|but|besides|other than|apart from|save for)\b\s+(.+)$");
            if (mEx.Success) keepPhrase = mEx.Groups[1].Value;
            else { var mTo = Regex.Match(cmd, @"\bstrip\b.*?\bto\b\s+(.+)$"); if (mTo.Success) keepPhrase = mTo.Groups[1].Value; }

            if (keepPhrase != null)
            {
                var keepTokens = Tokens(keepPhrase);
                var keep = new List<Component2>();
                var rest = new List<Component2>();
                foreach (var o in top ?? new object[0])
                {
                    var c = o as Component2; if (c == null) continue;
                    if (IsSup(c)) continue;                          // already inactive — ignore
                    if (MatchesAny(NameOf(c), keepTokens)) keep.Add(c); else rest.Add(c);
                }
                t.Kind = "everything except " + keepPhrase.Trim();
                if (keep.Count == 0) { t.Ask = "Nothing here matches \"" + keepPhrase.Trim() + "\" to keep. " + Present(all); return t; }
                if (rest.Count == 0) { t.Ask = "Every component matches \"" + keepPhrase.Trim() + "\" — there's nothing left to suppress."; return t; }
                t.Comps = rest;
                return t;
            }

            // ---- kind keywords (fasteners / hardware / bolts / nuts / screws / washers) — union of requested kinds ----
            bool wantFastener = Regex.IsMatch(cmd, @"\b(fastener|fasteners|hardware)\b");
            bool wantBolt = wantFastener || Regex.IsMatch(cmd, @"\b(bolt|bolts|screw|screws|cap screw|cap screws)\b");
            bool wantNut = wantFastener || Regex.IsMatch(cmd, @"\b(nut|nuts)\b");
            bool wantWasher = wantFastener || Regex.IsMatch(cmd, @"\b(washer|washers)\b");
            if (wantBolt || wantNut || wantWasher)
            {
                foreach (var o in all ?? new object[0])
                {
                    var c = o as Component2; if (c == null) continue;
                    string k = Classify(NameOf(c));
                    if ((k == "bolt" && wantBolt) || (k == "nut" && wantNut) || (k == "washer" && wantWasher)) t.Comps.Add(c);
                }
                t.Kind = wantFastener ? "fasteners" : KindLabel(wantBolt, wantNut, wantWasher);
                if (t.Comps.Count == 0) t.Ask = "No " + t.Kind + " found. " + Present(all);
                return t;
            }

            // ---- a specific named part (fuzzy) ----
            // "hide"/"kill"/"remove"/"get rid of" added alongside "suppress" (see IsSuppressIntent) — same findings.
            var mName = Regex.Match(cmd, @"\b(?:suppress|hide|kill|remove|get\s+rid\s+of)\b\s+(?:all\s+the\s+|all\s+|the\s+)?(.+)$");
            string phrase = mName.Success ? mName.Groups[1].Value.Trim() : null;
            // A CHAINED command ("hide the endseal, measure from the block face to the endcap, then show it
            // again") must not bleed the OTHER legs' words into THIS leg's target-name tokens — test-loop hedged
            // finding suppress-measure-unsuppress-endseal: without this cut, "block"/"endcap" (real component
            // names in the SAME sentence) would ALSO fuzzy-match and get suppressed alongside "endseal", an
            // over-broad write the user never asked for. Cut at the first clause boundary.
            if (phrase != null)
            {
                var cut = Regex.Match(phrase, @"^(.*?)(?:,|\bthen\b|\band\s+(?:measure|show|check|verify|rotate|move|unsuppress|delete)\b)", RegexOptions.IgnoreCase);
                if (cut.Success) phrase = cut.Groups[1].Value.Trim();
            }
            var tokens = Tokens(phrase);
            if (tokens.Count > 0)
            {
                foreach (var o in all ?? new object[0])
                {
                    var c = o as Component2; if (c == null) continue;
                    if (MatchesAny(NameOf(c), tokens)) t.Comps.Add(c);
                }
                t.Kind = phrase;
            }
            if (t.Comps.Count == 0) t.Ask = "I couldn't tell what to suppress" + (phrase != null ? " from \"" + phrase + "\"" : "") + ". " + Present(all);
            return t;
        }

        // ---- broad, definition-changing preview (Rule #3): only when the set is >3 and unambiguous, else null (execute directly) ----
        public static string PreviewLine(IModelDoc2 model, string intent)
        {
            var asm = model as AssemblyDoc; if (asm == null) return null;
            var t = Resolve(asm, intent);
            if (t.Ask != null || t.Comps.Count == 0) return null;   // let Run() ask / no-op
            int toDo = 0; foreach (var c in t.Comps) if (!IsSup(c)) toDo++;
            if (toDo <= 3) return null;                             // small unambiguous op → run directly
            int active = ActiveCount(asm);
            return "Suppressing " + toDo + " of " + active + " components (" + t.Kind + ") — reversible (one Ctrl+Z per part, and Forge never saves)";
        }

        public static async Task<SuppressComponentsResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new SuppressComponentsResult();
            var asm = model as AssemblyDoc;
            if (asm == null) { res.Error = "Open the assembly (.SLDASM) you want to strip down."; return res; }

            await emit("Gauge", "resolving what to suppress", "run", null);
            GroundTruth.Trace?.Invoke("SuppressComponents: Resolve start");
            var t = Resolve(asm, intent);
            GroundTruth.Trace?.Invoke("SuppressComponents: Resolve done, matched=" + t.Comps.Count + " ask=" + (t.Ask ?? "(none)"));
            res.TargetKind = t.Kind;
            if (t.Ask != null) { await emit("Gauge", null, "fail", t.Ask); res.Error = t.Ask; return res; }
            res.Matched = t.Comps.Count;
            await emit("Gauge", null, "done", res.Matched + " component" + (res.Matched == 1 ? "" : "s") + " match \"" + t.Kind + "\"");

            // ---- suppress each matched component, one at a time, per-item try/continue (Rule #4) ----
            await emit("Stripper", "suppressing components", "run", null);
            var attempted = new List<Component2>();
            int idx = 0;
            foreach (var c in t.Comps)
            {
                idx++;
                GroundTruth.Trace?.Invoke("SuppressComponents: item " + idx + "/" + t.Comps.Count + " (" + (NameOf(c) ?? "?") + ") start");
                if (IsSup(c)) { res.AlreadySuppressed++; GroundTruth.Trace?.Invoke("SuppressComponents: item " + idx + " already suppressed"); continue; }   // idempotent skip (Rule #5)
                try { c.SetSuppression2((int)swComponentSuppressionState_e.swComponentSuppressed); attempted.Add(c); }
                catch (Exception ex) { res.Failed++; GroundTruth.Trace?.Invoke("SuppressComponents: item " + idx + " SetSuppression2 threw " + ex.GetType().Name); }
                GroundTruth.Trace?.Invoke("SuppressComponents: item " + idx + " done");
                if (res.Matched > 25 && idx % 10 == 0) await emit(null, null, "done", "suppressing… " + idx + "/" + res.Matched);
            }
            GroundTruth.Trace?.Invoke("SuppressComponents: ForceRebuild3 start");
            try { model.ForceRebuild3(false); } catch { }
            GroundTruth.Trace?.Invoke("SuppressComponents: ForceRebuild3 done");
            await emit("Stripper", null, "done", attempted.Count + " suppressed · " + res.AlreadySuppressed + " already");

            // ---- FAIL CLOSED (Rule #6): re-read each attempted component's state; a component counts only if CONFIRMED suppressed ----
            await emit("Sentinel", "verifying suppression state", "run", null);
            foreach (var c in attempted)
            {
                if (IsSup(c)) res.Suppressed++; else res.Failed++;
            }
            GroundTruth.Trace?.Invoke("SuppressComponents: GetWhatsWrongCount start");
            try { res.RebuildErrors = model.Extension.GetWhatsWrongCount(); } catch { }
            GroundTruth.Trace?.Invoke("SuppressComponents: GetWhatsWrongCount done");
            await emit("Sentinel", null, "done",
                res.Suppressed + " confirmed suppressed" + (res.Failed > 0 ? " · " + res.Failed + " unconfirmed" : "") +
                (res.RebuildErrors == 0 ? " · rebuild clean" : " · " + res.RebuildErrors + " rebuild flag(s)"));

            res.Info = BuildInfo(res);
            return res;
        }

        // ---- verdict first (Character #3), the number not the adjective (Character #2), honest about what was VERIFIED ----
        private static string BuildInfo(SuppressComponentsResult r)
        {
            if (r.Matched > 0 && r.Suppressed == 0 && r.Failed == 0 && r.AlreadySuppressed == r.Matched)
                return "Already suppressed all " + r.Matched + " " + r.TargetKind + " — nothing to do.";

            var sb = new StringBuilder();
            sb.Append("Suppressed " + r.Suppressed + " " + r.TargetKind + ".");
            if (r.AlreadySuppressed > 0) sb.Append(" " + r.AlreadySuppressed + " already suppressed.");
            if (r.Failed > 0) sb.Append(" " + r.Failed + " couldn't be confirmed suppressed — left for review.");
            if (r.RebuildErrors > 0) sb.Append(" " + r.RebuildErrors + " rebuild flag(s) after suppressing — check the assembly.");
            sb.Append(" Reversible: one Ctrl+Z per part, and the document was not saved.");
            return sb.ToString();
        }

        // ---- name-based kind classification (self-contained fastener vocabulary, same spirit as Scout.FastenerHints) ----
        private static readonly string[] BoltHints = { "bolt", "screw", "hcs", "shcs", "cap", "socket", "hex", "stud", "vis", "boulon", "bulong", "iso", "din", "b18" };
        private static string Classify(string n)
        {
            if (string.IsNullOrEmpty(n)) return "other";
            n = n.ToLowerInvariant();
            if (n.Contains("nut") || n.Contains("ecrou")) return "nut";
            if (n.Contains("washer") || n.Contains("rondelle")) return "washer";
            foreach (var h in BoltHints) if (n.Contains(h)) return "bolt";
            return "other";
        }

        // coarse human label for the "what's present" prompt when nothing matched
        private static string Coarse(string n)
        {
            if (string.IsNullOrEmpty(n)) return "other";
            n = n.ToLowerInvariant();
            if (n.Contains("nut") || n.Contains("ecrou")) return "nuts";
            if (n.Contains("washer") || n.Contains("rondelle")) return "washers";
            foreach (var h in BoltHints) if (n.Contains(h)) return "bolts";
            if (n.Contains("gear")) return "gears";
            if (n.Contains("shaft")) return "shafts";
            if (n.Contains("flange")) return "flanges";
            if (n.Contains("housing") || n.Contains("case") || n.Contains("casing")) return "housings";
            if (n.Contains("bracket")) return "brackets";
            if (n.Contains("plate")) return "plates";
            return "other";
        }

        // "This assembly has: bolts, nuts, flanges, shafts. What should I suppress?" — grounded in the live model
        private static string Present(object[] all)
        {
            var counts = new Dictionary<string, int>();
            foreach (var o in all ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                if (IsSup(c)) continue;
                string k = Coarse(NameOf(c));
                int n; counts.TryGetValue(k, out n); counts[k] = n + 1;
            }
            var kinds = new List<KeyValuePair<string, int>>(counts);
            kinds.Sort((a, b) => b.Value.CompareTo(a.Value));
            var labels = new List<string>();
            foreach (var kv in kinds) { labels.Add(kv.Key); if (labels.Count >= 6) break; }
            if (labels.Count == 0) return "What should I suppress?";
            return "This assembly has: " + string.Join(", ", labels) + ". What should I suppress?";
        }

        private static string KindLabel(bool b, bool n, bool w)
        {
            var parts = new List<string>();
            if (b) parts.Add("bolts");
            if (n) parts.Add("nuts");
            if (w) parts.Add("washers");
            if (parts.Count == 0) return "fasteners";
            if (parts.Count == 1) return parts[0];
            return string.Join(" and ", parts);
        }

        // ---- small helpers ----
        private static bool IsSup(Component2 c) { try { return c.IsSuppressed(); } catch { return false; } }
        private static string NameOf(Component2 c) { try { return c.Name2; } catch { return null; } }

        private static readonly HashSet<string> Stop = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "the", "a", "an", "and", "all", "part", "parts", "component", "components", "one", "ones", "them", "it", "everything", "other", "than", "just", "only", "down", "to" };

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

        private static int ActiveCount(AssemblyDoc asm)
        {
            int a = 0;
            foreach (var o in (asm.GetComponents(false) as object[]) ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                if (!IsSup(c)) a++;
            }
            return a;
        }
    }
}
