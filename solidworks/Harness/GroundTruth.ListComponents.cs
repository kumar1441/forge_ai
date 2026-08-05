using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT component roster — shares NO code with ListComponents. Its own GetComponents traversal, its own
        // suppression read and its own unique-file set, so the harness can prove the handler's totals rather than
        // echo them. Counts suppressed components DELIBERATELY (most GT here skips them), because a roster that
        // silently drops suppressed instances is the exact bug this tool exists to prevent.
        public static JObject MeasureListComponents(IModelDoc2 model)
        {
            var res = new JObject();
            var asm = model as AssemblyDoc;
            if (asm == null) { res["total"] = -1; return res; }

            int total = 0, suppressed = 0, subAsm = 0;
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var names = new JArray();
            try
            {
                foreach (var o in (asm.GetComponents(false) as object[]) ?? new object[0])
                {
                    var c = o as Component2; if (c == null) continue;
                    total++;
                    bool sup = false; try { sup = c.IsSuppressed(); } catch { }
                    if (sup) suppressed++;
                    string p = null; try { p = c.GetPathName(); } catch { }
                    if (!string.IsNullOrEmpty(p))
                    {
                        files.Add(p);
                        if (p.EndsWith(".sldasm", StringComparison.OrdinalIgnoreCase)) subAsm++;
                    }
                    string n = null; try { n = c.Name2; } catch { }
                    if (!string.IsNullOrEmpty(n)) names.Add(n);
                }
            }
            catch { }

            res["total"] = total;
            res["suppressed"] = suppressed;
            res["subAssemblies"] = subAsm;
            res["uniqueFiles"] = files.Count;
            res["names"] = names;
            return res;
        }
    }
}
