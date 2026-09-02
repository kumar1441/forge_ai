using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class CreateTorusResult
    {
        public bool Created;
        public double MajorRadiusMm;    // major radius R parsed from the intent; default 40mm
        public double MinorRadiusMm;    // minor (tube) radius r parsed from the intent; default 10mm
        public double VolumeMm3 = -1;   // post-rebuild, independently measured
        public bool RebuildClean;
        public bool Verified;           // fail closed: volume ≈ 2π²Rr² AND the rebuild is clean
        public string Info;
        public string Error;
    }

    /// <summary>
    /// CreateTorus — tool: create a SOLID RING TORUS part FROM SCRATCH (blank part + a small circle profile on the
    /// Front plane revolved 360° around an axis offset from the circle's centre). The "create a torus/donut" basic-solid
    /// primitive. Sketch construction: a centreline along the Y axis through the origin (the revolve axis) + a circle of
    /// minor radius r whose centre sits at distance R (major radius) from that axis. Revolving the full circle 360°
    /// sweeps a ring torus of major radius R and tube radius r. Mirrors the PROVEN CreateSphere revolve spine (centreline
    /// + profile + the identical FeatureRevolve2 call). Volume ≈ 2π²Rr². Intent carries R/r in mm: "torus 40mm major
    /// radius 10mm minor", "make a donut 40 and 10 mm" reads major × minor. Defaults R=40mm, r=10mm. Never saves.
    /// </summary>
    public static class CreateTorus
    {
        private const double MM = 0.001;              // mm -> SW metres
        private const double DefaultMajorMm = 40.0;   // sensible default when no size is stated
        private const double DefaultMinorMm = 10.0;   // sensible default when no tube size is stated

        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            bool obj = Regex.IsMatch(c, @"\b(torus|donut|doughnut|ring torus)\b");
            bool verb = Regex.IsMatch(c, @"\b(create|make|build|add|new|start)\b");
            return obj && verb;
        }

        public static async Task<CreateTorusResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new CreateTorusResult();

            string cue = CreateGuardrail.UnsupportedCue(intent);
            if (cue != null) { res.Error = "I can only create a plain torus from scratch — \"" + cue + "\" (compound/positional/feature requests) isn't supported yet. Ask for a bare shape like \"create a 50mm torus\"."; return res; }

            double RMm = DefaultMajorMm, rMm = DefaultMinorMm;
            ParseDims(intent ?? "", ref RMm, ref rMm);
            res.MajorRadiusMm = RMm;
            res.MinorRadiusMm = rMm;
            double expectedMm3 = 2 * Math.PI * Math.PI * RMm * rMm * rMm;

            await emit("Builder", "creating a torus " + Trim(RMm) + " mm major × " + Trim(rMm) + " mm tube", "run", null);
            try
            {
                // clean slate: close any open doc so the new one is active without lock issues (CreatePlate spine)
                try { var cur = app.ActiveDoc as IModelDoc2; if (cur != null) app.CloseDoc(cur.GetTitle()); } catch { }
                string template = PartTemplate(app);
                if (template == null) { res.Error = "Couldn't find a part template on this install."; return res; }
                var part = app.NewDocument(template, 0, 0, 0) as IModelDoc2;
                if (part == null) { res.Error = "NewDocument returned nothing — the part template may be invalid."; return res; }

                double R = RMm * MM, rr = rMm * MM;
                bool sel = false;
                try { sel = part.Extension.SelectByID2("Front Plane", "PLANE", 0, 0, 0, false, 0, null, 0); } catch { }
                if (!sel) { res.Error = "could not select the Front Plane"; return res; }

                // profile: a vertical centreline through the origin (the revolve axis) + a minor-radius circle whose
                // centre sits at (R, 0) — distance R from the axis. The circle never touches the axis (R > r), so a
                // 360° revolve of the FULL circle sweeps a closed ring torus centred on the origin.
                var sm = part.SketchManager;
                sm.InsertSketch(true);
                double axisHalf = 2 * (R + rr);                 // axis far longer than the tube, for visual clarity only
                sm.CreateCenterLine(0, -axisHalf, 0, 0, axisHalf, 0);   // axis of revolution (Y)
                sm.CreateCircleByRadius(R, 0, 0, rr);           // tube cross-section, centre offset by the major radius
                sm.InsertSketch(true);                          // exit the sketch
                part.ClearSelection2(true);

                var skFeat = part.FeatureByPositionReverse(0) as Feature;
                if (skFeat != null) skFeat.Select2(false, 0);

                // full 360° revolve of the circle around the centreline -> a torus. The exact proven call shape from
                // CreateSphere/AddRevolve (auto-select profile + the single centreline axis).
                Feature feat = null;
                try
                {
                    feat = part.FeatureManager.FeatureRevolve2(
                        true, true, false, false, false, false,
                        (int)swEndConditions_e.swEndCondBlind, 0,
                        2 * Math.PI, 0,
                        false, false, 0, 0,
                        0, 0, 0,
                        false, false, true) as Feature;
                    part.ClearSelection2(true);
                }
                catch { feat = null; }

                if (feat == null)
                {
                    CleanupLooseSketch(part);
                    res.Error = "SolidWorks refused the revolve — the torus could not be built. Nothing was left behind except an empty new part.";
                    return res;
                }

                // ---- rebuild once, then INDEPENDENTLY verify by mass-property volume (fail closed) ----
                try { part.ForceRebuild3(false); } catch { }
                try { res.RebuildClean = part.Extension.GetWhatsWrongCount() == 0; } catch { }
                try { var mp = part.Extension.CreateMassProperty(); if (mp != null) { mp.UseSystemUnits = true; res.VolumeMm3 = mp.Volume * 1e9; } } catch { }

                // Volume read failing is NOT a refusal to report the feature as created — but it IS a failed
                // verification: Created stays true, Verified goes false (never a fake green on an unmeasured solid).
                res.Created = true;
                double tol = Math.Max(1.0, expectedMm3 * 0.02);   // 2% of the expected torus volume
                res.Verified = res.VolumeMm3 > 0
                               && Math.Abs(res.VolumeMm3 - expectedMm3) <= tol
                               && res.RebuildClean;

                await emit("Sentinel", null, "done",
                    "torus " + Trim(RMm) + " mm major × " + Trim(rMm) + " mm tube, " +
                    (res.VolumeMm3 > 0 ? res.VolumeMm3.ToString("N0") + " mm³" : "volume read") +
                    " (expected ≈ " + Math.Round(expectedMm3).ToString("N0") + " mm³), rebuild " +
                    (res.RebuildClean ? "clean" : "flagged"));

                if (res.Verified)
                    res.Info = "Created a " + Trim(RMm) + " mm major × " + Trim(rMm) + " mm tube torus — " +
                               res.VolumeMm3.ToString("N0") + " mm³ (expected ≈ " + Math.Round(expectedMm3).ToString("N0") +
                               "), rebuild clean. One Ctrl+Z removes it; Forge didn't save.";
                else
                    res.Info = "Created a " + Trim(RMm) + " mm major × " + Trim(rMm) + " mm tube torus feature — but it couldn't be independently verified: " +
                               (res.VolumeMm3 > 0 ? res.VolumeMm3.ToString("N0") + " mm³ measured vs ≈" + Math.Round(expectedMm3).ToString("N0") + " expected" : "volume read failed") +
                               ", rebuild " + (res.RebuildClean ? "clean" : "flagged") + ". Check the model. Forge didn't save.";
            }
            catch (Exception ex) { res.Error = ex.Message; }
            return res;
        }

        // Major (R) + minor (r) radius in mm from the intent. Labelled first ("40mm major radius" / "major radius 40",
        // "10mm minor/tube"); a bare "R and r mm" pair reads major × minor; a single "N mm" is the major radius.
        // Defaults when nothing is stated.
        private static void ParseDims(string intent, ref double RMm, ref double rMm)
        {
            string c = (intent ?? "").ToLowerInvariant();

            var majorM = Regex.Match(c, @"\b(?:major|outer)\s+radius\s*[=:]?\s*(\d+(?:\.\d+)?)\s*mm");
            var minorM = Regex.Match(c, @"\b(?:minor|inner|tube|small)\s+radius\s*[=:]?\s*(\d+(?:\.\d+)?)\s*mm");
            if (majorM.Success) double.TryParse(majorM.Groups[1].Value, out RMm);
            if (minorM.Success) double.TryParse(minorM.Groups[1].Value, out rMm);
            if (majorM.Success || minorM.Success) return;

            var bareR = Regex.Match(c, @"\bradius\s*[=:]?\s*(\d+(?:\.\d+)?)\s*mm");
            if (bareR.Success) { double.TryParse(bareR.Groups[1].Value, out RMm); return; }

            var pair = Regex.Match(c, @"(\d+(?:\.\d+)?)\s*[x×,]\s*(\d+(?:\.\d+)?)\s*mm");
            if (pair.Success)
            {
                double a = 0, b = 0;
                double.TryParse(pair.Groups[1].Value, out a);
                double.TryParse(pair.Groups[2].Value, out b);
                if (a > 0) RMm = a;
                if (b > 0) rMm = b;
                return;
            }

            var all = Regex.Matches(c, @"(\d+(?:\.\d+)?)\s*mm");
            if (all.Count >= 2)
            {
                double a = 0, b = 0;
                double.TryParse(all[0].Groups[1].Value, out a);
                double.TryParse(all[1].Groups[1].Value, out b);
                if (a > 0) RMm = a;
                if (b > 0) rMm = b;
            }
            else if (all.Count == 1)
            {
                double a = 0;
                double.TryParse(all[0].Groups[1].Value, out a);
                if (a > 0) RMm = a;
            }
        }

        // same template resolution spine as CreatePart: configured default part template, else the stock part.prtdot
        private static string PartTemplate(ISldWorks app)
        {
            string template = null;
            try { template = app.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swDefaultTemplatePart); } catch { }
            if (string.IsNullOrEmpty(template) || !File.Exists(template))
            {
                string exeDir = null; try { exeDir = Path.GetDirectoryName(app.GetExecutablePath()); } catch { }
                string fallback = !string.IsNullOrEmpty(exeDir) ? Path.Combine(exeDir, @"..\data\templates\part.prtdot") : null;
                if (fallback != null && File.Exists(fallback)) template = fallback;
            }
            return !string.IsNullOrEmpty(template) && File.Exists(template) ? template : null;
        }

        // a refused revolve leaves the just-drawn sketch loose — delete it so the new part isn't littered
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

        private static string Trim(double v) => v.ToString("0.###");
    }
}
