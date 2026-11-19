using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground-truth for the geometry_defeature handler. Shares NO code with GeometryDefeature.cs.
    ///
    /// Defeature is a real geometry WRITE that removes small holes/fillets and heals the openings, so — like the shell
    /// handler — the harness compares a BASELINE read (run0, before any removal) with the post-write read (run1) and
    /// asserts the geometry changed the way a real detail-strip must:
    ///
    ///   1. faceCount DROPS          (each removed hole/fillet takes its face(s) with it; patching may merge more)
    ///   2. smallHoleFaceCount DROPS (the very faces the handler targeted are gone)
    ///   3. volumeMm3 RISES          (filling a hole / sharpening a fillet ADDS material — a defeatured solid is fuller)
    ///
    /// Every number here is re-derived from scratch through a DIFFERENT path than the handler: this counts small hole
    /// faces with its OWN cylinder+concavity test and its OWN bbox-fraction threshold, and sums volume per body via
    /// IBody2.GetMassProperties (the handler uses the whole-doc IModelDocExtension.CreateMassProperty engine). So
    /// agreement on the drop/rise is a genuine cross-check, not a mirror of the handler's own math. hasSolid lets the
    /// grader demand an honest handler refusal on a body-less part.
    /// </summary>
    public static partial class GroundTruth
    {
        private const double GdDefaultFrac = 0.08;   // independent copy of the small-detail threshold (8% of smallest bbox span)

        public static JObject MeasureGeometryDefeature(ISldWorks app, IModelDoc2 model)
        {
            var mo = new JObject();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { mo["applicable"] = false; mo["reason"] = "active doc is not a part"; return mo; }
            mo["applicable"] = true;

            var part = model as PartDoc;
            int bodyCount = 0, faceCount = 0, cylFaceCount = 0, smallHoleFaceCount = 0;
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
                    bbox = GdUnionBox(bbox, GdBodyBox(body));
                    double[] mp = null; try { mp = body.GetMassProperties(1.0) as double[]; } catch { }
                    if (mp != null && mp.Length >= 4) volM3 += mp[3];
                }

                double minSpanM = GdMinSpanM(bbox);
                double thrDiaM = GdDefaultFrac * minSpanM;   // own threshold (default frac only — the read-only diff is what's asserted)

                foreach (var bo in bodies ?? new object[0])
                {
                    var body = bo as Body2; if (body == null) continue;
                    object[] faces = null; try { faces = body.GetFaces() as object[]; } catch { }
                    foreach (var fo in faces ?? new object[0])
                    {
                        var face = fo as Face2; if (face == null) continue;
                        faceCount++;
                        Surface s = null; try { s = face.GetSurface() as Surface; } catch { }
                        if (s == null) continue;
                        bool isCyl = false; try { isCyl = s.IsCylinder(); } catch { }
                        if (!isCyl) continue;
                        cylFaceCount++;
                        double[] cp = null; try { cp = s.CylinderParams as double[]; } catch { }
                        if (cp == null || cp.Length < 7) continue;
                        double dia = cp[6] * 2.0;
                        if (thrDiaM > 0 && dia > 0 && dia <= thrDiaM && GdConcave(face, s, cp)) smallHoleFaceCount++;
                    }
                }
            }

            int rebuild = 0; try { rebuild = model.Extension.GetWhatsWrongCount(); } catch { }
            double volMm3 = volM3 * 1e9;

            mo["bodyCount"] = bodyCount;
            mo["faceCount"] = faceCount;
            mo["cylindricalFaceCount"] = cylFaceCount;
            mo["smallHoleFaceCount"] = smallHoleFaceCount;
            mo["volumeMm3"] = volMm3;
            mo["bboxDiagMm"] = GdBoxDiagMm(bbox);
            mo["rebuildErrors"] = rebuild;
            mo["hasSolid"] = bodyCount > 0;   // no solid => the handler MUST refuse honestly, not fake a simplify

            // change fingerprint the grader diffs run0 -> run1 (faces DOWN, volume UP, small holes DOWN)
            var fp = new JObject();
            fp["bodyCount"] = bodyCount;
            fp["faceCount"] = faceCount;
            fp["volumeMm3"] = volMm3;
            fp["rebuildErrors"] = rebuild;
            mo["fingerprint"] = fp;
            return mo;
        }

        // concave (a hole) iff the outward normal at the face centroid points toward the cylinder axis. Independent copy.
        private static bool GdConcave(Face2 face, Surface s, double[] cp)
        {
            try
            {
                double[] box = face.GetBox() as double[];
                if (box == null || box.Length < 6) return false;
                double[] c = { (box[0] + box[3]) / 2, (box[1] + box[4]) / 2, (box[2] + box[5]) / 2 };
                double[] P = face.GetClosestPointOn(c[0], c[1], c[2]) as double[];
                if (P == null || P.Length < 3) return false;
                double[] n = s.EvaluateAtPoint(P[0], P[1], P[2]) as double[];
                if (n == null || n.Length < 3) return false;
                double nl = Math.Sqrt(n[0] * n[0] + n[1] * n[1] + n[2] * n[2]); if (nl < 1e-9) return false;
                double[] nout = { n[0] / nl, n[1] / nl, n[2] / nl };
                bool reversed = false; try { reversed = face.FaceInSurfaceSense(); } catch { }
                if (reversed) { nout[0] = -nout[0]; nout[1] = -nout[1]; nout[2] = -nout[2]; }
                double[] O = { cp[0], cp[1], cp[2] };
                double al = Math.Sqrt(cp[3] * cp[3] + cp[4] * cp[4] + cp[5] * cp[5]); if (al < 1e-9) return false;
                double[] a = { cp[3] / al, cp[4] / al, cp[5] / al };
                double[] d = { P[0] - O[0], P[1] - O[1], P[2] - O[2] };
                double axial = d[0] * a[0] + d[1] * a[1] + d[2] * a[2];
                double[] w = { d[0] - axial * a[0], d[1] - axial * a[1], d[2] - axial * a[2] };
                double wl = Math.Sqrt(w[0] * w[0] + w[1] * w[1] + w[2] * w[2]); if (wl < 1e-9) return false;
                return (nout[0] * w[0] + nout[1] * w[1] + nout[2] * w[2]) / wl < 0;
            }
            catch { return false; }
        }

        private static double[] GdBodyBox(Body2 body)
        { try { return body.GetBodyBox() as double[]; } catch { return null; } }

        private static double[] GdUnionBox(double[] acc, double[] b)
        {
            if (b == null || b.Length < 6) return acc;
            if (acc == null) return new[] { b[0], b[1], b[2], b[3], b[4], b[5] };
            return new[]
            {
                Math.Min(acc[0], b[0]), Math.Min(acc[1], b[1]), Math.Min(acc[2], b[2]),
                Math.Max(acc[3], b[3]), Math.Max(acc[4], b[4]), Math.Max(acc[5], b[5])
            };
        }

        private static double GdMinSpanM(double[] b)
        {
            if (b == null || b.Length < 6) return 0;
            return Math.Min(b[3] - b[0], Math.Min(b[4] - b[1], b[5] - b[2]));
        }

        private static double GdBoxDiagMm(double[] b)
        {
            if (b == null || b.Length < 6) return 0;
            double dx = b[3] - b[0], dy = b[4] - b[1], dz = b[5] - b[2];
            return Math.Sqrt(dx * dx + dy * dy + dz * dz) * 1000.0;
        }
    }
}
