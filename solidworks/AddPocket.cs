using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class AddPocketResult
    {
        public double WidthMm;               // pocket width parsed from the intent (mm); default 20
        public double LengthMm;              // pocket length parsed from the intent (mm); default 12
        public double DepthMm;               // pocket depth parsed from the intent (mm); default 6 (ignored when THROUGH)
        public bool Through;                 // a through-all slot was requested ("through", "slot ... through")
        public string TargetFace;            // which face was pocketed, e.g. "top face, 2400 mm²"
        public double VolumeBeforeMm3 = -1;  // solid volume before the cut (mm^3), independently measured
        public double VolumeAfterMm3 = -1;   // solid volume after the cut + rebuild (mm^3)
        public int RebuildErrors;            // GetWhatsWrongCount() post-rebuild (0 => clean)
        public bool RolledBack;              // the cut was created but failed to verify → deleted, part restored
        public bool Verified;                // fail closed: true ONLY when volume DROPPED plausibly and rebuild clean
        public bool AlreadyDone;             // idempotent: a Forge-Pocket already exists → nothing to do
        public string Info;                  // verdict-first panel line
        public string Error;                 // honest failure text (assembly, no solid, won't-fit, missed the solid)
    }

    /// <summary>
    /// AddPocket (tool #207 "cut a rectangular pocket / slot") — a REAL geometry WRITE on a single PART. It mills ONE
    /// rectangular recess (a cut-extrude of a rectangle) at the centre of a resolved face: "cut a 20x10mm pocket 5mm
    /// deep", "add a rectangular pocket", "mill a slot in the top", "pocket the top 8mm deep". Distinct from AddHole
    /// (a round bore) — pockets/slots/keyways/windows are a different, extremely common op, and this proves rectangular
    /// sketching, not just circles.
    ///
    /// The sketch/cut mechanics are REUSED verbatim from AddHole (the proven geometry-write spine): resolve the target
    /// face (largest planar, or the "top" face when named), open a sketch on it, draw a CENTRE RECTANGLE at the face
    /// centroid projected into sketch space (ISketch.ModelToSketchTransform), exit, and cut-extrude — BLIND to the
    /// requested depth (a real pocket), or THROUGH ALL when the intent says "through"/"slot ... through". The one thing
    /// beyond AddHole: a blind cut can face either way off the sketch plane, so the handler tries one direction, and if
    /// no material was removed it rolls back and FLIPS once (Rule #6, self-correct with geometry, max-1 flip).
    ///
    /// Robustness (the 12 rules): PART only — an assembly is refused honestly (Rule #2). Size/depth have sensible
    /// defaults (20×12×6 mm, face centre), so there is no ambiguity to ask about (Character #6); a pocket wider than the
    /// face's shorter side is refused honestly (Rule #2/#3, "won't fit"), never guessed smaller. IDEMPOTENT (Rule #5):
    /// the cut is tagged "Forge-Pocket"; a second run finds it and reports "already added a pocket — nothing to do".
    /// UNDO is sacred (Rule #7): one tagged feature, one Ctrl+Z; Forge never saves. FAIL CLOSED (Rule #6): after the
    /// rebuild the handler INDEPENDENTLY confirms the solid volume DROPPED by a plausible amount (~ w·l·depth) and the
    /// rebuild is clean; anything less and the Forge-Pocket feature is DELETED, the part restored, the failure reported
    /// honestly. Never a fake green.
    /// </summary>
    public static class AddPocket
    {
        private const string PocketFeatureName = "Forge-Pocket";
        private const double MM = 0.001;         // mm -> SW metres
        private const double DefaultWidthMm = 20.0;
        private const double DefaultLengthMm = 12.0;
        private const double DefaultDepthMm = 6.0;

        public static bool IsAddPocketIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            // removing/filling is defeature, not add
            if (Regex.IsMatch(c, @"\b(remove|delete|strip|get rid of|kill|fill|defeature)\b")) return false;
            bool addVerb = Regex.IsMatch(c, @"\b(add|cut|mill|make|create|put|machine|pocket)\b");
            bool hasPocket = Regex.IsMatch(c, @"\b(pocket|slot|recess|keyway|counterbore|window)\b");
            return addVerb && hasPocket;
        }

        private class PlanarFace { public Face2 Face; public double AreaMm2; public double[] Normal; public double[] Centroid; public double SmallSpanMm; public double[] Box; }

        public static async Task<AddPocketResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new AddPocketResult();

            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { res.Error = "Cutting a pocket works on a single part — open the .SLDPRT you want milled, not an assembly."; return res; }
            var part = model as PartDoc;
            if (part == null) { res.Error = "This document has no part geometry to pocket."; return res; }

            // ---- IDEMPOTENT (Rule #5): a Forge-Pocket already present → don't stack a second ----
            if (FindFeatureByName(model, PocketFeatureName) != null)
            {
                res.AlreadyDone = true;
                res.Verified = true;
                res.Info = "Already added a pocket — a Forge-Pocket feature is present, so there's nothing to do. To cut a " +
                           "different pocket, delete Forge-Pocket first (Edit > Delete, or Ctrl+Z), then run again.";
                await emit("Miller", null, "done", "Forge-Pocket already present — nothing to do");
                return res;
            }

            ParseSize(intent, res);

            await emit("Gauge", "reading the solid and picking the face", "run", null);
            object[] bodies = SolidBodies(part);
            if (bodies == null || bodies.Length == 0)
            { res.Error = "No solid body to pocket — this part has no solid geometry to mill a recess into."; return res; }

            bool wantTop = Regex.IsMatch((intent ?? "").ToLowerInvariant(), @"\btop\b");
            PlanarFace target = ResolveTargetFace(bodies, wantTop);
            if (target == null)
            { res.Error = "No planar face to pocket — this part has no flat face to mill a recess on (v1 pockets planar faces)."; return res; }

            res.TargetFace = (wantTop ? "top face" : "largest planar face") + ", " + target.AreaMm2.ToString("N0") + " mm²";
            res.VolumeBeforeMm3 = GetVolumeMm3(model);
            await emit("Gauge", null, "done",
                "target " + res.TargetFace + " · " + Trim(res.WidthMm) + "×" + Trim(res.LengthMm) + " mm" +
                (res.Through ? " through" : " × " + Trim(res.DepthMm) + " mm deep") + " · solid " +
                (res.VolumeBeforeMm3 > 0 ? res.VolumeBeforeMm3.ToString("N0") + " mm³" : "read"));

            // ---- sanity: a pocket wider than the face's shorter side won't fit → refuse honestly (Rule #2/#3) ----
            double biggerSpan = Math.Max(res.WidthMm, res.LengthMm);
            if (target.SmallSpanMm > 0 && biggerSpan >= target.SmallSpanMm)
            {
                res.Error = "A " + Trim(res.WidthMm) + "×" + Trim(res.LengthMm) + "mm pocket is as wide as the face's shorter side (" +
                            Trim(target.SmallSpanMm) + "mm) — it wouldn't fit. Pick a smaller size, say " +
                            Trim(Math.Max(2.0, target.SmallSpanMm / 3.0)) + "mm.";
                return res;
            }
            if (!res.Through && res.DepthMm <= 0)
            { res.Error = "A pocket needs a positive depth — say how deep, e.g. '5mm deep', or 'through' for a slot."; return res; }

            // ---- CLEAR-REGION PLACEMENT (Rule #8, ground in geometry): the face box-centre can snap onto a HOLE EDGE on a
            //      non-convex real face (a plate with a central hole), so the pocket straddles void and removes nothing.
            //      Refine the centre to a point whose FULL footprint lands on the trimmed (solid) face. Found by
            //      generalization on a real base plate; keeps the block behaviour (its box-centre is already clear). ----
            double halfW_m = res.WidthMm / 2.0 * MM, halfL_m = res.LengthMm / 2.0 * MM;
            double[] clear = ClearFootprintPoint(target.Face, target.Box, halfW_m, halfL_m);
            if (clear != null)
            {
                target.Centroid = clear;
                await emit("Miller", null, "done", "placing on clear solid (footprint-checked)");
            }

            // ---- WRITE: sketch a centre rectangle and cut blind (or through), with a max-1 flip if the first way misses ----
            await emit("Miller", "milling a " + Trim(res.WidthMm) + "×" + Trim(res.LengthMm) + " mm pocket" +
                (res.Through ? " through" : " " + Trim(res.DepthMm) + " mm deep") + " at the centre of the " + (wantTop ? "top face" : "largest face"), "run", null);

            var mu = app.GetMathUtility() as MathUtility;

            // first attempt: cut in the default direction
            string err = await TryCut(app, model, mu, target, res, reverse: false, emit);
            if (err != null) { res.Error = err; RollbackPocket(model); return res; }

            try { model.ForceRebuild3(false); } catch { }
            res.RebuildErrors = SafeWhatsWrong(model);
            res.VolumeAfterMm3 = GetVolumeMm3(model);
            bool dropped = RemovedEnough(res);

            // no material removed on a BLIND cut → the cut faced into void; flip once (Rule #6 self-correct with geometry)
            if (!dropped && !res.Through)
            {
                await emit("Miller", "first direction removed nothing — flipping the cut into the solid", "run", null);
                RollbackPocket(model);
                // Re-resolve the target face before the flip (RollbackPocket deleted+rebuilt → the captured Face2 is
                // stale), BUT RE-APPLY the clear-region placement to the fresh face — ResolveTargetFace resets Centroid
                // to the box-centre (which can be over a hole), so without this the flip cuts at the BAD spot and the
                // good dir-2 (into solid) still removes nothing. This was the actual real-plate bug.
                var tflip = ResolveTargetFace(SolidBodies(part), wantTop);
                if (tflip != null)
                {
                    var clear2 = ClearFootprintPoint(tflip.Face, tflip.Box, halfW_m, halfL_m);
                    if (clear2 != null) tflip.Centroid = clear2;
                    else if (clear != null) tflip.Centroid = clear;   // keep the prior clear point if re-scan finds none
                    target = tflip;
                }
                err = await TryCut(app, model, mu, target, res, reverse: true, emit);
                if (err != null) { res.Error = err; RollbackPocket(model); return res; }
                try { model.ForceRebuild3(false); } catch { }
                res.RebuildErrors = SafeWhatsWrong(model);
                res.VolumeAfterMm3 = GetVolumeMm3(model);
                dropped = RemovedEnough(res);
            }

            // ---- INDEPENDENTLY verify (Rule #6): volume dropped by a PLAUSIBLE amount + clean rebuild + feature present ----
            await emit("Sentinel", "verifying the pocket post-rebuild", "run", null);
            bool clean = res.RebuildErrors == 0;
            bool tagged = FindFeatureByName(model, PocketFeatureName) != null;
            double removed = res.VolumeBeforeMm3 - res.VolumeAfterMm3;
            double expected = res.WidthMm * res.LengthMm * (res.Through ? 0 : res.DepthMm); // 0 => skip the plausibility band for a through slot
            bool plausible = res.Through
                ? removed > 0
                : (expected <= 0 || (removed >= expected * 0.15 && removed <= expected * 4.0)); // loose band: catches a runaway/clipped cut

            if (!dropped || !clean || !tagged || !plausible)
            {
                RollbackPocket(model);
                res.RolledBack = true;
                res.VolumeAfterMm3 = GetVolumeMm3(model);
                res.Error = !clean
                    ? "The pocket rebuilt with " + res.RebuildErrors + " error(s) — rolled it back; the part is unchanged."
                    : (!dropped
                        ? "The cut removed no material (it missed the solid, both directions) — rolled it back; the part is unchanged."
                        : (!tagged
                            ? "The pocket could not be confirmed in the tree — rolled it back; the part is unchanged."
                            : "The cut removed an implausible amount of material (" + removed.ToString("N0") + " mm³ vs ~" +
                              expected.ToString("N0") + " expected) — rolled it back; the part is unchanged."));
                await emit("Sentinel", null, "fail", "rolled back — part restored");
                return res;
            }

            res.Verified = true;
            await emit("Sentinel", null, "done",
                "pocketed: volume −" + removed.ToString("N0") + " mm³, " + Trim(res.WidthMm) + "×" + Trim(res.LengthMm) + " mm" +
                (res.Through ? " through" : " × " + Trim(res.DepthMm) + " mm deep") + ", rebuild clean");

            res.Info = BuildInfo(res, removed);
            return res;
        }

        // one cut attempt (sketch a centre rectangle at the face centroid, cut blind-to-depth or through-all). Returns
        // an error string on a hard failure (leaves cleanup to the caller's RollbackPocket), or null on a created cut.
        private static async Task<string> TryCut(ISldWorks app, IModelDoc2 model, MathUtility mu, PlanarFace target, AddPocketResult res, bool reverse, Func<string, string, string, string, Task> emit)
        {
            Feature cut = null;
            try
            {
                try { model.ClearSelection2(true); } catch { }
                bool sel = false; try { sel = ((Entity)target.Face).Select4(false, null); } catch { }
                if (!sel) return "Couldn't select the target face to sketch on — the part geometry may be in an unexpected state.";

                var sk = model.SketchManager;
                sk.InsertSketch(true);
                var active = sk.ActiveSketch as Sketch;
                double[] sc = ModelToSketchXY(mu, active, target.Centroid);
                if (sc == null)
                {
                    sk.InsertSketch(true);
                    try { model.ClearSelection2(true); } catch { }
                    var loose = model.FeatureByPositionReverse(0) as Feature;
                    if (loose != null) { try { loose.Select2(false, 0); model.EditDelete(); } catch { } }
                    return "Couldn't project the face centre into the sketch — Forge left the part untouched.";
                }
                double halfW = res.WidthMm / 2.0 * MM;
                double halfL = res.LengthMm / 2.0 * MM;
                // centre rectangle: centre at the projected centroid, corner at (+halfW,+halfL)
                sk.CreateCenterRectangle(sc[0], sc[1], 0, sc[0] + halfW, sc[1] + halfL, 0);
                sk.InsertSketch(true);
                try { model.ClearSelection2(true); } catch { }

                var skFeat = model.FeatureByPositionReverse(0) as Feature;
                if (skFeat != null) skFeat.Select2(false, 0);

                int t1, t2; double d1;
                if (res.Through) { t1 = t2 = (int)swEndConditions_e.swEndCondThroughAll; d1 = 0; }
                else { t1 = (int)swEndConditions_e.swEndCondBlind; t2 = (int)swEndConditions_e.swEndCondBlind; d1 = res.DepthMm * MM; }

                // Sd=true (single ended for blind; harmless for through), Dir=reverse controls which way off the sketch
                // plane the cut goes. Same arg shape as AddHole's FeatureCut4 call.
                bool singleEnded = !res.Through;
                cut = model.FeatureManager.FeatureCut4(
                    singleEnded, false, reverse, t1, t2, d1, 0, false, false, false, false, 0, 0,
                    false, false, false, false, false, true, true, true, true, false, 0, 0, false, false) as Feature;
                try { model.ClearSelection2(true); } catch { }
            }
            catch (Exception ex)
            {
                return "The pocket cut couldn't be created (" + ex.GetType().Name + ") — Forge rolled back and left the part unchanged.";
            }

            if (cut == null)
            {
                // clean up a loose sketch if the cut was refused
                try
                {
                    var sf = model.FeatureByPositionReverse(0) as Feature;
                    string tn = null; try { tn = sf?.GetTypeName2(); } catch { }
                    if (sf != null && tn != null && tn.IndexOf("Sketch", StringComparison.OrdinalIgnoreCase) >= 0)
                    { sf.Select2(false, 0); model.EditDelete(); }
                }
                catch { }
                try { model.ClearSelection2(true); } catch { }
                return "SolidWorks refused the cut — the rectangle may not have landed on the solid. The part is unchanged.";
            }
            try { cut.Name = PocketFeatureName; } catch { }   // tag for idempotency (Rule #5)
            return null;
        }

        private static string BuildInfo(AddPocketResult r, double removed)
        {
            return "Cut a " + Trim(r.WidthMm) + "×" + Trim(r.LengthMm) + " mm pocket" +
                   (r.Through ? " through" : " " + Trim(r.DepthMm) + " mm deep") + " at the centre of the " + (r.TargetFace ?? "target face") +
                   " — volume " + r.VolumeBeforeMm3.ToString("N0") + " → " + r.VolumeAfterMm3.ToString("N0") +
                   " mm³ (−" + removed.ToString("N0") + "), rebuild clean. One Ctrl+Z removes it; Forge didn't save.";
        }

        // ================= intent parsing =================

        private static void ParseSize(string intent, AddPocketResult res)
        {
            string c = (intent ?? "").ToLowerInvariant();

            res.Through = Regex.IsMatch(c, @"\bthrough\b");

            // "20x10", "20 x 10", "20x10mm", "20 by 10" → width x length
            var wl = Regex.Match(c, @"(\d+(\.\d+)?)\s*(?:x|by|\*)\s*(\d+(\.\d+)?)");
            if (wl.Success && double.TryParse(wl.Groups[1].Value, out double w) && double.TryParse(wl.Groups[3].Value, out double l) && w > 0 && l > 0)
            { res.WidthMm = w; res.LengthMm = l; }
            else
            {
                // a single size word ("a 15mm pocket") sets a square-ish footprint
                var one = Regex.Match(c, @"(\d+(\.\d+)?)\s*mm");
                if (one.Success && double.TryParse(one.Groups[1].Value, out double s) && s > 0)
                { res.WidthMm = s; res.LengthMm = s; }
                else { res.WidthMm = DefaultWidthMm; res.LengthMm = DefaultLengthMm; }
            }

            // depth: "5mm deep", "deep 5mm", "depth of 4mm", "4 mm deep"
            var dep = Regex.Match(c, @"(\d+(\.\d+)?)\s*mm\s*deep");
            if (!dep.Success) dep = Regex.Match(c, @"deep\s*(?:of\s*)?(\d+(\.\d+)?)\s*mm");
            if (!dep.Success) dep = Regex.Match(c, @"depth\s*(?:of\s*)?(\d+(\.\d+)?)\s*mm");
            if (dep.Success && double.TryParse(dep.Groups[1].Value, out double d) && d > 0) res.DepthMm = d;
            else res.DepthMm = DefaultDepthMm;
        }

        // ================= face resolution (mirrors AddHole) =================

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
                        SmallSpanMm = SmallerInPlaneSpanMm(box),
                        Box = box
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
                    double up = Math.Max(p.Normal[1], p.Normal[2]);
                    if (up > bestUp) { bestUp = up; best = p; }
                }
                if (best != null && bestUp > 0.5) return best;
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

        // Find a point on the face whose full W×L FOOTPRINT lands on the trimmed (solid) region — not over an inner-loop
        // hole. Samples a grid across the face's in-plane extent; a candidate qualifies only if the centre AND all four
        // footprint corners are genuinely ON the face (GetClosestPointOn ≈ self, i.e. inside the outer loop and outside
        // every hole). Returns the qualifying point closest to the box centre; null if none (caller keeps box-centre).
        private static double[] ClearFootprintPoint(Face2 face, double[] box, double halfW_m, double halfL_m)
        {
            if (face == null || box == null || box.Length < 6) return null;
            // perpendicular axis = smallest box extent; the other two are in-plane (u, v)
            double[] ext = { box[3] - box[0], box[4] - box[1], box[5] - box[2] };
            int perp = 0; for (int a = 1; a < 3; a++) if (ext[a] < ext[perp]) perp = a;
            int u = (perp + 1) % 3, v = (perp + 2) % 3;
            double planeVal = (box[perp] + box[perp + 3]) / 2.0;
            double[] ctr = { (box[0] + box[3]) / 2.0, (box[1] + box[4]) / 2.0, (box[2] + box[5]) / 2.0 };

            double[] best = null; double bestDist = double.MaxValue;
            const int N = 11;
            for (int i = 0; i < N; i++)
                for (int j = 0; j < N; j++)
                {
                    double uu = box[u] + (box[u + 3] - box[u]) * (i / (N - 1.0));
                    double vv = box[v] + (box[v + 3] - box[v]) * (j / (N - 1.0));
                    double[] P = new double[3]; P[perp] = planeVal; P[u] = uu; P[v] = vv;
                    if (!OnFace(face, P)) continue;
                    // the pocket footprint (centre ± half in each in-plane axis) must also be on solid
                    if (!OnFace(face, MakePt(perp, planeVal, u, uu + halfW_m, v, vv + halfL_m))) continue;
                    if (!OnFace(face, MakePt(perp, planeVal, u, uu + halfW_m, v, vv - halfL_m))) continue;
                    if (!OnFace(face, MakePt(perp, planeVal, u, uu - halfW_m, v, vv + halfL_m))) continue;
                    if (!OnFace(face, MakePt(perp, planeVal, u, uu - halfW_m, v, vv - halfL_m))) continue;
                    double dx = P[0] - ctr[0], dy = P[1] - ctr[1], dz = P[2] - ctr[2];
                    double d = dx * dx + dy * dy + dz * dz;
                    if (d < bestDist) { bestDist = d; best = new[] { P[0], P[1], P[2] }; }
                }
            if (best == null) return null;
            try { double[] q = face.GetClosestPointOn(best[0], best[1], best[2]) as double[]; if (q != null && q.Length >= 3) return new[] { q[0], q[1], q[2] }; }
            catch { }
            return best;
        }

        private static double[] MakePt(int a1, double v1, int a2, double v2, int a3, double v3)
        { double[] p = new double[3]; p[a1] = v1; p[a2] = v2; p[a3] = v3; return p; }

        // is P genuinely ON the trimmed face? GetClosestPointOn returns the nearest point WITHIN the face's boundary; if P
        // is over a hole (inner loop) or off the outer edge, the returned point is a boundary point > tol away.
        private static bool OnFace(Face2 face, double[] P)
        {
            try
            {
                double[] q = face.GetClosestPointOn(P[0], P[1], P[2]) as double[];
                if (q == null || q.Length < 3) return false;
                double dx = q[0] - P[0], dy = q[1] - P[1], dz = q[2] - P[2];
                return Math.Sqrt(dx * dx + dy * dy + dz * dz) < 0.2 * MM;
            }
            catch { return false; }
        }

        private static double SmallerInPlaneSpanMm(double[] box)
        {
            if (box == null || box.Length < 6) return 0;
            double dx = Math.Abs(box[3] - box[0]) * 1000.0;
            double dy = Math.Abs(box[4] - box[1]) * 1000.0;
            double dz = Math.Abs(box[5] - box[2]) * 1000.0;
            double[] d = { dx, dy, dz };
            Array.Sort(d);
            return d[1];
        }

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

        /// <summary>
        /// Did the cut actually remove material? Compare against the volume this pocket is EXPECTED to remove, never
        /// against a fixed percentage of the part. The old test (`after &lt; before * 0.999`) demanded a 0.1% drop, but a
        /// 6x4x1.5mm pocket in a 160x160x4mm plate is only 0.035% — so a perfectly good cut read as "removed nothing",
        /// got flipped (destroying it) and rolled back. That was the entire "blind cut on large faces" bug: the
        /// geometry always worked, the VERIFICATION was wrong. Scales correctly to any part size.
        /// </summary>
        private static bool RemovedEnough(AddPocketResult res)
        {
            if (res.VolumeBeforeMm3 <= 0 || res.VolumeAfterMm3 <= 0) return false;
            double removed = res.VolumeBeforeMm3 - res.VolumeAfterMm3;
            // blind pocket: expect ~W*L*D. Through cut: depth is the (unknown) wall thickness, so just require a
            // real, measurable removal. 25% of expected tolerates fillet/draft losses without accepting noise.
            double expected = res.Through ? 0 : res.WidthMm * res.LengthMm * res.DepthMm;
            double minDrop = expected > 0 ? expected * 0.25 : 1e-4;
            return removed > minDrop;
        }

        private static double GetVolumeMm3(IModelDoc2 model)
        {
            try { var mp = model.Extension.CreateMassProperty(); return mp == null ? -1 : mp.Volume * 1e9; }
            catch { return -1; }
        }

        private static object[] SolidBodies(PartDoc part)
        { try { return part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]; } catch { return null; } }

        private static int SafeWhatsWrong(IModelDoc2 model)
        { try { return model.Extension.GetWhatsWrongCount(); } catch { return 0; } }

        private static void RollbackPocket(IModelDoc2 model)
        {
            try
            {
                var f = FindFeatureByName(model, PocketFeatureName);
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

        private static string Trim(double v) => v.ToString("0.###");
    }
}
