using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT check for insert_standard_views: re-reads the active drawing's own view list directly
        // (dd.GetViews(), not the handler's Inserted/ViewsInserted report) and counts how many of those views
        // reference the expected source model — a completely different path than the handler's own CountViews.
        public static JObject MeasureInsertStandardViews(ISldWorks app, string expectedSourcePath)
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
            int viewCount = 0, matchingSourceCount = 0;
            var names = new List<string>();
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
                            try { names.Add(v.Name); } catch { }
                            string refModel = null;
                            try { refModel = v.GetReferencedModelName(); } catch { }
                            if (!string.IsNullOrEmpty(refModel) && !string.IsNullOrEmpty(expectedSourcePath) &&
                                refModel.IndexOf(System.IO.Path.GetFileNameWithoutExtension(expectedSourcePath), StringComparison.OrdinalIgnoreCase) >= 0)
                                matchingSourceCount++;
                        }
                    }
                }
            }
            res["viewCount"] = viewCount;
            res["matchingSourceCount"] = matchingSourceCount;
            res["viewNames"] = new JArray(names);
            return res;
        }
    }
}
