using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT for move_file_with_references (tool 129). Shares no code with the handler: a FILE-LEVEL
        // query (ISldWorks.GetDocumentDependencies2, the same call FindWhereUsed/tool 128 already prove) rather
        // than the handler's own AssemblyDoc.ReplaceComponents-based relink. Called BOTH before (run0, active
        // doc's path is still the OLD folder) and after (run1, active doc's path is the NEW folder post-move).
        // Unlike rename (folder never changes), a move's active-doc folder DOES change between calls, so this
        // scans TWO candidate folders every time — the doc's current folder AND its immediate parent — which
        // covers the common "move into/out of a subfolder" case without hardcoding any fixture-specific path:
        // at run0 the parent assembly sits in the current folder itself; at run1 (now one level deeper) it sits
        // in the parent folder.
        public static JObject MeasureMoveFileWithReferences(ISldWorks app, IModelDoc2 model)
        {
            var res = new JObject();
            var referencing = new JArray();
            string current = null; try { current = model?.GetPathName(); } catch { }
            res["currentPath"] = current;
            if (string.IsNullOrWhiteSpace(current)) { res["referencingAssemblies"] = referencing; res["count"] = 0; return res; }

            string folder = null; try { folder = Path.GetDirectoryName(current); } catch { }
            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder)) candidates.Add(folder);
            try
            {
                string parent = Directory.GetParent(folder)?.FullName;
                if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent) && !candidates.Contains(parent, StringComparer.OrdinalIgnoreCase))
                    candidates.Add(parent);
            }
            catch { }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var scanFolder in candidates)
            {
                foreach (var cand in Directory.GetFiles(scanFolder, "*.SLDASM").Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!seen.Add(cand)) continue;
                    object[] deps = null;
                    try { deps = app.GetDocumentDependencies2(cand, true, true, false) as object[]; } catch { deps = null; }
                    if (deps == null) continue;
                    for (int k = 0; k + 1 < deps.Length; k += 2)
                    {
                        string p = deps[k + 1] as string;
                        if (!string.IsNullOrEmpty(p) && string.Equals(NormMove(p), NormMove(current), StringComparison.OrdinalIgnoreCase))
                        { referencing.Add(Path.GetFileName(cand)); break; }
                    }
                }
            }
            res["referencingAssemblies"] = referencing;
            res["count"] = referencing.Count;
            return res;
        }

        private static string NormMove(string p) { return string.IsNullOrEmpty(p) ? "" : p.Trim().ToLowerInvariant().Replace('/', '\\'); }
    }
}
