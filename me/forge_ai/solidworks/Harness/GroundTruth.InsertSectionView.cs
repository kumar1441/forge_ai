using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT for insert_section_view (tool 104). Shares no code with the handler: a fresh
        // GetFirstView()/GetNextView() walk (the handler ALSO walks this way for its own idempotency check, but
        // this is a completely separate traversal call — no shared object, no cached list) counting how many
        // views on the sheet report Type == swDrawingSectionView. 0 before insert, 1 after, still 1 on an
        // idempotent rerun.
        public static JObject MeasureInsertSectionView(IModelDoc2 model)
        {
            var res = new JObject();
            var dd = model as DrawingDoc;
            if (dd == null) { res["sectionViewCount"] = 0; return res; }

            int count = 0;
            try
            {
                var v = dd.GetFirstView() as IView;
                while (v != null)
                {
                    int t = -1; try { t = v.Type; } catch { }
                    if (t == (int)swDrawingViewTypes_e.swDrawingSectionView) count++;
                    v = v.GetNextView() as IView;
                }
            }
            catch { }

            res["sectionViewCount"] = count;
            return res;
        }
    }
}
