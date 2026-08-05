using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for the DeleteFeature (delete_feature) WRITE handler. Shares NO code with DeleteFeature.cs.
    /// It does its OWN feature-tree traversal (FirstFeature / GetNextFeature), its OWN per-type tally, and its OWN
    /// GetBodies2 solid-body count — a genuine cross-check, not the same math twice.
    ///
    /// Because a delete REMOVES features from the tree (unlike suppress, which only flips a state), the harness asserts:
    ///   run1.totalFeatures == run0.totalFeatures − handler.Deleted   (the tree shrank by exactly Deleted)
    ///   run1.byType[deletedType] == 0  (or run0 − Deleted)           (the whole target type is gone)
    ///   run1.solidBodyCount >= 1                                     (the part was NOT destroyed)
    ///   run1.rebuildErrors == 0                                      (no survivor lost its parent)
    /// and the idempotent rerun run2 == run1 (once gone, gone — nothing left to delete).
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureDeleteFeature(ISldWorks app, IModelDoc2 model)
        {
            var d = new JObject();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { d["applicable"] = false; d["reason"] = model == null ? "no active document" : "not a part"; return d; }
            d["applicable"] = true;

            int total = 0;
            var byType = new JObject();
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    total++;
                    string tn = null; try { tn = f.GetTypeName2(); } catch { }
                    if (string.IsNullOrEmpty(tn)) tn = "Unknown";
                    byType[tn] = ((int?)byType[tn] ?? 0) + 1;
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch (Exception ex) { d["error"] = ex.GetType().Name + ": " + ex.Message; }

            int solids = 0;
            try
            {
                var part = model as PartDoc;
                if (part != null) { var b = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]; solids = b == null ? 0 : b.Length; }
            }
            catch { }

            int rb = 0; try { rb = model.Extension.GetWhatsWrongCount(); } catch { }

            d["totalFeatures"] = total;                 // a delete SHRINKS this by handler.Deleted (unlike suppress, which leaves it fixed)
            d["byType"] = byType;                        // per-type total tally — the deleted type's count drops to 0 (or by Deleted)
            d["solidBodyCount"] = solids;                // the part survives — a delete must never take this to 0
            d["rebuildErrors"] = rb;
            d["hasFeatures"] = total > 0;
            d["hasSolid"] = solids > 0;
            d["fingerprint"] = new JObject { ["totalFeatures"] = total, ["solidBodyCount"] = solids, ["rebuildErrors"] = rb };
            return d;
        }
    }
}
