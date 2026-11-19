using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for validate_scale_sanity (tool 254) — shares NO code with ValidateScaleSanity.cs.
    /// The handler reads the box via IBody2.GetBodyBox (the analytic box). This GT re-derives it a DIFFERENT way — the
    /// min/max over every solid body's VERTEX points (IBody2.GetVertices -> Vertex.GetPoint) — and applies the same 1 m
    /// ceiling to derive the expected verdict. For a flat-faced block the two APIs agree exactly, so a disagreement would
    /// expose a bad box read rather than a bad rule. Read-only. Known truth:
    ///   normal 80x40x20 block  -> maxDimMm ~80    => sane
    ///   giant 2032x1016x508    -> maxDimMm ~2032  => review-scale, /25.4 -> 80.0mm
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureValidateScaleSanity(IModelDoc2 model)
        {
            var d = new JObject();
            var part = model as PartDoc;
            if (part == null) { d["applicable"] = false; d["reason"] = "not a part"; return d; }
            d["applicable"] = true;

            double xmin = double.MaxValue, ymin = double.MaxValue, zmin = double.MaxValue;
            double xmax = double.MinValue, ymax = double.MinValue, zmax = double.MinValue;
            int verts = 0;
            try
            {
                var bs = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[];
                foreach (var o in bs ?? new object[0])
                {
                    var b = o as Body2; if (b == null) continue;
                    var vs = b.GetVertices() as object[];
                    foreach (var vo in vs ?? new object[0])
                    {
                        var v = vo as Vertex; if (v == null) continue;
                        double[] p = null; try { p = v.GetPoint() as double[]; } catch { }
                        if (p == null || p.Length < 3) continue;
                        verts++;
                        if (p[0] < xmin) xmin = p[0]; if (p[0] > xmax) xmax = p[0];
                        if (p[1] < ymin) ymin = p[1]; if (p[1] > ymax) ymax = p[1];
                        if (p[2] < zmin) zmin = p[2]; if (p[2] > zmax) zmax = p[2];
                    }
                }
            }
            catch { }

            bool hasSolid = verts > 0 && xmax > xmin;
            d["hasSolid"] = hasSolid;
            d["vertexCount"] = verts;
            if (!hasSolid) return d;

            double dx = (xmax - xmin) * 1000.0, dy = (ymax - ymin) * 1000.0, dz = (zmax - zmin) * 1000.0;
            double maxMm = Math.Max(dx, Math.Max(dy, dz));
            d["dxMm"] = dx; d["dyMm"] = dy; d["dzMm"] = dz;
            d["maxDimMm"] = maxMm;
            string expected = maxMm >= ValidateScaleSanity.CeilingMm ? "review-scale" : "sane";
            d["expectedVerdict"] = expected;
            d["inchRecoverMm"] = maxMm / 25.4;   // /25.4 recovered size (inch->mm hypothesis) — the giant fixture lands on 80.0
            return d;
        }
    }
}
