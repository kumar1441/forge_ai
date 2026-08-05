using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for detect_in_context_writes (tool 242). Re-derives the same per-feature
    /// ListExternalFileReferencesCount signal via a DIFFERENT traversal primitive than the handler:
    /// FeatureManager.GetFeatures(false) (ALL features including sub-features, one array call) instead of the
    /// handler's FirstFeature/GetNextFeature top-level linked-list walk. Shares no code with the handler.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureDetectInContextWrites(IModelDoc2 model)
        {
            var mo = new JObject();
            int scanned = 0, withRefs = 0;
            var affected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            object[] feats = null;
            try { feats = model.FeatureManager.GetFeatures(false) as object[]; } catch { }
            foreach (var fo in feats ?? new object[0])
            {
                var feat = fo as Feature; if (feat == null) continue;
                scanned++;
                int cnt = 0;
                try { cnt = feat.ListExternalFileReferencesCount(); } catch { }
                if (cnt <= 0) continue;
                withRefs++;

                object modelPathObj = null, compPathObj = null, featObj = null, dataTypeObj = null, statusObj = null, refEntObj = null, featComObj = null;
                try { feat.ListExternalFileReferences(out modelPathObj, out compPathObj, out featObj, out dataTypeObj, out statusObj, out refEntObj, out featComObj); } catch { }
                var paths = modelPathObj as object[];
                if (paths != null)
                    foreach (var p in paths) { var s = p as string; if (!string.IsNullOrWhiteSpace(s)) affected.Add(s); }
            }

            mo["expectedFeaturesScanned"] = scanned;
            mo["expectedInContextFeatureCount"] = withRefs;
            mo["expectedAffectedFileCount"] = affected.Count;
            mo["expectedAffectedFiles"] = new JArray(affected.OrderBy(s => s, StringComparer.OrdinalIgnoreCase));
            return mo;
        }
    }
}
