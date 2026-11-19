using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT face-type inventory — shares NO code with GetFaces. Its own body/face traversal + surface-type
        // test, returning total/planar/cylindrical. The seeded block (box + one through-hole) has a KNOWN 6 planar +
        // 1 cylindrical face — the harness asserts those.
        public static JObject MeasureGetFaces(IModelDoc2 model)
        {
            var res = new JObject();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART) { res["total"] = 0; return res; }
            int total = 0, planar = 0, cyl = 0;
            try
            {
                var bodies = (model as PartDoc).GetBodies2((int)swBodyType_e.swSolidBody, false) as object[];
                foreach (var bo in bodies ?? new object[0])
                {
                    var body = bo as Body2; if (body == null) continue;
                    foreach (var fo in (body.GetFaces() as object[]) ?? new object[0])
                    {
                        var face = fo as Face2; if (face == null) continue;
                        total++;
                        var s = face.GetSurface() as Surface; if (s == null) continue;
                        if (s.IsPlane()) planar++;
                        else if (s.IsCylinder()) cyl++;
                    }
                }
            }
            catch { }
            res["total"] = total;
            res["planar"] = planar;
            res["cylindrical"] = cyl;
            return res;
        }
    }
}
