using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT tree order for reorder_feature. Deliberately RAW: the ORDERED list of every feature Name the tree
        // walk yields (consumed sketches and scaffold folders included), plus the rebuild-error count. The harness knows
        // nothing about which features the handler targeted from this blob alone — it looks up the configured
        // move/target names in this ordered list and compares their INDICES across run0/run1/run2. So the "did the order
        // actually change" verdict is decided by a second tree walk that shares no code with the handler's own re-read.
        public static JObject MeasureReorderFeature(IModelDoc2 model)
        {
            var res = new JObject();
            var names = new JArray();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { res["names"] = names; res["count"] = 0; res["rebuildErrors"] = 0; return res; }
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    string nm = null; try { nm = f.Name; } catch { }
                    if (!string.IsNullOrEmpty(nm)) names.Add(nm);
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch { }
            res["names"] = names;
            res["count"] = names.Count;
            int err = 0; try { err = model.Extension.GetWhatsWrongCount(); } catch { }
            res["rebuildErrors"] = err;
            return res;
        }
    }
}
