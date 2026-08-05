using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT nearest-perpendicular face-pair angle — shares NO code with MeasureAngle. Its own planar-face
        // normal collection + its own pairwise search. On a box the answer is 90°, a KNOWN truth the harness asserts.
        public static JObject MeasureMeasureAngle(IModelDoc2 model)
        {
            var res = new JObject();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART) { res["angleDeg"] = -1; return res; }
            var normals = new List<double[]>();
            try
            {
                var bodies = (model as PartDoc).GetBodies2((int)swBodyType_e.swSolidBody, false) as object[];
                foreach (var bo in bodies ?? new object[0])
                {
                    var body = bo as Body2; if (body == null) continue;
                    foreach (var fo in (body.GetFaces() as object[]) ?? new object[0])
                    {
                        var face = fo as Face2; if (face == null) continue;
                        var s = face.GetSurface() as Surface; if (s == null || !s.IsPlane()) continue;
                        var p = s.PlaneParams as double[]; if (p == null || p.Length < 3) continue;
                        double m = Math.Sqrt(p[0] * p[0] + p[1] * p[1] + p[2] * p[2]); if (m < 1e-12) continue;
                        var n = new[] { p[0] / m, p[1] / m, p[2] / m };
                        bool dup = false;
                        foreach (var e in normals) if (Math.Abs(e[0] * n[0] + e[1] * n[1] + e[2] * n[2]) > 0.9999) { dup = true; break; }
                        if (!dup) normals.Add(n);
                    }
                }
            }
            catch { }
            res["planarFaces"] = normals.Count;
            double best = -1, bestGap = double.MaxValue;
            for (int i = 0; i < normals.Count; i++)
                for (int j = i + 1; j < normals.Count; j++)
                {
                    double d = Math.Abs(normals[i][0] * normals[j][0] + normals[i][1] * normals[j][1] + normals[i][2] * normals[j][2]);
                    if (d > 1) d = 1;
                    double ang = Math.Acos(d) * 180.0 / Math.PI;
                    double gap = Math.Abs(ang - 90.0);
                    if (gap < bestGap) { bestGap = gap; best = ang; }
                }
            res["angleDeg"] = best;
            return res;
        }
    }
}
