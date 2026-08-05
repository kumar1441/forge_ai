using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for handle_assembly_features (tool 250). Re-derives the same solid-modifying-type
    /// signal via a DIFFERENT traversal primitive than the handler: FeatureManager.GetFeatures(false) (ALL
    /// assembly-owned features including sub-features, one array call) instead of the handler's
    /// FirstFeature/GetNextFeature top-level linked-list walk. Family classification is re-implemented locally
    /// (not calling HandleAssemblyFeatures.IsSolidModifyingType) so a bug in the handler's classifier can't hide
    /// behind an agreeing GT.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureHandleAssemblyFeatures(IModelDoc2 model)
        {
            var mo = new JObject();
            int scanned = 0, asmFeatureCount = 0;
            var names = new List<string>();

            object[] feats = null;
            try { feats = model.FeatureManager.GetFeatures(false) as object[]; } catch { }
            foreach (var fo in feats ?? new object[0])
            {
                var feat = fo as Feature; if (feat == null) continue;
                scanned++;
                string tn = null, name = null;
                try { tn = feat.GetTypeName2(); } catch { }
                try { name = feat.Name; } catch { }
                if (string.IsNullOrEmpty(tn)) continue;

                bool solidModifying =
                    tn.IndexOf("Hole", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    tn.Equals("ICE", StringComparison.OrdinalIgnoreCase) ||
                    tn.IndexOf("Cut", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    tn.IndexOf("Fillet", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    tn.IndexOf("Chamfer", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    tn.IndexOf("Draft", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    tn.IndexOf("Shell", StringComparison.OrdinalIgnoreCase) >= 0;

                if (solidModifying) { asmFeatureCount++; names.Add(name); }
            }

            mo["expectedFeaturesScanned"] = scanned;
            mo["expectedAssemblyFeatureCount"] = asmFeatureCount;
            mo["expectedAssemblyFeatureNames"] = new JArray(names.OrderBy(s => s, StringComparer.OrdinalIgnoreCase));
            return mo;
        }
    }
}
