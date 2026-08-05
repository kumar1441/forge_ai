using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for AddSketchRelation. Shares no code with the handler: recurses into EVERY
    /// feature AND its sub-features (the handler only re-checks the single feature it just exited) looking for the
    /// "Forge-SketchRelation" tagged sketch, then re-derives the SAME geometric relationships (angle between the
    /// two lines / length difference / point-to-infinite-line distance / endpoint-to-endpoint distance) via its
    /// OWN separate implementation — not a call into AddSketchRelation.cs.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureAddSketchRelation(IModelDoc2 model)
        {
            var d = new JObject();
            int forgeCount = 0;
            double angleDeg = -1, lenDiffMm = -1, lineDistMm = -1, pointDistMm = -1;
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                AsrWalk(f, ref forgeCount, ref angleDeg, ref lenDiffMm, ref lineDistMm, ref pointDistMm);
                f = f.GetNextFeature() as Feature;
            }
            int rebuildErrors = 0; try { rebuildErrors = model.Extension.GetWhatsWrongCount(); } catch { }

            d["forgeSketchRelationCount"] = forgeCount;
            d["angleDeg"] = angleDeg;
            d["lenDiffMm"] = lenDiffMm;
            d["lineDistMm"] = lineDistMm;
            d["pointDistMm"] = pointDistMm;
            d["rebuildErrors"] = rebuildErrors;
            d["fingerprint"] = new JObject { ["forgeSketchRelationCount"] = forgeCount };
            return d;
        }

        private static void AsrWalk(Feature f, ref int forgeCount, ref double angleDeg, ref double lenDiffMm, ref double lineDistMm, ref double pointDistMm)
        {
            if (f == null) return;
            string nm = null; try { nm = f.Name; } catch { }
            if (nm != null && nm.Equals("Forge-SketchRelation", StringComparison.OrdinalIgnoreCase))
            {
                forgeCount++;
                Sketch sk = null; try { sk = f.GetSpecificFeature2() as Sketch; } catch { }
                if (sk != null)
                {
                    var lines = new List<SketchLine>();
                    foreach (var o in (sk.GetSketchSegments() as object[]) ?? new object[0])
                    {
                        var line = o as SketchLine; if (line == null) continue;
                        var seg = o as SketchSegment;
                        bool constr = false; try { constr = seg != null && seg.ConstructionGeometry; } catch { }
                        if (constr) continue;
                        lines.Add(line);
                        if (lines.Count == 2) break;
                    }
                    if (lines.Count == 2)
                    {
                        var a0 = lines[0].GetStartPoint2() as SketchPoint; var a1 = lines[0].GetEndPoint2() as SketchPoint;
                        var b0 = lines[1].GetStartPoint2() as SketchPoint; var b1 = lines[1].GetEndPoint2() as SketchPoint;
                        if (a0 != null && a1 != null && b0 != null && b1 != null)
                        {
                            double ax = a1.X - a0.X, ay = a1.Y - a0.Y, az = a1.Z - a0.Z;
                            double bx = b1.X - b0.X, by = b1.Y - b0.Y, bz = b1.Z - b0.Z;
                            double la = Math.Sqrt(ax * ax + ay * ay + az * az), lb = Math.Sqrt(bx * bx + by * by + bz * bz);
                            lenDiffMm = Math.Abs(la - lb) * 1000.0;
                            if (la > 1e-9 && lb > 1e-9)
                            {
                                double dot = (ax * bx + ay * by + az * bz) / (la * lb);
                                dot = Math.Max(-1.0, Math.Min(1.0, dot));
                                angleDeg = Math.Acos(Math.Abs(dot)) * 180.0 / Math.PI;
                                double ux = ax / la, uy = ay / la, uz = az / la;
                                lineDistMm = Math.Max(AsrPointLineDist(b0, a0, ux, uy, uz), AsrPointLineDist(b1, a0, ux, uy, uz)) * 1000.0;
                            }
                            pointDistMm = AsrDist(a1, b0) * 1000.0;
                        }
                    }
                }
            }
            var sub = f.GetFirstSubFeature() as Feature;
            while (sub != null) { AsrWalk(sub, ref forgeCount, ref angleDeg, ref lenDiffMm, ref lineDistMm, ref pointDistMm); sub = sub.GetNextSubFeature() as Feature; }
        }

        private static double AsrPointLineDist(SketchPoint p, SketchPoint a0, double ux, double uy, double uz)
        {
            double px = p.X - a0.X, py = p.Y - a0.Y, pz = p.Z - a0.Z;
            double cx = py * uz - pz * uy, cy = pz * ux - px * uz, cz = px * uy - py * ux;
            return Math.Sqrt(cx * cx + cy * cy + cz * cz);
        }

        private static double AsrDist(SketchPoint a, SketchPoint b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }
    }
}
