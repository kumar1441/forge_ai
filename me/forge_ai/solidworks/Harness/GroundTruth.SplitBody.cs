using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT check for split_body — re-derives the solid body count directly off PartDoc.GetBodies2, not
        // from the handler's own BodyCountBefore/After report.
        public static JObject MeasureSplitBody(IModelDoc2 model)
        {
            var res = new JObject();
            int count = 0;
            try
            {
                var pd = model as PartDoc;
                var bodies = pd?.GetBodies2((int)swBodyType_e.swSolidBody, true) as object[];
                count = bodies?.Length ?? 0;
            }
            catch { }
            res["solidBodyCount"] = count;
            return res;
        }
    }
}
