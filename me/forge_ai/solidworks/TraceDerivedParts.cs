using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class DerivedChainHop
    {
        public string Path;
        public string FeatureName;   // the feature IN THE CHILD that carries the reference to this file
    }

    public class TraceDerivedPartsResult
    {
        public bool Success;
        public string RootPath;
        public int ChainDepth;                          // 0 = self-contained, no lineage
        public List<DerivedChainHop> Chain = new List<DerivedChainHop>();   // root(open doc) -> ... -> ultimate ancestor
        public bool Truncated;                           // hit the depth/node cap before the chain ended
        public string Info;
        public string Error;
    }

    /// <summary>
    /// TraceDerivedParts (tool 251, READ) — "external reference chains (base → mirrored → derived → inserted):
    /// map the chain, warn which files an edit will cascade into." Distinct from DetectInContextWrites (tool
    /// 242, one hop: THIS document's own features and which files THEY touch) — this tool follows the SAME
    /// per-feature external-file-reference signal (`IFeature.ListExternalFileReferencesCount()`/
    /// `ListExternalFileReferences`, proven live by tool 242) but walks it RECURSIVELY across documents: if the
    /// referenced file is ITSELF derived from something else, that hop is opened and walked too, building the
    /// full lineage back to its ultimate ancestor(s) rather than stopping one level up.
    ///
    /// Each hop beyond the currently open document is resolved the same reuse-if-open / open-then-close pattern
    /// CopyPropertiesBetweenFiles.cs already uses (Rule #7 — never disturb what the user already has open).
    /// Bounded by MaxDepth and a visited-set (cycle guard — a broken/circular in-context reference should never
    /// hang this tool).
    ///
    /// READ-ONLY: nothing is changed, rebuilt or saved. Every opened-by-us document is closed again afterward.
    /// </summary>
    public static class TraceDerivedParts
    {
        private const int MaxDepth = 8;

        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            bool derivedWord = Regex.IsMatch(c, @"\bderiv(ed|ation|es|e)\b|\blineage\b");
            bool chainAsk = Regex.IsMatch(c, @"\btrace\b|\bmap\b") && Regex.IsMatch(c, @"\bchain\b|\bpart(s)?\b|\breference(s)?\b");
            return derivedWord || chainAsk;
        }

        public static async Task<TraceDerivedPartsResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new TraceDerivedPartsResult();
            if (model == null) { res.Error = "Open the part or assembly whose derived-part lineage you want to trace."; return res; }

            string root = null; try { root = model.GetPathName(); } catch { }
            if (string.IsNullOrWhiteSpace(root))
            { res.Error = "This document has never been saved, so it has no path — lineage can only be traced for a file on disk."; return res; }
            res.RootPath = root;

            await emit("Sentinel", "walking the external-file-reference chain for " + Path.GetFileName(root), "run", null);

            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root };
            IModelDoc2 current = model;
            string currentPath = root;
            bool weOpenedCurrent = false;

            try
            {
                for (int depth = 0; depth < MaxDepth; depth++)
                {
                    string parentPath = null, featName = null;
                    FindFirstExternalRef(current, out parentPath, out featName);

                    // close the current hop's document if WE opened it (never the user's original open doc)
                    if (weOpenedCurrent && current != null) { try { app.CloseDoc(current.GetTitle()); } catch { } }

                    if (parentPath == null) break;                      // no further ancestor — chain ends here
                    if (!visited.Add(parentPath)) { res.Truncated = true; break; }   // cycle guard

                    res.Chain.Add(new DerivedChainHop { Path = parentPath, FeatureName = featName });

                    if (!File.Exists(parentPath)) break;                // ancestor file is gone — chain ends, honestly

                    // ---- resolve the ancestor doc: reuse if already open, else open it ourselves ----
                    IModelDoc2 parentDoc = null;
                    try { parentDoc = app.GetOpenDocumentByName(parentPath) as IModelDoc2; } catch { }
                    weOpenedCurrent = parentDoc == null;
                    if (parentDoc == null)
                    {
                        int docType = parentPath.EndsWith(".sldasm", StringComparison.OrdinalIgnoreCase)
                            ? (int)swDocumentTypes_e.swDocASSEMBLY : (int)swDocumentTypes_e.swDocPART;
                        int errs = 0, warns = 0;
                        try { parentDoc = app.OpenDoc6(parentPath, docType, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref errs, ref warns) as IModelDoc2; }
                        catch { parentDoc = null; }
                        if (parentDoc == null) break;                   // couldn't open the ancestor — chain ends, honestly
                    }
                    current = parentDoc;
                    currentPath = parentPath;
                    if (depth == MaxDepth - 1) res.Truncated = true;
                }
            }
            finally
            {
                // safety net: if the loop exited via an exception mid-hop on a doc WE opened, don't leak it open
                if (weOpenedCurrent && current != null && !ReferenceEquals(current, model))
                { try { app.CloseDoc(current.GetTitle()); } catch { } }
            }

            res.ChainDepth = res.Chain.Count;
            res.Success = true;

            res.Info = res.ChainDepth == 0
                ? Path.GetFileName(root) + " is self-contained — no derived-part lineage; it doesn't trace back to any other file."
                : Path.GetFileName(root) + " traces back " + res.ChainDepth + " hop" + (res.ChainDepth == 1 ? "" : "s") + ": " +
                  Path.GetFileName(root) + " <- " + string.Join(" <- ", res.Chain.Select(h => Path.GetFileName(h.Path))) +
                  ". Editing " + Path.GetFileName(res.Chain[res.Chain.Count - 1].Path) + " cascades forward through this whole chain." +
                  (res.Truncated ? " (chain continues beyond the " + MaxDepth + "-hop trace limit or hit a cycle — treat as a lower bound.)" : "");
            await emit("Sentinel", null, "done", res.Info);
            return res;
        }

        // The proven-live per-feature signal (tool 242): walk top-level features, return the FIRST one that
        // carries an external file reference (a derived/in-context part typically carries exactly one — the
        // base it was built from). Multiple such features on one document would mean multiple ancestors; this
        // v1 traces the primary lineage (first found), same scope line tool 242 draws for "attribute the risk
        // to a specific operation" rather than enumerating every combination.
        private static void FindFirstExternalRef(IModelDoc2 doc, out string parentPath, out string featureName)
        {
            parentPath = null; featureName = null;
            if (doc == null) return;
            Feature feat = null;
            try { feat = doc.FirstFeature() as Feature; } catch { }
            while (feat != null)
            {
                int cnt = 0;
                try { cnt = feat.ListExternalFileReferencesCount(); } catch { }
                if (cnt > 0)
                {
                    object modelPathObj = null, compPathObj = null, featObj = null, dataTypeObj = null, statusObj = null, refEntObj = null, featComObj = null;
                    try { feat.ListExternalFileReferences(out modelPathObj, out compPathObj, out featObj, out dataTypeObj, out statusObj, out refEntObj, out featComObj); } catch { }
                    var paths = modelPathObj as object[];
                    if (paths != null)
                        foreach (var p in paths)
                        {
                            var s = p as string;
                            if (!string.IsNullOrWhiteSpace(s)) { parentPath = s; try { featureName = feat.Name; } catch { } return; }
                        }
                }
                Feature next = null; try { next = feat.GetNextFeature() as Feature; } catch { }
                feat = next;
            }
        }
    }
}
