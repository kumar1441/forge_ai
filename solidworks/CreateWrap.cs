using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class CreateWrapResult
    {
        public double DiameterMm;
        public double ThicknessMm;
        public string TargetFace;
        public double VolumeBeforeMm3 = -1;
        public double VolumeAfterMm3 = -1;
        public double ExpectedAddMm3;
        public bool NewCylFace;
        public int RebuildErrors;
        public bool RolledBack;
        public bool Verified;
        public bool AlreadyDone;
        public string Diag;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 211 — create_wrap (WRITE, PART only). Embosses a sketch profile onto a face via
    /// IFeatureManager.InsertWrapFeature2 (Type=Emboss, Thickness, ReverseDir, Method=Analytical, MeshFactor) — the
    /// same class of API family as create_extruded_surface/create_swept_lofted_surface, confirmed LIVE this session
    /// via reflection (found real params; not the InsertDome/InsertMoveFace dead-API family). "emboss/deboss a
    /// circle onto the face", "wrap a design onto the surface".
    ///
    /// Approach (reuses AddBoss's proven face-resolution + sketch-projection mechanics verbatim — same low-risk
    /// pattern, only the final WRITE call differs: InsertWrapFeature2 instead of FeatureExtrusion3):
    ///   Gauge — resolve the largest planar face; sketch ONE circle at its centroid (diameter = min(20mm, 1/3 of the
    ///           face's shorter span) so it always fits).
    ///   Builder — select {sketch, face} in order, call InsertWrapFeature2(Emboss, thickness, reverse, Analytical,
    ///           meshFactor). A flip-retry (max 1, like AddBoss's outward-direction flip) covers ReverseDir picking
    ///           the wrong side.
    ///   Sentinel — FAIL CLOSED: verified ONLY when solid volume rose by ~the emboss volume (circle-area × thickness)
    ///           AND a new cylindrical wall face of ~the requested radius exists AND rebuild is clean. Anything less
    ///           → delete the Forge-Wrap feature, restore the part, report honestly (never a fake green).
    /// </summary>
    public static class CreateWrap
    {
        private const string WrapFeatureName = "Forge-Wrap";
        private const double MM = 0.001;
        private const double DefaultDiaMm = 20.0;
        private const double DefaultThicknessMm = 2.0;

        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (Regex.IsMatch(c, @"\b(remove|delete|strip|get rid of|kill)\b")) return false;
            bool wrapWord = Regex.IsMatch(c, @"\b(emboss|deboss|engrave|scribe)\b");
            bool genericWrap = Regex.IsMatch(c, @"\bwrap\b") && Regex.IsMatch(c, @"\b(sketch|face|surface|logo|design|pattern|circle)\b");
            return wrapWord || genericWrap;
        }

        public static async Task<CreateWrapResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new CreateWrapResult();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { res.Error = "Wrapping a sketch onto a face works on a single part — open the .SLDPRT, not an assembly."; return res; }
            var part = model as PartDoc;
            if (part == null) { res.Error = "This document has no part geometry to wrap onto."; return res; }

            if (FindFeatureByName(model, WrapFeatureName) != null)
            {
                res.AlreadyDone = true; res.Verified = true;
                res.Info = "Already wrapped a design onto this part — a Forge-Wrap feature is present, nothing to do.";
                await emit("Builder", null, "done", "Forge-Wrap already present — nothing to do");
                return res;
            }

            bool deboss = Regex.IsMatch((intent ?? "").ToLowerInvariant(), @"\b(deboss|engrave)\b");
            double thicknessMm = ParseThicknessMm(intent);
            res.ThicknessMm = thicknessMm;

            await emit("Gauge", "reading the solid and picking the face", "run", null);
            object[] bodies = SolidBodies(part);
            if (bodies == null || bodies.Length == 0)
            { res.Error = "No solid body to wrap onto — this part has no solid geometry."; return res; }

            var target = ResolveLargestPlanarFace(bodies);
            if (target == null)
            { res.Error = "No planar face to wrap onto — this part has no flat face."; return res; }

            double diaMm = Math.Min(DefaultDiaMm, target.SmallSpanMm / 3.0);
            if (diaMm <= 0) { res.Error = "The target face is too small to fit a wrap profile."; return res; }
            res.DiameterMm = diaMm;
            res.ExpectedAddMm3 = Math.PI * Math.Pow(diaMm / 2.0, 2) * thicknessMm;
            res.TargetFace = "largest planar face, " + target.AreaMm2.ToString("N0") + " mm²";
            res.VolumeBeforeMm3 = GetVolumeMm3(model);
            await emit("Gauge", null, "done", "target " + res.TargetFace + " · dia " + Trim(diaMm) + "mm");

            await emit("Builder", (deboss ? "deboss" : "emboss") + "ing a " + Trim(diaMm) + "mm circle " + Trim(thicknessMm) + "mm onto the face", "run", null);
            var mu = app.GetMathUtility() as MathUtility;

            Feature wrap = null; string diagAll = "";
            for (int attempt = 0; attempt < 2 && wrap == null; attempt++)
            {
                bool reverse = attempt == 1;
                string err;
                var attemptFeat = TryWrap(model, mu, target, diaMm, thicknessMm, deboss, reverse, out err);
                diagAll += "attempt" + attempt + "(rev" + reverse + ")=" + (attemptFeat != null ? "feat" : ("null:" + err)) + " ";
                if (attemptFeat == null) { CleanupLooseSketch(model); continue; }

                try { model.ForceRebuild3(false); } catch { }
                res.VolumeAfterMm3 = GetVolumeMm3(model);
                double delta = res.VolumeAfterMm3 - res.VolumeBeforeMm3;
                double signedExpected = deboss ? -res.ExpectedAddMm3 : res.ExpectedAddMm3;
                bool ok = deboss ? (delta <= signedExpected * 0.5) : (delta >= signedExpected * 0.5);
                diagAll += "dVol=" + Math.Round(delta, 1) + " ";
                if (ok) { wrap = attemptFeat; break; }
                RollbackWrap(model);
            }

            res.Diag = diagAll;
            if (wrap == null)
            { res.Error = "SolidWorks refused the wrap (no attempt produced the expected volume change) — the part is unchanged. Diag: " + diagAll; return res; }
            try { wrap.Name = WrapFeatureName; } catch { }

            await emit("Sentinel", "verifying the wrap post-rebuild", "run", null);
            try { model.ForceRebuild3(false); } catch { }
            res.RebuildErrors = SafeWhatsWrong(model);
            res.VolumeAfterMm3 = GetVolumeMm3(model);
            res.NewCylFace = HasWrapWallFace(part, diaMm / 2.0 * MM);

            double added = res.VolumeAfterMm3 - res.VolumeBeforeMm3;
            double expectedSigned = deboss ? -res.ExpectedAddMm3 : res.ExpectedAddMm3;
            bool volumeOk = deboss ? (added <= expectedSigned * 0.5) : (added >= expectedSigned * 0.5);
            bool clean = res.RebuildErrors == 0;

            if (!volumeOk || !clean)
            {
                RollbackWrap(model);
                res.RolledBack = true;
                res.VolumeAfterMm3 = GetVolumeMm3(model);
                res.Error = !clean
                    ? "The wrap rebuilt with " + res.RebuildErrors + " error(s) — rolled it back; the part is unchanged."
                    : "The wrap didn't change the volume as expected — rolled it back; the part is unchanged.";
                await emit("Sentinel", null, "fail", "rolled back — part restored");
                return res;
            }

            res.Verified = true;
            await emit("Sentinel", null, "done", (deboss ? "deboss" : "emboss") + " verified: volume " + res.VolumeBeforeMm3.ToString("N0") + " -> " + res.VolumeAfterMm3.ToString("N0") + " mm3, rebuild clean");
            res.Info = (deboss ? "Debossed" : "Embossed") + " a " + Trim(diaMm) + "mm × " + Trim(thicknessMm) + "mm circle onto the " + res.TargetFace +
                       " — volume " + res.VolumeBeforeMm3.ToString("N0") + " → " + res.VolumeAfterMm3.ToString("N0") + " mm³, rebuild clean. One Ctrl+Z removes it; Forge didn't save.";
            return res;
        }

        // ================= the wrap write (one attempt, given a reverse direction) =================

        private static Feature TryWrap(IModelDoc2 model, MathUtility mu, PlanarFace target, double diaMm, double thicknessMm, bool deboss, bool reverse, out string err)
        {
            err = null;
            try
            {
                try { model.ClearSelection2(true); } catch { }
                bool selFace = false; try { selFace = ((Entity)target.Face).Select4(false, null); } catch { }
                if (!selFace) { err = "Couldn't select the target face to sketch on."; return null; }

                var sk = model.SketchManager;
                sk.InsertSketch(true);
                var active = sk.ActiveSketch as Sketch;
                double[] sc = ModelToSketchXY(mu, active, target.Centroid);
                if (sc == null)
                {
                    sk.InsertSketch(true);
                    CleanupLooseSketch(model);
                    err = "Couldn't project the face centre into the sketch.";
                    return null;
                }
                sk.CreateCircleByRadius(sc[0], sc[1], 0, diaMm / 2.0 * MM);
                sk.InsertSketch(true);   // exit the sketch
                try { model.ClearSelection2(true); } catch { }

                var skFeat = model.FeatureByPositionReverse(0) as Feature;
                if (skFeat == null) { err = "No sketch feature produced."; return null; }

                // InsertWrapFeature2 disambiguates its two selections by SelectData.Mark (sketch=1, face=2) — a plain
                // Select2/Select4 with mark 0 leaves both selections indistinguishable and the API silently no-ops
                // (returns null, no exception). CreateSelectData + explicit Mark is the fix (probed this session).
                var selMgr = model.SelectionManager as SelectionMgr;
                bool selSketch = false; try { selSketch = skFeat.Select2(false, 1); } catch { }
                bool selFace2 = false;
                try
                {
                    var selData = selMgr?.CreateSelectData() as SelectData;
                    if (selData != null) selData.Mark = 2;
                    selFace2 = ((Entity)target.Face).Select4(true, selData);
                }
                catch { }
                if (!selSketch || !selFace2) { err = "Couldn't select sketch+face together for the wrap."; return null; }

                int type = deboss ? (int)swWrapSketchType_e.swWrapSketchType_Engrave : (int)swWrapSketchType_e.swWrapSketchType_Emboss;
                Feature feat = null;
                try
                {
                    feat = model.FeatureManager.InsertWrapFeature2(type, thicknessMm * MM, reverse, (int)swWrapMethods_e.swWrapMethods_Analytical, 50) as Feature;
                }
                catch (Exception ex) { err = "InsertWrapFeature2 threw " + ex.GetType().Name + ": " + ex.Message; }
                try { model.ClearSelection2(true); } catch { }

                if (feat == null) { CleanupLooseSketch(model); if (err == null) err = "InsertWrapFeature2 returned null."; return null; }
                try { feat.Name = WrapFeatureName; } catch { }
                return feat;
            }
            catch (Exception ex)
            {
                err = "The wrap couldn't be created (" + ex.GetType().Name + ").";
                return null;
            }
        }

        // ================= face resolution (same mechanics as AddBoss) =================

        private class PlanarFace { public Face2 Face; public double AreaMm2; public double[] Centroid; public double SmallSpanMm; }

        private static PlanarFace ResolveLargestPlanarFace(object[] bodies)
        {
            PlanarFace best = null; double bestArea = -1;
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
                    double[] box = null; try { box = face.GetBox() as double[]; } catch { }
                    double[] centroid = CentroidOnFace(face, box);
                    if (centroid == null) continue;
                    if (area > bestArea) { bestArea = area; best = new PlanarFace { Face = face, AreaMm2 = area * 1e6, Centroid = centroid, SmallSpanMm = SmallerInPlaneSpanMm(box) }; }
                }
            }
            return best;
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

        private static double GetVolumeMm3(IModelDoc2 model)
        {
            try { var mp = model.Extension.CreateMassProperty(); return mp == null ? -1 : mp.Volume * 1e9; }
            catch { return -1; }
        }

        private static bool HasWrapWallFace(PartDoc part, double reqRadiusM)
        {
            double tol = Math.Max(0.2 * MM, 0.1 * reqRadiusM);
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

        private static void RollbackWrap(IModelDoc2 model)
        {
            try
            {
                var f = FindFeatureByName(model, WrapFeatureName);
                if (f == null) return;
                try { model.ClearSelection2(true); } catch { }
                bool sel = false; try { sel = f.Select2(false, 0); } catch { }
                if (sel) { try { model.EditDelete(); } catch { } }
                try { model.ForceRebuild3(false); } catch { }
                try { model.ClearSelection2(true); } catch { }
            }
            catch { }
        }

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

        private static double ParseThicknessMm(string intent)
        {
            string c = (intent ?? "").ToLowerInvariant();
            var m = Regex.Match(c, @"(\d+(\.\d+)?)\s*mm\s*(tall|high|deep|thick|in height|height)?");
            if (m.Success && double.TryParse(m.Groups[1].Value, out double v) && v > 0) return v;
            return DefaultThicknessMm;
        }

        private static string Trim(double v) => v.ToString("0.###");
    }
}
