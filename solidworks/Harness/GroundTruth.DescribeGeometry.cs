using System;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for describe_geometry (tool 237). Never re-reads live SolidWorks selection — same
    /// non-negotiable as select_face/get_selected_entities (the harness's own ForceRebuild3 between the handler's
    /// run and this measurement drops the live selection). Re-parses the SAME embedded pre-select sub-command
    /// from the intent text and independently re-derives what that criterion SHOULD find:
    ///   - planar criteria (top/bottom/left/right/largest) reuse MeasureSelectFace's linked-list area
    ///     re-derivation (GetFirstFace/GetNextFace, not the handler's GetFaces() array) — already proven
    ///     independent of the handler.
    ///   - "hole"/"bore" walks bodies via the SAME linked-list style (not GetFaces()) with its OWN concavity
    ///     test and OWN bbox-onto-axis extent projection — a second, independent implementation of the same
    ///     math DescribeGeometry.cs uses, sharing no code with it.
    /// </summary>
    public static partial class GroundTruth
    {
        private static string DgParsePreSelectCriterion(string intent)
        {
            string c = (intent ?? "").ToLowerInvariant();
            if (Regex.IsMatch(c, @"\b(hole|bore)\b")) return "hole";
            if (!Regex.IsMatch(c, @"\bface\b")) return null;
            if (Regex.IsMatch(c, @"\b(largest|biggest)\b")) return "largest";
            if (Regex.IsMatch(c, @"\btop\b")) return "top";
            if (Regex.IsMatch(c, @"\bbottom\b")) return "bottom";
            if (Regex.IsMatch(c, @"\bleft\b")) return "left";
            if (Regex.IsMatch(c, @"\bright\b")) return "right";
            return null;
        }

        public static JObject MeasureDescribeGeometry(IModelDoc2 model, string intent)
        {
            var mo = new JObject();
            string crit = DgParsePreSelectCriterion(intent);
            mo["preSelectCriterion"] = crit;
            if (crit == null) return mo;

            if (crit == "hole")
            {
                var part = model as PartDoc;
                if (part == null) { mo["error"] = "not a part"; return mo; }
                object[] bodies = null; try { bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]; } catch { }

                double[] unionBox = null;
                foreach (var bo in bodies ?? new object[0])
                {
                    var body = bo as Body2; if (body == null) continue;
                    double[] bb = null; try { bb = body.GetBodyBox() as double[]; } catch { }
                    unionBox = DgUnionBox(unionBox, bb);
                }

                double bestR = -1, bestHeightMm = -1; bool found = false;
                foreach (var bo in bodies ?? new object[0])
                {
                    var body = bo as Body2; if (body == null) continue;
                    Face2 f = null; try { f = body.GetFirstFace() as Face2; } catch { }
                    while (f != null)
                    {
                        Surface surf = null; try { surf = f.GetSurface() as Surface; } catch { }
                        bool isCyl = false; try { isCyl = surf != null && surf.IsCylinder(); } catch { }
                        if (isCyl)
                        {
                            double[] cp = null; try { cp = surf.CylinderParams as double[]; } catch { }
                            if (cp != null && cp.Length >= 7 && DgIsConcave(f, surf, cp))
                            {
                                double diaM = cp[6] * 2.0;
                                bool sizeOk = true;
                                if (unionBox != null && unionBox.Length >= 6)
                                {
                                    double spanX = unionBox[3] - unionBox[0], spanY = unionBox[4] - unionBox[1], spanZ = unionBox[5] - unionBox[2];
                                    double minSpan = Math.Min(spanX, Math.Min(spanY, spanZ));
                                    sizeOk = diaM >= 0.0015 && diaM <= 0.6 * minSpan;
                                }
                                if (sizeOk && cp[6] > bestR)
                                {
                                    bestR = cp[6];
                                    bestHeightMm = DgAxialExtentMm(f, cp);
                                    found = true;
                                }
                            }
                        }
                        Face2 next = null; try { next = f.GetNextFace() as Face2; } catch { }
                        f = next;
                    }
                }
                mo["expectedShapeType"] = found ? "cylindrical" : null;
                mo["expectedDiameterMm"] = found ? (double?)(bestR * 2.0 * 1000.0) : null;
                mo["expectedHeightMm"] = found ? (double?)bestHeightMm : null;
                mo["expectedConcave"] = found ? (bool?)true : null;
                return mo;
            }

            // planar criterion — reuse the SAME independent linked-list area re-derivation select_face's GT proved.
            var sf = MeasureSelectFace(model);
            mo["expectedShapeType"] = "planar";
            string key = "independent" + char.ToUpperInvariant(crit[0]) + crit.Substring(1) + "AreaMm2";
            mo["expectedAreaMm2"] = sf[key];
            return mo;
        }

        private static double[] DgUnionBox(double[] acc, double[] b)
        {
            if (b == null || b.Length < 6) return acc;
            if (acc == null) return new[] { b[0], b[1], b[2], b[3], b[4], b[5] };
            return new[]
            {
                Math.Min(acc[0], b[0]), Math.Min(acc[1], b[1]), Math.Min(acc[2], b[2]),
                Math.Max(acc[3], b[3]), Math.Max(acc[4], b[4]), Math.Max(acc[5], b[5])
            };
        }

        private static double DgAxialExtentMm(Face2 face, double[] cylParams)
        {
            try
            {
                double[] axisO = { cylParams[0], cylParams[1], cylParams[2] };
                double[] axisD = { cylParams[3], cylParams[4], cylParams[5] };
                double dl = Math.Sqrt(axisD[0] * axisD[0] + axisD[1] * axisD[1] + axisD[2] * axisD[2]);
                if (dl < 1e-9) return 0;
                axisD = new[] { axisD[0] / dl, axisD[1] / dl, axisD[2] / dl };
                double[] box = null; try { box = face.GetBox() as double[]; } catch { }
                if (box == null || box.Length < 6) return 0;
                double minT = double.MaxValue, maxT = double.MinValue;
                for (int cx = 0; cx < 2; cx++)
                for (int cy = 0; cy < 2; cy++)
                for (int cz = 0; cz < 2; cz++)
                {
                    double px = cx == 0 ? box[0] : box[3];
                    double py = cy == 0 ? box[1] : box[4];
                    double pz = cz == 0 ? box[2] : box[5];
                    double t = (px - axisO[0]) * axisD[0] + (py - axisO[1]) * axisD[1] + (pz - axisO[2]) * axisD[2];
                    if (t < minT) minT = t;
                    if (t > maxT) maxT = t;
                }
                return (maxT - minT) * 1000.0;
            }
            catch { return 0; }
        }

        private static bool DgIsConcave(Face2 face, Surface surf, double[] cylParams)
        {
            try
            {
                double[] box = null; try { box = face.GetBox() as double[]; } catch { }
                if (box == null || box.Length < 6) return false;
                double[] center = { (box[0] + box[3]) / 2, (box[1] + box[4]) / 2, (box[2] + box[5]) / 2 };
                double[] p = null; try { p = face.GetClosestPointOn(center[0], center[1], center[2]) as double[]; } catch { }
                if (p == null || p.Length < 3) return false;

                double[] axisO = { cylParams[0], cylParams[1], cylParams[2] };
                double[] axisD = { cylParams[3], cylParams[4], cylParams[5] };
                double dl = Math.Sqrt(axisD[0] * axisD[0] + axisD[1] * axisD[1] + axisD[2] * axisD[2]);
                if (dl < 1e-9) return false;
                axisD = new[] { axisD[0] / dl, axisD[1] / dl, axisD[2] / dl };

                double[] v = { p[0] - axisO[0], p[1] - axisO[1], p[2] - axisO[2] };
                double along = v[0] * axisD[0] + v[1] * axisD[1] + v[2] * axisD[2];
                double[] radial = { v[0] - along * axisD[0], v[1] - along * axisD[1], v[2] - along * axisD[2] };
                double rl = Math.Sqrt(radial[0] * radial[0] + radial[1] * radial[1] + radial[2] * radial[2]);
                if (rl < 1e-9) return false;
                radial = new[] { radial[0] / rl, radial[1] / rl, radial[2] / rl };

                double[] n = null; try { n = surf.EvaluateAtPoint(p[0], p[1], p[2]) as double[]; } catch { }
                if (n == null || n.Length < 3) return false;
                double nl = Math.Sqrt(n[0] * n[0] + n[1] * n[1] + n[2] * n[2]);
                if (nl < 1e-9) return false;
                double[] nu = { n[0] / nl, n[1] / nl, n[2] / nl };
                bool reversed = false; try { reversed = face.FaceInSurfaceSense(); } catch { }
                if (reversed) nu = new[] { -nu[0], -nu[1], -nu[2] };

                double dot = nu[0] * radial[0] + nu[1] * radial[1] + nu[2] * radial[2];
                return dot < 0;
            }
            catch { return false; }
        }
    }
}
