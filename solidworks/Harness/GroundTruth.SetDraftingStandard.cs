using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT drafting-standard read — shares NO code with SetDraftingStandard's write. Its own
        // GetUserPreferenceInteger, so the harness proves the standard CHANGED to the KNOWN target and a rerun is a no-op.
        public static JObject MeasureSetDraftingStandard(IModelDoc2 model)
        {
            var res = new JObject();
            int code = -999;
            if (model != null) { try { code = model.Extension.GetUserPreferenceInteger((int)swUserPreferenceIntegerValue_e.swDetailingDimensionStandard, (int)swUserPreferenceOption_e.swDetailingNoOptionSpecified); } catch { } }
            res["code"] = code;
            return res;
        }
    }
}
