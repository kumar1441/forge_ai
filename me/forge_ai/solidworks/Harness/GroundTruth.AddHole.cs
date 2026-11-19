using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground-truth for the add_hole handler. Shares NO code with AddHole.cs.
    ///
    /// Drilling a through-hole is a WRITE that REMOVES material and ADDS a bore face, so — like the other write handlers
    /// — the harness compares a BASELINE read (run0, before any hole) against the post-write read (run1) and asserts the
    /// geometry changed the way a real hole must:
    ///
    ///   1. volumeMm3 DROPS               (material removed by the cut — the defining act of a hole)
    ///   2. cylindricalFaceCount RISES    (a new internal cylindrical bore face appears)
    ///   3. hasForgeHole is TRUE on run1  (a feature literally named 'Forge-Hole' now exists)
    ///   4. rebuildErrors == 0            (the cut rebuilt clean)
    /// and the rerun is idempotent (run2 == run1 — no second hole stacked).
    ///
    /// Every number here is re-derived from scratch through a DIFFERENT SolidWorks path than the handler's: the handler
    /// measures volume with the whole-doc mass-property engine (IModelDocExtension.CreateMassProperty), while this ground
    /// truth sums per-body IBody2.GetMassProperties across the solid bodies — so agreement on the drop is a genuine
    /// cross-check, not a mirror of the handler's own math. The cylindrical-face count is an independent surface scan;
    /// the bbox diagonal is from IBody2.GetBodyBox. hasSolid lets the grader demand an honest handler refusal on a
    /// body-less part.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureAddHole(ISldWorks app, IModelDoc2 model)
        {
            var mo = new JObject();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { mo["applicable"] = false; mo["reason"] = "active doc is not a part"; return mo; }
            mo["applicable"] = true;

            var part = model as PartDoc;
            int bodyCount = 0, faceCount = 0, cylFaceCount = 0;
            double volM3 = 0;
            double[] bbox = null;
            var cylAxes = new System.Collections.Generic.List<double[]>();   // each bore's [ax,ay,az] unit direction
            double maxCylRadiusMm = 0;
            if (part != null)
            {
                object[] bodies = null;
                try { bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]; } catch { }
                foreach (var bo in bodies ?? new object[0])
                {
                    var body = bo as Body2; if (body == null) continue;
                    bodyCount++;
                    bbox = AhUnionBox(bbox, AhBodyBox(body));

                    object[] faces = null; try { faces = body.GetFaces() as object[]; } catch { }
                    foreach (var fo in faces ?? new object[0])
                    {
                        var face = fo as Face2; if (face == null) continue;
                        faceCount++;
                        Surface s = null; try { s = face.GetSurface() as Surface; } catch { }
                        bool cyl = false; try { cyl = s != null && s.IsCylinder(); } catch { }
                        if (cyl)
                        {
                            cylFaceCount++;
                            // CylinderParams = [originX,Y,Z, axisX,Y,Z, radius] — the bore's own axis direction AND
                            // radius, read via a DIFFERENT surface-query path than AddHole.cs's own bore-radius check.
                            double[] cp = null; try { cp = s.CylinderParams as double[]; } catch { }
                            if (cp != null && cp.Length >= 6) cylAxes.Add(new[] { cp[3], cp[4], cp[5] });
                            if (cp != null && cp.Length >= 7) maxCylRadiusMm = Math.Max(maxCylRadiusMm, cp[6] * 1000.0);
                        }
                    }

                    // independent volume: per-body mass properties. GetMassProperties(density) returns
                    // [0..2]=COM, [3]=Volume, [4]=Area, [5]=Mass, ... — a different path than the handler's whole-doc engine.
                    double[] mp = null; try { mp = body.GetMassProperties(1.0) as double[]; } catch { }
                    if (mp != null && mp.Length >= 4) volM3 += mp[3];
                }
            }

            // does any bore's axis align with the part's LONGEST bounding-box dimension (>0.9 |dot| with that axis'
            // unit vector)? On a tall/narrow part this is only true for a hole drilled from the TOP/BOTTOM cap — a
            // hole drilled from a SIDE face runs perpendicular to the long axis instead. Independent of which face
            // AddHole.cs itself claims it picked; a genuine cross-check for "top"/"bottom" face-resolution asks.
            bool cylAlignsLongAxis = false;
            if (bbox != null && bbox.Length >= 6 && cylAxes.Count > 0)
            {
                double dx = Math.Abs(bbox[3] - bbox[0]), dy = Math.Abs(bbox[4] - bbox[1]), dz = Math.Abs(bbox[5] - bbox[2]);
                int longAxis = (dx >= dy && dx >= dz) ? 0 : (dy >= dz ? 1 : 2);
                foreach (var ax in cylAxes)
                {
                    double len = Math.Sqrt(ax[0] * ax[0] + ax[1] * ax[1] + ax[2] * ax[2]);
                    if (len < 1e-9) continue;
                    double comp = Math.Abs(ax[longAxis]) / len;
                    if (comp > 0.9) { cylAlignsLongAxis = true; break; }
                }
            }

            bool hasForgeHole = false;
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                string nm = null; try { nm = f.Name; } catch { }
                if (string.Equals(nm, "Forge-Hole", StringComparison.OrdinalIgnoreCase)) { hasForgeHole = true; break; }
                f = f.GetNextFeature() as Feature;
            }

            int rebuild = 0; try { rebuild = model.Extension.GetWhatsWrongCount(); } catch { }
            double volMm3 = volM3 * 1e9;   // m^3 -> mm^3

            mo["bodyCount"] = bodyCount;
            mo["faceCount"] = faceCount;
            mo["cylindricalFaceCount"] = cylFaceCount;
            mo["volumeMm3"] = volMm3;
            mo["bboxDiagMm"] = AhBoxDiagMm(bbox);
            mo["hasForgeHole"] = hasForgeHole;   // does a feature named 'Forge-Hole' exist?
            mo["rebuildErrors"] = rebuild;
            mo["hasSolid"] = bodyCount > 0;      // no solid => the handler MUST refuse honestly, not fake a hole
            mo["cylAlignsLongAxis"] = cylAlignsLongAxis;   // a bore runs along the part's longest dimension (cap-drilled)
            mo["maxCylRadiusMm"] = maxCylRadiusMm;         // largest bore radius found — independent cross-check of the actual drilled diameter

            // change fingerprint the grader diffs run0 -> run1 (volume DOWN, cyl faces UP, hole present); idempotent run2==run1
            var fp = new JObject();
            fp["faceCount"] = faceCount;
            fp["volumeMm3"] = volMm3;
            mo["fingerprint"] = fp;
            return mo;
        }

        private static double[] AhBodyBox(Body2 body)
        { try { return body.GetBodyBox() as double[]; } catch { return null; } }

        private static double[] AhUnionBox(double[] acc, double[] b)
        {
            if (b == null || b.Length < 6) return acc;
            if (acc == null) return new[] { b[0], b[1], b[2], b[3], b[4], b[5] };
            return new[]
            {
                Math.Min(acc[0], b[0]), Math.Min(acc[1], b[1]), Math.Min(acc[2], b[2]),
                Math.Max(acc[3], b[3]), Math.Max(acc[4], b[4]), Math.Max(acc[5], b[5])
            };
        }

        private static double AhBoxDiagMm(double[] b)
        {
            if (b == null || b.Length < 6) return 0;
            double dx = b[3] - b[0], dy = b[4] - b[1], dz = b[5] - b[2];
            return Math.Sqrt(dx * dx + dy * dy + dz * dz) * 1000.0;
        }
    }
}
