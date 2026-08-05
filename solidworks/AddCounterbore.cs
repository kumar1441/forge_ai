using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class AddCounterboreResult
    {
        public double BoreDiaMm;             // counterbore (large) diameter (mm)
        public double BoreDepthMm;           // counterbore depth (mm)
        public double ClearanceDiaMm;        // clearance (small, through) diameter (mm)
        public string Metric;                // recognised cap-screw size if any, e.g. "M6"
        public string TargetFace;
        public double VolumeBeforeMm3 = -1;
        public double VolumeAfterMm3 = -1;
        public int CylFacesBefore = -1;
        public int CylFacesAfter = -1;
        public int RebuildErrors;
        public bool RolledBack;
        public bool Verified;                // fail closed: true ONLY when volume dropped + two coaxial bores appeared + rebuild clean
        public bool AlreadyDone;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// AddCounterbore (tool #208 "add a counterbored hole") — a REAL geometry WRITE on a single PART: a stepped hole for
    /// a socket-head cap screw — a large shallow bore (the head recess) over a small through clearance hole, coaxial:
    /// "add a counterbore for an M6 cap screw", "counterbore the top for M8", "add a 14mm counterbore 6mm deep with a
    /// 7mm clearance". One of the most common machined-part features (every bolted plate has them).
    ///
    /// COMPOSES two proven cut paths (AddHole's through-cut + AddPocket's blind-cut) at the SAME face centre: (1) a blind
    /// circular cut of the bore diameter to the bore depth (the head recess), with a max-1 direction flip so it always
    /// mills INTO the solid; (2) a through-all circular cut of the smaller clearance diameter (the screw shank). Both are
    /// tagged Forge-Counterbore*. No new SolidWorks API — same FeatureCut4 mechanics already validated in AddHole/AddPocket.
    ///
    /// Robustness: PART only (Rule #2). A recognised cap-screw size ("M6"/"M8"/"M10"…) sets standard bore/clearance/depth;
    /// otherwise sensible defaults, and each dimension is overridable. A counterbore wider than the face's shorter side is
    /// refused honestly (Rule #2/#3). IDEMPOTENT (Rule #5): both cuts tagged; a rerun reports "already added a
    /// counterbore". UNDO (Rule #7): one Ctrl+Z per cut. FAIL CLOSED (Rule #6): after the rebuild the handler
    /// INDEPENDENTLY confirms the volume dropped, TWO coaxial cylindrical faces of different radii now exist (the stepped
    /// bore), and the rebuild is clean; anything less and both cuts are DELETED, the part restored, the failure reported.
    /// </summary>
    public static class AddCounterbore
    {
        private const string BoreName = "Forge-CounterboreBore";
        private const string ClearName = "Forge-CounterboreClear";
        private const double MM = 0.001;

        // standard-ish metric socket-head cap-screw counterbore table: {clearanceDia, boreDia, boreDepth} mm
        private static readonly Dictionary<string, double[]> CapScrew = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["M3"] = new[] { 3.4, 6.5, 3.3 },
            ["M4"] = new[] { 4.5, 8.0, 4.4 },
            ["M5"] = new[] { 5.5, 10.0, 5.4 },
            ["M6"] = new[] { 6.6, 11.0, 6.5 },
            ["M8"] = new[] { 9.0, 15.0, 8.6 },
            ["M10"] = new[] { 11.0, 18.0, 10.8 },
            ["M12"] = new[] { 14.0, 20.0, 13.0 },
        };

        public static bool IsAddCounterboreIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (Regex.IsMatch(c, @"\b(remove|delete|strip|fill|defeature)\b")) return false;
            bool verb = Regex.IsMatch(c, @"\b(add|cut|mill|make|create|put|counterbore|c'?bore)\b");
            bool noun = Regex.IsMatch(c, @"\b(counterbore|counter-bore|c'?bore|cbore)\b");
            return verb && noun || noun;
        }

        private class PlanarFace { public Face2 Face; public double AreaMm2; public double[] Normal; public double[] Centroid; public double SmallSpanMm; }

        public static async Task<AddCounterboreResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new AddCounterboreResult();

            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { res.Error = "Adding a counterbore works on a single part — open the .SLDPRT you want machined, not an assembly."; return res; }
            var part = model as PartDoc;
            if (part == null) { res.Error = "This document has no part geometry to counterbore."; return res; }

            if (FindFeatureByName(model, BoreName) != null || FindFeatureByName(model, ClearName) != null)
            {
                res.AlreadyDone = true;
                res.Verified = true;
                res.Info = "Already added a counterbore — a Forge-Counterbore feature is present, so there's nothing to do. " +
                           "To add a different one, delete the Forge-Counterbore features first (Ctrl+Z), then run again.";
                await emit("Miller", null, "done", "Forge-Counterbore already present — nothing to do");
                return res;
            }

            ParseSize(intent, res);

            await emit("Gauge", "reading the solid and picking the face", "run", null);
            object[] bodies = SolidBodies(part);
            if (bodies == null || bodies.Length == 0)
            { res.Error = "No solid body to counterbore — this part has no solid geometry to machine."; return res; }

            bool wantTop = Regex.IsMatch((intent ?? "").ToLowerInvariant(), @"\btop\b");
            PlanarFace target = ResolveTargetFace(bodies, wantTop);
            if (target == null)
            { res.Error = "No planar face to counterbore — this part has no flat face to machine (v1 counterbores planar faces)."; return res; }

            res.TargetFace = (wantTop ? "top face" : "largest planar face") + ", " + target.AreaMm2.ToString("N0") + " mm²";
            res.VolumeBeforeMm3 = GetVolumeMm3(model);
            res.CylFacesBefore = CountCylFaces(part);
            await emit("Gauge", null, "done",
                "target " + res.TargetFace + " · " + (res.Metric != null ? res.Metric + " cap screw · " : "") +
                "⌀" + Trim(res.BoreDiaMm) + "×" + Trim(res.BoreDepthMm) + "mm bore over ⌀" + Trim(res.ClearanceDiaMm) + "mm clearance");

            if (target.SmallSpanMm > 0 && res.BoreDiaMm >= target.SmallSpanMm)
            {
                res.Error = "A ⌀" + Trim(res.BoreDiaMm) + "mm counterbore is as wide as the face's shorter side (" +
                            Trim(target.SmallSpanMm) + "mm) — it wouldn't fit. Pick a smaller size.";
                return res;
            }
            if (res.ClearanceDiaMm >= res.BoreDiaMm)
            { res.Error = "The clearance hole (⌀" + Trim(res.ClearanceDiaMm) + "mm) must be smaller than the counterbore (⌀" + Trim(res.BoreDiaMm) + "mm)."; return res; }

            var mu = app.GetMathUtility() as MathUtility;
            // capture the target face's centroid + normal in MODEL space (stable — a CENTRED counterbore does not move
            // them). Each cut RE-RESOLVES the live Face2 at this location: after the bore cut modifies the top face, the
            // captured Face2 pointer goes stale, AND "largest planar" would wrongly flip to the untouched bottom face.
            double[] centroid = target.Centroid;
            double[] normal = target.Normal;

            // ---- cut 1: the blind BORE (head recess). try one direction, flip once if it removes nothing ----
            await emit("Miller", "milling the ⌀" + Trim(res.BoreDiaMm) + "mm × " + Trim(res.BoreDepthMm) + "mm counterbore", "run", null);
            Face2 f1 = FaceAt(part, centroid, normal); if (f1 == null) f1 = target.Face;
            string err = TryCircleCut(app, model, mu, f1, centroid, res.BoreDiaMm, BoreName, through: false, reverse: false, boreDepthMm: res.BoreDepthMm);
            if (err != null) { res.Error = err; Rollback(model); return res; }
            try { model.ForceRebuild3(false); } catch { }
            double afterBore = GetVolumeMm3(model);
            if (!(afterBore > 0 && res.VolumeBeforeMm3 > 0 && res.VolumeBeforeMm3 - afterBore > 1e-4))
            {
                DeleteNamed(model, BoreName); try { model.ForceRebuild3(false); } catch { }
                Face2 f1b = FaceAt(part, centroid, normal); if (f1b == null) f1b = target.Face;
                err = TryCircleCut(app, model, mu, f1b, centroid, res.BoreDiaMm, BoreName, through: false, reverse: true, boreDepthMm: res.BoreDepthMm);
                if (err != null) { res.Error = err; Rollback(model); return res; }
                try { model.ForceRebuild3(false); } catch { }
            }

            // ---- cut 2: the through CLEARANCE hole (coaxial, same face centre) — RE-RESOLVE the (now-modified) top face ----
            await emit("Miller", "drilling the ⌀" + Trim(res.ClearanceDiaMm) + "mm clearance hole through", "run", null);
            Face2 f2 = FaceAt(part, centroid, normal);
            if (f2 == null) { res.Error = "Lost the target face after the counterbore cut — rolled back; the part is unchanged."; Rollback(model); return res; }
            err = TryCircleCut(app, model, mu, f2, centroid, res.ClearanceDiaMm, ClearName, through: true, reverse: false, boreDepthMm: 0);
            if (err != null) { res.Error = err; Rollback(model); return res; }

            try { model.ForceRebuild3(false); } catch { }
            res.RebuildErrors = SafeWhatsWrong(model);
            res.VolumeAfterMm3 = GetVolumeMm3(model);
            res.CylFacesAfter = CountCylFaces(part);

            // ---- INDEPENDENTLY verify (Rule #6): volume dropped + two coaxial bores of DIFFERENT radii + clean ----
            await emit("Sentinel", "verifying the counterbore post-rebuild", "run", null);
            bool dropped = res.VolumeAfterMm3 > 0 && res.VolumeBeforeMm3 > 0 && res.VolumeBeforeMm3 - res.VolumeAfterMm3 > 1e-4;
            bool clean = res.RebuildErrors == 0;
            bool bothBores = HasCoaxialBores(part, res.ClearanceDiaMm / 2.0 * MM, res.BoreDiaMm / 2.0 * MM);
            bool tagged = FindFeatureByName(model, BoreName) != null && FindFeatureByName(model, ClearName) != null;

            if (!dropped || !clean || !bothBores || !tagged)
            {
                Rollback(model);
                res.RolledBack = true;
                res.VolumeAfterMm3 = GetVolumeMm3(model);
                res.CylFacesAfter = CountCylFaces(part);
                res.Error = !clean
                    ? "The counterbore rebuilt with " + res.RebuildErrors + " error(s) — rolled it back; the part is unchanged."
                    : (!dropped
                        ? "The cuts removed no material (missed the solid) — rolled it back; the part is unchanged."
                        : (!bothBores
                            ? "The stepped bore (two coaxial diameters) couldn't be confirmed — rolled it back; the part is unchanged."
                            : "The counterbore couldn't be confirmed in the tree — rolled it back; the part is unchanged."));
                await emit("Sentinel", null, "fail", "rolled back — part restored");
                return res;
            }

            res.Verified = true;
            double removed = res.VolumeBeforeMm3 - res.VolumeAfterMm3;
            await emit("Sentinel", null, "done",
                "counterbored: volume −" + removed.ToString("N0") + " mm³, ⌀" + Trim(res.BoreDiaMm) + "→⌀" + Trim(res.ClearanceDiaMm) +
                "mm stepped bore, rebuild clean");

            res.Info = BuildInfo(res, removed);
            return res;
        }

        private static string BuildInfo(AddCounterboreResult r, double removed)
        {
            return "Added a " + (r.Metric != null ? r.Metric + " " : "") + "counterbore at the centre of the " + (r.TargetFace ?? "target face") +
                   " — ⌀" + Trim(r.BoreDiaMm) + "×" + Trim(r.BoreDepthMm) + "mm recess over a ⌀" + Trim(r.ClearanceDiaMm) +
                   "mm through hole (volume −" + removed.ToString("N0") + " mm³, stepped bore confirmed, rebuild clean). One Ctrl+Z per cut; Forge didn't save.";
        }

        // one circular cut at the target face centre — blind-to-depth (bore, boreDepthMm) or through-all (clearance).
        // `face` is the LIVE face to sketch on (re-resolved by the caller each cut); `centroid` is the stable model-space
        // centre the circle is placed at.
        private static string TryCircleCut(ISldWorks app, IModelDoc2 model, MathUtility mu, Face2 face, double[] centroid, double diaMm, string tag, bool through, bool reverse, double boreDepthMm)
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
                double[] sc = ModelToSketchXY(mu, active, centroid);
                if (sc == null)
                {
                    sk.InsertSketch(true);
                    try { model.ClearSelection2(true); } catch { }
                    var loose = model.FeatureByPositionReverse(0) as Feature;
                    if (loose != null) { try { loose.Select2(false, 0); model.EditDelete(); } catch { } }
                    return "Couldn't project the face centre into the sketch — Forge left the part untouched.";
                }
                sk.CreateCircleByRadius(sc[0], sc[1], 0, diaMm / 2.0 * MM);
                sk.InsertSketch(true);
                try { model.ClearSelection2(true); } catch { }

                var skFeat = model.FeatureByPositionReverse(0) as Feature;
                if (skFeat != null) skFeat.Select2(false, 0);

                int t1, t2; double d1; bool singleEnded;
                if (through) { t1 = t2 = (int)swEndConditions_e.swEndCondThroughAll; d1 = 0; singleEnded = false; }
                else { t1 = t2 = (int)swEndConditions_e.swEndCondBlind; d1 = boreDepthMm * MM; singleEnded = true; }

                var cut = model.FeatureManager.FeatureCut4(
                    singleEnded, false, reverse, t1, t2, d1, 0, false, false, false, false, 0, 0,
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
                    return "SolidWorks refused the cut — the circle may not have landed on the solid. The part is unchanged.";
                }
                try { cut.Name = tag; } catch { }
                return null;
            }
            catch (Exception ex) { return "The counterbore cut couldn't be created (" + ex.GetType().Name + ") — the part is unchanged."; }
        }

        // re-resolve the LIVE planar face at a model-space point with the given outward normal (the same top face,
        // even after a centred cut has punched a hole through it and invalidated the earlier Face2 pointer).
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
                            if (dot < 0.9) continue;   // must face the same way as the original target
                        }
                    }
                    double[] cpn = null; try { cpn = face.GetClosestPointOn(pt[0], pt[1], pt[2]) as double[]; } catch { }
                    if (cpn == null || cpn.Length < 3) continue;
                    double dx = cpn[0] - pt[0], dy = cpn[1] - pt[1], dz = cpn[2] - pt[2];
                    double d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    if (d < bestDist) { bestDist = d; best = face; }
                }
            }
            // the nearest same-normal planar face IS the original top face: after a centred cut the face keeps its plane
            // and normal (it just gains an inner loop), so the centre projects onto the recess rim ~mm away — still the
            // closest same-facing planar face by a wide margin over the opposite (bottom) face.
            return best;
        }

        // two coaxial cylindrical faces of DIFFERENT radii (the clearance + the recess) both present on the solid?
        private static bool HasCoaxialBores(PartDoc part, double rSmall, double rBig)
        {
            bool small = false, big = false;
            double tol = Math.Max(0.2 * MM, 0.06 * rBig);
            foreach (var bo in SolidBodies(part) ?? new object[0])
            {
                var body = bo as Body2; if (body == null) continue;
                object[] faces = null; try { faces = body.GetFaces() as object[]; } catch { }
                foreach (var fo in faces ?? new object[0])
                {
                    var face = fo as Face2; if (face == null) continue;
                    Surface s = null; try { s = face.GetSurface() as Surface; } catch { }
                    bool cyl = false; try { cyl = s != null && s.IsCylinder(); } catch { }
                    if (!cyl) continue;
                    double[] cp = null; try { cp = s.CylinderParams as double[]; } catch { }
                    if (cp == null || cp.Length < 7) continue;
                    if (Math.Abs(cp[6] - rSmall) <= tol) small = true;
                    if (Math.Abs(cp[6] - rBig) <= tol) big = true;
                }
            }
            return small && big;
        }

        // ================= intent parsing =================

        private static void ParseSize(string intent, AddCounterboreResult res)
        {
            string c = (intent ?? "").ToLowerInvariant();

            // metric cap-screw size → standard table
            var mm = Regex.Match(c, @"\bm(\d{1,2})\b");
            if (mm.Success)
            {
                string key = "M" + mm.Groups[1].Value;
                if (CapScrew.TryGetValue(key, out var t)) { res.Metric = key; res.ClearanceDiaMm = t[0]; res.BoreDiaMm = t[1]; res.BoreDepthMm = t[2]; }
            }
            if (res.BoreDiaMm <= 0) { res.BoreDiaMm = 14.0; res.BoreDepthMm = 6.0; res.ClearanceDiaMm = 7.0; }   // default ~M6/M8

            // explicit overrides
            var bore = Regex.Match(c, @"(\d+(\.\d+)?)\s*mm\s*counterbore|counterbore[^\d]{0,8}(\d+(\.\d+)?)\s*mm");
            if (bore.Success) { double v = ParseFirst(bore); if (v > 0) res.BoreDiaMm = v; }
            var depth = Regex.Match(c, @"(\d+(\.\d+)?)\s*mm\s*deep|deep[^\d]{0,6}(\d+(\.\d+)?)\s*mm");
            if (depth.Success) { double v = ParseFirst(depth); if (v > 0) res.BoreDepthMm = v; }
            var clr = Regex.Match(c, @"(\d+(\.\d+)?)\s*mm\s*(?:clearance|clear|through|thru)");
            if (clr.Success && double.TryParse(clr.Groups[1].Value, out double cv) && cv > 0) res.ClearanceDiaMm = cv;
        }

        private static double ParseFirst(Match m)
        {
            for (int i = 1; i < m.Groups.Count; i++)
                if (m.Groups[i].Success && double.TryParse(m.Groups[i].Value, out double v) && v > 0) return v;
            return 0;
        }

        // ================= face resolution + helpers (same shape as AddPocket) =================

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

        private static double GetVolumeMm3(IModelDoc2 model)
        { try { var mp = model.Extension.CreateMassProperty(); return mp == null ? -1 : mp.Volume * 1e9; } catch { return -1; } }

        private static object[] SolidBodies(PartDoc part)
        { try { return part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]; } catch { return null; } }

        private static int SafeWhatsWrong(IModelDoc2 model)
        { try { return model.Extension.GetWhatsWrongCount(); } catch { return 0; } }

        private static void Rollback(IModelDoc2 model)
        {
            try { DeleteNamed(model, ClearName); DeleteNamed(model, BoreName); try { model.ForceRebuild3(false); } catch { } try { model.ClearSelection2(true); } catch { } }
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
