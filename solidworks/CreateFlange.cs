using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class CreateFlangeResult
    {
        public bool Created;
        public double OuterMm;          // outer diameter parsed from the intent; default 60mm
        public double BoreMm;           // bore diameter parsed from the intent; default 20mm
        public double ThicknessMm;      // thickness parsed from the intent; default 10mm
        public double VolumeMm3 = -1;   // post-rebuild, independently measured
        public bool RebuildClean;
        public bool Verified;           // fail closed: volume ≈ π(ro²−ri²)·h AND the rebuild is clean
        public string Info;
        public string Error;
    }

    /// <summary>
    /// CreateFlange — tool: create a flat annulus disc (washer/flange/ring) part FROM SCRATCH: a blank part + two
    /// CONCENTRIC circles on the Front plane (outer rim + central bore) extruded to a short height. The "create a
    /// washer/flange/ring" basic-solid primitive — CreatePart alone makes a BLANK part (no solid). The sketch/extrude
    /// calls mirror the PROVEN CreatePlate/CreateCylinder spine (CreateCircleByRadius + FeatureExtrusion3): SolidWorks
    /// reads the fully-nested inner circle as a void, so a single boss-extrude of the two concentric circles yields the
    /// annulus, volume ≈ π(ro²−ri²)·h. Intent carries outer/bore/thickness in mm: "flange 80mm outer 30mm bore 12mm
    /// thick", "make a washer 60x20x10 mm". Defaults: outer 60mm, bore 20mm, thickness 10mm. Never saves.
    /// </summary>
    public static class CreateFlange
    {
        private const double MM = 0.001;              // mm -> SW metres
        private const double DefaultOuterMm = 60.0;   // sensible default when no size is stated
        private const double DefaultBoreMm = 20.0;    // sensible default when no bore is stated
        private const double DefaultThicknessMm = 10.0;

        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            bool obj = Regex.IsMatch(c, @"\b(flange|flanges|washer|washers|annulus|ring)\b");
            bool verb = Regex.IsMatch(c, @"\b(create|make|build|add|new|start)\b");
            return obj && verb;
        }

        public static async Task<CreateFlangeResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new CreateFlangeResult();

            double dMm = DefaultOuterMm, bMm = DefaultBoreMm, hMm = DefaultThicknessMm;
            ParseDims(intent ?? "", ref dMm, ref bMm, ref hMm);
            res.OuterMm = dMm;
            res.BoreMm = bMm;
            res.ThicknessMm = hMm;
            double ro = dMm / 2.0, ri = bMm / 2.0;
            double expectedMm3 = Math.PI * (ro * ro - ri * ri) * hMm;

            await emit("Builder", "creating a " + Trim(dMm) + " mm outer × " + Trim(bMm) + " mm bore × " + Trim(hMm) + " mm thick flange", "run", null);
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

                part.SketchManager.InsertSketch(true);
                part.SketchManager.CreateCircleByRadius(0, 0, 0, ro * MM);   // outer rim
                part.SketchManager.CreateCircleByRadius(0, 0, 0, ri * MM);   // central bore (nested -> void)
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
                    res.Error = "SolidWorks refused the extrusion — the flange could not be built. Nothing was left behind except an empty new part.";
                    return res;
                }

                // ---- rebuild once, then INDEPENDENTLY verify by mass-property volume (fail closed) ----
                try { part.ForceRebuild3(false); } catch { }
                try { res.RebuildClean = part.Extension.GetWhatsWrongCount() == 0; } catch { }
                try { var mp = part.Extension.CreateMassProperty(); if (mp != null) { mp.UseSystemUnits = true; res.VolumeMm3 = mp.Volume * 1e9; } } catch { }

                // Volume read failing is NOT a refusal to report the feature as created — but it IS a failed
                // verification: Created stays true, Verified goes false (never a fake green on an unmeasured solid).
                res.Created = true;
                double tol = Math.Max(1.0, expectedMm3 * 0.02);   // 2% of the expected annulus volume
                res.Verified = res.VolumeMm3 > 0
                               && Math.Abs(res.VolumeMm3 - expectedMm3) <= tol
                               && res.RebuildClean;

                await emit("Sentinel", null, "done",
                    "flange " + Trim(dMm) + " mm outer × " + Trim(bMm) + " mm bore × " + Trim(hMm) + " mm, " +
                    (res.VolumeMm3 > 0 ? res.VolumeMm3.ToString("N0") + " mm³" : "volume read") +
                    " (expected ≈ " + Math.Round(expectedMm3).ToString("N0") + " mm³), rebuild " +
                    (res.RebuildClean ? "clean" : "flagged"));

                if (res.Verified)
                    res.Info = "Created a " + Trim(dMm) + " mm outer × " + Trim(bMm) + " mm bore × " + Trim(hMm) + " mm flange — " +
                               res.VolumeMm3.ToString("N0") + " mm³ (expected ≈ " + Math.Round(expectedMm3).ToString("N0") +
                               "), rebuild clean. One Ctrl+Z removes it; Forge didn't save.";
                else
                    res.Info = "Created a " + Trim(dMm) + " mm outer × " + Trim(bMm) + " mm bore × " + Trim(hMm) + " mm flange feature — but it couldn't be independently verified: " +
                               (res.VolumeMm3 > 0 ? res.VolumeMm3.ToString("N0") + " mm³ measured vs ≈" + Math.Round(expectedMm3).ToString("N0") + " expected" : "volume read failed") +
                               ", rebuild " + (res.RebuildClean ? "clean" : "flagged") + ". Check the model. Forge didn't save.";
            }
            catch (Exception ex) { res.Error = ex.Message; }
            return res;
        }

        // Outer diameter + bore + thickness in mm from the intent. Labelled first ("60mm outer" / "outer 60mm",
        // "bore 20mm", "8mm thick"); a "AxBxC mm" pair/triple reads as outer × bore × thickness; else bare "N mm"
        // numbers fill outer, bore, thickness in that order. Defaults when nothing is stated.
        private static void ParseDims(string intent, ref double dMm, ref double bMm, ref double hMm)
        {
            string c = (intent ?? "").ToLowerInvariant();

            var outerM = Regex.Match(c, @"\b(?:outer|outside|od|o\.?d\.?|o\/d)\s*[=:]?\s*(\d+(?:\.\d+)?)\s*mm");
            var boreM = Regex.Match(c, @"\b(?:bore|inner|inside|hole|id|i\.?d\.?|i\/d)\s*[=:]?\s*(\d+(?:\.\d+)?)\s*mm");
            var thickM = Regex.Match(c, @"\b(?:thick|thickness)\s*[=:]?\s*(\d+(?:\.\d+)?)\s*mm");
            if (outerM.Success) double.TryParse(outerM.Groups[1].Value, out dMm);
            if (boreM.Success) double.TryParse(boreM.Groups[1].Value, out bMm);
            if (thickM.Success) double.TryParse(thickM.Groups[1].Value, out hMm);
            if (outerM.Success || boreM.Success || thickM.Success) return;

            var triple = Regex.Match(c, @"(\d+(?:\.\d+)?)\s*[x×]\s*(\d+(?:\.\d+)?)\s*[x×]\s*(\d+(?:\.\d+)?)\s*mm");
            if (triple.Success)
            {
                double a = 0, bb = 0, cc = 0;
                double.TryParse(triple.Groups[1].Value, out a);
                double.TryParse(triple.Groups[2].Value, out bb);
                double.TryParse(triple.Groups[3].Value, out cc);
                if (a > 0) dMm = a;
                if (bb > 0) bMm = bb;
                if (cc > 0) hMm = cc;
                return;
            }

            var pair = Regex.Match(c, @"(\d+(?:\.\d+)?)\s*[x×]\s*(\d+(?:\.\d+)?)\s*mm");
            if (pair.Success)
            {
                double a = 0, bb = 0;
                double.TryParse(pair.Groups[1].Value, out a);
                double.TryParse(pair.Groups[2].Value, out bb);
                if (a > 0) dMm = a;
                if (bb > 0) bMm = bb;
                return;
            }

            var all = Regex.Matches(c, @"(\d+(?:\.\d+)?)\s*mm");
            if (all.Count >= 3)
            {
                double a = 0, bb = 0, cc = 0;
                double.TryParse(all[0].Groups[1].Value, out a);
                double.TryParse(all[1].Groups[1].Value, out bb);
                double.TryParse(all[2].Groups[1].Value, out cc);
                if (a > 0) dMm = a;
                if (bb > 0) bMm = bb;
                if (cc > 0) hMm = cc;
            }
            else if (all.Count == 2)
            {
                double a = 0, bb = 0;
                double.TryParse(all[0].Groups[1].Value, out a);
                double.TryParse(all[1].Groups[1].Value, out bb);
                if (a > 0) dMm = a;
                if (bb > 0) bMm = bb;
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
