using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT sheet-body census for fill_surface (tool 226). Shares NO code with FillSurface's own
        // open-rim/cross-body-coincidence detection — its own GetBodies2 walk and its own summed surface area
        // (Body2.GetMassProperties()[3] for a SHEET body — see the mp[3] comment below), never the handler's
        // own rim/edge bookkeeping. A genuine patch must both raise the sheet-body count AND raise the total
        // surface area by roughly the patched opening's own area — either alone (e.g. a zero-area sliver body)
        // would be a false green.
        public static JObject MeasureFillSurface(IModelDoc2 model)
        {
            var res = new JObject();
            var part = model as PartDoc;
            if (part == null) { res["error"] = "not a part"; return res; }

            int sheetCount = 0;
            double areaMm2 = 0;
            try
            {
                var sheets = part.GetBodies2((int)swBodyType_e.swSheetBody, false) as object[];
                sheetCount = sheets == null ? 0 : sheets.Length;
                foreach (var o in sheets ?? new object[0])
                {
                    var b = o as Body2; if (b == null) continue;
                    var mp = b.GetMassProperties(0) as double[];
                    // For a SHEET (non-solid) body, index [3] is surface area (SI m^2) — NOT volume/[4] as the
                    // solid-body indexing in landmines.md's compare_bodies note assumes. Confirmed live 2026-07-31
                    // via raw-array dump: mp[3] landed exactly on the fixture's known 3500mm2 (tube)/1200mm2 (cap)
                    // areas; mp[4] instead turned out to be the body's total naked-edge/boundary length (140mm/
                    // 280mm — perimeter, not area), and mp[5] duplicates mp[3].
                    if (mp != null && mp.Length >= 4) areaMm2 += mp[3] * 1e6;
                }
            }
            catch { }

            res["sheetBodies"] = sheetCount;
            res["totalSurfaceAreaMm2"] = Math.Round(areaMm2, 2);
            return res;
        }
    }
}
