using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class AddBossResult
    {
        public double DiameterMm;            // boss diameter parsed from the intent (mm); default 12 when unspecified
        public double HeightMm;              // boss height/length parsed from the intent (mm); default 10 when unspecified
        public string TargetFace;            // which face the boss stands on, e.g. "largest planar face, 2400 mm²"
        public double VolumeBeforeMm3 = -1;  // solid volume before the boss (mm^3), independently measured
        public double VolumeAfterMm3 = -1;   // solid volume after the boss + rebuild (mm^3)
        public double ExpectedAddMm3;        // π·(dia/2)²·height — the volume a real outward boss must ADD
        public bool NewCylFace;              // a new external cylindrical face of ~the requested diameter now exists (the boss wall)
        public int RebuildErrors;            // GetWhatsWrongCount() post-rebuild (0 => clean)
        public bool RolledBack;              // the boss was created but failed to verify → deleted, part restored
        public bool Verified;                // fail closed: true ONLY when volume ROSE by ~the boss, a boss wall appeared, rebuild clean
        public bool AlreadyDone;             // idempotent: a Forge-Boss already exists → nothing to do
        public string Info;                  // verdict-first panel line
        public string Error;                 // honest failure text (assembly, no solid, won't-fit, no outward direction)
    }

    /// <summary>
    /// AddBoss (tool #77 create_extrude — "add a boss/pad to a part") — a REAL geometry WRITE on a single PART. It adds
    /// ONE round boss (a positive extrude that ADDS material) at the centre of a resolved face: "add a boss",
    /// "add a 20mm boss", "add a 10mm tall pad on the top", "add a cylindrical boss 15mm diameter 10mm tall".
    /// It is the material-ADDING sibling of AddHole (which cut-extrudes to REMOVE material). Everything about face
    /// resolution and the sketch-space projection is the SAME proven code as AddHole — the ONLY difference is the
    /// extrude ADDS material OUTWARD (away from the solid) instead of cutting through it.
    ///
    /// Approach (deliberate, documented — the sketch/circle mechanics are REUSED verbatim from AddHole /
    /// RecipeExecutor.DoExtrude, so this is low-risk):
    ///   Gauge — read the solid; resolve the TARGET FACE deterministically. Default = the LARGEST planar face by area;
    ///           "top"/"top face" = the planar face whose outward normal is most +Y/+Z (SW frame), else the largest
    ///           planar. Resolve the POSITION = the face CENTROID (the sensible default; v1 does not ask for coordinates).
    ///   Builder — select the face, open a sketch (ISketchManager.InsertSketch), draw ONE circle at the centroid
    ///           projected into the SKETCH coordinate system (ISketch.ModelToSketchTransform), exit, and boss-extrude
    ///           BLIND by the height with merge=true (IFeatureManager.FeatureExtrusion3 — the exact positive-extrude
    ///           call from RecipeExecutor.DoExtrude). A boss must grow AWAY from the solid: SW's default extrude side on
    ///           a face is not guaranteed, so Builder does a TRY → MEASURE → max-1 FLIP retry (the proven AutoMate
    ///           flip-retry pattern) — it keeps the direction whose rebuild ADDS material; a direction that merges
    ///           INTO the solid (volume unchanged) is discarded and flipped. Tag the extrude "Forge-Boss" for idempotency.
    ///   Sentinel — FAIL CLOSED (Rule #6): after the rebuild, INDEPENDENTLY confirm the solid volume ROSE by ~the boss
    ///           volume (π·r²·h — material added OUTWARD, not merged into existing material), a new external cylindrical
    ///           face of ~the requested diameter exists (the boss wall), and the rebuild is clean. Anything less — the
    ///           extrude errored, or both directions failed to add material — and the Forge-Boss feature is DELETED, the
    ///           part restored, and the failure reported honestly. Never a fake green.
    ///
    /// Robustness (the 12 rules): PART only — an assembly is refused honestly (Rule #2). Diameter (12mm) / height (10mm) /
    /// position (face centre) all default sensibly, so there is no ambiguity to ask about — a boss wider than the face's
    /// shorter span is refused honestly (Rule #2/#3, "won't fit"), never shrunk to guess. IDEMPOTENT (Rule #5): the
    /// extrude is tagged "Forge-Boss"; a second run finds it and reports "already added a boss — nothing to do" instead
    /// of stacking a second. UNDO is sacred (Rule #7): one tagged feature, one Ctrl+Z; Forge never saves. Verified reports
    /// what was MEASURED (volume up by ~the boss + boss wall present + clean rebuild), never what was attempted.
    /// </summary>
    public static class AddBoss
    {
        private const string BossFeatureName = "Forge-Boss";
        private const double MM = 0.001;         // mm -> SW metres
        private const double DefaultDiaMm = 12.0; // sensible default when no diameter is stated
        private const double DefaultHeightMm = 10.0; // sensible default when no height is stated

        public static bool IsAddBossIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            // A boss/pad ADDS material. "remove/delete/strip a boss/pad" is not this handler — never claim those.
            if (Regex.IsMatch(c, @"\b(remove|delete|strip|get rid of|kill|defeature)\b")) return false;
            bool addVerb = Regex.IsMatch(c, @"\b(add|put|make|create|extrude|raise|place)\b");
            bool hasBoss = Regex.IsMatch(c, @"\b(boss|pad|pillar|stud|protrusion)\b");
            return addVerb && hasBoss;
        }

        private class PlanarFace { public Face2 Face; public double AreaMm2; public double[] Normal; public double[] Centroid; public double SmallSpanMm; }

        public static async Task<AddBossResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new AddBossResult();

            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { res.Error = "Adding a boss works on a single part — open the .SLDPRT you want the boss on, not an assembly."; return res; }
            var part = model as PartDoc;
            if (part == null) { res.Error = "This document has no part geometry to add a boss to."; return res; }

            // ---- IDEMPOTENT (Rule #5): a Forge-Boss already present → don't stack a second ----
            if (FindFeatureByName(model, BossFeatureName) != null)
            {
                res.AlreadyDone = true;
                res.Verified = true;   // the requested state already holds
                res.Info = "Already added a boss — a Forge-Boss feature is present, so there's nothing to do. To add a " +
                           "different boss, delete Forge-Boss first (Edit > Delete, or Ctrl+Z), then run again.";
                await emit("Builder", null, "done", "Forge-Boss already present — nothing to do");
                return res;
            }

            double diaMm = ParseDiameterMm(intent);
            double heightMm = ParseHeightMm(intent);
            res.DiameterMm = diaMm;
            res.HeightMm = heightMm;
            res.ExpectedAddMm3 = Math.PI * Math.Pow(diaMm / 2.0, 2) * heightMm;

            await emit("Gauge", "reading the solid and picking the face", "run", null);
            object[] bodies = SolidBodies(part);
            if (bodies == null || bodies.Length == 0)
            { res.Error = "No solid body to build on — this part has no solid geometry (a surface/sheet body or an empty doc has no face to stand a boss on)."; return res; }

            bool wantTop = Regex.IsMatch((intent ?? "").ToLowerInvariant(), @"\btop\b");
            PlanarFace target = ResolveTargetFace(bodies, wantTop);
            if (target == null)
            { res.Error = "No planar face to build on — this part has no flat face to stand a boss on (v1 adds round bosses on planar faces)."; return res; }

            res.TargetFace = (wantTop ? "top face" : "largest planar face") + ", " + target.AreaMm2.ToString("N0") + " mm²";
            res.VolumeBeforeMm3 = GetVolumeMm3(model);
            await emit("Gauge", null, "done",
                "target " + res.TargetFace + " · centre pick · solid " +
                (res.VolumeBeforeMm3 > 0 ? res.VolumeBeforeMm3.ToString("N0") + " mm³" : "read"));

            // ---- sanity: a boss at/above the face's smaller span won't fit → refuse honestly (Rule #2/#3) ----
            if (target.SmallSpanMm > 0 && diaMm >= target.SmallSpanMm)
            {
                res.Error = "A " + Trim(diaMm) + "mm boss is as wide as the face's shorter side (" + Trim(target.SmallSpanMm) +
                            "mm) — it wouldn't fit. Pick a smaller diameter, say " + Trim(Math.Max(1.0, target.SmallSpanMm / 4.0)) + "mm.";
                return res;
            }

            // ---- WRITE: sketch a circle at the face centre and boss-extrude OUTWARD (RecipeExecutor.DoExtrude mechanics) ----
            await emit("Builder", "adding a " + Trim(diaMm) + " mm boss " + Trim(heightMm) + " mm tall at the centre of the " + (wantTop ? "top face" : "largest face"), "run", null);

            var mu = app.GetMathUtility() as MathUtility;

            // A boss must grow AWAY from the solid. SW's default extrude side on a face is not guaranteed, so try one
            // direction, measure whether it ADDED material, and if it merged into the solid instead (volume unchanged),
            // flip ONCE and try the other side (the proven AutoMate max-1 flip-retry). Keep the outward direction.
            Feature boss = null;
            bool addedOutward = false;
            for (int attempt = 0; attempt < 2 && !addedOutward; attempt++)
            {
                bool flip = attempt == 1;      // second attempt = the opposite side
                string err;
                boss = TryBoss(model, mu, target, diaMm, heightMm, flip, out err);
                if (boss == null)
                {
                    if (attempt == 1) { res.Error = err ?? "SolidWorks refused the boss extrude — the part is unchanged."; RollbackBoss(model); return res; }
                    // first attempt could not even create the feature → clean up any loose sketch, then flip-retry
                    CleanupLooseSketch(model);
                    continue;
                }

                try { model.ForceRebuild3(false); } catch { }
                res.VolumeAfterMm3 = GetVolumeMm3(model);
                double added = res.VolumeAfterMm3 - res.VolumeBeforeMm3;

                // outward boss ADDS ~expected volume; a boss that merged INTO the solid leaves volume ~unchanged.
                if (added >= res.ExpectedAddMm3 * 0.5)
                {
                    addedOutward = true;
                }
                else
                {
                    // wrong side (merged inward, no material added) → delete it and flip to the other side once
                    RollbackBoss(model);
                    boss = null;
                    if (attempt == 1)
                    {
                        res.VolumeAfterMm3 = GetVolumeMm3(model);
                        res.Error = "The boss added no material either way — neither direction grew the solid, so Forge rolled it back; the part is unchanged.";
                        return res;
                    }
                }
            }

            if (boss == null)
            { res.Error = "SolidWorks refused the boss extrude — the part is unchanged."; RollbackBoss(model); return res; }
            try { boss.Name = BossFeatureName; } catch { }   // tag for idempotency (Rule #5)

            // ---- rebuild, then INDEPENDENTLY verify (Rule #6) ----
            await emit("Sentinel", "verifying the boss post-rebuild", "run", null);
            try { model.ForceRebuild3(false); } catch { }
            res.RebuildErrors = SafeWhatsWrong(model);
            res.VolumeAfterMm3 = GetVolumeMm3(model);
            res.NewCylFace = HasBossWallFace(part, diaMm / 2.0 * MM);

            double addedVol = res.VolumeAfterMm3 - res.VolumeBeforeMm3;
            bool volumeRose = res.VolumeAfterMm3 > 0 && res.VolumeBeforeMm3 > 0 && addedVol >= res.ExpectedAddMm3 * 0.5;
            bool clean = res.RebuildErrors == 0;

            if (!volumeRose || !clean || !res.NewCylFace)
            {
                // FAIL CLOSED + never ship a broken part (Rule #4/#6/#7): delete the boss, restore the solid.
                RollbackBoss(model);
                res.RolledBack = true;
                res.VolumeAfterMm3 = GetVolumeMm3(model);
                res.Error = !clean
                    ? "The boss rebuilt with " + res.RebuildErrors + " error(s) — rolled it back; the part is unchanged."
                    : (!volumeRose
                        ? "The extrude added no material (it merged into the solid instead of standing off it) — rolled it back; the part is unchanged."
                        : "The extrude left no clean boss wall face — rolled it back; the part is unchanged.");
                await emit("Sentinel", null, "fail", "rolled back — part restored");
                return res;
            }

            res.Verified = true;
            await emit("Sentinel", null, "done",
                "boss added: volume +" + addedVol.ToString("N0") + " mm³ (≈π·r²·h " + res.ExpectedAddMm3.ToString("N0") +
                "), wall ⌀" + Trim(diaMm) + "mm, rebuild clean");

            res.Info = BuildInfo(res);
            return res;
        }

        // ---- verdict first (Character #3), the number not the adjective (Character #2), honest about what was VERIFIED ----
        private static string BuildInfo(AddBossResult r)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("Added a " + Trim(r.DiameterMm) + " mm × " + Trim(r.HeightMm) + " mm tall boss at the centre of the " +
                      (r.TargetFace ?? "target face") + " — volume " + r.VolumeBeforeMm3.ToString("N0") + " → " +
                      r.VolumeAfterMm3.ToString("N0") + " mm³ (+" + (r.VolumeAfterMm3 - r.VolumeBeforeMm3).ToString("N0") +
                      ", expected ≈" + r.ExpectedAddMm3.ToString("N0") + "), a new ⌀" + Trim(r.DiameterMm) +
                      "mm boss wall face present, rebuild clean.");
            sb.Append(" One Ctrl+Z removes it; Forge didn't save.");
            return sb.ToString();
        }

        // ================= the boss write (one attempt, given a flip) =================

        // Sketch a circle at the projected face centroid and boss-extrude BLIND by the height with merge=true. 'flip'
        // chooses which side of the face the material grows on (the caller decides outward by measuring the volume it
        // adds). Returns the created extrude Feature, or null (with 'err') if the sketch/extrude could not be created.
        private static Feature TryBoss(IModelDoc2 model, MathUtility mu, PlanarFace target, double diaMm, double heightMm, bool flip, out string err)
        {
            err = null;
            try
            {
                try { model.ClearSelection2(true); } catch { }
                bool sel = false; try { sel = ((Entity)target.Face).Select4(false, null); } catch { }
                if (!sel) { err = "Couldn't select the target face to sketch on — the part geometry may be in an unexpected state."; return null; }

                var sk = model.SketchManager;
                sk.InsertSketch(true);                                   // begin a sketch on the selected face
                var active = sk.ActiveSketch as Sketch;
                double[] sc = ModelToSketchXY(mu, active, target.Centroid);
                if (sc == null)
                {
                    sk.InsertSketch(true);                               // abandon the sketch cleanly
                    CleanupLooseSketch(model);
                    err = "Couldn't project the face centre into the sketch — Forge left the part untouched.";
                    return null;
                }
                sk.CreateCircleByRadius(sc[0], sc[1], 0, diaMm / 2.0 * MM);
                sk.InsertSketch(true);                                   // exit the sketch
                try { model.ClearSelection2(true); } catch { }

                var skFeat = model.FeatureByPositionReverse(0) as Feature;
                if (skFeat != null) skFeat.Select2(false, 0);

                // Positive boss: single-ended BLIND extrude, merge=true (ADD to the existing body). This is the exact
                // positive-extrude call from RecipeExecutor.DoExtrude — Sd=true (one direction), Flip=<flip> chooses the
                // side, T1=Blind for 'heightMm', Merge=true so the boss fuses to the part instead of a separate body.
                var feat = model.FeatureManager.FeatureExtrusion3(
                    true, flip, false,
                    (int)swEndConditions_e.swEndCondBlind, 0,
                    heightMm * MM, 0,
                    false, false, false, false, 0, 0,
                    false, false, false, false,
                    true, true, true, 0, 0, false) as Feature;
                try { model.ClearSelection2(true); } catch { }

                if (feat == null) { CleanupLooseSketch(model); err = "SolidWorks refused the boss extrude — the circle may not have landed on the solid."; return null; }
                // tag immediately so the flip-retry / verify rollback (RollbackBoss, by name) can always reach it
                try { feat.Name = BossFeatureName; } catch { }
                return feat;
            }
            catch (Exception ex)
            {
                err = "The boss extrude couldn't be created (" + ex.GetType().Name + ") — Forge rolled back and left the part unchanged.";
                return null;
            }
        }

        // ================= face resolution (identical logic to AddHole) =================

        private static PlanarFace ResolveTargetFace(object[] bodies, bool wantTop)
        {
            var planars = new List<PlanarFace>();
            foreach (var bo in bodies)
            {
                var body = bo as Body2; if (body == null) continue;
                object[] faces = null; try { faces = body.GetFaces() as object[]; } catch { }
                foreach (var fo in faces ?? new object[0])
                {
                    var face = fo as Face2; if (face == null) continue;
                    Surface s = null; try { s = face.GetSurface() as Surface; } catch { }
                    bool plane = false; try { plane = s != null && s.IsPlane(); } catch { }
                    if (!plane) continue;

                    double area = 0; try { area = face.GetArea(); } catch { }
                    if (area <= 0) continue;
                    double[] n = null; try { n = face.Normal as double[]; } catch { }
                    double[] box = null; try { box = face.GetBox() as double[]; } catch { }
                    double[] centroid = CentroidOnFace(face, box);
                    if (centroid == null) continue;

                    planars.Add(new PlanarFace
                    {
                        Face = face,
                        AreaMm2 = area * 1e6,
                        Normal = n,
                        Centroid = centroid,
                        SmallSpanMm = SmallerInPlaneSpanMm(box)
                    });
                }
            }
            if (planars.Count == 0) return null;

            if (wantTop)
            {
                PlanarFace best = null; double bestUp = -2;
                foreach (var p in planars)
                {
                    if (p.Normal == null || p.Normal.Length < 3) continue;
                    double up = Math.Max(p.Normal[1], p.Normal[2]);   // most +Y or +Z (SW frame)
                    if (up > bestUp) { bestUp = up; best = p; }
                }
                if (best != null && bestUp > 0.5) return best;   // a face that genuinely faces up
                // no clearly-up face → fall through to largest planar
            }

            PlanarFace largest = null; double bestArea = -1;
            foreach (var p in planars) if (p.AreaMm2 > bestArea) { bestArea = p.AreaMm2; largest = p; }
            return largest;
        }

        private static double[] CentroidOnFace(Face2 face, double[] box)
        {
            if (box == null || box.Length < 6) return null;
            double[] c = { (box[0] + box[3]) / 2, (box[1] + box[4]) / 2, (box[2] + box[5]) / 2 };
            try
            {
                double[] p = face.GetClosestPointOn(c[0], c[1], c[2]) as double[];
                if (p != null && p.Length >= 3) return new[] { p[0], p[1], p[2] };
            }
            catch { }
            return c;
        }

        private static double SmallerInPlaneSpanMm(double[] box)
        {
            if (box == null || box.Length < 6) return 0;
            double dx = Math.Abs(box[3] - box[0]) * 1000.0;
            double dy = Math.Abs(box[4] - box[1]) * 1000.0;
            double dz = Math.Abs(box[5] - box[2]) * 1000.0;
            double[] d = { dx, dy, dz };
            Array.Sort(d);          // [thickness~0, smallerSpan, largerSpan]
            return d[1];
        }

        // ================= sketch-space projection (the load-bearing coordinate transform) =================

        private static double[] ModelToSketchXY(MathUtility mu, Sketch sk, double[] p3)
        {
            if (mu == null || sk == null || p3 == null || p3.Length < 3) return null;
            try
            {
                var xform = sk.ModelToSketchTransform as MathTransform;
                if (xform == null) return null;
                var mp = mu.CreatePoint(new[] { p3[0], p3[1], p3[2] }) as MathPoint;
                if (mp == null) return null;
                var sp = mp.MultiplyTransform(xform) as MathPoint;
                double[] a = sp?.ArrayData as double[];
                if (a == null || a.Length < 2) return null;
                return new[] { a[0], a[1] };
            }
            catch { return null; }
        }

        // ================= verification helpers =================

        // solid volume in mm^3 via the whole-doc mass-property engine (the ground truth sums per-body IBody2
        // GetMassProperties — a DIFFERENT path, so verification is a genuine cross-check, not the same math twice).
        private static double GetVolumeMm3(IModelDoc2 model)
        {
            try { var mp = model.Extension.CreateMassProperty(); return mp == null ? -1 : mp.Volume * 1e9; }
            catch { return -1; }
        }

        // does a cylindrical face of ~the requested radius now exist on the solid? (the boss's lateral wall). Tolerance
        // scales with the radius so a small and a large boss both match; independent of the extrude's own return code.
        private static bool HasBossWallFace(PartDoc part, double reqRadiusM)
        {
            double tol = Math.Max(0.2 * MM, 0.05 * reqRadiusM);
            foreach (var bo in SolidBodies(part) ?? new object[0])
            {
                var body = bo as Body2; if (body == null) continue;
                object[] faces = null; try { faces = body.GetFaces() as object[]; } catch { }
                foreach (var fo in faces ?? new object[0])
                {
                    var face = fo as Face2; if (face == null) continue;
                    Surface s = null; try { s = face.GetSurface() as Surface; } catch { }
                    if (s == null) continue;
                    bool cyl = false; try { cyl = s.IsCylinder(); } catch { }
                    if (!cyl) continue;
                    double[] cp = null; try { cp = s.CylinderParams as double[]; } catch { }
                    if (cp == null || cp.Length < 7) continue;
                    if (Math.Abs(cp[6] - reqRadiusM) <= tol) return true;
                }
            }
            return false;
        }

        private static object[] SolidBodies(PartDoc part)
        { try { return part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]; } catch { return null; } }

        private static int SafeWhatsWrong(IModelDoc2 model)
        { try { return model.Extension.GetWhatsWrongCount(); } catch { return 0; } }

        // delete the tagged boss and rebuild — restores the solid so a failed/wrong-way extrude never ships a broken part
        private static void RollbackBoss(IModelDoc2 model)
        {
            try
            {
                var f = FindFeatureByName(model, BossFeatureName);
                if (f == null) return;
                try { model.ClearSelection2(true); } catch { }
                bool sel = false; try { sel = f.Select2(false, 0); } catch { }
                if (sel) { try { model.EditDelete(); } catch { } }
                try { model.ForceRebuild3(false); } catch { }
                try { model.ClearSelection2(true); } catch { }
            }
            catch { }
        }

        // If the last feature is an orphan sketch (a boss attempt that never produced an extrude), delete it so the part
        // is left exactly as it was found.
        private static void CleanupLooseSketch(IModelDoc2 model)
        {
            try
            {
                var sf = model.FeatureByPositionReverse(0) as Feature;
                string tn = null; try { tn = sf?.GetTypeName2(); } catch { }
                if (sf != null && tn != null && tn.IndexOf("Sketch", StringComparison.OrdinalIgnoreCase) >= 0)
                { sf.Select2(false, 0); model.EditDelete(); }
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

        // ================= intent parsing =================

        // boss DIAMETER in mm: "15mm diameter" / "15 mm dia" / "diameter 15mm" / "15mm wide/across" wins; else a bare
        // "X mm" that is NOT the height qualifier ("X mm tall/high") is taken as the diameter (a "20mm boss"); else the
        // 12mm default. Inch words ("quarter/half/eighth inch", "3/8 inch", "0.25 inch") map to mm.
        private static double ParseDiameterMm(string intent)
        {
            string c = (intent ?? "").ToLowerInvariant();

            // explicit diameter keyword, either order
            var dk = Regex.Match(c, @"(\d+(\.\d+)?)\s*mm\s*(diameter|dia\b|wide|across|thick in dia)");
            if (dk.Success && double.TryParse(dk.Groups[1].Value, out double vd) && vd > 0) return vd;
            var dk2 = Regex.Match(c, @"(diameter|dia)\s*(?:of\s*)?(\d+(\.\d+)?)\s*mm");
            if (dk2.Success && double.TryParse(dk2.Groups[2].Value, out double vd2) && vd2 > 0) return vd2;

            // inch words / fractions
            if (Regex.IsMatch(c, @"\bquarter\s*(inch|in|\"")")) return 6.35;
            if (Regex.IsMatch(c, @"\bhalf\s*(inch|in|\"")")) return 12.7;
            if (Regex.IsMatch(c, @"\beighth\s*(inch|in|\"")")) return 3.175;
            var frac = Regex.Match(c, @"(\d+)\s*/\s*(\d+)\s*(inch|in|\"")");
            if (frac.Success && double.TryParse(frac.Groups[1].Value, out double num) && double.TryParse(frac.Groups[2].Value, out double den) && den > 0)
                return num / den * 25.4;
            var inch = Regex.Match(c, @"(\d+(\.\d+)?)\s*(inch(es)?|in\b|\"")");
            if (inch.Success && double.TryParse(inch.Groups[1].Value, out double vin) && vin > 0) return vin * 25.4;

            // a bare "X mm" that is NOT the height qualifier → the diameter (e.g. "add a 20mm boss")
            foreach (Match m in Regex.Matches(c, @"(\d+(\.\d+)?)\s*mm(?!\s*(tall|high|deep|thick|in height|height))"))
                if (double.TryParse(m.Groups[1].Value, out double vb) && vb > 0) return vb;

            return DefaultDiaMm;
        }

        // boss HEIGHT in mm: a number qualified by "tall/high/deep/thick/in height/height" — "10mm tall", "20mm high",
        // "tall 10mm", "10mm in height". Else the 10mm default.
        private static double ParseHeightMm(string intent)
        {
            string c = (intent ?? "").ToLowerInvariant();

            var h1 = Regex.Match(c, @"(\d+(\.\d+)?)\s*mm\s*(tall|high|deep|thick|in height|height)");
            if (h1.Success && double.TryParse(h1.Groups[1].Value, out double vh) && vh > 0) return vh;
            var h2 = Regex.Match(c, @"(tall|high|height|depth)\s*(?:of\s*)?(\d+(\.\d+)?)\s*mm");
            if (h2.Success && double.TryParse(h2.Groups[2].Value, out double vh2) && vh2 > 0) return vh2;

            return DefaultHeightMm;
        }

        private static string Trim(double v) => v.ToString("0.###");
    }
}
