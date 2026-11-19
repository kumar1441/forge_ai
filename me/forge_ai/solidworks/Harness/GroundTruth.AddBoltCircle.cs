using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground-truth for the add_bolt_circle handler. Shares NO code with AddBoltCircle.cs.
    ///
    /// Adding N bolt holes is a WRITE, so — like add_hole — the harness compares a BASELINE read (run0) against the
    /// post-write read (run1): volumeMm3 DROPS, cylindricalFaceCount RISES by (at least) the hole count, and a
    /// feature literally named 'Forge-BoltCircle' now exists.
    ///
    /// Beyond that shared shape, this ALSO re-derives the pattern's own geometry from scratch — bucketing every
    /// cylindrical face by radius (0.3mm buckets, same technique MeasureBoltCircle.cs uses for the READ side) and
    /// reporting the largest same-radius group's member count, mean pattern diameter (2x mean distance from the
    /// group's own centroid to each hole), and the standard deviation of the angular spacing between neighbours —
    /// so the grader can independently confirm "N holes, ~the stated bolt-circle diameter, evenly spaced" without
    /// trusting AddBoltCircle.cs's own math.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureAddBoltCircle(ISldWorks app, IModelDoc2 model)
        {
            var mo = new JObject();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { mo["applicable"] = false; mo["reason"] = "active doc is not a part"; return mo; }
            mo["applicable"] = true;

            var part = model as PartDoc;
            int bodyCount = 0, faceCount = 0, cylFaceCount = 0;
            double volM3 = 0;
            var cyl = new List<double[]>();   // each: [radiusM, originX, originY, originZ]
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
                        bool isCyl = false; try { isCyl = s != null && s.IsCylinder(); } catch { }
                        if (isCyl)
                        {
                            cylFaceCount++;
                            // CylinderParams = [originX,Y,Z, axisX,Y,Z, radius]
                            double[] cp = null; try { cp = s.CylinderParams as double[]; } catch { }
                            if (cp != null && cp.Length >= 7) cyl.Add(new[] { cp[6], cp[0], cp[1], cp[2] });
                        }
                    }

                    double[] mp = null; try { mp = body.GetMassProperties(1.0) as double[]; } catch { }
                    if (mp != null && mp.Length >= 4) volM3 += mp[3];
                }
            }

            // dominant same-radius group (>=2 members) = the just-added pattern (or an existing one) — bucket by
            // radius so a genuinely independent re-derivation, not a trust of the handler's own count/placement.
            var groups = new Dictionary<int, List<double[]>>();
            foreach (var h in cyl)
            {
                int bucket = (int)Math.Round(h[0] * 1000.0 / 0.3);
                if (!groups.ContainsKey(bucket)) groups[bucket] = new List<double[]>();
                groups[bucket].Add(h);
            }
            List<double[]> best = null;
            foreach (var kv in groups)
                if (kv.Value.Count >= 2 && (best == null || kv.Value.Count > best.Count)) best = kv.Value;

            int patternCount = 0;
            double patternHoleRadiusMm = 0, patternDiameterMm = 0, angularSpacingStdDeg = -1;
            if (best != null)
            {
                patternCount = best.Count;
                foreach (var h in best) patternHoleRadiusMm += h[0];
                patternHoleRadiusMm = patternHoleRadiusMm / best.Count * 1000.0;

                double cx = 0, cy = 0;
                foreach (var h in best) { cx += h[1]; cy += h[2]; }
                cx /= best.Count; cy /= best.Count;

                var radii = new List<double>();
                var angles = new List<double>();
                foreach (var h in best)
                {
                    double dx = h[1] - cx, dy = h[2] - cy;
                    radii.Add(Math.Sqrt(dx * dx + dy * dy));
                    angles.Add(Math.Atan2(dy, dx));
                }
                double meanR = 0; foreach (var r in radii) meanR += r; meanR /= radii.Count;
                patternDiameterMm = meanR * 2.0 * 1000.0;

                angles.Sort();
                var deltasDeg = new List<double>();
                for (int i = 0; i < angles.Count; i++)
                {
                    double a1 = angles[i], a2 = angles[(i + 1) % angles.Count];
                    double d = a2 - a1; if (d <= 0) d += 2 * Math.PI;
                    deltasDeg.Add(d * 180.0 / Math.PI);
                }
                double meanD = 0; foreach (var d in deltasDeg) meanD += d; meanD /= deltasDeg.Count;
                double var2 = 0; foreach (var d in deltasDeg) var2 += (d - meanD) * (d - meanD);
                angularSpacingStdDeg = Math.Sqrt(var2 / deltasDeg.Count);
            }

            bool hasFeature = false;
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                string nm = null; try { nm = f.Name; } catch { }
                if (string.Equals(nm, "Forge-BoltCircle", StringComparison.OrdinalIgnoreCase)) { hasFeature = true; break; }
                f = f.GetNextFeature() as Feature;
            }

            int rebuild = 0; try { rebuild = model.Extension.GetWhatsWrongCount(); } catch { }
            double volMm3 = volM3 * 1e9;

            mo["bodyCount"] = bodyCount;
            mo["faceCount"] = faceCount;
            mo["cylindricalFaceCount"] = cylFaceCount;
            mo["volumeMm3"] = volMm3;
            mo["hasForgeBoltCircle"] = hasFeature;
            mo["rebuildErrors"] = rebuild;
            mo["hasSolid"] = bodyCount > 0;
            mo["patternMemberCount"] = patternCount;          // members of the dominant same-radius group
            mo["patternHoleRadiusMm"] = patternHoleRadiusMm;  // each member's own radius (mean, mm)
            mo["patternDiameterMm"] = patternDiameterMm;      // 2x mean distance from group centroid to each hole
            mo["angularSpacingStdDeg"] = angularSpacingStdDeg; // 0 => perfectly even; -1 => no qualifying group

            var fp = new JObject();
            fp["faceCount"] = faceCount;
            fp["volumeMm3"] = volMm3;
            mo["fingerprint"] = fp;
            return mo;
        }
    }
}
