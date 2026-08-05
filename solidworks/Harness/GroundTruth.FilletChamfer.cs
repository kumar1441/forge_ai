using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground-truth for the fillet_chamfer handler. Shares NO code with FilletChamfer.cs — every number here
    /// is re-derived from scratch through this file's own helpers (prefixed Fc*), so agreement with the handler is a
    /// genuine cross-check, not a mirror of its math.
    ///
    /// Filleting/chamfering is a WRITE that adds rounds/bevels to edges, so — like the other write handlers — the harness
    /// compares a BASELINE read (run0, before any feature) against the post-write read (run1) and asserts the geometry
    /// changed the way a real fillet/chamfer must:
    ///
    ///   1. faceCount RISES   (every filleted/chamfered edge replaces a sharp corner with a new blend/bevel FACE)
    ///   2. hasForgeFillet is TRUE on run1 (a feature literally named 'Forge-Fillet' OR 'Forge-Chamfer' now exists)
    ///   3. rebuildErrors == 0 (the feature rebuilt clean)
    /// and the rerun is idempotent (run2 == run1 — no second fillet/chamfer stacked).
    ///
    /// Independent convex-edge count: for each solid-body edge, this file evaluates the two adjacent faces' outward
    /// normals at the edge midpoint and applies its OWN convexity test (N2·I1 &lt; 0 with an in-face-1 interior direction),
    /// re-implemented here so the count is a true second opinion on how many sharp convex edges exist before/after.
    /// hasSolid lets the grader demand an honest handler refusal on a body-less part.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureFilletChamfer(ISldWorks app, IModelDoc2 model)
        {
            var mo = new JObject();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { mo["applicable"] = false; mo["reason"] = "active doc is not a part"; return mo; }
            mo["applicable"] = true;

            var part = model as PartDoc;
            int bodyCount = 0, faceCount = 0, edgeCount = 0, convexEdges = 0;
            double volM3 = 0;
            if (part != null)
            {
                object[] bodies = null;
                try { bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]; } catch { }
                foreach (var bo in bodies ?? new object[0])
                {
                    var body = bo as Body2; if (body == null) continue;
                    bodyCount++;

                    object[] faces = null; try { faces = body.GetFaces() as object[]; } catch { }
                    if (faces != null) faceCount += faces.Length;

                    object[] edges = null; try { edges = body.GetEdges() as object[]; } catch { }
                    foreach (var eo in edges ?? new object[0])
                    {
                        var e = eo as Edge; if (e == null) continue;
                        edgeCount++;
                        if (FcIsConvexSharp(e)) convexEdges++;
                    }

                    // independent volume: per-body mass properties (a different path than the handler's whole-doc engine).
                    double[] mp = null; try { mp = body.GetMassProperties(1.0) as double[]; } catch { }
                    if (mp != null && mp.Length >= 4) volM3 += mp[3];
                }
            }

            bool hasForgeFillet = false;
            string forgeFeatureName = null;
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                string nm = null; try { nm = f.Name; } catch { }
                if (string.Equals(nm, "Forge-Fillet", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(nm, "Forge-Chamfer", StringComparison.OrdinalIgnoreCase)) { hasForgeFillet = true; forgeFeatureName = nm; break; }
                f = f.GetNextFeature() as Feature;
            }

            int rebuild = 0; try { rebuild = model.Extension.GetWhatsWrongCount(); } catch { }
            double volMm3 = volM3 * 1e9;   // m^3 -> mm^3

            mo["bodyCount"] = bodyCount;
            mo["faceCount"] = faceCount;
            mo["edgeCount"] = edgeCount;
            mo["convexEdgeCount"] = convexEdges;
            mo["volumeMm3"] = volMm3;
            mo["hasForgeFillet"] = hasForgeFillet;   // a feature named 'Forge-Fillet' OR 'Forge-Chamfer' exists?
            mo["isChamfer"] = string.Equals(forgeFeatureName, "Forge-Chamfer", StringComparison.OrdinalIgnoreCase);
            mo["rebuildErrors"] = rebuild;
            mo["hasSolid"] = bodyCount > 0;          // no solid => the handler MUST refuse honestly, not fake a fillet

            // change fingerprint the grader diffs run0 -> run1 (faces UP, edges UP, feature present); idempotent run2==run1
            var fp = new JObject();
            fp["faceCount"] = faceCount;
            fp["edgeCount"] = edgeCount;
            fp["volumeMm3"] = volMm3;
            mo["fingerprint"] = fp;
            return mo;
        }

        // ---- independent convex-sharp test (own helpers, no handler code) ----

        private const double FcSharpDotMax = 0.94;   // normals closer than ~20deg apart => tangent/smooth => not sharp
        private const double FcConvexMargin = 0.02;
        private const double FcEps = 1e-9;

        private static bool FcIsConvexSharp(Edge e)
        {
            try
            {
                double[] mid = FcEdgeMid(e); if (mid == null) return false;
                object[] faces = e.GetTwoAdjacentFaces2() as object[];
                if (faces == null || faces.Length < 2) return false;
                var f1 = faces[0] as Face2; var f2 = faces[1] as Face2;
                if (f1 == null || f2 == null) return false;

                double[] p1, n1, p2, n2;
                if (!FcNormalAt(f1, mid, out p1, out n1)) return false;
                if (!FcNormalAt(f2, mid, out p2, out n2)) return false;

                double nd = FcDot(n1, n2);
                if (nd > FcSharpDotMax) return false;

                double[] c1 = FcCenter(f1); if (c1 == null) return false;
                double[] v = { c1[0] - p1[0], c1[1] - p1[1], c1[2] - p1[2] };
                double axial = FcDot(v, n1);
                double[] i1 = { v[0] - axial * n1[0], v[1] - axial * n1[1], v[2] - axial * n1[2] };
                double il = FcLen(i1); if (il < FcEps) return false;
                i1[0] /= il; i1[1] /= il; i1[2] /= il;

                return FcDot(n2, i1) < -FcConvexMargin;
            }
            catch { return false; }
        }

        private static bool FcNormalAt(Face2 face, double[] at, out double[] pOut, out double[] nOut)
        {
            pOut = null; nOut = null;
            try
            {
                Surface s = face.GetSurface() as Surface; if (s == null) return false;
                double[] p = face.GetClosestPointOn(at[0], at[1], at[2]) as double[];
                if (p == null || p.Length < 3) p = at;
                double[] n = s.EvaluateAtPoint(p[0], p[1], p[2]) as double[];
                if (n == null || n.Length < 3) return false;
                double nl = FcLen(n); if (nl < FcEps) return false;
                double[] nu = { n[0] / nl, n[1] / nl, n[2] / nl };
                bool reversed = false; try { reversed = face.FaceInSurfaceSense(); } catch { }
                if (reversed) { nu[0] = -nu[0]; nu[1] = -nu[1]; nu[2] = -nu[2]; }
                pOut = new[] { p[0], p[1], p[2] };
                nOut = nu;
                return true;
            }
            catch { return false; }
        }

        private static double[] FcCenter(Face2 face)
        {
            try
            {
                double[] b = face.GetBox() as double[];
                if (b == null || b.Length < 6) return null;
                return new[] { (b[0] + b[3]) / 2, (b[1] + b[4]) / 2, (b[2] + b[5]) / 2 };
            }
            catch { return null; }
        }

        private static double[] FcEdgeMid(Edge e)
        {
            try
            {
                double[] p = e.GetCurveParams2() as double[];
                if (p != null && p.Length >= 6) return new[] { (p[0] + p[3]) / 2, (p[1] + p[4]) / 2, (p[2] + p[5]) / 2 };
            }
            catch { }
            try
            {
                var sv = e.GetStartVertex() as Vertex; var ev = e.GetEndVertex() as Vertex;
                if (sv != null && ev != null)
                {
                    double[] a = sv.GetPoint() as double[]; double[] b = ev.GetPoint() as double[];
                    if (a != null && b != null) return new[] { (a[0] + b[0]) / 2, (a[1] + b[1]) / 2, (a[2] + b[2]) / 2 };
                }
            }
            catch { }
            return null;
        }

        private static double FcDot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
        private static double FcLen(double[] a) => Math.Sqrt(a[0] * a[0] + a[1] * a[1] + a[2] * a[2]);
    }
}
