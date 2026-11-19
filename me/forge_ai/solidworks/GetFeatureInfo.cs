using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class FeatInfoRow { public string Name; public string Type; public bool Suppressed; public string Param; }

    public class GetFeatureInfoResult
    {
        public int Count;
        public double MaxExtrudeDepthMm = -1;   // the deepest boss-extrude depth found (known-truth hook)
        public List<FeatInfoRow> Rows = new List<FeatInfoRow>();
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 5 — get_feature_info (READ). Per-feature PARAMETERS (not just the type/count that get_feature_tree gives):
    /// extrude/cut depth, fillet radius, hole diameter, pattern count — the numbers that say what a feature actually
    /// does. Reads each feature's definition object (IExtrudeFeatureData2.GetDepth, ISimpleFilletFeatureData2.Radius,
    /// IWizardHoleFeatureData2, ILinearPatternFeatureData). Read-only; own read via GetDefinition.
    /// </summary>
    public static class GetFeatureInfo
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            // "change the depth of Boss-Extrude1 to 35mm" is edit_feature_parameter (a WRITE), not a read — a feature
            // QUERY never carries a change/set verb AND a standalone target number, so excluding that keeps the boundary.
            if (System.Text.RegularExpressions.Regex.IsMatch(c, @"\b(change|set|edit|adjust|modify|make|update)\b") &&
                System.Text.RegularExpressions.Regex.IsMatch(c, @"(?<![a-z0-9])\d+(\.\d+)?"))
                return false;
            return System.Text.RegularExpressions.Regex.IsMatch(c, @"\b(feature info|feature details|feature parameters|feature params|details of|parameters of|how deep|depth of|radius of|feature settings)\b") ||
                   (System.Text.RegularExpressions.Regex.IsMatch(c, @"\b(depth|radius|diameter|parameter|parameters)\b") &&
                    System.Text.RegularExpressions.Regex.IsMatch(c, @"\b(feature|extrude|boss|cut|fillet|hole|pattern)\b"));
        }

        public static async Task<GetFeatureInfoResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new GetFeatureInfoResult();
            if (model == null) { res.Error = "Open a part to read feature parameters."; return res; }

            await emit("Reader", "reading feature parameters", "run", null);
            var feat = model.FirstFeature() as Feature;
            while (feat != null)
            {
                string tn = null; try { tn = feat.GetTypeName2(); } catch { }
                if (!string.IsNullOrEmpty(tn) && IsRealFeature(tn))
                {
                    var r = new FeatInfoRow();
                    try { r.Name = feat.Name; } catch { }
                    r.Type = tn;
                    try { r.Suppressed = feat.IsSuppressed(); } catch { }
                    r.Param = ReadParam(feat, tn, res);
                    res.Rows.Add(r);
                    res.Count++;
                }
                feat = feat.GetNextFeature() as Feature;
            }

            await emit("Reader", null, "done", res.Count + " feature" + (res.Count == 1 ? "" : "s") + " with parameters");
            if (res.Count == 0) { res.Error = "No parametric features found (an imported dumb solid, or empty document)."; return res; }

            var sb = new StringBuilder(res.Count + " feature" + (res.Count == 1 ? "" : "s") + ":");
            int shown = 0;
            foreach (var r in res.Rows)
            {
                if (shown++ >= 24) { sb.Append("\n… (" + (res.Count - 24) + " more)"); break; }
                sb.Append("\n• " + r.Name + " (" + r.Type + ")" + (string.IsNullOrEmpty(r.Param) ? "" : " — " + r.Param) + (r.Suppressed ? " [suppressed]" : ""));
            }
            res.Info = sb.ToString();
            return res;
        }

        // real modelling features (skip origin planes/axes/the coordinate system)
        private static bool IsRealFeature(string tn)
        {
            switch (tn)
            {
                case "RefPlane": case "RefAxis": case "OriginProfileFeature": case "CoordSys":
                case "DetailCabinet": case "HistoryFolder": case "SketchBlockDef": return false;
                default: return true;
            }
        }

        // read the headline numeric parameter for the feature types that carry one; record the max extrude depth
        private static string ReadParam(Feature feat, string tn, GetFeatureInfoResult res)
        {
            try
            {
                object def = feat.GetDefinition();
                if (def == null) return null;

                var ext = def as IExtrudeFeatureData2;
                if (ext != null)
                {
                    double d = 0; try { d = ext.GetDepth(true); } catch { }
                    double mm = d * 1000.0;
                    if (tn.IndexOf("Boss", StringComparison.OrdinalIgnoreCase) >= 0 || tn == "Extrusion")
                        if (mm > res.MaxExtrudeDepthMm) res.MaxExtrudeDepthMm = mm;
                    return "depth " + mm.ToString("0.###", CultureInfo.InvariantCulture) + " mm";
                }
                var fil = def as ISimpleFilletFeatureData2;
                if (fil != null) { double rr = 0; try { rr = fil.DefaultRadius; } catch { } return "radius " + (rr * 1000.0).ToString("0.###", CultureInfo.InvariantCulture) + " mm"; }

                var lin = def as ILinearPatternFeatureData;
                if (lin != null) { int c = 0; try { c = lin.D1TotalInstances; } catch { } return c + " instances"; }
            }
            catch { }
            return null;
        }
    }
}
