using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for create_extruded_surface (tool 222). Own feature walk for the Forge-ExtrudeSurface
    /// feature and its OWN surface-body count (IPartDoc.GetBodies2(swSheetBody) — surface bodies enumerate under
    /// swSheetBody on this build, not swSurfaceBody; the same landmine get_bodies/list_bodies already hit). Read-only.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureCreateExtrudedSurface(IModelDoc2 model)
        {
            var mo = new JObject();
            if (model == null) { mo["error"] = "no model"; return mo; }

            bool hasForge = false; string surfType = null;
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    string nm = null; try { nm = f.Name; } catch { }
                    if (nm != null && nm.Equals("Forge-ExtrudeSurface", StringComparison.OrdinalIgnoreCase))
                    {
                        hasForge = true;
                        try { surfType = f.GetTypeName2(); } catch { }
                        break;
                    }
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch { }

            int surfaceBodies = 0;
            try
            {
                var part = model as PartDoc;
                if (part != null) { var b = part.GetBodies2((int)swBodyType_e.swSheetBody, false) as object[]; surfaceBodies = b?.Length ?? 0; }
            }
            catch { }
            int rw = 0; try { rw = model.Extension.GetWhatsWrongCount(); } catch { }

            mo["hasForgeSurface"] = hasForge;
            mo["surfaceFeatureType"] = surfType;
            mo["surfaceBodies"] = surfaceBodies;
            mo["rebuildErrors"] = rw;
            return mo;
        }
    }
}
