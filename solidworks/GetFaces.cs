using System;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class GetFacesResult
    {
        public int Total;
        public int Planar, Cylindrical, Conical, Spherical, Other;
        public int DistinctPlaneOrientations;   // how many unique planar-face directions (a box = 3)
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 23 — get_face_normal / face inventory (READ). Face-type breakdown of a part: planar / cylindrical /
    /// conical / spherical / other, plus how many DISTINCT planar orientations exist (a box = 3 axes). Feeds DFM and
    /// feature-recognition questions ("how many holes-ish cylinders", "is this a prismatic part"). Read-only.
    /// </summary>
    public static class GetFaces
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            return System.Text.RegularExpressions.Regex.IsMatch(c, @"\bface(s)?\b") &&
                   System.Text.RegularExpressions.Regex.IsMatch(c, @"\b(normal|normals|inventory|breakdown|how many|count|list|planar|cylindrical|type|types)\b");
        }

        public static async Task<GetFacesResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new GetFacesResult();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { res.Error = "Open a part to inventory its faces."; return res; }

            await emit("Surveyor", "reading faces", "run", null);
            var normals = new System.Collections.Generic.List<double[]>();
            try
            {
                var bodies = (model as PartDoc).GetBodies2((int)swBodyType_e.swSolidBody, false) as object[];
                foreach (var bo in bodies ?? new object[0])
                {
                    var body = bo as Body2; if (body == null) continue;
                    foreach (var fo in (body.GetFaces() as object[]) ?? new object[0])
                    {
                        var face = fo as Face2; if (face == null) continue;
                        var s = face.GetSurface() as Surface; if (s == null) { res.Other++; res.Total++; continue; }
                        res.Total++;
                        if (s.IsPlane())
                        {
                            res.Planar++;
                            var p = s.PlaneParams as double[];
                            if (p != null && p.Length >= 3)
                            {
                                double m = Math.Sqrt(p[0] * p[0] + p[1] * p[1] + p[2] * p[2]);
                                if (m > 1e-12)
                                {
                                    var n = new[] { p[0] / m, p[1] / m, p[2] / m };
                                    bool dup = false;
                                    foreach (var e in normals) if (Math.Abs(e[0] * n[0] + e[1] * n[1] + e[2] * n[2]) > 0.9999) { dup = true; break; }
                                    if (!dup) normals.Add(n);
                                }
                            }
                        }
                        else if (s.IsCylinder()) res.Cylindrical++;
                        else if (s.IsCone()) res.Conical++;
                        else if (s.IsSphere()) res.Spherical++;
                        else res.Other++;
                    }
                }
            }
            catch (Exception ex) { res.Error = "Face read failed (" + ex.GetType().Name + ")."; return res; }
            res.DistinctPlaneOrientations = normals.Count;

            await emit("Surveyor", null, "done",
                res.Total + " faces · " + res.Planar + " planar (" + res.DistinctPlaneOrientations + " orientations) · " + res.Cylindrical + " cylindrical");
            if (res.Total == 0) { res.Error = "No faces (an empty part)."; return res; }

            res.Info = res.Total + " faces: " + res.Planar + " planar (" + res.DistinctPlaneOrientations + " distinct orientations), " +
                       res.Cylindrical + " cylindrical" + (res.Conical > 0 ? ", " + res.Conical + " conical" : "") +
                       (res.Spherical > 0 ? ", " + res.Spherical + " spherical" : "") + (res.Other > 0 ? ", " + res.Other + " other" : "") + ".";
            return res;
        }
    }
}
