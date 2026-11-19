using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT angular-unit read — shares NO code with SetAngularUnits' write. Its own GetUserPreferenceInteger +
        // enum->label map, so the harness proves the angular unit CHANGED to a KNOWN target and a rerun is a no-op.
        public static JObject MeasureSetAngularUnits(IModelDoc2 model)
        {
            var res = new JObject();
            if (model == null) { res["unitInt"] = -999; res["label"] = "none"; return res; }
            int u = -999;
            try { u = model.Extension.GetUserPreferenceInteger((int)swUserPreferenceIntegerValue_e.swUnitsAngular, (int)swUserPreferenceOption_e.swDetailingNoOptionSpecified); } catch { }
            res["unitInt"] = u;
            res["label"] = u == (int)swAngleUnit_e.swDEGREES ? "degrees" : u == (int)swAngleUnit_e.swRADIANS ? "radians" : "a" + u;
            return res;
        }
    }
}
