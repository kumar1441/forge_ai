using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class ListComponentsRow
    {
        public string Name;
        public string Config;
        public bool Suppressed;
    }

    public class ListComponentsResult
    {
        public int Total;
        public int TopLevel;
        public int Suppressed;
        public int SubAssemblies;
        public int UniqueFiles;
        public List<ListComponentsRow> Rows = new List<ListComponentsRow>();
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 2 — list_components (READ). The assembly tree with each instance's CONFIGURATION and suppression state,
    /// plus how many distinct files back it. This is the roster every assembly-scoped tool resolves against, and the
    /// two facts it reports are the ones that silently break batch operations: a suppressed instance has no geometry
    /// to measure, and two instances of the same file in different configurations are not interchangeable.
    ///
    /// Deliberately NOT get_component_info (tool 3), which classifies each instance (toolbox / virtual / lightweight);
    /// this is the plain roster, so it answers "what's in here" without the classification pass.
    /// </summary>
    public static class ListComponents
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            // "list the components" belongs to get_component_info (tool 3) — this one answers the tree question
            if (Regex.IsMatch(c, @"\b(flag|flags|toolbox|virtual|lightweight|classif)\b")) return false;
            return Regex.IsMatch(c, @"\b(assembly|component|part)\s*tree\b") ||
                   Regex.IsMatch(c, @"what.?s in (this|the) assembly") ||
                   (Regex.IsMatch(c, @"\b(roster|inventory)\b") && Regex.IsMatch(c, @"\b(component|components|part|parts)\b"));
        }

        public static async Task<ListComponentsResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new ListComponentsResult();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
            { res.Error = "Open an assembly to list its components."; return res; }

            await emit("Scribe", "walking the assembly tree", "run", null);
            var asm = model as AssemblyDoc;
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var o in (asm.GetComponents(false) as object[]) ?? new object[0])
            {
                var comp = o as Component2; if (comp == null) continue;
                var row = new ListComponentsRow();
                try { row.Name = comp.Name2; } catch { }
                try { row.Config = comp.ReferencedConfiguration; } catch { }
                try { row.Suppressed = comp.IsSuppressed(); } catch { }
                res.Total++;
                if (row.Suppressed) res.Suppressed++;
                string path = null; try { path = comp.GetPathName(); } catch { }
                if (!string.IsNullOrEmpty(path))
                {
                    files.Add(path);
                    if (path.EndsWith(".sldasm", StringComparison.OrdinalIgnoreCase)) res.SubAssemblies++;
                }
                res.Rows.Add(row);
            }
            res.UniqueFiles = files.Count;
            foreach (var o in (asm.GetComponents(true) as object[]) ?? new object[0]) if (o is Component2) res.TopLevel++;

            await emit("Scribe", null, "done", res.Total + " components · " + res.Suppressed + " suppressed · " + res.UniqueFiles + " files");

            if (res.Total == 0) { res.Error = "This assembly is empty — no components to list."; return res; }

            var sb = new StringBuilder(res.Total + " component" + (res.Total == 1 ? "" : "s") +
                                       " from " + res.UniqueFiles + " file" + (res.UniqueFiles == 1 ? "" : "s") +
                                       " (" + res.TopLevel + " at the top level" +
                                       (res.SubAssemblies > 0 ? ", " + res.SubAssemblies + " sub-assemblies" : "") +
                                       (res.Suppressed > 0 ? ", " + res.Suppressed + " suppressed" : "") + "):");
            int shown = 0;
            foreach (var r in res.Rows)
            {
                if (shown++ >= 24) { sb.Append("\n… (" + (res.Total - 24) + " more)"); break; }
                sb.Append("\n• " + r.Name + (string.IsNullOrEmpty(r.Config) ? "" : " [" + r.Config + "]") + (r.Suppressed ? " — suppressed" : ""));
            }
            if (res.Suppressed > 0) sb.Append("\n" + res.Suppressed + " suppressed component" + (res.Suppressed == 1 ? " has" : "s have") + " no geometry — anything measuring or mating will skip " + (res.Suppressed == 1 ? "it" : "them") + ".");
            res.Info = sb.ToString();
            return res;
        }
    }
}
