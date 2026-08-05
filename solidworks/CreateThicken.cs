using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class CreateThickenResult
    {
        public bool Success;
        public bool AlreadyDone;
        public string FeatureName;
        public int SolidBodiesBefore = -1;
        public int SolidBodiesAfter = -1;
        public double VolumeMm3;         // expected 30mm*20mm*5mm = 3000 mm3
        public int RebuildErrors;
        public bool RolledBack;
        public string Diag;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 209 — create_thicken (WRITE, PROBE, not yet proven live). Reuses CreateExtrudedSurface's proven fixture
    /// (30x20mm rectangle on Top plane extruded to a SURFACE via FeatureExtruRefSurface3, confirmed LIVE) then calls
    /// IFeatureManager.FeatureBossThicken(Thickness, Direction, FaceIndex, FillVolume, Merge, UseFeatScope,
    /// UseAutoSelect) on the surface's single face — real signature confirmed by reflection (7 params, no dedicated
    /// enum for Direction/FaceIndex found in swconst). Thicken sits ambiguously between the LIVE create-from-sketch
    /// family (its INPUT is a freshly-created surface, same pattern as loft/sweep needing profile sketches) and the
    /// DEAD modify-existing-geometry family (it consumes/converts an EXISTING body rather than sketch entities
    /// directly) — the create_boundary_feature probe (tool 210) already overturned one "likely dead" prediction in
    /// this same bucket, so this is instrumented rather than assumed either way. Known truth if live: a NEW solid
    /// body (Merge=false), volume = 30*20*5 = 3000 mm3. Names it "Forge-Thicken"; never saves.
    /// </summary>
    public static class CreateThicken
    {
        private const double MM = 0.001;
        private const double DepthM = 0.020;      // surface: 20mm (matches CreateExtrudedSurface's proven depth)
        private const double ThicknessM = 0.005;  // thicken: 5mm
        private const string SurfName = "Forge-ThickenSurface";
        private const string SketchName = "Forge-ThickenSketch";
        private const string ThickenName = "Forge-Thicken";
        private static readonly double ExpectedVolMm3 = 30.0 * 20.0 * 5.0; // 3000 mm3

        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            // narrow: "thicken" + "surface" — the surface-to-solid feature, distinct from WallThickness's
            // "thicken the wall to Xmm" write-chain phrasing (which never says "surface").
            return Regex.IsMatch(c, @"\bthicken\b") && Regex.IsMatch(c, @"\bsurface\b");
        }

        public static async Task<CreateThickenResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new CreateThickenResult();
            var part = model as PartDoc;
            if (part == null) { res.Error = "Open a part (.SLDPRT) to thicken a surface."; return res; }

            if (FindFeature(model, ThickenName) != null)
            {
                res.AlreadyDone = true; res.Success = true; res.FeatureName = ThickenName;
                res.SolidBodiesAfter = SolidBodyCount(part);
                res.Info = "A thickened solid (" + ThickenName + ") is already here — nothing to do.";
                await emit("Builder", null, "done", "Forge-Thicken already present — nothing to do");
                return res;
            }

            await emit("Builder", "creating a surface, then thickening it into a solid", "run", null);

            res.SolidBodiesBefore = SolidBodyCount(part);
            double volBefore = TotalSolidVolumeMm3(part);

            string err = TryMakeSurface(model);
            if (err != null)
            {
                RollbackNamed(model, SurfName); RollbackNamed(model, SketchName);
                res.Error = "Couldn't build the source surface (" + err + ") — thicken has nothing to work on.";
                await emit("Builder", null, "fail", res.Error);
                return res;
            }

            Feature feat = null; bool retNull = false;
            try
            {
                var surfBody = FirstSurfaceBody(part);
                if (surfBody == null) { res.Error = "Surface created but no sheet body found to select."; RollbackAll(model); return res; }
                var face = surfBody.GetFirstFace() as Face2;
                if (face == null) { res.Error = "Surface body has no face to thicken."; RollbackAll(model); return res; }
                model.ClearSelection2(true);
                bool sel = false; try { sel = ((Entity)face).Select4(false, null); } catch { }
                if (!sel) { res.Error = "Couldn't select the surface face for thicken."; RollbackAll(model); return res; }

                feat = model.FeatureManager.FeatureBossThicken(
                    ThicknessM, 0 /*Direction*/, 0 /*FaceIndex*/, false /*FillVolume*/,
                    false /*Merge — separate body*/, false /*UseFeatScope*/, false /*UseAutoSelect*/) as Feature;
                retNull = feat == null;
                if (feat == null)
                {
                    // same InsertNetBlend2-style trap: a null Feature return doesn't always mean nothing happened —
                    // check the top of the tree for a real new feature before giving up.
                    var top = model.FeatureByPositionReverse(0) as Feature;
                    string topType = null; try { topType = top.GetTypeName2(); } catch { }
                    if (top != null && topType != "ProfileFeature" && topType != "RefSurface" && SafeName(top) != SurfName)
                        feat = top;
                }
                model.ClearSelection2(true);
                model.ForceRebuild3(false);
            }
            catch (Exception ex) { res.Error = "Thicken failed: " + ex.Message; res.Diag = "exception: " + ex.Message; RollbackAll(model); return res; }

            if (feat == null)
            {
                RollbackAll(model);
                res.Diag = "FeatureBossThicken returned null (retNull=" + retNull + ")";
                res.Error = "SolidWorks refused the thicken (FeatureBossThicken returned null) — the operation may be dead headless on this build.";
                await emit("Builder", null, "fail", res.Error);
                return res;
            }
            try { feat.Name = ThickenName; } catch { }

            res.SolidBodiesAfter = SolidBodyCount(part);
            double volAfter = TotalSolidVolumeMm3(part);
            res.VolumeMm3 = Math.Round(volAfter - volBefore, 2);
            int rw = 0; try { rw = model.Extension.GetWhatsWrongCount(); } catch { }
            res.FeatureName = SafeName(feat);
            res.Success = res.SolidBodiesAfter == res.SolidBodiesBefore + 1
                          && Math.Abs(res.VolumeMm3 - ExpectedVolMm3) < 50.0
                          && rw == 0;
            res.RebuildErrors = rw;
            res.Diag = "bodies " + res.SolidBodiesBefore + "->" + res.SolidBodiesAfter + " deltaVolMm3=" + res.VolumeMm3 + " expectedMm3=" + ExpectedVolMm3 + " rebuildErr=" + rw + " name=" + res.FeatureName;

            if (!res.Success)
            {
                RollbackAll(model);
                res.RolledBack = true;
                await emit("Builder", null, "fail", res.Diag);
                return res;
            }

            await emit("Builder", null, "done", "thickened surface into a solid (" + res.VolumeMm3 + " mm3)");
            res.Info = "Thickened the surface into a solid (" + res.FeatureName + "): " + res.VolumeMm3 + " mm3, bodies " + res.SolidBodiesBefore + " -> " + res.SolidBodiesAfter + ". Undo removes it; nothing was saved.";
            return res;
        }

        private static string TryMakeSurface(IModelDoc2 model)
        {
            try
            {
                model.ClearSelection2(true);
                if (!model.Extension.SelectByID2("Top Plane", "PLANE", 0, 0, 0, false, 0, null, 0))
                    return "no Top plane";
                var sm = model.SketchManager;
                sm.InsertSketch(true);
                sm.CreateCornerRectangle(-0.015, -0.010, 0, 0.015, 0.010, 0); // 30x20mm
                sm.InsertSketch(true);
                model.ClearSelection2(true);

                var sk = model.FeatureByPositionReverse(0) as Feature;
                if (sk == null) return "no sketch";
                try { sk.Name = SketchName; } catch { }
                sk.Select2(false, 0);

                model.FeatureManager.FeatureExtruRefSurface3(
                    true, false, 0, 0, 0, 0, DepthM, 0, false, false, false, false, 0, 0,
                    false, false, false, false, false, false, false, false);
                model.ClearSelection2(true);

                var last = model.FeatureByPositionReverse(0) as Feature;
                string lt = last == null ? null : SafeType(last);
                if (last == null || lt == "ProfileFeature") return "no-op (sketch still last feature)";
                try { last.Name = SurfName; } catch { }
                return null;
            }
            catch (Exception ex) { return ex.GetType().Name + ": " + ex.Message; }
        }

        private static Body2 FirstSurfaceBody(PartDoc part)
        {
            try
            {
                var b = part.GetBodies2((int)swBodyType_e.swSheetBody, false) as object[];
                if (b == null || b.Length == 0) return null;
                return b[0] as Body2;
            }
            catch { return null; }
        }

        private static void RollbackAll(IModelDoc2 model)
        {
            RollbackNamed(model, ThickenName);
            RollbackNamed(model, SurfName);
            RollbackNamed(model, SketchName);
            try { model.ForceRebuild3(false); } catch { }
            try { model.ClearSelection2(true); } catch { }
        }

        private static void RollbackNamed(IModelDoc2 model, string name)
        {
            var f = FindFeature(model, name);
            if (f == null) return;
            try { model.ClearSelection2(true); } catch { }
            bool sel = false; try { sel = f.Select2(false, 0); } catch { }
            if (sel) { try { model.EditDelete(); } catch { } }
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

        private static string SafeName(Feature f) { try { return f == null ? null : f.Name; } catch { return null; } }
        private static string SafeType(Feature f) { try { return f == null ? null : f.GetTypeName2(); } catch { return null; } }
    }
}
