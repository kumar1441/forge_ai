using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for detect_simulation_artifacts (tool 256). Re-derives via
    /// `FeatureManager.GetFeatures(false)` (ALL features incl. sub-features, one array call) — the SAME
    /// different-traversal-primitive split `DetectInContextWrites`/`HandleUnknownFeatures`/
    /// `HandleAssemblyFeatures` already use — with its own separate weld-bead/belt-chain type-name check,
    /// sharing no code with the handler's `Classify`.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureDetectSimulationArtifacts(IModelDoc2 model)
        {
            var mo = new JObject();
            int weldBeads = 0, beltChains = 0, total = 0;
            object[] feats = null;
            try { feats = model.FeatureManager.GetFeatures(false) as object[]; } catch { }
            foreach (var o in feats ?? new object[0])
            {
                var feat = o as Feature; if (feat == null) continue;
                total++;
                string tn = null; try { tn = feat.GetTypeName2(); } catch { }
                if (string.IsNullOrEmpty(tn)) continue;
                if (tn.IndexOf("WeldBead", System.StringComparison.OrdinalIgnoreCase) >= 0) weldBeads++;
                else if (tn.IndexOf("BeltChain", System.StringComparison.OrdinalIgnoreCase) >= 0) beltChains++;
            }
            mo["expectedTotalFeatures"] = total;
            mo["expectedWeldBeads"] = weldBeads;
            mo["expectedBeltChains"] = beltChains;
            mo["expectedArtifactCount"] = weldBeads + beltChains;
            return mo;
        }
    }
}
