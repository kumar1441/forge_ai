using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT edge inventory — shares NO code with GetEdges. Its own body/edge traversal + its own length read
        // (endpoint distance for the straight box edges), returning edge count + longest (mm). The seeded block's
        // longest edge is its 80mm length, a KNOWN truth.
        public static JObject MeasureGetEdges(IModelDoc2 model)
        {
            var res = new JObject();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART) { res["edgeCount"] = 0; res["longestMm"] = -1; return res; }
            int count = 0; double longest = -1;
            try
            {
                var bodies = (model as PartDoc).GetBodies2((int)swBodyType_e.swSolidBody, false) as object[];
                foreach (var bo in bodies ?? new object[0])
                {
                    var body = bo as Body2; if (body == null) continue;
                    foreach (var eo in (body.GetEdges() as object[]) ?? new object[0])
                    {
                        var edge = eo as Edge; if (edge == null) continue;
                        var cp = edge.GetCurveParams2() as double[];
                        double len = -1;
                        var curve = edge.GetCurve() as Curve;
                        if (curve != null && cp != null && cp.Length >= 8) { try { len = curve.GetLength3(cp[6], cp[7]); } catch { } }
                        if (len <= 0 && cp != null && cp.Length >= 6)
                        {
                            double dx = cp[3] - cp[0], dy = cp[4] - cp[1], dz = cp[5] - cp[2];
                            len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                        }
                        if (len <= 0) continue;
                        count++;
                        double mm = len * 1000.0;
                        if (mm > longest) longest = mm;
                    }
                }
            }
            catch { }
            res["edgeCount"] = count;
            res["longestMm"] = longest;
            return res;
        }
    }
}
