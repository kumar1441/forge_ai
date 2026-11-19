using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for PatternDrivenPatternComponent. Shares no code with the handler: re-derives the
    /// seed component from the SAME natural-language name (via SelectComponent.Normalize) but with its OWN
    /// recursive GetRootComponent3/GetChildren tree walk (not the handler's flat GetComponents(false) array —
    /// same "different traversal, same live state" shape as GroundTruth.MeasureLinearPatternComponent), and
    /// separately re-reads the host feature's own instance count straight off ILinearPatternFeatureData /
    /// ICircularPatternFeatureData so the harness can confirm the handler actually followed that number, not one
    /// it invented.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasurePatternDrivenPatternComponent(ISldWorks app, IModelDoc2 model, string intent)
        {
            var d = new JObject();
            var asm = model as AssemblyDoc;
            if (asm == null) { d["error"] = "active doc is not an assembly"; return d; }

            string query = PdpcParseComponentName(intent);
            string normQuery = query == null ? null : SelectComponent.Normalize(query);

            int totalComponents = 0;
            var pathCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            string seedPath = null;
            Component2 root = null; try { root = model.ConfigurationManager.ActiveConfiguration.GetRootComponent3(true) as Component2; } catch { }
            int hostPatternInstances = -1;
            PdpcWalk(root, normQuery, pathCounts, ref totalComponents, ref seedPath, ref hostPatternInstances);

            int seedFileInstanceCount = 0;
            if (seedPath != null) pathCounts.TryGetValue(seedPath, out seedFileInstanceCount);

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
            d["hostPatternInstances"] = hostPatternInstances;
            d["overDefinedComponents"] = overDefined;
            d["rebuildErrors"] = rebuildErrors;
            d["fingerprint"] = new JObject { ["totalComponents"] = totalComponents, ["overDefinedComponents"] = overDefined };
            return d;
        }

        private static void PdpcWalk(Component2 comp, string normQuery, Dictionary<string, int> pathCounts, ref int total, ref string seedPath, ref int hostPatternInstances)
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
                    if (hostPatternInstances < 0)
                    {
                        IModelDoc2 pdoc = null; try { pdoc = c.GetModelDoc2() as IModelDoc2; } catch { }
                        if (pdoc != null)
                        {
                            var f = pdoc.FirstFeature() as Feature;
                            while (f != null)
                            {
                                string tn = null; try { tn = f.GetTypeName2(); } catch { }
                                if (tn != null && tn.IndexOf("LPattern", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    var lin = f.GetDefinition() as ILinearPatternFeatureData;
                                    if (lin != null) { try { hostPatternInstances = lin.D1TotalInstances; } catch { } }
                                    break;
                                }
                                if (tn != null && tn.IndexOf("CirPattern", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    var cir = f.GetDefinition() as ICircularPatternFeatureData;
                                    if (cir != null) { try { hostPatternInstances = cir.TotalInstances; } catch { } }
                                    break;
                                }
                                f = f.GetNextFeature() as Feature;
                            }
                        }
                    }
                }
                PdpcWalk(c, normQuery, pathCounts, ref total, ref seedPath, ref hostPatternInstances);
            }
        }

        private static string PdpcParseComponentName(string intent)
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
