using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT check for import_model_dimensions: re-reads the active drawing's own view list directly
        // (dd.GetViews()) and, for each view, its own GetDisplayDimensions() count — a completely different path
        // than the handler's own CountDisplayDimensions (same shape, kept as an independent copy per this
        // family's convention so a bug in one can't hide behind the other's GT).
        public static JObject MeasureImportModelDimensions(ISldWorks app)
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
            int viewCount = 0, totalDims = 0;
            var perView = new JArray();
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
                            viewCount++;
                            string name = null; try { name = v.Name; } catch { }
                            int dimCount = 0;
                            object[] dims = null;
                            try { dims = v.GetDisplayDimensions() as object[]; } catch { }
                            if (dims != null) dimCount = dims.Length;
                            totalDims += dimCount;
                            perView.Add(new JObject { ["name"] = name, ["dimensionCount"] = dimCount });
                        }
                    }
                }
            }
            res["viewCount"] = viewCount;
            res["totalDimensions"] = totalDims;
            res["views"] = perView;
            return res;
        }
    }
}
