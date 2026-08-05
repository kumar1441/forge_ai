using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public class FeatureTreeResult
    {
        public int TotalFeatures = -1;
        public int Suppressed;
        public Dictionary<string, int> ByType = new Dictionary<string, int>();
        public List<string> TopTypes = new List<string>();   // "Cut-Extrude ×12" etc, most common first
        public bool Verified;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// GetFeatureTree (tool #4) — READ-ONLY: a summary of the active part/assembly's feature tree — total feature count,
    /// a breakdown by feature TYPE, and how many are suppressed. "list the features", "what's the feature tree",
    /// "how many features", "what features does this part have", "feature breakdown". Never writes. Traverses the tree
    /// (FirstFeature / GetNextFeature); the harness cross-checks the total against an INDEPENDENT IModelDoc2.GetFeatureCount.
    /// </summary>
    public static class GetFeatureTree
    {
        public static bool IsFeatureTreeIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            return System.Text.RegularExpressions.Regex.IsMatch(cmd,
                @"\b(feature\s*tree|feature\s*breakdown|feature\s*list|list\s+(the\s+)?features|what\s+features|how\s*many\s+features|feature\s*count|features\s+does)\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        public static async Task<FeatureTreeResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new FeatureTreeResult();
            if (model == null) { res.Error = "Open a part or assembly to read its feature tree."; return res; }

            await emit("Scribe", "walking the feature tree", "run", null);
            int total = 0, suppressed = 0;
            try
            {
                var feat = model.FirstFeature() as Feature;
                while (feat != null)
                {
                    total++;
                    string tn = null; try { tn = feat.GetTypeName2(); } catch { }
                    if (string.IsNullOrEmpty(tn)) tn = "Unknown";
                    int n; res.ByType.TryGetValue(tn, out n); res.ByType[tn] = n + 1;
                    bool sup = false; try { sup = feat.IsSuppressed(); } catch { }
                    if (sup) suppressed++;
                    feat = feat.GetNextFeature() as Feature;
                }
            }
            catch (Exception ex) { res.Error = "Feature-tree read failed (" + ex.GetType().Name + ")."; return res; }

            res.TotalFeatures = total;
            res.Suppressed = suppressed;
            if (total == 0) { res.Error = "This model has no feature tree (an imported dumb solid, or an empty document)."; await emit("Scribe", null, "done", "no features"); return res; }

            res.TopTypes = res.ByType.OrderByDescending(kv => kv.Value).Take(6).Select(kv => kv.Key + " ×" + kv.Value).ToList();
            res.Verified = total > 0;
            res.Info = total + " features (" + res.ByType.Count + " distinct types" +
                       (suppressed > 0 ? ", " + suppressed + " suppressed" : "") + "). Most common: " + string.Join(", ", res.TopTypes) + ".";
            await emit("Scribe", null, "done", total + " features · " + res.ByType.Count + " types");
            return res;
        }
    }
}
