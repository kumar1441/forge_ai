using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for create_variable_fillet (tool 217). Own feature walk for Forge-VarFillet, own mass
    /// read, and own longest-straight-edge measurement (so the harness can compute the analytic fillet-volume bounds
    /// without trusting the handler's edge pick). A variable 2->8mm fillet must remove material strictly BETWEEN a
    /// constant-2mm and constant-8mm quarter-round fillet on the same edge: removed = (1-pi/4)*r^2*L, so the harness
    /// asserts (1-pi/4)*4*L < (V0-V1) < (1-pi/4)*64*L. Read-only.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureCreateVariableFillet(IModelDoc2 model)
        {
            var mo = new JObject();
            if (model == null) { mo["error"] = "no model"; return mo; }

            bool hasForge = false; string filletType = null;
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    string nm = null; try { nm = f.Name; } catch { }
                    if (nm != null && nm.Equals("Forge-VarFillet", StringComparison.OrdinalIgnoreCase))
                    {
                        hasForge = true;
                        try { filletType = f.GetTypeName2(); } catch { }
                        break;
                    }
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch { }

            double vol = -1;
            try { var mp = model.Extension.CreateMassProperty(); if (mp != null) vol = mp.Volume * 1e9; } catch { }
            int rw = 0; try { rw = model.Extension.GetWhatsWrongCount(); } catch { }

            double longestMm = LongestStraightEdgeMm(model as PartDoc);

            mo["hasForgeVarFillet"] = hasForge;
            mo["filletType"] = filletType;
            mo["volumeMm3"] = vol;
            mo["longestEdgeMm"] = longestMm;
            mo["rebuildErrors"] = rw;
            return mo;
        }

        private static double LongestStraightEdgeMm(PartDoc part)
        {
            if (part == null) return -1;
            double best = -1;
            try
            {
                var bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[];
                foreach (var bo in bodies ?? new object[0])
                {
                    var body = bo as Body2; if (body == null) continue;
                    var edges = body.GetEdges() as object[];
                    foreach (var eo in edges ?? new object[0])
                    {
                        var e = eo as Edge; if (e == null) continue;
                        var curve = e.GetCurve() as Curve; if (curve == null) continue;
                        bool line = false; try { line = curve.IsLine(); } catch { }
                        if (!line) continue;
                        double[] cp = null; try { cp = e.GetCurveParams2() as double[]; } catch { }
                        if (cp == null || cp.Length < 6) continue;
                        double dx = cp[3] - cp[0], dy = cp[4] - cp[1], dz = cp[5] - cp[2];
                        double len = Math.Sqrt(dx * dx + dy * dy + dz * dz) * 1000.0;
                        if (len > best) best = len;
                    }
                }
            }
            catch { }
            return best;
        }
    }
}
