using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for the get_bounding_box (tool #21) READ handler. Shares NO code with GetBoundingBox.cs.
    ///
    /// The handler reads the box via IBody2.GetBodyBox (the analytic tight box). This GT re-derives it a DIFFERENT way:
    /// the min/max over every solid body's VERTEX points (IBody2.GetVertices → Vertex.GetPoint). For a part with planar
    /// faces the two agree exactly; on a curved part the vertex hull can UNDER-estimate the box (vertices sit inside the
    /// tight box of a curved face), so the harness weighs a FLAT-faced part (the sharp block) where the cross-check is
    /// exact. Part-only (an assembly's per-component vertex frames are out of scope here).
    ///
    /// Read-only: identical fingerprint on run1/run2 proves the handler wrote nothing.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureBoundingBox(ISldWorks app, IModelDoc2 model)
        {
            var d = new JObject();
            var part = model as PartDoc;
            if (part == null) { d["applicable"] = false; d["reason"] = "active doc is not a part (bbox GT is part-only)"; return d; }
            d["applicable"] = true;

            double xmin = double.MaxValue, ymin = double.MaxValue, zmin = double.MaxValue;
            double xmax = double.MinValue, ymax = double.MinValue, zmax = double.MinValue;
            int verts = 0;
            object[] bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[];
            foreach (var o in bodies ?? new object[0])
            {
                var b = o as Body2; if (b == null) continue;
                object[] vs = null; try { vs = b.GetVertices() as object[]; } catch { }
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

            bool hasSolid = verts > 0 && xmax > xmin;
            d["vertexCount"] = verts;
            d["hasSolid"] = hasSolid;
            if (hasSolid)
            {
                double dx = (xmax - xmin) * 1000.0, dy = (ymax - ymin) * 1000.0, dz = (zmax - zmin) * 1000.0;
                d["dxMm"] = dx; d["dyMm"] = dy; d["dzMm"] = dz;
                d["diagonalMm"] = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            }

            int rb = 0; try { rb = model.Extension.GetWhatsWrongCount(); } catch { }
            d["rebuildErrors"] = rb;
            d["fingerprint"] = new JObject { ["vertexCount"] = verts, ["rebuildErrors"] = rb };
            return d;
        }
    }
}
