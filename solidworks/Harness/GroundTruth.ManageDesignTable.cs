using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT verification for manage_design_table (tool 194). Never re-reads the table's own cells for
        // the pass/fail verdict — activates EVERY live configuration, rebuilds, and reads D1@Boss-Extrude1's
        // SystemValue straight off the feature tree (the same activate+rebuild+SystemValue idiom
        // GroundTruth.ConfigSpecificDimension already proves live), so an edit that silently didn't propagate to
        // the actual solid can never pass by matching the handler's own SetEntryText echo.
        public static JObject MeasureManageDesignTable(IModelDoc2 model)
        {
            var res = new JObject();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART) { res["configs"] = new JArray(); return res; }

            bool hasTable = false; try { hasTable = model.Extension.HasDesignTable(); } catch { }
            res["hasTable"] = hasTable;

            if (hasTable)
            {
                var dt = model.IGetDesignTable();
                if (dt != null)
                {
                    try { dt.Attach(); } catch { }
                    try { res["linkToFile"] = dt.LinkToFile; } catch { }
                    try { res["fileName"] = dt.FileName; } catch { }
                    try { res["rowCount"] = dt.GetRowCount(); } catch { }
                    try { res["colCount"] = dt.GetColumnCount(); } catch { }
                    try { dt.Detach(); } catch { }
                }
            }

            string[] names = null; try { names = model.GetConfigurationNames() as string[]; } catch { }
            names = names ?? new string[0];
            var cfgArr = new JArray(); foreach (var n in names) cfgArr.Add(n);
            res["configs"] = cfgArr;

            string origActive = null; try { origActive = model.ConfigurationManager?.ActiveConfiguration?.Name; } catch { }
            var perConfig = new JObject();
            foreach (var n in names)
            {
                try { model.ShowConfiguration2(n); model.ForceRebuild3(false); } catch { }
                double mm = -1;
                try
                {
                    var feat = model.FirstFeature() as Feature;
                    while (feat != null && mm < 0)
                    {
                        string fn = null; try { fn = feat.Name; } catch { }
                        if (fn == "Boss-Extrude1")
                        {
                            var dd = feat.GetFirstDisplayDimension() as DisplayDimension;
                            if (dd != null) { var d = dd.GetDimension2(0) as Dimension; if (d != null) { try { mm = d.SystemValue * 1000.0; } catch { } } }
                        }
                        feat = feat.GetNextFeature() as Feature;
                    }
                }
                catch { }
                perConfig[n] = Math.Round(mm, 4);
            }
            try { if (!string.IsNullOrEmpty(origActive)) { model.ShowConfiguration2(origActive); model.ForceRebuild3(false); } } catch { }
            res["perConfigDepthMm"] = perConfig;
            return res;
        }
    }
}
