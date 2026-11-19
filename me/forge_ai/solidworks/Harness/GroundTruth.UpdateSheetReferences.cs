using System;
using System.IO;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT for update_sheet_references (tool 114). Shares no code with the handler: walks the FLAT
        // GetFirstView()/GetNextView() linked list (the handler's own scan does too, since that's the only walk
        // IDrawingDoc exposes, but the missing-check here is a fresh File.Exists re-derivation, not a reuse of the
        // handler's grouping/repair state) rather than the nested GetViews() sheet-grouping GetDrawingViews.cs
        // uses. Called both before (run0, missing count must be >0) and after (run1, must be 0) the update.
        public static JObject MeasureUpdateSheetReferences(IModelDoc2 model)
        {
            var res = new JObject();
            var missing = new JArray();
            int total = 0;
            var dd = model as DrawingDoc;
            if (dd == null) { res["totalViews"] = 0; res["missingCount"] = 0; res["missingModels"] = missing; return res; }

            string drwFolder = null;
            try { drwFolder = Path.GetDirectoryName(model.GetPathName()); } catch { }

            var v = dd.GetFirstView() as IView;
            bool first = true;
            while (v != null)
            {
                if (!first)
                {
                    total++;
                    string rm = null; try { rm = v.GetReferencedModelName(); } catch { }
                    if (!string.IsNullOrEmpty(rm))
                    {
                        string resolved = rm;
                        try { if (!Path.IsPathRooted(resolved) && drwFolder != null) resolved = Path.Combine(drwFolder, resolved); } catch { }
                        bool exists = false; try { exists = File.Exists(resolved); } catch { }
                        if (!exists) missing.Add(rm);
                    }
                }
                first = false;
                v = v.GetNextView() as IView;
            }
            res["totalViews"] = total;
            res["missingCount"] = missing.Count;
            res["missingModels"] = missing;
            return res;
        }
    }
}
