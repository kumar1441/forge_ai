using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class FaceGapResult
    {
        public double GapMm = -1;
        public string CompA, CompB;
        public bool Verified;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 28 — check_clearance (READ-ONLY): "how far apart are the mating faces", "gap between the flange
    /// faces", "clearance between the faces". Forge previously had NO intent-executor for this phrasing (only
    /// measure_distance's component-to-component "between X and Y" NAMED matcher, which needs explicit component
    /// names and never fires on a generic "the mating faces") — it hedged with an ambiguity question instead of
    /// measuring. Never writes.
    /// For every pair of non-fastener components, collects their planar faces (world-space normal + point, via
    /// Surface.PlaneParams transformed by Component2.Transform2 — the AutoMate.cs-proven primitive) and finds the
    /// closest ANTI-PARALLEL face pair across the two components: that pair IS the mating interface (a flange's
    /// outer/back faces pair with nothing anti-parallel and close; only the two inner faces that actually meet are
    /// both anti-parallel AND near-zero apart). Reports the perpendicular gap along that pair's shared normal.
    /// GT (GroundTruth.MeasureFaceGap.cs) cross-checks via face TESSELLATION points, not vertex/plane math —
    /// IBody2.GetVertices() returns null on assembly-context bodies from Component2.GetBodies3, so it can't be
    /// used here despite working fine on part-level bodies elsewhere in the codebase.
    /// </summary>
    public static class MeasureFaceGap
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            return Regex.IsMatch(c, @"\bmating\s+face|face\s*gap|faces?\s+apart|apart\b.*\bfaces?\b|gap\s+between\b.*\bface|clearance\s+between\b.*\bface");
        }

        private class Plane { public double[] P; public double[] N; public double Area; public Component2 Comp; }

        public static async Task<FaceGapResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new FaceGapResult();
            var asm = model as AssemblyDoc;
            if (asm == null) { res.Error = "Open the assembly (.SLDASM) to measure the gap between mating faces."; return res; }

            await emit("Caliper", "measuring the mating-face gap", "run", null);

            var mu = (MathUtility)app.GetMathUtility();
            var byComp = new List<Plane>();
            try
            {
                foreach (var o in (asm.GetComponents(true) as object[]) ?? new object[0])
                {
                    var c = o as Component2; if (c == null) continue;
                    bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                    string nm = null; try { nm = c.Name2; } catch { }
                    if (LooksLikeFastener(nm)) continue;
                    CollectPlanarFaces(mu, c, byComp);
                }
            }
            catch (Exception ex) { res.Error = "Face-gap read failed (" + ex.GetType().Name + ")."; return res; }

            // large planar faces only — a bolt-hole counterbore or chamfer isn't a mating interface
            double maxArea = 0; foreach (var p in byComp) if (p.Area > maxArea) maxArea = p.Area;
            if (maxArea <= 0 || byComp.Count < 2)
            {
                res.Error = "Couldn't find two components with planar faces to measure a gap between.";
                await emit("Caliper", null, "done", "no candidate faces");
                return res;
            }
            double areaFloor = maxArea * 0.1;

            double best = double.MaxValue; Plane bestA = null, bestB = null;
            for (int i = 0; i < byComp.Count; i++)
            {
                var a = byComp[i]; if (a.Area < areaFloor) continue;
                for (int j = 0; j < byComp.Count; j++)
                {
                    var b = byComp[j];
                    if (b.Comp == a.Comp) continue;   // only ACROSS different components — that's a "mating" gap, not a face-to-face gap within one part
                    if (b.Area < areaFloor) continue;
                    double dot = a.N[0] * b.N[0] + a.N[1] * b.N[1] + a.N[2] * b.N[2];
                    if (dot > -0.95) continue;   // must be anti-parallel (facing each other)
                    double gap = Math.Abs((b.P[0] - a.P[0]) * a.N[0] + (b.P[1] - a.P[1]) * a.N[1] + (b.P[2] - a.P[2]) * a.N[2]);
                    if (gap < best) { best = gap; bestA = a; bestB = b; }
                }
            }

            if (bestA == null)
            {
                res.Error = "No facing planar-face pair found between components — can't measure a mating-face gap.";
                await emit("Caliper", null, "done", "no facing pair");
                return res;
            }

            res.GapMm = best * 1000.0;
            try { res.CompA = bestA.Comp.Name2; res.CompB = bestB.Comp.Name2; } catch { }
            res.Verified = true;

            string apartness = res.GapMm < 0.05 ? "touching (mated coincident)" : Trim(res.GapMm) + "mm apart";
            res.Info = "Mating faces (" + res.CompA + " ↔ " + res.CompB + ") are " + apartness + ".";
            await emit("Caliper", null, "done", res.CompA + " ↔ " + res.CompB + " = " + Trim(res.GapMm) + "mm");
            return res;
        }

        private static void CollectPlanarFaces(MathUtility mu, Component2 comp, List<Plane> into)
        {
            var xform = comp.Transform2;
            object bi;
            object[] bodies = comp.GetBodies3((int)swBodyType_e.swSolidBody, out bi) as object[];
            foreach (var bo in bodies ?? new object[0])
            {
                var body = bo as Body2; if (body == null) continue;
                object[] faces = body.GetFaces() as object[]; if (faces == null) continue;
                foreach (var fo in faces)
                {
                    var face = fo as Face2; if (face == null) continue;
                    var surf = face.GetSurface() as Surface; if (surf == null || !surf.IsPlane()) continue;
                    double[] pp = surf.PlaneParams as double[]; if (pp == null || pp.Length < 6) continue;

                    var nv = (MathVector)((MathVector)mu.CreateVector(new[] { pp[0], pp[1], pp[2] })).MultiplyTransform(xform);
                    var pt = (MathPoint)((MathPoint)mu.CreatePoint(new[] { pp[3], pp[4], pp[5] })).MultiplyTransform(xform);
                    double[] na = nv.ArrayData as double[]; double[] pa = pt.ArrayData as double[];
                    double nl = Math.Sqrt(na[0] * na[0] + na[1] * na[1] + na[2] * na[2]); if (nl < 1e-9) continue;

                    double area = 0; try { area = face.GetArea(); } catch { }
                    into.Add(new Plane { P = new[] { pa[0], pa[1], pa[2] }, N = new[] { na[0] / nl, na[1] / nl, na[2] / nl }, Area = area, Comp = comp });
                }
            }
        }

        private static readonly string[] FastenerHints =
            { "bolt", "screw", "nut", "washer", "hcs", "shcs", "cap", "socket", "hex", "stud", "vis", "boulon", "bulong", "ecrou", "rondelle", "iso", "din", "b18" };
        private static bool LooksLikeFastener(string n)
        {
            if (string.IsNullOrEmpty(n)) return false; n = n.ToLowerInvariant();
            foreach (var h in FastenerHints) if (n.Contains(h)) return true;
            return false;
        }

        private static string Trim(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
