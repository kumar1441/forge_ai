using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class AddHoleResult
    {
        public double DiameterMm;            // hole diameter parsed from the intent (mm); default 8 when unspecified
        public string TargetFace;            // which face was drilled, e.g. "largest planar face, 2400 mm²"
        public double VolumeBeforeMm3 = -1;  // solid volume before the cut (mm^3), independently measured
        public double VolumeAfterMm3 = -1;   // solid volume after the cut + rebuild (mm^3)
        public bool NewCylFace;              // a new internal cylindrical face of ~the requested diameter now exists
        public int RebuildErrors;            // GetWhatsWrongCount() post-rebuild (0 => clean)
        public bool RolledBack;              // the cut was created but failed to verify → deleted, part restored
        public bool Verified;                // fail closed: true ONLY when volume DROPPED, a bore face appeared, rebuild clean
        public bool AlreadyDone;             // idempotent: a Forge-Hole already exists → nothing to do
        public string Info;                  // verdict-first panel line
        public string Error;                 // honest failure text (assembly, no solid, won't-fit, missed the solid)
    }

    /// <summary>
    /// AddHole (tool #206 "add a hole to a part") — a REAL geometry WRITE on a single PART. It drills ONE round
    /// through-hole (a cut-extrude of a circle) at the centre of a resolved face: "add a hole", "add a 10mm hole",
    /// "drill a 6mm hole in the top", "put a mounting hole in the center".
    ///
    /// Approach (deliberate, documented — the sketch/circle/cut mechanics are REUSED verbatim from RecipeExecutor,
    /// the proven parametric-generation engine, so this is low-risk):
    ///   Gauge — read the solid; resolve the TARGET FACE deterministically. Default = the LARGEST planar face by area
    ///           (a mounting-plate face); "top"/"top face" in the text = the planar face whose outward normal is most
    ///           +Y/+Z (SW frame), else the largest planar. Resolve the POSITION = the face CENTROID (centre of the
    ///           face — the sensible default for a mounting hole; v1 does not ask for coordinates, Character #6).
    ///   Driller — select the face, open a sketch on it (ISketchManager.InsertSketch), draw ONE circle at the centroid
    ///           projected into the SKETCH coordinate system (ISketch.ModelToSketchTransform), exit the sketch, and
    ///           cut-extrude THROUGH ALL both directions (IFeatureManager.FeatureCut4) — the exact call pattern from
    ///           RecipeExecutor.DoHole / DoCut. Tag the cut "Forge-Hole" for idempotency. ONE ForceRebuild3.
    ///   Sentinel — FAIL CLOSED (Rule #6): after the rebuild, INDEPENDENTLY confirm the solid volume DROPPED (material
    ///           removed), a new internal cylindrical face of ~the requested diameter exists (the bore), and the rebuild
    ///           is clean. Anything less — the cut errored the rebuild or missed the solid (no material removed) — and the
    ///           Forge-Hole feature is DELETED, the part restored, and the failure reported honestly. Never a fake green.
    ///
    /// Robustness (the 12 rules): PART only — an assembly is refused honestly (Rule #2). Diameter/position have sensible
    /// defaults (8mm, face centre), so there is no ambiguity to ask about — a hole bigger than the face's smaller span is
    /// refused honestly (Rule #2/#3, "won't fit"), never guessed smaller. IDEMPOTENT (Rule #5): the cut is tagged
    /// "Forge-Hole"; a second run finds it and reports "already added a hole — nothing to do" instead of stacking a
    /// second (a real "add another hole" is a v2 refinement). UNDO is sacred (Rule #7): one tagged feature, one Ctrl+Z;
    /// Forge never saves. Verified reports what was MEASURED (volume down + bore present + clean rebuild), never what was
    /// attempted.
    /// </summary>
    public static class AddHole
    {
        private const string HoleFeatureName = "Forge-Hole";
        private const double MM = 0.001;        // mm -> SW metres
        private const double DefaultDiaMm = 8.0; // sensible default when no size is stated

        public static bool IsAddHoleIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            // "remove/delete/strip/fill the holes" is GEOMETRY DEFEATURE, not add — never claim those (defeature is
            // also matched earlier in Dispatch). AddHole owns the ADD verbs paired with "hole".
            if (Regex.IsMatch(c, @"\b(remove|delete|strip|get rid of|kill|fill|defeature)\b")) return false;
            // "make the hole bigger/smaller by 2mm" is a RESIZE of an EXISTING hole (set_dimension's relative-delta
            // territory), not an ADD — "make" + "hole" alone reads as an add-hole intent (test-loop no-change finding
            // hole-enlarge-and-fillet: this exact phrase misrouted to AddHole, drilling a bogus 2mm-diameter hole
            // instead of growing the existing one by 2mm). Exclude any grow/shrink-by-delta wording up front.
            if (Regex.IsMatch(c, @"\b(bigger|larger|wider|taller|longer|deeper|thicker|smaller|narrower|shorter|thinner|increase[sd]?|enlarge[sd]?|grow[n]?|expand(ed)?|decrease[sd]?|shrink|reduce[d]?)\b[\s\w]{0,20}?\bby\s+\d")) return false;
            bool addVerb = Regex.IsMatch(c, @"\b(add|drill|put|bore|cut|make|create|place)\b");
            bool hasHole = Regex.IsMatch(c, @"\bholes?\b");
            return addVerb && hasHole;
        }

        private class PlanarFace { public Face2 Face; public double AreaMm2; public double[] Normal; public double[] Centroid; public double SmallSpanMm; }

        public static async Task<AddHoleResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new AddHoleResult();

            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { res.Error = "Adding a hole works on a single part — open the .SLDPRT you want drilled, not an assembly."; return res; }
            var part = model as PartDoc;
            if (part == null) { res.Error = "This document has no part geometry to drill."; return res; }

            // ---- IDEMPOTENT (Rule #5): a Forge-Hole already present → don't stack a second ----
            if (FindFeatureByName(model, HoleFeatureName) != null)
            {
                res.AlreadyDone = true;
                res.Verified = true;   // the requested state already holds
                res.Info = "Already added a hole — a Forge-Hole feature is present, so there's nothing to do. To drill a " +
                           "different hole, delete Forge-Hole first (Edit > Delete, or Ctrl+Z), then run again.";
                await emit("Driller", null, "done", "Forge-Hole already present — nothing to do");
                return res;
            }

            double diaMm = ParseDiameterMm(intent, out bool diaExplicit);
            res.DiameterMm = diaMm;

            await emit("Gauge", "reading the solid and picking the face", "run", null);
            object[] bodies = SolidBodies(part);
            if (bodies == null || bodies.Length == 0)
            { res.Error = "No solid body to drill — this part has no solid geometry (a surface/sheet body or an empty doc has no face to put a hole through)."; return res; }

            string ic = (intent ?? "").ToLowerInvariant();
            bool wantTop = Regex.IsMatch(ic, @"\btop\b");
            bool wantBottom = !wantTop && Regex.IsMatch(ic, @"\bbottom\b|\bunderside\b|\bunderneath\b");
            List<PlanarFace> candidates = ResolveCandidateFaces(bodies, wantTop, wantBottom);
            if (candidates.Count == 0)
            { res.Error = "No planar face to drill — this part has no flat face to place a hole on (v1 drills through-holes on planar faces)."; return res; }

            string faceWord = wantTop ? "top face" : (wantBottom ? "bottom face" : "largest planar face");
            res.VolumeBeforeMm3 = GetVolumeMm3(model);
            await emit("Gauge", null, "done",
                faceWord + " candidates: " + candidates.Count + " · solid " +
                (res.VolumeBeforeMm3 > 0 ? res.VolumeBeforeMm3.ToString("N0") + " mm³" : "read"));

            var mu = app.GetMathUtility() as MathUtility;
            int tried = 0;
            string lastFit = null;
            double[] diaCascade = DiameterCascadeMm(diaMm, diaExplicit);

            // Outer loop: when no size was stated, fall back through smaller standard screw diameters if the
            // requested/default one doesn't fit anywhere (test-loop wrong-answer add-screw-hole — see
            // DiameterCascadeMm). Inner loop: try candidate faces largest-first; a face that's too small or whose
            // circle doesn't land on the solid is skipped (not a final refusal) — the NEXT candidate gets a real
            // attempt. This is what makes a curved/multi-body part (e.g. a leaf spring, where the biggest area is
            // a swept, non-planar face and only small end/step faces are planar) still drillable instead of
            // failing on the first bad pick.
            foreach (var tryDia in diaCascade)
            {
                foreach (var target in candidates)
                {
                    if (target.SmallSpanMm > 0 && tryDia >= target.SmallSpanMm)
                    {
                        lastFit = "A " + Trim(tryDia) + "mm hole is as wide as the face's shorter side (" + Trim(target.SmallSpanMm) + "mm)";
                        continue;   // won't fit this face — try the next candidate
                    }
                    tried++;

                    await emit("Driller", "adding a " + Trim(tryDia) + " mm through-hole (candidate face " + tried + ", " + target.AreaMm2.ToString("N0") + " mm²)", "run", null);

                    Feature cut = null;
                    try
                    {
                        try { model.ClearSelection2(true); } catch { }
                        bool sel = false; try { sel = ((Entity)target.Face).Select4(false, null); } catch { }
                        if (!sel) continue;   // couldn't select this face — try the next
                        Body2 ownerBody = null; try { ownerBody = target.Face.GetBody() as Body2; } catch { }

                        var sk = model.SketchManager;
                        sk.InsertSketch(true);                                   // begin a sketch on the selected face
                        var active = sk.ActiveSketch as Sketch;
                        double[] sc = ModelToSketchXY(mu, active, target.Centroid);
                        if (sc == null)
                        {
                            sk.InsertSketch(true);
                            try { model.ClearSelection2(true); } catch { }
                            var loose = model.FeatureByPositionReverse(0) as Feature;
                            if (loose != null) { try { loose.Select2(false, 0); model.EditDelete(); } catch { } }
                            continue;   // couldn't project the centre — try the next candidate
                        }
                        sk.CreateCircleByRadius(sc[0], sc[1], 0, tryDia / 2.0 * MM);
                        sk.InsertSketch(true);                                   // exit the sketch
                        try { model.ClearSelection2(true); } catch { }

                        var skFeat = model.FeatureByPositionReverse(0) as Feature;
                        if (skFeat != null) skFeat.Select2(false, 0);

                        // On a MULTI-BODY part, also explicitly select the face's OWNING body for feature scope
                        // (append to the sketch selection) — auto-select-only feature scope has been unreliable for
                        // other in-model multi-body writes on this build (Scale/Mirror/Combine/MoveCopyBody all proven
                        // dead on multi-body parts, see docs/kb/landmines.md); an explicit scope selection is the
                        // cheap thing to try before concluding the cut itself is dead here too.
                        try { if (ownerBody != null) ((Entity)ownerBody).Select4(true, null); } catch { }

                        // Cut THROUGH ALL both directions (Sd=false, T1=T2=ThroughAll) so it works regardless of which side of
                        // the face's sketch plane the solid sits on — the exact form of RecipeExecutor.DoCut's through-all path.
                        int through = (int)swEndConditions_e.swEndCondThroughAll;
                        cut = model.FeatureManager.FeatureCut4(
                            false, false, false, through, through, 0, 0, false, false, false, false, 0, 0,
                            false, false, false, false, false, true, true, true, true, false, 0, 0, false, false) as Feature;
                        try { model.ClearSelection2(true); } catch { }
                    }
                    catch { cut = null; }

                    if (cut == null)
                    {
                        // this face's circle didn't land on the solid — clean up the loose sketch and try the next candidate
                        try
                        {
                            var sf = model.FeatureByPositionReverse(0) as Feature;
                            string tn = null; try { tn = sf?.GetTypeName2(); } catch { }
                            if (sf != null && tn != null && tn.IndexOf("Sketch", StringComparison.OrdinalIgnoreCase) >= 0)
                            { sf.Select2(false, 0); model.EditDelete(); }
                        }
                        catch { }
                        try { model.ClearSelection2(true); } catch { }
                        continue;
                    }
                    try { cut.Name = HoleFeatureName; } catch { }   // tag for idempotency (Rule #5)

                    // ---- rebuild, then INDEPENDENTLY verify (Rule #6) ----
                    await emit("Sentinel", "verifying the bore post-rebuild", "run", null);
                    try { model.ForceRebuild3(false); } catch { }
                    res.RebuildErrors = SafeWhatsWrong(model);
                    res.VolumeAfterMm3 = GetVolumeMm3(model);
                    res.NewCylFace = HasNewBoreFace(part, tryDia / 2.0 * MM);

                    bool volumeDropped = res.VolumeAfterMm3 > 0 && res.VolumeBeforeMm3 > 0 && res.VolumeBeforeMm3 - res.VolumeAfterMm3 > 1e-4;
                    bool clean = res.RebuildErrors == 0;

                    if (!volumeDropped || !clean || !res.NewCylFace)
                    {
                        // this candidate rebuilt badly or missed the solid — roll back and try the next face
                        RollbackHole(model);
                        res.VolumeAfterMm3 = GetVolumeMm3(model);
                        continue;
                    }

                    res.DiameterMm = tryDia;
                    res.TargetFace = (wantTop ? "top face" : (wantBottom ? "bottom face" : "planar face")) + " (candidate " + tried + " of " + candidates.Count + "), " + target.AreaMm2.ToString("N0") + " mm²";
                    res.Verified = true;
                    double dVol = res.VolumeBeforeMm3 - res.VolumeAfterMm3;
                    await emit("Sentinel", null, "done",
                        "drilled: volume −" + dVol.ToString("N0") + " mm³, bore ⌀" + Trim(tryDia) + "mm, rebuild clean");

                    res.Info = BuildInfo(res, tryDia != diaMm);
                    return res;
                }
            }

            // every candidate face x diameter was tried (or too small) and none produced a verified through-hole
            res.Error = tried > 0
                ? "Tried " + tried + " planar face/diameter combination(s) — none produced a clean through-hole (the circle kept missing the solid or the rebuild broke). The part is unchanged."
                : (lastFit ?? "No planar face was wide enough for a " + Trim(diaMm) + "mm hole.") + (diaExplicit ? " — try a smaller diameter." : ", not even at " + Trim(diaCascade[diaCascade.Length - 1]) + "mm — this part has no face wide enough for a screw hole.");
            return res;
        }

        // ---- verdict first (Character #3), the number not the adjective (Character #2), honest about what was VERIFIED ----
        private static string BuildInfo(AddHoleResult r, bool downsized)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("Added a " + Trim(r.DiameterMm) + " mm through-hole at the centre of the " + (r.TargetFace ?? "target face") +
                      " — volume " + r.VolumeBeforeMm3.ToString("N0") + " → " + r.VolumeAfterMm3.ToString("N0") +
                      " mm³, a new ⌀" + Trim(r.DiameterMm) + "mm bore face present, rebuild clean.");
            if (downsized)
                sb.Append(" No size was stated and the default 8mm didn't fit this face, so I used a smaller " +
                          "standard screw size (" + Trim(r.DiameterMm) + "mm) instead — say a size to override.");
            sb.Append(" One Ctrl+Z removes it; Forge didn't save.");
            return sb.ToString();
        }

        // ================= face resolution =================

        // Resolve RANKED candidate faces: "top"/"bottom" → the face whose outward normal points most along the
        // part's OWN VERTICAL axis (found from ITS bounding box, not assumed) — else area descending only. Ranked
        // (not single-best) so the caller can fall through to the next candidate when the biggest pick's circle
        // doesn't actually land on the solid (e.g. a curved/multi-body part where the widest surfaces are swept/
        // non-planar and only small end faces are flat).
        //
        // Earlier version scored each face by Math.Max/Min(Normal[1], Normal[2]) — "whichever of Y or Z reads more
        // positive/negative" — which SILENTLY CONFLATES a genuine top/bottom cap (normal ≈ ±Z) with a SIDE wall
        // whose normal happens to be ≈ ±Y (a box has faces on all 3 axes, so a side wall's Normal[1]=±1 scores
        // identically to a cap's Normal[2]=±1). PROVEN WRONG live on a 20x20x80mm post (tool add_hole, test-loop
        // no-change finding add-drain-hole): "add a hole in the bottom" resolved to a 1600mm² SIDE wall, not the
        // 400mm² bottom cap. FIX: find the part's actual vertical axis first — whichever of Y/Z has the LARGER
        // overall bounding-box span (from the union of every solid body's IBody2.GetBodyBox) — then top/bottom
        // means "normal points + / - along THAT specific axis", which a side wall (normal on the OTHER two axes)
        // cannot satisfy. Verified live: same fixture now resolves to the true 400mm² cap.
        private static List<PlanarFace> ResolveCandidateFaces(object[] bodies, bool wantTop, bool wantBottom = false)
        {
            var planars = CollectPlanarFaces(bodies);
            if (wantTop || wantBottom)
            {
                int vAxis = VerticalAxisIndex(bodies);   // 1 = Y, 2 = Z
                PlanarFace best = null; double bestScore = wantTop ? -2 : 2;
                foreach (var p in planars)
                {
                    if (p.Normal == null || p.Normal.Length < 3) continue;
                    double score = p.Normal[vAxis];
                    if (wantTop ? (score > bestScore) : (score < bestScore)) { bestScore = score; best = p; }
                }
                bool clear = wantTop ? bestScore > 0.5 : bestScore < -0.5;
                if (best != null && clear)
                {
                    var rest = new List<PlanarFace>();
                    foreach (var p in planars) if (p != best) rest.Add(p);
                    rest.Sort((a, b) => b.AreaMm2.CompareTo(a.AreaMm2));
                    var ordered = new List<PlanarFace> { best };
                    ordered.AddRange(rest);
                    return ordered;
                }
                // no clearly top/bottom face → fall through to area-descending order below
            }
            planars.Sort((a, b) => b.AreaMm2.CompareTo(a.AreaMm2));
            return planars;
        }

        // whichever of Y (index 1) or Z (index 2) has the LARGER span across every solid body's bounding box —
        // the part's own "vertical" axis, not an assumed one. Defaults to Z (index 2) if bodies carry no box.
        private static int VerticalAxisIndex(object[] bodies)
        {
            double[] box = null;
            foreach (var bo in bodies ?? new object[0])
            {
                var body = bo as Body2; if (body == null) continue;
                double[] b = null; try { b = body.GetBodyBox() as double[]; } catch { }
                if (b == null || b.Length < 6) continue;
                box = box == null ? b : new[]
                {
                    Math.Min(box[0], b[0]), Math.Min(box[1], b[1]), Math.Min(box[2], b[2]),
                    Math.Max(box[3], b[3]), Math.Max(box[4], b[4]), Math.Max(box[5], b[5])
                };
            }
            if (box == null || box.Length < 6) return 2;
            double ySpan = box[4] - box[1], zSpan = box[5] - box[2];
            return zSpan >= ySpan ? 2 : 1;
        }

        private static List<PlanarFace> CollectPlanarFaces(object[] bodies)
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
                    double[] centroid = SafeInteriorPoint(face, box);
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
            return planars;
        }

        // A SAFE interior point on the face, in part/model space. `Face2.GetClosestPointOn` respects the face's TRIMMED
        // boundary — if the box-centre sample falls outside an irregular (non-rectangular, e.g. leaf/crescent-shaped)
        // face, it snaps to the nearest boundary EDGE instead of the interior, and a circle sketched there pokes off
        // the face and the cut fails. Sample the box centre first, then 8 inset points (35%/65% along each in-plane
        // axis), and accept the FIRST sample whose snapped point stays close to where it was sampled (genuinely
        // interior, not boundary-snapped) — center-first so a rectangular face still gets its natural middle.
        private static double[] SafeInteriorPoint(Face2 face, double[] box)
        {
            if (box == null || box.Length < 6) return null;
            double[] fracs = { 0.5, 0.35, 0.65 };
            double diag = Math.Sqrt(Math.Pow(box[3] - box[0], 2) + Math.Pow(box[4] - box[1], 2) + Math.Pow(box[5] - box[2], 2));
            double tol = Math.Max(0.001, Math.Min(0.002, diag * 0.03));   // metres; small vs the face's own size
            double[] fallback = null;
            foreach (double fx in fracs)
                foreach (double fy in fracs)
                    foreach (double fz in fracs)
                    {
                        double[] c = {
                            box[0] + fx * (box[3] - box[0]),
                            box[1] + fy * (box[4] - box[1]),
                            box[2] + fz * (box[5] - box[2])
                        };
                        double[] p = null; try { p = face.GetClosestPointOn(c[0], c[1], c[2]) as double[]; } catch { }
                        if (p == null || p.Length < 3) continue;
                        if (fallback == null) fallback = new[] { p[0], p[1], p[2] };
                        double d = Math.Sqrt(Math.Pow(p[0] - c[0], 2) + Math.Pow(p[1] - c[1], 2) + Math.Pow(p[2] - c[2], 2));
                        if (d <= tol) return new[] { p[0], p[1], p[2] };   // genuinely interior — use it
                    }
            return fallback;   // every sample snapped to a boundary — best-effort (old behaviour), let the cut verify honestly
        }

        // the smaller of the two in-plane spans of a planar face (mm). A face perpendicular to a principal axis has one
        // near-zero box dimension (its thickness); the smallest of the three sorted dims is that ~0, so the SMALLER span
        // is the MIDDLE value. Used for the won't-fit sanity gate.
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

        // Project a model/part-space point onto the ACTIVE sketch's 2D coordinate system. SketchManager entity-creation
        // (CreateCircleByRadius) interprets its X,Y in SKETCH space, NOT model space — a circle drawn at the raw model
        // centroid would land at the wrong place on a face whose sketch origin isn't the model origin. ISketch.
        // ModelToSketchTransform maps model->sketch; apply it to the centroid to get the correct sketch X,Y.
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

        // does a cylindrical face of ~the requested radius now exist on the solid? (the drilled bore). Tolerance scales
        // with the radius so a 3mm and a 20mm bore both match; independent of the cut's own return code (Rule #6).
        private static bool HasNewBoreFace(PartDoc part, double reqRadiusM)
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

        // delete the tagged hole and rebuild — restores the solid so a failed cut never ships a broken part
        private static void RollbackHole(IModelDoc2 model)
        {
            try
            {
                var f = FindFeatureByName(model, HoleFeatureName);
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

        // diameter in mm from the intent: "10mm" / "6 mm" wins; else inches ("0.25 inch", "1/4\"", "quarter inch",
        // "half inch", "eighth inch") -> mm; else a bare number ("a 6 hole") as mm; else the 8mm default.
        // explicit=false only on the default fallback — lets Run() know it's free to shrink the hole to fit
        // (Rule #2 still applies to anything the user actually stated).
        private static double ParseDiameterMm(string intent, out bool explicitDia)
        {
            string c = (intent ?? "").ToLowerInvariant();
            explicitDia = true;

            var mm = Regex.Match(c, @"(\d+(\.\d+)?)\s*mm");
            if (mm.Success && double.TryParse(mm.Groups[1].Value, out double vmm) && vmm > 0) return vmm;

            // fractional-inch words
            if (Regex.IsMatch(c, @"\bquarter\s*(inch|in|\"")")) return 6.35;
            if (Regex.IsMatch(c, @"\bhalf\s*(inch|in|\"")")) return 12.7;
            if (Regex.IsMatch(c, @"\beighth\s*(inch|in|\"")")) return 3.175;

            // "3/8 inch" style fraction
            var frac = Regex.Match(c, @"(\d+)\s*/\s*(\d+)\s*(inch|in|\"")");
            if (frac.Success && double.TryParse(frac.Groups[1].Value, out double num) && double.TryParse(frac.Groups[2].Value, out double den) && den > 0)
                return num / den * 25.4;

            // decimal inches: "0.25 inch", "0.5\""
            var inch = Regex.Match(c, @"(\d+(\.\d+)?)\s*(inch(es)?|in\b|\"")");
            if (inch.Success && double.TryParse(inch.Groups[1].Value, out double vin) && vin > 0) return vin * 25.4;

            // bare number as mm
            var bare = Regex.Match(c, @"\b(\d+(\.\d+)?)\b");
            if (bare.Success && double.TryParse(bare.Groups[1].Value, out double vb) && vb > 0) return vb;

            explicitDia = false;
            return DefaultDiaMm;
        }

        // when no size was stated (test-loop wrong-answer add-screw-hole: the 8mm default was wider than the
        // target face's shorter span, so the handler dead-ended with a bare "won't fit" error on a real, doable
        // task instead of trying an ordinary smaller screw size), fall back through common small-fastener
        // diameters until one actually fits and drills clean. Never applied when the user stated a size (Rule #2
        // — an explicit request is never silently downsized).
        private static readonly double[] StandardCascadeMm = { 8.0, 6.0, 5.0, 4.0, 3.0, 2.5, 2.0 };
        private static double[] DiameterCascadeMm(double requestedMm, bool explicitDia)
        {
            if (explicitDia) return new[] { requestedMm };
            var list = new List<double> { requestedMm };
            foreach (var d in StandardCascadeMm) if (d < requestedMm) list.Add(d);
            return list.ToArray();
        }

        private static string Trim(double v) => v.ToString("0.###");
    }
}
