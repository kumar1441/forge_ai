using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for add_dome (tool 121). Own feature walk: detects the Forge-Dome feature
    /// (run0=absent -> run1=present -> run2=present proves write + idempotency) and independently sums SOLID-body
    /// volume — a dome adds material, so run1 volume must exceed run0 (measured by a path the handler never touched).
    /// Records the dome's type name for learning. Read-only.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureAddDome(IModelDoc2 model)
        {
            var mo = new JObject();
            if (model == null) { mo["error"] = "no model"; return mo; }
            var part = model as PartDoc;

            bool hasForge = false; string domeType = null;
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    string nm = null; try { nm = f.Name; } catch { }
                    if (nm != null && nm.StartsWith("Forge-Dome", StringComparison.OrdinalIgnoreCase))
                    {
                        hasForge = true;
                        try { domeType = f.GetTypeName2(); } catch { }
                        break;
                    }
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch { }

            int bodies = 0; double volMm3 = 0;
            try
            {
                var bs = part == null ? null : part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[];
                if (bs != null)
                {
                    bodies = bs.Length;
                    foreach (var o in bs)
                    {
                        var b = o as Body2; if (b == null) continue;
                        var mp = b.GetMassProperties(0) as double[];
                        if (mp != null && mp.Length >= 4) volMm3 += mp[3] * 1e9;
                    }
                }
            }
            catch { }

            int rw = 0; try { rw = model.Extension.GetWhatsWrongCount(); } catch { }

            mo["hasForgeDome"] = hasForge;
            mo["domeType"] = domeType;
            mo["solidBodies"] = bodies;
            mo["totalVolumeMm3"] = Math.Round(volMm3, 2);
            mo["rebuildErrors"] = rw;
            return mo;
        }
    }
}
