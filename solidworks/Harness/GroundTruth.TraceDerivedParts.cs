using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for trace_derived_parts (tool 251). Re-derives the same recursive external-
    /// file-reference chain via a DIFFERENT per-hop traversal primitive than the handler:
    /// FeatureManager.GetFeatures(false) (ALL features incl. sub-features, one array call) instead of the
    /// handler's FirstFeature/GetNextFeature top-level linked-list walk — same traversal-primitive split as
    /// DetectInContextWrites' GT (tool 242). The multi-hop open/close loop is its own separate implementation,
    /// sharing no code with the handler.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureTraceDerivedParts(ISldWorks app, IModelDoc2 model)
        {
            var mo = new JObject();
            const int maxDepth = 8;
            string root = null; try { root = model.GetPathName(); } catch { }
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (root != null) visited.Add(root);
            var chain = new List<string>();

            IModelDoc2 current = model;
            bool weOpened = false;

            for (int depth = 0; depth < maxDepth; depth++)
            {
                string parentPath = FirstExternalRefPath(current);
                if (weOpened && current != null) { try { app.CloseDoc(current.GetTitle()); } catch { } }
                if (parentPath == null) break;
                if (!visited.Add(parentPath)) break;
                chain.Add(parentPath);
                if (!File.Exists(parentPath)) break;

                IModelDoc2 next = null;
                try { next = app.GetOpenDocumentByName(parentPath) as IModelDoc2; } catch { }
                weOpened = next == null;
                if (next == null)
                {
                    int docType = parentPath.EndsWith(".sldasm", StringComparison.OrdinalIgnoreCase)
                        ? (int)swDocumentTypes_e.swDocASSEMBLY : (int)swDocumentTypes_e.swDocPART;
                    int errs = 0, warns = 0;
                    try { next = app.OpenDoc6(parentPath, docType, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref errs, ref warns) as IModelDoc2; }
                    catch { next = null; }
                    if (next == null) break;
                }
                current = next;
            }

            mo["expectedChainDepth"] = chain.Count;
            var arr = new JArray(); foreach (var p in chain) arr.Add(Path.GetFileName(p));
            mo["expectedChainFiles"] = arr;
            return mo;
        }

        private static string FirstExternalRefPath(IModelDoc2 doc)
        {
            if (doc == null) return null;
            object[] feats = null;
            try { feats = doc.FeatureManager.GetFeatures(false) as object[]; } catch { }
            foreach (var fo in feats ?? new object[0])
            {
                var feat = fo as Feature; if (feat == null) continue;
                int cnt = 0;
                try { cnt = feat.ListExternalFileReferencesCount(); } catch { }
                if (cnt <= 0) continue;
                object modelPathObj = null, compPathObj = null, featObj = null, dataTypeObj = null, statusObj = null, refEntObj = null, featComObj = null;
                try { feat.ListExternalFileReferences(out modelPathObj, out compPathObj, out featObj, out dataTypeObj, out statusObj, out refEntObj, out featComObj); } catch { }
                var paths = modelPathObj as object[];
                if (paths != null)
                    foreach (var p in paths) { var s = p as string; if (!string.IsNullOrWhiteSpace(s)) return s; }
            }
            return null;
        }
    }
}
