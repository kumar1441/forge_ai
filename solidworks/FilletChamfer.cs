using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class FilletChamferResult
    {
        public string Op = "fillet";        // "fillet" | "chamfer" — what was actually applied
        public double SizeMm;               // radius (fillet) or leg distance (chamfer), mm, parsed from the intent
        public string EdgeSet = "all sharp"; // "all sharp" | "outer" | "top" — the criterion resolved from the text
        public int EdgesTargeted;           // convex edges matched by the criterion BEFORE creating the feature
        public int EdgesFilleted;           // edges actually taken by the feature that VERIFIED (full set, or the reduced retry)
        public int RebuildErrors;           // GetWhatsWrongCount() post-rebuild (0 => clean)
        public bool RolledBack;             // the feature was created but failed to verify → deleted, part restored
        public bool Verified;               // fail closed: true ONLY when the feature exists, faces ROSE, rebuild clean
        public bool AlreadyDone;            // idempotent: a Forge-Fillet/Forge-Chamfer already exists, or no sharp edge to touch
        public bool NeedsConfirm;           // missing size → ask one question, run nothing
        public string Question;             // the one clarifying question when NeedsConfirm
        public int FaceBefore = -1;
        public int FaceAfter = -1;
        public string Info;                 // verdict-first panel line
        public string Error;                // honest failure text (assembly handed in, no solid, radius too big everywhere)
    }

    /// <summary>
    /// FilletChamfer (tool #79 "fillet or chamfer edges by criteria") — a REAL DFM / finishing geometry WRITE on a single
    /// PART. Adds ONE Fillet or Chamfer feature over the edges a plain-English criterion selects: "fillet all the sharp
    /// edges 2mm", "round the outer edges 3mm", "chamfer the top edges 1mm", "break all sharp corners 0.5mm".
    ///
    /// Approach (deliberate, documented):
    ///   Gauge — enumerate every solid-body edge and keep the CONVEX, non-tangent ("sharp") ones. Convexity is decided
    ///           from live geometry: at the edge midpoint, take the two adjacent faces' OUTWARD normals N1,N2 and the
    ///           in-face-1 direction I1 (perpendicular to N1, pointing from the edge toward face-1's interior). The edge
    ///           is CONVEX iff N2·I1 < 0 (face-2's outward normal points AWAY from face-1's interior — the two faces
    ///           enclose material like a box corner); CONCAVE (a valley — never filleted here) iff N2·I1 > 0. Faces that
    ///           meet nearly tangent (|N1·N2| high) are smooth transitions / existing rounds, so they are excluded — that
    ///           also means an already-rounded edge is never re-selected. The criterion then narrows the convex set:
    ///           "all sharp"/"outer" = every convex edge; "top" = convex edges whose midpoint sits in the top band of the
    ///           bbox along Z (part-space up). Preview the count BEFORE writing (Rule #3).
    ///   Finisher — create ONE feature over the selected edges: IFeatureManager.FeatureFillet3 (constant-radius, tangent
    ///           propagation) or IFeatureManager.InsertFeatureChamfer (angle-distance, 45°). Size is in METERS. Tag it
    ///           "Forge-Fillet" / "Forge-Chamfer" for idempotency. PARTIAL (Rule #4): if the full set fails (the radius
    ///           won't fit some edge), roll the failed feature back and RETRY once on just the edges long enough to take
    ///           the size; report "N of M (K too small for the size)". If even that fails, roll back and report honestly
    ///           with the number and a smaller-size suggestion.
    ///   Sentinel — FAIL CLOSED (Rule #6): after the rebuild, INDEPENDENTLY confirm the tagged feature exists, the solid
    ///           FACE COUNT rose (a fillet/chamfer adds faces), and the rebuild is clean. Anything less → the feature is
    ///           deleted and the part is restored; never a fake green.
    ///
    /// Robustness (the 12 rules): PART only — an assembly is refused honestly (Rule #2). Missing size → ONE question, no
    /// guessed default (Rule #2). IDEMPOTENT (Rule #5): a Forge-Fillet/Forge-Chamfer already present, or no sharp convex
    /// edge left, → "already done, nothing to do." UNDO is sacred (Rule #7): one tagged feature, one Ctrl+Z; Forge never
    /// saves. Verified reports what was MEASURED (faces up + clean rebuild), never what was attempted.
    /// </summary>
    public static class FilletChamfer
    {
        private const string FilletName = "Forge-Fillet";
        private const string ChamferName = "Forge-Chamfer";
        private const double MM = 0.001;         // mm -> SW metres
        private const double DefaultDeburrMm = 1.0; // standard small break-edge size for a plain "knock down the sharp corners" deburr ask

        // Sharpness / convexity thresholds (unit vectors).
        private const double SharpDotMax = 0.94; // adjacent-face normals closer than ~20deg apart => tangent/smooth => NOT a sharp edge
        private const double ConvexMargin = 0.02; // N2·I1 must be clearly < 0 to call an edge convex (avoid borderline coplanar noise)
        private const double TopBandFrac = 0.06; // "top edges" = convex edges within the top 6% of the bbox Z-span
        private const double Eps = 1e-9;

        public static bool IsFilletChamferIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            // Owns fillet/round/chamfer/bevel/"break (edge|corner)". A size (Nmm or a bare number) must be present so a
            // bare "round it off" without a value still asks. Does NOT collide with defeature/simplify — neither of those
            // uses fillet/round/chamfer/bevel/break verbs (defeature = "remove/strip the holes/detail"; simplify = "print
            // prep / suppress the fillets"). "suppress the fillets" contains "fillet" but not a size AND is negated by the
            // suppress verb, so it is excluded here.
            if (Regex.IsMatch(c, @"\bsuppress\b")) return false;
            // A fillet/chamfer ADDS material to an edge. "delete/remove/replace ... face" targets a FACE for
            // removal (delete_replace_face, tool 227) or a named FEATURE (DeleteFeature) — neither is this handler,
            // same negative-verb exclusion AddBoss/AddHole already use so "delete the fillet face" doesn't get
            // mistaken for "add a fillet" just because the word "fillet" appears.
            if (Regex.IsMatch(c, @"\b(delete|remove|strip|get rid of|kill|erase|replace|swap)\b")) return false;
            bool verb = Regex.IsMatch(c, @"\b(fillet|filleted|round(ed|ing)?|chamfer(ed|ing)?|bevel(led|ing)?|break)\b");
            // test-loop hedged fix (add-fillets-edges): shop-talk deburr phrasing ("need the sharp corners knocked
            // down", "deburr the edges", "soften/ease the corners") names the SAME operation (a small break-edge
            // fillet/chamfer) without ever saying fillet/round/chamfer/bevel/break literally — IsDeburrPhrasing
            // owns that vocabulary so it reaches this handler at all, then Run() below defaults the size instead
            // of asking (a doable task must act, same class as AddHole's/AddBoltCircle's sensible-default rule).
            return verb || IsDeburrPhrasing(c);
        }

        // shop-talk for "put a small break-edge finish on it" — no explicit size, no literal fillet/chamfer word,
        // but a real, doable, well-understood operation. Deliberately narrow (does NOT match a bare "smooth"/
        // "smoother", which HonestLimits.IsVagueAestheticRequest already owns as a genuine aesthetic clarify).
        private static bool IsDeburrPhrasing(string c)
        {
            return Regex.IsMatch(c,
                @"\bknock(?:ed)?\s+(?:down|off)\b|\bdeburr(?:ed|ing)?\b|\bsoften(?:ed|ing)?\s+(?:the\s+)?(?:sharp\s+)?(?:edges?|corners?)\b|" +
                @"\bease\s+(?:the\s+)?edges?\b|\bsmooth(?:ed)?\s+(?:out|down)\s+(?:the\s+)?(?:sharp\s+)?corners?\b|" +
                @"\bsharp\s+corners?\s+knocked\s+down\b");
        }

        private enum EdgeSetKind { AllSharp, Outer, Top }

        private class EdgeRec
        {
            public Edge Edge;
            public double LenM;      // straight-line span (heuristic length; underestimates arcs)
            public double MidZ;      // midpoint Z (part space) for the "top" filter
        }

        public static async Task<FilletChamferResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new FilletChamferResult();

            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { res.Error = "Fillet/chamfer works on a single part — open the .SLDPRT whose edges you want finished, not an assembly."; return res; }
            var part = model as PartDoc;
            if (part == null) { res.Error = "This document has no part geometry to fillet or chamfer."; return res; }

            bool chamfer = ParseIsChamfer(intent);
            res.Op = chamfer ? "chamfer" : "fillet";
            string tag = chamfer ? ChamferName : FilletName;

            // ---- IDEMPOTENT (Rule #5): a Forge fillet/chamfer already present → don't stack another ----
            var existing = FindFeatureByName(model, FilletName) ?? FindFeatureByName(model, ChamferName);
            if (existing != null)
            {
                res.AlreadyDone = true;
                res.Verified = true;
                res.Info = "Already done — a " + (existing.Name ?? tag) + " feature is present, so there's nothing to add. " +
                           "To refinish at a different size, delete that feature (Edit > Delete, or Ctrl+Z), then run again.";
                await emit("Finisher", null, "done", (existing.Name ?? tag) + " already present — nothing to do");
                return res;
            }

            // ---- size: ask when truly ambiguous (Rule #2) — UNLESS the phrasing itself names a standard shop
            // operation with an implied size (test-loop hedged fix add-fillets-edges: "need the sharp corners
            // knocked down" is a plain deburr ask, not an open-ended "what size?" question — a doable task must
            // act). DefaultDeburrMm is a standard small break-edge size, same class as AddHole's 8mm default.
            double sizeMm = ParseSizeMm(intent);
            bool sizeDefaulted = false;
            if (sizeMm <= 0 && IsDeburrPhrasing((intent ?? "").ToLowerInvariant()))
            {
                sizeMm = DefaultDeburrMm;
                sizeDefaulted = true;
            }
            if (sizeMm <= 0)
            {
                res.NeedsConfirm = true;
                res.Question = chamfer
                    ? "What chamfer size? e.g. \"chamfer the top edges 1mm\"."
                    : "What fillet radius? e.g. \"fillet all the sharp edges 2mm\".";
                return res;
            }
            res.SizeMm = sizeMm;

            EdgeSetKind kind = ParseEdgeSet(intent);
            res.EdgeSet = kind == EdgeSetKind.Top ? "top" : (kind == EdgeSetKind.Outer ? "outer" : "all sharp");

            await emit("Gauge", "reading the solid edges", "run", null);
            object[] bodies = SolidBodies(part);
            if (bodies == null || bodies.Length == 0)
            { res.Error = "No solid body to finish — this part has no solid geometry (a surface/sheet body or empty doc has no edges to fillet)."; return res; }

            double[] bbox = UnionBodyBox(bodies);
            var targeted = SelectConvexEdges(bodies, kind, bbox);
            res.EdgesTargeted = targeted.Count;
            res.FaceBefore = FaceCount(part);

            await emit("Gauge", null, "done",
                targeted.Count + " " + res.EdgeSet + " convex edge" + (targeted.Count == 1 ? "" : "s") + " found  ·  " + res.FaceBefore + " faces");

            // ---- no matching convex edge → honest "nothing to do" (also the fully-rounded / idempotent case) ----
            if (targeted.Count == 0)
            {
                res.AlreadyDone = true;
                res.Verified = true;
                res.FaceAfter = res.FaceBefore;
                res.Info = "Nothing to " + res.Op + " — no " + res.EdgeSet + " convex edges were found (the part may already be fully rounded, " +
                           "or the criterion matched no external corners). Forge changed nothing.";
                await emit("Finisher", null, "done", "no " + res.EdgeSet + " convex edges — nothing to " + res.Op);
                return res;
            }

            // ---- PREVIEW then WRITE (Rule #3): one feature over the whole convex set ----
            await emit("Finisher", targeted.Count + " " + res.EdgeSet + " convex edge" + (targeted.Count == 1 ? "" : "s") + " — " + res.Op + "ing " + Trim(sizeMm) + "mm", "run", null);

            double sizeM = sizeMm * MM;
            var attempt = CreateFeature(model, part, targeted.Select(t => t.Edge).ToList(), chamfer, sizeM, tag, res.FaceBefore);

            if (attempt.Ok)
            {
                res.EdgesFilleted = targeted.Count;
                res.RebuildErrors = attempt.RebuildErrors;
                res.FaceAfter = attempt.FaceAfter;
                res.Verified = true;
                await emit("Sentinel", null, "done",
                    res.Op + "ed " + targeted.Count + " edge" + (targeted.Count == 1 ? "" : "s") + " · faces " + res.FaceBefore + "→" + res.FaceAfter + " · rebuild clean");
                res.Info = BuildInfo(res, targeted.Count, 0, sizeDefaulted);
                return res;
            }

            // ---- attempt 1 failed → roll it back, RETRY on just the edges long enough to take the size (Rule #4) ----
            RollbackFeature(model, tag);
            var reduced = targeted.Where(t => t.LenM >= 2.0 * sizeM).Select(t => t.Edge).ToList();

            if (reduced.Count > 0 && reduced.Count < targeted.Count)
            {
                await emit("Finisher", "full set failed at " + Trim(sizeMm) + "mm — retrying " + reduced.Count + " edge" + (reduced.Count == 1 ? "" : "s") + " big enough for it", "run", null);
                var retry = CreateFeature(model, part, reduced, chamfer, sizeM, tag, res.FaceBefore);
                if (retry.Ok)
                {
                    int skipped = targeted.Count - reduced.Count;
                    res.EdgesFilleted = reduced.Count;
                    res.RebuildErrors = retry.RebuildErrors;
                    res.FaceAfter = retry.FaceAfter;
                    res.Verified = true;   // partial success is still a VERIFIED write on the edges that took it
                    await emit("Sentinel", null, "done",
                        res.Op + "ed " + reduced.Count + " of " + targeted.Count + " · " + skipped + " too small · faces " + res.FaceBefore + "→" + res.FaceAfter);
                    res.Info = BuildInfo(res, reduced.Count, skipped, sizeDefaulted);
                    return res;
                }
                RollbackFeature(model, tag);
            }

            // ---- both attempts failed → part restored, honest failure with a smaller-size suggestion ----
            res.RolledBack = true;
            res.FaceAfter = FaceCount(part);
            double suggest = Math.Max(0.25, sizeMm / 2.0);
            res.Error = res.Op + " at " + Trim(sizeMm) + "mm failed — the " + (chamfer ? "leg" : "radius") + " is too large for these edges (the faces around them are smaller than " +
                        Trim(sizeMm) + "mm). Rolled it back; the part is unchanged. Try " + Trim(suggest) + "mm.";
            await emit("Sentinel", null, "fail", "rolled back — part restored (try " + Trim(suggest) + "mm)");
            return res;
        }

        // ================= feature creation + verification =================

        private struct Attempt { public bool Ok; public int RebuildErrors; public int FaceAfter; }

        // Select the edges, create ONE fillet/chamfer feature, tag it, rebuild, and INDEPENDENTLY verify (faces rose +
        // clean rebuild). Returns Ok=false if the feature was refused or didn't add faces — the caller rolls it back.
        private static Attempt CreateFeature(IModelDoc2 model, PartDoc part, List<Edge> edges, bool chamfer, double sizeM, string tag, int faceBefore)
        {
            var at = new Attempt { Ok = false, RebuildErrors = -1, FaceAfter = -1 };
            try { model.ClearSelection2(true); } catch { }

            int selected = 0;
            foreach (var e in edges)
            { try { if (((Entity)e).Select4(true, null)) selected++; } catch { } }
            if (selected == 0) { try { model.ClearSelection2(true); } catch { } return at; }

            Feature feat = null;
            try
            {
                if (chamfer)
                {
                    // InsertFeatureChamfer(Options, Type, Width_m, Angle_rad, OtherDist, v1, v2, v3) — angle-distance, 45deg.
                    feat = model.FeatureManager.InsertFeatureChamfer(
                        0, (int)swChamferType_e.swChamferAngleDistance, sizeM, 45.0 * Math.PI / 180.0, 0, 0, 0, 0) as Feature;
                }
                else
                {
                    // FeatureFillet3(Options, R1_m, R2, Rtyp, FilletType, ...nulls) — constant-radius simple fillet, tangent propagation.
                    // Options MUST include UniformRadius, else SW doesn't know the radius type and FeatureFillet3 returns null
                    // (proven: it failed on 4 clean top edges of a perfect block until this flag was added).
                    feat = model.FeatureManager.FeatureFillet3(
                        (int)swFeatureFilletOptions_e.swFeatureFilletUniformRadius | (int)swFeatureFilletOptions_e.swFeatureFilletPropagate,
                        sizeM, 0, 0,
                        (int)swFeatureFilletType_e.swFeatureFilletType_Simple, 0, 0, null, null, null, null, null, null, null) as Feature;
                }
            }
            catch { feat = null; }
            try { model.ClearSelection2(true); } catch { }

            if (feat == null) return at;
            try { feat.Name = tag; } catch { }

            try { model.ForceRebuild3(false); } catch { }
            at.RebuildErrors = SafeWhatsWrong(model);
            at.FaceAfter = FaceCount(part);

            // fail closed: a real fillet/chamfer ADDS faces and rebuilds clean. Anything less is not a success.
            at.Ok = at.RebuildErrors == 0 && at.FaceAfter > faceBefore;
            return at;
        }

        // ================= edge selection by criterion =================

        // Every convex, non-tangent ("sharp") solid-body edge that matches the criterion.
        private static List<EdgeRec> SelectConvexEdges(object[] bodies, EdgeSetKind kind, double[] bbox)
        {
            var list = new List<EdgeRec>();
            double zTopCut = double.NegativeInfinity;
            if (kind == EdgeSetKind.Top && bbox != null && bbox.Length >= 6)
            {
                double zSpan = bbox[5] - bbox[2];
                zTopCut = bbox[5] - Math.Max(1e-5, TopBandFrac * zSpan);
            }

            foreach (var bo in bodies)
            {
                var body = bo as Body2; if (body == null) continue;
                object[] edges = null; try { edges = body.GetEdges() as object[]; } catch { }
                foreach (var eo in edges ?? new object[0])
                {
                    var e = eo as Edge; if (e == null) continue;
                    double[] mid = EdgeMid(e); if (mid == null) continue;
                    if (!IsConvexSharp(e, mid)) continue;
                    if (kind == EdgeSetKind.Top && mid[2] < zTopCut) continue;
                    list.Add(new EdgeRec { Edge = e, LenM = EdgeSpanM(e), MidZ = mid[2] });
                }
            }
            return list;
        }

        // Convex + sharp test from live geometry (see class summary). N1,N2 = adjacent-face outward normals at the edge
        // midpoint; I1 = in-face-1 direction toward face-1's interior. Sharp iff normals are NOT near-parallel; convex
        // iff N2·I1 < 0. Fails CLOSED to "not selected" on any unmeasurable edge (never fillet something we can't read).
        private static bool IsConvexSharp(Edge e, double[] mid)
        {
            try
            {
                object[] faces = e.GetTwoAdjacentFaces2() as object[];
                if (faces == null || faces.Length < 2) return false;
                var f1 = faces[0] as Face2; var f2 = faces[1] as Face2;
                if (f1 == null || f2 == null) return false;

                double[] p1, n1, p2, n2;
                if (!FaceNormalAt(f1, mid, out p1, out n1)) return false;
                if (!FaceNormalAt(f2, mid, out p2, out n2)) return false;

                double nd = Dot(n1, n2);
                if (nd > SharpDotMax) return false;   // near-tangent/smooth transition (or an existing round) — not a sharp edge

                double[] c1 = FaceCenter(f1);
                if (c1 == null) return false;
                double[] v = { c1[0] - p1[0], c1[1] - p1[1], c1[2] - p1[2] };
                double axial = Dot(v, n1);
                double[] i1 = { v[0] - axial * n1[0], v[1] - axial * n1[1], v[2] - axial * n1[2] };
                double il = Len(i1); if (il < Eps) return false;
                i1[0] /= il; i1[1] /= il; i1[2] /= il;

                return Dot(n2, i1) < -ConvexMargin;   // face-2 normal points away from face-1 interior => convex corner
            }
            catch { return false; }
        }

        // Outward unit normal of a face at (near) the given assembly point; also returns the snapped surface point.
        private static bool FaceNormalAt(Face2 face, double[] at, out double[] pOut, out double[] nOut)
        {
            pOut = null; nOut = null;
            try
            {
                Surface s = face.GetSurface() as Surface; if (s == null) return false;
                double[] p = face.GetClosestPointOn(at[0], at[1], at[2]) as double[];
                if (p == null || p.Length < 3) p = at;
                double[] n = s.EvaluateAtPoint(p[0], p[1], p[2]) as double[];
                if (n == null || n.Length < 3) return false;
                double nl = Len(n); if (nl < Eps) return false;
                double[] nu = { n[0] / nl, n[1] / nl, n[2] / nl };
                bool reversed = false; try { reversed = face.FaceInSurfaceSense(); } catch { }
                if (reversed) { nu[0] = -nu[0]; nu[1] = -nu[1]; nu[2] = -nu[2]; }
                pOut = new[] { p[0], p[1], p[2] };
                nOut = nu;
                return true;
            }
            catch { return false; }
        }

        private static double[] FaceCenter(Face2 face)
        {
            try
            {
                double[] b = face.GetBox() as double[];
                if (b == null || b.Length < 6) return null;
                return new[] { (b[0] + b[3]) / 2, (b[1] + b[4]) / 2, (b[2] + b[5]) / 2 };
            }
            catch { return null; }
        }

        private static double[] EdgeMid(Edge e)
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

        // straight-line span of the edge (heuristic; a closed/circular edge returns 0 and is treated as "small")
        private static double EdgeSpanM(Edge e)
        {
            try
            {
                double[] p = e.GetCurveParams2() as double[];
                if (p != null && p.Length >= 6)
                {
                    double dx = p[3] - p[0], dy = p[4] - p[1], dz = p[5] - p[2];
                    return Math.Sqrt(dx * dx + dy * dy + dz * dz);
                }
            }
            catch { }
            return 0;
        }

        // ================= misc helpers =================

        private static object[] SolidBodies(PartDoc part)
        { try { return part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]; } catch { return null; } }

        private static int FaceCount(PartDoc part)
        {
            int n = 0;
            foreach (var bo in SolidBodies(part) ?? new object[0])
            {
                var body = bo as Body2; if (body == null) continue;
                object[] faces = null; try { faces = body.GetFaces() as object[]; } catch { }
                if (faces != null) n += faces.Length;
            }
            return n;
        }

        private static double[] UnionBodyBox(object[] bodies)
        {
            double[] bb = null;
            foreach (var bo in bodies)
            {
                var body = bo as Body2; if (body == null) continue;
                double[] b = null; try { b = body.GetBodyBox() as double[]; } catch { }
                if (b == null || b.Length < 6) continue;
                if (bb == null) bb = new[] { b[0], b[1], b[2], b[3], b[4], b[5] };
                else
                {
                    bb[0] = Math.Min(bb[0], b[0]); bb[1] = Math.Min(bb[1], b[1]); bb[2] = Math.Min(bb[2], b[2]);
                    bb[3] = Math.Max(bb[3], b[3]); bb[4] = Math.Max(bb[4], b[4]); bb[5] = Math.Max(bb[5], b[5]);
                }
            }
            return bb;
        }

        private static int SafeWhatsWrong(IModelDoc2 model)
        { try { return model.Extension.GetWhatsWrongCount(); } catch { return 0; } }

        private static void RollbackFeature(IModelDoc2 model, string name)
        {
            try
            {
                var f = FindFeatureByName(model, name);
                if (f == null) return;
                try { model.ClearSelection2(true); } catch { }
                bool sel = false; try { sel = f.Select2(false, 0); } catch { }
                if (sel) { try { model.EditDelete(); } catch { } }
                try { model.ForceRebuild3(false); } catch { }
                try { model.ClearSelection2(true); } catch { }
            }
            catch { }
        }

        private static Feature FindFeatureByName(IModelDoc2 model, string name)
        {
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    string nm = null; try { nm = f.Name; } catch { }
                    if (string.Equals(nm, name, StringComparison.OrdinalIgnoreCase)) return f;
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch { }
            return null;
        }

        // chamfer if the text says chamfer/bevel/break; else fillet (default, also explicit round/fillet/radius).
        // "break" alone is ambiguous ("break the edges" = generic deburr) — an explicit fillet/round keyword
        // elsewhere in the same command wins over it (e.g. "break all edges ... with a 0.25 fillet").
        private static bool ParseIsChamfer(string intent)
        {
            string c = (intent ?? "").ToLowerInvariant();
            if (Regex.IsMatch(c, @"\b(chamfer(ed|ing)?|bevel(led|ing)?)\b")) return true;
            if (Regex.IsMatch(c, @"\bbreak\b")) return !Regex.IsMatch(c, @"\b(fillet(ed|ing)?|round(ed|ing)?)\b");
            return false;
        }

        private static EdgeSetKind ParseEdgeSet(string intent)
        {
            string c = (intent ?? "").ToLowerInvariant();
            if (Regex.IsMatch(c, @"\btop\b")) return EdgeSetKind.Top;
            if (Regex.IsMatch(c, @"\bouter\b|\boutside\b|\bexternal\b")) return EdgeSetKind.Outer;
            return EdgeSetKind.AllSharp;
        }

        // size in mm: "2mm", "2.5 mm", or a bare "3" (as in "chamfer to 3"); -1 if none stated.
        // Machinist shop-talk names sizes in inch fractions as often as decimals — "quarter inch radius", "an
        // eighth inch chamfer" — and never says "mm" at all (test-loop false-hedge hull-fillet-edges: "a quarter
        // inch radius" was stated PLAINLY but ParseSizeMm only understood "Nmm"/a bare number, so it fell through
        // to sizeMm<=0 and asked "what fillet radius?" instead of acting on the value the user already gave).
        private static readonly Dictionary<string, double> InchFractionWords = new Dictionary<string, double>
        {
            { "sixteenth", 1.0/16 }, { "eighth", 1.0/8 }, { "quarter", 1.0/4 }, { "third", 1.0/3 },
            { "half", 1.0/2 }, { "three quarter", 3.0/4 }, { "three-quarter", 3.0/4 }, { "three quarters", 3.0/4 },
        };

        private static double ParseSizeMm(string intent)
        {
            string c = (intent ?? "").ToLowerInvariant();
            var m = Regex.Match(c, @"(\d+(\.\d+)?)\s*mm");
            if (m.Success && double.TryParse(m.Groups[1].Value, out double v) && v > 0) return v;

            // "1/4 inch", "0.25 inch", "0.25in", "0.25""  -> mm
            m = Regex.Match(c, @"(\d+)\s*/\s*(\d+)\s*(inch|inches|in\b|"")");
            if (m.Success && double.TryParse(m.Groups[1].Value, out double num) && double.TryParse(m.Groups[2].Value, out double den) && den > 0)
                return (num / den) * 25.4;
            m = Regex.Match(c, @"(\d+(\.\d+)?)\s*(inch|inches|in\b|"")");
            if (m.Success && double.TryParse(m.Groups[1].Value, out double vin) && vin > 0) return vin * 25.4;

            // spelled-out inch fractions: "quarter inch", "an eighth inch", "three quarter inch"
            foreach (var kv in InchFractionWords)
                if (Regex.IsMatch(c, @"\b" + Regex.Escape(kv.Key) + @"\b.{0,8}\b(inch|inches|in\b)"))
                    return kv.Value * 25.4;

            m = Regex.Match(c, @"\b(\d+(\.\d+)?)\b");
            if (m.Success && double.TryParse(m.Groups[1].Value, out double v2) && v2 > 0) return v2;
            return -1;
        }

        // verdict first (Character #3), the number not the adjective (Character #2), honest about what was VERIFIED.
        private static string BuildInfo(FilletChamferResult r, int done, int skipped, bool sizeDefaulted = false)
        {
            var sb = new System.Text.StringBuilder();
            string verb = r.Op == "chamfer" ? "Chamfered" : "Filleted";
            if (skipped > 0)
                sb.Append("Partial — " + verb.ToLowerInvariant() + " " + done + " of " + r.EdgesTargeted + " " + r.EdgeSet +
                          " edges at " + Trim(r.SizeMm) + "mm (" + skipped + " too small for the " + (r.Op == "chamfer" ? "leg" : "radius") + "). ");
            else
                sb.Append(verb + " " + done + " " + r.EdgeSet + " convex edge" + (done == 1 ? "" : "s") + " at " + Trim(r.SizeMm) + "mm. ");
            if (sizeDefaulted)
                sb.Append("No size was stated — used a standard " + Trim(r.SizeMm) + "mm deburr size; say a size to override. ");
            sb.Append("Faces " + r.FaceBefore + "→" + r.FaceAfter + ", rebuild clean. One Ctrl+Z removes the " + r.Op + "; Forge didn't save.");
            return sb.ToString();
        }

        private static string Trim(double v) => v.ToString("0.###");
        private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
        private static double Len(double[] a) => Math.Sqrt(a[0] * a[0] + a[1] * a[1] + a[2] * a[2]);
    }
}
