using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground-truth for the create_wrap handler. Shares NO code with CreateWrap.cs.
    ///
    /// An emboss wrap is a WRITE that ADDS material and ADDS a cylindrical wall face, the exact same defining
    /// deltas as add_boss (a wrap onto a planar face degenerates to a stand-off boss geometrically) — so the harness
    /// compares a BASELINE read (run0, before any wrap) against the post-write read (run1) and asserts:
    ///
    ///   1. volumeMm3 ROSE                (material ADDED by the emboss)
    ///   2. cylindricalFaceCount ROSE     (a new external cylindrical wrap-wall face appears)
    ///   3. hasForgeWrap is TRUE on run1  (a feature literally named 'Forge-Wrap' now exists)
    ///   4. rebuildErrors == 0            (the wrap rebuilt clean)
    /// and the rerun is idempotent (run2 == run1 — no second wrap stacked).
    ///
    /// Volume is re-derived through a DIFFERENT SolidWorks path than the handler's: the handler measures with the
    /// whole-doc mass-property engine (IModelDocExtension.CreateMassProperty), while this ground truth sums per-body
    /// IBody2.GetMassProperties across the solid bodies.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureCreateWrap(ISldWorks app, IModelDoc2 model)
        {
            var mo = new JObject();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { mo["applicable"] = false; mo["reason"] = "active doc is not a part"; return mo; }
            mo["applicable"] = true;

            var part = model as PartDoc;
            int bodyCount = 0, faceCount = 0, cylFaceCount = 0;
            double volM3 = 0;
            double[] bbox = null;
            if (part != null)
            {
                object[] bodies = null;
                try { bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]; } catch { }
                foreach (var bo in bodies ?? new object[0])
                {
                    var body = bo as Body2; if (body == null) continue;
                    bodyCount++;
                    bbox = AwUnionBox(bbox, AwBodyBox(body));

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

            bool hasForgeWrap = false;
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                string nm = null; try { nm = f.Name; } catch { }
                if (string.Equals(nm, "Forge-Wrap", StringComparison.OrdinalIgnoreCase)) { hasForgeWrap = true; break; }
                f = f.GetNextFeature() as Feature;
            }

            int rebuild = 0; try { rebuild = model.Extension.GetWhatsWrongCount(); } catch { }
            double volMm3 = volM3 * 1e9;

            mo["bodyCount"] = bodyCount;
            mo["faceCount"] = faceCount;
            mo["cylindricalFaceCount"] = cylFaceCount;
            mo["volumeMm3"] = volMm3;
            mo["bboxDiagMm"] = AwBoxDiagMm(bbox);
            mo["hasForgeWrap"] = hasForgeWrap;
            mo["rebuildErrors"] = rebuild;
            mo["hasSolid"] = bodyCount > 0;

            var fp = new JObject();
            fp["faceCount"] = faceCount;
            fp["volumeMm3"] = volMm3;
            mo["fingerprint"] = fp;
            return mo;
        }

        private static double[] AwBodyBox(Body2 body)
        { try { return body.GetBodyBox() as double[]; } catch { return null; } }

        private static double[] AwUnionBox(double[] acc, double[] b)
        {
            if (b == null || b.Length < 6) return acc;
            if (acc == null) return new[] { b[0], b[1], b[2], b[3], b[4], b[5] };
            return new[]
            {
                Math.Min(acc[0], b[0]), Math.Min(acc[1], b[1]), Math.Min(acc[2], b[2]),
                Math.Max(acc[3], b[3]), Math.Max(acc[4], b[4]), Math.Max(acc[5], b[5])
            };
        }

        private static double AwBoxDiagMm(double[] b)
        {
            if (b == null || b.Length < 6) return 0;
            double dx = b[3] - b[0], dy = b[4] - b[1], dz = b[5] - b[2];
            return Math.Sqrt(dx * dx + dy * dy + dz * dz) * 1000.0;
        }
    }
}
