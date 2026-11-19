using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT density read — shares NO code with GetMaterialDensity. The handler computes mass/volume; this GT
        // reads IMassProperty.Density directly (a different code path). They must agree, and the value must be a
        // physically plausible solid density.
        public static JObject MeasureGetMaterialDensity(IModelDoc2 model)
        {
            var res = new JObject();
            double density = -1;
            if (model != null)
            {
                try { var mp = model.Extension.CreateMassProperty(); if (mp != null) density = mp.Density; } catch { }
            }
            res["densityKgM3"] = density;
            return res;
        }
    }
}
