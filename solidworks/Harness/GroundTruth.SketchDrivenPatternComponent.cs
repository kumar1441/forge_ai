using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for SketchDrivenPatternComponent. Shares no code with the handler: re-derives the
    /// seed component from the SAME natural-language name (via SelectComponent.Normalize) but with its OWN
    /// recursive GetRootComponent3/GetChildren tree walk (not the handler's flat GetComponents(false) array —
    /// same "different traversal, same live state" shape as the 41/42/44 ground truths), and separately re-reads
    /// the driving sketch's own point count straight off Sketch.GetSketchPointsCount2() so the harness can confirm
    /// the handler actually followed that number, not one it invented.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureSketchDrivenPatternComponent(ISldWorks app, IModelDoc2 model, string intent)
        {
            var d = new JObject();
            var asm = model as AssemblyDoc;
            if (asm == null) { d["error"] = "active doc is not an assembly"; return d; }

            string query = SdpcParseComponentName(intent);
            string normQuery = query == null ? null : SelectComponent.Normalize(query);

            int totalComponents = 0;
            var pathCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            string seedPath = null;
            Component2 root = null; try { root = model.ConfigurationManager.ActiveConfiguration.GetRootComponent3(true) as Component2; } catch { }
            SdpcWalk(root, normQuery, pathCounts, ref totalComponents, ref seedPath);

            int seedFileInstanceCount = 0;
            if (seedPath != null) pathCounts.TryGetValue(seedPath, out seedFileInstanceCount);

            int sketchPointCount = 0;
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                Sketch sk = null; try { sk = f.GetSpecificFeature2() as Sketch; } catch { }
                if (sk != null)
                {
                    int pc = 0; try { pc = sk.GetSketchPointsCount2(); } catch { }
                    if (pc >= 2) { sketchPointCount = pc; break; }
                }
                f = f.GetNextFeature() as Feature;
            }

            object[] top = asm.GetComponents(true) as object[];
            int overDefined = 0;
            foreach (var o in top ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                int st = (int)swConstrainedStatus_e.swUnderConstrained; try { st = c.GetConstrainedStatus(); } catch { }
                if (st == (int)swConstrainedStatus_e.swOverConstrained || st == (int)swConstrainedStatus_e.swNoSolution || st == (int)swConstrainedStatus_e.swInvalidSolution) overDefined++;
            }
            int rebuildErrors = 0; try { rebuildErrors = model.Extension.GetWhatsWrongCount(); } catch { }

            d["totalComponents"] = totalComponents;
            d["seedFileInstanceCount"] = seedFileInstanceCount;
            d["sketchPointCount"] = sketchPointCount;
            d["overDefinedComponents"] = overDefined;
            d["rebuildErrors"] = rebuildErrors;
            d["fingerprint"] = new JObject { ["totalComponents"] = totalComponents, ["overDefinedComponents"] = overDefined };
            return d;
        }

        private static void SdpcWalk(Component2 comp, string normQuery, Dictionary<string, int> pathCounts, ref int total, ref string seedPath)
        {
            if (comp == null) return;
            object[] children = comp.GetChildren() as object[];
            foreach (var o in children ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { }
                if (!sup)
                {
                    total++;
                    string path = null; try { path = c.GetPathName(); } catch { }
                    if (!string.IsNullOrEmpty(path)) { int n; pathCounts.TryGetValue(path, out n); pathCounts[path] = n + 1; }
                    if (seedPath == null && normQuery != null && !string.IsNullOrEmpty(path))
                    {
                        string nm = null; try { nm = c.Name2; } catch { }
                        string norm = SelectComponent.Normalize(nm ?? "");
                        if (norm == normQuery || norm.Contains(normQuery) || normQuery.Contains(norm)) seedPath = path;
                    }
                }
                SdpcWalk(c, normQuery, pathCounts, ref total, ref seedPath);
            }
        }

        private static string SdpcParseComponentName(string intent)
        {
            string raw = intent ?? "";
            var qm = Regex.Match(raw, "[\"']([^\"']{2,})[\"']");
            if (qm.Success) return qm.Groups[1].Value.Trim();
            var m = Regex.Match(raw, @"pattern\s+(?:the\s+)?(.+?)\s+component\b", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value.Trim();
            return null;
        }
    }
}
