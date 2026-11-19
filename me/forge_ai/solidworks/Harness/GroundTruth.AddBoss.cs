using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground-truth for the add_boss handler. Shares NO code with AddBoss.cs.
    ///
    /// Adding a boss is a WRITE that ADDS material and ADDS a cylindrical wall face, so — the exact inverse of add_hole
    /// (which removes volume and adds a bore face) — the harness compares a BASELINE read (run0, before any boss) against
    /// the post-write read (run1) and asserts the geometry changed the way a real outward boss must:
    ///
    ///   1. volumeMm3 ROSE                (material ADDED by the extrude — the defining act of a boss; UP, not down)
    ///   2. cylindricalFaceCount ROSE     (a new external cylindrical boss-wall face appears)
    ///   3. hasForgeBoss is TRUE on run1  (a feature literally named 'Forge-Boss' now exists)
    ///   4. rebuildErrors == 0            (the extrude rebuilt clean)
    /// and the rerun is idempotent (run2 == run1 — no second boss stacked).
    ///
    /// Every number here is re-derived from scratch through a DIFFERENT SolidWorks path than the handler's: the handler
    /// measures volume with the whole-doc mass-property engine (IModelDocExtension.CreateMassProperty), while this ground
    /// truth sums per-body IBody2.GetMassProperties across the solid bodies — so agreement on the RISE is a genuine
    /// cross-check, not a mirror of the handler's own math. The cylindrical-face count is an independent surface scan;
    /// the bbox diagonal is from IBody2.GetBodyBox. hasSolid lets the grader demand an honest handler refusal on a
    /// body-less part.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureAddBoss(ISldWorks app, IModelDoc2 model)
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
                    bbox = AbUnionBox(bbox, AbBodyBox(body));

                    object[] faces = null; try { faces = body.GetFaces() as object[]; } catch { }
                    foreach (var fo in faces ?? new object[0])
                    {
                        var face = fo as Face2; if (face == null) continue;
                        faceCount++;
                        Surface s = null; try { s = face.GetSurface() as Surface; } catch { }
                        bool cyl = false; try { cyl = s != null && s.IsCylinder(); } catch { }
                        if (cyl) cylFaceCount++;
                    }

                    // independent volume: per-body mass properties. GetMassProperties(density) returns
                    // [0..2]=COM, [3]=Volume, [4]=Area, [5]=Mass, ... — a different path than the handler's whole-doc engine.
                    double[] mp = null; try { mp = body.GetMassProperties(1.0) as double[]; } catch { }
                    if (mp != null && mp.Length >= 4) volM3 += mp[3];
                }
            }

            bool hasForgeBoss = false;
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                string nm = null; try { nm = f.Name; } catch { }
                if (string.Equals(nm, "Forge-Boss", StringComparison.OrdinalIgnoreCase)) { hasForgeBoss = true; break; }
                f = f.GetNextFeature() as Feature;
            }

            int rebuild = 0; try { rebuild = model.Extension.GetWhatsWrongCount(); } catch { }
            double volMm3 = volM3 * 1e9;   // m^3 -> mm^3

            mo["bodyCount"] = bodyCount;
            mo["faceCount"] = faceCount;
            mo["cylindricalFaceCount"] = cylFaceCount;
            mo["volumeMm3"] = volMm3;
            mo["bboxDiagMm"] = AbBoxDiagMm(bbox);
            mo["hasForgeBoss"] = hasForgeBoss;   // does a feature named 'Forge-Boss' exist?
            mo["rebuildErrors"] = rebuild;
            mo["hasSolid"] = bodyCount > 0;      // no solid => the handler MUST refuse honestly, not fake a boss

            // change fingerprint the grader diffs run0 -> run1 (volume UP, cyl faces UP, boss present); idempotent run2==run1
            var fp = new JObject();
            fp["faceCount"] = faceCount;
            fp["volumeMm3"] = volMm3;
            mo["fingerprint"] = fp;
            return mo;
        }

        private static double[] AbBodyBox(Body2 body)
        { try { return body.GetBodyBox() as double[]; } catch { return null; } }

        private static double[] AbUnionBox(double[] acc, double[] b)
        {
            if (b == null || b.Length < 6) return acc;
            if (acc == null) return new[] { b[0], b[1], b[2], b[3], b[4], b[5] };
            return new[]
            {
                Math.Min(acc[0], b[0]), Math.Min(acc[1], b[1]), Math.Min(acc[2], b[2]),
                Math.Max(acc[3], b[3]), Math.Max(acc[4], b[4]), Math.Max(acc[5], b[5])
            };
        }

        private static double AbBoxDiagMm(double[] b)
        {
            if (b == null || b.Length < 6) return 0;
            double dx = b[3] - b[0], dy = b[4] - b[1], dz = b[5] - b[2];
            return Math.Sqrt(dx * dx + dy * dy + dz * dz) * 1000.0;
        }
    }
}
