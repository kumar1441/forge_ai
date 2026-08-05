using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class ListFeatureDependenciesResult
    {
        public string Target;                 // resolved feature name
        public int ChildCount = -1;           // features that would break if the target went away
        public int ParentCount = -1;          // features the target itself is built on
        public List<string> Children = new List<string>();
        public List<string> Parents = new List<string>();
        public bool Resolved;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 153 — list_feature_dependencies (READ). Parents and children of one feature: what it is built ON, and what
    /// breaks if it is deleted, suppressed or reordered. This is the pre-flight every destructive feature op owes the
    /// user — "suppressing Seed-Hole also takes LPattern1 with it" — so the preview count can't silently grow at
    /// execute time (Handler Robustness Rule 3).
    ///
    /// Children come from IFeature.GetChildren, parents from IFeature.GetParents. Ground truth crosses the two: it
    /// derives children by INVERTING every feature's parent list and parents by inverting every feature's child list,
    /// so neither side can confirm its own API.
    /// </summary>
    public static class ListFeatureDependencies
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (!Regex.IsMatch(c, @"\b(depend\w*|children|child|parents?|breaks if)\b")) return false;
            // "feature" scopes it away from Impact (tool 70, DIMENSION dependents); children/parents are unambiguous alone
            return Regex.IsMatch(c, @"\bfeature") || Regex.IsMatch(c, @"\b(children|child|parent)\b");
        }

        public static async Task<ListFeatureDependenciesResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new ListFeatureDependenciesResult();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { res.Error = "Open a part to trace feature dependencies."; return res; }

            await emit("Scout", "resolving the feature", "run", null);
            var features = RealFeatures(model);
            if (features.Count == 0) { res.Error = "No modelling features (an imported dumb solid, or an empty part)."; return res; }

            string c = (intent ?? "").ToLowerInvariant();
            var hits = new List<Feature>();
            foreach (var f in features)
            {
                string n = null; try { n = f.Name; } catch { }
                if (!string.IsNullOrEmpty(n) && c.Contains(n.ToLowerInvariant())) hits.Add(f);
            }
            if (hits.Count == 0)
            {
                res.Error = "Which feature? This part has: " + string.Join(", ", NamesOf(features)) + ".";
                await emit("Scout", null, "fail", "no feature named in the request");
                return res;
            }
            // longest name wins — "Seed-Hole" beats a bare "Hole" substring, never a coin flip
            Feature target = hits[0];
            foreach (var f in hits) { try { if (f.Name.Length > target.Name.Length) target = f; } catch { } }
            try { res.Target = target.Name; } catch { }
            res.Resolved = true;

            await emit("Scout", "tracing " + res.Target, "run", null);
            res.Children = NamesOf(Related(target, true));
            res.Parents = NamesOf(Related(target, false));
            res.ChildCount = res.Children.Count;
            res.ParentCount = res.Parents.Count;

            await emit("Scout", null, "done", res.Target + " · " + res.ChildCount + " dependent, " + res.ParentCount + " referenced");

            var sb = new StringBuilder();
            if (res.ChildCount == 0) sb.Append(res.Target + " has no dependents — deleting it breaks nothing else.");
            else
            {
                sb.Append(res.ChildCount + " feature" + (res.ChildCount == 1 ? "" : "s") + " depend" + (res.ChildCount == 1 ? "s" : "") +
                          " on " + res.Target + " — delete or suppress it and " + (res.ChildCount == 1 ? "this goes" : "these go") + " too:");
                foreach (var n in res.Children) sb.Append("\n• " + n);
            }
            if (res.ParentCount > 0) sb.Append("\nBuilt on: " + string.Join(", ", res.Parents) + ".");
            res.Info = sb.ToString();
            return res;
        }

        // children == true → what this feature feeds; false → what feeds it
        private static List<Feature> Related(Feature f, bool children)
        {
            var outp = new List<Feature>();
            object[] arr = null;
            try { arr = (children ? f.GetChildren() : f.GetParents()) as object[]; } catch { }
            foreach (var o in arr ?? new object[0])
            {
                var rf = o as Feature; if (rf == null) continue;
                string tn = null; try { tn = rf.GetTypeName2(); } catch { }
                if (string.IsNullOrEmpty(tn) || !IsRealFeature(tn)) continue;
                outp.Add(rf);
            }
            return outp;
        }

        private static List<Feature> RealFeatures(IModelDoc2 model)
        {
            var list = new List<Feature>();
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                string tn = null; try { tn = f.GetTypeName2(); } catch { }
                if (!string.IsNullOrEmpty(tn) && IsRealFeature(tn)) list.Add(f);
                f = f.GetNextFeature() as Feature;
            }
            return list;
        }

        private static List<string> NamesOf(List<Feature> fs)
        {
            var names = new List<string>();
            foreach (var f in fs) { string n = null; try { n = f.Name; } catch { } if (!string.IsNullOrEmpty(n)) names.Add(n); }
            return names;
        }

        // same scaffold rule as find_features_by_type — *Folder containers and the origin scaffold are not features
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
