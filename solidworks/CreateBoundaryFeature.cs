using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class CreateBoundaryFeatureResult
    {
        public bool Success;
        public int BodyCountBefore;
        public int BodyCountAfter;   // expect +1 (a separate boundary-boss body, MergeBody=false)
        public double VolumeMm3;     // the new body's volume (known truth ~15708 mm3, same as AddLoft's cylinder)
        public bool AlreadyDone;
        public string FeatureName;
        public string Diag;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 210 — create_boundary_feature (PROBE, not yet proven live). SAME fixture geometry as AddLoft (tool 120,
    /// proven LIVE): a 10mm-radius circle on Front and an identical coaxial circle on a plane offset 50mm, profiles
    /// selected in direction-1 order. Calls IFeatureManager.InsertNetBlend2 (the real API behind the UI's "Boundary
    /// Boss/Base" command — confirmed by reflection, NOT guessed: 21 params, NCurvesDir1/NCurvesDir2 curve-network
    /// counts, WantsSolid/MergeBody/CreateSolid match Boundary's solid-boss mode) with NCurvesDir1=2 (the two
    /// pre-selected profiles), NCurvesDir2=0 (no direction-2 curve network — degenerates to a loft-like single-
    /// direction blend, the simplest possible boundary case). Type=0 (no dedicated enum found for this param in
    /// swconst; only swBoundaryBoss* enums for direction/influence/alignment/tangency exist, none named "type" —
    /// 0 is the untested default, not a guess dressed as fact). INSTRUMENTS the null return + independent
    /// body-count/volume recount and fails CLOSED on a no-op, same discipline as CreateWrap/AddLoft. Names it
    /// "Forge-Boundary"; never saves.
    /// </summary>
    public static class CreateBoundaryFeature
    {
        private const double MM = 0.001;
        private const string BoundaryName = "Forge-Boundary";
        private static readonly double ExpectedVolMm3 = Math.PI * 0.01 * 0.01 * 0.05 * 1e9;

        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            return Regex.IsMatch(c, @"\bboundary\s*(boss|base|feature|blend)?\b");
        }

        public static async Task<CreateBoundaryFeatureResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new CreateBoundaryFeatureResult();
            var part = model as PartDoc;
            if (part == null) { res.Error = "Open a part (.SLDPRT) to add a boundary feature."; return res; }

            var existing = FindFeature(model, BoundaryName);
            if (existing != null)
            {
                res.AlreadyDone = true; res.Success = true; res.FeatureName = SafeName(existing);
                res.BodyCountAfter = SolidBodyCount(part);
                res.Info = "A boundary feature (" + res.FeatureName + ") is already here — nothing to do.";
                return res;
            }

            await emit("Builder", "boundary-blending between two circular profiles", "run", null);

            res.BodyCountBefore = SolidBodyCount(part);
            double volBefore = TotalSolidVolumeMm3(part);
            Feature feat = null; bool retNull = false;
            try
            {
                var sm = model.SketchManager;

                // profile 1: circle on the Front plane (z=0) — identical fixture to AddLoft
                SelectPlane(model, "Front Plane");
                sm.InsertSketch(true);
                sm.CreateCircleByRadius(0, 0, 0, 0.010);
                sm.InsertSketch(true);
                var sketch1 = model.FeatureByPositionReverse(0) as Feature;
                model.ClearSelection2(true);

                // offset reference plane 50mm from Front
                SelectPlane(model, "Front Plane");
                int constraint = (int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_Distance;
                var plane = model.FeatureManager.InsertRefPlane(constraint, 50 * MM, 0, 0, 0, 0) as Feature;
                model.ClearSelection2(true);
                if (plane == null) { CleanupLooseSketch(model); res.Error = "Couldn't create the offset plane for the boundary feature."; return res; }

                // profile 2: identical coaxial circle on the offset plane (z=0.05)
                plane.Select2(false, 0);
                sm.InsertSketch(true);
                sm.CreateCircleByRadius(0, 0, 0, 0.010);
                sm.InsertSketch(true);
                var sketch2 = model.FeatureByPositionReverse(0) as Feature;
                model.ClearSelection2(true);

                // select both profiles as the direction-1 curve network (NCurvesDir1=2), no direction-2 curves
                if (sketch1 != null) sketch1.Select2(false, 0);
                if (sketch2 != null) sketch2.Select2(true, 0);
                feat = model.FeatureManager.InsertNetBlend2(
                    0,      // Type — no dedicated enum found; untested default
                    2,      // NCurvesDir1 — the two pre-selected profiles
                    0,      // NCurvesDir2 — no direction-2 network
                    false,  // HasCenterline
                    0.1,    // TessTolFactor
                    true,   // WantsSolid
                    false,  // MergeBody — separate body, mirrors AddLoft
                    false,  // FeatureScope
                    false,  // AutoSelect — we pre-selected explicitly
                    false, 0, 0, false, 0, false,  // Thin family (unused)
                    false, 0,                       // CapEnds, EndThickness
                    false, 0,                       // AutoFillet, FilletRadius
                    false,  // ForceNonRational
                    true    // CreateSolid
                ) as Feature;
                retNull = feat == null;
                if (feat == null)
                {
                    // InsertNetBlend2 can return null while STILL committing the feature (unlike the fully-dead
                    // InsertCombineFeature/InsertWrapFeature2/InsertMoveCopyBody2 family) — check the top of the
                    // tree for a real new feature before giving up.
                    var top = model.FeatureByPositionReverse(0) as Feature;
                    string topType = null; try { topType = top.GetTypeName2(); } catch { }
                    if (top != null && topType != "ProfileFeature") feat = top;
                }
                model.ClearSelection2(true);
                model.ForceRebuild3(false);
            }
            catch (Exception ex) { res.Error = "Boundary feature failed: " + ex.Message; res.Diag = "exception: " + ex.Message; return res; }

            if (feat == null)
            {
                CleanupLooseSketch(model);
                res.Diag = "InsertNetBlend2 returned null (retNull=" + retNull + ")";
                res.Error = "SolidWorks refused the boundary feature (InsertNetBlend2 returned null) — the operation may be dead headless on this build.";
                return res;
            }
            try { feat.Name = BoundaryName; } catch { }

            res.FeatureName = SafeName(feat);
            res.BodyCountAfter = SolidBodyCount(part);
            double volAfter = TotalSolidVolumeMm3(part);
            res.VolumeMm3 = Math.Round(volAfter - volBefore, 2);
            int rw = 0; try { rw = model.Extension.GetWhatsWrongCount(); } catch { }
            res.Success = res.BodyCountAfter == res.BodyCountBefore + 1
                          && Math.Abs(res.VolumeMm3 - ExpectedVolMm3) < 50.0
                          && rw == 0;
            res.Diag = "bodies " + res.BodyCountBefore + "->" + res.BodyCountAfter + " deltaVolMm3=" + res.VolumeMm3 + " expectedMm3=" + Math.Round(ExpectedVolMm3, 2) + " rebuildErr=" + rw + " name=" + res.FeatureName;

            await emit("Builder", null, "done", res.Success ? "boundary feature body added" : ("bodies " + res.BodyCountBefore + "->" + res.BodyCountAfter + " vol=" + res.VolumeMm3 + "mm3"));

            res.Info = res.Success
                ? "Boundary-blended between two 10mm circles 50mm apart (" + res.FeatureName + "): " + res.VolumeMm3 + " mm3, bodies " + res.BodyCountBefore + " -> " + res.BodyCountAfter + ". Undo removes it; nothing was saved."
                : "Boundary feature did not verify (bodies " + res.BodyCountBefore + "->" + res.BodyCountAfter + ", volume " + res.VolumeMm3 + "mm3 vs expected " + Math.Round(ExpectedVolMm3, 2) + ") — check the profile selection.";
            return res;
        }

        private static int SolidBodyCount(PartDoc part)
        {
            try { var b = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]; return b == null ? 0 : b.Length; }
            catch { return 0; }
        }

        private static double TotalSolidVolumeMm3(PartDoc part)
        {
            double v = 0;
            try
            {
                var bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[];
                if (bodies == null) return 0;
                foreach (var o in bodies)
                {
                    var b = o as Body2; if (b == null) continue;
                    var mp = b.GetMassProperties(0) as double[];
                    if (mp != null && mp.Length >= 4) v += mp[3] * 1e9;
                }
            }
            catch { }
            return v;
        }

        private static Feature FindFeature(IModelDoc2 model, string prefix)
        {
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                string nm = SafeName(f);
                if (nm != null && nm.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return f;
                f = f.GetNextFeature() as Feature;
            }
            return null;
        }

        private static void CleanupLooseSketch(IModelDoc2 model)
        {
            try
            {
                var f = model.FeatureByPositionReverse(0) as Feature;
                string tn = ""; try { tn = f.GetTypeName2(); } catch { }
                if (f != null && tn == "ProfileFeature") { f.Select2(false, 0); model.EditDelete(); }
            }
            catch { }
        }

        private static string SafeName(Feature f) { try { return f.Name; } catch { return null; } }
        private static void SelectPlane(IModelDoc2 model, string plane)
        { try { model.Extension.SelectByID2(plane, "PLANE", 0, 0, 0, false, 0, null, 0); } catch { } }
    }
}
