using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class DescribeGeometryResult
    {
        public bool Success;
        public string Criterion;       // pre-select criterion actually used, if any: top/bottom/left/right/largest/hole
        public string ShapeType;       // "planar" | "cylindrical" | "conical" | "spherical" | "other"
        public double AreaMm2 = -1;
        public double DiameterMm = -1; // cylindrical only
        public double HeightMm = -1;   // cylindrical only: axial extent of this face
        public bool? Concave;          // cylindrical only: true = bore/hole (material removed), false = boss/shaft
        public string Orientation;     // planar only: "top"/"bottom"/"left"/"right"/"side"
        public string Description;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// DescribeGeometry (tool 237, READ) — semantic readout of the currently SELECTED face: shape family
    /// (planar/cylindrical/conical/spherical), key dimensions (diameter+height for a cylinder, area+orientation
    /// for a planar face), and for a cylinder whether it's a bore (concave, material removed) or a boss/shaft
    /// (convex, material added) — the same inward/outward-normal concavity test CountThroughHoles/RunDfmChecks
    /// already proved live (a real hole's outward face normal points back toward its own axis; a boss/rib's
    /// points away). Axial extent (cylinder "height") is the face's own bbox corners projected onto the bore
    /// axis, span = max-min — the same primitive RunDfmChecks' HoleDepthMm proved.
    ///
    /// Distinct from GetSelectedEntities (tool 16, "what's selected" — a bare type/count listing): this is a
    /// deeper per-entity geometric narrative, one face at a time. Distinct from GetFeatureInfo (tool 5, named
    /// feature-tree parameters like "depth of Boss-Extrude1"): this reads the SELECTED geometry itself, never a
    /// feature by name — disjoint vocabulary (describe/explain vs depth-of/radius-of/feature-info), no ordering
    /// dependency needed either direction.
    ///
    /// Supports an optional EMBEDDED pre-select sub-command ("describe the top face", "describe the geometry of
    /// the hole") — the only way to prove this against a REAL non-empty selection in the automated harness,
    /// since GroundTruth.Measure's ForceRebuild3 runs immediately after every handler call and drops any live
    /// selection as a side effect (same non-negotiable established by select_face/edge/plane/component/
    /// get_selected_entities). Planar criteria (top/bottom/left/right/largest) delegate to SelectFace.Run
    /// verbatim; "hole"/"bore" is resolved directly by this handler (SelectFace is planar-only) via the largest
    /// concave cylindrical face on the part, same size-bounded (1.5mm..60%-of-part) noise exclusion
    /// CountThroughHoles proved against thread-root/knurl false positives.
    ///
    /// KNOWN SCOPE LIMIT: face-level only (v1). Whole-body/whole-part description (volume, face-type breakdown)
    /// and feature-count context ("3 tapped holes on this face") are NOT implemented — this codebase has no
    /// thread/tap detection anywhere, so that part of the tool-doc's illustrative example is honestly out of
    /// scope rather than guessed. A selection that isn't a single face (a body, component, or nothing) is
    /// refused with a clear message, never misreported as a face. Conical and spherical faces are classified but
    /// not dimensioned beyond area — no fixture/need has proven a dimensioning scheme for them yet.
    /// </summary>
    public static class DescribeGeometry
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            bool verb = Regex.IsMatch(c, @"\bdescribe\b") || Regex.IsMatch(c, @"\bexplain\b") ||
                        Regex.IsMatch(c, @"\bwhat\s+is\s+this\b") || Regex.IsMatch(c, @"\bwhat\s+am\s+i\s+looking\s+at\b");
            if (!verb) return false;
            return Regex.IsMatch(c, @"\b(face|geometry|shape|surface)\b");
        }

        public static async Task<DescribeGeometryResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new DescribeGeometryResult();
            if (model == null) { res.Error = "Open a part first."; return res; }
            var part = model as PartDoc;
            if (part == null) { res.Error = "Describing geometry works on a part — open the .SLDPRT, not an assembly."; return res; }

            await emit("Narrator", "reading the selected geometry", "run", null);

            string crit = ParsePreSelectCriterion(intent);
            if (crit == "hole")
            {
                var holeFace = FindLargestConcaveCylindricalFace(part);
                if (holeFace == null)
                { res.Error = "Couldn't find a concave cylindrical hole face on this part."; await emit("Narrator", null, "fail", res.Error); return res; }
                model.ClearSelection2(true);
                bool sel = false; try { sel = ((Entity)holeFace).Select4(false, null); } catch (Exception ex) { res.Error = "Select4 threw: " + ex.Message; return res; }
                if (!sel)
                { res.Error = "Found a hole face but SolidWorks refused the selection."; await emit("Narrator", null, "fail", res.Error); return res; }
                res.Criterion = "hole";
            }
            else if (crit != null)
            {
                var sf = await SelectFace.Run(app, model, "select the " + crit + " face", (a, b, c2, d) => Task.CompletedTask);
                if (!sf.Success)
                { res.Error = "Couldn't pre-select the " + crit + " face: " + sf.Error; await emit("Narrator", null, "fail", res.Error); return res; }
                res.Criterion = crit;
            }

            var sm = model.SelectionManager as SelectionMgr;
            int count = 0;
            try { count = sm.GetSelectedObjectCount2(-1); } catch (Exception ex) { res.Error = "Couldn't read the selection manager: " + ex.Message; return res; }
            if (count == 0)
            {
                res.Error = "Nothing is selected — select a face first (or say which one, e.g. \"describe the top face\").";
                await emit("Narrator", null, "fail", res.Error);
                return res;
            }

            object sel0 = null; try { sel0 = sm.GetSelectedObject6(1, -1); } catch { }
            var face = sel0 as Face2;
            if (face == null)
            {
                res.Error = "The current selection isn't a single face — select one face to describe (whole-body description isn't implemented yet).";
                await emit("Narrator", null, "fail", res.Error);
                return res;
            }

            Surface surf = null; try { surf = face.GetSurface() as Surface; } catch { }
            double area = 0; try { area = face.GetArea(); } catch { }
            res.AreaMm2 = area * 1e6;

            bool isPlane = false, isCyl = false, isCone = false, isSphere = false;
            try { isPlane = surf != null && surf.IsPlane(); } catch { }
            try { isCyl = surf != null && surf.IsCylinder(); } catch { }
            try { isCone = surf != null && surf.IsCone(); } catch { }
            try { isSphere = surf != null && surf.IsSphere(); } catch { }

            if (isPlane)
            {
                res.ShapeType = "planar";
                double[] n = null; try { n = face.Normal as double[]; } catch { }
                res.Orientation = ClassifyOrientation(n, part);
                res.Description = "Planar face, " + Math.Round(res.AreaMm2, 1) + " mm2" +
                    (res.Orientation != null ? " (" + res.Orientation + " face)" : "") + ".";
            }
            else if (isCyl)
            {
                res.ShapeType = "cylindrical";
                double[] cp = null; try { cp = surf.CylinderParams as double[]; } catch { }
                if (cp != null && cp.Length >= 7)
                {
                    res.DiameterMm = cp[6] * 2.0 * 1000.0;
                    res.HeightMm = AxialExtentMm(face, cp);
                    res.Concave = IsConcave(face, surf, cp);
                    string kind = res.Concave == true ? "bore/hole" : "boss/shaft";
                    res.Description = "Cylindrical " + kind + ", diameter " + Math.Round(res.DiameterMm, 2) + "mm" +
                        (res.HeightMm > 0 ? ", height " + Math.Round(res.HeightMm, 1) + "mm" : "") + ".";
                }
                else
                {
                    res.Description = "Cylindrical face (radius unreadable).";
                }
            }
            else if (isCone)
            {
                res.ShapeType = "conical";
                res.Description = "Conical face, " + Math.Round(res.AreaMm2, 1) + " mm2.";
            }
            else if (isSphere)
            {
                res.ShapeType = "spherical";
                res.Description = "Spherical face, " + Math.Round(res.AreaMm2, 1) + " mm2.";
            }
            else
            {
                res.ShapeType = "other";
                res.Description = "Freeform/other surface, " + Math.Round(res.AreaMm2, 1) + " mm2 (not a plane, cylinder, cone, or sphere).";
            }

            res.Success = true;
            res.Info = res.Description;
            await emit("Narrator", null, "done", res.Description);
            return res;
        }

        private static string ParsePreSelectCriterion(string intent)
        {
            string c = (intent ?? "").ToLowerInvariant();
            if (Regex.IsMatch(c, @"\b(hole|bore)\b")) return "hole";
            if (!Regex.IsMatch(c, @"\bface\b")) return null;
            if (Regex.IsMatch(c, @"\b(largest|biggest)\b")) return "largest";
            if (Regex.IsMatch(c, @"\btop\b")) return "top";
            if (Regex.IsMatch(c, @"\bbottom\b")) return "bottom";
            if (Regex.IsMatch(c, @"\bleft\b")) return "left";
            if (Regex.IsMatch(c, @"\bright\b")) return "right";
            return null;
        }

        // "top"/"bottom" mean the part's OWN vertical axis (whichever of Y/Z has the larger overall bounding-box
        // span) and "left"/"right" the X axis — the exact heuristic SelectFace.cs already proved live (AddHole's
        // fix for test-loop no-change finding add-drain-hole). Reused here (not re-derived) so a face this tool
        // calls "top" always agrees with what select_face would call "top" on the same part.
        private static string ClassifyOrientation(double[] n, PartDoc part)
        {
            if (n == null || n.Length < 3) return null;
            object[] bodies = null;
            try { bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]; } catch { }
            int vAxis = SelectFace.VerticalAxisIndex(bodies);
            double v = n[vAxis];
            double h = n[0];
            if (vAxis != 0 && Math.Abs(v) > 0.7 && Math.Abs(v) >= Math.Abs(h))
                return v > 0 ? "top" : "bottom";
            if (Math.Abs(h) > 0.7)
                return h > 0 ? "right" : "left";
            return "side";
        }

        // largest concave cylindrical face on the part (by radius), same size-bounded noise exclusion
        // CountThroughHoles proved (below 1.5mm = thread-root/knurl noise; above 60% of the part's own smallest
        // overall dimension = the part's main body cavity, not a "hole"). Picking the LARGEST rather than the
        // first found keeps the pick deterministic and biased toward the real functional hole.
        internal static Face2 FindLargestConcaveCylindricalFace(PartDoc part)
        {
            object[] bodies = null; try { bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]; } catch { }
            if (bodies == null) return null;

            double[] unionBox = null;
            var candidates = new List<(Face2 Face, double R)>();
            foreach (var bo in bodies)
            {
                var body = bo as Body2; if (body == null) continue;
                try { unionBox = UnionBox(unionBox, body.GetBodyBox() as double[]); } catch { }
                object[] faces = null; try { faces = body.GetFaces() as object[]; } catch { }
                foreach (var fo in faces ?? new object[0])
                {
                    var face = fo as Face2; if (face == null) continue;
                    Surface surf = null; try { surf = face.GetSurface() as Surface; } catch { }
                    bool isCyl = false; try { isCyl = surf != null && surf.IsCylinder(); } catch { }
                    if (!isCyl) continue;
                    double[] cp = null; try { cp = surf.CylinderParams as double[]; } catch { }
                    if (cp == null || cp.Length < 7) continue;
                    if (!IsConcave(face, surf, cp)) continue;
                    candidates.Add((face, cp[6]));
                }
            }
            if (candidates.Count == 0) return null;

            if (unionBox != null && unionBox.Length >= 6)
            {
                double spanX = unionBox[3] - unionBox[0], spanY = unionBox[4] - unionBox[1], spanZ = unionBox[5] - unionBox[2];
                double minSpan = Math.Min(spanX, Math.Min(spanY, spanZ));
                const double minDiaM = 0.0015;
                double maxDiaM = 0.6 * minSpan;
                candidates.RemoveAll(c => (c.R * 2.0) < minDiaM || (c.R * 2.0) > maxDiaM);
            }
            if (candidates.Count == 0) return null;

            Face2 best = null; double bestR = -1;
            foreach (var c in candidates) if (c.R > bestR) { bestR = c.R; best = c.Face; }
            return best;
        }

        private static double[] UnionBox(double[] acc, double[] b)
        {
            if (b == null || b.Length < 6) return acc;
            if (acc == null) return new[] { b[0], b[1], b[2], b[3], b[4], b[5] };
            return new[]
            {
                Math.Min(acc[0], b[0]), Math.Min(acc[1], b[1]), Math.Min(acc[2], b[2]),
                Math.Max(acc[3], b[3]), Math.Max(acc[4], b[4]), Math.Max(acc[5], b[5])
            };
        }

        // face's own bbox corners projected onto the cylinder axis, span = max-min — same primitive
        // RunDfmChecks.HoleDepthMm proved live. Exact for a clean bore/boss wall regardless of through/blind.
        private static double AxialExtentMm(Face2 face, double[] cylParams)
        {
            try
            {
                double[] axisO = { cylParams[0], cylParams[1], cylParams[2] };
                double[] axisD = { cylParams[3], cylParams[4], cylParams[5] };
                double dl = Math.Sqrt(axisD[0] * axisD[0] + axisD[1] * axisD[1] + axisD[2] * axisD[2]);
                if (dl < 1e-9) return 0;
                axisD = new[] { axisD[0] / dl, axisD[1] / dl, axisD[2] / dl };

                double[] box = null; try { box = face.GetBox() as double[]; } catch { }
                if (box == null || box.Length < 6) return 0;
                double minT = double.MaxValue, maxT = double.MinValue;
                for (int cx = 0; cx < 2; cx++)
                for (int cy = 0; cy < 2; cy++)
                for (int cz = 0; cz < 2; cz++)
                {
                    double px = cx == 0 ? box[0] : box[3];
                    double py = cy == 0 ? box[1] : box[4];
                    double pz = cz == 0 ? box[2] : box[5];
                    double t = (px - axisO[0]) * axisD[0] + (py - axisO[1]) * axisD[1] + (pz - axisO[2]) * axisD[2];
                    if (t < minT) minT = t;
                    if (t > maxT) maxT = t;
                }
                return (maxT - minT) * 1000.0;
            }
            catch { return 0; }
        }

        // same inward/outward-normal concavity test CountThroughHoles.cs proved live: a real hole's outward face
        // normal points BACK toward its own axis (empty bore is the cylinder's interior); a boss/rib/tooth/knurl
        // bump's normal points AWAY from its axis (solid material fills the cylinder).
        private static bool IsConcave(Face2 face, Surface surf, double[] cylParams)
        {
            try
            {
                double[] box = null; try { box = face.GetBox() as double[]; } catch { }
                if (box == null || box.Length < 6) return false;
                double[] center = { (box[0] + box[3]) / 2, (box[1] + box[4]) / 2, (box[2] + box[5]) / 2 };
                double[] p = null; try { p = face.GetClosestPointOn(center[0], center[1], center[2]) as double[]; } catch { }
                if (p == null || p.Length < 3) return false;

                double[] axisO = { cylParams[0], cylParams[1], cylParams[2] };
                double[] axisD = { cylParams[3], cylParams[4], cylParams[5] };
                double dl = Math.Sqrt(axisD[0] * axisD[0] + axisD[1] * axisD[1] + axisD[2] * axisD[2]);
                if (dl < 1e-9) return false;
                axisD = new[] { axisD[0] / dl, axisD[1] / dl, axisD[2] / dl };

                double[] v = { p[0] - axisO[0], p[1] - axisO[1], p[2] - axisO[2] };
                double along = v[0] * axisD[0] + v[1] * axisD[1] + v[2] * axisD[2];
                double[] radial = { v[0] - along * axisD[0], v[1] - along * axisD[1], v[2] - along * axisD[2] };
                double rl = Math.Sqrt(radial[0] * radial[0] + radial[1] * radial[1] + radial[2] * radial[2]);
                if (rl < 1e-9) return false;
                radial = new[] { radial[0] / rl, radial[1] / rl, radial[2] / rl };   // unit, points OUTWARD from axis toward p

                double[] n = null; try { n = surf.EvaluateAtPoint(p[0], p[1], p[2]) as double[]; } catch { }
                if (n == null || n.Length < 3) return false;
                double nl = Math.Sqrt(n[0] * n[0] + n[1] * n[1] + n[2] * n[2]);
                if (nl < 1e-9) return false;
                double[] nu = { n[0] / nl, n[1] / nl, n[2] / nl };
                bool reversed = false; try { reversed = face.FaceInSurfaceSense(); } catch { }
                if (reversed) nu = new[] { -nu[0], -nu[1], -nu[2] };   // make it point OUT of the solid

                double dot = nu[0] * radial[0] + nu[1] * radial[1] + nu[2] * radial[2];
                return dot < 0;   // outward normal pointing back TOWARD the axis => concave bore, not a convex boss
            }
            catch { return false; }
        }
    }
}
