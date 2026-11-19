using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class MeasureAngleResult
    {
        public double AngleDeg = -1;   // angle between the two chosen planar-face normals
        public int PlanarFaces;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 20 — measure_angle (READ), planar-face mode. Reports the angle between two of a part's planar faces. To be
    /// deterministic headless (no face picking), it finds the two planar faces whose normals are CLOSEST to
    /// perpendicular and reports that angle — on a box that's 90°, the canonical "are these faces square" check.
    /// Read-only; the ground truth recomputes from its own face-normal read.
    /// </summary>
    public static class MeasureAngle
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            // "add a 30 degree angle mate between X and Y" is add_angle_mate (a WRITE), not a measurement — exclude the
            // add-verb + "mate" so that write is never stolen by the angle-measurement read.
            return System.Text.RegularExpressions.Regex.IsMatch(c, @"\bangle\b") &&
                   System.Text.RegularExpressions.Regex.IsMatch(c, @"\b(between|faces|face|planes|square|perpendicular|of)\b") &&
                   !System.Text.RegularExpressions.Regex.IsMatch(c, @"\bmate\b") &&
                   !System.Text.RegularExpressions.Regex.IsMatch(c, @"\b(add|create|make|insert|place|put)\b");
        }

        public static async Task<MeasureAngleResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new MeasureAngleResult();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { res.Error = "Open a part to measure the angle between its faces."; return res; }

            await emit("Protractor", "reading planar faces", "run", null);
            var normals = CollectPlaneNormals(model as PartDoc);
            res.PlanarFaces = normals.Count;
            if (normals.Count < 2) { res.Error = "Need at least two planar faces to measure an angle."; return res; }

            // find the pair whose angle is closest to 90° (the meaningful "square" measure)
            double best = -1, bestGap = double.MaxValue;
            for (int i = 0; i < normals.Count; i++)
                for (int j = i + 1; j < normals.Count; j++)
                {
                    double ang = AngleBetween(normals[i], normals[j]);
                    double gap = Math.Abs(ang - 90.0);
                    if (gap < bestGap) { bestGap = gap; best = ang; }
                }
            res.AngleDeg = best;

            await emit("Protractor", null, "done", res.PlanarFaces + " planar faces · nearest-perpendicular pair = " + best.ToString("0.##", CultureInfo.InvariantCulture) + "°");
            res.Info = "Across " + res.PlanarFaces + " planar faces, the nearest-perpendicular pair meets at " +
                       best.ToString("0.##", CultureInfo.InvariantCulture) + "°.";
            return res;
        }

        // one representative normal per distinct planar-face orientation (dedup near-parallel normals)
        private static List<double[]> CollectPlaneNormals(PartDoc part)
        {
            var outp = new List<double[]>();
            try
            {
                var bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[];
                foreach (var bo in bodies ?? new object[0])
                {
                    var body = bo as Body2; if (body == null) continue;
                    foreach (var fo in (body.GetFaces() as object[]) ?? new object[0])
                    {
                        var face = fo as Face2; if (face == null) continue;
                        var s = face.GetSurface() as Surface; if (s == null || !s.IsPlane()) continue;
                        var p = s.PlaneParams as double[]; if (p == null || p.Length < 3) continue;
                        var n = Norm(new[] { p[0], p[1], p[2] });
                        bool dup = false;
                        foreach (var e in outp) if (Math.Abs(Dot(e, n)) > 0.9999) { dup = true; break; }   // same/opposite orientation
                        if (!dup) outp.Add(n);
                    }
                }
            }
            catch { }
            return outp;
        }

        private static double AngleBetween(double[] a, double[] b)
        {
            double d = Math.Abs(Dot(a, b)); if (d > 1) d = 1;
            return Math.Acos(d) * 180.0 / Math.PI;   // 0..90 (unsigned between planes)
        }
        private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
        private static double[] Norm(double[] v) { double m = Math.Sqrt(Dot(v, v)); return m < 1e-12 ? v : new[] { v[0] / m, v[1] / m, v[2] / m }; }
    }
}
