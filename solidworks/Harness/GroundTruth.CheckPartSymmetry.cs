using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for check_part_symmetry (tool 176) — shares NO detection code with CheckPartSymmetry.cs.
    /// The handler mirrors the body and compares by SOLID-INTERSECTION VOLUME; this instead builds a multiset of every
    /// face's (bounding-box centre, area), reflects that multiset about each centroidal principal plane, and calls the
    /// body symmetric about a plane when the reflected multiset equals the original. Two different geometric routes that
    /// must agree on the all-planar-face fixtures (box => 3 planes, L => 1). Read-only.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureCheckPartSymmetry(IModelDoc2 model)
        {
            var mo = new JObject();
            var part = model as PartDoc;
            if (part == null) { mo["error"] = "not a part"; return mo; }

            Body2 body = null; double best = -1;
            foreach (var o in (part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]) ?? new object[0])
            {
                var b = o as Body2; if (b == null) continue;
                double v = VolOf(b);
                if (v > best) { best = v; body = b; }
            }
            if (body == null) { mo["error"] = "no solid body"; return mo; }

            double[] c = new double[3];
            try { var mp = body.GetMassProperties(0) as double[]; if (mp != null && mp.Length >= 3) { c[0] = mp[0]; c[1] = mp[1]; c[2] = mp[2]; } }
            catch { }

            var faces = new List<double[]>();   // {cx, cy, cz, area}
            foreach (var fo in (body.GetFaces() as object[]) ?? new object[0])
            {
                var f = fo as Face2; if (f == null) continue;
                double[] box = null; try { box = f.GetBox() as double[]; } catch { }
                if (box == null || box.Length < 6) continue;
                double ar = 0; try { ar = f.GetArea(); } catch { }
                faces.Add(new double[] { (box[0] + box[3]) / 2, (box[1] + box[4]) / 2, (box[2] + box[5]) / 2, ar });
            }

            var orig = Sig(faces, -1, 0);
            int planes = 0; var axes = new JArray();
            string[] nm = { "X", "Y", "Z" };
            for (int k = 0; k < 3; k++)
            {
                var refl = Sig(faces, k, c[k]);
                if (MultisetEqual(orig, refl)) { planes++; axes.Add(nm[k]); }
            }

            mo["symmetryPlanes"] = planes;
            mo["symmetric"] = planes >= 1;
            mo["planeAxes"] = axes;
            mo["faceCount"] = faces.Count;
            return mo;
        }

        // build a sorted list of "cx|cy|cz|area" signatures; if axis>=0 reflect that coordinate about ck first.
        private static List<string> Sig(List<double[]> faces, int axis, double ck)
        {
            var sigs = new List<string>();
            foreach (var f in faces)
            {
                double x = f[0], y = f[1], z = f[2];
                if (axis == 0) x = 2 * ck - x; else if (axis == 1) y = 2 * ck - y; else if (axis == 2) z = 2 * ck - z;
                sigs.Add(Q(x) + "|" + Q(y) + "|" + Q(z) + "|" + QA(f[3]));
            }
            sigs.Sort(StringComparer.Ordinal);
            return sigs;
        }

        private static bool MultisetEqual(List<string> a, List<string> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static double VolOf(Body2 b)
        { try { var mp = b.GetMassProperties(0) as double[]; return (mp != null && mp.Length >= 4) ? mp[3] : 0; } catch { return 0; } }

        private static string Q(double v) { return Math.Round(v, 4).ToString("0.0000", CultureInfo.InvariantCulture); }
        private static string QA(double v) { return Math.Round(v, 8).ToString("0.00000000", CultureInfo.InvariantCulture); }
    }
}
