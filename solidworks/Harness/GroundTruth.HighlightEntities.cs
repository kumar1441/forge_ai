using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for highlight_entities (tool 238). highlight_entities and describe_geometry
    /// (tool 237) resolve criteria (top/bottom/left/right/largest/hole) via the exact same two primitives
    /// (SelectFace's planar resolution, DescribeGeometry's concave-cylindrical hole finder) — so the same face
    /// is expected either way, and MeasureDescribeGeometry's independent re-derivation applies verbatim. No
    /// second implementation to keep in sync; same non-negotiable as the rest of this file (never re-reads live
    /// selection, since the harness's ForceRebuild3 drops it between the handler call and this measurement).
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureHighlightEntities(IModelDoc2 model, string intent)
        {
            return MeasureDescribeGeometry(model, intent);
        }
    }
}
