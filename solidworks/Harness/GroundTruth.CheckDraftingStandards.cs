using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        private static readonly double[] CdsStandardRatios = { 1.0, 0.5, 0.2, 0.1, 0.05, 0.02, 0.01, 2.0, 5.0, 10.0 };
        private static readonly string[] CdsRequiredFields = { "Description", "Material", "Revision" };

        // INDEPENDENT check for check_drafting_standards: its own GetViews() walk (not the handler's own report)
        // re-counting dangling dims + no-tolerance dims, its own get_CustomPropertyManager("") read of the same
        // required-field set, and its own per-view GetIScaleRatio() re-check against the standard-ratio table.
        public static JObject MeasureCheckDraftingStandards(IModelDoc2 model)
        {
            var res = new JObject();
            var dd = model as DrawingDoc;
            if (dd == null) { res["isDrawing"] = false; return res; }
            res["isDrawing"] = true;

            int total = 0, dangling = 0, noTol = 0;
            object[] perSheet = null;
            try { perSheet = dd.GetViews() as object[]; } catch { }
            var oddScales = new JArray();
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
                        if (dims != null)
                        {
                            foreach (var o in dims)
                            {
                                var ddim = o as DisplayDimension;
                                if (ddim == null) continue;
                                total++;
                                bool isDangling = false;
                                try { var ann = ddim.GetAnnotation() as IAnnotation; if (ann != null) isDangling = ann.IsDangling(); } catch { }
                                if (isDangling) { dangling++; continue; }
                                try
                                {
                                    var d = ddim.GetDimension2(0) as Dimension;
                                    int tol = (int)swTolType_e.swTolNONE;
                                    if (d != null) tol = d.GetToleranceType();
                                    if (tol == (int)swTolType_e.swTolNONE) noTol++;
                                }
                                catch { }
                            }
                        }
                    }
                    for (int k = 0; k < group.Length; k++)
                    {
                        var v = group[k] as IView;
                        if (v == null) continue;
                        double ratio = 0; try { ratio = v.get_IScaleRatio(); } catch { }
                        if (ratio <= 0) continue;
                        bool standard = CdsStandardRatios.Any(sr => Math.Abs(sr - ratio) < 0.001);
                        if (!standard) oddScales.Add(ratio);
                    }
                }
            }
            res["totalDimensions"] = total;
            res["danglingCount"] = dangling;
            res["noToleranceCount"] = noTol;
            res["nonStandardScaleCount"] = oddScales.Count;

            CustomPropertyManager cpm = null;
            try { cpm = model.Extension.get_CustomPropertyManager(""); } catch { }
            var empty = new JArray();
            foreach (var field in CdsRequiredFields)
            {
                string val = null, resolved = null;
                try { cpm?.Get4(field, false, out val, out resolved); } catch { }
                if (string.IsNullOrWhiteSpace(resolved) && string.IsNullOrWhiteSpace(val)) empty.Add(field);
            }
            res["emptyFieldCount"] = empty.Count;
            res["emptyFields"] = empty;
            res["releaseReady"] = dangling == 0 && empty.Count == 0 && oddScales.Count == 0;
            return res;
        }
    }
}
