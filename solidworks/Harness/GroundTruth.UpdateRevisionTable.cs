using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT check for update_revision_table — re-resolves the sheet via GetSheetNames()/get_Sheet(name)
        // (a different lookup path than the handler's own GetCurrentSheet()) and re-reads the revision table's
        // row count + the label of every revision id via a fresh RevisionTable/TableAnnotation cast pair, sharing
        // no code with the handler's own AddRevision/read-back.
        public static JObject MeasureUpdateRevisionTable(IModelDoc2 model)
        {
            var res = new JObject();
            string sheetName = null;
            int rows = 0;
            var labels = new JArray();
            try
            {
                var dd = model as DrawingDoc;
                var names = dd?.GetSheetNames() as object[];
                if (names != null && names.Length > 0)
                {
                    sheetName = (string)names[0];
                    var sheet = dd.get_Sheet(sheetName) as ISheet;
                    object raw = sheet?.RevisionTable;
                    var revTable = raw as RevisionTableAnnotation;
                    var ta = raw as TableAnnotation;
                    try { rows = ta != null ? ta.RowCount : 0; } catch { }
                    if (revTable != null)
                    {
                        for (int r = 1; r < rows; r++)
                        {
                            int id = -1; try { id = revTable.GetIdForRowNumber(r); } catch { }
                            if (id < 0) continue;
                            string lbl = null; try { lbl = revTable.GetRevisionForId(id); } catch { }
                            labels.Add(lbl);
                        }
                    }
                }
            }
            catch { }
            res["sheetName"] = sheetName;
            res["rowCount"] = rows;
            res["revisionLabels"] = labels;
            return res;
        }
    }
}
