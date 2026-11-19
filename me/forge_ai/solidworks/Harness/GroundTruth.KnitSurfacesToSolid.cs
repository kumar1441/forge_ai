using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT sheet/solid body census for knit_surfaces_to_solid (tool 181). Shares NO code with
        // KnitSurfacesToSolid — its own GetBodies2 walk and its own volume sum, never the handler's counts.
        public static JObject MeasureKnitSurfacesToSolid(IModelDoc2 model)
        {
            var res = new JObject();
            var part = model as PartDoc;
            if (part == null) { res["error"] = "not a part"; return res; }

            int sheetCount = 0, solidCount = 0;
            double volMm3 = 0;
            try
            {
                var sheets = part.GetBodies2((int)swBodyType_e.swSheetBody, false) as object[];
                sheetCount = sheets == null ? 0 : sheets.Length;
            }
            catch { }
            try
            {
                var solids = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[];
                solidCount = solids == null ? 0 : solids.Length;
                foreach (var o in solids ?? new object[0])
                {
                    var b = o as Body2; if (b == null) continue;
                    var mp = b.GetMassProperties(0) as double[];
                    if (mp != null && mp.Length >= 4) volMm3 += mp[3] * 1e9;
                }
            }
            catch { }

            res["sheetBodies"] = sheetCount;
            res["solidBodies"] = solidCount;
            res["solidVolumeMm3"] = Math.Round(volMm3, 2);
            return res;
        }
    }
}
