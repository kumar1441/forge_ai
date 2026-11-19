using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for get_cut_list (tool 165) — shares NO code with GetCutList.cs. The handler groups
    /// bodies by (volume, surface area, sorted extents). This GT groups by the sorted bounding-box EXTENTS ALONE (a
    /// different, coarser key), so the two must still agree on the unique-shape count / quantities or a grouping bug is
    /// exposed. Known truth: multibody-block -> 4 bodies, 2 unique shapes (quantities 2 & 2); props-block -> 1 body,
    /// 1 unique. Read-only.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureGetCutList(IModelDoc2 model)
        {
            var d = new JObject();
            if (!(model is PartDoc)) { d["applicable"] = false; d["reason"] = "not a part"; return d; }
            d["applicable"] = true;

            object[] bodies = null;
            try { bodies = ((PartDoc)model).GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]; } catch { }
            var list = (bodies ?? new object[0]).OfType<Body2>().ToList();

            var groups = new Dictionary<string, int>();
            foreach (var b in list)
            {
                double dx = 0, dy = 0, dz = 0;
                try
                {
                    var box = b.GetBodyBox() as double[];
                    if (box != null && box.Length >= 6)
                    {
                        dx = Math.Abs(box[3] - box[0]) * 1000; dy = Math.Abs(box[4] - box[1]) * 1000; dz = Math.Abs(box[5] - box[2]) * 1000;
                    }
                }
                catch { }
                var ext = new[] { dx, dy, dz }.OrderBy(v => v).Select(v => Math.Round(v, 1)).ToArray();
                string key = ext[0] + "|" + ext[1] + "|" + ext[2];
                groups[key] = groups.TryGetValue(key, out var n) ? n + 1 : 1;
            }

            d["totalBodies"] = list.Count;
            d["uniqueGroups"] = groups.Count;
            var quantities = new JArray(groups.Values.OrderByDescending(q => q).Select(q => (JToken)q));
            d["quantities"] = quantities;
            d["maxQuantity"] = groups.Count == 0 ? 0 : groups.Values.Max();
            return d;
        }
    }
}
