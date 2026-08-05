using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class PatternRow { public string Name; public string Kind; public int Instances; }

    public class GetPatternInfoResult
    {
        public int PatternCount;
        public int TotalInstances;   // summed across all patterns
        public List<PatternRow> Patterns = new List<PatternRow>();
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 50 — get_pattern_info (READ). Reads a part's feature patterns and their instance counts (linear / circular).
    /// "how many patterns", "what's the pattern count", "read the pattern". Uses the pattern feature's definition
    /// (ILinearPatternFeatureData.D1TotalInstances / ICircularPatternFeatureData.TotalInstances). Read-only.
    /// </summary>
    public static class GetPatternInfo
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            return System.Text.RegularExpressions.Regex.IsMatch(c, @"\bpattern(s)?\b") &&
                   System.Text.RegularExpressions.Regex.IsMatch(c, @"\b(info|how many|count|instances|read|details|what|list)\b") &&
                   !System.Text.RegularExpressions.Regex.IsMatch(c, @"\b(create|add|make|dissolve|edit|change|delete)\b");
        }

        public static async Task<GetPatternInfoResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new GetPatternInfoResult();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { res.Error = "Open a part to read its patterns."; return res; }

            await emit("Reader", "reading patterns", "run", null);
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                string tn = null; try { tn = f.GetTypeName2(); } catch { }
                if (tn != null && tn.IndexOf("LPattern", StringComparison.OrdinalIgnoreCase) >= 0)
                    AddRow(f, "linear", res);
                else if (tn != null && tn.IndexOf("CirPattern", StringComparison.OrdinalIgnoreCase) >= 0)
                    AddRow(f, "circular", res);
                f = f.GetNextFeature() as Feature;
            }

            await emit("Reader", null, "done", res.PatternCount + " patterns · " + res.TotalInstances + " total instances");
            if (res.PatternCount == 0) { res.Info = "No feature patterns on this part."; return res; }

            var sb = new StringBuilder(res.PatternCount + " pattern" + (res.PatternCount == 1 ? "" : "s") + ":");
            foreach (var p in res.Patterns) sb.Append("\n• " + p.Name + " (" + p.Kind + ") — " + p.Instances + " instances");
            res.Info = sb.ToString();
            return res;
        }

        private static void AddRow(Feature f, string kind, GetPatternInfoResult res)
        {
            var row = new PatternRow { Kind = kind };
            try { row.Name = f.Name; } catch { }
            int inst = 0;
            try
            {
                object def = f.GetDefinition();
                var lin = def as ILinearPatternFeatureData;
                if (lin != null) { try { inst = lin.D1TotalInstances; } catch { } }
                var cir = def as ICircularPatternFeatureData;
                if (cir != null) { try { inst = cir.TotalInstances; } catch { } }
            }
            catch { }
            row.Instances = inst;
            res.Patterns.Add(row);
            res.PatternCount++;
            res.TotalInstances += inst;
        }
    }
}
