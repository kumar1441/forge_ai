using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground-truth for the add_counterbore handler. Shares NO code with AddCounterbore.cs.
    ///
    /// A counterbore is a WRITE that REMOVES material and leaves a STEPPED bore (two coaxial cylindrical faces of
    /// different radii), so the harness compares run0 (before) against run1 (after) and asserts:
    ///
    ///   1. volumeMm3 DROPS               (material removed by the two cuts)
    ///   2. cylindricalFaceCount RISES    (the recess + the clearance bore add cylindrical faces)
    ///   3. distinctCylRadii RISES        (two DIFFERENT bore radii now exist — the defining stepped shape)
    ///   4. hasForgeCounterbore run1      (both Forge-Counterbore* features exist)
    ///   5. rebuildErrors == 0
    /// and the rerun is idempotent (run2 == run1).
    ///
    /// Volume sums per-body IBody2.GetMassProperties (the handler uses the whole-doc CreateMassProperty engine); the
    /// cylindrical-radius set is an independent surface scan.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureAddCounterbore(ISldWorks app, IModelDoc2 model)
        {
            var mo = new JObject();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { mo["applicable"] = false; mo["reason"] = "active doc is not a part"; return mo; }
            mo["applicable"] = true;

            var part = model as PartDoc;
            int bodyCount = 0, faceCount = 0, cylFaceCount = 0;
            double volM3 = 0;
            var radii = new System.Collections.Generic.List<double>();
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
                        if (cyl)
                        {
                            cylFaceCount++;
                            double[] cp = null; try { cp = s.CylinderParams as double[]; } catch { }
                            if (cp != null && cp.Length >= 7) radii.Add(Math.Round(cp[6] * 1000.0, 2)); // mm, 0.01 resolution
                        }
                    }
                    double[] mp = null; try { mp = body.GetMassProperties(1.0) as double[]; } catch { }
                    if (mp != null && mp.Length >= 4) volM3 += mp[3];
                }
            }

            // count DISTINCT cylinder radii (a stepped bore introduces two different ones)
            var distinct = new System.Collections.Generic.HashSet<double>();
            foreach (var r in radii) distinct.Add(r);

            bool hasBore = false, hasClear = false;
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                string nm = null; try { nm = f.Name; } catch { }
                if (string.Equals(nm, "Forge-CounterboreBore", StringComparison.OrdinalIgnoreCase)) hasBore = true;
                if (string.Equals(nm, "Forge-CounterboreClear", StringComparison.OrdinalIgnoreCase)) hasClear = true;
                f = f.GetNextFeature() as Feature;
            }

            int rebuild = 0; try { rebuild = model.Extension.GetWhatsWrongCount(); } catch { }
            double volMm3 = volM3 * 1e9;

            mo["bodyCount"] = bodyCount;
            mo["faceCount"] = faceCount;
            mo["cylindricalFaceCount"] = cylFaceCount;
            mo["distinctCylRadii"] = distinct.Count;
            mo["volumeMm3"] = volMm3;
            mo["hasForgeCounterbore"] = hasBore && hasClear;
            mo["rebuildErrors"] = rebuild;
            mo["hasSolid"] = bodyCount > 0;

            var fp = new JObject();
            fp["faceCount"] = faceCount;
            fp["distinctCylRadii"] = distinct.Count;
            fp["volumeMm3"] = volMm3;
            mo["fingerprint"] = fp;
            return mo;
        }
    }
}
