using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class CreateConeResult
    {
        public bool Created;
        public double DiameterMm;       // base diameter parsed from the intent; default 40mm
        public double HeightMm;         // height parsed from the intent; default 60mm
        public double VolumeMm3 = -1;   // post-rebuild, independently measured
        public bool RebuildClean;
        public bool Verified;           // fail closed: volume ≈ (1/3)πr²h AND the rebuild is clean
        public string Info;
        public string Error;
    }

    /// <summary>
    /// CreateCone — tool: create a SOLID RIGHT CONE part FROM SCRATCH (blank part + a right-triangle profile on the
    /// Front plane + a 360° revolve around its axis). The "create a cone" basic-solid primitive. The profile is the
    /// classic half-cone cross-section (like CreateSphere's half-circle): a right triangle whose height leg lies on the
    /// revolve axis and whose hypotenuse runs from the apex to the base rim. Revolving it 360° sweeps a cone of radius
    /// r = D/2 and height H. Sketch construction mirrors the PROVEN CreateSphere revolve spine (centreline + profile +
    /// the identical FeatureRevolve2 call). Volume ≈ (1/3)πr²h. Intent carries D/H in mm: "cone 40mm diameter 60mm
    /// tall", "make a 30x45mm cone" reads base-diameter × height. Defaults D=40mm, H=60mm. Never saves.
    /// </summary>
    public static class CreateCone
    {
        private const double MM = 0.001;              // mm -> SW metres
        private const double DefaultDiameterMm = 40.0; // sensible default when no size is stated
        private const double DefaultHeightMm = 60.0;   // sensible default when no height is stated

        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            bool obj = Regex.IsMatch(c, @"\b(cone|conical|frustum)\b");
            bool verb = Regex.IsMatch(c, @"\b(create|make|build|add|new|start)\b");
            return obj && verb;
        }

        public static async Task<CreateConeResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new CreateConeResult();

            string cue = CreateGuardrail.UnsupportedCue(intent);
            if (cue != null) { res.Error = "I can only create a plain cone from scratch — \"" + cue + "\" (compound/positional/feature requests) isn't supported yet. Ask for a bare shape like \"create a 50mm cone\"."; return res; }

            double dMm = DefaultDiameterMm, hMm = DefaultHeightMm;
            ParseDims(intent ?? "", ref dMm, ref hMm);
            res.DiameterMm = dMm;
            res.HeightMm = hMm;
            double rMm = dMm / 2.0;
            double expectedMm3 = (1.0 / 3.0) * Math.PI * rMm * rMm * hMm;

            await emit("Builder", "creating a " + Trim(dMm) + " mm base × " + Trim(hMm) + " mm tall cone", "run", null);
            try
            {
                // clean slate: close any open doc so the new one is active without lock issues (CreatePlate spine)
                try { var cur = app.ActiveDoc as IModelDoc2; if (cur != null) app.CloseDoc(cur.GetTitle()); } catch { }
                string template = PartTemplate(app);
                if (template == null) { res.Error = "Couldn't find a part template on this install."; return res; }
                var part = app.NewDocument(template, 0, 0, 0) as IModelDoc2;
                if (part == null) { res.Error = "NewDocument returned nothing — the part template may be invalid."; return res; }

                double r = rMm * MM;
                double H = hMm * MM;
                bool sel = false;
                try { sel = part.Extension.SelectByID2("Front Plane", "PLANE", 0, 0, 0, false, 0, null, 0); } catch { }
                if (!sel) { res.Error = "could not select the Front Plane"; return res; }

                // right-triangle half-profile: apex on the revolve axis at the left, base rim at the right.
                // Vertices: A(0,0) apex · B(H,0) base centre ON the axis · C(H,r) base rim. Revolving the triangle
                // about the X axis sweeps the full cone (each x-slice is a disk of radius r·x/H).
                var sm = part.SketchManager;
                sm.InsertSketch(true);
                sm.CreateCenterLine(0, 0, 0, H, 0, 0);          // axis of revolution (X)
                sm.CreateLine(0, 0, 0, H, 0, 0);                // height leg along the axis (closes the profile)
                sm.CreateLine(H, 0, 0, H, r, 0);                // base rim (perpendicular to the axis)
                sm.CreateLine(H, r, 0, 0, 0, 0);                // hypotenuse — the cone's surface
                sm.InsertSketch(true);                          // exit the sketch
                part.ClearSelection2(true);

                var skFeat = part.FeatureByPositionReverse(0) as Feature;
                if (skFeat != null) skFeat.Select2(false, 0);

                // full 360° revolve of the triangle around the centreline -> a cone. The exact proven call shape from
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
                    res.Error = "SolidWorks refused the revolve — the cone could not be built. Nothing was left behind except an empty new part.";
                    return res;
                }

                // ---- rebuild once, then INDEPENDENTLY verify by mass-property volume (fail closed) ----
                try { part.ForceRebuild3(false); } catch { }
                try { res.RebuildClean = part.Extension.GetWhatsWrongCount() == 0; } catch { }
                try { var mp = part.Extension.CreateMassProperty(); if (mp != null) { mp.UseSystemUnits = true; res.VolumeMm3 = mp.Volume * 1e9; } } catch { }

                // Volume read failing is NOT a refusal to report the feature as created — but it IS a failed
                // verification: Created stays true, Verified goes false (never a fake green on an unmeasured solid).
                res.Created = true;
                double tol = Math.Max(1.0, expectedMm3 * 0.02);   // 2% of the expected cone volume
                res.Verified = res.VolumeMm3 > 0
                               && Math.Abs(res.VolumeMm3 - expectedMm3) <= tol
                               && res.RebuildClean;

                await emit("Sentinel", null, "done",
                    "cone " + Trim(dMm) + " mm base dia × " + Trim(hMm) + " mm, " +
                    (res.VolumeMm3 > 0 ? res.VolumeMm3.ToString("N0") + " mm³" : "volume read") +
                    " (expected ≈ " + Math.Round(expectedMm3).ToString("N0") + " mm³), rebuild " +
                    (res.RebuildClean ? "clean" : "flagged"));

                if (res.Verified)
                    res.Info = "Created a " + Trim(dMm) + " mm base × " + Trim(hMm) + " mm tall cone — " +
                               res.VolumeMm3.ToString("N0") + " mm³ (expected ≈ " + Math.Round(expectedMm3).ToString("N0") +
                               "), rebuild clean. One Ctrl+Z removes it; Forge didn't save.";
                else
                    res.Info = "Created a " + Trim(dMm) + " mm base × " + Trim(hMm) + " mm tall cone feature — but it couldn't be independently verified: " +
                               (res.VolumeMm3 > 0 ? res.VolumeMm3.ToString("N0") + " mm³ measured vs ≈" + Math.Round(expectedMm3).ToString("N0") + " expected" : "volume read failed") +
                               ", rebuild " + (res.RebuildClean ? "clean" : "flagged") + ". Check the model. Forge didn't save.";
            }
            catch (Exception ex) { res.Error = ex.Message; }
            return res;
        }

        // Base diameter + height in mm from the intent. Labelled first ("40mm diameter" / "diameter 40mm" and
        // "60mm tall"/"high"/"long"); an "AxB mm" pair reads as diameter × height; a single "N mm" is the diameter.
        // Defaults when nothing is stated.
        private static void ParseDims(string intent, ref double dMm, ref double hMm)
        {
            string c = (intent ?? "").ToLowerInvariant();

            var diaM = Regex.Match(c, @"\b(?:diam(?:eter)?|dia|Ø|⌀)\s*[=:]?\s*(\d+(?:\.\d+)?)\s*mm");
            var hM = Regex.Match(c, @"\b(?:height|tall(?:ness)?|high|long|length)\s*[=:]?\s*(\d+(?:\.\d+)?)\s*mm");
            if (diaM.Success) double.TryParse(diaM.Groups[1].Value, out dMm);
            if (hM.Success) double.TryParse(hM.Groups[1].Value, out hMm);
            if (diaM.Success || hM.Success) return;

            var pair = Regex.Match(c, @"(\d+(?:\.\d+)?)\s*[x×]\s*(\d+(?:\.\d+)?)\s*mm");
            if (pair.Success)
            {
                double a = 0, b = 0;
                double.TryParse(pair.Groups[1].Value, out a);
                double.TryParse(pair.Groups[2].Value, out b);
                if (a > 0) dMm = a;
                if (b > 0) hMm = b;
                return;
            }

            var all = Regex.Matches(c, @"(\d+(?:\.\d+)?)\s*mm");
            if (all.Count >= 2)
            {
                double a = 0, b = 0;
                double.TryParse(all[0].Groups[1].Value, out a);
                double.TryParse(all[1].Groups[1].Value, out b);
                if (a > 0) dMm = a;
                if (b > 0) hMm = b;
            }
            else if (all.Count == 1)
            {
                double a = 0;
                double.TryParse(all[0].Groups[1].Value, out a);
                if (a > 0) dMm = a;
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
