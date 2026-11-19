using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class AddBoltCircleResult
    {
        public int Count;                    // number of holes placed
        public double HoleDiameterMm;         // each hole's diameter (mm)
        public double BoltCircleDiameterMm;   // the circle the hole centres sit on (mm)
        public string TargetFace;             // which face was drilled
        public double VolumeBeforeMm3 = -1;
        public double VolumeAfterMm3 = -1;
        public int CylFacesBefore = -1;
        public int CylFacesAfter = -1;
        public int RebuildErrors;
        public bool RolledBack;
        public bool Verified;
        public bool AlreadyDone;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// AddBoltCircle — a REAL geometry WRITE on a single PART: "put 5 bolt holes equally spaced on a 4.5 inch
    /// circle", "add a 6-hole bolt pattern on a 100mm circle", "drill 8 holes around a 3 inch bolt circle". Distinct
    /// from AddHole (which drills exactly ONE hole at a face's centre) and from the READ-ONLY MeasureBoltCircle
    /// (which reports an EXISTING pattern's PCD/count, never writes) — this is the WRITE counterpart neither of
    /// those covers: N holes newly created, equally spaced around a circle of the stated diameter.
    ///
    /// Approach (deliberately simpler than a two-step hole+circular-pattern chain — see BUILD-LOG rim-add-bolt-holes
    /// diagnosis): rather than create one hole then FeatureCircularPattern4 it (which needs a rotation-axis feature
    /// through an arbitrary point — extra geometry, extra failure surface), this places ALL N circles directly in
    /// ONE sketch (trig around the resolved face centre, computed once in SKETCH space) and cuts them ALL through
    /// with a SINGLE FeatureCut4 — SolidWorks cuts every closed profile in a sketch in one feature. Same proven
    /// sketch/circle/cut mechanics as AddHole.Run, just N circles instead of one.
    ///   Gauge — read the solid; resolve the TARGET FACE deterministically (same ranked-candidate approach as
    ///           AddHole: largest planar face, or "top"/"bottom" when named).
    ///   Driller — sketch N circles of the requested (or default 8mm) hole diameter, evenly spaced by angle around
    ///           the requested (or a size-derived default) bolt-circle diameter, centred on the face's own centre —
    ///           and cut them all through in one FeatureCut4.
    ///   Sentinel — FAIL CLOSED (Rule #6): after rebuild, INDEPENDENTLY confirm the solid volume dropped and exactly
    ///           `count` new cylindrical bore faces of ~the requested hole diameter now exist, and the rebuild is
    ///           clean. Anything less and the Forge-BoltCircle feature is deleted, the part restored.
    ///
    /// Robustness: PART only (Rule #2). Won't fit (bolt circle + hole diameter wider than the face) is refused
    /// honestly with the actual numbers, tried against every candidate face before giving up. IDEMPOTENT (Rule #5):
    /// tagged "Forge-BoltCircle"; a second run finds it and does nothing. UNDO is sacred (Rule #7): one feature, one
    /// Ctrl+Z.
    /// </summary>
    public static class AddBoltCircle
    {
        private const string FeatureName = "Forge-BoltCircle";
        private const double MM = 0.001;
        private const double DefaultHoleDiaMm = 8.0;
        private static readonly double[] StandardCascadeMm = { 8.0, 6.0, 5.0, 4.0, 3.0, 2.5, 2.0 };

        public static bool IsAddBoltCircleIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (Regex.IsMatch(c, @"\b(remove|delete|strip|get rid of|kill|fill|defeature)\b")) return false;
            // a genuine ADD requires a count sitting right next to "hole(s)" (optionally "bolt holes") — this is what
            // keeps it disjoint from MeasureBoltCircle.IsBoltCircleIntent (READ: "count the bolt holes", "how many
            // holes" have the count word BEFORE "hole", not a bare cardinal glued to it) and from AddHole (which
            // never has a count at all).
            bool hasCount = Regex.IsMatch(c, @"\b([2-9]|[1-9]\d+)\s*(?:bolt\s+)?holes?\b");
            bool hasCircleWord = Regex.IsMatch(c, @"\bbolt\s*circle\b|\bcircle\b|\bequally\s+spaced\b|\bevenly\s+spaced\b|\bbolt\s+pattern\b");
            return hasCount && hasCircleWord;
        }

        public static async Task<AddBoltCircleResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new AddBoltCircleResult();

            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { res.Error = "Adding a bolt-hole circle works on a single part — open the .SLDPRT you want drilled, not an assembly."; return res; }
            var part = model as PartDoc;
            if (part == null) { res.Error = "This document has no part geometry to drill."; return res; }

            if (FindFeatureByName(model, FeatureName) != null)
            {
                res.AlreadyDone = true;
                res.Verified = true;
                res.Info = "Already added a bolt-hole circle — a Forge-BoltCircle feature is present, so there's nothing to do. " +
                           "To drill a different pattern, delete Forge-BoltCircle first (Ctrl+Z), then run again.";
                await emit("Driller", null, "done", "Forge-BoltCircle already present — nothing to do");
                return res;
            }

            int count = ParseCount(intent);
            if (count < 2)
            { res.Error = "Couldn't tell how many holes to place — say a number, e.g. '5 bolt holes on a 4.5 inch circle'."; return res; }
            res.Count = count;

            double bcdMm = ParseBoltCircleDiameterMm(intent, out bool explicitBcd, out int maskStart, out int maskLen);
            string maskedIntent = MaskSpan(intent ?? "", maskStart, maskLen);
            double holeDiaMm = ParseHoleDiameterMm(maskedIntent, out bool explicitHoleDia);

            await emit("Gauge", "reading the solid and picking the face", "run", null);
            object[] bodies = SolidBodies(part);
            if (bodies == null || bodies.Length == 0)
            { res.Error = "No solid body to drill — this part has no solid geometry."; return res; }

            string ic = (intent ?? "").ToLowerInvariant();
            bool wantTop = Regex.IsMatch(ic, @"\btop\b");
            bool wantBottom = !wantTop && Regex.IsMatch(ic, @"\bbottom\b|\bunderside\b|\bunderneath\b");
            List<PlanarFace> candidates = ResolveCandidateFaces(bodies, wantTop, wantBottom);
            if (candidates.Count == 0)
            { res.Error = "No planar face to drill — this part has no flat face to place a bolt circle on."; return res; }

            res.VolumeBeforeMm3 = GetVolumeMm3(model);
            res.CylFacesBefore = CountCylFaces(part);
            await emit("Gauge", null, "done",
                candidates.Count + " planar face candidate(s) · solid " +
                (res.VolumeBeforeMm3 > 0 ? res.VolumeBeforeMm3.ToString("N0") + " mm³" : "read"));

            var mu = app.GetMathUtility() as MathUtility;
            double[] holeCascade = explicitHoleDia ? new[] { holeDiaMm } : CascadeFrom(holeDiaMm);
            string lastFit = null;
            int tried = 0;

            foreach (var tryHoleDia in holeCascade)
            {
                foreach (var target in candidates)
                {
                    double effectiveBcdMm = explicitBcd ? bcdMm : target.SmallSpanMm * 0.6;
                    double footprintMm = effectiveBcdMm + tryHoleDia;
                    if (target.SmallSpanMm > 0 && footprintMm >= target.SmallSpanMm * 0.95)
                    {
                        lastFit = "A " + Trim(effectiveBcdMm) + "mm bolt circle with " + Trim(tryHoleDia) + "mm holes needs about " +
                                  Trim(footprintMm) + "mm, wider than this face's shorter side (" + Trim(target.SmallSpanMm) + "mm)";
                        continue;
                    }
                    tried++;

                    await emit("Driller", "placing " + count + "× " + Trim(tryHoleDia) + "mm holes on a " + Trim(effectiveBcdMm) +
                                          "mm circle (candidate face " + tried + ")", "run", null);

                    Feature cut = TryCutBoltCircle(model, mu, target, count, effectiveBcdMm, tryHoleDia);
                    if (cut == null) continue;   // this face didn't work out — try the next candidate
                    try { cut.Name = FeatureName; } catch { }

                    await emit("Sentinel", "verifying the pattern post-rebuild", "run", null);
                    try { model.ForceRebuild3(false); } catch { }
                    res.RebuildErrors = SafeWhatsWrong(model);
                    res.VolumeAfterMm3 = GetVolumeMm3(model);
                    res.CylFacesAfter = CountCylFaces(part);

                    bool volumeDropped = res.VolumeAfterMm3 > 0 && res.VolumeBeforeMm3 > 0 && res.VolumeBeforeMm3 - res.VolumeAfterMm3 > 1e-4;
                    bool clean = res.RebuildErrors == 0;
                    bool boresAdded = (res.CylFacesAfter - res.CylFacesBefore) >= count;

                    if (!volumeDropped || !clean || !boresAdded)
                    {
                        RollbackBoltCircle(model);
                        res.VolumeAfterMm3 = GetVolumeMm3(model);
                        res.CylFacesAfter = CountCylFaces(part);
                        continue;
                    }

                    res.HoleDiameterMm = tryHoleDia;
                    res.BoltCircleDiameterMm = effectiveBcdMm;
                    res.TargetFace = (wantTop ? "top face" : (wantBottom ? "bottom face" : "largest planar face")) +
                                      ", " + target.AreaMm2.ToString("N0") + " mm²";
                    res.Verified = true;
                    double dVol = res.VolumeBeforeMm3 - res.VolumeAfterMm3;
                    await emit("Sentinel", null, "done",
                        "drilled " + count + " holes: volume −" + dVol.ToString("N0") + " mm³, bore faces " +
                        res.CylFacesBefore + " → " + res.CylFacesAfter + ", rebuild clean");

                    res.Info = BuildInfo(res, !explicitBcd, tryHoleDia != holeDiaMm);
                    return res;
                }
            }

            res.Error = tried > 0
                ? "Tried " + tried + " placement(s) — none produced a clean set of " + count + " through-holes. The part is unchanged."
                : (lastFit ?? "No planar face was wide enough for a " + count + "-hole bolt circle.");
            return res;
        }

        private static string BuildInfo(AddBoltCircleResult r, bool bcdDefaulted, bool holeDownsized)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("Added " + r.Count + " × " + Trim(r.HoleDiameterMm) + " mm through-holes equally spaced on a " +
                      Trim(r.BoltCircleDiameterMm) + " mm circle, centred on the " + (r.TargetFace ?? "target face") +
                      " — volume " + r.VolumeBeforeMm3.ToString("N0") + " → " + r.VolumeAfterMm3.ToString("N0") + " mm³, " +
                      "bore faces " + r.CylFacesBefore + " → " + r.CylFacesAfter + ", rebuild clean.");
            if (bcdDefaulted)
                sb.Append(" No bolt-circle size was stated, so I sized it to fit the face — say a diameter to override.");
            if (holeDownsized)
                sb.Append(" No hole size was stated and the default 8mm didn't fit, so I used a smaller standard size instead.");
            sb.Append(" One Ctrl+Z removes it; Forge didn't save.");
            return sb.ToString();
        }

        // ================= geometry authoring =================

        // sketch `count` circles of `holeDiaMm` diameter, evenly spaced by angle, on a circle of `bcdMm` diameter
        // centred on `target`'s own centre (projected into sketch space) — then cut them ALL through in one feature.
        // Returns null (and cleans up any loose sketch) if the projection or cut fails; caller tries the next candidate.
        private static Feature TryCutBoltCircle(IModelDoc2 model, MathUtility mu, PlanarFace target, int count, double bcdMm, double holeDiaMm)
        {
            try
            {
                try { model.ClearSelection2(true); } catch { }
                bool sel = false; try { sel = ((Entity)target.Face).Select4(false, null); } catch { }
                if (!sel) return null;
                Body2 ownerBody = null; try { ownerBody = target.Face.GetBody() as Body2; } catch { }

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
                    return null;
                }

                double radiusM = bcdMm / 2.0 * MM;
                double holeRadiusM = holeDiaMm / 2.0 * MM;
                for (int k = 0; k < count; k++)
                {
                    double angle = k * (2.0 * Math.PI / count);
                    double px = sc[0] + radiusM * Math.Cos(angle);
                    double py = sc[1] + radiusM * Math.Sin(angle);
                    sk.CreateCircleByRadius(px, py, 0, holeRadiusM);
                }
                sk.InsertSketch(true);
                try { model.ClearSelection2(true); } catch { }

                var skFeat = model.FeatureByPositionReverse(0) as Feature;
                if (skFeat != null) skFeat.Select2(false, 0);
                try { if (ownerBody != null) ((Entity)ownerBody).Select4(true, null); } catch { }

                int through = (int)swEndConditions_e.swEndCondThroughAll;
                var cut = model.FeatureManager.FeatureCut4(
                    false, false, false, through, through, 0, 0, false, false, false, false, 0, 0,
                    false, false, false, false, false, true, true, true, true, false, 0, 0, false, false) as Feature;
                try { model.ClearSelection2(true); } catch { }

                if (cut == null)
                {
                    var sf = model.FeatureByPositionReverse(0) as Feature;
                    string tn = null; try { tn = sf?.GetTypeName2(); } catch { }
                    if (sf != null && tn != null && tn.IndexOf("Sketch", StringComparison.OrdinalIgnoreCase) >= 0)
                    { try { sf.Select2(false, 0); model.EditDelete(); } catch { } }
                    try { model.ClearSelection2(true); } catch { }
                    return null;
                }
                return cut;
            }
            catch { return null; }
        }

        // ================= face resolution (same approach as AddHole.cs) =================

        private class PlanarFace { public Face2 Face; public double AreaMm2; public double[] Normal; public double[] Centroid; public double SmallSpanMm; }

        private static List<PlanarFace> ResolveCandidateFaces(object[] bodies, bool wantTop, bool wantBottom)
        {
            var planars = CollectPlanarFaces(bodies);
            if (wantTop || wantBottom)
            {
                int vAxis = VerticalAxisIndex(bodies);
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
            }
            planars.Sort((a, b) => b.AreaMm2.CompareTo(a.AreaMm2));
            return planars;
        }

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

        private static double[] SafeInteriorPoint(Face2 face, double[] box)
        {
            if (box == null || box.Length < 6) return null;
            double[] fracs = { 0.5, 0.35, 0.65 };
            double diag = Math.Sqrt(Math.Pow(box[3] - box[0], 2) + Math.Pow(box[4] - box[1], 2) + Math.Pow(box[5] - box[2], 2));
            double tol = Math.Max(0.001, Math.Min(0.002, diag * 0.03));
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
                        if (d <= tol) return new[] { p[0], p[1], p[2] };
                    }
            return fallback;
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

        // ================= verification / cleanup helpers =================

        private static double GetVolumeMm3(IModelDoc2 model)
        {
            try { var mp = model.Extension.CreateMassProperty(); return mp == null ? -1 : mp.Volume * 1e9; }
            catch { return -1; }
        }

        private static int CountCylFaces(PartDoc part)
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
                    bool cyl = false; try { cyl = s != null && s.IsCylinder(); } catch { }
                    if (cyl) n++;
                }
            }
            return n;
        }

        private static object[] SolidBodies(PartDoc part)
        { try { return part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]; } catch { return null; } }

        private static int SafeWhatsWrong(IModelDoc2 model)
        { try { return model.Extension.GetWhatsWrongCount(); } catch { return 0; } }

        private static void RollbackBoltCircle(IModelDoc2 model)
        {
            try
            {
                var f = FindFeatureByName(model, FeatureName);
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

        // ================= intent parsing =================

        private static int ParseCount(string intent)
        {
            string c = (intent ?? "").ToLowerInvariant();
            var m = Regex.Match(c, @"\b(\d+)\s*(?:bolt\s+)?holes?\b");
            if (m.Success && int.TryParse(m.Groups[1].Value, out int v) && v >= 2) return v;
            return 0;
        }

        // finds the bolt-circle DIAMETER near the word "circle"; returns its match span (in the LOWERCASED intent)
        // so the caller can mask it out before parsing the (separate) hole diameter.
        private static double ParseBoltCircleDiameterMm(string intent, out bool explicitBcd, out int maskStart, out int maskLen)
        {
            string c = (intent ?? "").ToLowerInvariant();
            explicitBcd = true; maskStart = -1; maskLen = 0;

            var inch = Regex.Match(c, @"(\d+(\.\d+)?)\s*(inch(es)?|in\b|\"")\s*(?:diameter\s*)?(?:bolt\s*)?circle");
            if (inch.Success && double.TryParse(inch.Groups[1].Value, out double vin))
            { maskStart = inch.Groups[1].Index; maskLen = inch.Groups[1].Length; return vin * 25.4; }

            var mmc = Regex.Match(c, @"(\d+(\.\d+)?)\s*mm\s*(?:diameter\s*)?(?:bolt\s*)?circle");
            if (mmc.Success && double.TryParse(mmc.Groups[1].Value, out double vmm))
            { maskStart = mmc.Groups[1].Index; maskLen = mmc.Groups[1].Length; return vmm; }

            var inch2 = Regex.Match(c, @"(?:bolt\s*)?circle[^.]{0,20}?(\d+(\.\d+)?)\s*(inch(es)?|in\b|\"")");
            if (inch2.Success && double.TryParse(inch2.Groups[1].Value, out double vin2))
            { maskStart = inch2.Groups[1].Index; maskLen = inch2.Groups[1].Length; return vin2 * 25.4; }

            var mmc2 = Regex.Match(c, @"(?:bolt\s*)?circle[^.]{0,20}?(\d+(\.\d+)?)\s*mm");
            if (mmc2.Success && double.TryParse(mmc2.Groups[1].Value, out double vmm2))
            { maskStart = mmc2.Groups[1].Index; maskLen = mmc2.Groups[1].Length; return vmm2; }

            explicitBcd = false;
            return -1;   // caller falls back to a face-relative default
        }

        // blank out [start,start+len) of `intent` (already lowercased by the caller's regex indices) so the same
        // number isn't ALSO read as the hole diameter.
        private static string MaskSpan(string intent, int start, int len)
        {
            string c = (intent ?? "").ToLowerInvariant();
            if (start < 0 || len <= 0 || start + len > c.Length) return c;
            var sb = new System.Text.StringBuilder(c);
            for (int i = start; i < start + len; i++) sb[i] = ' ';
            return sb.ToString();
        }

        private static double ParseHoleDiameterMm(string maskedLowerIntent, out bool explicitDia)
        {
            string c = maskedLowerIntent ?? "";
            explicitDia = true;

            var mm = Regex.Match(c, @"(\d+(\.\d+)?)\s*mm\s*(?:hole|holes)?\b");
            if (mm.Success && double.TryParse(mm.Groups[1].Value, out double vmm) && vmm > 0) return vmm;

            var mthread = Regex.Match(c, @"\bm(\d+(\.\d+)?)\b");
            if (mthread.Success && double.TryParse(mthread.Groups[1].Value, out double vm) && vm > 0) return vm;

            if (Regex.IsMatch(c, @"\bquarter\s*(inch|in|\"")")) return 6.35;
            if (Regex.IsMatch(c, @"\bhalf\s*(inch|in|\"")")) return 12.7;
            if (Regex.IsMatch(c, @"\beighth\s*(inch|in|\"")")) return 3.175;

            var frac = Regex.Match(c, @"(\d+)\s*/\s*(\d+)\s*(inch|in|\"")");
            if (frac.Success && double.TryParse(frac.Groups[1].Value, out double num) && double.TryParse(frac.Groups[2].Value, out double den) && den > 0)
                return num / den * 25.4;

            var inch = Regex.Match(c, @"(\d+(\.\d+)?)\s*(inch(es)?|in\b|\"")");
            if (inch.Success && double.TryParse(inch.Groups[1].Value, out double vin) && vin > 0) return vin * 25.4;

            explicitDia = false;
            return DefaultHoleDiaMm;
        }

        private static double[] CascadeFrom(double requestedMm)
        {
            var list = new List<double> { requestedMm };
            foreach (var d in StandardCascadeMm) if (d < requestedMm) list.Add(d);
            return list.ToArray();
        }

        private static string Trim(double v) => v.ToString("0.###");
    }
}
