using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for select_plane (tool 15). Never re-reads live selection (harness's
    /// ForceRebuild3 between handler-run and GT-measure drops it — same non-negotiable as select_face/
    /// select_edge/select_component). Independence axis: the handler walks the feature tree LINKED-LIST
    /// (IModelDoc2.FirstFeature/IFeature.GetNextFeature); this GT instead calls
    /// IFeatureManager.GetFeatures(true), a flat ARRAY call — the same linked-list-vs-array duality
    /// select_face used for faces, one level up for features — and re-parses the query from scratch.
    /// </summary>
    public static partial class GroundTruth
    {
        private static readonly string[] SelPlaneStopWords =
        {
            "select","the","a","an","plane","reference","ref","please","this","that","it",
            "named","called","of","in","find","highlight","pick","choose","click","on","standard"
        };

        private static string SelPlaneParseQuery(string intent)
        {
            string raw = intent ?? "";
            var qm = Regex.Match(raw, "[\"']([^\"']{2,})[\"']");
            if (qm.Success) return qm.Groups[1].Value.Trim();
            var words = new List<string>();
            foreach (Match wm in Regex.Matches(raw, @"[A-Za-z0-9_]+"))
            {
                string lw = wm.Value.ToLowerInvariant();
                if (Array.IndexOf(SelPlaneStopWords, lw) >= 0) continue;
                words.Add(wm.Value);
            }
            return words.Count == 0 ? null : string.Join(" ", words);
        }

        public static JObject MeasureSelectPlane(IModelDoc2 model, string intent)
        {
            var mo = new JObject();
            string query = SelPlaneParseQuery(intent);
            mo["query"] = query;
            if (query == null) return mo;
            string normQuery = SelectComponent.Normalize(query);

            object[] feats = null;
            try { feats = model.FeatureManager.GetFeatures(true) as object[]; } catch { }

            string exactName = null;
            int matchCount = 0, planeCount = 0;
            var names = new JArray();
            foreach (var o in feats ?? new object[0])
            {
                var feat = o as Feature; if (feat == null) continue;
                string tn = null; try { tn = feat.GetTypeName2(); } catch { }
                if (tn != "RefPlane") continue;
                planeCount++;
                string nm = null; try { nm = feat.Name; } catch { }
                if (string.IsNullOrEmpty(nm)) continue;
                string norm = SelectComponent.Normalize(nm);
                bool isExact = norm == normQuery;
                bool isSub = isExact || norm.Contains(normQuery) || normQuery.Contains(norm);
                if (isExact && exactName == null) exactName = nm;
                if (isSub)
                {
                    matchCount++;
                    if (names.Count < 25) names.Add(nm);
                }
            }

            mo["planeCount"] = planeCount;
            mo["expectedExactName"] = exactName;
            mo["matchCount"] = matchCount;
            mo["matchNames"] = names;
            return mo;
        }
    }
}
