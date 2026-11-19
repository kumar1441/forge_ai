using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for create_curve (tool 214). Own feature walk: detects the Forge-Curve feature
    /// (run0=absent -> run1=present -> run2=present proves the write + idempotency) and records its type name via a
    /// path independent of the handler (it never sees the handler's captured feature). A curve is not a body, so the
    /// honest signals are feature presence, type, and a clean rebuild. Read-only.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureCreateCurve(IModelDoc2 model)
        {
            var mo = new JObject();
            if (model == null) { mo["error"] = "no model"; return mo; }

            bool hasForge = false; string curveType = null;
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    string nm = null; try { nm = f.Name; } catch { }
                    if (nm != null && nm.StartsWith("Forge-Curve", StringComparison.OrdinalIgnoreCase))
                    {
                        hasForge = true;
                        try { curveType = f.GetTypeName2(); } catch { }
                        break;
                    }
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch { }

            int rw = 0; try { rw = model.Extension.GetWhatsWrongCount(); } catch { }

            mo["hasForgeCurve"] = hasForge;
            mo["curveType"] = curveType;
            mo["rebuildErrors"] = rw;
            return mo;
        }
    }
}
