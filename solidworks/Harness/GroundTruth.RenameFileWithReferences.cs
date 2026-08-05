using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT for rename_file_with_references (tool 128). Shares no code with the handler: a FILE-LEVEL
        // query (ISldWorks.GetDocumentDependencies2, the same call FindWhereUsed already proves) rather than the
        // handler's own AssemblyDoc.ReplaceComponents-based relink. Called BOTH before (run0, active doc's path is
        // still the OLD name) and after (run1, active doc's path is the NEW name post-rename) the handler runs — in
        // both cases it reports which assemblies in the folder currently reference the active doc's CURRENT path,
        // so run0 proves the baseline link is real and run1 independently proves the relink actually stuck.
        public static JObject MeasureRenameFileWithReferences(ISldWorks app, IModelDoc2 model)
        {
            var res = new JObject();
            var referencing = new JArray();
            string current = null; try { current = model?.GetPathName(); } catch { }
            res["currentPath"] = current;
            if (string.IsNullOrWhiteSpace(current)) { res["referencingAssemblies"] = referencing; res["count"] = 0; return res; }

            string folder = null;
            try { folder = Path.GetDirectoryName(current); } catch { }
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            { res["referencingAssemblies"] = referencing; res["count"] = 0; return res; }

            foreach (var cand in Directory.GetFiles(folder, "*.SLDASM").Distinct(StringComparer.OrdinalIgnoreCase))
            {
                object[] deps = null;
                try { deps = app.GetDocumentDependencies2(cand, true, true, false) as object[]; } catch { deps = null; }
                if (deps == null) continue;
                for (int k = 0; k + 1 < deps.Length; k += 2)
                {
                    string p = deps[k + 1] as string;
                    if (!string.IsNullOrEmpty(p) && string.Equals(Norm(p), Norm(current), StringComparison.OrdinalIgnoreCase))
                    { referencing.Add(Path.GetFileName(cand)); break; }
                }
            }
            res["referencingAssemblies"] = referencing;
            res["count"] = referencing.Count;
            return res;
        }

        private static string Norm(string p) { return string.IsNullOrEmpty(p) ? "" : p.Trim().ToLowerInvariant().Replace('/', '\\'); }
    }
}
