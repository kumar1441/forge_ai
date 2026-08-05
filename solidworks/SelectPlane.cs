using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public class SelectPlaneResult
    {
        public bool Success;
        public string Query;      // the name text parsed out of the intent ("front", "top", a custom name...)
        public string Name;       // the exact Feature.Name picked
        public int MatchCount;    // how many planes matched the query at all (1 = unambiguous)
        public string Info;
        public string Error;
        public string Question;
        public bool NeedsConfirm;
    }

    /// <summary>
    /// SelectPlane (tool 15) — WRITE-of-state: selects one reference plane — standard (Front/Top/Right) or a
    /// custom named one — so it's ready for a follow-up command (e.g. sketch-on-plane). Never modifies
    /// geometry. Works on both part and assembly docs (the SAME name-match shape as SelectComponent, one
    /// document level up: a Feature walk instead of a component walk); scope is the document's OWN top-level
    /// planes only, not planes nested inside an assembly's sub-components — a future extension, same honest
    /// scope-limit shape as SelectFace/SelectEdge's part-only notes.
    ///
    /// Standard plane names ("Front Plane"/"Top Plane"/"Right Plane") are matched the SAME name-normalize way
    /// as any custom plane — no hardcoded enum, so a renamed or non-English-UI plane still resolves as long
    /// as its Feature.Name contains the query word.
    /// </summary>
    public static class SelectPlane
    {
        private static readonly string[] StopWords =
        {
            "select","the","a","an","plane","reference","ref","please","this","that","it",
            "named","called","of","in","find","highlight","pick","choose","click","on","standard"
        };

        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            return Regex.IsMatch(c, @"\bselect\b") && Regex.IsMatch(c, @"\bplane\b");
        }

        public static async Task<SelectPlaneResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new SelectPlaneResult();
            if (model == null) { res.Error = "Open a part or assembly to select a plane."; return res; }

            string query = ParseQuery(intent);
            if (string.IsNullOrEmpty(query))
            { res.Error = "Which plane? Say front/top/right, or a custom plane's name."; return res; }
            res.Query = query;
            string normQuery = SelectComponent.Normalize(query);

            await emit("Selector", "resolving plane '" + query + "'", "run", null);
            Feature exact = null;
            var candidates = new List<Feature>();
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                string tn = null; try { tn = f.GetTypeName2(); } catch { }
                if (tn == "RefPlane")
                {
                    string nm = null; try { nm = f.Name; } catch { }
                    if (!string.IsNullOrEmpty(nm))
                    {
                        string norm = SelectComponent.Normalize(nm);
                        if (norm == normQuery) { if (exact == null) exact = f; candidates.Add(f); }
                        else if (norm.Contains(normQuery) || normQuery.Contains(norm)) candidates.Add(f);
                    }
                }
                f = f.GetNextFeature() as Feature;
            }
            res.MatchCount = candidates.Count;

            Feature pick = exact;
            if (pick == null && candidates.Count == 1) pick = candidates[0];
            if (pick == null && candidates.Count > 1)
            {
                res.NeedsConfirm = true;
                res.Question = candidates.Count + " planes match '" + query + "' and none is an exact name — which one?";
                await emit("Selector", null, "fail", res.Question);
                return res;
            }
            if (pick == null)
            { res.Error = "Couldn't find a plane matching '" + query + "'."; await emit("Selector", null, "fail", res.Error); return res; }

            model.ClearSelection2(true);
            bool selected = false;
            try { selected = pick.Select2(false, 0); } catch (Exception ex) { res.Error = "Select2 threw: " + ex.Message; return res; }
            string pickName = null; try { pickName = pick.Name; } catch { }
            if (!selected)
            { res.Error = "Found '" + pickName + "' but SolidWorks refused the selection."; await emit("Selector", null, "fail", res.Error); return res; }

            res.Success = true;
            res.Name = pickName;
            res.Info = "Selected plane '" + res.Name + "'.";
            await emit("Selector", null, "done", res.Name + " selected");
            return res;
        }

        private static string ParseQuery(string intent)
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
    }
}
