using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public class CreateCoordSysResult
    {
        public double RequestedXmm, RequestedYmm, RequestedZmm;
        public double MeasuredXmm = double.NaN, MeasuredYmm = double.NaN, MeasuredZmm = double.NaN;
        public int CoordSystemsBefore = -1;
        public int CoordSystemsAfter = -1;
        public string Name;
        public string TypeNameObserved;   // diagnostic: what SW actually calls this feature on this build
        public bool AlreadyExisted;       // idempotent path — a Forge coordinate system was already there, same origin
        public bool RolledBack;
        public bool Verified;
        public string Info;
        public string Error;
        public string Question;           // ONE clarifying question, when the ask is ambiguous (Rule #2)
    }

    /// <summary>
    /// Tool 167 — create_coordinate_system (WRITE). Places a named coordinate system at a numeric offset from the
    /// model origin, so exports and CAM have an explicit output frame instead of "whatever the origin happens to be".
    ///
    /// API route: IFeatureManager.CreateCoordinateSystemUsingNumericalValues — it takes the location directly, so no
    /// selection dance and no dependence on a vertex existing. Verified FAIL-CLOSED through a DIFFERENT API than the
    /// one that wrote it: IModelDocExtension.GetCoordinateSystemTransformByName must return a transform whose
    /// translation IS the requested point (Rule #6 — a non-null return from the create call is not evidence).
    /// A verification miss deletes the feature again; the part is left as it was found. Never saves.
    /// </summary>
    public static class CreateCoordSys
    {
        public const string ForgeName = "Forge-CSYS";
        private const double TolM = 1e-6;   // 0.001 mm

        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            // must be ABOUT a coordinate system...
            if (!Regex.IsMatch(c, @"\b(coordinate system|coord system|coordinate-system|csys|coordinate frame)\b")) return false;
            // ...must not be a read, a delete, or an export that merely NAMES a frame to output in
            if (Regex.IsMatch(c, @"\b(list|show|how many|count|what|which|where|delete|remove|rename|export|save as|dxf|step|iges|parasolid|stl)\b")) return false;
            return Regex.IsMatch(c, @"\b(create|add|make|insert|new|place|define|set up)\b");
        }

        public static async Task<CreateCoordSysResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new CreateCoordSysResult();
            if (model == null) { res.Error = "Open a part or assembly to add a coordinate system."; return res; }

            // ---- parse the location. Never guess a partial one (Rule #2): 1 or 2 numbers is a question, not a guess.
            double[] xyz;
            string parseIssue;
            if (!TryParseLocationMm(intent, out xyz, out parseIssue))
            {
                res.Question = parseIssue;
                res.Error = parseIssue;
                await emit("Gauge", null, "fail", parseIssue);
                return res;
            }
            res.RequestedXmm = xyz[0]; res.RequestedYmm = xyz[1]; res.RequestedZmm = xyz[2];

            // ---- Gauge: what coordinate systems are already here (by BEHAVIOUR — a feature whose name resolves to a
            //      coordinate-system transform — not by a guessed type-name string).
            await emit("Gauge", "checking for existing coordinate systems", "run", null);
            var existing = Census(model);
            res.CoordSystemsBefore = existing.Count;
            await emit("Gauge", null, "done", res.CoordSystemsBefore + " coordinate system" + (res.CoordSystemsBefore == 1 ? "" : "s") + " present");

            // ---- idempotency: Forge's own frame already there?
            if (existing.ContainsKey(ForgeName))
            {
                double[] at = existing[ForgeName];
                if (Close(at[0], xyz[0]) && Close(at[1], xyz[1]) && Close(at[2], xyz[2]))
                {
                    res.AlreadyExisted = true;
                    res.Verified = true;
                    res.Name = ForgeName;
                    res.CoordSystemsAfter = res.CoordSystemsBefore;
                    res.MeasuredXmm = at[0]; res.MeasuredYmm = at[1]; res.MeasuredZmm = at[2];
                    res.TypeNameObserved = TypeNameOf(model, ForgeName);
                    res.Info = ForgeName + " already sits at (" + Fmt(at[0]) + ", " + Fmt(at[1]) + ", " + Fmt(at[2]) +
                               ") mm — nothing to do.";
                    await emit("Scribe", null, "done", "already there — no second coordinate system added");
                    return res;
                }
                res.Question = ForgeName + " already exists at (" + Fmt(at[0]) + ", " + Fmt(at[1]) + ", " + Fmt(at[2]) +
                               ") mm, not (" + Fmt(xyz[0]) + ", " + Fmt(xyz[1]) + ", " + Fmt(xyz[2]) +
                               ") — move that one, or add a second frame?";
                res.Error = res.Question;
                res.CoordSystemsAfter = res.CoordSystemsBefore;
                await emit("Scribe", null, "fail", res.Question);
                return res;
            }

            // ---- WRITE ----
            await emit("Scribe", "placing the coordinate system at (" + Fmt(xyz[0]) + ", " + Fmt(xyz[1]) + ", " + Fmt(xyz[2]) + ") mm", "run", null);
            Feature created = null;
            try
            {
                created = model.FeatureManager.CreateCoordinateSystemUsingNumericalValues(
                    true, xyz[0] / 1000.0, xyz[1] / 1000.0, xyz[2] / 1000.0, false, 0, 0, 0);
            }
            catch (Exception ex)
            {
                res.Error = "Couldn't create the coordinate system (" + ex.GetType().Name + ": " + ex.Message + ") — the part is unchanged.";
                await emit("Scribe", null, "fail", res.Error);
                return res;
            }
            if (created == null)
            {
                res.Error = "SolidWorks returned no feature for the coordinate system — nothing was added.";
                await emit("Scribe", null, "fail", res.Error);
                return res;
            }

            try { res.TypeNameObserved = created.GetTypeName2(); } catch { }
            // Tag it so a rerun, and every other Forge handler, can recognise Forge's own work (Rule #5).
            try { created.Name = ForgeName; } catch { }
            try { res.Name = created.Name; } catch { }
            try { model.ClearSelection2(true); } catch { }
            try { model.ForceRebuild3(false); } catch { }

            // ---- Sentinel: verify through the OTHER API (name → transform), never the create call's return value ----
            await emit("Sentinel", "verifying the frame's origin independently", "run", null);
            var after = Census(model);
            res.CoordSystemsAfter = after.Count;
            int rebuildErr = 0; try { rebuildErr = model.Extension.GetWhatsWrongCount(); } catch { }

            double[] got = null;
            if (res.Name != null && after.ContainsKey(res.Name)) got = after[res.Name];
            if (got != null) { res.MeasuredXmm = got[0]; res.MeasuredYmm = got[1]; res.MeasuredZmm = got[2]; }

            bool located = got != null && Close(got[0], xyz[0]) && Close(got[1], xyz[1]) && Close(got[2], xyz[2]);
            res.Verified = located && res.CoordSystemsAfter == res.CoordSystemsBefore + 1 && rebuildErr == 0;

            if (!res.Verified)
            {
                res.Error = got == null
                    ? "The coordinate system didn't come back when looked up by name — treating it as not created."
                    : (!located
                        ? "The frame landed at (" + Fmt(got[0]) + ", " + Fmt(got[1]) + ", " + Fmt(got[2]) + ") mm, not (" +
                          Fmt(xyz[0]) + ", " + Fmt(xyz[1]) + ", " + Fmt(xyz[2]) + ") mm."
                        : (res.CoordSystemsAfter != res.CoordSystemsBefore + 1
                            ? "The coordinate-system count went " + res.CoordSystemsBefore + " → " + res.CoordSystemsAfter + ", not +1."
                            : "The frame was added but the rebuild reports " + rebuildErr + " error(s)."));
                res.RolledBack = Delete(model, created);
                res.CoordSystemsAfter = Census(model).Count;
                await emit("Sentinel", null, "fail", res.Error + (res.RolledBack ? " Rolled back — part unchanged." : " Roll-back FAILED — check the tree."));
                return res;
            }

            await emit("Sentinel", null, "done", res.Name + " at (" + Fmt(got[0]) + ", " + Fmt(got[1]) + ", " + Fmt(got[2]) + ") mm, rebuild clean");
            res.Info = "Added " + res.Name + " at (" + Fmt(got[0]) + ", " + Fmt(got[1]) + ", " + Fmt(got[2]) + ") mm (" +
                       res.CoordSystemsBefore + " → " + res.CoordSystemsAfter + " coordinate systems). One Ctrl+Z removes it; Forge didn't save.";
            return res;
        }

        // ---- every feature whose NAME resolves to a coordinate-system transform, with its origin in mm ----
        private static Dictionary<string, double[]> Census(IModelDoc2 model)
        {
            var map = new Dictionary<string, double[]>(StringComparer.Ordinal);
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    string nm = null; try { nm = f.Name; } catch { }
                    if (!string.IsNullOrEmpty(nm) && !map.ContainsKey(nm))
                    {
                        double[] o = OriginMm(model, nm);
                        if (o != null) map[nm] = o;
                    }
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch { }
            return map;
        }

        private static double[] OriginMm(IModelDoc2 model, string name)
        {
            try
            {
                var mt = model.Extension.GetCoordinateSystemTransformByName(name) as MathTransform;
                if (mt == null) return null;
                var arr = mt.ArrayData as double[];
                if (arr == null || arr.Length < 12) return null;
                return new double[] { arr[9] * 1000.0, arr[10] * 1000.0, arr[11] * 1000.0 };
            }
            catch { return null; }
        }

        private static string TypeNameOf(IModelDoc2 model, string name)
        {
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    string nm = null; try { nm = f.Name; } catch { }
                    if (nm == name) { try { return f.GetTypeName2(); } catch { return null; } }
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch { }
            return null;
        }

        private static bool Delete(IModelDoc2 model, Feature f)
        {
            try
            {
                model.ClearSelection2(true);
                if (!f.Select2(false, 0)) return false;
                model.EditDelete();
                model.ClearSelection2(true);
                try { model.ForceRebuild3(false); } catch { }
                return true;
            }
            catch { return false; }
        }

        private static bool Close(double a, double b) { return Math.Abs(a - b) <= TolM * 1000.0; }
        private static string Fmt(double v) { return v.ToString("0.###"); }

        // ---- "at 30, 20, 10 mm" / "at x=30 y=20 z=10" / "at the origin" ----
        private static bool TryParseLocationMm(string intent, out double[] xyz, out string issue)
        {
            xyz = null; issue = null;
            string c = (intent ?? "").ToLowerInvariant();

            var lx = Regex.Match(c, @"\bx\s*[=:]\s*(-?\d+(?:\.\d+)?)");
            var ly = Regex.Match(c, @"\by\s*[=:]\s*(-?\d+(?:\.\d+)?)");
            var lz = Regex.Match(c, @"\bz\s*[=:]\s*(-?\d+(?:\.\d+)?)");
            if (lx.Success && ly.Success && lz.Success)
            {
                xyz = new double[] { D(lx), D(ly), D(lz) };
                return true;
            }

            if (Regex.IsMatch(c, @"\b(at|on) the origin\b") || Regex.IsMatch(c, @"\bat the model origin\b"))
            { xyz = new double[] { 0, 0, 0 }; return true; }

            var nums = Regex.Matches(c, @"-?\d+(?:\.\d+)?");
            if (nums.Count == 3)
            {
                xyz = new double[] { double.Parse(nums[0].Value), double.Parse(nums[1].Value), double.Parse(nums[2].Value) };
                return true;
            }
            if (nums.Count == 0) { xyz = new double[] { 0, 0, 0 }; return true; }   // no numbers at all = at the origin

            issue = "Where should the coordinate system go? Give me all three offsets in mm (e.g. \"at 30, 20, 10\") — " +
                    "I only found " + nums.Count + " number" + (nums.Count == 1 ? "" : "s") + " in that.";
            return false;
        }

        private static double D(Match m) { return double.Parse(m.Groups[1].Value); }
    }
}
