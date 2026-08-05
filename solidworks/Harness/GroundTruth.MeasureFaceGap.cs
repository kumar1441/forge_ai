using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for the check_clearance / mating-face-gap (READ) handler. Shares NO code with
    /// MeasureFaceGap.cs: the handler works off planar-FACE plane equations (Surface.PlaneParams, anti-parallel
    /// normal pairing) and picks the SPECIFIC facing pair. This GT instead reads raw TESSELLATED surface points
    /// (IFace2.GetTessTriangles, a different primitive than plane-equation math) off every face of every
    /// non-fastener component, and takes the brute-force minimum Euclidean distance between any point-cloud of
    /// one component and any point-cloud of another — the closest approach between the two solids overall, with
    /// no face-pairing logic at all. For two solids whose closest approach is a flat face touching (or nearly
    /// touching) another flat face, this converges to the same number as the handler's plane-equation gap.
    /// NOTE: IBody2.GetVertices() returns null on assembly-context bodies from Component2.GetBodies3 (it only
    /// works on part-level bodies from PartDoc.GetBodies2) — tessellation points are the primitive that actually
    /// works at the assembly-component level.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureFaceGap(ISldWorks app, IModelDoc2 model)
        {
            var d = new JObject();
            var asm = model as AssemblyDoc;
            if (asm == null) { d["applicable"] = false; d["reason"] = "active doc is not an assembly"; return d; }
            d["applicable"] = true;

            var mu = (MathUtility)app.GetMathUtility();
            var clouds = new List<List<double[]>>();
            foreach (var o in (asm.GetComponents(true) as object[]) ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                string nm = null; try { nm = c.Name2; } catch { }
                if (IsFastenerName(nm)) continue;
                var pts = CollectWorldTessPoints(mu, c);
                if (pts.Count > 0) clouds.Add(pts);
            }

            d["componentsConsidered"] = clouds.Count;
            if (clouds.Count < 2)
            {
                d["gapMm"] = -1.0;
                d["fingerprint"] = new JObject { ["componentsConsidered"] = clouds.Count };
                return d;
            }

            double best = double.MaxValue;
            for (int i = 0; i < clouds.Count; i++)
                for (int j = i + 1; j < clouds.Count; j++)
                {
                    double m = MinDistance(clouds[i], clouds[j]);
                    if (m < best) best = m;
                }

            double gapMm = best * 1000.0;
            d["gapMm"] = gapMm;
            int rb = 0; try { rb = model.Extension.GetWhatsWrongCount(); } catch { }
            d["rebuildErrors"] = rb;
            d["fingerprint"] = new JObject { ["gapMmRounded"] = Math.Round(gapMm, 1), ["rebuildErrors"] = rb };
            return d;
        }

        private static double MinDistance(List<double[]> a, List<double[]> b)
        {
            double best = double.MaxValue;
            foreach (var p in a)
                foreach (var q in b)
                {
                    double dx = p[0] - q[0], dy = p[1] - q[1], dz = p[2] - q[2];
                    double dist = dx * dx + dy * dy + dz * dz;
                    if (dist < best) best = dist;
                }
            return best >= double.MaxValue ? best : Math.Sqrt(best);
        }

        // Sub-samples one point per triangle (the first vertex) — plenty dense to find the true closest approach
        // between two flange faces without the O(n^2) blowup of every triangle vertex on a fine mesh.
        private static List<double[]> CollectWorldTessPoints(MathUtility mu, Component2 comp)
        {
            var list = new List<double[]>();
            try
            {
                var xf = comp.Transform2; object bi;
                object[] bodies = comp.GetBodies3((int)swBodyType_e.swSolidBody, out bi) as object[];
                foreach (var bo in bodies ?? new object[0])
                {
                    var body = bo as Body2; if (body == null) continue;
                    object[] faces = body.GetFaces() as object[]; if (faces == null) continue;
                    foreach (var fo in faces)
                    {
                        var face = fo as Face2; if (face == null) continue;
                        float[] tris = null; try { tris = face.GetTessTriangles(false) as float[]; } catch { }
                        if (tris == null) continue;
                        for (int t = 0; t + 8 < tris.Length; t += 9)
                        {
                            var wp = (MathPoint)((MathPoint)mu.CreatePoint(new double[] { tris[t], tris[t + 1], tris[t + 2] })).MultiplyTransform(xf);
                            double[] wa = wp.ArrayData as double[]; if (wa == null) continue;
                            list.Add(new[] { wa[0], wa[1], wa[2] });
                        }
                    }
                }
            }
            catch { }
            return list;
        }
    }
}
