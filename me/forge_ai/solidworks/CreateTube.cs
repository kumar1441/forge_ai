using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class CreateTubeResult
    {
        public bool Created;
        public double OuterMm;          // outer diameter parsed from the intent; default 40mm
        public double InnerMm;          // inner diameter parsed from the intent; default 24mm
        public double LengthMm;         // length parsed from the intent; default 60mm
        public double VolumeMm3 = -1;   // post-rebuild, independently measured
        public bool RebuildClean;
        public bool Verified;           // fail closed: volume ≈ π(ro²−ri²)·L AND the rebuild is clean
        public string Info;
        public string Error;
    }

    /// <summary>
    /// CreateTube — tool: create a HOLLOW CYLINDER (pipe/tube/sleeve) part FROM SCRATCH: a blank part + two CONCENTRIC
    /// circles on the Front plane (outer wall + inner bore) extruded to a length. The "create a tube/pipe" basic-solid
    /// primitive — the extruded-annulus sibling of CreateFlange, just taller (length ≫ thickness). The sketch/extrude
    /// calls mirror the PROVEN CreateFlange/CreateCylinder spine (CreateCircleByRadius + FeatureExtrusion3): SolidWorks
    /// reads the fully-nested inner circle as a void, so a single boss-extrude of the two concentric circles yields the
    /// hollow tube, volume ≈ π(ro²−ri²)·L. Intent carries outer/inner/length in mm: "tube 40mm outer 24mm inner 60mm
    /// long", "make a pipe 40x24x60 mm". Defaults: outer 40mm, inner 24mm, length 60mm. Never saves.
    /// </summary>
    public static class CreateTube
    {
        private const double MM = 0.001;              // mm -> SW metres
        private const double DefaultOuterMm = 40.0;   // sensible default when no size is stated
        private const double DefaultInnerMm = 24.0;   // sensible default when no bore is stated
        private const double DefaultLengthMm = 60.0;  // sensible default when no length is stated

        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            bool obj = Regex.IsMatch(c, @"\b(tube|tubes|pipe|pipes|hollow cylinder|hollow cylindrical|sleeve)\b");
            bool verb = Regex.IsMatch(c, @"\b(create|make|build|add|new|start)\b");
            return obj && verb;
        }

        public static async Task<CreateTubeResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new CreateTubeResult();

            string cue = CreateGuardrail.UnsupportedCue(intent);
            if (cue != null) { res.Error = "I can only create a plain tube from scratch — \"" + cue + "\" (compound/positional/feature requests) isn't supported yet. Ask for a bare shape like \"create a 50mm tube\"."; return res; }

            double dMm = DefaultOuterMm, iMm = DefaultInnerMm, lMm = DefaultLengthMm;
            ParseDims(intent ?? "", ref dMm, ref iMm, ref lMm);
            res.OuterMm = dMm;
            res.InnerMm = iMm;
            res.LengthMm = lMm;
            double ro = dMm / 2.0, ri = iMm / 2.0;
            double expectedMm3 = Math.PI * (ro * ro - ri * ri) * lMm;

            await emit("Builder", "creating a " + Trim(dMm) + " mm outer × " + Trim(iMm) + " mm inner × " + Trim(lMm) + " mm long tube", "run", null);
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
                part.SketchManager.CreateCircleByRadius(0, 0, 0, ro * MM);   // outer wall
                part.SketchManager.CreateCircleByRadius(0, 0, 0, ri * MM);   // inner bore (nested -> void)
                part.SketchManager.InsertSketch(true);

                Feature feat = null;
                try
                {
                    feat = part.FeatureManager.FeatureExtrusion3(true, false, false, 0, 0, lMm * MM, 0, false, false, false,
                        false, 0, 0, false, false, false, false, true, true, true, 0, 0, false) as Feature;
                }
                catch { feat = null; }

                if (feat == null)
                {
                    res.Error = "SolidWorks refused the extrusion — the tube could not be built. Nothing was left behind except an empty new part.";
                    return res;
                }

                // ---- rebuild once, then INDEPENDENTLY verify by mass-property volume (fail closed) ----
                try { part.ForceRebuild3(false); } catch { }
                try { res.RebuildClean = part.Extension.GetWhatsWrongCount() == 0; } catch { }
                try { var mp = part.Extension.CreateMassProperty(); if (mp != null) { mp.UseSystemUnits = true; res.VolumeMm3 = mp.Volume * 1e9; } } catch { }

                // Volume read failing is NOT a refusal to report the feature as created — but it IS a failed
                // verification: Created stays true, Verified goes false (never a fake green on an unmeasured solid).
                res.Created = true;
                double tol = Math.Max(1.0, expectedMm3 * 0.02);   // 2% of the expected tube volume
                res.Verified = res.VolumeMm3 > 0
                               && Math.Abs(res.VolumeMm3 - expectedMm3) <= tol
                               && res.RebuildClean;

                await emit("Sentinel", null, "done",
                    "tube " + Trim(dMm) + " mm outer × " + Trim(iMm) + " mm inner × " + Trim(lMm) + " mm, " +
                    (res.VolumeMm3 > 0 ? res.VolumeMm3.ToString("N0") + " mm³" : "volume read") +
                    " (expected ≈ " + Math.Round(expectedMm3).ToString("N0") + " mm³), rebuild " +
                    (res.RebuildClean ? "clean" : "flagged"));

                if (res.Verified)
                    res.Info = "Created a " + Trim(dMm) + " mm outer × " + Trim(iMm) + " mm inner × " + Trim(lMm) + " mm tube — " +
                               res.VolumeMm3.ToString("N0") + " mm³ (expected ≈ " + Math.Round(expectedMm3).ToString("N0") +
                               "), rebuild clean. One Ctrl+Z removes it; Forge didn't save.";
                else
                    res.Info = "Created a " + Trim(dMm) + " mm outer × " + Trim(iMm) + " mm inner × " + Trim(lMm) + " mm tube feature — but it couldn't be independently verified: " +
                               (res.VolumeMm3 > 0 ? res.VolumeMm3.ToString("N0") + " mm³ measured vs ≈" + Math.Round(expectedMm3).ToString("N0") + " expected" : "volume read failed") +
                               ", rebuild " + (res.RebuildClean ? "clean" : "flagged") + ". Check the model. Forge didn't save.";
            }
            catch (Exception ex) { res.Error = ex.Message; }
            return res;
        }

        // Outer diameter + inner diameter + length in mm from the intent. Labelled first ("40mm outer" / "outer 40mm",
        // "inner/inside 24mm", "60mm long"); a "AxBxC mm" pair/triple reads as outer × inner × length; else bare "N mm"
        // numbers fill outer, inner, length in that order. Defaults when nothing is stated.
        private static void ParseDims(string intent, ref double dMm, ref double iMm, ref double lMm)
        {
            string c = (intent ?? "").ToLowerInvariant();

            var outerM = Regex.Match(c, @"\b(?:outer|outside|od|o\.?d\.?|o\/d)\s*[=:]?\s*(\d+(?:\.\d+)?)\s*mm");
            var innerM = Regex.Match(c, @"\b(?:inner|inside|bore|hole|id|i\.?d\.?|i\/d)\s*[=:]?\s*(\d+(?:\.\d+)?)\s*mm");
            var lenM = Regex.Match(c, @"\b(?:length|long|tall|height|high)\s*[=:]?\s*(\d+(?:\.\d+)?)\s*mm");
            if (outerM.Success) double.TryParse(outerM.Groups[1].Value, out dMm);
            if (innerM.Success) double.TryParse(innerM.Groups[1].Value, out iMm);
            if (lenM.Success) double.TryParse(lenM.Groups[1].Value, out lMm);
            if (outerM.Success || innerM.Success || lenM.Success) return;

            var triple = Regex.Match(c, @"(\d+(?:\.\d+)?)\s*[x×]\s*(\d+(?:\.\d+)?)\s*[x×]\s*(\d+(?:\.\d+)?)\s*mm");
            if (triple.Success)
            {
                double a = 0, bb = 0, cc = 0;
                double.TryParse(triple.Groups[1].Value, out a);
                double.TryParse(triple.Groups[2].Value, out bb);
                double.TryParse(triple.Groups[3].Value, out cc);
                if (a > 0) dMm = a;
                if (bb > 0) iMm = bb;
                if (cc > 0) lMm = cc;
                return;
            }

            var pair = Regex.Match(c, @"(\d+(?:\.\d+)?)\s*[x×]\s*(\d+(?:\.\d+)?)\s*mm");
            if (pair.Success)
            {
                double a = 0, bb = 0;
                double.TryParse(pair.Groups[1].Value, out a);
                double.TryParse(pair.Groups[2].Value, out bb);
                if (a > 0) dMm = a;
                if (bb > 0) iMm = bb;
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
                if (bb > 0) iMm = bb;
                if (cc > 0) lMm = cc;
            }
            else if (all.Count == 2)
            {
                double a = 0, bb = 0;
                double.TryParse(all[0].Groups[1].Value, out a);
                double.TryParse(all[1].Groups[1].Value, out bb);
                if (a > 0) dMm = a;
                if (bb > 0) iMm = bb;
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
