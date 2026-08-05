using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT flexibility census for set_subassembly_flexibility (tool 158). Shares NO code with
        // SetSubassemblyFlexibility — its own top-level traversal re-reading IComponent2.Solving directly (never
        // the handler's own StateBefore/StateAfter). Publishes each top-level sub-assembly's name + raw Solving
        // int (0=rigid,1=flexible) plus a byName lookup so the harness can pull the SAME named target the handler
        // resolved and compare its state across run0/run1/run2.
        public static JObject MeasureSetSubassemblyFlexibility(IModelDoc2 model)
        {
            var res = new JObject();
            var asm = model as AssemblyDoc;
            if (asm == null) { res["error"] = "not an assembly"; return res; }
            var byName = new JObject();
            int count = 0;
            foreach (var o in (asm.GetComponents(true) as object[]) ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                var pd = c.GetModelDoc2() as IModelDoc2;
                bool isAsm = false; try { isAsm = pd != null && (int)pd.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY; } catch { }
                if (!isAsm) continue;
                count++;
                string nm = null; try { nm = c.Name2; } catch { }
                int solving = -1; try { solving = c.Solving; } catch { }
                if (nm != null) byName[nm] = solving;
            }
            res["subAssemblies"] = count;
            res["byName"] = byName;
            return res;
        }
    }
}
