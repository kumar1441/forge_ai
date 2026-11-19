using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT dimension-name check — shares NO code with RenameDimension. Its own tree walk collecting every
        // dimension's full name, so the harness can prove a dimension named "<newName>@..." now exists.
        public static JObject MeasureRenameDimension(IModelDoc2 model)
        {
            var res = new JObject();
            var names = new JArray();
            int count = 0;
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    var dd = f.GetFirstDisplayDimension() as DisplayDimension;
                    while (dd != null)
                    {
                        var d = dd.GetDimension2(0) as Dimension;
                        if (d != null) { string n = null; try { n = d.FullName; } catch { } names.Add(n ?? ""); count++; }
                        dd = f.GetNextDisplayDimension(dd) as DisplayDimension;
                    }
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch { }
            res["count"] = count;
            res["names"] = names;
            return res;
        }
    }
}
