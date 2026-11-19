using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class CreateSweptSurfaceResult
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
    /// Tool 223 — create_swept_lofted_surface (WRITE, PROBE resolved LIVE, sweep half). Sweeps an EXPLICIT circular
    /// profile sketch along a fresh 50mm path line into a SURFACE body via IFeatureManager.InsertSweepSurface3 — the
    /// surface-family sibling of the proven solid sweep (tool 119 AddSweep, InsertProtrusionSwept4), and (unlike
    /// FeatureExtruRefSurface3) it returns a real Feature handle directly, no FeatureByPositionReverse capture needed.
    /// PROBE FINDING (measured, not guessed): the CircularProfile-shortcut path (no 2nd sketch) that works for the
    /// SOLID sweep is a SILENT NO-OP for the SURFACE sweep on this build — a throw-4 sweep across
    /// UseAutoSelect/UseFeatScope/pre-selection combinations with CircularProfile=true all returned null. An EXPLICIT
    /// profile sketch (a small circle on a plane perpendicular to the path's start, selected profile-then-path,
    /// CircularProfile=false) is the working recipe — same distinction as tool 120's loft needing two real profile
    /// sketches. Verification is INDEPENDENT: the part's surface-body count (IPartDoc.GetBodies2(swSheetBody) — NOT
    /// swSurfaceBody, the same landmine already hit on get_bodies/list_bodies/create_extruded_surface) must rise by
    /// exactly one, with a clean rebuild. A no-op or thrown call is caught and rolled back, reported honestly (Rule
    /// #6) rather than force-greened. Tagged "Forge-SweptSurface" for idempotency; never saves.
    /// </summary>
    public static class CreateSweptSurface
    {
        private const string SurfName = "Forge-SweptSurface";
        private const string PathSketchName = "Forge-SweptSurfacePath";
        private const string ProfileSketchName = "Forge-SweptSurfaceProfile";
        private const double DiaM = 0.010;   // 10mm circular profile
        private const double PathLenM = 0.050; // 50mm straight path

        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (Regex.IsMatch(c, @"\b(remove|delete|suppress)\b")) return false;
            bool verb = Regex.IsMatch(c, @"\b(add|create|make|insert|build)\b");
            bool noun = Regex.IsMatch(c, @"\bsurface\b") && Regex.IsMatch(c, @"\bsweep|swept\b");
            return verb && noun;
        }

        public static async Task<CreateSweptSurfaceResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new CreateSweptSurfaceResult();
            var part = model as PartDoc;
            if (part == null) { res.Error = "Open a part (.SLDPRT) to add a swept surface."; return res; }

            if (FindFeature(model, SurfName) != null)
            {
                res.AlreadyDone = true; res.Success = true; res.FeatureName = SurfName;
                res.Info = "A swept surface (" + SurfName + ") is already here — nothing to do.";
                await emit("Builder", null, "done", "Forge-SweptSurface already present — nothing to do");
                return res;
            }

            res.SurfaceBodiesBefore = SurfaceBodyCount(part);
            await emit("Builder", "sweeping a circular profile along a path into a surface", "run", null);

            string err = TrySurface(model);
            if (err != null)
            {
                RollbackSurface(model);
                res.Error = "InsertSweepSurface3 didn't create a surface (" + err + ") — may be dead headless on this build.";
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
            await emit("Builder", null, "done", "swept surface added (bodies " + res.SurfaceBodiesBefore + " -> " + after + ")");
            res.Info = "Added a swept surface (" + res.FeatureName + "): " + (DiaM * 1000.0) + "mm circular profile along a " + (PathLenM * 1000.0) + "mm path. One Ctrl+Z undoes it; Forge didn't save.";
            return res;
        }

        // A 50mm path (Y-axis, on Front Plane) + a 10mm-diameter profile circle on Top Plane (perpendicular to the
        // path at its start, y=0) — select profile FIRST, then path (append), CircularProfile=false. The
        // CircularProfile=true shortcut (proven live for the SOLID sweep) is a silent no-op here — see class doc.
        private static string TrySurface(IModelDoc2 model)
        {
            try
            {
                model.ClearSelection2(true);
                if (!model.Extension.SelectByID2("Front Plane", "PLANE", 0, 0, 0, false, 0, null, 0)) return "no Front plane";
                var sm = model.SketchManager;
                sm.InsertSketch(true);
                sm.CreateLine(0, 0, 0, 0, PathLenM, 0);   // straight path (open sketch), floats clear of existing geometry
                sm.InsertSketch(true);
                model.ClearSelection2(true);
                var pathSk = model.FeatureByPositionReverse(0) as Feature;
                if (pathSk == null) return "no path sketch";
                try { pathSk.Name = PathSketchName; } catch { }

                model.Extension.SelectByID2("Top Plane", "PLANE", 0, 0, 0, false, 0, null, 0);
                sm.InsertSketch(true);
                sm.CreateCircleByRadius(0, 0, 0, DiaM / 2.0);
                sm.InsertSketch(true);
                model.ClearSelection2(true);
                var profSk = model.FeatureByPositionReverse(0) as Feature;
                if (profSk == null) return "no profile sketch";
                try { profSk.Name = ProfileSketchName; } catch { }

                profSk.Select2(false, 0);   // profile first
                pathSk.Select2(true, 0);    // then path (append)

                // InsertSweepSurface3(Propagate, TwistCtrlOption, KeepTangency, BAdvancedSmoothing, StartMatchingType,
                //                     EndMatchingType, PathAlign, UseFeatScope, UseAutoSelect, TwistAngle,
                //                     BMergeSmoothFaces, CircularProfile, CircularProfileDiameter, Direction)
                Feature feat = model.FeatureManager.InsertSweepSurface3(
                    false, 0, false, false, 0, 0, 0,
                    false, false, 0, false,
                    false, 0, 0) as Feature;
                model.ClearSelection2(true);

                if (feat == null) return "no-op (InsertSweepSurface3 returned null)";
                try { feat.Name = SurfName; } catch { }
                return null;
            }
            catch (Exception ex) { return ex.GetType().Name + ": " + ex.Message; }
        }

        private static void RollbackSurface(IModelDoc2 model)
        {
            try
            {
                DeleteNamed(model, SurfName);
                DeleteNamed(model, PathSketchName);
                DeleteNamed(model, ProfileSketchName);
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
