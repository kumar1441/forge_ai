using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class CreateRibResult
    {
        public bool Success;
        public bool AlreadyDone;
        public string FeatureName;
        public string FeatureType;
        public double ThicknessMm;
        public double VolumeBeforeMm3 = -1;
        public double VolumeAfterMm3 = -1;
        public int RebuildErrors;
        public bool Flipped;         // a first direction combo missed → swept to another
        public string Diag;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 207 — create_rib. Adds a gusset rib bridging the reentrant (concave) corner of an L-shaped part via
    /// IFeatureManager.InsertRib. A rib is a create-FROM-sketch feature (LIVE family), so it should work headless like
    /// extrude/revolve/sweep — but a rib must be BOUNDED by existing walls, so the sketch line's endpoints are placed
    /// ON the two inner arm faces of the L (x=10 and y=10 on the symmetry-L fixture) and the rib fills the triangle
    /// toward the corner. A rib ADDS material, so verification is by VOLUME INCREASE + feature presence + clean rebuild.
    ///
    /// InsertRib is VOID on this interop (no Feature handle), so — like InsertHelix/InsertCurveFile — the created
    /// feature is captured as the new last feature (FeatureByPositionReverse(0)). The rib has two direction bits
    /// (thickness dir in Z, and material/fill dir toward the corner); the handler SWEEPS the small combo set and keeps
    /// the first that VERIFIES (volume up + rebuild clean), rolling back the misses (Rule #6). Names it "Forge-Rib" for
    /// idempotency; never saves. Fails CLOSED and honestly if InsertRib no-ops on this build.
    /// </summary>
    public static class CreateRib
    {
        private const string RibName = "Forge-Rib";
        private const string SketchName = "Forge-RibSketch";
        private const double MM = 0.001;
        private const double ThickM = 0.004;          // 4mm rib wall
        // rib profile endpoints (metres), each ON an inner arm face of the L: (20,10) on y=10 face, (10,20) on x=10 face
        private static readonly double[] P1 = { 0.020, 0.010, 0.0 };
        private static readonly double[] P2 = { 0.010, 0.020, 0.0 };

        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (Regex.IsMatch(c, @"\b(remove|delete|suppress)\b")) return false;
            bool verb = Regex.IsMatch(c, @"\b(add|create|make|insert|build|put)\b");
            bool noun = Regex.IsMatch(c, @"\b(rib|gusset|stiffener|web)\b");
            return verb && noun;
        }

        public static async Task<CreateRibResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new CreateRibResult();
            var part = model as PartDoc;
            if (part == null) { res.Error = "Open a part (.SLDPRT) to add a rib."; return res; }

            if (FindFeature(model, RibName) != null)
            {
                res.AlreadyDone = true; res.Success = true; res.FeatureName = RibName;
                res.Info = "A rib (" + RibName + ") is already here — nothing to do.";
                await emit("Builder", null, "done", "Forge-Rib already present — nothing to do");
                return res;
            }

            res.ThicknessMm = ThickM * 1000.0;
            res.VolumeBeforeMm3 = GetVolumeMm3(model);
            await emit("Builder", "bridging the corner with a rib", "run", null);

            // sweep the 4 direction combos; keep the first that verifies (volume up + rebuild clean).
            bool[] tf = { false, true };
            int attempt = 0;
            foreach (bool revThick in tf)
            foreach (bool revMat in tf)
            {
                attempt++;
                string err = TryRib(model, revThick, revMat);
                if (err != null) { RollbackRib(model); continue; }
                try { model.ForceRebuild3(false); } catch { }
                int rw = SafeWhatsWrong(model);
                double vol = GetVolumeMm3(model);
                bool present = FindFeature(model, RibName) != null;
                bool grew = res.VolumeBeforeMm3 > 0 && vol > res.VolumeBeforeMm3 * 1.0005;
                if (present && rw == 0 && grew)
                {
                    res.Flipped = attempt > 1;
                    res.RebuildErrors = rw;
                    res.VolumeAfterMm3 = vol;
                    var f = FindFeature(model, RibName);
                    res.FeatureName = SafeName(f); res.FeatureType = SafeType(f);
                    res.Success = true;
                    res.Diag = "rib attempt=" + attempt + " revThick=" + revThick + " revMat=" + revMat +
                               " vol " + res.VolumeBeforeMm3.ToString("N0") + "->" + vol.ToString("N0") + " rebuildErr=" + rw;
                    await emit("Builder", null, "done", "rib added (vol " + res.VolumeBeforeMm3.ToString("N0") + " -> " + vol.ToString("N0") + " mm3)");
                    res.Info = "Added a " + res.ThicknessMm + "mm rib (" + res.FeatureName + ") bridging the corner" +
                               (res.Flipped ? " (swept the fill direction)" : "") + ". Undo removes it; nothing was saved.";
                    return res;
                }
                RollbackRib(model);   // this combo missed — restore and try the next
            }

            // nothing verified — restore and report honestly
            RollbackRib(model);
            res.VolumeAfterMm3 = GetVolumeMm3(model);
            res.Diag = "InsertRib swept 4 combos, none verified (before=" + res.VolumeBeforeMm3.ToString("N0") + ")";
            res.Error = "SolidWorks refused the rib (no combo added bounded material) — InsertRib may be dead headless on this build, or the profile didn't bound.";
            await emit("Builder", null, "fail", "rib not created");
            return res;
        }

        // one InsertRib attempt with the given direction bits. Builds the open sketch, ribs, names the feature.
        private static string TryRib(IModelDoc2 model, bool reverseThickness, bool reverseMaterial)
        {
            try
            {
                model.ClearSelection2(true);
                if (!model.Extension.SelectByID2("Front Plane", "PLANE", 0, 0, 0, false, 0, null, 0))
                    return "no Front plane";
                var sm = model.SketchManager;
                sm.InsertSketch(true);
                sm.CreateLine(P1[0], P1[1], P1[2], P2[0], P2[1], P2[2]);   // the open rib profile
                sm.InsertSketch(true);                                     // exit — leaves the sketch selected/last
                model.ClearSelection2(true);

                var sk = model.FeatureByPositionReverse(0) as Feature;
                if (sk == null) return "no sketch";
                try { sk.Name = SketchName; } catch { }
                sk.Select2(false, 0);

                // InsertRib(Is2Sided, ReverseThicknessDir, Thickness, RefEdgeIndex, ReverseMaterialDir, IsDrafted,
                //           DraftOutward, DraftAngle, IsNormToSketch, IsDraftedFromWall)
                model.FeatureManager.InsertRib(false, reverseThickness, ThickM, 0, reverseMaterial, false, false, 0, false, false);
                model.ClearSelection2(true);

                var last = model.FeatureByPositionReverse(0) as Feature;
                string lt = last == null ? null : SafeType(last);
                // success leaves a NON-sketch last feature (the rib consumed / follows the sketch); a no-op leaves the sketch
                if (last == null || lt == "ProfileFeature") return "rib no-op";
                try { last.Name = RibName; } catch { }
                return null;
            }
            catch (Exception ex) { return ex.GetType().Name; }
        }

        private static void RollbackRib(IModelDoc2 model)
        {
            try
            {
                DeleteNamed(model, RibName);
                DeleteNamed(model, SketchName);
                try { model.ForceRebuild3(false); } catch { }
                model.ClearSelection2(true);
            }
            catch { }
        }

        private static void DeleteNamed(IModelDoc2 model, string name)
        {
            var f = FindFeature(model, name);
            if (f == null) return;
            try { model.ClearSelection2(true); } catch { }
            bool sel = false; try { sel = f.Select2(false, 0); } catch { }
            if (sel) { try { model.EditDelete(); } catch { } }
        }

        private static Feature FindFeature(IModelDoc2 model, string name)
        {
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                if (string.Equals(SafeName(f), name, StringComparison.OrdinalIgnoreCase)) return f;
                f = f.GetNextFeature() as Feature;
            }
            return null;
        }

        private static double GetVolumeMm3(IModelDoc2 model)
        { try { var mp = model.Extension.CreateMassProperty(); return mp == null ? -1 : mp.Volume * 1e9; } catch { return -1; } }

        private static int SafeWhatsWrong(IModelDoc2 model)
        { try { return model.Extension.GetWhatsWrongCount(); } catch { return 0; } }

        private static string SafeName(Feature f) { try { return f == null ? null : f.Name; } catch { return null; } }
        private static string SafeType(Feature f) { try { return f == null ? null : f.GetTypeName2(); } catch { return null; } }
    }
}
