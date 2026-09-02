using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class CreateCylinderResult
    {
        public bool Created;
        public double DiameterMm;       // diameter parsed from the intent; default 40mm
        public double HeightMm;         // height parsed from the intent; default 60mm
        public double VolumeMm3 = -1;   // post-rebuild, independently measured
        public bool RebuildClean;
        public bool Verified;           // fail closed: volume ≈ πr²h AND the rebuild is clean
        public string Info;
        public string Error;
    }

    /// <summary>
    /// CreateCylinder — tool: create a SOLID CYLINDER part FROM SCRATCH (blank part + centred circle on the
    /// Front plane + FeatureExtrusion3 to the requested height). The "create a cylinder" basic-solid primitive —
    /// CreatePart alone makes a BLANK part (no solid), so a from-scratch extruded cylinder is its own tool.
    /// The sketch/extrude calls mirror the PROVEN CreatePlate box spine (CreateCircleByRadius + FeatureExtrusion3);
    /// the height is the plate's blind depth. Diameter defaults to 40mm, height to 60mm when the intent carries
    /// only one (or no) dimension. Never saves.
    /// </summary>
    public static class CreateCylinder
    {
        private const double MM = 0.001;               // mm -> SW metres
        private const double DefaultDiameterMm = 40.0; // sensible default when no size is stated
        private const double DefaultHeightMm = 60.0;   // sensible default when no height is stated

        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            bool obj = Regex.IsMatch(c, @"\b(cylinde(?:r|rical)|pillar|rod|tube|pipe)\b");
            bool verb = Regex.IsMatch(c, @"\b(create|make|build|add|new|start)\b");
            return obj && verb;
        }

        public static async Task<CreateCylinderResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new CreateCylinderResult();

            double dMm = DefaultDiameterMm, hMm = DefaultHeightMm;
            ParseDims(intent ?? "", ref dMm, ref hMm);
            res.DiameterMm = dMm;
            res.HeightMm = hMm;
            double expectedMm3 = Math.PI * (dMm / 2.0) * (dMm / 2.0) * hMm;

            await emit("Builder", "creating a " + Trim(dMm) + " mm diameter × " + Trim(hMm) + " mm tall cylinder", "run", null);
            try
            {
                // clean slate: close any open doc so the new one is active without lock issues (CreatePlate spine)
                try { var cur = app.ActiveDoc as IModelDoc2; if (cur != null) app.CloseDoc(cur.GetTitle()); } catch { }
                string template = PartTemplate(app);
                if (template == null) { res.Error = "Couldn't find a part template on this install."; return res; }
                var part = app.NewDocument(template, 0, 0, 0) as IModelDoc2;
                if (part == null) { res.Error = "NewDocument returned nothing — the part template may be invalid."; return res; }

                bool sel = false;
                try { sel = part.Extension.SelectByID2("Front Plane", "PLANE", 0, 0, 0, false, 0, null, 0); } catch { }
                if (!sel) { res.Error = "could not select the Front Plane"; return res; }

                double r = dMm / 2.0 * MM;
                part.SketchManager.InsertSketch(true);
                part.SketchManager.CreateCircleByRadius(0, 0, 0, r);
                part.SketchManager.InsertSketch(true);

                Feature feat = null;
                try
                {
                    feat = part.FeatureManager.FeatureExtrusion3(true, false, false, 0, 0, hMm * MM, 0, false, false, false,
                        false, 0, 0, false, false, false, false, true, true, true, 0, 0, false) as Feature;
                }
                catch { feat = null; }

                if (feat == null)
                {
                    res.Error = "SolidWorks refused the extrusion — the cylinder could not be built. Nothing was left behind except an empty new part.";
                    return res;
                }

                // ---- rebuild once, then INDEPENDENTLY verify by mass-property volume (fail closed) ----
                try { part.ForceRebuild3(false); } catch { }
                try { res.RebuildClean = part.Extension.GetWhatsWrongCount() == 0; } catch { }
                try { var mp = part.Extension.CreateMassProperty(); if (mp != null) { mp.UseSystemUnits = true; res.VolumeMm3 = mp.Volume * 1e9; } } catch { }

                // Volume read failing is NOT a refusal to report the feature as created — but it IS a failed
                // verification: Created stays true, Verified goes false (never a fake green on an unmeasured solid).
                res.Created = true;
                double tol = Math.Max(1.0, expectedMm3 * 0.02);   // 2% of the expected cylinder volume
                res.Verified = res.VolumeMm3 > 0
                               && Math.Abs(res.VolumeMm3 - expectedMm3) <= tol
                               && res.RebuildClean;

                await emit("Sentinel", null, "done",
                    "cylinder " + Trim(dMm) + " mm dia × " + Trim(hMm) + " mm, " +
                    (res.VolumeMm3 > 0 ? res.VolumeMm3.ToString("N0") + " mm³" : "volume read") +
                    " (expected ≈ " + Math.Round(expectedMm3).ToString("N0") + " mm³), rebuild " +
                    (res.RebuildClean ? "clean" : "flagged"));

                if (res.Verified)
                    res.Info = "Created a " + Trim(dMm) + " mm diameter × " + Trim(hMm) + " mm tall cylinder — " +
                               res.VolumeMm3.ToString("N0") + " mm³ (expected ≈ " + Math.Round(expectedMm3).ToString("N0") +
                               "), rebuild clean. One Ctrl+Z removes it; Forge didn't save.";
                else
                    res.Info = "Created a " + Trim(dMm) + " mm diameter × " + Trim(hMm) + " mm tall cylinder feature — but it couldn't be independently verified: " +
                               (res.VolumeMm3 > 0 ? res.VolumeMm3.ToString("N0") + " mm³ measured vs ≈" + Math.Round(expectedMm3).ToString("N0") + " expected" : "volume read failed") +
                               ", rebuild " + (res.RebuildClean ? "clean" : "flagged") + ". Check the model. Forge didn't save.";
            }
            catch (Exception ex) { res.Error = ex.Message; }
            return res;
        }

        // Diameter + height in mm from the intent. "40mm diameter" / "diameter 40mm" and "80mm height" / "tall"
        // / "high" / "long" pair up when stated; an "AxB mm" pair reads as diameter × height; else a single "N mm"
        // is the diameter and the height keeps its default. Defaults when nothing is stated.
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

        private static string Trim(double v) => v.ToString("0.###");
    }
}
