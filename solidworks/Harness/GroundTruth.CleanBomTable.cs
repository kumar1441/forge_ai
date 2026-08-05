using System;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT for clean_bom_table (tool 161). Shares no code with the handler beyond the unavoidable
        // "BomFeat" feature-tree lookup (the same signal InsertBomTable.cs's own idempotency check and this
        // handler both rely on, since IView.GetBomTable() is a confirmed-dead accessor on this build) — from
        // there this does its OWN GetSpecificFeature2()/IBomFeature/IGetTableAnnotations walk and its OWN
        // column-title scan + get_Text row read, rather than reusing CleanBomTable.cs's helpers.
        public static JObject MeasureCleanBomTable(IModelDoc2 model)
        {
            var res = new JObject();
            res["hasBomTable"] = false;
            res["rowCount"] = 0;
            res["orphanedRows"] = 0;
            res["sortColumn"] = null;
            res["sortAscending"] = true;
            res["isSortedAsExpected"] = true;

            Feature bomFeat = null;
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    string tn = null; try { tn = f.GetTypeName2(); } catch { }
                    if (tn == "BomFeat") { bomFeat = f; break; }
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch { }
            if (bomFeat == null) return res;

            var ibf = bomFeat.GetSpecificFeature2() as IBomFeature;
            if (ibf == null) return res;
            int tc = 0; try { tc = ibf.GetTableAnnotationCount(); } catch { }
            if (tc <= 0) return res;
            var bta = ibf.IGetTableAnnotations(1) as BomTableAnnotation;
            var ita = bta as ITableAnnotation;
            if (ita == null) return res;

            res["hasBomTable"] = true;
            int rows = 0; try { rows = ita.RowCount; } catch { }
            res["rowCount"] = rows;

            int cols = 0; try { cols = ita.ColumnCount; } catch { }

            int orphaned = 0;
            for (int r = 1; r < rows; r++)
            {
                int cc = -1; try { cc = bta.GetComponentsCount(r); } catch { }
                if (cc == 0) orphaned++;
            }
            res["orphanedRows"] = orphaned;

            // pick the same column preference the handler uses: QTY/QUANTITY descending, else column 0 ascending
            int sortCol = -1; string sortTitle = null;
            for (int c = 0; c < cols; c++)
            {
                string title = null; try { title = ita.GetColumnTitle(c); } catch { }
                if (string.IsNullOrEmpty(title)) continue;
                string t = title.ToUpperInvariant();
                if (t.Contains("QTY") || t.Contains("QUANTITY")) { sortCol = c; sortTitle = title; break; }
            }
            bool ascending = sortCol < 0;
            if (sortCol < 0 && cols > 0) { sortCol = 0; try { sortTitle = ita.GetColumnTitle(0); } catch { } }
            res["sortColumn"] = sortTitle;
            res["sortAscending"] = ascending;

            bool ok = true;
            if (sortCol >= 0 && rows > 2)
            {
                double? prev = null;
                for (int r = 1; r < rows; r++)
                {
                    string txt = null; try { txt = ita.get_Text(r, sortCol); } catch { }
                    var m = Regex.Match(txt ?? "", @"[-+]?[0-9]*\.?[0-9]+");
                    if (!m.Success) continue;
                    double v = double.Parse(m.Value);
                    if (prev.HasValue)
                    {
                        if (ascending && v < prev.Value - 1e-6) { ok = false; break; }
                        if (!ascending && v > prev.Value + 1e-6) { ok = false; break; }
                    }
                    prev = v;
                }
            }
            res["isSortedAsExpected"] = ok;
            return res;
        }
    }
}
