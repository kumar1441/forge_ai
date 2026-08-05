using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground-truth for the add_countersink handler. Shares NO code with AddCountersink.cs.
    ///
    /// A countersink is a WRITE that REMOVES material and leaves a CONICAL recess over a through bore, so the harness
    /// compares run0 (before) against run1 (after) and asserts:
    ///
    ///   1. volumeMm3 DROPS               (material removed by the cut + chamfer)
    ///   2. conicalFaceCount RISES        (a new CONE face appears — the defining countersink shape)
    ///   3. cylindricalFaceCount RISES    (the through clearance bore)
    ///   4. hasForgeCountersink run1      (both Forge-Countersink* features exist)
    ///   5. rebuildErrors == 0
    /// and the rerun is idempotent (run2 == run1).
    ///
    /// Volume sums per-body IBody2.GetMassProperties (the handler uses the whole-doc CreateMassProperty engine); the
    /// cone/cylinder face counts are an independent surface scan (Surface.IsCone / IsCylinder).
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureAddCountersink(ISldWorks app, IModelDoc2 model)
        {
            var mo = new JObject();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { mo["applicable"] = false; mo["reason"] = "active doc is not a part"; return mo; }
            mo["applicable"] = true;

            var part = model as PartDoc;
            int bodyCount = 0, faceCount = 0, coneFaceCount = 0, cylFaceCount = 0;
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
                        if (s == null) continue;
                        bool cone = false; try { cone = s.IsCone(); } catch { }
                        bool cyl = false; try { cyl = s.IsCylinder(); } catch { }
                        if (cone) coneFaceCount++;
                        if (cyl) cylFaceCount++;
                    }
                    double[] mp = null; try { mp = body.GetMassProperties(1.0) as double[]; } catch { }
                    if (mp != null && mp.Length >= 4) volM3 += mp[3];
                }
            }

            bool hasHole = false, hasCone = false;
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                string nm = null; try { nm = f.Name; } catch { }
                if (string.Equals(nm, "Forge-CountersinkHole", StringComparison.OrdinalIgnoreCase)) hasHole = true;
                if (string.Equals(nm, "Forge-CountersinkCone", StringComparison.OrdinalIgnoreCase)) hasCone = true;
                f = f.GetNextFeature() as Feature;
            }

            int rebuild = 0; try { rebuild = model.Extension.GetWhatsWrongCount(); } catch { }
            double volMm3 = volM3 * 1e9;

            mo["bodyCount"] = bodyCount;
            mo["faceCount"] = faceCount;
            mo["conicalFaceCount"] = coneFaceCount;
            mo["cylindricalFaceCount"] = cylFaceCount;
            mo["volumeMm3"] = volMm3;
            mo["hasForgeCountersink"] = hasHole && hasCone;
            mo["rebuildErrors"] = rebuild;
            mo["hasSolid"] = bodyCount > 0;

            var fp = new JObject();
            fp["conicalFaceCount"] = coneFaceCount;
            fp["volumeMm3"] = volMm3;
            mo["fingerprint"] = fp;
            return mo;
        }
    }
}
