using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT for add_drawing_dimension (tool 109). Shares no code with the handler's own marker-name
        // lookup: this uses IDrawingDoc.GetViews() (a per-sheet array-of-arrays traversal, NOT the handler's
        // GetFirstView()/GetNextView() walk) and simply totals every view's GetDisplayDimensions() count — a
        // DIFFERENT signal (aggregate count vs a specific name) than what the handler checks for its own
        // idempotency. Baseline is whatever the fixture already carries (imported model dims); after the handler
        // runs the total must be exactly baseline+1, and stay there on an idempotent rerun.
        public static JObject MeasureAddDrawingDimension(IModelDoc2 model)
        {
            var res = new JObject();
            var dd = model as DrawingDoc;
            if (dd == null) { res["totalDimensionCount"] = 0; return res; }

            int count = 0;
            try
            {
                var perSheet = dd.GetViews() as object[];
                if (perSheet != null)
                {
                    foreach (var so in perSheet)
                    {
                        var group = so as object[];
                        if (group == null) continue;
                        for (int k = 1; k < group.Length; k++)
                        {
                            var v = group[k] as IView; if (v == null) continue;
                            object[] dims = null;
                            try { dims = v.GetDisplayDimensions() as object[]; } catch { }
                            if (dims != null) count += dims.Length;
                        }
                    }
                }
            }
            catch { }

            res["totalDimensionCount"] = count;
            return res;
        }
    }
}
