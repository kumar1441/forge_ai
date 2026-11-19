using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class UnsuppressComponentsResult
    {
        public string TargetKind;        // what to unsuppress ("all", "fasteners", a named part)
        public int Matched;              // SUPPRESSED components the target resolved to
        public int Unsuppressed;         // newly resolved AND independently confirmed active (read-back)
        public int AlreadyActive;        // matched-by-name but already active at run start (idempotency context)
        public int Failed;               // attempted but NOT confirmed active afterward (fail closed)
        public int RebuildErrors;        // GetWhatsWrongCount after the single post-write rebuild
        public bool NothingToDo;         // no suppressed components matched → idempotent no-op
        public string Info;
        public string Error;
    }

    /// <summary>
    /// UnsuppressComponents (tool unsuppress_components — the completeness PAIR of SuppressComponents, which owns the
    /// suppress verb and explicitly does NOT match "unsuppress"). A reversible STATE WRITE that RE-ACTIVATES suppressed
    /// components in the active assembly: "unsuppress the bolts", "bring back the fasteners", "restore everything",
    /// "turn the nuts back on".
    ///
    /// Deliberately a SEPARATE, lean handler (not surgery on the green SuppressComponents): it reuses the SAME proven API
    /// — IComponent2.SetSuppression2(swComponentResolved) — but resolves against SUPPRESSED components. Target resolution:
    ///   • "all" / "everything" / no kind named → every suppressed top-level component.
    ///   • fastener kinds (fasteners/hardware/bolts/nuts/screws/washers) → suppressed components of that kind (by name).
    ///   • a named part → suppressed components whose name fuzzy-matches.
    /// FAIL CLOSED (Rule #6): counts only components INDEPENDENTLY confirmed active via IsSuppressed()==false after the
    /// rebuild. IDEMPOTENT (Rule #5): if nothing suppressed matches, reports "nothing to unsuppress". Reversible; Forge
    /// never saves.
    /// </summary>
    public static class UnsuppressComponents
    {
        // "unsuppress" / "un-suppress" / "bring back" / "restore" / "reactivate" / "turn … back on" — NOT plain "suppress".
        public static bool IsUnsuppressIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (!Regex.IsMatch(c, @"\b(component|components|part|parts|bolt|bolts|nut|nuts|screw|screws|washer|washers|fastener|fasteners|hardware|everything|all)\b"))
                // still allow "bring back the X" / "restore the X" where X is a part name
                if (!Regex.IsMatch(c, @"\b(bring back|restore|reactivate|turn .* on)\b")) return false;
            return Regex.IsMatch(c, @"\b(unsuppress|un-suppress|bring back|reactivate|turn (?:it |them )?back on|turn on)\b")
                || (Regex.IsMatch(c, @"\brestore\b") && Regex.IsMatch(c, @"\b(component|components|part|parts|bolt|nut|screw|washer|fastener|hardware|everything|all)\b"));
        }

        private class Target { public string Kind; public List<Component2> Comps = new List<Component2>(); }

        // read-only resolution against SUPPRESSED components (safe to call twice)
        private static Target Resolve(AssemblyDoc asm, string intent)
        {
            var t = new Target();
            string cmd = (intent ?? "").ToLowerInvariant();
            object[] all = asm.GetComponents(false) as object[];
            object[] top = asm.GetComponents(true) as object[];

            bool wantAll = Regex.IsMatch(cmd, @"\b(all|everything|every|whole|entire)\b") ||
                           !Regex.IsMatch(cmd, @"\b(bolt|bolts|nut|nuts|screw|screws|washer|washers|fastener|fasteners|hardware)\b") &&
                           ParseNamePhrase(cmd) == null;

            bool wantFastener = Regex.IsMatch(cmd, @"\b(fastener|fasteners|hardware)\b");
            bool wantBolt = wantFastener || Regex.IsMatch(cmd, @"\b(bolt|bolts|screw|screws|cap screw|cap screws)\b");
            bool wantNut = wantFastener || Regex.IsMatch(cmd, @"\b(nut|nuts)\b");
            bool wantWasher = wantFastener || Regex.IsMatch(cmd, @"\b(washer|washers)\b");

            if (wantBolt || wantNut || wantWasher)
            {
                foreach (var o in all ?? new object[0])
                {
                    var c = o as Component2; if (c == null || !IsSup(c)) continue;   // only suppressed
                    string k = Classify(NameOf(c));
                    if ((k == "bolt" && wantBolt) || (k == "nut" && wantNut) || (k == "washer" && wantWasher)) t.Comps.Add(c);
                }
                t.Kind = wantFastener ? "fasteners" : KindLabel(wantBolt, wantNut, wantWasher);
                return t;
            }

            string phrase = ParseNamePhrase(cmd);
            if (!wantAll && phrase != null)
            {
                var tokens = Tokens(phrase);
                foreach (var o in all ?? new object[0])
                {
                    var c = o as Component2; if (c == null || !IsSup(c)) continue;
                    if (MatchesAny(NameOf(c), tokens)) t.Comps.Add(c);
                }
                t.Kind = phrase.Trim();
                return t;
            }

            // "all" / default → every suppressed component
            foreach (var o in all ?? new object[0])
            {
                var c = o as Component2; if (c == null || !IsSup(c)) continue;
                t.Comps.Add(c);
            }
            t.Kind = "all";
            return t;
        }

        public static async Task<UnsuppressComponentsResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new UnsuppressComponentsResult();
            var asm = model as AssemblyDoc;
            if (asm == null) { res.Error = "Open the assembly (.SLDASM) whose components you want to bring back."; return res; }

            await emit("Gauge", "finding suppressed components", "run", null);
            var t = Resolve(asm, intent);
            res.TargetKind = t.Kind;
            res.Matched = t.Comps.Count;

            if (res.Matched == 0)
            {
                res.NothingToDo = true;
                res.Info = "Nothing to unsuppress — no suppressed " + (t.Kind == "all" ? "components" : t.Kind) + " in this assembly.";
                await emit("Gauge", null, "done", "no suppressed " + (t.Kind == "all" ? "components" : t.Kind));
                return res;
            }
            await emit("Gauge", null, "done", res.Matched + " suppressed " + (t.Kind == "all" ? "component" + (res.Matched == 1 ? "" : "s") : t.Kind) + " to restore");

            // ---- resolve each, one at a time (Rule #4) ----
            await emit("Restorer", "unsuppressing components", "run", null);
            var attempted = new List<Component2>();
            int idx = 0;
            foreach (var c in t.Comps)
            {
                idx++;
                try { c.SetSuppression2((int)swComponentSuppressionState_e.swComponentResolved); attempted.Add(c); }
                catch { res.Failed++; }
                if (res.Matched > 25 && idx % 10 == 0) await emit(null, null, "done", "restoring… " + idx + "/" + res.Matched);
            }
            try { model.ForceRebuild3(false); } catch { }
            await emit("Restorer", null, "done", attempted.Count + " restored");

            // ---- FAIL CLOSED (Rule #6): re-read each attempted component; counts only if CONFIRMED active ----
            await emit("Sentinel", "verifying active state", "run", null);
            foreach (var c in attempted) { if (!IsSup(c)) res.Unsuppressed++; else res.Failed++; }
            try { res.RebuildErrors = model.Extension.GetWhatsWrongCount(); } catch { }
            await emit("Sentinel", null, "done",
                res.Unsuppressed + " confirmed active" + (res.Failed > 0 ? " · " + res.Failed + " unconfirmed" : "") +
                (res.RebuildErrors == 0 ? " · rebuild clean" : " · " + res.RebuildErrors + " rebuild flag(s)"));

            res.Info = BuildInfo(res);
            return res;
        }

        private static string BuildInfo(UnsuppressComponentsResult r)
        {
            var sb = new StringBuilder();
            sb.Append("Unsuppressed " + r.Unsuppressed + " " + (r.TargetKind == "all" ? "component" + (r.Unsuppressed == 1 ? "" : "s") : r.TargetKind) + ".");
            if (r.Failed > 0) sb.Append(" " + r.Failed + " couldn't be confirmed active — left for review.");
            if (r.RebuildErrors > 0) sb.Append(" " + r.RebuildErrors + " rebuild flag(s) after — check the assembly.");
            sb.Append(" Reversible: one Ctrl+Z per part, and the document was not saved.");
            return sb.ToString();
        }

        // ---- name-based classification (self-contained; same vocabulary as SuppressComponents) ----
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

        private static string ParseNamePhrase(string cmd)
        {
            var m = Regex.Match(cmd, @"\b(?:unsuppress|un-suppress|bring back|restore|reactivate|turn on|turn back on)\b\s+(?:all\s+the\s+|all\s+|the\s+)?(.+)$");
            if (!m.Success) return null;
            string p = m.Groups[1].Value.Trim();
            p = Regex.Replace(p, @"\b(component|components|part|parts|back on|on|again)\b", "", RegexOptions.IgnoreCase).Trim();
            if (p.Length == 0 || Regex.IsMatch(p, @"^(it|them|everything|all)$", RegexOptions.IgnoreCase)) return null;
            return p;
        }

        private static bool IsSup(Component2 c) { try { return c.IsSuppressed(); } catch { return false; } }
        private static string NameOf(Component2 c) { try { return c.Name2; } catch { return null; } }

        private static readonly HashSet<string> Stop = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "the", "a", "an", "and", "all", "part", "parts", "component", "components", "back", "on", "again", "them", "it", "everything" };

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
    }
}
