using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT body count — shares NO code with GetBodies. Own GetBodies2 read per type. Seeded block = 1 solid.
        public static JObject MeasureGetBodies(IModelDoc2 model)
        {
            var res = new JObject();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART) { res["solid"] = -1; return res; }
            int solid = 0, surface = 0;
            var part = model as PartDoc;
            try { var s = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]; solid = s?.Length ?? 0; } catch { }
            try { var f = part.GetBodies2((int)swBodyType_e.swSheetBody, false) as object[]; surface = f?.Length ?? 0; } catch { }
            res["solid"] = solid;
            res["surface"] = surface;
            return res;
        }
    }
}
