using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT for insert_detail_view (tool 105). A fresh GetFirstView()/GetNextView() walk (its own
        // traversal call, no shared object with the handler's) counting how many views on the sheet report
        // Type == swDrawingDetailView. 0 before insert, 1 after, still 1 on an idempotent rerun.
        public static JObject MeasureInsertDetailView(IModelDoc2 model)
        {
            var res = new JObject();
            var dd = model as DrawingDoc;
            if (dd == null) { res["detailViewCount"] = 0; return res; }

            int count = 0;
            try
            {
                var v = dd.GetFirstView() as IView;
                while (v != null)
                {
                    int t = -1; try { t = v.Type; } catch { }
                    if (t == (int)swDrawingViewTypes_e.swDrawingDetailView) count++;
                    v = v.GetNextView() as IView;
                }
            }
            catch { }

            res["detailViewCount"] = count;
            return res;
        }
    }
}
