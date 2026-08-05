using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for add_sweep (tool 119). Own feature walk: detects the Forge-Sweep feature
    /// (run0=absent -> run1=present -> run2=present proves write + idempotency) and independently counts SOLID bodies
    /// + sums their volume (known truth: bodies rise by 1 and the new-body volume = pi*0.005^2*0.05 = ~3927 mm3 for a
    /// 10mm-dia circle swept 50mm). Plus a rebuild-clean read. Read-only.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureAddSweep(IModelDoc2 model)
        {
            var mo = new JObject();
            if (model == null) { mo["error"] = "no model"; return mo; }
            var part = model as PartDoc;

            bool hasForge = false;
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    string nm = null; try { nm = f.Name; } catch { }
                    if (nm != null && nm.StartsWith("Forge-Sweep", StringComparison.OrdinalIgnoreCase)) { hasForge = true; break; }
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

            mo["hasForgeSweep"] = hasForge;
            mo["solidBodies"] = bodies;
            mo["totalVolumeMm3"] = Math.Round(volMm3, 2);
            mo["rebuildErrors"] = rw;
            return mo;
        }
    }
}
