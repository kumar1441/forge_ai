using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class AddLoftResult
    {
        public bool Success;
        public int BodyCountBefore;
        public int BodyCountAfter;   // expect +1 (a separate lofted body, Merge=false)
        public double VolumeMm3;     // the lofted body's volume (known truth ~15708 mm3)
        public bool AlreadyDone;
        public string FeatureName;
        public string Diag;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 120 — add_loft. Sketches a 10mm-radius circle on the Front plane and an identical coaxial circle on a plane
    /// offset 50mm from Front, then lofts BETWEEN the two profiles into a SEPARATE solid body
    /// (IFeatureManager.InsertProtrusionBlend2, Merge=false). Loft has the most selection legs (two profile sketches +
    /// an offset ref plane), so the handler INSTRUMENTS the null return + an independent body-count/volume recount and
    /// fails CLOSED on a no-op. Known truth: two equal coaxial circles 50mm apart => a cylinder, solid bodies 1 -> 2,
    /// new-body volume = pi*0.01^2*0.05 = ~15708 mm3. Names it "Forge-Loft"; never saves.
    /// </summary>
    public static class AddLoft
    {
        private const double MM = 0.001;
        private const string LoftName = "Forge-Loft";
        // pi*r^2*h = pi*0.01^2*0.05 m^3, in mm^3 (equal circles loft => a cylinder)
        private static readonly double ExpectedVolMm3 = Math.PI * 0.01 * 0.01 * 0.05 * 1e9;

        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            return Regex.IsMatch(c, @"\b(loft|lofted|blend)\b");
        }

        public static async Task<AddLoftResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new AddLoftResult();
            var part = model as PartDoc;
            if (part == null) { res.Error = "Open a part (.SLDPRT) to add a loft."; return res; }

            var existing = FindFeature(model, LoftName);
            if (existing != null)
            {
                res.AlreadyDone = true; res.Success = true; res.FeatureName = SafeName(existing);
                res.BodyCountAfter = SolidBodyCount(part);
                res.Info = "A lofted feature (" + res.FeatureName + ") is already here — nothing to do.";
                return res;
            }

            await emit("Builder", "lofting between two circular profiles", "run", null);

            res.BodyCountBefore = SolidBodyCount(part);
            double volBefore = TotalSolidVolumeMm3(part);
            Feature feat = null; bool retNull = false;
            try
            {
                var sm = model.SketchManager;

                // profile 1: circle on the Front plane (z=0)
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
                if (plane == null) { CleanupLooseSketch(model); res.Error = "Couldn't create the offset plane for the loft."; return res; }

                // profile 2: identical coaxial circle on the offset plane (z=0.05)
                plane.Select2(false, 0);
                sm.InsertSketch(true);
                sm.CreateCircleByRadius(0, 0, 0, 0.010);
                sm.InsertSketch(true);
                var sketch2 = model.FeatureByPositionReverse(0) as Feature;
                model.ClearSelection2(true);

                // select both profiles in order, then loft (separate body, Merge=false)
                if (sketch1 != null) sketch1.Select2(false, 0);
                if (sketch2 != null) sketch2.Select2(true, 0);
                feat = model.FeatureManager.InsertProtrusionBlend2(
                    false, false, false, 0.1, 0, 0, 0, 0, false, false,
                    false, 0, 0, 0,
                    false, false, false, 0) as Feature;
                retNull = feat == null;
                model.ClearSelection2(true);
                model.ForceRebuild3(false);
            }
            catch (Exception ex) { res.Error = "Loft failed: " + ex.Message; return res; }

            if (feat == null)
            {
                CleanupLooseSketch(model);
                res.Diag = "InsertProtrusionBlend2 returned null (retNull=" + retNull + ")";
                res.Error = "SolidWorks refused the loft (InsertProtrusionBlend2 returned null) — the operation may be dead headless on this build.";
                return res;
            }
            try { feat.Name = LoftName; } catch { }

            res.FeatureName = SafeName(feat);
            res.BodyCountAfter = SolidBodyCount(part);
            double volAfter = TotalSolidVolumeMm3(part);
            res.VolumeMm3 = Math.Round(volAfter - volBefore, 2);
            int rw = 0; try { rw = model.Extension.GetWhatsWrongCount(); } catch { }
            res.Success = res.BodyCountAfter == res.BodyCountBefore + 1
                          && Math.Abs(res.VolumeMm3 - ExpectedVolMm3) < 50.0
                          && rw == 0;
            res.Diag = "bodies " + res.BodyCountBefore + "->" + res.BodyCountAfter + " deltaVolMm3=" + res.VolumeMm3 + " expectedMm3=" + Math.Round(ExpectedVolMm3, 2) + " rebuildErr=" + rw + " name=" + res.FeatureName;

            await emit("Builder", null, "done", res.Success ? "lofted body added" : ("bodies " + res.BodyCountBefore + "->" + res.BodyCountAfter + " vol=" + res.VolumeMm3 + "mm3"));

            res.Info = res.Success
                ? "Lofted between two 10mm circles 50mm apart (" + res.FeatureName + "): " + res.VolumeMm3 + " mm3, bodies " + res.BodyCountBefore + " -> " + res.BodyCountAfter + ". Undo removes it; nothing was saved."
                : "Loft did not verify (bodies " + res.BodyCountBefore + "->" + res.BodyCountAfter + ", volume " + res.VolumeMm3 + "mm3 vs expected " + Math.Round(ExpectedVolMm3, 2) + ") — check the profile selection.";
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
