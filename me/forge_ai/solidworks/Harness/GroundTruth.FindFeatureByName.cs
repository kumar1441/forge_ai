using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT feature-name roster. Deliberately does NO matching of its own: it publishes the raw tree names
        // and lets the harness do the matching in PowerShell — a THIRD implementation, in a different language, of
        // "which of these names is 'seed hole'". If the GT normalised the names itself it would just be the handler's
        // heuristic written twice, and the two could agree on a wrong answer (exactly how the ICE type-name bug hid).
        public static JObject MeasureFindFeatureByName(IModelDoc2 model)
        {
            var res = new JObject();
            var names = new JArray();
            if (model == null) { res["names"] = names; res["count"] = 0; return res; }
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    string tn = null; try { tn = f.GetTypeName2(); } catch { }
                    if (!string.IsNullOrEmpty(tn) && !IsScaffold(tn))
                    {
                        string n = null; try { n = f.Name; } catch { }
                        if (!string.IsNullOrEmpty(n)) names.Add(n);
                    }
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch { }
            res["names"] = names;
            res["count"] = names.Count;
            return res;
        }
    }
}
