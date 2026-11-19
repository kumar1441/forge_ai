using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class FindFeaturesByTypeResult
    {
        public string TypeKey;                  // hole / fillet / chamfer / boss / shell / revolve / draft
        public int TotalFeatures;               // real modelling features scanned
        public int MatchedByType;               // features of the requested type (before the size filter)
        public int Matched;                     // features surviving the size filter too
        public double SizeFilterMm = -1;        // -1 = no size filter given
        public string SizeOp;                   // "under" / "over" / null
        public double SmallestMatchMm = -1;     // measured size of the smallest match (hole dia / fillet radius)
        public string SizeSource;               // "sketch" (independent of geometry) / "faces" (fallback) / null
        public int Unmeasured;                  // matched-by-type but size unreadable — excluded, reported, never silently dropped
        public List<string> Names = new List<string>();
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 169 — find_features_by_type (READ). The bulk-selection engine behind "suppress every hole under 3mm" and
    /// "kill the cosmetic fillets": resolves a feature TYPE (plus an optional size criterion) to the concrete feature
    /// list, so the write tools that follow act on a named set rather than a guess. Distinct from get_feature_tree
    /// (counts everything) and get_feature_info (parameters of every feature) — this one FILTERS.
    ///
    /// Sizes come from the feature's own sketch (circle radius for a bored hole) or the fillet definition — deliberately
    /// NOT from body face geometry, which is how ground truth measures, so the two paths stay independent. When a size
    /// can't be read the feature is reported as unmeasured, never counted as passing the filter (fail closed).
    /// </summary>
    public static class FindFeaturesByType
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            // capture_viewport/capture_section own screenshot/image vocabulary — "show me a section cut screenshot"
            // must not be swallowed here just because "cut" resolves to the hole type-key below.
            if (Regex.IsMatch(c, @"\b(screenshot|snapshot|picture|photo|capture|section)\b")) return false;
            // must be a SEARCH, not a write and not a bare "list the features" (that's get_feature_tree)
            if (!Regex.IsMatch(c, @"\b(find|list|show|which|how many|search|locate|inventory)\b")) return false;
            if (Regex.IsMatch(c, @"\b(add|create|make|drill|insert|remove|delete|suppress|unsuppress|rename|edit|change|set|scale|resize|upsize|tap|thread)\b")) return false;
            // nouns owned by narrower tools — never steal them
            if (Regex.IsMatch(c, @"\bpattern(s)?\b|\bsketch(es)?\b|\bmate(s)?\b|\bcomponent(s)?\b|\bconfig")) return false;
            return TypeKeyOf(c) != null;
        }

        private static string TypeKeyOf(string c)
        {
            if (Regex.IsMatch(c, @"\b(hole|holes|bore|bores|cut|cuts)\b")) return "hole";
            if (Regex.IsMatch(c, @"\b(fillet|fillets|round|rounds)\b")) return "fillet";
            if (Regex.IsMatch(c, @"\b(chamfer|chamfers|bevel|bevels)\b")) return "chamfer";
            if (Regex.IsMatch(c, @"\b(extrude|extrudes|extrusion|boss|bosses|pad|pads)\b")) return "boss";
            if (Regex.IsMatch(c, @"\bshells?\b")) return "shell";
            if (Regex.IsMatch(c, @"\b(revolve|revolves|revolution)\b")) return "revolve";
            if (Regex.IsMatch(c, @"\bdrafts?\b")) return "draft";
            return null;
        }

        public static async Task<FindFeaturesByTypeResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new FindFeaturesByTypeResult();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { res.Error = "Open a part to search its feature tree by type."; return res; }

            string c = (intent ?? "").ToLowerInvariant();
            res.TypeKey = TypeKeyOf(c);
            if (res.TypeKey == null) { res.Error = "Say which kind of feature to find — holes, fillets, chamfers, bosses, shells."; return res; }
            ParseSize(c, res);

            string what = res.TypeKey + (res.SizeFilterMm > 0 ? " " + res.SizeOp + " " + res.SizeFilterMm.ToString("0.###", CultureInfo.InvariantCulture) + "mm" : "");
            await emit("Scout", "searching the tree for " + what, "run", null);

            Feature lastSketch = null;                  // this build lists a consumed sketch as a flat SIBLING of the
            var f = model.FirstFeature() as Feature;    // feature that ate it, so the preceding sketch is a real route
            while (f != null)
            {
                string tn = null; try { tn = f.GetTypeName2(); } catch { }
                if (!string.IsNullOrEmpty(tn) && IsRealFeature(tn))
                {
                    res.TotalFeatures++;
                    if (tn == "ProfileFeature") lastSketch = f;
                    if (FamilyOf(tn) == res.TypeKey)
                    {
                        res.MatchedByType++;
                        string src; double size = MeasureFeature(f, res.TypeKey, lastSketch, out src);
                        if (res.SizeFilterMm <= 0)
                        {
                            res.Matched++;
                            AddName(res, f, size, src);
                        }
                        else if (size < 0) res.Unmeasured++;                       // fail closed — unmeasurable never passes
                        else if (res.SizeOp == "over" ? size > res.SizeFilterMm : size < res.SizeFilterMm)
                        {
                            res.Matched++;
                            AddName(res, f, size, src);
                        }
                    }
                }
                f = f.GetNextFeature() as Feature;
            }

            await emit("Scout", null, "done", res.Matched + " of " + res.TotalFeatures + " features match " + what);

            var sb = new StringBuilder();
            if (res.Matched == 0)
                sb.Append("No " + res.TypeKey + " features" + (res.SizeFilterMm > 0 ? " " + res.SizeOp + " " + res.SizeFilterMm.ToString("0.###", CultureInfo.InvariantCulture) + "mm" : "") +
                          " — the tree has " + res.MatchedByType + " " + res.TypeKey + " feature" + (res.MatchedByType == 1 ? "" : "s") + " of " + res.TotalFeatures + " total.");
            else
            {
                sb.Append(res.Matched + " " + res.TypeKey + " feature" + (res.Matched == 1 ? "" : "s"));
                if (res.SizeFilterMm > 0) sb.Append(" " + res.SizeOp + " " + res.SizeFilterMm.ToString("0.###", CultureInfo.InvariantCulture) + "mm");
                sb.Append(" of " + res.TotalFeatures + " features:");
                int shown = 0;
                foreach (var n in res.Names)
                {
                    if (shown++ >= 24) { sb.Append("\n… (" + (res.Names.Count - 24) + " more)"); break; }
                    sb.Append("\n• " + n);
                }
            }
            if (res.Unmeasured > 0) sb.Append("\n" + res.Unmeasured + " " + res.TypeKey + " feature" + (res.Unmeasured == 1 ? "" : "s") + " had no readable size — excluded, needs your eyes.");
            res.Info = sb.ToString();
            return res;
        }

        private static void AddName(FindFeaturesByTypeResult res, Feature f, double size, string src)
        {
            string name = null; try { name = f.Name; } catch { }
            res.Names.Add((name ?? "?") + (size > 0 ? " — " + size.ToString("0.###", CultureInfo.InvariantCulture) + "mm" : ""));
            if (size > 0 && (res.SmallestMatchMm < 0 || size < res.SmallestMatchMm)) { res.SmallestMatchMm = size; res.SizeSource = src; }
        }

        // "under 3mm" / "smaller than 3" / "below 3mm"  |  "over 10mm" / "bigger than 10"
        private static void ParseSize(string c, FindFeaturesByTypeResult res)
        {
            var m = Regex.Match(c, @"\b(under|below|less than|smaller than|thinner than|<)\s*([0-9]*\.?[0-9]+)\s*(mm)?");
            if (m.Success) { res.SizeOp = "under"; res.SizeFilterMm = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture); return; }
            m = Regex.Match(c, @"\b(over|above|greater than|bigger than|larger than|>)\s*([0-9]*\.?[0-9]+)\s*(mm)?");
            if (m.Success) { res.SizeOp = "over"; res.SizeFilterMm = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture); }
        }

        // Real modelling features only. Beyond the origin scaffold, this 3DEXPERIENCE tree carries ELEVEN empty
        // container folders (Comments/Sensors/Solid Bodies/Material/Equations/…) — counting them would report a
        // 5-feature block as 16 features. Every *Folder type is scaffold.
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

        // SolidWorks type name → the plain-English family a user asks for.
        // LANDMINE: a cut-extrude reports GetTypeName2() == "ICE" on this R2026x build, NOT "Cut" — measured on the
        // pattern-block fixture (Seed-Hole|ICE). Matching only "Cut" finds zero holes on a part that plainly has one.
        private static string FamilyOf(string tn)
        {
            if (tn.IndexOf("HoleWzd", StringComparison.OrdinalIgnoreCase) >= 0) return "hole";
            if (tn.IndexOf("Fillet", StringComparison.OrdinalIgnoreCase) >= 0) return "fillet";
            if (tn.IndexOf("Chamfer", StringComparison.OrdinalIgnoreCase) >= 0) return "chamfer";
            if (tn.IndexOf("Shell", StringComparison.OrdinalIgnoreCase) >= 0) return "shell";
            if (tn.IndexOf("Draft", StringComparison.OrdinalIgnoreCase) >= 0) return "draft";
            if (tn.IndexOf("RevCut", StringComparison.OrdinalIgnoreCase) >= 0) return "hole";
            if (tn.IndexOf("Revolution", StringComparison.OrdinalIgnoreCase) >= 0) return "revolve";
            if (tn.Equals("ICE", StringComparison.OrdinalIgnoreCase) ||
                tn.IndexOf("Cut", StringComparison.OrdinalIgnoreCase) >= 0) return "hole";   // cut-extrude = bored hole/pocket
            if (tn.IndexOf("Extrusion", StringComparison.OrdinalIgnoreCase) >= 0 ||
                tn.IndexOf("Boss", StringComparison.OrdinalIgnoreCase) >= 0) return "boss";
            return null;
        }

        // hole → bore diameter (mm); fillet → radius (mm). -1 when unreadable.
        private static double MeasureFeature(Feature f, string typeKey, Feature lastSketch, out string source)
        {
            source = null;
            if (typeKey == "fillet")
            {
                try
                {
                    var fd = f.GetDefinition() as ISimpleFilletFeatureData2;
                    if (fd != null) { source = "definition"; return fd.DefaultRadius * 1000.0; }
                }
                catch { }
                return -1;
            }
            if (typeKey != "hole") return -1;

            // three routes to the feature's own profile, tried in order of directness; whichever answers is recorded
            double d = SmallestCircleMm(SubSketch(f));
            if (d > 0) { source = "sketch-sub"; return d; }
            d = SmallestCircleMm(ParentSketch(f));
            if (d > 0) { source = "sketch-parent"; return d; }
            d = SmallestCircleMm(SketchOf(lastSketch));
            if (d > 0) { source = "sketch-prev"; return d; }
            d = FaceCylinderDiaMm(f);                       // last resort: the resulting geometry
            if (d > 0) { source = "faces"; return d; }
            return -1;
        }

        private static Sketch SketchOf(Feature f)
        {
            if (f == null) return null;
            try { return f.GetSpecificFeature2() as Sketch; } catch { return null; }
        }

        private static Sketch SubSketch(Feature f)
        {
            var sub = f.GetFirstSubFeature() as Feature;
            while (sub != null)
            {
                var sk = SketchOf(sub); if (sk != null) return sk;
                sub = sub.GetNextSubFeature() as Feature;
            }
            return null;
        }

        private static Sketch ParentSketch(Feature f)
        {
            object[] parents = null; try { parents = f.GetParents() as object[]; } catch { }
            foreach (var po in parents ?? new object[0])
            {
                var sk = SketchOf(po as Feature); if (sk != null) return sk;
            }
            return null;
        }

        // smallest circle in a sketch — the parametric intent, not the resulting geometry
        private static double SmallestCircleMm(Sketch sk)
        {
            if (sk == null) return -1;
            double best = -1;
            object[] segs = null; try { segs = sk.GetSketchSegments() as object[]; } catch { }
            foreach (var so in segs ?? new object[0])
            {
                var arc = so as SketchArc; if (arc == null) continue;
                double r = 0; try { r = arc.GetRadius(); } catch { }
                double d = r * 2000.0;
                if (d > 0 && (best < 0 || d < best)) best = d;
            }
            return best;
        }

        // fallback only: smallest cylindrical face the feature created
        private static double FaceCylinderDiaMm(Feature f)
        {
            double best = -1;
            object[] faces = null; try { faces = f.GetFaces() as object[]; } catch { }
            foreach (var fo in faces ?? new object[0])
            {
                var face = fo as Face2; if (face == null) continue;
                var surf = face.GetSurface() as Surface; if (surf == null) continue;
                bool cyl = false; try { cyl = surf.IsCylinder(); } catch { }
                if (!cyl) continue;
                double[] p = null; try { p = surf.CylinderParams as double[]; } catch { }
                if (p == null || p.Length < 7) continue;
                double d = p[6] * 2000.0;
                if (d > 0 && (best < 0 || d < best)) best = d;
            }
            return best;
        }
    }
}
