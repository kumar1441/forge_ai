using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class CreateExtrudedSurfaceResult
    {
        public bool Success;
        public bool AlreadyDone;
        public string FeatureName;
        public string FeatureType;
        public int SurfaceBodiesBefore = -1;
        public int SurfaceBodiesAfter = -1;
        public int RebuildErrors;
        public bool RolledBack;
        public string Diag;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 222 — create_extruded_surface (WRITE, PROBE). Extrudes a fresh rectangle sketch into a SURFACE body via
    /// IFeatureManager.FeatureExtruRefSurface3 — a create-FROM-sketch operation (the LIVE family, same shape as
    /// extrude/revolve/sweep/loft/rib), distinct from the DEAD modify-existing-solid family. FeatureExtruRefSurface3 is
    /// VOID on this interop (no Feature handle), so — like InsertRib/InsertHelix/InsertCurveFile — the created feature
    /// is captured as the new last feature (FeatureByPositionReverse(0)). Verification is INDEPENDENT: the part's
    /// surface-body count (IPartDoc.GetBodies2(swSheetBody) — NOT swSurfaceBody, the same landmine get_bodies/list_bodies
    /// already hit on this build) must rise by exactly one, with a clean rebuild. A no-op or thrown call is caught and
    /// rolled back, reported honestly (Rule #6) rather than force-greened. Tagged "Forge-ExtrudeSurface" for
    /// idempotency; never saves.
    /// </summary>
    public static class CreateExtrudedSurface
    {
        private const string SurfName = "Forge-ExtrudeSurface";
        private const string SketchName = "Forge-ExtrudeSurfaceSketch";
        private const double MM = 0.001;
        private const double DepthM = 0.020;   // 20mm

        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (Regex.IsMatch(c, @"\b(remove|delete|suppress)\b")) return false;
            bool verb = Regex.IsMatch(c, @"\b(add|create|make|insert|build)\b");
            bool noun = Regex.IsMatch(c, @"\bsurface\b") && Regex.IsMatch(c, @"\bextrud");
            return verb && noun;
        }

        public static async Task<CreateExtrudedSurfaceResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new CreateExtrudedSurfaceResult();
            var part = model as PartDoc;
            if (part == null) { res.Error = "Open a part (.SLDPRT) to add an extruded surface."; return res; }

            if (FindFeature(model, SurfName) != null)
            {
                res.AlreadyDone = true; res.Success = true; res.FeatureName = SurfName;
                res.Info = "An extruded surface (" + SurfName + ") is already here — nothing to do.";
                await emit("Builder", null, "done", "Forge-ExtrudeSurface already present — nothing to do");
                return res;
            }

            res.SurfaceBodiesBefore = SurfaceBodyCount(part);
            await emit("Builder", "sketching + extruding a surface", "run", null);

            string err = TrySurface(model);
            if (err != null)
            {
                RollbackSurface(model);
                res.Error = "FeatureExtruRefSurface3 didn't create a surface (" + err + ") — may be dead headless on this build.";
                await emit("Builder", null, "fail", res.Error);
                return res;
            }
            try { model.ForceRebuild3(false); } catch { }

            int rw = SafeWhatsWrong(model);
            int after = SurfaceBodyCount(part);
            bool grew = res.SurfaceBodiesBefore >= 0 && after == res.SurfaceBodiesBefore + 1;
            var f = FindFeature(model, SurfName);

            if (f == null || rw != 0 || !grew)
            {
                RollbackSurface(model);
                res.RolledBack = true;
                res.SurfaceBodiesAfter = SurfaceBodyCount(part);
                res.Diag = "before=" + res.SurfaceBodiesBefore + " after=" + after + " rebuildErr=" + rw + " featurePresent=" + (f != null);
                res.Error = "Surface didn't verify (surface-body count " + res.SurfaceBodiesBefore + " -> " + after + ", rebuildErr=" + rw + ") — rolled back.";
                await emit("Builder", null, "fail", res.Error);
                return res;
            }

            res.SurfaceBodiesAfter = after;
            res.RebuildErrors = rw;
            res.FeatureName = SafeName(f); res.FeatureType = SafeType(f);
            res.Success = true;
            await emit("Builder", null, "done", "surface added (bodies " + res.SurfaceBodiesBefore + " -> " + after + ")");
            res.Info = "Added an extruded surface (" + res.FeatureName + "), " + (DepthM * 1000.0) + "mm deep. One Ctrl+Z undoes it; Forge didn't save.";
            return res;
        }

        private static string TrySurface(IModelDoc2 model)
        {
            try
            {
                model.ClearSelection2(true);
                if (!model.Extension.SelectByID2("Top Plane", "PLANE", 0, 0, 0, false, 0, null, 0))
                    return "no Top plane";
                var sm = model.SketchManager;
                sm.InsertSketch(true);
                sm.CreateCornerRectangle(-0.015, -0.010, 0, 0.015, 0.010, 0);   // 30x20mm, floats clear of existing geometry
                sm.InsertSketch(true);                                          // exit — leaves the sketch selected/last
                model.ClearSelection2(true);

                var sk = model.FeatureByPositionReverse(0) as Feature;
                if (sk == null) return "no sketch";
                try { sk.Name = SketchName; } catch { }
                sk.Select2(false, 0);

                // FeatureExtruRefSurface3(Sd, Dir, StartCond, OffsetVal, T1, T2, D1, D2, Dchk1, Dchk2, Ddir1, Ddir2,
                //                          Dang1, Dang2, OffsetReverse1, OffsetReverse2, TranslateSurface1,
                //                          TranslateSurface2, CapEnd1, CapEnd2, DeleteOriginalFace, KnitResult)
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

        private static void RollbackSurface(IModelDoc2 model)
        {
            try
            {
                DeleteNamed(model, SurfName);
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

        private static int SurfaceBodyCount(PartDoc part)
        {
            try { var b = part.GetBodies2((int)swBodyType_e.swSheetBody, false) as object[]; return b?.Length ?? 0; }
            catch { return 0; }
        }

        private static int SafeWhatsWrong(IModelDoc2 model)
        { try { return model.Extension.GetWhatsWrongCount(); } catch { return 0; } }

        private static string SafeName(Feature f) { try { return f == null ? null : f.Name; } catch { return null; } }
        private static string SafeType(Feature f) { try { return f == null ? null : f.GetTypeName2(); } catch { return null; } }
    }
}
