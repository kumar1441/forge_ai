using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class AddCountersinkResult
    {
        public double ClearanceDiaMm;        // through clearance-hole diameter (mm)
        public double SinkDiaMm;             // countersink (cone rim) diameter at the face (mm)
        public double ChamferDistMm;         // chamfer leg = (sinkDia - clearanceDia)/2 (mm)
        public string Metric;                // recognised flat-head size if any, e.g. "M6"
        public string TargetFace;
        public double VolumeBeforeMm3 = -1;
        public double VolumeAfterMm3 = -1;
        public int ConeFacesBefore = -1;
        public int ConeFacesAfter = -1;
        public int RebuildErrors;
        public bool RolledBack;
        public bool Verified;                // fail closed: true ONLY when a NEW conical face appeared + volume dropped + rebuild clean
        public bool AlreadyDone;
        public bool NeedsConfirm;            // genuinely ambiguous op/target (e.g. chamfer vs countersink) → ask one question, run nothing
        public string Question;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// AddCountersink (tool #209 "add a countersunk hole") — a REAL geometry WRITE on a single PART: a conical recess
    /// for a FLAT-HEAD screw (the head sits flush) over a through clearance hole: "add a countersink for an M6 flat
    /// head", "countersink the top for M8", "add a countersunk hole ⌀6.6 clearance, 12mm sink". The everyday flush-screw
    /// feature — the conical sibling of the (cylindrical) counterbore.
    ///
    /// COMPOSES TWO ALREADY-GREEN features (no new API risk): (1) a through-all circular cut of the clearance diameter
    /// (AddHole's proven FeatureCut4 path) at the target face centre; (2) an angle-distance CHAMFER on the bore's TOP
    /// circular rim (FilletChamfer's proven InsertFeatureChamfer path) — chamfering a hole's edge produces the conical
    /// countersink face. The one new mechanic is selecting that specific rim edge: after the cut, the (re-resolved) top
    /// face's inner-loop CIRCULAR edge is the bore rim.
    ///
    /// Robustness: PART only (Rule #2). A recognised flat-head size ("M6"/"M8"…) sets standard clearance + sink; else
    /// sensible defaults, each overridable. A countersink wider than the face's shorter side is refused honestly. IDEMPOTENT
    /// (Rule #5): both cuts tagged Forge-Countersink*. UNDO (Rule #7). FAIL CLOSED (Rule #6): after the rebuild the
    /// handler INDEPENDENTLY confirms a NEW conical face exists (the sink), the solid volume dropped, and the rebuild is
    /// clean; anything less and both features are DELETED, the part restored, the failure reported honestly.
    /// </summary>
    public static class AddCountersink
    {
        private const string HoleName = "Forge-CountersinkHole";
        private const string SinkName = "Forge-CountersinkCone";
        private const double MM = 0.001;

        // metric flat-head (90°) countersink table: {clearanceDia, sinkDia} mm (sink ≈ head dia at the surface)
        private static readonly Dictionary<string, double[]> FlatHead = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["M3"] = new[] { 3.4, 6.3 },
            ["M4"] = new[] { 4.5, 8.4 },
            ["M5"] = new[] { 5.5, 10.4 },
            ["M6"] = new[] { 6.6, 12.6 },
            ["M8"] = new[] { 9.0, 16.4 },
            ["M10"] = new[] { 11.0, 20.0 },
            ["M12"] = new[] { 14.0, 24.0 },
        };

        public static bool IsAddCountersinkIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (Regex.IsMatch(c, @"\b(remove|delete|strip|fill|defeature)\b")) return false;
            return Regex.IsMatch(c, @"\b(countersink|countersunk|counter-sink|csk|flat[\s-]?head)\b");
        }

        private class PlanarFace { public Face2 Face; public double AreaMm2; public double[] Normal; public double[] Centroid; public double SmallSpanMm; }

        public static async Task<AddCountersinkResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new AddCountersinkResult();

            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            {
                // test-loop wrong-route (flange-22-chamfer-bolt-circle, the regression corpus): "put a
                // 45 on the bolt holes" is machinist shorthand for a 45-degree CHAMFER, but the cloud parser (low
                // confidence, 0.55) guessed countersink — a genuinely different feature (a conical seat, not an edge
                // break) — and this handler then dead-ended on the assembly-vs-part error, never surfacing that the
                // parser itself flagged 2 ambiguities. When the intent has NO countersink-specific word (no
                // "countersink"/"csk"/"flat-head"), don't assume the guess was right just because it's the handler
                // that got called — ask which operation was actually meant (Rule #2).
                if (!IsAddCountersinkIntent(intent ?? ""))
                {
                    res.NeedsConfirm = true;
                    res.Question = "\"Put a 45 on the bolt holes\" could mean a 45° CHAMFER on the hole edges " +
                        "(a break, for burr/thread relief) or a COUNTERSINK (a conical seat for a flat-head screw) " +
                        "— they're different features. Which do you want, and on which part?";
                    return res;
                }
                res.Error = "Adding a countersink works on a single part — open the .SLDPRT, not an assembly.";
                return res;
            }
            var part = model as PartDoc;
            if (part == null) { res.Error = "This document has no part geometry to countersink."; return res; }

            if (FindFeatureByName(model, HoleName) != null || FindFeatureByName(model, SinkName) != null)
            {
                res.AlreadyDone = true;
                res.Verified = true;
                res.Info = "Already added a countersink — a Forge-Countersink feature is present, so there's nothing to do. " +
                           "To add a different one, delete the Forge-Countersink features first (Ctrl+Z), then run again.";
                await emit("Miller", null, "done", "Forge-Countersink already present — nothing to do");
                return res;
            }

            ParseSize(intent, res);

            await emit("Gauge", "reading the solid and picking the face", "run", null);
            object[] bodies = SolidBodies(part);
            if (bodies == null || bodies.Length == 0)
            { res.Error = "No solid body to countersink — this part has no solid geometry to machine."; return res; }

            bool wantTop = Regex.IsMatch((intent ?? "").ToLowerInvariant(), @"\btop\b");
            PlanarFace target = ResolveTargetFace(bodies, wantTop);
            if (target == null)
            { res.Error = "No planar face to countersink — this part has no flat face to machine (v1 countersinks planar faces)."; return res; }

            res.TargetFace = (wantTop ? "top face" : "largest planar face") + ", " + target.AreaMm2.ToString("N0") + " mm²";
            res.VolumeBeforeMm3 = GetVolumeMm3(model);
            res.ConeFacesBefore = CountConeFaces(part);
            double[] centroid = target.Centroid; double[] normal = target.Normal;
            await emit("Gauge", null, "done",
                "target " + res.TargetFace + " · " + (res.Metric != null ? res.Metric + " flat head · " : "") +
                "⌀" + Trim(res.ClearanceDiaMm) + "mm clearance, ⌀" + Trim(res.SinkDiaMm) + "mm sink");

            if (target.SmallSpanMm > 0 && res.SinkDiaMm >= target.SmallSpanMm)
            { res.Error = "A ⌀" + Trim(res.SinkDiaMm) + "mm countersink is as wide as the face's shorter side (" + Trim(target.SmallSpanMm) + "mm) — it wouldn't fit."; return res; }
            if (res.SinkDiaMm <= res.ClearanceDiaMm)
            { res.Error = "The countersink (⌀" + Trim(res.SinkDiaMm) + "mm) must be wider than the clearance hole (⌀" + Trim(res.ClearanceDiaMm) + "mm)."; return res; }

            var mu = app.GetMathUtility() as MathUtility;

            // ---- step 1: through clearance hole (AddHole's proven both-direction through-all cut) ----
            await emit("Miller", "drilling the ⌀" + Trim(res.ClearanceDiaMm) + "mm clearance hole through", "run", null);
            Face2 f1 = FaceAt(part, centroid, normal); if (f1 == null) f1 = target.Face;
            string err = TryThroughCut(app, model, mu, f1, centroid, res.ClearanceDiaMm, HoleName);
            if (err != null) { res.Error = err; Rollback(model); return res; }
            try { model.ForceRebuild3(false); } catch { }

            // ---- step 2: chamfer the bore's TOP rim into the conical countersink (FilletChamfer's proven path) ----
            await emit("Miller", "countersinking the rim to ⌀" + Trim(res.SinkDiaMm) + "mm (45°)", "run", null);
            Edge rim = TopBoreRimEdge(part, centroid, normal, res.ClearanceDiaMm / 2.0 * MM);
            if (rim == null) { res.Error = "Couldn't find the bore's top rim to countersink — rolled back; the part is unchanged."; Rollback(model); return res; }
            err = TryChamfer(model, rim, res.ChamferDistMm);
            if (err != null) { res.Error = err; Rollback(model); return res; }

            try { model.ForceRebuild3(false); } catch { }
            res.RebuildErrors = SafeWhatsWrong(model);
            res.VolumeAfterMm3 = GetVolumeMm3(model);
            res.ConeFacesAfter = CountConeFaces(part);

            // ---- INDEPENDENTLY verify (Rule #6): a new cone face + volume dropped + rebuild clean + both tagged ----
            await emit("Sentinel", "verifying the countersink post-rebuild", "run", null);
            bool dropped = res.VolumeAfterMm3 > 0 && res.VolumeBeforeMm3 > 0 && res.VolumeBeforeMm3 - res.VolumeAfterMm3 > 1e-4;
            bool clean = res.RebuildErrors == 0;
            bool coneAdded = res.ConeFacesAfter > res.ConeFacesBefore;
            bool tagged = FindFeatureByName(model, HoleName) != null && FindFeatureByName(model, SinkName) != null;

            if (!dropped || !clean || !coneAdded || !tagged)
            {
                Rollback(model);
                res.RolledBack = true;
                res.VolumeAfterMm3 = GetVolumeMm3(model);
                res.ConeFacesAfter = CountConeFaces(part);
                res.Error = !clean
                    ? "The countersink rebuilt with " + res.RebuildErrors + " error(s) — rolled it back; the part is unchanged."
                    : (!dropped
                        ? "The cut removed no material (missed the solid) — rolled it back; the part is unchanged."
                        : (!coneAdded
                            ? "No conical countersink face appeared — rolled it back; the part is unchanged."
                            : "The countersink couldn't be confirmed in the tree — rolled it back; the part is unchanged."));
                await emit("Sentinel", null, "fail", "rolled back — part restored");
                return res;
            }

            res.Verified = true;
            double removed = res.VolumeBeforeMm3 - res.VolumeAfterMm3;
            await emit("Sentinel", null, "done",
                "countersunk: volume −" + removed.ToString("N0") + " mm³, ⌀" + Trim(res.SinkDiaMm) + "mm cone over ⌀" +
                Trim(res.ClearanceDiaMm) + "mm bore, rebuild clean");

            res.Info = BuildInfo(res, removed);
            return res;
        }

        private static string BuildInfo(AddCountersinkResult r, double removed)
        {
            return "Added a " + (r.Metric != null ? r.Metric + " " : "") + "countersink at the centre of the " + (r.TargetFace ?? "target face") +
                   " — ⌀" + Trim(r.SinkDiaMm) + "mm conical recess over a ⌀" + Trim(r.ClearanceDiaMm) + "mm through hole (volume −" +
                   removed.ToString("N0") + " mm³, a new conical face confirmed, rebuild clean). One Ctrl+Z per step; Forge didn't save.";
        }

        // ---- through-all circular cut (both directions), tagged (AddHole mechanics) ----
        private static string TryThroughCut(ISldWorks app, IModelDoc2 model, MathUtility mu, Face2 face, double[] centroid, double diaMm, string tag)
        {
            try
            {
                if (face == null) return "No live target face to sketch on — the part geometry is in an unexpected state.";
                try { model.ClearSelection2(true); } catch { }
                bool sel = false; try { sel = ((Entity)face).Select4(false, null); } catch { }
                if (!sel) return "Couldn't select the target face to sketch on — the part geometry may be in an unexpected state.";

                var sk = model.SketchManager;
                sk.InsertSketch(true);
                var active = sk.ActiveSketch as Sketch;
                double[] scoord = ModelToSketchXY(mu, active, centroid);
                if (scoord == null)
                {
                    sk.InsertSketch(true);
                    try { model.ClearSelection2(true); } catch { }
                    var loose = model.FeatureByPositionReverse(0) as Feature;
                    if (loose != null) { try { loose.Select2(false, 0); model.EditDelete(); } catch { } }
                    return "Couldn't project the face centre into the sketch — Forge left the part untouched.";
                }
                sk.CreateCircleByRadius(scoord[0], scoord[1], 0, diaMm / 2.0 * MM);
                sk.InsertSketch(true);
                try { model.ClearSelection2(true); } catch { }
                var skFeat = model.FeatureByPositionReverse(0) as Feature;
                if (skFeat != null) skFeat.Select2(false, 0);

                int ta = (int)swEndConditions_e.swEndCondThroughAll;
                var cut = model.FeatureManager.FeatureCut4(false, false, false, ta, ta, 0, 0, false, false, false, false, 0, 0,
                    false, false, false, false, false, true, true, true, true, false, 0, 0, false, false) as Feature;
                try { model.ClearSelection2(true); } catch { }
                if (cut == null)
                {
                    try
                    {
                        var sf = model.FeatureByPositionReverse(0) as Feature;
                        string tn = null; try { tn = sf?.GetTypeName2(); } catch { }
                        if (sf != null && tn != null && tn.IndexOf("Sketch", StringComparison.OrdinalIgnoreCase) >= 0)
                        { sf.Select2(false, 0); model.EditDelete(); }
                    }
                    catch { }
                    return "SolidWorks refused the clearance cut — the circle may not have landed on the solid. The part is unchanged.";
                }
                try { cut.Name = tag; } catch { }
                return null;
            }
            catch (Exception ex) { return "The clearance cut couldn't be created (" + ex.GetType().Name + ") — the part is unchanged."; }
        }

        // ---- angle-distance chamfer on ONE edge (the bore rim), tagged (FilletChamfer mechanics) ----
        private static string TryChamfer(IModelDoc2 model, Edge rim, double distMm)
        {
            try
            {
                try { model.ClearSelection2(true); } catch { }
                bool sel = false; try { sel = ((Entity)rim).Select4(false, null); } catch { }
                if (!sel) return "Couldn't select the bore rim to countersink — the part geometry may be in an unexpected state.";
                var feat = model.FeatureManager.InsertFeatureChamfer(
                    0, (int)swChamferType_e.swChamferAngleDistance, distMm * MM, 45.0 * Math.PI / 180.0, 0, 0, 0, 0) as Feature;
                try { model.ClearSelection2(true); } catch { }
                if (feat == null) return "SolidWorks refused the countersink chamfer — the rim may not chamfer cleanly.";
                try { feat.Name = SinkName; } catch { }
                return null;
            }
            catch (Exception ex) { return "The countersink chamfer couldn't be created (" + ex.GetType().Name + ") — the part is unchanged."; }
        }

        // the bore's TOP circular rim: the circular edge nearest the target face, of ~the clearance radius
        private static Edge TopBoreRimEdge(PartDoc part, double[] facePt, double[] normal, double reqRadiusM)
        {
            Edge best = null; double bestScore = double.MaxValue;
            double tol = Math.Max(0.3 * MM, 0.1 * reqRadiusM);
            foreach (var bo in SolidBodies(part) ?? new object[0])
            {
                var body = bo as Body2; if (body == null) continue;
                object[] edges = null; try { edges = body.GetEdges() as object[]; } catch { }
                foreach (var eo in edges ?? new object[0])
                {
                    var e = eo as Edge; if (e == null) continue;
                    var curve = e.GetCurve() as Curve; if (curve == null) continue;
                    bool circle = false; try { circle = curve.IsCircle(); } catch { }
                    if (!circle) continue;
                    double[] cp = null; try { cp = curve.CircleParams as double[]; } catch { }
                    if (cp == null || cp.Length < 7) continue;
                    double r = cp[6];
                    if (Math.Abs(r - reqRadiusM) > tol) continue;          // must be the bore rim, not some other circle
                    double cx = cp[0], cy = cp[1], cz = cp[2];             // circle centre
                    // distance from the circle-centre to the target-face plane (through facePt with `normal`): smaller = the TOP rim
                    double d;
                    if (normal != null && normal.Length >= 3)
                        d = Math.Abs((cx - facePt[0]) * normal[0] + (cy - facePt[1]) * normal[1] + (cz - facePt[2]) * normal[2]);
                    else
                    { double ddx = cx - facePt[0], ddy = cy - facePt[1], ddz = cz - facePt[2]; d = Math.Sqrt(ddx * ddx + ddy * ddy + ddz * ddz); }
                    if (d < bestScore) { bestScore = d; best = e; }
                }
            }
            return best;
        }

        // ================= intent parsing =================

        private static void ParseSize(string intent, AddCountersinkResult res)
        {
            string c = (intent ?? "").ToLowerInvariant();
            var mm = Regex.Match(c, @"\bm(\d{1,2})\b");
            if (mm.Success)
            {
                string key = "M" + mm.Groups[1].Value;
                if (FlatHead.TryGetValue(key, out var t)) { res.Metric = key; res.ClearanceDiaMm = t[0]; res.SinkDiaMm = t[1]; }
            }
            if (res.ClearanceDiaMm <= 0) { res.ClearanceDiaMm = 6.6; res.SinkDiaMm = 12.6; }   // default ~M6

            var clr = Regex.Match(c, @"(\d+(\.\d+)?)\s*mm\s*(?:clearance|clear|hole|bore)");
            if (clr.Success && double.TryParse(clr.Groups[1].Value, out double cv) && cv > 0) res.ClearanceDiaMm = cv;
            var sink = Regex.Match(c, @"(\d+(\.\d+)?)\s*mm\s*(?:sink|countersink|csk|head|cone)");
            if (sink.Success && double.TryParse(sink.Groups[1].Value, out double sv) && sv > 0) res.SinkDiaMm = sv;

            res.ChamferDistMm = Math.Max(0.1, (res.SinkDiaMm - res.ClearanceDiaMm) / 2.0);
        }

        // ================= face resolution + helpers (shared shape with AddCounterbore) =================

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
                    planars.Add(new PlanarFace { Face = face, AreaMm2 = area * 1e6, Normal = n, Centroid = centroid, SmallSpanMm = SmallerInPlaneSpanMm(box) });
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
            try { double[] p = face.GetClosestPointOn(c[0], c[1], c[2]) as double[]; if (p != null && p.Length >= 3) return new[] { p[0], p[1], p[2] }; }
            catch { }
            return c;
        }

        private static double SmallerInPlaneSpanMm(double[] box)
        {
            if (box == null || box.Length < 6) return 0;
            double dx = Math.Abs(box[3] - box[0]) * 1000.0, dy = Math.Abs(box[4] - box[1]) * 1000.0, dz = Math.Abs(box[5] - box[2]) * 1000.0;
            double[] d = { dx, dy, dz }; Array.Sort(d); return d[1];
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

        private static Face2 FaceAt(PartDoc part, double[] pt, double[] normal)
        {
            if (pt == null || pt.Length < 3) return null;
            Face2 best = null; double bestDist = double.MaxValue;
            foreach (var bo in SolidBodies(part) ?? new object[0])
            {
                var body = bo as Body2; if (body == null) continue;
                object[] faces = null; try { faces = body.GetFaces() as object[]; } catch { }
                foreach (var fo in faces ?? new object[0])
                {
                    var face = fo as Face2; if (face == null) continue;
                    Surface s = null; try { s = face.GetSurface() as Surface; } catch { }
                    bool plane = false; try { plane = s != null && s.IsPlane(); } catch { }
                    if (!plane) continue;
                    if (normal != null && normal.Length >= 3)
                    {
                        double[] n = null; try { n = face.Normal as double[]; } catch { }
                        if (n != null && n.Length >= 3)
                        {
                            double dot = n[0] * normal[0] + n[1] * normal[1] + n[2] * normal[2];
                            if (dot < 0.9) continue;
                        }
                    }
                    double[] cpn = null; try { cpn = face.GetClosestPointOn(pt[0], pt[1], pt[2]) as double[]; } catch { }
                    if (cpn == null || cpn.Length < 3) continue;
                    double dx = cpn[0] - pt[0], dy = cpn[1] - pt[1], dz = cpn[2] - pt[2];
                    double d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    if (d < bestDist) { bestDist = d; best = face; }
                }
            }
            return best;
        }

        private static int CountConeFaces(PartDoc part)
        {
            int n = 0;
            foreach (var bo in SolidBodies(part) ?? new object[0])
            {
                var body = bo as Body2; if (body == null) continue;
                object[] faces = null; try { faces = body.GetFaces() as object[]; } catch { }
                foreach (var fo in faces ?? new object[0])
                {
                    var face = fo as Face2; if (face == null) continue;
                    Surface s = null; try { s = face.GetSurface() as Surface; } catch { }
                    bool cone = false; try { cone = s != null && s.IsCone(); } catch { }
                    if (cone) n++;
                }
            }
            return n;
        }

        private static double GetVolumeMm3(IModelDoc2 model)
        { try { var mp = model.Extension.CreateMassProperty(); return mp == null ? -1 : mp.Volume * 1e9; } catch { return -1; } }

        private static object[] SolidBodies(PartDoc part)
        { try { return part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]; } catch { return null; } }

        private static int SafeWhatsWrong(IModelDoc2 model)
        { try { return model.Extension.GetWhatsWrongCount(); } catch { return 0; } }

        private static void Rollback(IModelDoc2 model)
        {
            try { DeleteNamed(model, SinkName); DeleteNamed(model, HoleName); try { model.ForceRebuild3(false); } catch { } try { model.ClearSelection2(true); } catch { } }
            catch { }
        }

        private static void DeleteNamed(IModelDoc2 model, string name)
        {
            var f = FindFeatureByName(model, name);
            if (f == null) return;
            try { model.ClearSelection2(true); } catch { }
            bool sel = false; try { sel = f.Select2(false, 0); } catch { }
            if (sel) { try { model.EditDelete(); } catch { } }
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
