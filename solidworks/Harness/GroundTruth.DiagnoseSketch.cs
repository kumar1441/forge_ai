using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT sketch census for diagnose_sketch. The entity count is taken from a DIFFERENT API family than the
        // handler's: the handler enumerates GetSketchSegments() and inspects each object; this asks the sketch for its
        // TYPED counts (GetLineCount2 + GetArcCount + GetEllipseCount + GetSplineCount + GetParabolaCount). Two routes
        // to "how many entities are in this sketch" that share no code — if the handler's enumeration silently drops a
        // segment type, the totals disagree instead of agreeing on a wrong number.
        public static JObject MeasureDiagnoseSketch(IModelDoc2 model)
        {
            var res = new JObject();
            var rows = new JArray();
            int under = 0, full = 0;
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { res["sketches"] = rows; res["count"] = 0; return res; }
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    string tn = null; try { tn = f.GetTypeName2(); } catch { }
                    if (string.Equals(tn, "ProfileFeature", StringComparison.OrdinalIgnoreCase))
                    {
                        string nm = null; try { nm = f.Name; } catch { }
                        Sketch sk = null; try { sk = f.GetSpecificFeature2() as Sketch; } catch { }
                        if (sk != null)
                        {
                            int lines = 0, arcs = 0, ellipses = 0, splines = 0, parabolas = 0, contours = 0, pts = 0, st = -1;
                            try { lines = sk.GetLineCount2(1); } catch { try { lines = sk.GetLineCount(); } catch { } }
                            try { arcs = sk.GetArcCount(); } catch { }
                            try { ellipses = sk.GetEllipseCount(); } catch { }
                            try { int pc = 0; splines = sk.GetSplineCount(ref pc); } catch { }
                            try { parabolas = sk.GetParabolaCount(); } catch { }
                            try { contours = sk.GetSketchContourCount(); } catch { }
                            try { pts = sk.GetSketchPointsCount2(); } catch { }
                            try { st = sk.GetConstrainedStatus(); } catch { }
                            if (st == (int)swConstrainedStatus_e.swFullyConstrained) full++; else under++;
                            rows.Add(new JObject
                            {
                                ["name"] = nm,
                                ["entities"] = lines + arcs + ellipses + splines + parabolas,
                                ["lines"] = lines,
                                ["arcs"] = arcs,
                                ["contours"] = contours,
                                ["points"] = pts,
                                ["status"] = st
                            });
                        }
                    }
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch { }
            res["sketches"] = rows;
            res["count"] = rows.Count;
            res["fullyDefined"] = full;
            res["notFullyDefined"] = under;
            return res;
        }
    }
}
