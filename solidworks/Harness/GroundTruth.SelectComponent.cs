using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for select_component (tool 11). Same non-negotiable as select_face's GT
    /// (tool 13): the harness's own ForceRebuild3 between the handler's run and this measurement drops the
    /// live SolidWorks selection, so this NEVER re-reads live selection state. Independence axis here is
    /// TRAVERSAL + a from-scratch re-parse: walks the component TREE via GetRootComponent3/GetChildren
    /// recursion (the handler uses a flat IAssemblyDoc.GetComponents(false) array), and re-derives which
    /// name(s) the query SHOULD match with its own normalize/compare logic, then the harness script
    /// cross-checks the handler's reported pick against it.
    /// </summary>
    public static partial class GroundTruth
    {
        private static readonly string[] SelCompStopWords =
        {
            "select","the","a","an","component","instance","part","please","this","that","it",
            "named","called","of","in","assembly","find","highlight","pick","choose","click","on"
        };

        private static string SelCompNormalize(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            string s = raw;
            int at = s.IndexOf('@'); if (at >= 0) s = s.Substring(0, at);
            s = Regex.Replace(s, @"-\d+$", "");
            s = s.Replace('_', ' ').Replace('-', ' ');
            s = Regex.Replace(s, @"\s+", " ").Trim().ToLowerInvariant();
            return s;
        }

        private static string SelCompParseQuery(string intent)
        {
            string raw = intent ?? "";
            var qm = Regex.Match(raw, "[\"']([^\"']{2,})[\"']");
            if (qm.Success) return qm.Groups[1].Value.Trim();
            var words = new List<string>();
            foreach (Match wm in Regex.Matches(raw, @"[A-Za-z0-9_]+"))
            {
                string lw = wm.Value.ToLowerInvariant();
                if (Array.IndexOf(SelCompStopWords, lw) >= 0) continue;
                words.Add(wm.Value);
            }
            return words.Count == 0 ? null : string.Join(" ", words);
        }

        // deliberately walks GetChildren() from the ROOT down (excluding the root itself, which has no
        // meaningful Name2 — it IS the assembly document) — the same recursive-descent shape as
        // CountNamedComponents' independent GT, distinct from the handler's flat GetComponents(false) array.
        private static void SelCompWalk(Component2 c, List<Component2> outAll)
        {
            if (c == null) return;
            object[] kids = null; try { kids = c.GetChildren() as object[]; } catch { }
            foreach (var o in kids ?? new object[0])
            {
                var k = o as Component2; if (k == null) continue;
                outAll.Add(k);
                SelCompWalk(k, outAll);
            }
        }

        public static JObject MeasureSelectComponent(IModelDoc2 model, string intent)
        {
            var mo = new JObject();
            var asm = model as AssemblyDoc;
            if (asm == null) { mo["error"] = "not an assembly"; return mo; }

            string query = SelCompParseQuery(intent);
            mo["query"] = query;
            if (query == null) return mo;
            string normQuery = SelCompNormalize(query);

            Component2 root = null;
            try { root = model.ConfigurationManager.ActiveConfiguration.GetRootComponent3(true) as Component2; }
            catch { }

            var all = new List<Component2>();
            SelCompWalk(root, all);

            string exactName = null;
            int matchCount = 0;
            var names = new JArray();
            foreach (var c in all)
            {
                string nm = null; try { nm = c.Name2; } catch { }
                if (string.IsNullOrEmpty(nm)) continue;
                string norm = SelCompNormalize(nm);
                bool isExact = norm == normQuery;
                bool isSub = isExact || norm.Contains(normQuery) || normQuery.Contains(norm);
                if (isExact && exactName == null) exactName = nm;
                if (isSub)
                {
                    matchCount++;
                    if (names.Count < 25) names.Add(nm);
                }
            }

            mo["treeComponentCount"] = all.Count;
            mo["expectedExactName"] = exactName;
            mo["matchCount"] = matchCount;
            mo["matchNames"] = names;
            return mo;
        }
    }
}
