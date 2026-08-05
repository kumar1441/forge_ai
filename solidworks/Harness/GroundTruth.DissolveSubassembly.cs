using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT dissolve census for dissolve_subassembly (tool 40). Shares NO code with DissolveSubassembly — its
        // own top-level traversal publishing the raw counts (topLevel / subAssemblies / parts) AND the top-level
        // component NAME list. The harness proves the flatten CROSSED against the handler's chosen target: run0 has the
        // named sub-assembly present with subAssemblies>=1; run1 has it GONE from the name list, subAssemblies down by
        // exactly 1 and topLevel risen (children promoted); run2 == run1 (idempotent). "Did the sub really dissolve" is
        // decided by a tree walk that never touches DissolveSubAssembly() or the handler's own recount.
        public static JObject MeasureDissolveSubassembly(IModelDoc2 model)
        {
            var res = new JObject();
            var asm = model as AssemblyDoc;
            if (asm == null) { res["error"] = "not an assembly"; return res; }
            int top = 0, sub = 0, parts = 0;
            var names = new JArray();
            foreach (var o in (asm.GetComponents(true) as object[]) ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                top++;
                string nm = null; try { nm = c.Name2; } catch { }
                if (nm != null) names.Add(nm);
                var pd = c.GetModelDoc2() as IModelDoc2;
                bool isAsm = false; try { isAsm = pd != null && (int)pd.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY; } catch { }
                if (isAsm) sub++; else parts++;
            }
            res["topLevel"] = top;
            res["subAssemblies"] = sub;
            res["parts"] = parts;
            res["names"] = names;
            return res;
        }
    }
}
