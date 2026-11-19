using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT linear-unit read — shares NO code with SetDocumentUnits' write. Reads swUnitsLinear via its own
        // GetUserPreferenceInteger and maps the enum to a label, so the harness can prove the unit CHANGED to a KNOWN
        // target (run0 label != run1 label == "inches") and that a rerun is a no-op.
        public static JObject MeasureSetDocumentUnits(IModelDoc2 model)
        {
            var res = new JObject();
            if (model == null) { res["unitInt"] = -999; res["label"] = "none"; return res; }
            int u = -999;
            try { u = model.Extension.GetUserPreferenceInteger((int)swUserPreferenceIntegerValue_e.swUnitsLinear, (int)swUserPreferenceOption_e.swDetailingNoOptionSpecified); } catch { }
            res["unitInt"] = u;
            res["label"] = Label(u);
            return res;
        }

        private static string Label(int u)
        {
            if (u == (int)swLengthUnit_e.swMM) return "mm";
            if (u == (int)swLengthUnit_e.swCM) return "cm";
            if (u == (int)swLengthUnit_e.swINCHES) return "inches";
            if (u == (int)swLengthUnit_e.swFEET) return "feet";
            if (u == (int)swLengthUnit_e.swMETER) return "meters";
            return "u" + u;
        }
    }
}
