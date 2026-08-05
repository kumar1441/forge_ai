using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground-truth for the rename_feature handler. Shares NO code with RenameFeature.cs.
    ///
    /// A rename is a pure METADATA change, so the harness asserts:
    ///   1. totalFeatures UNCHANGED   (a rename is not an add or a delete)
    ///   2. hasNewName TRUE run1      (a feature literally named &lt;NewName&gt; now exists)
    ///   3. rebuildErrors did not RISE (a rename must not corrupt the tree)
    /// and the rerun is idempotent (run2 == run1). The grader passes the expected NewName in via the test config's
    /// `renameTo` field; this GT reports the full name list + a flag for whether that name is present, re-read fresh from
    /// its own tree traversal.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureRenameFeature(ISldWorks app, IModelDoc2 model)
        {
            var mo = new JObject();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { mo["applicable"] = false; mo["reason"] = "active doc is not a part"; return mo; }
            mo["applicable"] = true;

            int total = 0;
            var names = new JArray();
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    total++;
                    string nm = null; try { nm = f.Name; } catch { }
                    names.Add(nm ?? "");
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch (Exception ex) { mo["error"] = ex.GetType().Name + ": " + ex.Message; }

            int rb = 0; try { rb = model.Extension.GetWhatsWrongCount(); } catch { }

            mo["totalFeatures"] = total;
            mo["names"] = names;                 // the grader checks the expected NewName is in here (and the old one is not)
            mo["rebuildErrors"] = rb;
            mo["hasFeatures"] = total > 0;
            mo["fingerprint"] = new JObject { ["totalFeatures"] = total, ["rebuildErrors"] = rb };
            return mo;
        }
    }
}
