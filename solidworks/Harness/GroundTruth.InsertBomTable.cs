using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT for insert_bom_table (tool 113). Shares no code with the handler: rather than trusting
        // IView.GetBomTable() (found LIVE to be a dead accessor on this build — it never reflects a just-inserted
        // table, not even after a rebuild+save+fresh-view re-fetch), this counts "BomFeat" entries in the
        // document's FEATURE TREE (FirstFeature()/GetNextFeature(), a completely different API surface than the
        // handler's own View-based InsertBomTable2 return) — confirmed live as the one signal that actually moves:
        // 0 before insert, 1 after, would go to 2 on a buggy non-idempotent rerun (caught a real bug this way:
        // the handler's first cut used GetBomTable() for its own AlreadyDone check too, so it never detected an
        // existing table and stacked a second BomFeat on every rerun before this was switched to a feature count).
        public static JObject MeasureInsertBomTable(IModelDoc2 model)
        {
            var res = new JObject();
            var dd = model as DrawingDoc;
            if (dd == null) { res["hasBomTable"] = false; res["bomFeatureCount"] = 0; return res; }

            int count = 0;
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    string tn = null; try { tn = f.GetTypeName2(); } catch { }
                    if (tn == "BomFeat") count++;
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch { }

            res["hasBomTable"] = count > 0;
            res["bomFeatureCount"] = count;
            return res;
        }
    }
}
