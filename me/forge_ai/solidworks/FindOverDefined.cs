using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class FindOverDefinedResult
    {
        public int Total;
        public int OverDefined;   // over-constrained / no-solution / invalid — the red-flag components
        public List<string> Names = new List<string>();
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool — find_over_defined_components (READ). Lists components SolidWorks reports as over-constrained /
    /// no-solution / invalid — the "red wave" flags, but as a read-only diagnosis (fix_red_wave is the fixer). The
    /// counterpart to find_floating. Reads each component's GetConstrainedStatus. Read-only; independent GT recount.
    /// </summary>
    public static class FindOverDefined
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            return System.Text.RegularExpressions.Regex.IsMatch(c, @"\b(over.?constrained|over.?defined|conflicting|no.?solution|red flag|red flags|invalid)\b") &&
                   System.Text.RegularExpressions.Regex.IsMatch(c, @"\b(which|find|list|how many|component|components|part|parts)\b") &&
                   !System.Text.RegularExpressions.Regex.IsMatch(c, @"\b(fix|repair|clear|resolve|remove|clean)\b");   // that's fix_red_wave
        }

        public static async Task<FindOverDefinedResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new FindOverDefinedResult();
            var asm = model as AssemblyDoc;
            if (asm == null) { res.Error = "Open the assembly (.SLDASM) to find over-defined components."; return res; }
            try { model.ForceRebuild3(false); } catch { }

            await emit("Auditor", "checking constraint status", "run", null);
            foreach (var o in (asm.GetComponents(true) as object[]) ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                res.Total++;
                int st = (int)swConstrainedStatus_e.swUnderConstrained; try { st = c.GetConstrainedStatus(); } catch { }
                if (st == (int)swConstrainedStatus_e.swOverConstrained || st == (int)swConstrainedStatus_e.swNoSolution || st == (int)swConstrainedStatus_e.swInvalidSolution)
                {
                    res.OverDefined++;
                    try { res.Names.Add(c.Name2); } catch { }
                }
            }

            await emit("Auditor", null, "done", res.OverDefined + " over-defined of " + res.Total + " components");
            if (res.Total == 0) { res.Error = "No components in this assembly."; return res; }

            if (res.OverDefined == 0) { res.Info = "No over-defined components — the assembly's mates all solve cleanly."; return res; }
            var sb = new StringBuilder(res.OverDefined + " of " + res.Total + " components are over-defined (conflicting mates):");
            int shown = 0;
            foreach (var n in res.Names) { if (shown++ >= 20) { sb.Append("\n… (" + (res.OverDefined - 20) + " more)"); break; } sb.Append("\n• " + n); }
            sb.Append("\nRun \"fix the mate errors\" to resolve them.");
            res.Info = sb.ToString();
            return res;
        }
    }
}
