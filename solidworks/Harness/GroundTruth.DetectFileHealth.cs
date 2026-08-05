using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for detect_file_health (tool 239) — shares NO code with DetectFileHealth.cs.
    /// The handler classifies via IModelDocExtension.GetWhatsWrong; this re-derives the problem count from a per-feature
    /// (and MateGroup sub-feature) IFeature.GetErrorCode2 walk (a genuinely different API — proves the handler's
    /// enumeration is COMPLETE and not the null-but-nonzero trap), plus the GetWhatsWrongCount scalar, plus its own
    /// unknown-feature-type census and the freeze location. Read-only. Known truth:
    ///   clean part  -> walkTotal 0, unknownTypes 0  => handler verdict "safe"
    ///   redwave3    -> walkTotal >0                 => handler verdict "do-not-touch" (over-defined mates)
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureDetectFileHealth(IModelDoc2 model)
        {
            var mo = new JObject();
            if (model == null) { mo["error"] = "no model"; return mo; }
            try { model.ForceRebuild3(false); } catch { }

            int wwCount = 0; try { wwCount = model.Extension.GetWhatsWrongCount(); } catch { }

            int walkErr = 0, walkWarn = 0, unknownTypes = 0;
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    TallyHealth(f, ref walkErr, ref walkWarn);
                    if (IsUnknown(f)) unknownTypes++;
                    var s = f.GetFirstSubFeature() as Feature;
                    while (s != null) { TallyHealth(s, ref walkErr, ref walkWarn); s = s.GetNextSubFeature() as Feature; }
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch { }

            int frozen = 0; try { if (model.FeatureManager.GetFreezeLocation() != null) frozen = 1; } catch { }

            mo["whatsWrongCount"] = wwCount;
            mo["walkErr"] = walkErr;
            mo["walkWarn"] = walkWarn;
            mo["walkTotal"] = walkErr + walkWarn;
            mo["unknownTypes"] = unknownTypes;
            mo["frozen"] = frozen;
            // expected verdict derived independently from the GT signals (mirrors the handler's own logic, computed here separately)
            string expected = (walkErr > 0 || unknownTypes > 0) ? "do-not-touch"
                            : (walkWarn > 0 || frozen > 0) ? "caution" : "safe";
            mo["expectedVerdict"] = expected;
            return mo;
        }

        private static void TallyHealth(Feature f, ref int err, ref int warn)
        {
            int code = 0; bool isWarn = false;
            try { code = f.GetErrorCode2(out isWarn); } catch { return; }
            if (code == (int)swFeatureError_e.swFeatureErrorNone) return;
            if (isWarn) warn++; else err++;
        }

        private static bool IsUnknown(Feature f)
        {
            string tn = null;
            try { tn = f.GetTypeName2(); } catch { return true; }
            if (string.IsNullOrEmpty(tn)) return true;
            return tn.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ||
                   tn.Equals("UnknownFeature", StringComparison.OrdinalIgnoreCase);
        }
    }
}
