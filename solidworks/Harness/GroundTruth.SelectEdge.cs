using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for select_edge (tool 14). Same non-negotiable as select_face's GT: never
    /// re-reads live selection (the harness's ForceRebuild3 between handler-run and GT-measure drops it).
    /// Independence axis is TRAVERSAL: derives the edge set by walking FACES (IFace2.GetEdges(), per-face)
    /// and unioning/deduping across every face, instead of the handler's single whole-body IBody2.GetEdges()
    /// array call — a genuinely different API entry point, same spirit as select_face's linked-list-vs-array
    /// split. Shared edges between two adjacent faces collapse to one via a geometry-keyed dedup (length +
    /// endpoints), not object identity (COM references from two different calls aren't guaranteed ==).
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureSelectEdge(IModelDoc2 model)
        {
            var mo = new JObject();
            var part = model as PartDoc;
            if (part == null) { mo["error"] = "not a part"; return mo; }

            object[] bodies = null; try { bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]; } catch { }

            var seen = new HashSet<string>();
            double longestAny = -1, shortestAny = -1, longestLine = -1, shortestLine = -1, longestCircle = -1, shortestCircle = -1;
            int lineCount = 0, circleCount = 0, total = 0;

            foreach (var bo in bodies ?? new object[0])
            {
                var body = bo as Body2; if (body == null) continue;
                object[] faces = null; try { faces = body.GetFaces() as object[]; } catch { }
                foreach (var fo in faces ?? new object[0])
                {
                    var face = fo as Face2; if (face == null) continue;
                    object[] eos = null; try { eos = face.GetEdges() as object[]; } catch { }
                    foreach (var eo in eos ?? new object[0])
                    {
                        var edge = eo as Edge; if (edge == null) continue;
                        double[] cp = null; try { cp = edge.GetCurveParams2() as double[]; } catch { }
                        if (cp == null || cp.Length < 8) continue;
                        double lenMm = SelectEdge.EdgeLengthMm(edge);
                        if (lenMm <= 0) continue;
                        string key = Math.Round(lenMm, 2) + "|" + Math.Round(cp[0], 4) + "," + Math.Round(cp[1], 4) + "," + Math.Round(cp[2], 4) +
                                     "|" + Math.Round(cp[3], 4) + "," + Math.Round(cp[4], 4) + "," + Math.Round(cp[5], 4);
                        string keyRev = Math.Round(lenMm, 2) + "|" + Math.Round(cp[3], 4) + "," + Math.Round(cp[4], 4) + "," + Math.Round(cp[5], 4) +
                                        "|" + Math.Round(cp[0], 4) + "," + Math.Round(cp[1], 4) + "," + Math.Round(cp[2], 4);
                        if (seen.Contains(key) || seen.Contains(keyRev)) continue;
                        seen.Add(key);

                        bool isLine = false, isCircle = false;
                        try { var cv = edge.GetCurve() as Curve; if (cv != null) { isLine = cv.IsLine(); isCircle = cv.IsCircle(); } } catch { }

                        total++;
                        if (longestAny < 0 || lenMm > longestAny) longestAny = lenMm;
                        if (shortestAny < 0 || lenMm < shortestAny) shortestAny = lenMm;
                        if (isLine)
                        {
                            lineCount++;
                            if (longestLine < 0 || lenMm > longestLine) longestLine = lenMm;
                            if (shortestLine < 0 || lenMm < shortestLine) shortestLine = lenMm;
                        }
                        if (isCircle)
                        {
                            circleCount++;
                            if (longestCircle < 0 || lenMm > longestCircle) longestCircle = lenMm;
                            if (shortestCircle < 0 || lenMm < shortestCircle) shortestCircle = lenMm;
                        }
                    }
                }
            }

            mo["edgeCount"] = total;
            mo["lineCount"] = lineCount;
            mo["circleCount"] = circleCount;
            mo["longestAnyMm"] = Math.Round(longestAny, 2);
            mo["shortestAnyMm"] = Math.Round(shortestAny, 2);
            mo["longestLineMm"] = Math.Round(longestLine, 2);
            mo["shortestLineMm"] = Math.Round(shortestLine, 2);
            mo["longestCircleMm"] = Math.Round(longestCircle, 2);
            mo["shortestCircleMm"] = Math.Round(shortestCircle, 2);
            return mo;
        }
    }
}
