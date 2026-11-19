using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT linear decimal-places read — shares NO code with SetDecimalPlaces' write. Its own
        // GetUserPreferenceInteger, so the harness proves the precision CHANGED to the KNOWN target and a rerun is a no-op.
        public static JObject MeasureSetDecimalPlaces(IModelDoc2 model)
        {
            var res = new JObject();
            int dp = -999;
            if (model != null) { try { dp = model.Extension.GetUserPreferenceInteger((int)swUserPreferenceIntegerValue_e.swUnitsLinearDecimalPlaces, (int)swUserPreferenceOption_e.swDetailingNoOptionSpecified); } catch { } }
            res["dp"] = dp;
            return res;
        }
    }
}
