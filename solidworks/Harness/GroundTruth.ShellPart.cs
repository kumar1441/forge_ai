using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground-truth for the shell_part handler. Shares NO code with ShellPart.cs.
    ///
    /// Shelling is a WRITE that hollows a solid, so — unlike the read handlers — the harness compares a BASELINE
    /// read (run0, before any shell) against the post-write read (run1) and asserts the geometry actually changed
    /// the way a real hollow must:
    ///
    ///   1. volumeMm3 DROPS   (material removed from the interior — the defining act of a shell)
    ///   2. surfaceAreaMm2 RISES (a fresh inner wall is added — a hollow has more surface than the solid it came from)
    ///   3. hasShellFeature is TRUE on run1 (a feature literally named 'Forge-Shell' now exists)
    ///
    /// Every number here is re-derived from scratch through a DIFFERENT SolidWorks path than the handler's: the
    /// handler measures volume with the whole-doc mass-property engine (IModelDocExtension.CreateMassProperty),
    /// while this ground truth sums per-body IBody2.GetMassProperties across the solid bodies — so agreement on the
    /// drop is a genuine cross-check, not a mirror of the handler's own math. The bbox diagonal is from
    /// IBody2.GetBodyBox. hasSolid lets the grader demand an honest handler refusal on a body-less part.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureShellPart(ISldWorks app, IModelDoc2 model)
        {
            var mo = new JObject();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { mo["applicable"] = false; mo["reason"] = "active doc is not a part"; return mo; }
            mo["applicable"] = true;

            var part = model as PartDoc;
            int bodyCount = 0, faceCount = 0;
            double volM3 = 0, areaM2 = 0;
            double[] bbox = null;
            if (part != null)
            {
                object[] bodies = null;
                try { bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]; } catch { }
                foreach (var bo in bodies ?? new object[0])
                {
                    var body = bo as Body2; if (body == null) continue;
                    bodyCount++;
                    bbox = SpUnionBox(bbox, SpBodyBox(body));
                    object[] faces = null; try { faces = body.GetFaces() as object[]; } catch { }
                    if (faces != null) faceCount += faces.Length;
                    // independent volume+area: per-body mass properties. GetMassProperties(density) returns
                    // [0..2]=COM, [3]=Volume, [4]=Area, [5]=Mass, ... — a different path than the handler's whole-doc engine.
                    double[] mp = null; try { mp = body.GetMassProperties(1.0) as double[]; } catch { }
                    if (mp != null && mp.Length >= 5) { volM3 += mp[3]; areaM2 += mp[4]; }
                }
            }

            bool hasShell = false;
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                string nm = null; try { nm = f.Name; } catch { }
                if (string.Equals(nm, "Forge-Shell", StringComparison.OrdinalIgnoreCase)) { hasShell = true; break; }
                f = f.GetNextFeature() as Feature;
            }

            int rebuild = 0; try { rebuild = model.Extension.GetWhatsWrongCount(); } catch { }

            double volMm3 = volM3 * 1e9;      // m^3 -> mm^3
            double areaMm2 = areaM2 * 1e6;    // m^2 -> mm^2

            mo["bodyCount"] = bodyCount;
            mo["faceCount"] = faceCount;
            mo["volumeMm3"] = volMm3;
            mo["surfaceAreaMm2"] = areaMm2;
            mo["bboxDiagMm"] = SpBoxDiagMm(bbox);
            mo["hasShellFeature"] = hasShell;   // does a feature named 'Forge-Shell' exist?
            mo["rebuildErrors"] = rebuild;
            mo["hasSolid"] = bodyCount > 0;     // no solid => the handler MUST refuse honestly, not fake a hollow

            // immutability/change fingerprint the grader diffs run0 -> run1 (volume DOWN, faces/area UP, shell present)
            var fp = new JObject();
            fp["bodyCount"] = bodyCount;
            fp["faceCount"] = faceCount;
            fp["volumeMm3"] = volMm3;
            mo["fingerprint"] = fp;
            return mo;
        }

        private static double[] SpBodyBox(Body2 body)
        { try { return body.GetBodyBox() as double[]; } catch { return null; } }

        private static double[] SpUnionBox(double[] acc, double[] b)
        {
            if (b == null || b.Length < 6) return acc;
            if (acc == null) return new[] { b[0], b[1], b[2], b[3], b[4], b[5] };
            return new[]
            {
                Math.Min(acc[0], b[0]), Math.Min(acc[1], b[1]), Math.Min(acc[2], b[2]),
                Math.Max(acc[3], b[3]), Math.Max(acc[4], b[4]), Math.Max(acc[5], b[5])
            };
        }

        private static double SpBoxDiagMm(double[] b)
        {
            if (b == null || b.Length < 6) return 0;
            double dx = b[3] - b[0], dy = b[4] - b[1], dz = b[5] - b[2];
            return Math.Sqrt(dx * dx + dy * dy + dz * dz) * 1000.0;
        }
    }
}
