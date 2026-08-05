using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT feature-type census + hole sizing — shares NO code with FindFeaturesByType.
        //   census: its own tree walk with its own type-name mapping (holeFeatures / bossFeatures / filletFeatures)
        //   sizing: measured from BODY CYLINDRICAL FACES (resulting geometry), whereas the handler reads the feature's
        //           consumed SKETCH (parametric intent). Two genuinely different paths that must land on the same
        //           number — a shared heuristic could agree on a wrong answer, geometry-vs-sketch cannot.
        public static JObject MeasureFindFeaturesByType(IModelDoc2 model)
        {
            var res = new JObject();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART) { res["totalFeatures"] = 0; return res; }

            int total = 0, holes = 0, bosses = 0, fillets = 0, chamfers = 0;
            var seen = new JArray();
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    string tn = null; try { tn = f.GetTypeName2(); } catch { }
                    if (!string.IsNullOrEmpty(tn) && !IsScaffold(tn))
                    {
                        total++;
                        string nm = null; try { nm = f.Name; } catch { }
                        seen.Add(nm + "|" + tn);
                        if (tn.IndexOf("Fillet", StringComparison.OrdinalIgnoreCase) >= 0) fillets++;
                        else if (tn.IndexOf("Chamfer", StringComparison.OrdinalIgnoreCase) >= 0) chamfers++;
                        else if (tn.IndexOf("HoleWzd", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 tn.Equals("ICE", StringComparison.OrdinalIgnoreCase) ||
                                 tn.IndexOf("Cut", StringComparison.OrdinalIgnoreCase) >= 0) holes++;
                        else if (tn.IndexOf("Extrusion", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 tn.IndexOf("Boss", StringComparison.OrdinalIgnoreCase) >= 0) bosses++;
                    }
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch { }

            double minDia = -1; int cylFaces = 0;
            try
            {
                var bodies = (model as PartDoc).GetBodies2((int)swBodyType_e.swSolidBody, false) as object[];
                foreach (var bo in bodies ?? new object[0])
                {
                    var body = bo as Body2; if (body == null) continue;
                    foreach (var fo in (body.GetFaces() as object[]) ?? new object[0])
                    {
                        var face = fo as Face2; if (face == null) continue;
                        var s = face.GetSurface() as Surface; if (s == null) continue;
                        bool cyl = false; try { cyl = s.IsCylinder(); } catch { }
                        if (!cyl) continue;
                        double[] p = null; try { p = s.CylinderParams as double[]; } catch { }
                        if (p == null || p.Length < 7) continue;
                        cylFaces++;
                        double d = p[6] * 2000.0;
                        if (d > 0 && (minDia < 0 || d < minDia)) minDia = d;
                    }
                }
            }
            catch { }

            res["featureTypes"] = seen;   // diagnostic: name|GetTypeName2 for every scanned feature
            res["totalFeatures"] = total;
            res["holeFeatures"] = holes;
            res["bossFeatures"] = bosses;
            res["filletFeatures"] = fillets;
            res["chamferFeatures"] = chamfers;
            res["cylFaceCount"] = cylFaces;
            res["minCylDiaMm"] = minDia;
            return res;
        }

        private static bool IsScaffold(string tn)
        {
            if (tn.EndsWith("Folder", StringComparison.OrdinalIgnoreCase)) return true;   // 11 empty container folders on this build
            switch (tn)
            {
                case "RefPlane": case "RefAxis": case "OriginProfileFeature": case "CoordSys":
                case "DetailCabinet": case "HistoryFolder": case "SketchBlockDef": return true;
                default: return false;
            }
        }
    }
}
