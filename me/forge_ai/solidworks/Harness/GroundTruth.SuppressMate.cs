using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT suppressed-mate count — shares NO code with SuppressMate. Its own Mates-folder traversal counting
        // total mates + suppressed, so the harness proves (run0 vs run1) the suppressed count changed by exactly ±1
        // and the total mate count is unchanged (a suppress must not delete the mate).
        public static JObject MeasureSuppressMate(IModelDoc2 model)
        {
            var res = new JObject();
            if (model as AssemblyDoc == null) { res["error"] = "not an assembly"; return res; }
            int total = 0, suppressed = 0;
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    string tn = ""; try { tn = f.GetTypeName2(); } catch { }
                    if (tn == "MateGroup")
                    {
                        var s = f.GetFirstSubFeature() as Feature;
                        while (s != null)
                        {
                            total++;
                            bool sup = false; try { sup = s.IsSuppressed(); } catch { }
                            if (sup) suppressed++;
                            s = s.GetNextSubFeature() as Feature;
                        }
                    }
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch { }
            res["totalMates"] = total;
            res["suppressedMates"] = suppressed;
            return res;
        }
    }
}
