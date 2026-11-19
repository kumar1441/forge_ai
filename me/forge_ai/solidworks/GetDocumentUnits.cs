using System;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class GetDocumentUnitsResult
    {
        public string LinearLabel;
        public int LinearInt = -999;
        public int LinearDecimals = -1;
        public string AngularLabel;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool — get_document_units (READ). Reports the document's linear + angular unit system and linear decimal places —
    /// "what units is this model in?". The read counterpart to set_document_units. Read-only; independent GT re-reads the
    /// linear-unit pref its own way (GroundTruth.MeasureSetDocumentUnits), so a mismatch would show a bad read path.
    /// </summary>
    public static class GetDocumentUnits
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            return System.Text.RegularExpressions.Regex.IsMatch(c, @"\bunits?\b") &&
                   System.Text.RegularExpressions.Regex.IsMatch(c, @"\b(what|which|show|get|tell|report|current|is|are|in what)\b") &&
                   !System.Text.RegularExpressions.Regex.IsMatch(c, @"\b(set|change|switch|convert)\b");   // that's set_document_units
        }

        public static async Task<GetDocumentUnitsResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new GetDocumentUnitsResult();
            if (model == null) { res.Error = "Open a part, assembly, or drawing to read its units."; return res; }

            await emit("Gauge", "reading unit system", "run", null);
            try { res.LinearInt = model.Extension.GetUserPreferenceInteger((int)swUserPreferenceIntegerValue_e.swUnitsLinear, (int)swUserPreferenceOption_e.swDetailingNoOptionSpecified); } catch { }
            try { res.LinearDecimals = model.Extension.GetUserPreferenceInteger((int)swUserPreferenceIntegerValue_e.swUnitsLinearDecimalPlaces, (int)swUserPreferenceOption_e.swDetailingNoOptionSpecified); } catch { }
            int ang = -999; try { ang = model.Extension.GetUserPreferenceInteger((int)swUserPreferenceIntegerValue_e.swUnitsAngular, (int)swUserPreferenceOption_e.swDetailingNoOptionSpecified); } catch { }
            res.LinearLabel = LinearLabel(res.LinearInt);
            res.AngularLabel = AngularLabel(ang);

            await emit("Gauge", null, "done", "linear " + res.LinearLabel + " · angular " + res.AngularLabel);
            res.Info = "This model is in " + res.LinearLabel + " (linear, " + (res.LinearDecimals >= 0 ? res.LinearDecimals + " dp" : "?") + "), " + res.AngularLabel + " (angular).";
            return res;
        }

        private static string LinearLabel(int u)
        {
            if (u == (int)swLengthUnit_e.swMM) return "mm";
            if (u == (int)swLengthUnit_e.swCM) return "cm";
            if (u == (int)swLengthUnit_e.swINCHES) return "inches";
            if (u == (int)swLengthUnit_e.swFEET) return "feet";
            if (u == (int)swLengthUnit_e.swMETER) return "meters";
            return "u" + u;
        }

        private static string AngularLabel(int u)
        {
            if (u == (int)swAngleUnit_e.swDEGREES) return "degrees";
            if (u == (int)swAngleUnit_e.swRADIANS) return "radians";
            return "a" + u;
        }
    }
}
