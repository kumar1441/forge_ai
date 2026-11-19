using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT sheet-metal measurement — touches NO sheet-metal API at all. The handler reads the thickness
        // parameter off the sheet-metal feature's definition; this measures the PHYSICAL WALL: take the largest planar
        // face, find the planar face parallel to it, and return the smallest non-zero separation along the normal.
        // That is the sheet thickness by construction, so a feature parameter that has drifted from the solid (or an
        // API that returns a stale/zero value) shows up as a disagreement instead of being self-certified.
        public static JObject MeasureGetSheetMetalProps(IModelDoc2 model)
        {
            var res = new JObject();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART) { res["wallThicknessMm"] = -1; return res; }

            double wall = -1; int planar = 0; int smFeatures = 0;
            try
            {
                // own feature census for the sheet-metal FLAG only (counts, not parameters)
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    string tn = null; try { tn = f.GetTypeName2(); } catch { }
                    if (!string.IsNullOrEmpty(tn) &&
                        (tn.IndexOf("SheetMetal", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         tn.IndexOf("FlatPattern", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         tn.IndexOf("SMBaseFlange", StringComparison.OrdinalIgnoreCase) >= 0)) smFeatures++;
                    f = f.GetNextFeature() as Feature;
                }

                var planes = new List<double[]>();   // nx,ny,nz,px,py,pz,area
                var bodies = (model as PartDoc).GetBodies2((int)swBodyType_e.swSolidBody, false) as object[];
                foreach (var bo in bodies ?? new object[0])
                {
                    var body = bo as Body2; if (body == null) continue;
                    foreach (var fo in (body.GetFaces() as object[]) ?? new object[0])
                    {
                        var face = fo as Face2; if (face == null) continue;
                        var s = face.GetSurface() as Surface; if (s == null) continue;
                        bool isPlane = false; try { isPlane = s.IsPlane(); } catch { }
                        if (!isPlane) continue;
                        double[] pp = null; try { pp = s.PlaneParams as double[]; } catch { }
                        if (pp == null || pp.Length < 6) continue;
                        double area = 0; try { area = face.GetArea(); } catch { }
                        planar++;
                        planes.Add(new[] { pp[0], pp[1], pp[2], pp[3], pp[4], pp[5], area });
                    }
                }

                double[] big = null;
                foreach (var p in planes) if (big == null || p[6] > big[6]) big = p;
                if (big != null)
                {
                    foreach (var p in planes)
                    {
                        double dot = big[0] * p[0] + big[1] * p[1] + big[2] * p[2];
                        if (Math.Abs(dot) < 0.999) continue;                       // not parallel to the big face
                        double dx = p[3] - big[3], dy = p[4] - big[4], dz = p[5] - big[5];
                        double sep = Math.Abs(dx * big[0] + dy * big[1] + dz * big[2]) * 1000.0;
                        if (sep > 1e-6 && (wall < 0 || sep < wall)) wall = sep;
                    }
                }
            }
            catch { }

            res["wallThicknessMm"] = wall;
            res["planarFaces"] = planar;
            res["sheetMetalFeatures"] = smFeatures;
            return res;
        }
    }
}
