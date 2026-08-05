using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class FindFeatureByNameResult
    {
        public string Query;
        public string Match;              // the one confident match, if there is one
        public string MatchType;          // exact / normalized / contains
        public int TotalFeatures;
        public int Candidates;            // how many features the query could mean
        public List<string> Names = new List<string>();
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 144 — find_feature_by_name (READ). Resolves a spoken feature name to the real tree entry: "the seed hole"
    /// → Seed-Hole. In a 500-feature tree nobody types "Fillet37" exactly, and every feature WRITE (rename, suppress,
    /// delete, reorder) needs a resolved target before it can act — so this is the primitive those tools call, not a
    /// convenience.
    ///
    /// Three passes, most confident first: exact, then normalized (case/space/hyphen/underscore ignored), then
    /// substring. It never picks between equally-good candidates — several matches is ONE question with the real
    /// names in it, per Handler Robustness Rule 2.
    /// </summary>
    public static class FindFeatureByName
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (Regex.IsMatch(c, @"\b(add|create|make|delete|remove|suppress|rename|edit|change|set)\b")) return false;
            return Regex.IsMatch(c, @"\bfeature\b") && Regex.IsMatch(c, @"\b(called|named|name)\b");
        }

        public static async Task<FindFeatureByNameResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new FindFeatureByNameResult();
            if (model == null) { res.Error = "Open a part or assembly to search its feature tree."; return res; }

            res.Query = ExtractQuery(intent);
            if (string.IsNullOrWhiteSpace(res.Query)) { res.Error = "Which feature name should I look for?"; return res; }

            await emit("Scout", "searching the tree for '" + res.Query + "'", "run", null);

            var names = new List<string>();
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                string tn = null; try { tn = f.GetTypeName2(); } catch { }
                if (!string.IsNullOrEmpty(tn) && IsRealFeature(tn))
                {
                    string n = null; try { n = f.Name; } catch { }
                    if (!string.IsNullOrEmpty(n)) names.Add(n);
                }
                f = f.GetNextFeature() as Feature;
            }
            res.TotalFeatures = names.Count;

            string q = res.Query;
            var exact = names.FindAll(n => string.Equals(n, q, StringComparison.OrdinalIgnoreCase));
            var norm = names.FindAll(n => Normalize(n) == Normalize(q));
            var part = names.FindAll(n => Normalize(n).Contains(Normalize(q)));

            List<string> hits; string how;
            if (exact.Count > 0) { hits = exact; how = "exact"; }
            else if (norm.Count > 0) { hits = norm; how = "normalized"; }
            else { hits = part; how = "contains"; }

            res.Names = hits;
            res.Candidates = hits.Count;

            if (hits.Count == 1) { res.Match = hits[0]; res.MatchType = how; }
            else if (hits.Count == 0)
            {
                res.Error = "No feature matching '" + res.Query + "'. The tree has: " + Join(names) + ".";
                await emit("Scout", null, "fail", "no match for '" + res.Query + "'");
                return res;
            }
            else
            {
                res.MatchType = how;
                res.Error = hits.Count + " features match '" + res.Query + "' — which one? " + Join(hits);
                await emit("Scout", null, "fail", hits.Count + " candidates — asking");
                return res;
            }

            await emit("Scout", null, "done", "'" + res.Query + "' → " + res.Match + " (" + how + ")");
            res.Info = "'" + res.Query + "' is " + res.Match +
                       (how == "exact" ? "." : " (matched by " + how + " — " + res.TotalFeatures + " features searched).");
            return res;
        }

        // pull the quoted or trailing name out of "find the feature called seed hole"
        private static string ExtractQuery(string intent)
        {
            string s = (intent ?? "").Trim();
            var m = Regex.Match(s, "[\"'“”‘’]([^\"'“”‘’]+)[\"'“”‘’]");
            if (m.Success) return m.Groups[1].Value.Trim();
            m = Regex.Match(s, @"\b(?:called|named)\s+(.+)$", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value.Trim().TrimEnd('?', '.', '!');
            m = Regex.Match(s, @"\bfeature\s+name\s+(.+)$", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value.Trim().TrimEnd('?', '.', '!');
            return null;
        }

        // case, spaces, hyphens and underscores are noise when a human says a feature name out loud
        private static string Normalize(string s)
        {
            if (s == null) return "";
            var sb = new StringBuilder();
            foreach (char ch in s) if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            return sb.ToString();
        }

        private static string Join(List<string> names)
        {
            if (names.Count <= 12) return string.Join(", ", names.ToArray());
            return string.Join(", ", names.GetRange(0, 12).ToArray()) + " … (" + (names.Count - 12) + " more)";
        }

        private static bool IsRealFeature(string tn)
        {
            if (tn.EndsWith("Folder", StringComparison.OrdinalIgnoreCase)) return false;
            switch (tn)
            {
                case "RefPlane": case "RefAxis": case "OriginProfileFeature": case "CoordSys":
                case "DetailCabinet": case "HistoryFolder": case "SketchBlockDef": return false;
                default: return true;
            }
        }
    }
}
