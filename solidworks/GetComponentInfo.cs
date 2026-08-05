using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class CompRow
    {
        public string Name; public string Config; public bool Suppressed; public bool Lightweight;
        public bool Virtual; public bool Toolbox; public bool Fixed;
    }

    public class GetComponentInfoResult
    {
        public int Total;
        public int Toolbox, Virtual, Suppressed, Lightweight, Fixed;
        public List<CompRow> Rows = new List<CompRow>();
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 3 — get_component_info (READ). Per-component classification flags the rest of the pipeline relies on:
    /// is_toolbox / is_virtual / is_suppressed / is_lightweight / is_fixed, plus config + instance name. Distinct from
    /// scan (a whole-assembly health count) — this is the per-instance flag detail that decides how OTHER handlers
    /// treat a part (skip toolbox on mirror, resolve lightweight before geometry reads, etc.). Read-only.
    /// </summary>
    public static class GetComponentInfo
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            // Regression fix (globe-valve-wall-thickness false-positive): a bare "flag(s)" alone matched ANY sentence
            // using it as a plain verb ("flag anything below 1mm" from a wall-thickness scan), stealing the intent
            // from wall_thickness even after the cloud correctly picked it. "flag(s)" now requires a co-occurring
            // component/part word, same shape as the toolbox/virtual/lightweight conjunctive clause below.
            return System.Text.RegularExpressions.Regex.IsMatch(c, @"\b(component info|components info|classify|classification|which.*(toolbox|virtual|lightweight)|is[- ]?toolbox|component details|list the components)\b") ||
                   (System.Text.RegularExpressions.Regex.IsMatch(c, @"\b(toolbox|virtual|lightweight|purchased|hardware)\b") &&
                    System.Text.RegularExpressions.Regex.IsMatch(c, @"\b(which|what|how many|list|show|count)\b") &&
                    System.Text.RegularExpressions.Regex.IsMatch(c, @"\b(component|components|part|parts)\b")) ||
                   (System.Text.RegularExpressions.Regex.IsMatch(c, @"\bflag(s)?\b") &&
                    System.Text.RegularExpressions.Regex.IsMatch(c, @"\b(component|components|part|parts)\b"));
        }

        public static async Task<GetComponentInfoResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new GetComponentInfoResult();
            var asm = model as AssemblyDoc;
            if (asm == null) { res.Error = "Open the assembly (.SLDASM) to inspect its components."; return res; }

            await emit("Inspector", "reading component flags", "run", null);
            foreach (var o in (asm.GetComponents(true) as object[]) ?? new object[0])   // top-level instances
            {
                var c = o as Component2; if (c == null) continue;
                var r = new CompRow();
                try { r.Name = c.Name2; } catch { }
                try { r.Config = c.ReferencedConfiguration; } catch { }
                try { r.Suppressed = c.IsSuppressed(); } catch { }
                try { r.Virtual = c.IsVirtual; } catch { }
                try { r.Fixed = c.IsFixed(); } catch { }
                int ss = -1; try { ss = c.GetSuppression2(); } catch { }
                r.Lightweight = ss == (int)swComponentSuppressionState_e.swComponentLightweight ||
                                ss == (int)swComponentSuppressionState_e.swComponentFullyLightweight;
                string path = null; try { path = c.GetPathName(); } catch { }
                // A real Toolbox part lives under a directory literally named "Toolbox" (the SW Toolbox library), so
                // require the SEGMENT "\toolbox\" — NOT a bare substring, which false-matched every part when the
                // assembly's parent folder happened to be "Mates Error & toolbox config". (Baked Toolbox COPIES aren't
                // under that path at all — detecting those is audit_toolbox's job, via config/design-table signals.)
                r.Toolbox = !string.IsNullOrEmpty(path) &&
                            System.Text.RegularExpressions.Regex.IsMatch(path, @"[\\/]toolbox[\\/]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                res.Total++;
                if (r.Toolbox) res.Toolbox++;
                if (r.Virtual) res.Virtual++;
                if (r.Suppressed) res.Suppressed++;
                if (r.Lightweight) res.Lightweight++;
                if (r.Fixed) res.Fixed++;
                res.Rows.Add(r);
            }

            await emit("Inspector", null, "done",
                res.Total + " components · " + res.Toolbox + " toolbox · " + res.Virtual + " virtual · " +
                res.Suppressed + " suppressed · " + res.Fixed + " fixed");

            if (res.Total == 0) { res.Error = "No components in this assembly."; return res; }

            var sb = new StringBuilder(res.Total + " components: " + res.Toolbox + " toolbox, " + res.Virtual +
                " virtual, " + res.Suppressed + " suppressed, " + res.Lightweight + " lightweight, " + res.Fixed + " fixed.");
            int shown = 0;
            foreach (var r in res.Rows)
            {
                if (shown++ >= 20) { sb.Append("\n… (" + (res.Total - 20) + " more)"); break; }
                var flags = new List<string>();
                if (r.Toolbox) flags.Add("toolbox");
                if (r.Virtual) flags.Add("virtual");
                if (r.Suppressed) flags.Add("suppressed");
                if (r.Lightweight) flags.Add("lightweight");
                if (r.Fixed) flags.Add("fixed");
                sb.Append("\n• " + r.Name + " [" + (r.Config ?? "?") + "]" + (flags.Count > 0 ? " — " + string.Join(", ", flags.ToArray()) : " — floating"));
            }
            res.Info = sb.ToString();
            return res;
        }
    }
}
