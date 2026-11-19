using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class CreateSphereResult
    {
        public bool Created;
        public double DiameterMm;       // diameter parsed from the intent; default 50mm
        public double VolumeMm3 = -1;   // post-rebuild, independently measured
        public bool RebuildClean;
        public bool Verified;           // fail closed: volume ≈ (4/3)πr³ AND the rebuild is clean
        public string Info;
        public string Error;
    }

    /// <summary>
    /// CreateSphere — tool: create a SOLID SPHERE part FROM SCRATCH (blank part + half-circle profile on the
    /// Front plane + 360° revolve). The "create a sphere" / "make a sphere of 50mm" basic-solid primitive —
    /// CreatePart alone makes a BLANK part (no solid), so a from-scratch revolved ball is its own tool.
    /// Sketch construction: a single clean centreline along the X axis (the revolve axis) + one half-circle arc whose
    /// endpoints sit ON the axis — NO separate diameter line overlapping the centreline (the old duplicate/overlapping
    /// entity made the profile ambiguous and SolidWorks refused the revolve headless). Revolving the half-circle 360°
    /// sweeps a sphere of radius r = D/2. The revolve call mirrors the PROVEN AddRevolve FeatureRevolve2 usage
    /// (auto-select profile + the single centreline axis), the same spine CONE and TORUS use. Never saves.
    /// </summary>
    public static class CreateSphere
    {
        private const double MM = 0.001;              // mm -> SW metres
        private const double DefaultDiameterMm = 50.0; // sensible default when no size is stated

        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            bool obj = Regex.IsMatch(c, @"\b(sphere|spherical|ball|orb|globe)\b");
            bool verb = Regex.IsMatch(c, @"\b(create|make|build|add|new|start)\b");
            return obj && verb;
        }

        public static async Task<CreateSphereResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new CreateSphereResult();

            string cue = CreateGuardrail.UnsupportedCue(intent);
            if (cue != null) { res.Error = "I can only create a plain sphere from scratch — \"" + cue + "\" (compound/positional/feature requests) isn't supported yet. Ask for a bare shape like \"create a 50mm sphere\"."; return res; }

            double dMm = DefaultDiameterMm;
            var m = Regex.Match(intent ?? "", @"(\d+(?:\.\d+)?)\s*mm");
            if (m.Success) double.TryParse(m.Groups[1].Value, out dMm);
            res.DiameterMm = dMm;
            double rMm = dMm / 2.0;
            double expectedMm3 = 4.0 / 3.0 * Math.PI * rMm * rMm * rMm;

            await emit("Builder", "creating a " + Trim(dMm) + " mm sphere", "run", null);
            try
            {
                // clean slate: close any open doc so the new one is active without lock issues (CreatePlate spine)
                try { var cur = app.ActiveDoc as IModelDoc2; if (cur != null) app.CloseDoc(cur.GetTitle()); } catch { }
                string template = PartTemplate(app);
                if (template == null) { res.Error = "Couldn't find a part template on this install."; return res; }
                var part = app.NewDocument(template, 0, 0, 0) as IModelDoc2;
                if (part == null) { res.Error = "NewDocument returned nothing — the part template may be invalid."; return res; }

                double r = rMm * MM;
                bool sel = false;
                try { sel = part.Extension.SelectByID2("Front Plane", "PLANE", 0, 0, 0, false, 0, null, 0); } catch { }
                if (!sel) { res.Error = "could not select the Front Plane"; return res; }

                // clean hemisphere profile: one centreline (axis of revolution) + a single half-circle arc above it,
                // endpoints ON the axis. The proven CONE/TORUS revolve-sketch spine — no duplicate line over the axis.
                var sm = part.SketchManager;
                sm.InsertSketch(true);
                sm.CreateCenterLine(-r, 0, 0, r, 0, 0);          // axis of revolution (X), spans the full diameter
                sm.CreateArc(0, 0, 0, r, 0, 0, -r, 0, 0, 1);     // single clean upper half-circle, endpoints on the axis
                sm.InsertSketch(true);                           // exit the sketch
                part.ClearSelection2(true);

                var skFeat = part.FeatureByPositionReverse(0) as Feature;
                if (skFeat != null) skFeat.Select2(false, 0);

                // full 360° revolve of the half profile around the centreline -> a sphere. The exact proven call
                // shape from AddRevolve (auto-select profile + the single centreline axis).
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
                    res.Error = "SolidWorks refused the revolve — the half-circle profile may need attention headless. Nothing was left behind except an empty new part.";
                    return res;
                }

                // ---- rebuild once, then INDEPENDENTLY verify by mass-property volume (fail closed, AddRevolve spine) ----
                try { part.ForceRebuild3(false); } catch { }
                try { res.RebuildClean = part.Extension.GetWhatsWrongCount() == 0; } catch { }
                try { var mp = part.Extension.CreateMassProperty(); if (mp != null) { mp.UseSystemUnits = true; res.VolumeMm3 = mp.Volume * 1e9; } } catch { }

                // Volume read failing is NOT a refusal to report the feature as created — but it IS a failed
                // verification: Created stays true, Verified goes false (never a fake green on an unmeasured solid).
                res.Created = true;
                double tol = Math.Max(1.0, expectedMm3 * 0.02);   // 2% of the expected sphere volume
                res.Verified = res.VolumeMm3 > 0
                               && Math.Abs(res.VolumeMm3 - expectedMm3) <= tol
                               && res.RebuildClean;

                await emit("Sentinel", null, "done",
                    "sphere " + Trim(dMm) + " mm, " +
                    (res.VolumeMm3 > 0 ? res.VolumeMm3.ToString("N0") + " mm³" : "volume read") +
                    " (expected ≈ " + Math.Round(expectedMm3).ToString("N0") + " mm³), rebuild " +
                    (res.RebuildClean ? "clean" : "flagged"));

                if (res.Verified)
                    res.Info = "Created a " + Trim(dMm) + " mm sphere — " + res.VolumeMm3.ToString("N0") + " mm³ (expected ≈ " +
                               Math.Round(expectedMm3).ToString("N0") + "), rebuild clean. One Ctrl+Z removes it; Forge didn't save.";
                else
                    res.Info = "Created a " + Trim(dMm) + " mm sphere feature — but it couldn't be independently verified: " +
                               (res.VolumeMm3 > 0 ? res.VolumeMm3.ToString("N0") + " mm³ measured vs ≈" + Math.Round(expectedMm3).ToString("N0") + " expected" : "volume read failed") +
                               ", rebuild " + (res.RebuildClean ? "clean" : "flagged") + ". Check the model. Forge didn't save.";
            }
            catch (Exception ex) { res.Error = ex.Message; }
            return res;
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
