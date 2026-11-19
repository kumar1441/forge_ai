using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for handle_unknown_features (tool 243). Re-derives the same
    /// GetTypeName2()=="MacroFeature" signal via a DIFFERENT traversal primitive than the handler:
    /// FeatureManager.GetFeatures(false) (ALL features including sub-features, one array call) instead of the
    /// handler's FirstFeature/GetNextFeature top-level linked-list walk. Shares no code with the handler.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureHandleUnknownFeatures(IModelDoc2 model)
        {
            var mo = new JObject();
            int scanned = 0, unknown = 0;

            object[] feats = null;
            try { feats = model.FeatureManager.GetFeatures(false) as object[]; } catch { }
            foreach (var fo in feats ?? new object[0])
            {
                var feat = fo as Feature; if (feat == null) continue;
                scanned++;
                string tn = null; try { tn = feat.GetTypeName2(); } catch { }
                if (string.Equals(tn, "MacroFeature", StringComparison.OrdinalIgnoreCase)) unknown++;
            }

            mo["expectedFeaturesScanned"] = scanned;
            mo["expectedUnknownFeatureCount"] = unknown;
            return mo;
        }
    }
}
