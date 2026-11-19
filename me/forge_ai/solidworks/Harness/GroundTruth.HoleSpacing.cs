using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground-truth for the hole_spacing handler. Shares NO code with HoleSpacing.cs, and uses a
    /// DIFFERENT topology primitive: CIRCULAR EDGES (IEdge.GetCurve -> Curve.CircleParams — the rim of a hole) via
    /// the same collection style GroundTruth.MeasureBoltCircle already uses, instead of the handler's CYLINDRICAL
    /// FACES (Surface.CylinderParams — the bore's own wall). A through-hole normally has TWO circular edges (the
    /// rim at each end), so they're collapsed to one point per hole by projecting onto the plane perpendicular to
    /// the group's own (sign-aligned) axis before deduping — genuinely different math from the handler's raw-3D-
    /// distance dedup on face origins.
    /// </summary>
    public static partial class GroundTruth
    {
        private class HsCirc { public double[] C; public double[] N; public double R; }

        public static JObject MeasureHoleSpacing(ISldWorks app, IModelDoc2 model)
        {
            var mo = new JObject();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { mo["applicable"] = false; mo["reason"] = "active doc is not a part"; return mo; }
            mo["applicable"] = true;

            var part = model as PartDoc;
            object[] bodies = null;
            try { bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]; } catch { }
            mo["hasSolid"] = bodies != null && bodies.Length > 0;
            if (bodies == null || bodies.Length == 0) { mo["spacingMm"] = -1; return mo; }

            var circs = new List<HsCirc>();
            foreach (var bo in bodies)
            {
                var body = bo as Body2; if (body == null) continue;
                object[] edges = null; try { edges = body.GetEdges() as object[]; } catch { }
                foreach (var eo in edges ?? new object[0])
                {
                    var e = eo as Edge; if (e == null) continue;
                    Curve curve = null; try { curve = e.GetCurve() as Curve; } catch { }
                    if (curve == null) continue;
                    bool circle = false; try { circle = curve.IsCircle(); } catch { }
                    if (!circle) continue;
                    double[] cp = null; try { cp = curve.CircleParams as double[]; } catch { }
                    if (cp == null || cp.Length < 7) continue;
                    double nl = Math.Sqrt(cp[3] * cp[3] + cp[4] * cp[4] + cp[5] * cp[5]);
                    double[] nrm = nl > 1e-9 ? new[] { cp[3] / nl, cp[4] / nl, cp[5] / nl } : null;
                    circs.Add(new HsCirc { C = new[] { cp[0], cp[1], cp[2] }, N = nrm, R = cp[6] });
                }
            }
            if (circs.Count == 0) { mo["spacingMm"] = -1; mo["reason"] = "no circular edges found"; return mo; }

            var groups = new Dictionary<int, List<HsCirc>>();
            foreach (var c in circs)
            {
                // 0.1mm buckets — see HoleSpacing.cs's own note: 0.3mm merged two genuinely distinct hole sizes
                // (4.06mm vs 4.20mm) on the real HIWIN HGH20CA block.
                int bucket = (int)Math.Round(c.R * 1000.0 / 0.1);
                if (!groups.ContainsKey(bucket)) groups[bucket] = new List<HsCirc>();
                groups[bucket].Add(c);
            }

            List<double[]> bestFootprints = null; double bestDia = -1, bestR = -1;
            foreach (var kv in groups)
            {
                var grp = kv.Value;
                if (grp.Count < 2) continue;

                // sign-aligned average axis (same technique as MeasureBoltCircle's own axis-align)
                double axx = 0, axy = 0, axz = 0; double[] refN = null;
                foreach (var c in grp)
                {
                    if (c.N == null) continue;
                    if (refN == null) refN = c.N;
                    double dot = c.N[0] * refN[0] + c.N[1] * refN[1] + c.N[2] * refN[2];
                    double s = dot < 0 ? -1 : 1;
                    axx += s * c.N[0]; axy += s * c.N[1]; axz += s * c.N[2];
                }
                double axl = Math.Sqrt(axx * axx + axy * axy + axz * axz);
                if (axl > 1e-9) { axx /= axl; axy /= axl; axz /= axl; } else { axx = 0; axy = 0; axz = 1; }

                // project each circle's center onto the plane perpendicular to the group axis (collapses a
                // through-hole's TWO rim edges — different depth, same footprint — to one point)
                var footprints = new List<double[]>();
                foreach (var c in grp)
                {
                    double along = c.C[0] * axx + c.C[1] * axy + c.C[2] * axz;
                    double[] fp = { c.C[0] - along * axx, c.C[1] - along * axy, c.C[2] - along * axz };
                    bool dup = false;
                    foreach (var f in footprints) { if (Dist(fp, f) < 0.0005) { dup = true; break; } }
                    if (!dup) footprints.Add(fp);
                }
                if (footprints.Count < 2) continue;
                // LARGEST-DIAMETER qualifying group, not most populous — mirrors HoleSpacing.cs's own reasoning
                // (small functional holes can outnumber the real bolt holes several-to-one on a real part).
                if (bestFootprints == null || grp[0].R > bestR)
                { bestFootprints = footprints; bestR = grp[0].R; bestDia = 2.0 * grp[0].R * 1000.0; }
            }

            if (bestFootprints == null) { mo["spacingMm"] = -1; mo["reason"] = "no repeating same-size hole pair"; return mo; }

            var nn = new List<double>();
            for (int i = 0; i < bestFootprints.Count; i++)
            {
                double bestD = double.MaxValue;
                for (int j = 0; j < bestFootprints.Count; j++)
                {
                    if (i == j) continue;
                    double d = Dist(bestFootprints[i], bestFootprints[j]);
                    if (d < bestD) bestD = d;
                }
                if (bestD < double.MaxValue) nn.Add(bestD);
            }
            double minNn = double.MaxValue, maxNn = double.MinValue;
            foreach (var d in nn) { if (d < minNn) minNn = d; if (d > maxNn) maxNn = d; }

            mo["holeCount"] = bestFootprints.Count;
            mo["holeDiameterMm"] = bestDia;
            mo["spacingMm"] = minNn == double.MaxValue ? -1 : minNn * 1000.0;
            mo["maxSpacingMm"] = maxNn == double.MinValue ? -1 : maxNn * 1000.0;
            return mo;
        }

        private static double Dist(double[] a, double[] b)
        {
            double dx = a[0] - b[0], dy = a[1] - b[1], dz = a[2] - b[2];
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }
    }
}
