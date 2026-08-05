using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT check for insert_view: re-reads the active drawing's own view list directly (dd.GetViews(),
        // not the handler's own Before/After report) and, for each view, its raw Name + GetOrientationName() +
        // ScaleDecimal — a completely different path than the handler's own EnumerateViews/target resolution.
        // Same shape as GroundTruth.SetViewScale's measurer, kept independent per this family's convention.
        public static JObject MeasureInsertView(ISldWorks app)
        {
            var res = new JObject();
            IModelDoc2 active = null;
            try { active = app.IActiveDoc2 as IModelDoc2; } catch { }
            if (active == null) { res["isDrawing"] = false; return res; }

            bool isDrawing = false;
            try { isDrawing = (int)active.GetType() == (int)swDocumentTypes_e.swDocDRAWING; } catch { }
            res["isDrawing"] = isDrawing;
            if (!isDrawing) return res;

            var dd = active as DrawingDoc;
            var rows = new JArray();
            if (dd != null)
            {
                object[] perSheet = null;
                try { perSheet = dd.GetViews() as object[]; } catch { }
                if (perSheet != null)
                {
                    foreach (var so in perSheet)
                    {
                        var group = so as object[];
                        if (group == null) continue;
                        for (int k = 1; k < group.Length; k++)
                        {
                            var v = group[k] as IView;
                            if (v == null) continue;
                            string name = null; try { name = v.Name; } catch { }
                            string orientation = null; try { orientation = v.GetOrientationName(); } catch { }
                            double scale = 0; try { scale = v.ScaleDecimal; } catch { }
                            rows.Add(new JObject { ["name"] = name, ["orientation"] = orientation, ["scale"] = scale });
                        }
                    }
                }
            }
            res["viewCount"] = rows.Count;
            res["views"] = rows;
            return res;
        }
    }
}
