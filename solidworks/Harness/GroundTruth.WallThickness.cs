using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground-truth for the wall-thickness handler (tool #182). Shares NO code with WallThickness.cs.
    ///
    /// A minimum wall thickness is expensive and non-deterministic to reproduce exactly (it depends on the sampling
    /// scheme), so this ground truth deliberately does NOT try to re-derive the handler's number. Instead it
    /// establishes the two things the harness CAN assert cheaply and honestly:
    ///
    ///   1. A read-only structural fingerprint of the PART — body count, face count, feature count, config count,
    ///      the bounding-box diagonal (mm), and the rebuild-error count — captured from scratch here. Across
    ///      run0/run1/run2 this must be byte-for-byte identical: a wall-thickness scan that changed a face, a
    ///      feature, or a config has failed the read-only contract.
    ///   2. A sane BOUND on the handler's answer: any true wall thickness must be > 0 and <= the bbox diagonal.
    ///      The grader checks run1Result.MinThicknessMm against this window (and against the handler's own
    ///      reported BboxDiagMm), proving a minimum was produced and that it lives inside the part.
    ///
    /// The bbox here is computed from IBody2.GetBodyBox unioned across bodies — a different path than the handler's,
    /// so it is a genuine cross-check, not a mirror of the handler's own math.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureWallThickness(ISldWorks app, IModelDoc2 model)
        {
            var mo = new JObject();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { mo["applicable"] = false; mo["reason"] = "active doc is not a part"; return mo; }
            mo["applicable"] = true;

            var part = model as PartDoc;
            int bodyCount = 0, faceCount = 0;
            double[] bbox = null;
            if (part != null)
            {
                object[] bodies = null;
                try { bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]; } catch { }
                foreach (var bo in bodies ?? new object[0])
                {
                    var body = bo as Body2; if (body == null) continue;
                    bodyCount++;
                    bbox = WtUnionBox(bbox, WtBodyBox(body));
                    object[] faces = null; try { faces = body.GetFaces() as object[]; } catch { }
                    if (faces != null) faceCount += faces.Length;
                }
            }

            int featCount = 0;
            var f = model.FirstFeature() as Feature;
            while (f != null) { featCount++; f = f.GetNextFeature() as Feature; }

            int cfgCount = 0;
            try { var c = model.GetConfigurationNames() as string[]; if (c != null) cfgCount = c.Length; } catch { }

            int rebuild = 0; try { rebuild = model.Extension.GetWhatsWrongCount(); } catch { }

            mo["bodyCount"] = bodyCount;
            mo["faceCount"] = faceCount;
            mo["featureCount"] = featCount;
            mo["configCount"] = cfgCount;
            mo["rebuildErrors"] = rebuild;
            mo["bboxDiagMm"] = WtBoxDiagMm(bbox);          // the sane upper bound on any thickness the handler reports
            mo["hasSolid"] = bodyCount > 0;                // Rule #4: no solid => the handler must honestly refuse, not invent a number
            return mo;
        }

        private static double[] WtBodyBox(Body2 body)
        { try { return body.GetBodyBox() as double[]; } catch { return null; } }

        private static double[] WtUnionBox(double[] acc, double[] b)
        {
            if (b == null || b.Length < 6) return acc;
            if (acc == null) return new[] { b[0], b[1], b[2], b[3], b[4], b[5] };
            return new[]
            {
                Math.Min(acc[0], b[0]), Math.Min(acc[1], b[1]), Math.Min(acc[2], b[2]),
                Math.Max(acc[3], b[3]), Math.Max(acc[4], b[4]), Math.Max(acc[5], b[5])
            };
        }

        private static double WtBoxDiagMm(double[] b)
        {
            if (b == null || b.Length < 6) return 0;
            double dx = b[3] - b[0], dy = b[4] - b[1], dz = b[5] - b[2];
            return Math.Sqrt(dx * dx + dy * dy + dz * dz) * 1000.0;
        }
    }
}
