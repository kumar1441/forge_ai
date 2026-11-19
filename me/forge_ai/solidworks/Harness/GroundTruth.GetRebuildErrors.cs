using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for get_rebuild_errors (tool 96) — shares NO code with GetRebuildErrors.cs.
    /// The handler enumerates via IModelDocExtension.GetWhatsWrong; this re-derives the problem count from a
    /// per-feature IFeature.GetErrorCode2 walk (a genuinely different API), plus the GetWhatsWrongCount scalar so the
    /// harness can prove the handler's enumeration is COMPLETE (array length == count) AND that a truly independent
    /// API also sees breakage (walk > 0 on the redwave fixture). Read-only.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureGetRebuildErrors(IModelDoc2 model)
        {
            var mo = new JObject();
            if (model == null) { mo["error"] = "no model"; return mo; }
            try { model.ForceRebuild3(false); } catch { }

            int wwCount = 0; try { wwCount = model.Extension.GetWhatsWrongCount(); } catch { }

            int walkErr = 0, walkWarn = 0;
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    Tally(f, ref walkErr, ref walkWarn);
                    var s = f.GetFirstSubFeature() as Feature;
                    while (s != null) { Tally(s, ref walkErr, ref walkWarn); s = s.GetNextSubFeature() as Feature; }
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch { }

            mo["whatsWrongCount"] = wwCount;
            mo["walkErr"] = walkErr;
            mo["walkWarn"] = walkWarn;
            mo["walkTotal"] = walkErr + walkWarn;
            return mo;
        }

        private static void Tally(Feature f, ref int err, ref int warn)
        {
            int code = 0; bool isWarn = false;
            try { code = f.GetErrorCode2(out isWarn); } catch { return; }
            if (code == (int)swFeatureError_e.swFeatureErrorNone) return;
            if (isWarn) warn++; else err++;
        }
    }
}
