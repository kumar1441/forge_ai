using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT width-mate census for add_width_mate (tool 58). Shares NO code with AddWidthMate. It identifies the
        // TAB as the component with the FEWEST solid faces (a plain block vs the slotted channel), finds the tab's
        // thinnest opposed planar pair to define the width axis, and reads the tab's position along that axis from its
        // Transform2 translation. The harness proves the CENTRING: run0 has the tab OFF-centre and 0 width mates; run1
        // has the tab at the slot centre (~0mm) with exactly 1 width mate; run2 == run1. "Did the tab actually centre"
        // is decided by a transform read that never calls AddMate5.
        public static JObject MeasureAddWidthMate(IModelDoc2 model)
        {
            var res = new JObject();
            var asm = model as AssemblyDoc;
            if (asm == null) { res["error"] = "not an assembly"; return res; }

            // mate census
            int total = 0, width = 0;
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    string tn = ""; try { tn = f.GetTypeName2(); } catch { }
                    if (tn == "MateGroup")
                    {
                        var s = f.GetFirstSubFeature() as Feature;
                        while (s != null)
                        {
                            total++;
                            try { var mate = s.GetSpecificFeature2() as Mate2; if (mate != null && (swMateType_e)mate.Type == swMateType_e.swMateWIDTH) width++; } catch { }
                            s = s.GetNextSubFeature() as Feature;
                        }
                    }
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch { }
            res["totalMates"] = total;
            res["widthMates"] = width;

            // find the tab = component with the fewest solid faces
            Component2 tab = null; int minFaces = int.MaxValue;
            foreach (var o in (asm.GetComponents(true) as object[]) ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                int fc = FaceCountGt(c);
                if (fc > 0 && fc < minFaces) { minFaces = fc; tab = c; }
            }
            if (tab == null) { res["tabFound"] = false; return res; }
            res["tabFound"] = true; try { res["tabName"] = tab.Name2; } catch { }

            double[] axis = ThinnestOpposedAxis(tab);
            res["axis"] = axis == null ? "none" : (Math.Abs(axis[0]) > 0.9 ? "X" : Math.Abs(axis[1]) > 0.9 ? "Y" : "Z");
            double pos = 0;
            try
            {
                var t = tab.Transform2 as MathTransform; var d = t != null ? t.ArrayData as double[] : null;
                if (d != null && d.Length >= 12 && axis != null)
                    pos = (d[9] * axis[0] + d[10] * axis[1] + d[11] * axis[2]) * 1000.0;
            }
            catch { }
            res["tabCenterMm"] = Math.Round(pos, 4);
            return res;
        }

        private static int FaceCountGt(Component2 comp)
        {
            int n = 0;
            try { object bi; var bodies = comp.GetBodies3((int)swBodyType_e.swSolidBody, out bi) as object[];
                  foreach (var bo in bodies ?? new object[0]) { var body = bo as Body2; if (body == null) continue; var fs = body.GetFaces() as object[]; if (fs != null) n += fs.Length; } } catch { }
            return n;
        }

        // axis (assembly space) of the tab's thinnest opposed planar pair
        private static double[] ThinnestOpposedAxis(Component2 comp)
        {
            var normals = new List<double[]>(); var points = new List<double[]>();
            try
            {
                object bi; var bodies = comp.GetBodies3((int)swBodyType_e.swSolidBody, out bi) as object[];
                var t = comp.Transform2 as MathTransform; double[] td = t != null ? t.ArrayData as double[] : null;
                foreach (var bo in bodies ?? new object[0])
                {
                    var body = bo as Body2; if (body == null) continue;
                    foreach (var fo in (body.GetFaces() as object[]) ?? new object[0])
                    {
                        var face = fo as Face2; if (face == null) continue;
                        var surf = face.GetSurface() as Surface; if (surf == null) continue;
                        bool plane = false; try { plane = surf.IsPlane(); } catch { } if (!plane) continue;
                        var pp = surf.PlaneParams as double[]; if (pp == null || pp.Length < 6) continue;
                        normals.Add(RotGt(td, new[] { pp[0], pp[1], pp[2] }));
                        points.Add(TrGt(td, new[] { pp[3], pp[4], pp[5] }));
                    }
                }
            }
            catch { }
            double[] bestAxis = null; double bestSep = double.MaxValue;
            for (int i = 0; i < normals.Count; i++)
                for (int j = i + 1; j < normals.Count; j++)
                {
                    double dot = normals[i][0]*normals[j][0] + normals[i][1]*normals[j][1] + normals[i][2]*normals[j][2];
                    if (dot > -0.98) continue;
                    double[] diff = { points[i][0]-points[j][0], points[i][1]-points[j][1], points[i][2]-points[j][2] };
                    double sep = Math.Abs(diff[0]*normals[i][0] + diff[1]*normals[i][1] + diff[2]*normals[i][2]) * 1000.0;
                    if (sep > 0.5 && sep < bestSep) { bestSep = sep; bestAxis = normals[i]; }
                }
            return bestAxis;
        }
        private static double[] RotGt(double[] td, double[] v)
        {
            if (td == null || td.Length < 9) { double n0 = Math.Sqrt(v[0]*v[0]+v[1]*v[1]+v[2]*v[2]); return n0<1e-12?v:new[]{v[0]/n0,v[1]/n0,v[2]/n0}; }
            double x = td[0]*v[0]+td[3]*v[1]+td[6]*v[2], y = td[1]*v[0]+td[4]*v[1]+td[7]*v[2], z = td[2]*v[0]+td[5]*v[1]+td[8]*v[2];
            double n = Math.Sqrt(x*x+y*y+z*z); return n<1e-12?new[]{x,y,z}:new[]{x/n,y/n,z/n};
        }
        private static double[] TrGt(double[] td, double[] p)
        {
            if (td == null || td.Length < 12) return p;
            double s = td.Length > 12 ? (td[12] == 0 ? 1 : td[12]) : 1;
            return new[] { (td[0]*p[0]+td[3]*p[1]+td[6]*p[2])*s+td[9], (td[1]*p[0]+td[4]*p[1]+td[7]*p[2])*s+td[10], (td[2]*p[0]+td[5]*p[1]+td[8]*p[2])*s+td[11] };
        }
    }
}
