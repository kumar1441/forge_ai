using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT overlap census for arrange_drawing_annotations (tool 190). Shares NO code with
        // ArrangeDrawingAnnotations — its own view/dimension walk and its own proximity check.
        public static JObject MeasureArrangeDrawingAnnotations(IModelDoc2 model)
        {
            var res = new JObject();
            var dd = model as DrawingDoc;
            if (dd == null) { res["error"] = "not a drawing"; return res; }

            var positions = new List<double[]>();
            int total = 0;
            var view = dd.GetFirstView() as IView;
            while (view != null)
            {
                object[] dims = null;
                try { dims = view.GetDisplayDimensions() as object[]; } catch { }
                if (dims != null)
                    foreach (var o in dims)
                    {
                        var ddim = o as DisplayDimension; if (ddim == null) continue;
                        total++;
                        IAnnotation ann = null; try { ann = ddim.GetAnnotation() as IAnnotation; } catch { }
                        double[] p = null; try { p = ann == null ? null : ann.GetPosition() as double[]; } catch { }
                        if (p != null && p.Length >= 2) positions.Add(p);
                    }
                view = view.GetNextView() as IView;
            }

            // X/Y ONLY — SetPosition's Z argument is a confirmed no-op on this build (a dimension's Z stays
            // pinned to its owning view; only X/Y actually move), and annotation position is fundamentally a
            // 2D sheet-space concept anyway.
            const double thresh = 0.001;
            int overlaps = 0;
            for (int i = 0; i < positions.Count; i++)
                for (int j = i + 1; j < positions.Count; j++)
                {
                    double dx = positions[i][0] - positions[j][0], dy = positions[i][1] - positions[j][1];
                    if (Math.Sqrt(dx * dx + dy * dy) < thresh) overlaps++;
                }

            res["totalDimensions"] = total;
            res["overlappingPairs"] = overlaps;
            return res;
        }
    }
}
