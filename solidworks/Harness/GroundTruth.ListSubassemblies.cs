using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT sub-assembly count — shares NO code with ListSubassemblies. Own top-level traversal + doc-type read.
        public static JObject MeasureListSubassemblies(IModelDoc2 model)
        {
            var res = new JObject();
            var asm = model as AssemblyDoc;
            if (asm == null) { res["error"] = "not an assembly"; return res; }
            int top = 0, sub = 0, parts = 0;
            foreach (var o in (asm.GetComponents(true) as object[]) ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                top++;
                var pd = c.GetModelDoc2() as IModelDoc2;
                bool isAsm = false; try { isAsm = pd != null && (int)pd.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY; } catch { }
                if (isAsm) sub++; else parts++;
            }
            res["topLevel"] = top; res["subAssemblies"] = sub; res["parts"] = parts;
            return res;
        }
    }
}
