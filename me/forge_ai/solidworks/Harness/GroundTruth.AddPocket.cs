using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground-truth for the add_pocket handler. Shares NO code with AddPocket.cs.
    ///
    /// Milling a rectangular pocket is a WRITE that REMOVES material and ADDS planar recess faces, so the harness
    /// compares a BASELINE read (run0, before any pocket) against the post-write read (run1) and asserts the geometry
    /// changed the way a real pocket must:
    ///
    ///   1. volumeMm3 DROPS               (material removed by the cut — the defining act of a pocket)
    ///   2. planarFaceCount RISES         (a blind rectangular recess adds new planar walls + floor)
    ///   3. hasForgePocket is TRUE run1   (a feature literally named 'Forge-Pocket' now exists)
    ///   4. rebuildErrors == 0            (the cut rebuilt clean)
    /// and the rerun is idempotent (run2 == run1 — no second pocket stacked).
    ///
    /// Every number is re-derived through a DIFFERENT SolidWorks path than the handler's: the handler measures volume
    /// with the whole-doc mass-property engine (IModelDocExtension.CreateMassProperty), while this ground truth sums
    /// per-body IBody2.GetMassProperties across the solid bodies — so agreement on the drop is a genuine cross-check,
    /// not a mirror of the handler's own math. The planar-face count is an independent surface scan; hasSolid lets the
    /// grader demand an honest handler refusal on a body-less part.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureAddPocket(ISldWorks app, IModelDoc2 model)
        {
            var mo = new JObject();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { mo["applicable"] = false; mo["reason"] = "active doc is not a part"; return mo; }
            mo["applicable"] = true;

            var part = model as PartDoc;
            int bodyCount = 0, faceCount = 0, planarFaceCount = 0;
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
                        bool plane = false; try { plane = s != null && s.IsPlane(); } catch { }
                        if (plane) planarFaceCount++;
                    }

                    double[] mp = null; try { mp = body.GetMassProperties(1.0) as double[]; } catch { }
                    if (mp != null && mp.Length >= 4) volM3 += mp[3];
                }
            }

            bool hasForgePocket = false;
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                string nm = null; try { nm = f.Name; } catch { }
                if (string.Equals(nm, "Forge-Pocket", StringComparison.OrdinalIgnoreCase)) { hasForgePocket = true; break; }
                f = f.GetNextFeature() as Feature;
            }

            int rebuild = 0; try { rebuild = model.Extension.GetWhatsWrongCount(); } catch { }
            double volMm3 = volM3 * 1e9;

            mo["bodyCount"] = bodyCount;
            mo["faceCount"] = faceCount;
            mo["planarFaceCount"] = planarFaceCount;
            mo["volumeMm3"] = volMm3;
            mo["hasForgePocket"] = hasForgePocket;
            mo["rebuildErrors"] = rebuild;
            mo["hasSolid"] = bodyCount > 0;

            var fp = new JObject();
            fp["faceCount"] = faceCount;
            fp["volumeMm3"] = volMm3;
            mo["fingerprint"] = fp;
            return mo;
        }
    }
}
