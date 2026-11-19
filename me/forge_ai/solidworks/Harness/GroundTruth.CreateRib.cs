using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for create_rib (tool 207). Own feature walk for the Forge-Rib feature and its OWN mass
    /// read (never the handler's numbers): run0=absent + volume V0, run1=present + volume V1>V0 (a rib ADDS material),
    /// run2=present + V1 (idempotent no-op). A rib is bounded added material, so the honest signals are feature
    /// presence, a volume INCREASE, and a clean rebuild. Read-only.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureCreateRib(IModelDoc2 model)
        {
            var mo = new JObject();
            if (model == null) { mo["error"] = "no model"; return mo; }

            bool hasForge = false; string ribType = null;
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    string nm = null; try { nm = f.Name; } catch { }
                    if (nm != null && nm.Equals("Forge-Rib", StringComparison.OrdinalIgnoreCase))
                    {
                        hasForge = true;
                        try { ribType = f.GetTypeName2(); } catch { }
                        break;
                    }
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch { }

            double vol = -1;
            try { var mp = model.Extension.CreateMassProperty(); if (mp != null) vol = mp.Volume * 1e9; } catch { }
            int rw = 0; try { rw = model.Extension.GetWhatsWrongCount(); } catch { }

            mo["hasForgeRib"] = hasForge;
            mo["ribType"] = ribType;
            mo["volumeMm3"] = vol;
            mo["rebuildErrors"] = rw;
            return mo;
        }
    }
}
