using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class AddRevolveResult
    {
        public bool Success;
        public int BodyCountBefore;
        public int BodyCountAfter;   // expect +1 (a separate revolved body, Merge=false)
        public double VolumeMm3;     // the revolved body's volume (known truth ~15708 mm3)
        public bool AlreadyDone;
        public string FeatureName;
        public string Diag;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 118 — add_revolve. Sketches a Y-axis centerline + a closed rectangle offset in +X (radial 20->30mm,
    /// height 10mm) and revolves it 360 deg into a SEPARATE solid body (IFeatureManager.FeatureRevolve2, Merge=false).
    /// FeatureRevolve2 is finicky headless, so the handler INSTRUMENTS the null return + an independent body-count and
    /// volume recount and fails CLOSED on a no-op. Known truth: solid bodies 1 -> 2, new-body volume = pi*(0.03^2-0.02^2)
    /// *0.01 = ~15708 mm3. Names the feature "Forge-Revolve" for idempotency; never saves.
    /// </summary>
    public static class AddRevolve
    {
        private const double MM = 0.001;
        private const string RevolveName = "Forge-Revolve";
        // pi*(Rout^2 - Rin^2)*h = pi*(0.03^2 - 0.02^2)*0.01 m^3, in mm^3
        private static readonly double ExpectedVolMm3 = Math.PI * (0.03 * 0.03 - 0.02 * 0.02) * 0.01 * 1e9;

        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            return Regex.IsMatch(c, @"\b(revolve|revolved|revolution)\b");
        }

        public static async Task<AddRevolveResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new AddRevolveResult();
            var part = model as PartDoc;
            if (part == null) { res.Error = "Open a part (.SLDPRT) to add a revolve."; return res; }

            var existing = FindFeature(model, RevolveName);
            if (existing != null)
            {
                res.AlreadyDone = true; res.Success = true; res.FeatureName = SafeName(existing);
                res.BodyCountAfter = SolidBodyCount(part);
                res.Info = "A revolved feature (" + res.FeatureName + ") is already here — nothing to do.";
                return res;
            }

            await emit("Builder", "revolving a profile 360 degrees", "run", null);

            res.BodyCountBefore = SolidBodyCount(part);
            double volBefore = TotalSolidVolumeMm3(part);
            Feature feat = null;
            bool retNull = false;
            try
            {
                SelectPlane(model, "Front Plane");
                var sm = model.SketchManager;
                sm.InsertSketch(true);
                sm.CreateCenterLine(0, -0.02, 0, 0, 0.02, 0);                 // axis of revolution (Y)
                sm.CreateCornerRectangle(0.02, -0.005, 0, 0.03, 0.005, 0);    // closed profile offset in +X
                sm.InsertSketch(true);                                        // exit the sketch
                model.ClearSelection2(true);

                var skFeat = model.FeatureByPositionReverse(0) as Feature;
                if (skFeat != null) skFeat.Select2(false, 0);

                // full 360 revolve, solid, separate body (Merge=false), auto-select profile + the single centerline axis
                feat = model.FeatureManager.FeatureRevolve2(
                    true, true, false, false, false, false,
                    (int)swEndConditions_e.swEndCondBlind, 0,
                    2 * Math.PI, 0,
                    false, false, 0, 0,
                    0, 0, 0,
                    false, false, true) as Feature;
                retNull = feat == null;
                model.ClearSelection2(true);
                model.ForceRebuild3(false);
            }
            catch (Exception ex) { res.Error = "Revolve failed: " + ex.Message; return res; }

            if (feat == null)
            {
                CleanupLooseSketch(model);
                res.Diag = "FeatureRevolve2 returned null (retNull=" + retNull + ")";
                res.Error = "SolidWorks refused the revolve (FeatureRevolve2 returned null) — the operation may be dead headless on this build.";
                return res;
            }
            try { feat.Name = RevolveName; } catch { }

            res.FeatureName = SafeName(feat);
            res.BodyCountAfter = SolidBodyCount(part);
            double volAfter = TotalSolidVolumeMm3(part);
            res.VolumeMm3 = Math.Round(volAfter - volBefore, 2);   // Merge=false => delta is exactly the new body
            int rw = 0; try { rw = model.Extension.GetWhatsWrongCount(); } catch { }
            res.Success = res.BodyCountAfter == res.BodyCountBefore + 1
                          && Math.Abs(res.VolumeMm3 - ExpectedVolMm3) < 50.0
                          && rw == 0;
            res.Diag = "bodies " + res.BodyCountBefore + "->" + res.BodyCountAfter + " deltaVolMm3=" + res.VolumeMm3 + " expectedMm3=" + Math.Round(ExpectedVolMm3, 2) + " rebuildErr=" + rw + " name=" + res.FeatureName;

            await emit("Builder", null, "done", res.Success ? "revolved body added" : ("bodies " + res.BodyCountBefore + "->" + res.BodyCountAfter + " vol=" + res.VolumeMm3 + "mm3"));

            res.Info = res.Success
                ? "Revolved a rectangle 360 deg into a ring (" + res.FeatureName + "): " + res.VolumeMm3 + " mm3, bodies " + res.BodyCountBefore + " -> " + res.BodyCountAfter + ". Undo removes it; nothing was saved."
                : "Revolve did not verify (bodies " + res.BodyCountBefore + "->" + res.BodyCountAfter + ", volume " + res.VolumeMm3 + "mm3 vs expected " + Math.Round(ExpectedVolMm3, 2) + ") — check the profile/axis selection.";
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
                    if (mp != null && mp.Length >= 4) v += mp[3] * 1e9;   // m^3 -> mm^3
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
