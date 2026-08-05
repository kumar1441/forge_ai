using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for compare_bodies (tool 170) — shares NO code with CompareBodies.cs. It fingerprints
    /// bodies by SORTED BOUNDING-BOX DIMS (from GetBodyBox) rather than the handler's volume+area, and detects overlap
    /// by AXIS-ALIGNED BOUNDING-BOX intersection rather than the handler's solid boolean. On the all-boxes multibody
    /// fixture both routes must agree (AABB == true solid overlap for axis-aligned boxes), so a divergence exposes a
    /// dead intersection API or a fingerprint bug. Read-only.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureCompareBodies(IModelDoc2 model)
        {
            var mo = new JObject();
            var part = model as PartDoc;
            if (part == null) { mo["error"] = "not a part"; return mo; }

            var boxes = new List<double[]>();
            var fps = new List<string>();
            foreach (var o in (part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]) ?? new object[0])
            {
                var body = o as Body2; if (body == null) continue;
                double[] bx = null; try { bx = body.GetBodyBox() as double[]; } catch { }
                if (bx == null || bx.Length < 6) { boxes.Add(null); fps.Add("null"); continue; }
                boxes.Add(bx);
                double[] d = { Math.Abs(bx[3] - bx[0]), Math.Abs(bx[4] - bx[1]), Math.Abs(bx[5] - bx[2]) };
                Array.Sort(d);
                fps.Add(K(d[0]) + "|" + K(d[1]) + "|" + K(d[2]));
            }

            int total = boxes.Count;
            var groups = new Dictionary<string, int>();
            foreach (var f in fps) { groups[f] = groups.ContainsKey(f) ? groups[f] + 1 : 1; }
            int dupGroups = 0, bodiesInDup = 0;
            foreach (var kv in groups) if (kv.Value >= 2) { dupGroups++; bodiesInDup += kv.Value; }

            int overlapPairs = 0;
            for (int i = 0; i < total; i++)
                for (int j = i + 1; j < total; j++)
                    if (AabbOverlap(boxes[i], boxes[j])) overlapPairs++;

            mo["totalBodies"] = total;
            mo["duplicateGroups"] = dupGroups;
            mo["bodiesInDupGroups"] = bodiesInDup;
            mo["uniqueShapes"] = groups.Count;
            mo["overlappingPairs"] = overlapPairs;
            return mo;
        }

        private static bool AabbOverlap(double[] a, double[] b)
        {
            if (a == null || b == null || a.Length < 6 || b.Length < 6) return false;
            const double eps = 1e-6;   // a 1 micron touch is not an overlap
            return (Math.Min(a[3], b[3]) - Math.Max(a[0], b[0]) > eps) &&
                   (Math.Min(a[4], b[4]) - Math.Max(a[1], b[1]) > eps) &&
                   (Math.Min(a[5], b[5]) - Math.Max(a[2], b[2]) > eps);
        }

        private static string K(double v) { return Math.Round(v, 6).ToString("0.######", CultureInfo.InvariantCulture); }
    }
}
