using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class PropRow { public string Source; public string Name; public string Value; }

    public class GetPropertiesResult
    {
        public int Total;              // total custom properties read across the doc + its unique parts
        public int Sources;            // how many distinct files contributed properties
        public List<PropRow> Rows = new List<PropRow>();
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 10 — get_custom_properties (READ). Reads file-level + active-config custom properties of the active doc,
    /// and (for an assembly) each UNIQUE part's file-level properties — part number, material, description, Toolbox
    /// metadata, etc. A support act for BOM / release questions ("what part number is this", "what's tagged on these").
    /// Read-only. Own property read; the ground truth re-reads by a different enumeration so a miscount shows up.
    /// </summary>
    public static class GetProperties
    {
        public static bool IsPropsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            return System.Text.RegularExpressions.Regex.IsMatch(c, @"\b(custom )?propert(y|ies)\b") ||
                   System.Text.RegularExpressions.Regex.IsMatch(c, @"\b(part number|part numbers|metadata|file properties|tags|custom props)\b");
        }

        public static async Task<GetPropertiesResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new GetPropertiesResult();
            if (model == null) { res.Error = "Open a document to read its properties."; return res; }

            await emit("Reader", "reading custom properties", "run", null);
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1) the active doc's own props (file-level "" config + active config)
            string activeCfg = ""; try { activeCfg = ((Configuration)model.GetActiveConfiguration()).Name; } catch { }
            ReadDoc(model, "this document", activeCfg, res);
            try { files.Add(model.GetPathName() ?? "active"); } catch { }

            // 2) for an assembly, each UNIQUE component part's file-level props
            var asm = model as AssemblyDoc;
            if (asm != null)
            {
                foreach (var o in (asm.GetComponents(false) as object[]) ?? new object[0])
                {
                    var c = o as Component2; if (c == null) continue;
                    bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                    string path = null; try { path = c.GetPathName(); } catch { }
                    if (string.IsNullOrEmpty(path) || !files.Add(path)) continue;   // once per file
                    var pd = c.GetModelDoc2() as IModelDoc2; if (pd == null) continue;
                    ReadDoc(pd, System.IO.Path.GetFileName(path), "", res);
                }
            }

            res.Sources = files.Count;
            await emit("Reader", null, "done",
                res.Total + " propert" + (res.Total == 1 ? "y" : "ies") + " across " + res.Sources + " file" + (res.Sources == 1 ? "" : "s"));

            if (res.Total == 0) { res.Info = "No custom properties found on this document or its parts."; return res; }

            var sb = new StringBuilder(res.Total + " custom propert" + (res.Total == 1 ? "y" : "ies") + " across " + res.Sources + " file(s):");
            int shown = 0;
            foreach (var r in res.Rows)
            {
                if (shown++ >= 24) { sb.Append("\n… (" + (res.Total - 24) + " more)"); break; }
                sb.Append("\n• [" + r.Source + "] " + r.Name + " = " + r.Value);
            }
            res.Info = sb.ToString();
            return res;
        }

        // read one doc's custom-property manager at the given config (and, for the active doc, also file-level "")
        private static void ReadDoc(IModelDoc2 doc, string source, string cfg, GetPropertiesResult res)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var scope in new[] { "", cfg })
            {
                if (scope == null) continue;
                CustomPropertyManager cpm = null;
                try { cpm = doc.Extension.get_CustomPropertyManager(scope); } catch { }
                if (cpm == null) continue;
                object namesObj = null;
                try { namesObj = cpm.GetNames(); } catch { }
                var names = namesObj as string[]; if (names == null) continue;
                foreach (var n in names)
                {
                    if (string.IsNullOrEmpty(n) || !seen.Add(n)) continue;   // dedupe file vs config for same name
                    string val = null, resolved = null;
                    try { cpm.Get4(n, false, out val, out resolved); } catch { }
                    res.Rows.Add(new PropRow { Source = source, Name = n, Value = resolved ?? val ?? "" });
                    res.Total++;
                }
            }
        }
    }
}
