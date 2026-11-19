using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT coordinate-system census for create_coordinate_system (tool 167).
        //
        // CROSSED, not parallel: the handler finds and verifies a coordinate system through
        // IModelDocExtension.GetCoordinateSystemTransformByName(name) — a DOCUMENT-level lookup keyed on the name it
        // just set. This walks the feature tree instead and asks each feature for its DEFINITION
        // (IFeature.GetDefinition() -> ICoordinateSystemFeatureData) and reads .Transform off the feature data. Neither
        // side can confirm its own API: if the name lookup returns a stale or wrong frame, the definition disagrees.
        //
        // The observed GetTypeName2() is published RAW for every feature whose definition IS a coordinate system —
        // the whole codebase assumes that string is "CoordSys" and no fixture has ever contained one to prove it
        // (the ICE lesson: never let handler and ground truth share a guessed type name).
        public static JObject MeasureCreateCoordSys(IModelDoc2 model)
        {
            var res = new JObject();
            var rows = new JArray();
            int total = 0, typeNameCoordSys = 0;
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    total++;
                    string nm = null, tn = null;
                    try { nm = f.Name; } catch { }
                    try { tn = f.GetTypeName2(); } catch { }

                    object def = null;
                    try { def = f.GetDefinition(); } catch { }
                    var csd = def as CoordinateSystemFeatureData;
                    if (csd != null)
                    {
                        var row = new JObject();
                        row["name"] = nm;
                        row["typeName"] = tn;
                        if (string.Equals(tn, "CoordSys", StringComparison.Ordinal)) typeNameCoordSys++;

                        double[] t = TranslationMm(csd);
                        if (t != null) { row["xMm"] = t[0]; row["yMm"] = t[1]; row["zMm"] = t[2]; }
                        else { row["xMm"] = null; row["yMm"] = null; row["zMm"] = null; }
                        rows.Add(row);
                    }
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch (Exception ex) { res["error"] = ex.GetType().Name + ": " + ex.Message; }

            int rebuildErrors = 0; try { rebuildErrors = model.Extension.GetWhatsWrongCount(); } catch { }

            res["rows"] = rows;
            res["count"] = rows.Count;
            res["typeNameCoordSys"] = typeNameCoordSys;   // how many of them actually report the assumed type string
            res["totalFeatures"] = total;
            res["rebuildErrors"] = rebuildErrors;
            return res;
        }

        private static double[] TranslationMm(CoordinateSystemFeatureData csd)
        {
            MathTransform mt = null;
            try { mt = csd.Transform as MathTransform; } catch { }
            if (mt == null) return null;
            double[] arr = null;
            try { arr = mt.ArrayData as double[]; } catch { }
            if (arr == null || arr.Length < 12) return null;
            return new double[] { arr[9] * 1000.0, arr[10] * 1000.0, arr[11] * 1000.0 };
        }
    }
}
