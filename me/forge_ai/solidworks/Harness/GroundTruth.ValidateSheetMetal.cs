using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT bend-geometry measurement — no sheet-metal API. The handler reads the bend radius off the
        // sheet-metal feature definition; this derives it from the SOLID, and the two must agree before any
        // radius-vs-thickness verdict is worth anything.
        //
        // Discriminating a BEND from a HOLE without asking SolidWorks: a bend is the only feature that produces a
        // CONCENTRIC PAIR of cylindrical faces exactly one sheet thickness apart — the inside and outside of the same
        // fold. A hole is a lone cylinder; a corner fillet has no partner offset by the thickness. The smaller radius
        // of such a pair IS the bend radius.
        //
        // (Rejected first: "a bend's axis lies in the sheet plane, a hole's pierces it." That reported 48 bends on a
        // 4-bend bracket — the part has flanges at 90 degrees, so holes drilled through a SIDE flange have axes lying
        // in the BASE wall's plane and passed the test. A multi-flange part has no single sheet plane to test against.)
        public static JObject MeasureValidateSheetMetal(IModelDoc2 model)
        {
            var res = new JObject();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART) { res["bendRadiusMm"] = -1; return res; }

            double bendR = -1, wall = -1;
            int bendPairs = 0, cylCount = 0;
            try
            {
                var planes = new List<double[]>();   // nx,ny,nz,px,py,pz,area
                var cyls = new List<double[]>();     // ox,oy,oz,ax,ay,az,radius (metres)

                var bodies = (model as PartDoc).GetBodies2((int)swBodyType_e.swSolidBody, false) as object[];
                foreach (var bo in bodies ?? new object[0])
                {
                    var body = bo as Body2; if (body == null) continue;
                    foreach (var fo in (body.GetFaces() as object[]) ?? new object[0])
                    {
                        var face = fo as Face2; if (face == null) continue;
                        var s = face.GetSurface() as Surface; if (s == null) continue;
                        bool isPlane = false, isCyl = false;
                        try { isPlane = s.IsPlane(); } catch { }
                        try { isCyl = s.IsCylinder(); } catch { }
                        if (isPlane)
                        {
                            double[] pp = null; try { pp = s.PlaneParams as double[]; } catch { }
                            if (pp == null || pp.Length < 6) continue;
                            double a = 0; try { a = face.GetArea(); } catch { }
                            planes.Add(new[] { pp[0], pp[1], pp[2], pp[3], pp[4], pp[5], a });
                        }
                        else if (isCyl)
                        {
                            double[] cp = null; try { cp = s.CylinderParams as double[]; } catch { }
                            if (cp == null || cp.Length < 7) continue;
                            cyls.Add(new[] { cp[0], cp[1], cp[2], cp[3], cp[4], cp[5], cp[6] });
                            cylCount++;
                        }
                    }
                }

                // sheet thickness = smallest separation between the largest planar face and a face parallel to it
                double[] big = null;
                foreach (var p in planes) if (big == null || p[6] > big[6]) big = p;
                if (big != null)
                    foreach (var p in planes)
                    {
                        if (Math.Abs(big[0] * p[0] + big[1] * p[1] + big[2] * p[2]) < 0.999) continue;
                        double sep = Math.Abs((p[3] - big[3]) * big[0] + (p[4] - big[4]) * big[1] + (p[5] - big[5]) * big[2]) * 1000.0;
                        if (sep > 1e-6 && (wall < 0 || sep < wall)) wall = sep;
                    }

                // a bend = two concentric cylinders whose radii differ by exactly the sheet thickness
                if (wall > 0)
                    for (int i = 0; i < cyls.Count; i++)
                        for (int j = i + 1; j < cyls.Count; j++)
                        {
                            var a = cyls[i]; var b = cyls[j];
                            if (Math.Abs(a[3] * b[3] + a[4] * b[4] + a[5] * b[5]) < 0.999) continue;   // axes parallel?
                            if (AxisGap(a, b) > 1e-5) continue;                                        // and collinear?
                            double ra = a[6] * 1000.0, rb = b[6] * 1000.0;
                            if (Math.Abs(Math.Abs(ra - rb) - wall) > 0.05) continue;                    // one thickness apart?
                            bendPairs++;
                            double inner = Math.Min(ra, rb);
                            if (inner > 0 && (bendR < 0 || inner < bendR)) bendR = inner;
                        }
            }
            catch { }

            int rebuildErrors = -1;
            try { rebuildErrors = model.Extension.GetWhatsWrongCount(); } catch { }

            res["bendRadiusMm"] = bendR;
            res["bendCylFaces"] = bendPairs;
            res["cylFaces"] = cylCount;
            res["wallThicknessMm"] = wall;
            res["rebuildErrors"] = rebuildErrors;
            return res;
        }

        // distance from b's axis point to a's axis line — zero means the two cylinders share an axis
        private static double AxisGap(double[] a, double[] b)
        {
            double dx = b[0] - a[0], dy = b[1] - a[1], dz = b[2] - a[2];
            double t = dx * a[3] + dy * a[4] + dz * a[5];
            double px = dx - t * a[3], py = dy - t * a[4], pz = dz - t * a[5];
            return Math.Sqrt(px * px + py * py + pz * pz);
        }
    }
}
