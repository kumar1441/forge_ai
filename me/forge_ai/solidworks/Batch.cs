using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class BatchResult
    {
        public int UniqueParts;
        public int Processed;
        public int TotalSuppressed;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Batch — apply a per-part workflow to EVERY unique part in an assembly in one command
    /// ("simplify all the parts for printing"). Today it batches the print-prep simplify across each
    /// unique part doc; the shape generalizes to any per-part operation. Idempotent (each part's
    /// Forge-Simplified config is created once). Verified INDEPENDENTLY by GroundTruth (per-part config scan).
    /// </summary>
    public static class Batcher
    {
        public static bool IsBatchIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            cmd = cmd.ToLowerInvariant();
            bool scope = Regex.IsMatch(cmd, @"\b(all|every|each|batch)\b") && Regex.IsMatch(cmd, @"\b(part|parts|component|components)\b");
            bool op = Regex.IsMatch(cmd, @"\b(simplify|print[- ]?prep|defeature|fea[- ]?prep|prep)\b");
            return scope && op;
        }

        public static async Task<BatchResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new BatchResult();
            var asm = model as AssemblyDoc;
            if (asm == null) { res.Error = "Open an assembly to batch its parts."; return res; }

            await emit("Ripple", "finding every unique part", "run", null);
            object[] comps = asm.GetComponents(false) as object[];
            // dedupe by part file path so we touch each part once, not once per instance
            var seen = new Dictionary<string, IModelDoc2>(StringComparer.OrdinalIgnoreCase);
            foreach (var o in comps ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { }
                if (sup) continue;
                string p = null; try { p = c.GetPathName(); } catch { }
                if (string.IsNullOrEmpty(p) || seen.ContainsKey(p)) continue;
                IModelDoc2 pd = null; try { pd = c.GetModelDoc2() as IModelDoc2; } catch { }
                if (pd != null && (int)pd.GetType() == (int)swDocumentTypes_e.swDocPART) seen[p] = pd;
            }
            res.UniqueParts = seen.Count;
            await emit("Ripple", null, "done", res.UniqueParts + " unique parts to process");

            await emit("Ripple", "simplifying each part for printing", "run", null);
            foreach (var kv in seen)
            {
                var r = Simplifier.SimplifyPartDoc(kv.Value, intent);
                if (r != null && r.Error == null) { res.Processed++; res.TotalSuppressed += r.Suppressed; }
            }
            model.ForceRebuild3(false);
            await emit("Ripple", null, "done", res.Processed + " parts simplified · " + res.TotalSuppressed + " features suppressed total");

            await emit("Sentinel", "checking the assembly still solves", "run", null);
            int wrong = 0; try { wrong = model.Extension.GetWhatsWrongCount(); } catch { }
            await emit("Sentinel", null, "done", wrong == 0 ? "rebuild clean" : wrong + " rebuild flags");

            res.Info = "Batch-simplified " + res.Processed + "/" + res.UniqueParts + " parts (" + res.TotalSuppressed + " features suppressed). Each part's original config is untouched.";
            return res;
        }
    }
}
