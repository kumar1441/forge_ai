using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class FindFloatingResult
    {
        public int Total;
        public int Floating;   // under-constrained and not fixed — free to drift
        public int Fixed;
        public int FullyConstrained;
        public List<string> FloatingNames = new List<string>();
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool — find_floating_components (READ). Lists components that are UNDER-constrained and not fixed — i.e. free to
    /// drift when the assembly is moved. A real assembly-health signal (unmated hardware, forgotten mates). Reads each
    /// top-level component's GetConstrainedStatus + IsFixed. Read-only; the ground truth recounts by its own read.
    /// </summary>
    public static class FindFloating
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            return System.Text.RegularExpressions.Regex.IsMatch(c, @"\b(floating|unmated|under.?constrained|loose|free|not mated|unfixed|un-constrained|drift)\b") &&
                   System.Text.RegularExpressions.Regex.IsMatch(c, @"\b(component|components|part|parts|which|find|list|how many)\b");
        }

        public static async Task<FindFloatingResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new FindFloatingResult();
            var asm = model as AssemblyDoc;
            if (asm == null) { res.Error = "Open the assembly (.SLDASM) to find floating components."; return res; }

            await emit("Auditor", "checking constraint status", "run", null);
            foreach (var o in (asm.GetComponents(true) as object[]) ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                res.Total++;
                bool fx = false; try { fx = c.IsFixed(); } catch { }
                int st = (int)swConstrainedStatus_e.swUnderConstrained; try { st = c.GetConstrainedStatus(); } catch { }
                if (fx) { res.Fixed++; continue; }
                if (st == (int)swConstrainedStatus_e.swFullyConstrained) res.FullyConstrained++;
                else if (st == (int)swConstrainedStatus_e.swUnderConstrained)
                {
                    res.Floating++;
                    try { res.FloatingNames.Add(c.Name2); } catch { }
                }
            }

            await emit("Auditor", null, "done", res.Floating + " floating · " + res.Fixed + " fixed · " + res.FullyConstrained + " fully-constrained of " + res.Total);
            if (res.Total == 0) { res.Error = "No components in this assembly."; return res; }

            var sb = new StringBuilder(res.Floating + " of " + res.Total + " components are floating (under-constrained, free to drift)" +
                (res.Fixed > 0 ? "; " + res.Fixed + " fixed" : "") + ".");
            int shown = 0;
            foreach (var n in res.FloatingNames) { if (shown++ >= 20) { sb.Append("\n… (" + (res.Floating - 20) + " more)"); break; } sb.Append("\n• " + n); }
            res.Info = sb.ToString();
            return res;
        }
    }
}
