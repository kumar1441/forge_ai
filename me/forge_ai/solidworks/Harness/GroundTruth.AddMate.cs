using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT mate census for the add_*_mate family — shares NO code with AddConcentricMate. Its own
        // Mates-folder traversal counting the TOTAL mate count and, per type, how many are concentric. The harness
        // proves (run0 vs run1) the total rose by exactly 1 AND the concentric count rose by exactly 1 (a real
        // concentric mate was created, not some other feature); run2 == run1 proves idempotency.
        public static JObject MeasureAddMate(IModelDoc2 model)
        {
            var res = new JObject();
            if (model as AssemblyDoc == null) { res["error"] = "not an assembly"; return res; }
            int total = 0, concentric = 0, coincident = 0, parallel = 0, angle = 0, distance = 0;
            // applied VALUES, read INDEPENDENTLY off the mate feature-data — proves add_distance_mate / add_angle_mate
            // wrote the requested number, not just a mate of the right type.
            var distValsMm = new JArray();
            var angValsDeg = new JArray();
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
                            try
                            {
                                var mate = s.GetSpecificFeature2() as Mate2;
                                if (mate != null)
                                {
                                    switch ((swMateType_e)mate.Type)
                                    {
                                        case swMateType_e.swMateCONCENTRIC: concentric++; break;
                                        case swMateType_e.swMateCOINCIDENT: coincident++; break;
                                        case swMateType_e.swMatePARALLEL: parallel++; break;
                                        case swMateType_e.swMateANGLE:
                                            angle++;
                                            try { var ad = s.GetDefinition() as IAngleMateFeatureData; if (ad != null) angValsDeg.Add(Math.Round(ad.Angle * 180.0 / Math.PI, 4)); } catch { }
                                            break;
                                        case swMateType_e.swMateDISTANCE:
                                            distance++;
                                            try { var dd = s.GetDefinition() as IDistanceMateFeatureData; if (dd != null) distValsMm.Add(Math.Round(dd.Distance * 1000.0, 4)); } catch { }
                                            break;
                                    }
                                }
                            }
                            catch { }
                            s = s.GetNextSubFeature() as Feature;
                        }
                    }
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch { }
            res["totalMates"] = total;
            res["concentricMates"] = concentric;
            res["coincidentMates"] = coincident;
            res["parallelMates"] = parallel;
            res["angleMates"] = angle;
            res["distanceMates"] = distance;
            res["distanceValuesMm"] = distValsMm;
            res["angleValuesDeg"] = angValsDeg;
            return res;
        }
    }
}
