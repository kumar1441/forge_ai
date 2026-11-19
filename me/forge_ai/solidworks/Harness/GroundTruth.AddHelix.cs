using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for add_helix (tool 213). Own feature walk: detects the Forge-Helix feature
    /// (run0=absent -> run1=present -> run2=present proves write + idempotency) and records its type name via a
    /// path independent of the handler. A helix is a curve, so there is no body/volume signal — the honest signals
    /// are feature presence, type, and a clean rebuild. Read-only.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureAddHelix(IModelDoc2 model)
        {
            var mo = new JObject();
            if (model == null) { mo["error"] = "no model"; return mo; }

            bool hasForge = false; string helixType = null;
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    string nm = null; try { nm = f.Name; } catch { }
                    if (nm != null && nm.StartsWith("Forge-Helix", StringComparison.OrdinalIgnoreCase))
                    {
                        hasForge = true;
                        try { helixType = f.GetTypeName2(); } catch { }
                        break;
                    }
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch { }

            int rw = 0; try { rw = model.Extension.GetWhatsWrongCount(); } catch { }

            mo["hasForgeHelix"] = hasForge;
            mo["helixType"] = helixType;
            mo["rebuildErrors"] = rw;
            return mo;
        }
    }
}
