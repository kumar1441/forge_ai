using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground-truth for the pattern_feature handler. Shares NO code with PatternFeature.cs.
    ///
    /// Patterning a hole seed is a WRITE that REPLICATES geometry, so the harness compares a BASELINE read (run0, one
    /// seed hole) against the post-write read (run1) and asserts the array actually appeared:
    ///
    ///   1. cylindricalFaceCount RISES    (each patterned through-hole adds a bore face — 1 seed → N)
    ///   2. volumeMm3 DROPS               (each extra hole removes more material)
    ///   3. hasForgePattern is TRUE run1  (a feature named 'Forge-Pattern' now exists)
    ///   4. rebuildErrors == 0            (the pattern rebuilt clean — no instances off the body)
    /// and the rerun is idempotent (run2 == run1 — no second pattern stacked).
    ///
    /// Every number is re-derived through a DIFFERENT path than the handler where it matters: volume here sums per-body
    /// IBody2.GetMassProperties (the handler uses the whole-doc CreateMassProperty engine). The cylindrical-face count
    /// is an independent surface scan. hasSolid lets the grader demand an honest refusal on a body-less part.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasurePatternFeature(ISldWorks app, IModelDoc2 model)
        {
            var mo = new JObject();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { mo["applicable"] = false; mo["reason"] = "active doc is not a part"; return mo; }
            mo["applicable"] = true;

            var part = model as PartDoc;
            int bodyCount = 0, faceCount = 0, cylFaceCount = 0;
            double volM3 = 0;
            if (part != null)
            {
                object[] bodies = null;
                try { bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]; } catch { }
                foreach (var bo in bodies ?? new object[0])
                {
                    var body = bo as Body2; if (body == null) continue;
                    bodyCount++;
                    object[] faces = null; try { faces = body.GetFaces() as object[]; } catch { }
                    foreach (var fo in faces ?? new object[0])
                    {
                        var face = fo as Face2; if (face == null) continue;
                        faceCount++;
                        Surface s = null; try { s = face.GetSurface() as Surface; } catch { }
                        bool cyl = false; try { cyl = s != null && s.IsCylinder(); } catch { }
                        if (cyl) cylFaceCount++;
                    }
                    double[] mp = null; try { mp = body.GetMassProperties(1.0) as double[]; } catch { }
                    if (mp != null && mp.Length >= 4) volM3 += mp[3];
                }
            }

            bool hasForgePattern = false;
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                string nm = null; try { nm = f.Name; } catch { }
                if (string.Equals(nm, "Forge-Pattern", StringComparison.OrdinalIgnoreCase)) { hasForgePattern = true; break; }
                f = f.GetNextFeature() as Feature;
            }

            int rebuild = 0; try { rebuild = model.Extension.GetWhatsWrongCount(); } catch { }
            double volMm3 = volM3 * 1e9;

            mo["bodyCount"] = bodyCount;
            mo["faceCount"] = faceCount;
            mo["cylindricalFaceCount"] = cylFaceCount;
            mo["volumeMm3"] = volMm3;
            mo["hasForgePattern"] = hasForgePattern;
            mo["rebuildErrors"] = rebuild;
            mo["hasSolid"] = bodyCount > 0;

            var fp = new JObject();
            fp["faceCount"] = faceCount;
            fp["cylindricalFaceCount"] = cylFaceCount;
            fp["volumeMm3"] = volMm3;
            mo["fingerprint"] = fp;
            return mo;
        }
    }
}
