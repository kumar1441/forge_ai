using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT sketch inventory — shares NO code with GetSketches. Its own recursive tree walk counting
        // ISketch-bearing features + their constrained state.
        public static JObject MeasureGetSketches(IModelDoc2 model)
        {
            var res = new JObject();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART) { res["count"] = -1; return res; }
            int count = 0, under = 0;
            try { var f = model.FirstFeature() as Feature; while (f != null) { Walk(f, ref count, ref under); f = f.GetNextFeature() as Feature; } } catch { }
            res["count"] = count;
            res["underDefined"] = under;
            return res;
        }

        private static void Walk(Feature f, ref int count, ref int under)
        {
            if (f == null) return;
            Sketch sk = null; try { sk = f.GetSpecificFeature2() as Sketch; } catch { }
            if (sk != null)
            {
                count++;
                bool full = false; try { full = sk.GetConstrainedStatus() == (int)swConstrainedStatus_e.swFullyConstrained; } catch { }
                if (!full) under++;
            }
            var sub = f.GetFirstSubFeature() as Feature;
            while (sub != null) { Walk(sub, ref count, ref under); sub = sub.GetNextSubFeature() as Feature; }
        }
    }
}
