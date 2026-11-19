using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground-truth for the scale_part handler. Shares NO code with ScalePart.cs.
    ///
    /// Scaling is a WRITE that grows/shrinks a solid by a uniform factor, so — unlike the read handlers — the harness
    /// compares a BASELINE read (run0, before any scale) against the post-write read (run1) and asserts the geometry
    /// changed exactly the way a true uniform scale must:
    ///
    ///   1. volumeMm3 ratio (run1/run0) ≈ factor^3   (volume scales as the cube of a linear factor)
    ///   2. bboxDiagMm ratio (run1/run0) ≈ factor     (every linear dimension scales by the factor)
    ///   3. hasScaleFeature is TRUE on run1           (a feature literally named 'Forge-Scale' now exists)
    ///   4. rebuildErrors == 0                         (the scale rebuilt clean)
    ///   ... and run2 == run1 (idempotent — no second scale stacked, factor not cubed again).
    ///
    /// Every number here is re-derived from scratch through a DIFFERENT SolidWorks path than the handler's: the
    /// handler measures volume with the whole-doc mass-property engine (IModelDocExtension.CreateMassProperty), while
    /// this ground truth sums per-body IBody2.GetMassProperties across the solid bodies — so agreement on the ×factor^3
    /// ratio is a genuine cross-check, not a mirror of the handler's own math. surfaceAreaMm2 is reported for context
    /// (it scales as factor^2). The bbox diagonal is from IBody2.GetBodyBox. hasSolid lets the grader demand an honest
    /// handler refusal on a body-less part.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureScalePart(ISldWorks app, IModelDoc2 model)
        {
            var mo = new JObject();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { mo["applicable"] = false; mo["reason"] = "active doc is not a part"; return mo; }
            mo["applicable"] = true;

            var part = model as PartDoc;
            int bodyCount = 0;
            double volM3 = 0, areaM2 = 0;
            double[] bbox = null;
            bool hasSolid = false;
            if (part != null)
            {
                object[] bodies = null;
                try { bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]; } catch { }
                foreach (var bo in bodies ?? new object[0])
                {
                    var body = bo as Body2; if (body == null) continue;
                    bodyCount++;
                    bbox = ScUnionBox(bbox, ScBodyBox(body));
                    // independent volume+area: per-body mass properties. GetMassProperties(density) returns
                    // [0..2]=COM, [3]=Volume, [4]=Area, [5]=Mass, ... — a different path than the handler's whole-doc engine.
                    double[] mp = null; try { mp = body.GetMassProperties(1.0) as double[]; } catch { }
                    if (mp != null && mp.Length >= 5) { volM3 += mp[3]; areaM2 += mp[4]; }
                }
                hasSolid = bodyCount > 0;
            }

            bool hasScale = false;
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                string nm = null; try { nm = f.Name; } catch { }
                if (string.Equals(nm, "Forge-Scale", StringComparison.OrdinalIgnoreCase)) { hasScale = true; break; }
                f = f.GetNextFeature() as Feature;
            }

            int rebuild = 0; try { rebuild = model.Extension.GetWhatsWrongCount(); } catch { }

            double volMm3 = volM3 * 1e9;      // m^3 -> mm^3
            double areaMm2 = areaM2 * 1e6;    // m^2 -> mm^2
            double diagMm = ScBoxDiagMm(bbox);
            double maxDimMm = ScBoxMaxDimMm(bbox);

            mo["bodyCount"] = bodyCount;
            mo["volumeMm3"] = volMm3;
            mo["surfaceAreaMm2"] = areaMm2;
            mo["bboxDiagMm"] = diagMm;
            mo["bboxMaxDimMm"] = maxDimMm;   // largest single bbox axis — mirrors ScalePart.cs BboxMaxDimMm
            mo["hasScaleFeature"] = hasScale;   // does a feature named 'Forge-Scale' exist?
            mo["rebuildErrors"] = rebuild;
            mo["hasSolid"] = hasSolid;          // no solid => the handler MUST refuse honestly, not fake a scale

            // immutability/change fingerprint the grader diffs run0 -> run1 (volume ×factor^3, diagonal ×factor, scale present)
            var fp = new JObject();
            fp["bodyCount"] = bodyCount;
            fp["volumeMm3"] = volMm3;
            fp["bboxDiagMm"] = diagMm;
            mo["fingerprint"] = fp;
            return mo;
        }

        private static double[] ScBodyBox(Body2 body)
        { try { return body.GetBodyBox() as double[]; } catch { return null; } }

        private static double[] ScUnionBox(double[] acc, double[] b)
        {
            if (b == null || b.Length < 6) return acc;
            if (acc == null) return new[] { b[0], b[1], b[2], b[3], b[4], b[5] };
            return new[]
            {
                Math.Min(acc[0], b[0]), Math.Min(acc[1], b[1]), Math.Min(acc[2], b[2]),
                Math.Max(acc[3], b[3]), Math.Max(acc[4], b[4]), Math.Max(acc[5], b[5])
            };
        }

        private static double ScBoxDiagMm(double[] b)
        {
            if (b == null || b.Length < 6) return 0;
            double dx = b[3] - b[0], dy = b[4] - b[1], dz = b[5] - b[2];
            return Math.Sqrt(dx * dx + dy * dy + dz * dz) * 1000.0;
        }

        private static double ScBoxMaxDimMm(double[] b)
        {
            if (b == null || b.Length < 6) return 0;
            double dx = b[3] - b[0], dy = b[4] - b[1], dz = b[5] - b[2];
            return Math.Max(dx, Math.Max(dy, dz)) * 1000.0;
        }
    }
}
