using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT reference-geometry count — shares NO code with GetRefGeometry. Own traversal by feature type.
        public static JObject MeasureGetRefGeometry(IModelDoc2 model)
        {
            var res = new JObject();
            if (model == null) { res["planes"] = -1; return res; }
            int planes = 0, axes = 0, points = 0, coord = 0;
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    string tn = null; try { tn = f.GetTypeName2(); } catch { }
                    if (tn == "RefPlane") planes++;
                    else if (tn == "RefAxis") axes++;
                    else if (tn == "RefPoint") points++;
                    else if (tn == "CoordSys") coord++;
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch { }
            res["planes"] = planes; res["axes"] = axes; res["points"] = points; res["coordSystems"] = coord;
            return res;
        }
    }
}
