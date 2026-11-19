using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class AddSweepResult
    {
        public bool Success;
        public int BodyCountBefore;
        public int BodyCountAfter;   // expect +1 (a separate swept body, Merge=false)
        public double VolumeMm3;     // the swept body's volume (known truth ~3927 mm3)
        public bool AlreadyDone;
        public string FeatureName;
        public string Diag;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 119 — add_sweep. Sketches a straight 50mm PATH line, then sweeps a 10mm-diameter CIRCULAR profile along it
    /// into a SEPARATE solid body (IFeatureManager.InsertProtrusionSwept4 with CircularProfile=true, Merge=false) — the
    /// circular-profile option means no second profile sketch is needed. Sweep is finicky headless, so the handler
    /// INSTRUMENTS the null return + an independent body-count/volume recount and fails CLOSED on a no-op. Known truth:
    /// solid bodies 1 -> 2, new-body volume = pi*0.005^2*0.05 = ~3927 mm3 (a cylinder). Names it "Forge-Sweep"; never saves.
    /// </summary>
    public static class AddSweep
    {
        private const string SweepName = "Forge-Sweep";
        // pi*r^2*L = pi*0.005^2*0.05 m^3, in mm^3
        private static readonly double ExpectedVolMm3 = Math.PI * 0.005 * 0.005 * 0.05 * 1e9;

        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            return Regex.IsMatch(c, @"\b(sweep|swept)\b");
        }

        public static async Task<AddSweepResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new AddSweepResult();
            var part = model as PartDoc;
            if (part == null) { res.Error = "Open a part (.SLDPRT) to add a sweep."; return res; }

            var existing = FindFeature(model, SweepName);
            if (existing != null)
            {
                res.AlreadyDone = true; res.Success = true; res.FeatureName = SafeName(existing);
                res.BodyCountAfter = SolidBodyCount(part);
                res.Info = "A swept feature (" + res.FeatureName + ") is already here — nothing to do.";
                return res;
            }

            await emit("Builder", "sweeping a circular profile along a path", "run", null);

            res.BodyCountBefore = SolidBodyCount(part);
            double volBefore = TotalSolidVolumeMm3(part);
            Feature feat = null; bool retNull = false;
            try
            {
                SelectPlane(model, "Front Plane");
                var sm = model.SketchManager;
                sm.InsertSketch(true);
                sm.CreateLine(0, 0, 0, 0, 0.05, 0);          // straight 50mm path (open sketch)
                sm.InsertSketch(true);                       // exit the path sketch
                model.ClearSelection2(true);

                var pathFeat = model.FeatureByPositionReverse(0) as Feature;
                if (pathFeat != null) pathFeat.Select2(false, 0);   // select the path; CircularProfile supplies the profile

                // circular-profile sweep (dia 10mm) along the selected path, separate body (Merge=false)
                feat = model.FeatureManager.InsertProtrusionSwept4(
                    false, false, 0, false, false, 0, 0,
                    false, 0, 0, 0, 0,
                    false, false, true,
                    0, false, true, 0.010, 0) as Feature;
                retNull = feat == null;
                model.ClearSelection2(true);
                model.ForceRebuild3(false);
            }
            catch (Exception ex) { res.Error = "Sweep failed: " + ex.Message; return res; }

            if (feat == null)
            {
                CleanupLooseSketch(model);
                res.Diag = "InsertProtrusionSwept4 returned null (retNull=" + retNull + ")";
                res.Error = "SolidWorks refused the sweep (InsertProtrusionSwept4 returned null) — the operation may be dead headless on this build.";
                return res;
            }
            try { feat.Name = SweepName; } catch { }

            res.FeatureName = SafeName(feat);
            res.BodyCountAfter = SolidBodyCount(part);
            double volAfter = TotalSolidVolumeMm3(part);
            res.VolumeMm3 = Math.Round(volAfter - volBefore, 2);
            int rw = 0; try { rw = model.Extension.GetWhatsWrongCount(); } catch { }
            res.Success = res.BodyCountAfter == res.BodyCountBefore + 1
                          && Math.Abs(res.VolumeMm3 - ExpectedVolMm3) < 30.0
                          && rw == 0;
            res.Diag = "bodies " + res.BodyCountBefore + "->" + res.BodyCountAfter + " deltaVolMm3=" + res.VolumeMm3 + " expectedMm3=" + Math.Round(ExpectedVolMm3, 2) + " rebuildErr=" + rw + " name=" + res.FeatureName;

            await emit("Builder", null, "done", res.Success ? "swept body added" : ("bodies " + res.BodyCountBefore + "->" + res.BodyCountAfter + " vol=" + res.VolumeMm3 + "mm3"));

            res.Info = res.Success
                ? "Swept a 10mm circle along a 50mm path (" + res.FeatureName + "): " + res.VolumeMm3 + " mm3 cylinder, bodies " + res.BodyCountBefore + " -> " + res.BodyCountAfter + ". Undo removes it; nothing was saved."
                : "Sweep did not verify (bodies " + res.BodyCountBefore + "->" + res.BodyCountAfter + ", volume " + res.VolumeMm3 + "mm3 vs expected " + Math.Round(ExpectedVolMm3, 2) + ") — check the path selection.";
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
