using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for handle_config_explosion (tool 255). Re-derives the configuration count via
    /// `IModelDoc2.GetConfigurationCount()` — a DIFFERENT API from the handler's own `GetConfigurationNames()
    /// .Length` — sharing no code, same "independent API, not independent re-implementation of the same call"
    /// shape `GetConfigs.cs`'s own doc comment already establishes for its sibling tool.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureHandleConfigExplosion(IModelDoc2 model)
        {
            var mo = new JObject();
            int count = 0;
            try { count = model.GetConfigurationCount(); } catch { }
            mo["expectedConfigCount"] = count;
            mo["expectedExploded"] = count >= HandleConfigExplosion.ExplosionThreshold;
            return mo;
        }
    }
}
