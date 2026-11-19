using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public class SelectByFilterResult
    {
        public string Filter;
        public int Matched;     // components matching the filter
        public int Active;      // of those, not suppressed
        public List<string> Names = new List<string>();
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 12 — select_components_by_filter (READ/resolve). Resolves a filter — a KIND (bolt/nut/washer/screw/flange/
    /// shaft/gear) or a name wildcard — to the matching set of components, reporting the count + names (and how many
    /// are active vs suppressed). This is the resolution primitive other handlers lean on ("all the bolts", "every
    /// HINGE-*"). Read-only; the ground truth re-resolves by its own name test so a miscount shows.
    /// </summary>
    public static class SelectByFilter
    {
        private static readonly Dictionary<string, string[]> KindWords = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["bolt"] = new[] { "bolt", "screw", "hcs", "shcs", "cap", "socket", "hex", "b18", "din", "iso" },
            ["nut"] = new[] { "nut", "ecrou" },
            ["washer"] = new[] { "washer", "rondelle" },
            ["flange"] = new[] { "flange", "plate" },
            ["shaft"] = new[] { "shaft", "axle", "pin", "rod" },
            ["gear"] = new[] { "gear", "pinion" },
        };

        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            // MUST name a specific KIND (this is a kind filter) — NOT generic "components/parts", which would shadow
            // get_component_info's "list the components" and other whole-assembly reads.
            return Regex.IsMatch(c, @"\b(select|filter|find|which|list|how many|count|all the)\b") &&
                   Regex.IsMatch(c, @"\b(bolt|bolts|nut|nuts|washer|washers|screw|screws|flange|flanges|shaft|shafts|gear|gears)\b") &&
                   !Regex.IsMatch(c, @"\b(mate|dimension|feature|property|properties|transform|distance|colou?r|paint|appearance)\b");
        }

        public static async Task<SelectByFilterResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new SelectByFilterResult();
            var asm = model as AssemblyDoc;
            if (asm == null) { res.Error = "Open the assembly (.SLDASM) to filter components."; return res; }

            string c = (intent ?? "").ToLowerInvariant();
            string[] tokens = null; string kind = null;
            foreach (var kv in KindWords) if (Regex.IsMatch(c, @"\b" + kv.Key + @"s?\b")) { kind = kv.Key; tokens = kv.Value; break; }
            if (tokens == null) { res.Error = "Tell me which kind to filter, e.g. \"select all the bolts\"."; return res; }
            res.Filter = kind;

            await emit("Filter", "resolving '" + kind + "'", "run", null);
            foreach (var o in (asm.GetComponents(true) as object[]) ?? new object[0])
            {
                var comp = o as Component2; if (comp == null) continue;
                string nm = null; try { nm = comp.Name2; } catch { }
                if (nm == null) continue;
                string low = nm.ToLowerInvariant();
                // a nut/washer must NOT be caught by the bolt filter (bolt hints overlap "screw" etc.)
                if (kind == "bolt" && (low.Contains("nut") || low.Contains("washer"))) continue;
                bool hit = false; foreach (var t in tokens) if (low.Contains(t)) { hit = true; break; }
                if (!hit) continue;
                res.Matched++;
                bool sup = false; try { sup = comp.IsSuppressed(); } catch { }
                if (!sup) res.Active++;
                res.Names.Add(nm);
            }

            await emit("Filter", null, "done", res.Matched + " match '" + kind + "' (" + res.Active + " active)");
            if (res.Matched == 0) { res.Info = "No components match '" + kind + "'."; return res; }

            var sb = new StringBuilder(res.Matched + " component" + (res.Matched == 1 ? "" : "s") + " match '" + kind + "'" +
                (res.Active != res.Matched ? " (" + res.Active + " active, " + (res.Matched - res.Active) + " suppressed)" : "") + ":");
            int shown = 0;
            foreach (var n in res.Names) { if (shown++ >= 20) { sb.Append("\n… (" + (res.Matched - 20) + " more)"); break; } sb.Append("\n• " + n); }
            res.Info = sb.ToString();
            return res;
        }
    }
}
