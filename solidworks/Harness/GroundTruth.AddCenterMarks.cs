using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT check for add_center_marks — its OWN GetFirstView/GetNextView walk (a second, separately-
        // written loop), not a reuse of the handler's own before/after count.
        public static JObject MeasureAddCenterMarks(IModelDoc2 model)
        {
            var res = new JObject();
            int total = 0, views = 0;
            try
            {
                var dd = model as DrawingDoc;
                var v = dd?.GetFirstView() as IView;
                v = v?.GetNextView() as IView;
                while (v != null)
                {
                    views++;
                    try { total += v.GetCenterMarkCount(); } catch { }
                    v = v.GetNextView() as IView;
                }
            }
            catch { }
            res["viewCount"] = views;
            res["totalCenterMarks"] = total;
            return res;
        }
    }
}
