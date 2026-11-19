using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public class GetCustomPropertyResult
    {
        public string PropName;
        public string Value;
        public bool Found;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool — get_custom_property (READ). Looks up ONE named file-scope custom property's resolved value —
    /// "what is the Revision property?", 'get the custom property "PartNo"'. The single-field counterpart to
    /// get_custom_properties (list-all). Read-only; independent GT re-reads the same name from its own property map.
    /// </summary>
    public static class GetCustomProperty
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            // singular "property" only — plural "properties" is get_custom_properties (list-all).
            return Regex.IsMatch(c, @"\bproperty\b") && !Regex.IsMatch(c, @"\bproperties\b") &&
                   Regex.IsMatch(c, @"\b(what|which|get|show|read|value|is|tell)\b") &&
                   !Regex.IsMatch(c, @"\b(set|add|write|update|delete|remove|drop|clear|all|list)\b");   // those are set/delete/list handlers
        }

        public static async Task<GetCustomPropertyResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new GetCustomPropertyResult();
            if (model == null) { res.Error = "Open a part or assembly to read a custom property."; return res; }
            string txt = intent ?? "";

            string name = null;
            var q = Regex.Match(txt, "[\"']([^\"']+)[\"']");
            if (q.Success) name = q.Groups[1].Value.Trim();
            else
            {
                var m = Regex.Match(txt, @"(?:the\s+)?(?:custom\s+)?propert(?:y|ies)\s+(?:called\s+|named\s+)?([A-Za-z0-9_\-]+)", RegexOptions.IgnoreCase);
                if (m.Success) name = m.Groups[1].Value.Trim();
                else { var m2 = Regex.Match(txt, @"(?:what|which|value)\s+(?:is\s+)?(?:the\s+)?([A-Za-z0-9_\-]+)\s+propert", RegexOptions.IgnoreCase); if (m2.Success) name = m2.Groups[1].Value.Trim(); }
            }
            if (string.IsNullOrWhiteSpace(name) || name.Equals("custom", StringComparison.OrdinalIgnoreCase))
            { res.Error = "Which property? e.g. \"what is the Revision property?\"."; return res; }
            res.PropName = name;

            await emit("Gauge", "reading property '" + name + "'", "run", null);
            CustomPropertyManager cpm = null;
            try { cpm = model.Extension.get_CustomPropertyManager(""); } catch { }
            if (cpm != null)
            {
                var names = cpm.GetNames() as string[];
                string canonical = null;
                if (names != null) foreach (var n in names) if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) { canonical = n; break; }
                if (canonical != null)
                {
                    res.Found = true;
                    string val = null, resolved = null; try { cpm.Get4(canonical, false, out val, out resolved); } catch { }
                    res.Value = string.IsNullOrEmpty(resolved) ? val : resolved;
                }
            }

            if (!res.Found) { res.Info = "No custom property named '" + name + "' on this document."; await emit("Gauge", null, "done", "'" + name + "' not present"); return res; }
            await emit("Gauge", null, "done", "'" + name + "' = '" + res.Value + "'");
            res.Info = "'" + name + "' = '" + res.Value + "'.";
            return res;
        }
    }
}
