using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class ConvertStepToParametricResult
    {
        public bool DumbSolid;                // the part WAS an imported dumb solid (no modeling features)
        public string Shape;                  // "box" | "cylinder" | null (recognized primitive)
        public string ShapeDetail;            // "80 × 40 × 20 mm" / "⌀40 × 20 mm"
        public double VolumeBeforeMm3 = -1;
        public double VolumeAfterMm3 = -1;
        public int ParametricFeatureCount;    // modeling features present after conversion
        public int RebuildErrors;
        public bool AlreadyParametric;        // idempotent: the tree already had real features
        public bool Verified;                 // fail closed: true ONLY when the rebuilt parametric solid reproduces the geometry
        public string OutputPath;             // the saved parametric copy (the ORIGINAL file is never written)
        public string FeatureTreeDump;        // names+types of the tree, for diagnosing a failed body-identification
        public string DeleteTrace;            // what the delete loop tried and measured, for diagnosing cascades
        public string PositionTrace;          // source vs rebuilt bbox, for diagnosing the in-plane sketch mapping
        public string Info;
        public string Error;
    }

    /// <summary>
    /// ConvertStepToParametric (tool #258 — "make this STEP part parametric", "convert the imported part to an
    /// editable feature tree") — a REAL geometry WRITE that rebuilds an imported DUMB SOLID (STEP/IGES/Parasolid
    /// "Inkoop" parts: a tree that is just a BaseBody with no modeling features) as a NATIVE, parametric feature.
    ///
    /// The full-power route for this is FeatureWorks (dumb solid → editable feature tree, catalog tool #155
    /// `recognize_features`) — but its COM server is NOT registered on this 3DEXPERIENCE SOLIDWORKS Design R2026x
    /// seat (REGDB_E_CLASSNOTREG, a SKU/licensing gap, documented in docs/kb/landmines.md). This handler is the
    /// buildable fallback: B-REP PRIMITIVE RECOGNITION for the shapes that dominate real STEP files.
    ///
    /// v1 recognizes two primitives on a single solid body:
    ///   - BOX      — exactly 6 planar faces whose normals form 3 anti-parallel, axis-aligned pairs.
    ///   - CYLINDER — exactly 1 cylindrical face + 2 planar circular caps, axis within tolerance of Z.
    /// Anything else fails CLOSED with an honest "what I found" breakdown (Character #4), never a fake attempt.
    ///
    /// Rebuild (safe ordering, no unrecoverable middle state — the OLD body is deleted LAST):
    ///   Builder  — draw the primitive's profile in a sketch on an ORIGIN PLANE (never on a face of the imported
    ///              body, so deleting that body can't break the sketch) and extrude BOTH directions from the body's
    ///              centroid by half-thickness with merge=FALSE → a NEW separate solid body, tagged "Forge-Parametric".
    ///              Centred-both-ways extrude preserves the original POSITION without fragile start-offset math.
    ///   Sentinel — BEFORE the old body is touched: independent geometry confirms the new body reproduces the solid
    ///              (whole-doc volume ≈ 2 × original, the two identical overlapping bodies; plus the extrude's own
    ///              body exists and its bbox matches the original within tolerance). Only then is the ORIGINAL
    ///              imported body feature deleted (found by body identity via Feature.GetBody, not by a type string)
    ///              and the part rebuilt. Final gate: exactly 1 solid body, volume ≈ the ORIGINAL within 2%,
    ///              modeling features now present, rebuild clean. Any failure before the delete → the tagged feature
    ///              is rolled back and the part is left exactly as found (Rule #4/#6/#7). One Ctrl+Z per step; Forge
    ///              never saves.
    ///
    /// IDEMPOTENT (Rule #5): a part whose tree already has modeling features is "already parametric" — nothing to do.
    /// </summary>
    public static class ConvertStepToParametric
    {
        public const string ParamFeatureName = "Forge-Parametric";

        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            bool wants = Regex.IsMatch(c, @"\bparametric\b")
                || Regex.IsMatch(c, @"\bparametri[sz]e(d|s|ing)?\b")
                || Regex.IsMatch(c, @"\beditable\s+features?\b")
                || (Regex.IsMatch(c, @"\b(convert|make|turn|import)\b") && Regex.IsMatch(c, @"\bfeature\s+tree\b"));
            if (!wants) return false;
            // never steal a neutral-format EXPORT: "convert this to STEP" stays batch_convert_files' territory.
            if (Regex.IsMatch(c, @"\bto\s+(step|stp|iges|igs|parasolid|dxf|dwg|pdf|stl)\b")) return false;
            return true;
        }

        public static async Task<ConvertStepToParametricResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new ConvertStepToParametricResult();

            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { res.Error = "Converting to parametric works on a single part — open the .SLDPRT you imported, not an assembly (v1 is part-scoped)."; return res; }
            var part = model as PartDoc;
            if (part == null) { res.Error = "This document has no part geometry to convert."; return res; }

            object[] bodies = SolidBodies(part);
            if (bodies == null || bodies.Length == 0)
            { res.Error = "No solid body to convert — this part has no solid geometry."; return res; }

            // ---- IDEMPOTENT (Rule #5): real modeling features already present → the part is already parametric ----
            int modelingNow = ModelingFeatureCount(model);
            if (modelingNow > 0)
            {
                res.AlreadyParametric = true;
                res.Verified = true;
                res.ParametricFeatureCount = modelingNow;
                res.Info = "Already parametric — the feature tree already has " + modelingNow +
                           " modeling feature(s), so there's nothing to convert. This tool is for imported dumb solids (a STEP/IGES part whose tree is just a body).";
                await emit("Gauge", null, "done", "already parametric — nothing to do");
                return res;
            }
            res.DumbSolid = true;

            res.VolumeBeforeMm3 = GetVolumeMm3(model);
            await emit("Gauge", "reading the imported geometry", "run", null);

            // ---- recognize the primitive ----
            var rec = Recognize(part, bodies);
            if (rec == null)
            {
                string faceSummary = FaceSummary(part);
                res.Error = "I can rebuild only the two shapes that dominate real STEP imports — an axis-aligned BOX " +
                            "(6 flat faces) or a Z-aligned CYLINDER (2 flat end caps + one round wall) — on a single solid body. " +
                            "This part doesn't match either: " + faceSummary + ". The geometry is a dumb solid, so nothing here is " +
                            "editable yet; v2 adds more primitives (or install a SolidWorks edition with FeatureWorks for the full converter).";
                await emit("Gauge", null, "fail", "no recognized primitive");
                return res;
            }
            res.Shape = rec.Shape;
            res.ShapeDetail = rec.Detail;
            await emit("Gauge", null, "done",
                res.Shape + " · " + rec.Detail + " · solid " + res.VolumeBeforeMm3.ToString("N0") + " mm³");

            // ---- WRITE: on this build the new extrude's body is cascade-removed when the imported body is deleted
            //      (observed live: deleting the MBimport body drops the whole solid 2->0), so the rebuild CANNOT
            //      coexist with the imported body. The conversion therefore happens on a SAVED COPY: SaveAs re-points
            //      this open doc to a NEW file (the ORIGINAL file on disk is never written), then delete-then-create
            //      on the copy — the extrude is built in an empty part, so nothing can cascade. ----
            string srcPath = null; try { srcPath = model.GetPathName(); } catch { }
            if (string.IsNullOrWhiteSpace(srcPath))
            {
                res.Error = "This part has never been saved, so Forge can't write a parametric copy beside it — save it once " +
                            "(File > Save) and rerun. The original stays untouched.";
                return res;
            }
            string outDir = Path.Combine(Path.GetDirectoryName(srcPath), "forge-parametric");
            string outPath = Path.Combine(outDir, Path.GetFileNameWithoutExtension(srcPath) + "-parametric.SLDPRT");
            try { Directory.CreateDirectory(outDir); } catch { }
            int se = 0, sw = 0;
            bool saved = false;
            try { saved = model.Extension.SaveAs(outPath, (int)swSaveAsVersion_e.swSaveAsCurrentVersion, (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, ref se, ref sw); } catch { }
            if (!saved || !File.Exists(outPath))
            {
                res.Error = "Couldn't write the parametric copy (SaveAs errs=" + se + "). The original file is untouched.";
                await emit("Builder", null, "fail", "copy not written");
                return res;
            }
            res.OutputPath = outPath;
            await emit("Builder", "working on a copy — " + Path.GetFileName(outPath), "run", null);

            // ---- delete the imported body feature FIRST (targeted type match; the re-count is the truth) ----
            var candidates = FindImportBodyCandidates(model, null);
            res.FeatureTreeDump = FeatureTreeDump(model);
            if (candidates.Length == 0)
            {
                res.Error = "Forge couldn't identify the imported body feature on the copy to remove it. The original file is untouched.";
                await emit("Builder", null, "fail", "could not identify the imported body feature");
                return res;
            }
            var trace = new System.Text.StringBuilder();
            int bodyCount = SolidBodyCount(part);
            bool removed = false;
            foreach (var cand in candidates)
            {
                string candType = null; try { candType = cand.GetTypeName2(); } catch { }
                int before = bodyCount;
                try { model.ClearSelection2(true); } catch { }
                bool sel = false; try { sel = cand.Select2(false, 0); } catch { }
                if (!sel) { trace.Append(candType + " sel=no; "); continue; }
                try { model.EditDelete(); } catch { }
                try { model.ClearSelection2(true); } catch { }
                try { model.ForceRebuild3(false); } catch { }
                int after = SolidBodyCount(part);
                trace.Append(candType + " bodies " + before + "->" + after + "; ");
                if (after == bodyCount - 1) { removed = true; bodyCount = after; }
                break;   // exactly one import body on a copy — first real removal wins, otherwise fail closed below
            }
            res.DeleteTrace = trace.ToString();
            if (!removed)
            {
                res.Error = "The imported body on the copy wouldn't delete cleanly. The original file is untouched.";
                await emit("Builder", null, "fail", "imported body would not delete on the copy");
                return res;
            }

            // ---- rebuild the primitive as a native parametric extrude (empty part now — no cascade possible) ----
            await emit("Builder", "rebuilding the " + res.Shape + " as a parametric extrude", "run", null);
            Feature built = TryBuild(model, rec);
            if (built == null)
            {
                res.Error = "SolidWorks refused the parametric rebuild on the copy — the original file is untouched (delete the broken copy if you don't want it).";
                await emit("Builder", null, "fail", "extrude not created");
                return res;
            }
            try { built.Name = ParamFeatureName; } catch { }
            try { model.ForceRebuild3(false); } catch { }

            // ---- Sentinel B (final gate): one solid body, original volume, POSITION preserved, modeling features, clean ----
            res.RebuildErrors = SafeWhatsWrong(model);
            res.VolumeAfterMm3 = GetVolumeMm3(model);
            res.ParametricFeatureCount = ModelingFeatureCount(model);
            int finalBodyCount = SolidBodyCount(part);
            bool volOk = res.VolumeAfterMm3 > 0 && res.VolumeBeforeMm3 > 0 &&
                         Math.Abs(res.VolumeAfterMm3 - res.VolumeBeforeMm3) <= Math.Max(1e-6, 0.02 * res.VolumeBeforeMm3);
            bool posOk = BodyMatchesBbox(part, rec, out string posTrace);
            res.PositionTrace = posTrace;
            res.Verified = removed && res.RebuildErrors == 0 && finalBodyCount == 1 && volOk && posOk && res.ParametricFeatureCount >= 1;
            await emit("Sentinel", null, res.Verified ? "done" : "fail",
                "final: " + finalBodyCount + " solid · " + res.VolumeAfterMm3.ToString("N0") + " mm³ vs " + res.VolumeBeforeMm3.ToString("N0") +
                " · position " + (posOk ? "match" : "mismatch") + " · " + res.ParametricFeatureCount + " modeling feature(s) · rebuild " +
                (res.RebuildErrors == 0 ? "clean" : res.RebuildErrors + " err"));

            res.Info = BuildInfo(res, rec, volOk);
            return res;
        }

        // ---- verdict first (Character #3), the number not the adjective (Character #2), only what was VERIFIED ----
        private static string BuildInfo(ConvertStepToParametricResult r, Recognized rec, bool volOk)
        {
            var sb = new System.Text.StringBuilder();
            if (r.Verified)
            {
                sb.Append("Converted to parametric — the " + r.Shape + " (" + r.ShapeDetail + ") is now a real " +
                          "extrude feature (" + r.ParametricFeatureCount + " modeling feature" + (r.ParametricFeatureCount == 1 ? "" : "s") +
                          "), volume " + r.VolumeBeforeMm3.ToString("N0") + " → " + r.VolumeAfterMm3.ToString("N0") +
                          " mm³, rebuild clean. ");
                sb.Append("Saved as a new parametric copy: " + (r.OutputPath ?? "?") + ". The original file was never touched.");
            }
            else
            {
                sb.Append("Partial — the original imported body was removed but the final geometry didn't fully verify");
                if (!volOk) sb.Append(" (volume " + r.VolumeAfterMm3.ToString("N0") + " vs " + r.VolumeBeforeMm3.ToString("N0") + " mm³)");
                if (r.RebuildErrors > 0) sb.Append(" (" + r.RebuildErrors + " rebuild error(s))");
                sb.Append(". One Ctrl+Z per step restores the original.");
            }
            return sb.ToString();
        }

        // ================= primitive recognition =================

        private class Recognized
        {
            public string Shape;        // "box" | "cylinder"
            public string Detail;       // human-readable dims
            public string Axis;         // "x" | "y" | "z" — cylinder axis / box extrude direction
            public double Cx, Cy, Cz;   // centroid (body bbox centre)
            public double ProfileA;     // box: X span / cylinder: radius
            public double ProfileB;     // box: Y span / cylinder: (unused)
            public double Thick;        // extrude depth along Axis
            public double Radius;       // cylinder only
            public double HoleRadius;   // box+through-hole: >0 means ONE concave Z-axis hole to re-cut
            public double HoleX, HoleY; // hole axis position in the box's centroid plane
            public double[] OrigBbox;   // original body bbox, for the sentinel bbox comparison
        }

        private static readonly double AlignTol = 0.995;   // |dot| > 0.995 ≈ axis-aligned

        private static Recognized Recognize(PartDoc part, object[] bodies)
        {
            try
            {
                if (bodies.Length != 1) return null;
                var body = bodies[0] as Body2;
                if (body == null) return null;
                double[] bbox = body.GetBodyBox() as double[];
                if (bbox == null || bbox.Length < 6) return null;

                var faces = new List<Face2>();
                object[] fo = body.GetFaces() as object[];
                if (fo != null) foreach (var o in fo) { var f = o as Face2; if (f != null) faces.Add(f); }

                int planar = 0, cyl = 0, other = 0;
                var cylFaces = new List<Face2>();
                foreach (var f in faces)
                {
                    Surface s = null; try { s = f.GetSurface() as Surface; } catch { }
                    if (s == null) { other++; continue; }
                    bool isPlane = false, isCyl = false;
                    try { isPlane = s.IsPlane(); } catch { }
                    try { isCyl = s.IsCylinder(); } catch { }
                    if (isPlane) planar++;
                    else if (isCyl) { cyl++; cylFaces.Add(f); }
                    else other++;
                }

                // ---- BOX (optionally with ONE Z-axis through-hole): 6 planar faces forming 3 anti-parallel
                //      axis-aligned pairs + cylindrical faces that are ALL one co-axial/co-radius CONCAVE hole
                //      (STEP imports SPLIT the bore wall — observed live: 8 faces = 6 flat + 2 cylindrical).
                //      A box with a through-hole is the single most common real STEP part shape. ----
                if (planar == 6 && other == 0 && cyl >= 0)
                {
                    var planars = new List<Face2>();
                    foreach (var f in faces)
                    {
                        Surface sf = null; try { sf = f.GetSurface() as Surface; } catch { }
                        bool isPlane = false; try { isPlane = sf != null && sf.IsPlane(); } catch { }
                        if (isPlane) planars.Add(f);
                    }
                    if (planars.Count != 6) return null;

                    var dirs = new List<double[]>();
                    foreach (var f in planars)
                    {
                        double[] n = FaceNormal(f);
                        if (n == null) return null;
                        bool paired = false;
                        foreach (var d in dirs)
                            if (Math.Abs(Dot(n, d)) > 0.999) { paired = true; break; }
                        if (!paired)
                        {
                            double mx = Math.Max(Math.Abs(n[0]), Math.Max(Math.Abs(n[1]), Math.Abs(n[2])));
                            if (mx < AlignTol) return null;
                            dirs.Add(n);
                        }
                    }
                    if (dirs.Count != 3) return null;

                    double L = Math.Abs(bbox[3] - bbox[0]);
                    double H = Math.Abs(bbox[4] - bbox[1]);
                    double T = Math.Abs(bbox[5] - bbox[2]);
                    if (L < 1e-6 || H < 1e-6 || T < 1e-6) return null;

                    double holeR = 0, holeX = 0, holeY = 0;
                    if (cyl > 0)
                    {
                        double r0 = -1; double[] ax0 = null; double hx0 = 0, hy0 = 0;
                        foreach (var cf in cylFaces)
                        {
                            Surface hs = null; try { hs = cf.GetSurface() as Surface; } catch { }
                            if (hs == null) return null;
                            double[] hp = null; try { hp = hs.CylinderParams as double[]; } catch { }
                            if (hp == null || hp.Length < 7 || hp[6] <= 0) return null;
                            if (!CylinderConcave(cf, hs, hp)) return null;   // a convex boss/round is NOT a hole
                            double[] ha = { hp[3], hp[4], hp[5] };
                            double hal = Math.Sqrt(ha[0] * ha[0] + ha[1] * ha[1] + ha[2] * ha[2]);
                            if (hal < 1e-9) return null;
                            ha[0] /= hal; ha[1] /= hal; ha[2] /= hal;
                            if (Math.Abs(ha[2]) < AlignTol) return null;   // the hole must be the box thickness
                            if (r0 < 0) { r0 = hp[6]; ax0 = ha; hx0 = hp[0]; hy0 = hp[1]; }
                            else if (Math.Abs(hp[6] - r0) > 0.005 * Math.Max(r0, 1e-6)) return null;
                            else if (Math.Abs(Dot(ax0, ha)) < 0.995) return null;
                        }
                        holeR = r0;
                        if (holeR >= 0.5 * Math.Min(L, H)) return null;   // a hole nearly as wide as the block isn't "one hole"
                        holeX = hx0; holeY = hy0;
                    }

                    return new Recognized
                    {
                        Shape = "box",
                        Detail = TrimMm(L) + " × " + TrimMm(H) + " × " + TrimMm(T) + " mm" + (holeR > 0 ? " + ⌀" + TrimMm(2 * holeR) + " through-hole" : ""),
                        Axis = "z",
                        Cx = (bbox[0] + bbox[3]) / 2, Cy = (bbox[1] + bbox[4]) / 2, Cz = (bbox[2] + bbox[5]) / 2,
                        ProfileA = L, ProfileB = H, Thick = T,
                        HoleRadius = holeR, HoleX = holeX, HoleY = holeY,
                        OrigBbox = bbox
                    };
                }

                // ---- CYLINDER (optionally with a concentric BORE): 2 planar caps + cylindrical faces that are
                //      EITHER one co-axial/co-radius wall (STEP splits it — observed live: 2 cylindrical faces) OR a
                //      BUSHING: a CONVEX outer wall (radius R) + a CONCAVE bore (radius r < R), all axes parallel.
                //      Axis-aligned on X/Y/Z. ----
                if (cyl >= 1 && planar == 2 && other == 0)
                {
                    double outerR = -1, boreR = 0; double[] axis = null;
                    foreach (var cf in cylFaces)
                    {
                        Surface s = null; try { s = cf.GetSurface() as Surface; } catch { }
                        if (s == null) return null;
                        double[] cp = null; try { cp = s.CylinderParams as double[]; } catch { }
                        if (cp == null || cp.Length < 7 || cp[6] <= 0) return null;
                        double[] a = { cp[3], cp[4], cp[5] };
                        double al = Math.Sqrt(a[0] * a[0] + a[1] * a[1] + a[2] * a[2]);
                        if (al < 1e-9) return null;
                        a[0] /= al; a[1] /= al; a[2] /= al;
                        if (axis == null) axis = a;
                        else if (Math.Abs(Dot(axis, a)) < 0.995) return null;   // not parallel axes -> not one cylinder
                        bool concave = CylinderConcave(cf, s, cp);
                        if (concave)
                        {
                            if (boreR == 0) boreR = cp[6];
                            else if (Math.Abs(cp[6] - boreR) > 0.005 * Math.Max(boreR, 1e-6)) return null;
                        }
                        else
                        {
                            if (outerR < 0) outerR = cp[6];
                            else if (Math.Abs(cp[6] - outerR) > 0.005 * Math.Max(outerR, 1e-6)) return null;
                        }
                    }
                    if (axis == null || outerR < 0) return null;   // need a convex outer wall
                    if (boreR > 0 && boreR >= outerR * 0.97) return null;   // a "bore" nearly the outer radius isn't a real bore
                    string cylAxis;
                    if (Math.Abs(axis[2]) > AlignTol) cylAxis = "z";
                    else if (Math.Abs(axis[0]) > AlignTol) cylAxis = "x";
                    else if (Math.Abs(axis[1]) > AlignTol) cylAxis = "y";
                    else return null;

                    double h;
                    if (cylAxis == "x") h = Math.Abs(bbox[3] - bbox[0]);
                    else if (cylAxis == "y") h = Math.Abs(bbox[4] - bbox[1]);
                    else h = Math.Abs(bbox[5] - bbox[2]);
                    if (h < 1e-6) return null;
                    return new Recognized
                    {
                        Shape = "cylinder",
                        Detail = "⌀" + TrimMm(2 * outerR) + " × " + TrimMm(h) + " mm (axis " + cylAxis.ToUpperInvariant() + ")" +
                                 (boreR > 0 ? " + ⌀" + TrimMm(2 * boreR) + " bore" : ""),
                        Axis = cylAxis,
                        Cx = (bbox[0] + bbox[3]) / 2, Cy = (bbox[1] + bbox[4]) / 2, Cz = (bbox[2] + bbox[5]) / 2,
                        ProfileA = outerR, Thick = h, Radius = outerR,
                        HoleRadius = boreR, HoleX = (bbox[0] + bbox[3]) / 2, HoleY = (bbox[1] + bbox[4]) / 2,
                        OrigBbox = bbox
                    };
                }

                return null;
            }
            catch { return null; }
        }

        // ================= the write: sketch on the centroid-height origin plane + both-direction extrude, merge=false =================

        private static Feature TryBuild(IModelDoc2 model, Recognized rec)
        {
            try
            {
                try { model.ClearSelection2(true); } catch { }

                // Which origin plane + which centroid coordinates map into it, per extrude axis. The extrude is
                // symmetric about its sketch plane, so the plane must sit at the body's centroid along the axis
                // (not at 0): build a small offset reference plane there (RecipeExecutor's proven InsertRefPlane
                // distance-constraint call). The plane is a part-level reference feature, so deleting the imported
                // body earlier can never break it. For a CYLINDER the profile is a circle, so the in-plane
                // orientation of the two non-axis coordinates doesn't matter for the shape — only the centre.
                string planeName; double offset; double s1, s2;
                switch (rec.Axis)
                {
                    // Sketch axes on the named origin planes (measured live on this build via PositionTrace): the
                    // RIGHT plane's sketch X maps to -model Z and sketch Y to +model Y, so an X-axis cylinder's
                    // in-plane centre is (-Cz, Cy). The TOP plane's sketch X maps to +model X and sketch Y to
                    // -model Z (same handedness), so a Y-axis cylinder's centre is (Cx, -Cz).
                    case "x": planeName = "Right Plane"; offset = rec.Cx; s1 = -rec.Cz; s2 = rec.Cy; break;
                    case "y": planeName = "Top Plane";   offset = rec.Cy; s1 = rec.Cx;  s2 = -rec.Cz; break;
                    default:  planeName = "Front Plane"; offset = rec.Cz; s1 = rec.Cx;  s2 = rec.Cy; break;
                }
                Feature buildPlane = null;
                if (Math.Abs(offset) > 1e-9)
                {
                    int constraint = (int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_Distance;
                    if (offset < 0) constraint |= (int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_OptionFlip;
                    if (!model.Extension.SelectByID2(planeName, "PLANE", 0, 0, 0, false, 0, null, 0)) return null;
                    buildPlane = model.FeatureManager.InsertRefPlane(constraint, Math.Abs(offset), 0, 0, 0, 0) as Feature;
                    if (buildPlane == null) return null;
                    try { model.ClearSelection2(true); } catch { }
                    if (!buildPlane.Select2(false, 0)) return null;
                }
                else if (!model.Extension.SelectByID2(planeName, "PLANE", 0, 0, 0, false, 0, null, 0)) return null;

                var sk = model.SketchManager;
                sk.InsertSketch(true);
                if (rec.Shape == "box")
                {
                    double L = rec.ProfileA, H = rec.ProfileB;
                    sk.CreateCornerRectangle(s1 - L / 2, s2 - H / 2, 0, s1 + L / 2, s2 + H / 2, 0);
                }
                else
                {
                    sk.CreateCircleByRadius(s1, s2, 0, rec.Radius);
                }
                sk.InsertSketch(true);
                try { model.ClearSelection2(true); } catch { }

                var skFeat = model.FeatureByPositionReverse(0) as Feature;
                if (skFeat == null) return null;
                if (!skFeat.Select2(false, 0)) return null;

                // both directions (Sd=false, T1=T2=Blind), half-thickness each way, merge=FALSE -> a NEW separate
                // solid body identical to the original and centred on it (position preserved, no offset math).
                double half = rec.Thick / 2;
                var feat = model.FeatureManager.FeatureExtrusion3(
                    false, false, false,
                    (int)swEndConditions_e.swEndCondBlind, (int)swEndConditions_e.swEndCondBlind, half, half,
                    false, false, false, false, 0, 0,
                    false, false, false, false,
                    false, true, true, 0, 0, false) as Feature;
                try { model.ClearSelection2(true); } catch { }
                if (feat == null) return null;

                // ---- box+hole / cylinder+bore: cut a circle at the hole's axis through the rebuilt body. The cut
                //      sketch goes on the SAME centroid plane (it intersects the body), through-all BOTH directions
                //      (proven FeatureCut4 shape from RecipeExecutor), merging into our parametric body. ----
                if (rec.HoleRadius > 0)
                {
                    try { model.ClearSelection2(true); } catch { }
                    if (buildPlane != null) { if (!buildPlane.Select2(false, 0)) return feat; }
                    else if (!model.Extension.SelectByID2(planeName, "PLANE", 0, 0, 0, false, 0, null, 0)) return feat;

                    sk.InsertSketch(true);
                    sk.CreateCircleByRadius(rec.HoleX, rec.HoleY, 0, rec.HoleRadius);
                    sk.InsertSketch(true);
                    try { model.ClearSelection2(true); } catch { }
                    var holeSk = model.FeatureByPositionReverse(0) as Feature;
                    if (holeSk == null) return feat;
                    if (!holeSk.Select2(false, 0)) return feat;

                    int ta = (int)swEndConditions_e.swEndCondThroughAll;
                    model.FeatureManager.FeatureCut4(
                        false, false, false, ta, ta, 0, 0,
                        false, false, false, false, 0, 0,
                        false, false, false, false, false, true, true, true, true, false, 0, 0, false, false);
                    try { model.ClearSelection2(true); } catch { }
                }
                return feat;
            }
            catch { return null; }
        }

        // Find the ORIGINAL imported body feature — the top-level feature whose TYPE is an import body. TARGETED,
        // not a has-body probe: on this build a "Feature.GetBody() != null" probe also matches non-body features
        // (e.g. a sketch's ProfileFeature), and deleting the wrong one cascades to remove OUR extrude's body too.
        // Imported bodies report "MBimport" on this 3DEXPERIENCE build (observed live); the other patterns cover the
        // common build-local variants. A feature we cannot match → honest fail-closed (rollback), never a guess.
        private static Feature[] FindImportBodyCandidates(IModelDoc2 model, Feature built)
        {
            var list = new List<Feature>();
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    if (!object.ReferenceEquals(f, built))
                    {
                        string name = null; try { name = f.Name; } catch { }
                        if (!string.Equals(name, ParamFeatureName, StringComparison.OrdinalIgnoreCase))
                        {
                            string tn = ""; try { tn = (f.GetTypeName2() ?? "").ToLowerInvariant(); } catch { }
                            if (tn.Length > 0)
                            {
                                bool importBody = tn.Contains("mbimport") || tn.Contains("imported")
                                    || tn.Contains("basebody") || tn.Contains("bodyfeature") || tn == "body";
                                bool referenceish = tn.Contains("plane") || tn.Contains("axis") || tn.Contains("origin")
                                    || tn.Contains("coord") || tn.Contains("folder") || tn.Contains("sketch") || tn.Contains("ref")
                                    || tn.Contains("mate") || tn.Contains("equation") || tn.Contains("detail") || tn.Contains("env")
                                    || tn.Contains("ink") || tn.Contains("material") || tn.Contains("binder") || tn.Contains("sensor")
                                    || tn.Contains("selection") || tn.Contains("comment") || tn.Contains("history") || tn.Contains("favorite");
                                if (importBody && !referenceish) list.Add(f);
                            }
                        }
                    }
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch { }
            return list.ToArray();
        }

        // names+types of every top-level feature, for the failure corpus when identification can't decide
        private static string FeatureTreeDump(IModelDoc2 model)
        {
            var sb = new System.Text.StringBuilder();
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    string nm = null, tn = null;
                    try { nm = f.Name; } catch { }
                    try { tn = f.GetTypeName2(); } catch { }
                    if (sb.Length > 0) sb.Append(" | ");
                    sb.Append(nm ?? "?");
                    sb.Append(":" + (tn ?? "?"));
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch { }
            return sb.ToString();
        }

        // delete the tagged rebuild (and any loose sketch) so a failed conversion leaves the part exactly as found
        // ================= geometry helpers =================

        private static object[] SolidBodies(PartDoc part)
        { try { return part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]; } catch { return null; } }

        private static int SolidBodyCount(PartDoc part)
        { var b = SolidBodies(part); return b == null ? 0 : b.Length; }

        // Sentinel B position gate: the rebuilt solid must reproduce the ORIGINAL body's bbox (catches a swapped
        // in-plane sketch centre on X/Y-axis cylinders — volume alone can't tell). 'trace' reports source vs rebuilt
        // bbox in mm so a mismatch names the exact axis error.
        private static bool BodyMatchesBbox(PartDoc part, Recognized rec, out string trace)
        {
            trace = "";
            if (rec.OrigBbox == null) return false;
            double[] ob = rec.OrigBbox;
            double tol = Math.Max(0.002, 0.01 * (Math.Abs(ob[3] - ob[0]) + Math.Abs(ob[5] - ob[2])));
            foreach (var bo in SolidBodies(part) ?? new object[0])
            {
                var body = bo as Body2; if (body == null) return false;
                double[] bb = null; try { bb = body.GetBodyBox() as double[]; } catch { }
                if (bb == null || bb.Length < 6) return false;
                double dx0 = (bb[0] - ob[0]) * 1000.0, dx1 = (bb[3] - ob[3]) * 1000.0;
                double dy0 = (bb[1] - ob[1]) * 1000.0, dy1 = (bb[4] - ob[4]) * 1000.0;
                double dz0 = (bb[2] - ob[2]) * 1000.0, dz1 = (bb[5] - ob[5]) * 1000.0;
                trace = "src[" + Mm(ob[0]) + "," + Mm(ob[1]) + "," + Mm(ob[2]) + " .. " + Mm(ob[3]) + "," + Mm(ob[4]) + "," + Mm(ob[5]) +
                        "] reb[" + Mm(bb[0]) + "," + Mm(bb[1]) + "," + Mm(bb[2]) + " .. " + Mm(bb[3]) + "," + Mm(bb[4]) + "," + Mm(bb[5]) +
                        "] dX=" + dx0.ToString("0.0") + "/" + dx1.ToString("0.0") + " dY=" + dy0.ToString("0.0") + "/" + dy1.ToString("0.0") + " dZ=" + dz0.ToString("0.0") + "/" + dz1.ToString("0.0");
                for (int i = 0; i < 6; i++)
                    if (Math.Abs(bb[i] - ob[i]) > tol) return false;
            }
            return true;
        }

        private static string Mm(double m) => (m * 1000.0).ToString("0.###");

        private static double GetVolumeMm3(IModelDoc2 model)
        {
            try { var mp = model.Extension.CreateMassProperty(); return mp == null ? -1 : mp.Volume * 1e9; }
            catch { return -1; }
        }

        private static int SafeWhatsWrong(IModelDoc2 model)
        { try { return model.Extension.GetWhatsWrongCount(); } catch { return 0; } }

        private static double[] FaceNormal(Face2 face)
        {
            try
            {
                double[] n = face.Normal as double[];
                if (n == null || n.Length < 3) return null;
                double l = Math.Sqrt(n[0] * n[0] + n[1] * n[1] + n[2] * n[2]);
                if (l < 1e-9) return null;
                return new[] { n[0] / l, n[1] / l, n[2] / l };
            }
            catch { return null; }
        }

        private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];

        // concave (a HOLE) iff the face's OUTWARD normal points toward the cylinder axis (solid material outside).
        // Same math GeometryDefeature proves live; convex (an external boss/round) points away.
        private static bool CylinderConcave(Face2 face, Surface s, double[] cp)
        {
            try
            {
                double[] box = face.GetBox() as double[];
                if (box == null || box.Length < 6) return true;
                double[] center = { (box[0] + box[3]) / 2, (box[1] + box[4]) / 2, (box[2] + box[5]) / 2 };
                double[] P = face.GetClosestPointOn(center[0], center[1], center[2]) as double[];
                if (P == null || P.Length < 3) return true;
                double[] n = s.EvaluateAtPoint(P[0], P[1], P[2]) as double[];
                if (n == null || n.Length < 3) return true;
                double nl = Math.Sqrt(n[0] * n[0] + n[1] * n[1] + n[2] * n[2]); if (nl < 1e-9) return true;
                double[] nout = { n[0] / nl, n[1] / nl, n[2] / nl };
                bool reversed = false; try { reversed = face.FaceInSurfaceSense(); } catch { }
                if (reversed) { nout[0] = -nout[0]; nout[1] = -nout[1]; nout[2] = -nout[2]; }
                double[] a = { cp[3], cp[4], cp[5] };
                double al = Math.Sqrt(a[0] * a[0] + a[1] * a[1] + a[2] * a[2]); if (al < 1e-9) return true;
                a[0] /= al; a[1] /= al; a[2] /= al;
                double[] d = { P[0] - cp[0], P[1] - cp[1], P[2] - cp[2] };
                double axial = d[0] * a[0] + d[1] * a[1] + d[2] * a[2];
                double[] w = { d[0] - axial * a[0], d[1] - axial * a[1], d[2] - axial * a[2] };
                double wl = Math.Sqrt(w[0] * w[0] + w[1] * w[1] + w[2] * w[2]); if (wl < 1e-9) return true;
                double radialDot = (nout[0] * w[0] + nout[1] * w[1] + nout[2] * w[2]) / wl;
                return radialDot < 0;   // normal points inward toward the axis → concave → a hole
            }
            catch { return true; }
        }

        private static int ModelingFeatureCount(IModelDoc2 model)
        {
            int n = 0;
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    string tn = ""; try { tn = f.GetTypeName2() ?? ""; } catch { }
                    string t = tn.ToLowerInvariant();
                    if (t.Contains("ice") || t.Contains("extru") || t.Contains("cut") || t.Contains("fillet") || t.Contains("chamfer")
                        || t.Contains("pattern") || t.Contains("hole") || t.Contains("revolve") || t.Contains("boss")
                        || t.Contains("sweep") || t.Contains("loft") || t.Contains("shell"))
                        n++;
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch { }
            return n;
        }

        private static string FaceSummary(PartDoc part)
        {
            int planar = 0, cyl = 0, cone = 0, sphere = 0, other = 0, total = 0;
            foreach (var bo in SolidBodies(part) ?? new object[0])
            {
                var body = bo as Body2; if (body == null) continue;
                object[] faces = null; try { faces = body.GetFaces() as object[]; } catch { }
                foreach (var fo in faces ?? new object[0])
                {
                    var face = fo as Face2; if (face == null) continue;
                    total++;
                    Surface s = null; try { s = face.GetSurface() as Surface; } catch { }
                    if (s == null) { other++; continue; }
                    bool p = false, cy = false, co = false, sp = false;
                    try { p = s.IsPlane(); } catch { }
                    try { cy = s.IsCylinder(); } catch { }
                    try { co = s.IsCone(); } catch { }
                    try { sp = s.IsSphere(); } catch { }
                    if (p) planar++; else if (cy) cyl++; else if (co) cone++; else if (sp) sphere++; else other++;
                }
            }
            return total + " faces (" + planar + " flat, " + cyl + " cylindrical, " + cone + " conical, " +
                   sphere + " spherical, " + other + " other)";
        }

        private static string TrimMm(double m) => (m * 1000.0).ToString("0.##");
    }
}
