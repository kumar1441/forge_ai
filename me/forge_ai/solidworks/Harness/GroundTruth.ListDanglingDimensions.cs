using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT check for list_dangling_dimensions: re-reads the active drawing's own view list directly
        // (dd.GetViews(), not the handler's own Dangling report) and, for each DisplayDimension, its own
        // IAnnotation.IsDangling() + Dimension.FullName — the same primitives the handler uses, but read fresh
        // here rather than trusted from the handler's own walk (a different code path can't share the same bug).
        public static JObject MeasureListDanglingDimensions(ISldWorks app)
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
            int total = 0, dangling = 0;
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
                            object[] dims = null;
                            try { dims = v.GetDisplayDimensions() as object[]; } catch { }
                            if (dims == null) continue;
                            foreach (var o in dims)
                            {
                                var ddim = o as DisplayDimension;
                                if (ddim == null) continue;
                                total++;
                                bool d = false;
                                try { var ann = ddim.GetAnnotation() as IAnnotation; if (ann != null) d = ann.IsDangling(); } catch { }
                                if (!d) continue;
                                dangling++;
                                string name = null;
                                try { var dim = ddim.GetDimension2(0) as Dimension; if (dim != null) name = dim.FullName; } catch { }
                                rows.Add(new JObject { ["name"] = name });
                            }
                        }
                    }
                }
            }
            res["totalDimensions"] = total;
            res["danglingCount"] = dangling;
            res["dangling"] = rows;
            return res;
        }
    }
}
