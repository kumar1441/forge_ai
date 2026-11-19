using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class CreateCurveResult
    {
        public bool Success;
        public bool AlreadyDone;
        public string FeatureName;
        public string FeatureType;
        public int Points;
        public string Diag;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 214 — create_curve (Curve Through XYZ Points). Threads a 3D curve through a set of absolute space points via
    /// IModelDoc2.InsertCurveFileBegin / InsertCurveFilePoint / InsertCurveFileEnd — the "curve through points" scaffold
    /// used for sweeps/springs/composite paths. It needs NO sketch (the points are absolute), so it's a pure CREATE op
    /// in the LIVE curve family (siblings: InsertHelix/create_helix). A curve adds NO body and changes NO volume, so
    /// verification is by feature EXISTENCE + type + clean rebuild — never volume.
    ///
    /// On this interop InsertCurveFileEnd returns a bool (NOT a Feature) and InsertCurveFileBegin is void, so — exactly
    /// like InsertHelix — the created feature is captured as the new last feature (FeatureByPositionReverse(0)) rather
    /// than from a return handle. The handler INSTRUMENTS the raw bool returns and fails CLOSED on a no-op (the curve
    /// creation may be dead headless like InsertDome/InsertMoveFace — instrument first, park honestly if so). Names the
    /// feature "Forge-Curve" for idempotency; never saves.
    /// </summary>
    public static class CreateCurve
    {
        private const string CurveName = "Forge-Curve";
        // three non-collinear absolute points (metres) — a simple space curve well within any block's neighbourhood
        private static readonly double[][] Pts = new[]
        {
            new[] { 0.0,   0.0,   0.0   },
            new[] { 0.020, 0.020, 0.010 },
            new[] { 0.040, 0.000, 0.020 },
        };

        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            // a helix/spiral is its own handler (tool 213) — exclude it so "curve" doesn't shadow it
            if (Regex.IsMatch(c, @"\b(helix|helical|spiral|coil)\b")) return false;
            bool verb = Regex.IsMatch(c, @"\b(add|create|make|insert|build|draw|thread)\b");
            // this is the curve-FEATURE (Curve Through XYZ Points), NOT a sketch spline: require the word "curve" AND a
            // feature qualifier (through points / 3d / space / guide / xyz). Plain "curve"/"spline" stays with tool 211
            // (AddSketchSpline) — so the two never shadow each other.
            bool curve = Regex.IsMatch(c, @"\bcurve\b");
            bool qualifier = Regex.IsMatch(c, @"(through\s+(the\s+)?points|through\s+xyz|3d\b|space\s+curve|guide\s+curve|xyz)");
            return verb && curve && qualifier;
        }

        public static async Task<CreateCurveResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new CreateCurveResult();
            var part = model as PartDoc;
            if (part == null) { res.Error = "Open a part (.SLDPRT) to add a curve."; return res; }

            var existing = FindFeature(model, CurveName);
            if (existing != null)
            {
                res.AlreadyDone = true; res.Success = true;
                res.FeatureName = SafeName(existing); res.FeatureType = SafeType(existing);
                res.Info = "A curve (" + res.FeatureName + ") is already here — nothing to do.";
                await emit("Builder", null, "done", "Forge-Curve already present — nothing to do");
                return res;
            }

            await emit("Builder", "threading a curve through " + Pts.Length + " points", "run", null);

            string beforeLast = SafeName(model.FeatureByPositionReverse(0) as Feature);
            Feature feat = null;
            bool endRet = false; int ptOk = 0;
            try
            {
                model.ClearSelection2(true);
                model.InsertCurveFileBegin();
                foreach (var p in Pts)
                {
                    bool ok = false;
                    try { ok = model.InsertCurveFilePoint(p[0], p[1], p[2]); } catch { }
                    if (ok) ptOk++;
                }
                try { endRet = model.InsertCurveFileEnd(); } catch { }
                model.ClearSelection2(true);
                model.ForceRebuild3(false);

                // On success a new curve feature is now the last feature (points consume no sketch). A no-op leaves the
                // prior last feature. Accept only a NEW last feature that is not a sketch (curves aren't ProfileFeatures).
                var last = model.FeatureByPositionReverse(0) as Feature;
                string lastName = SafeName(last);
                string lastType = last == null ? null : SafeType(last);
                bool isNew = last != null && !string.Equals(lastName, beforeLast, StringComparison.OrdinalIgnoreCase);
                feat = (isNew && lastType != "ProfileFeature") ? last : null;
            }
            catch (Exception ex) { res.Error = "Curve failed: " + ex.Message; return res; }

            res.Points = ptOk;

            if (feat == null)
            {
                res.Diag = "InsertCurveFile no-op (ptOk=" + ptOk + " endRet=" + endRet + " beforeLast=" + beforeLast + ")";
                res.Error = "SolidWorks refused the curve (no curve feature appeared) — the operation may be dead headless on this build.";
                await emit("Builder", null, "fail", "curve not created");
                return res;
            }
            try { feat.Name = CurveName; } catch { }

            res.FeatureName = SafeName(feat);
            res.FeatureType = SafeType(feat);
            int rw = 0; try { rw = model.Extension.GetWhatsWrongCount(); } catch { }
            bool present = FindFeature(model, CurveName) != null;
            res.Success = present && rw == 0;
            res.Diag = "curve name=" + res.FeatureName + " type=" + res.FeatureType + " ptOk=" + ptOk + " endRet=" + endRet + " rebuildErr=" + rw;

            await emit("Builder", null, "done", res.Success ? "curve added" : ("curve not verified (rebuildErr=" + rw + ")"));

            res.Info = res.Success
                ? "Threaded a curve (" + res.FeatureName + ") through " + ptOk + " points. Undo removes it; nothing was saved."
                : "Curve did not verify (present=" + present + ", rebuildErr=" + rw + ").";
            return res;
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

        private static string SafeName(Feature f) { try { return f == null ? null : f.Name; } catch { return null; } }
        private static string SafeType(Feature f) { try { return f == null ? null : f.GetTypeName2(); } catch { return null; } }
    }
}
