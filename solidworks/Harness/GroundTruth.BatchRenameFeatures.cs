using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT tree roster for batch_rename_features. Deliberately RAW and UNFILTERED: every entry the tree walk
        // yields, as "name|GetTypeName2", with no notion of what a "real feature" is. The harness re-derives the
        // scaffold filter and the whole old→new mapping in PowerShell from the run0 and run1 rosters — a third
        // implementation, in another language, so the handler's scope rule and its rename plan are both checked against
        // code that shares nothing with them. (A GT that applied the same scaffold list would just be the handler's
        // heuristic written twice — the trap the ICE type-name bug hid in.)
        public static JObject MeasureBatchRenameFeatures(IModelDoc2 model)
        {
            var res = new JObject();
            var entries = new JArray();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { res["entries"] = entries; res["count"] = 0; return res; }
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    string tn = null; try { tn = f.GetTypeName2(); } catch { }
                    string nm = null; try { nm = f.Name; } catch { }
                    entries.Add((nm ?? "") + "|" + (tn ?? ""));
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch { }
            res["entries"] = entries;
            res["count"] = entries.Count;
            return res;
        }
    }
}
