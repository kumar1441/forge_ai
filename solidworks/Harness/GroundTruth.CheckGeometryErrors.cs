using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT check for check_geometry_errors — its OWN Body2.Diagnose() loop (a second, separately-written
        // call), not a reuse of the handler's own per-body gap count.
        public static JObject MeasureCheckGeometryErrors(IModelDoc2 model)
        {
            var res = new JObject();
            int bodyCount = 0, totalGaps = 0;
            try
            {
                var pd = model as PartDoc;
                var bodies = pd?.GetBodies2((int)swBodyType_e.swSolidBody, true) as object[];
                bodyCount = bodies?.Length ?? 0;
                if (bodies != null)
                {
                    foreach (var o in bodies)
                    {
                        var b = o as Body2;
                        if (b == null) continue;
                        var dr = b.Diagnose() as DiagnoseResult;
                        try { totalGaps += dr?.GetGapsCount() ?? 0; } catch { }
                    }
                }
            }
            catch { }
            res["solidBodyCount"] = bodyCount;
            res["totalGaps"] = totalGaps;
            return res;
        }
    }
}
