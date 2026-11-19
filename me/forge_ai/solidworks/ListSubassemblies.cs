using System;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class ListSubassembliesResult
    {
        public int TopLevel;
        public int SubAssemblies;
        public int Parts;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool — list_subassemblies (READ). Breaks the top level of an assembly into sub-assemblies vs parts. Answers
    /// "how many sub-assemblies", "is this flat or nested". A component is a sub-assembly if its referenced document is
    /// itself an assembly. Read-only; own top-level traversal + doc-type read.
    /// </summary>
    public static class ListSubassemblies
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            return System.Text.RegularExpressions.Regex.IsMatch(c, @"\b(sub.?assembl(y|ies)|subassembl(y|ies)|nested|flat)\b") &&
                   System.Text.RegularExpressions.Regex.IsMatch(c, @"\b(how many|count|list|any|is this)\b");
        }

        public static async Task<ListSubassembliesResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new ListSubassembliesResult();
            var asm = model as AssemblyDoc;
            if (asm == null) { res.Error = "Open the assembly (.SLDASM) to list its sub-assemblies."; return res; }

            await emit("Surveyor", "scanning the top level", "run", null);
            foreach (var o in (asm.GetComponents(true) as object[]) ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                res.TopLevel++;
                var pd = c.GetModelDoc2() as IModelDoc2;
                bool isAsm = false; try { isAsm = pd != null && (int)pd.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY; } catch { }
                if (isAsm) res.SubAssemblies++; else res.Parts++;
            }

            await emit("Surveyor", null, "done", res.SubAssemblies + " sub-assemblies · " + res.Parts + " parts (top level: " + res.TopLevel + ")");
            if (res.TopLevel == 0) { res.Error = "No components in this assembly."; return res; }

            res.Info = "Top level: " + res.SubAssemblies + " sub-assembl" + (res.SubAssemblies == 1 ? "y" : "ies") + ", " +
                       res.Parts + " part" + (res.Parts == 1 ? "" : "s") +
                       (res.SubAssemblies == 0 ? " — this is a FLAT assembly." : " — this is a NESTED assembly.");
            return res;
        }
    }
}
