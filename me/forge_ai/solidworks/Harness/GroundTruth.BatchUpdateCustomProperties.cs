using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT property-name parse — its own regex, mirrors the handler's goal (not its code) so a fixture's
        // expected name can be resolved from the same intent string without calling into BatchUpdateCustomProperties.
        public static string ParseBatchPropName(string intent)
        {
            string c = intent ?? "";
            var q = System.Text.RegularExpressions.Regex.Matches(c, "[\"']([^\"']+)[\"']");
            if (q.Count >= 1) return q[0].Groups[1].Value.Trim();
            var m = Regex.Match(c, @"propert(?:y|ies)\s+(?:called\s+|named\s+)?([A-Za-z0-9_\- ]+?)\s+to\s+", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value.Trim();
            return null;
        }

        // INDEPENDENT per-part custom-property census: every unique non-suppressed part's OWN CustomPropertyManager,
        // read via its own GetNames/Get4 walk — shares NO code/objects with BatchUpdateCustomProperties.
        public static JObject MeasureBatchUpdateCustomProperties(IModelDoc2 model, string propName)
        {
            var arr = new JArray();
            var res = new JObject();
            var asm = model as AssemblyDoc;
            if (asm == null || string.IsNullOrEmpty(propName)) { res["parts"] = arr; res["propName"] = propName; return res; }

            object[] comps = asm.GetComponents(false) as object[];
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var o in comps ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                string path = null; try { path = c.GetPathName(); } catch { }
                if (string.IsNullOrEmpty(path) || !seen.Add(path)) continue;
                var pd = c.GetModelDoc2() as IModelDoc2; if (pd == null) continue;

                CustomPropertyManager cpm = null; try { cpm = pd.Extension.get_CustomPropertyManager(""); } catch { }
                bool present = false; string val = null;
                if (cpm != null)
                {
                    try
                    {
                        var names = cpm.GetNames() as string[];
                        if (names != null) foreach (var n in names) if (string.Equals(n, propName, StringComparison.OrdinalIgnoreCase)) { present = true; break; }
                    }
                    catch { }
                    if (present) { string v = null, resolved = null; try { cpm.Get4(propName, false, out v, out resolved); } catch { } val = string.IsNullOrEmpty(resolved) ? v : resolved; }
                }
                string nm = null; try { nm = c.Name2; } catch { }
                var jo = new JObject(); jo["name"] = nm; jo["present"] = present; jo["value"] = val;
                arr.Add(jo);
            }
            res["parts"] = arr;
            res["propName"] = propName;
            return res;
        }
    }
}
