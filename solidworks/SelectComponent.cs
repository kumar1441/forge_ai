using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public class SelectComponentResult
    {
        public bool Success;
        public string Query;          // the name text parsed out of the intent
        public string Name;           // the exact Component2.Name2 picked
        public bool Suppressed;
        public int MatchCount;        // how many components matched the query at all (1 = unambiguous)
        public string Info;
        public string Error;
        public string Question;
        public bool NeedsConfirm;
    }

    /// <summary>
    /// SelectComponent (tool 11) — WRITE-of-state: selects one assembly component by (near-)exact name
    /// ("select the Bracket_Mount component", "select 'Flange Plate-2'") so it's ready for a follow-up
    /// command. Never modifies geometry. Assembly-doc only — SelectFace.cs already covers part-level face
    /// selection; SelectByFilter.cs covers bulk KIND selection (bolts/nuts/...). This is the third leg:
    /// ONE named component, exact-first.
    ///
    /// Matching: normalize both the parsed query and every component's Name2 (strip the trailing SW
    /// instance suffix "-N" and any "@Config" tag, fold '_'/'-' to spaces, collapse whitespace, lowercase).
    /// An exact normalized match always wins over a substring one — never let a fuzzy hit beat a real
    /// name match. Ambiguous ties (e.g. two components share a normalized base name) are asked, not
    /// guessed (Rule #2) — but a query that already carries a distinguishing instance number
    /// ("Bracket_Mount-2") resolves straight through.
    /// </summary>
    public static class SelectComponent
    {
        private static readonly string[] StopWords =
        {
            "select","the","a","an","component","instance","part","please","this","that","it",
            "named","called","of","in","assembly","find","highlight","pick","choose","click","on"
        };

        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (!Regex.IsMatch(c, @"\bselect\b")) return false;
            // disjoint from SelectFace (face-level) and the not-yet-built select_edge/select_plane siblings.
            if (Regex.IsMatch(c, @"\b(face|edge|plane|vertex|sketch)\b")) return false;
            // disjoint from SelectByFilter's bulk KIND roster ("select all the bolts") and its bulk verbs.
            if (Regex.IsMatch(c, @"\b(all|every|which|filter|how many|count)\b")) return false;
            if (Regex.IsMatch(c, @"\b(bolt|bolts|nut|nuts|washer|washers|screw|screws|flange|flanges|shaft|shafts|gear|gears)\b")) return false;
            // must actually name a specific component — the word "component" or a quoted name.
            return Regex.IsMatch(c, @"\bcomponent\b") || Regex.IsMatch(c, "[\"']([^\"']{2,})[\"']");
        }

        public static async Task<SelectComponentResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new SelectComponentResult();
            var asm = model as AssemblyDoc;
            if (asm == null) { res.Error = "Open the assembly (.SLDASM) to select a component."; return res; }

            string query = ParseCandidateName(intent);
            if (string.IsNullOrEmpty(query))
            { res.Error = "Which component? Give me its name."; return res; }
            res.Query = query;
            string normQuery = Normalize(query);

            await emit("Selector", "resolving component '" + query + "'", "run", null);
            object[] comps = null;
            try { comps = asm.GetComponents(false) as object[]; }
            catch (Exception ex) { res.Error = "Couldn't read the assembly's components: " + ex.Message; return res; }

            Component2 exact = null;
            var candidates = new List<Component2>();
            foreach (var o in comps ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                string nm = null; try { nm = c.Name2; } catch { }
                if (string.IsNullOrEmpty(nm)) continue;
                string norm = Normalize(nm);
                if (norm == normQuery) { if (exact == null) exact = c; candidates.Add(c); }
                else if (norm.Contains(normQuery) || normQuery.Contains(norm)) candidates.Add(c);
            }
            res.MatchCount = candidates.Count;

            Component2 pick = exact;
            if (pick == null && candidates.Count == 1) pick = candidates[0];
            if (pick == null && candidates.Count > 1)
            {
                res.NeedsConfirm = true;
                res.Question = candidates.Count + " components match '" + query + "' and none is an exact name — " +
                    "which one? (e.g. include the instance number, like '" + SafeName(candidates[0]) + "').";
                await emit("Selector", null, "fail", res.Question);
                return res;
            }
            if (pick == null)
            { res.Error = "Couldn't find a component matching '" + query + "'."; await emit("Selector", null, "fail", res.Error); return res; }

            model.ClearSelection2(true);
            bool selected = false;
            try { selected = pick.Select4(false, null, false); } catch (Exception ex) { res.Error = "Select4 threw: " + ex.Message; return res; }
            if (!selected)
            { res.Error = "Found '" + SafeName(pick) + "' but SolidWorks refused the selection."; await emit("Selector", null, "fail", res.Error); return res; }

            res.Success = true;
            res.Name = SafeName(pick);
            try { res.Suppressed = pick.IsSuppressed(); } catch { }
            res.Info = "Selected component '" + res.Name + "'" + (res.Suppressed ? " (suppressed)" : "") + ".";
            await emit("Selector", null, "done", res.Name + " selected");
            return res;
        }

        private static string SafeName(Component2 c) { try { return c.Name2; } catch { return "?"; } }

        private static string ParseCandidateName(string intent)
        {
            string raw = intent ?? "";
            var qm = Regex.Match(raw, "[\"']([^\"']{2,})[\"']");
            if (qm.Success) return qm.Groups[1].Value.Trim();

            var words = new List<string>();
            foreach (Match wm in Regex.Matches(raw, @"[A-Za-z0-9_]+"))
            {
                string lw = wm.Value.ToLowerInvariant();
                if (Array.IndexOf(StopWords, lw) >= 0) continue;
                words.Add(wm.Value);
            }
            return words.Count == 0 ? null : string.Join(" ", words);
        }

        // strips the SW instance suffix ("-3") and config tag ("@Config"), folds separators, collapses
        // whitespace, lowercases — so "Bracket_Mount-2@Default" and "bracket mount" compare equal.
        internal static string Normalize(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            string s = raw;
            int at = s.IndexOf('@'); if (at >= 0) s = s.Substring(0, at);
            s = Regex.Replace(s, @"-\d+$", "");
            s = s.Replace('_', ' ').Replace('-', ' ');
            s = Regex.Replace(s, @"\s+", " ").Trim().ToLowerInvariant();
            return s;
        }
    }
}
