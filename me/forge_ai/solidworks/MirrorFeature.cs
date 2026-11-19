using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class MirrorFeatureResult
    {
        public string SeedFeature;           // the feature that was mirrored, e.g. "Seed-Hole"
        public string MirrorPlane;           // "Right Plane" | "Front Plane" | "Top Plane"
        public int CylFacesBefore = -1;      // internal cylindrical (bore) faces before, independently measured
        public int CylFacesAfter = -1;       // ... after the mirror + rebuild
        public double VolumeBeforeMm3 = -1;
        public double VolumeAfterMm3 = -1;
        public int RebuildErrors;
        public bool RolledBack;
        public bool Verified;                // fail closed: true ONLY when the mirror feature appeared + rebuild clean + geometry changed
        public bool AlreadyDone;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// MirrorFeature (tool #211 "mirror a feature") — a REAL geometry WRITE on a single PART: it reflects an existing
    /// SEED feature across a standard plane to make a symmetric twin — "mirror the hole about the right plane", "mirror
    /// the cut", "make a mirrored copy of the pocket on the other side".
    ///
    /// Uses IFeatureManager.InsertMirrorFeature2(BMirrorBody=false, ...) — mirror a FEATURE (not a body) — with the
    /// mirror plane + seed feature pre-selected. The exact selection-mark convention for InsertMirrorFeature2 varies by
    /// build, so the handler is fail-safe: it TRIES the documented scheme (plane mark 2, feature mark 1), and if that
    /// returns null it retries the alternate (plane mark 1, feature mark 2) — bounded, deterministic, never a guess left
    /// to chance.
    ///
    /// This is the reflect-counterpart to PatternFeature and shares its spine: seed resolution grounded in the live tree
    /// (most-recent hole/cut/boss, prefers a Forge-tagged one; never the base body), a Forge-MirrorFeat tag for
    /// idempotency (Rule #5), one clean Ctrl+Z (Rule #7), and a FAIL-CLOSED independent verify (Rule #6): after the
    /// rebuild it confirms the mirror feature exists, the rebuild is clean, and the geometry actually changed the way a
    /// real mirror must (a new bore face for a hole seed, or a volume change). Anything less and the feature is DELETED,
    /// the part restored, the failure reported honestly.
    /// </summary>
    public static class MirrorFeature
    {
        private const string MirrorName = "Forge-MirrorFeat";
        private const int DefaultCount = 1;

        private static readonly HashSet<string> Patternable = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "Cut", "CutExtrusion", "Extrusion", "Boss", "BossExtrusion", "Fillet", "Chamfer", "HoleWzd", "HoleSeries",
          "CBORE", "CSK", "Rib", "Dome", "Draft", "ICE", "MacroFeature" };

        public static bool IsMirrorFeatureIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (Regex.IsMatch(c, @"\b(remove|delete|un-?mirror)\b")) return false;
            // a COMPONENT/body mirror in an assembly is the other Mirror handler — never steal "mirror the components/bolts"
            if (Regex.IsMatch(c, @"\b(component|components|bolts?|nuts?|screws?|fasteners?|parts?|assembly|everything|whole)\b")) return false;
            bool verb = Regex.IsMatch(c, @"\bmirror\b");
            bool feat = Regex.IsMatch(c, @"\b(feature|hole|cut|pocket|slot|boss|fillet|chamfer|rib)\b");
            return verb && feat;
        }

        // "left-hand/right-hand version", "mirror image", "flip it", "opposite hand" — asks to reflect the WHOLE
        // part, not one feature. Distinct from IsMirrorFeatureIntent's own vocabulary (feature nouns). Used both as
        // MirrorFeature.Run's own fallback (once ResolveSeed confirms there's no nameable feature) and by Mirror.cs
        // (the component-mirror handler errors flat "Open an assembly" on a PART doc — the cloud sometimes routes
        // this same wording to action=mirror instead of mirror_feature; both must land on the same capability).
        public static bool WantsWholePartMirror(string cmd) =>
            Regex.IsMatch(cmd ?? "", @"\b(left-hand|right-hand|left hand|right hand|mirror image|opposite hand|other hand)\b|\bflip\b", RegexOptions.IgnoreCase);

        // Entry point for an EXTERNAL caller (Mirror.cs) landing on a PART with whole-part-mirror wording — does its
        // own idempotency + doc-type guard rather than assuming Run()'s already covered them.
        public static async Task<MirrorFeatureResult> RunWholePart(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new MirrorFeatureResult();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { res.Error = "Mirroring the whole part works on a single part — open the .SLDPRT."; return res; }
            var part = model as PartDoc;
            if (part == null) { res.Error = "This document has no part geometry to mirror."; return res; }

            if (FindFeatureByName(model, MirrorName) != null)
            {
                res.AlreadyDone = true; res.Verified = true;
                res.Info = "Already mirrored — a Forge-MirrorFeat feature is present, so there's nothing to do.";
                await emit("Mirrorer", null, "done", "Forge-MirrorFeat already present — nothing to do");
                return res;
            }
            return await MirrorWholeBody(model, part, ParsePlane(intent), emit);
        }

        public static async Task<MirrorFeatureResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new MirrorFeatureResult();

            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { res.Error = "Mirroring a feature works on a single part — open the .SLDPRT (component/body mirrors in an assembly are a different command)."; return res; }
            var part = model as PartDoc;
            if (part == null) { res.Error = "This document has no part geometry to mirror."; return res; }

            if (FindFeatureByName(model, MirrorName) != null)
            {
                res.AlreadyDone = true;
                res.Verified = true;
                res.Info = "Already mirrored — a Forge-MirrorFeat feature is present, so there's nothing to do. To mirror " +
                           "differently, delete Forge-MirrorFeat first (Ctrl+Z), then run again.";
                await emit("Mirrorer", null, "done", "Forge-MirrorFeat already present — nothing to do");
                return res;
            }

            res.MirrorPlane = ParsePlane(intent);

            await emit("Gauge", "finding the feature to mirror", "run", null);
            Feature seed = ResolveSeed(model);
            if (seed == null)
            {
                // test-loop wrong-answer fix (mirror-spring, "need a left-hand version of this leaf spring" — no
                // nameable hole/cut/boss to reflect on a curve-only multi-body part): a hand/orientation request has
                // nothing to do with a single feature — it means reflect the WHOLE part. Only take this fallback on
                // wording that actually asks for that (never silently reinterpret a genuine "nothing to mirror" part).
                if (WantsWholePartMirror(intent)) return await MirrorWholeBody(model, part, res.MirrorPlane, emit);
                res.Error = "Nothing to mirror — this part has only its base body, no hole/cut/boss/fillet to reflect. Add one first, or name the feature.";
                return res;
            }
            res.SeedFeature = SafeName(seed);

            res.CylFacesBefore = CountCylFaces(part);
            res.VolumeBeforeMm3 = GetVolumeMm3(model);
            await emit("Gauge", null, "done",
                "seed '" + res.SeedFeature + "' · mirror about the " + res.MirrorPlane + " · " + res.CylFacesBefore + " bore face(s)");

            await emit("Mirrorer", "mirroring '" + res.SeedFeature + "' about the " + res.MirrorPlane, "run", null);

            // try the documented mark scheme, then the alternate if it returns null
            string err = TryMirror(model, seed, res.MirrorPlane, planeMark: 2, featMark: 1);
            if (err != null)
            {
                RollbackMirror(model);
                err = TryMirror(model, seed, res.MirrorPlane, planeMark: 1, featMark: 2);
                if (err != null) { res.Error = err; RollbackMirror(model); await emit("Mirrorer", null, "fail", res.Error); return res; }
            }

            try { model.ForceRebuild3(false); } catch { }
            res.RebuildErrors = SafeWhatsWrong(model);
            res.CylFacesAfter = CountCylFaces(part);
            res.VolumeAfterMm3 = GetVolumeMm3(model);

            await emit("Sentinel", "verifying the mirror post-rebuild", "run", null);
            bool clean = res.RebuildErrors == 0;
            bool tagged = FindFeatureByName(model, MirrorName) != null;
            bool geomChanged = GeometryChanged(res);

            if (!tagged || !clean || !geomChanged)
            {
                RollbackMirror(model);
                res.RolledBack = true;
                res.CylFacesAfter = CountCylFaces(part);
                res.VolumeAfterMm3 = GetVolumeMm3(model);
                res.Error = !clean
                    ? "The mirror rebuilt with " + res.RebuildErrors + " error(s) (the twin likely fell off the part) — rolled it back; the part is unchanged."
                    : (!tagged
                        ? "The mirror could not be confirmed in the tree — rolled it back; the part is unchanged."
                        : "The mirror changed no geometry (the twin landed on top of the seed, or off the body) — rolled it back; the part is unchanged.");
                await emit("Sentinel", null, "fail", "rolled back — part restored");
                return res;
            }

            res.Verified = true;
            await emit("Sentinel", null, "done",
                "mirrored '" + res.SeedFeature + "' about the " + res.MirrorPlane + ", bore faces " +
                res.CylFacesBefore + " → " + res.CylFacesAfter + ", rebuild clean");

            res.Info = BuildInfo(res);
            return res;
        }

        // Whole-part fallback (no nameable feature to reflect — e.g. a curve-only multi-body part like a leaf
        // spring): mirror EVERY solid body in the part together, instead of one feature. The selection-mark scheme
        // for a BODY mirror is undocumented on this build (only the FEATURE scheme is proven, in TryMirror above),
        // so this sweeps mergeSolids true/false and fails closed — never claims success on a null/no-op return.
        private static async Task<MirrorFeatureResult> MirrorWholeBody(IModelDoc2 model, PartDoc part, string plane, Func<string, string, string, string, Task> emit)
        {
            var res = new MirrorFeatureResult { MirrorPlane = plane };
            object[] bodies = SolidBodies(part);
            if (bodies == null || bodies.Length == 0)
            { res.Error = "No solid body to mirror."; return res; }

            int bodyCountBefore = bodies.Length;
            double volBefore = GetVolumeMm3(model);
            res.SeedFeature = "(whole part — " + bodyCountBefore + " solid" + (bodyCountBefore == 1 ? "" : "s") + ")";
            res.CylFacesBefore = CountCylFaces(part);
            res.VolumeBeforeMm3 = volBefore;
            await emit("Gauge", null, "done", "no nameable feature to mirror — reflecting the WHOLE part (" +
                bodyCountBefore + " solid" + (bodyCountBefore == 1 ? "" : "s") + ") about the " + plane + " instead");

            await emit("Mirrorer", "mirroring the whole part about " + plane, "run", null);
            // Selection-mark scheme for a BODY mirror is undocumented on this build. Bounded sweep (bodies-then-plane
            // vs plane-then-bodies selection order, x mergeSolids true/false) — same shape as InsertRib's 4-combo
            // direction sweep. Roll back between attempts so a partial selection never leaks into the next try.
            string err = null;
            foreach (bool planeFirst in new[] { false, true })
                foreach (bool mergeSolids in new[] { false, true })
                {
                    err = TryMirrorBody(model, bodies, plane, mergeSolids, planeFirst);
                    if (err == null) goto attempted;
                    RollbackMirror(model);
                }
            attempted:
            if (err != null)
            {
                res.Error = "Whole-part mirror unavailable on this build (" + err + ") — this part has no reflectable feature and body-level mirroring isn't live here either.";
                RollbackMirror(model);
                await emit("Mirrorer", null, "fail", res.Error);
                return res;
            }

            try { model.ForceRebuild3(false); } catch { }
            res.RebuildErrors = SafeWhatsWrong(model);
            res.CylFacesAfter = CountCylFaces(part);
            res.VolumeAfterMm3 = GetVolumeMm3(model);
            int bodyCountAfter = (SolidBodies(part) ?? new object[0]).Length;

            bool clean = res.RebuildErrors == 0;
            bool tagged = FindFeatureByName(model, MirrorName) != null;
            // A real body mirror either ADDS a twin body (mergeSolids=false) or — if merged into the touching
            // original — roughly doubles the volume. Reject a silent no-op (same count, same volume).
            bool bodiesGrew = bodyCountAfter > bodyCountBefore;
            bool volGrew = volBefore > 0 && res.VolumeAfterMm3 > volBefore * 1.5;
            bool geomChanged = bodiesGrew || volGrew;

            if (!tagged || !clean || !geomChanged)
            {
                RollbackMirror(model);
                res.RolledBack = true;
                res.CylFacesAfter = CountCylFaces(part);
                res.VolumeAfterMm3 = GetVolumeMm3(model);
                res.Error = !clean
                    ? "The whole-part mirror rebuilt with " + res.RebuildErrors + " error(s) — rolled it back; the part is unchanged."
                    : (!tagged
                        ? "The whole-part mirror could not be confirmed in the tree — rolled it back; the part is unchanged."
                        : "The whole-part mirror changed no geometry — rolled it back; the part is unchanged.");
                await emit("Sentinel", null, "fail", "rolled back — part restored");
                return res;
            }

            res.Verified = true;
            res.Info = "Mirrored the whole part (" + bodyCountBefore + " → " + bodyCountAfter + " bodies) about the " +
                       plane + ", rebuild clean. One Ctrl+Z removes it; Forge didn't save.";
            await emit("Sentinel", null, "done", res.Info);
            return res;
        }

        // one whole-body mirror attempt: select bodies + plane (order swept by the caller), InsertMirrorFeature2.
        private static string TryMirrorBody(IModelDoc2 model, object[] bodies, string plane, bool mergeSolids, bool planeFirst)
        {
            try
            {
                try { model.ClearSelection2(true); } catch { }
                bool any = false;
                Action selectBodies = () =>
                {
                    bool first = !any;
                    foreach (var bo in bodies)
                    {
                        var body = bo as Body2; if (body == null) continue;
                        string nm = null; try { nm = body.Name; } catch { }
                        if (string.IsNullOrEmpty(nm)) continue;
                        try { model.Extension.SelectByID2(nm, "SOLIDBODY", 0, 0, 0, !first, 1, null, 0); } catch { }
                        first = false; any = true;
                    }
                };
                if (planeFirst)
                {
                    bool sp = model.Extension.SelectByID2(plane, "PLANE", 0, 0, 0, false, 2, null, 0);
                    if (!sp) return "couldn't select the " + plane;
                    selectBodies();
                }
                else
                {
                    selectBodies();
                    bool sp = model.Extension.SelectByID2(plane, "PLANE", 0, 0, 0, true, 2, null, 0);
                    if (!sp) return "couldn't select the " + plane;
                }
                if (!any) return "couldn't select any solid body";

                var feat = model.FeatureManager.InsertMirrorFeature2(false, false, mergeSolids, false, 0) as Feature;
                try { model.ClearSelection2(true); } catch { }
                if (feat == null) return "InsertMirrorFeature2 returned null (planeFirst=" + planeFirst + ", mergeSolids=" + mergeSolids + ")";
                try { feat.Name = MirrorName; } catch { }
                return null;
            }
            catch (Exception ex) { return ex.GetType().Name + ": " + ex.Message; }
        }

        private static bool GeometryChanged(MirrorFeatureResult r)
        {
            bool bores = r.CylFacesAfter > r.CylFacesBefore;
            bool vol = r.VolumeBeforeMm3 > 0 && r.VolumeAfterMm3 > 0 &&
                       Math.Abs(r.VolumeAfterMm3 - r.VolumeBeforeMm3) > r.VolumeBeforeMm3 * 0.0005;
            return bores || vol;
        }

        private static string BuildInfo(MirrorFeatureResult r)
        {
            string geom = r.CylFacesAfter > r.CylFacesBefore
                ? "bore faces " + r.CylFacesBefore + " → " + r.CylFacesAfter
                : "volume " + r.VolumeBeforeMm3.ToString("N0") + " → " + r.VolumeAfterMm3.ToString("N0") + " mm³";
            return "Mirrored '" + r.SeedFeature + "' about the " + r.MirrorPlane + " (" + geom +
                   ", rebuild clean). One Ctrl+Z removes it; Forge didn't save.";
        }

        // one mirror attempt with a given (plane, feature) selection-mark scheme
        private static string TryMirror(IModelDoc2 model, Feature seed, string plane, int planeMark, int featMark)
        {
            try
            {
                try { model.ClearSelection2(true); } catch { }
                bool selPlane = model.Extension.SelectByID2(plane, "PLANE", 0, 0, 0, false, planeMark, null, 0);
                if (!selPlane) return "Couldn't select the " + plane + " — the part's standard planes may be renamed or missing.";
                if (!seed.Select2(true, featMark)) return "Couldn't select the seed feature '" + SafeName(seed) + "' to mirror.";

                var feat = model.FeatureManager.InsertMirrorFeature2(false, false, false, false, 0) as Feature;
                try { model.ClearSelection2(true); } catch { }
                if (feat == null) return "InsertMirrorFeature2 returned null";   // caller retries the alternate mark scheme
                try { feat.Name = MirrorName; } catch { }
                return null;
            }
            catch (Exception ex) { return "The mirror couldn't be created (" + ex.GetType().Name + ") — the part is unchanged."; }
        }

        // ================= seed resolution (identical policy to PatternFeature) =================

        private static Feature ResolveSeed(IModelDoc2 model)
        {
            Feature forgeTag = null, lastCandidate = null, baseSolid = null;
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    string tn = SafeType(f);
                    string nm = SafeName(f);
                    bool solidish = tn != null && Patternable.Contains(tn);
                    bool nameHint = nm != null && Regex.IsMatch(nm, @"(hole|cut|pocket|slot|boss|fillet|chamfer|rib|seed)", RegexOptions.IgnoreCase);
                    if (solidish || nameHint)
                    {
                        if (baseSolid == null) baseSolid = f;
                        else lastCandidate = f;
                        if (nm != null && nm.StartsWith("Forge-", StringComparison.OrdinalIgnoreCase) &&
                            !nm.Equals(MirrorName, StringComparison.OrdinalIgnoreCase))
                            forgeTag = f;
                    }
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch { }
            return forgeTag ?? lastCandidate;
        }

        // ================= geometry + intent helpers (shared shape with PatternFeature) =================

        // Mirror plane: "right" → Right Plane (default — reflects an X-offset feature to the other side), "front" →
        // Front Plane, "top" → Top Plane.
        private static string ParsePlane(string intent)
        {
            string c = (intent ?? "").ToLowerInvariant();
            if (Regex.IsMatch(c, @"\bfront\b")) return "Front Plane";
            if (Regex.IsMatch(c, @"\btop\b")) return "Top Plane";
            if (Regex.IsMatch(c, @"\bright\b")) return "Right Plane";
            return "Right Plane";
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
        {
            try { var mp = model.Extension.CreateMassProperty(); return mp == null ? -1 : mp.Volume * 1e9; }
            catch { return -1; }
        }

        private static object[] SolidBodies(PartDoc part)
        { try { return part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]; } catch { return null; } }

        private static int SafeWhatsWrong(IModelDoc2 model)
        { try { return model.Extension.GetWhatsWrongCount(); } catch { return 0; } }

        private static void RollbackMirror(IModelDoc2 model)
        {
            try
            {
                var f = FindFeatureByName(model, MirrorName);
                if (f != null)
                {
                    try { model.ClearSelection2(true); } catch { }
                    bool sel = false; try { sel = f.Select2(false, 0); } catch { }
                    if (sel) { try { model.EditDelete(); } catch { } }
                }
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
                    if (string.Equals(SafeName(f), name, StringComparison.OrdinalIgnoreCase)) return f;
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch { }
            return null;
        }

        private static string SafeName(Feature f) { try { return f?.Name; } catch { return null; } }
        private static string SafeType(Feature f) { try { return f?.GetTypeName2(); } catch { return null; } }
    }
}
