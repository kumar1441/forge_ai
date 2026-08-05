using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class AddHelixResult
    {
        public bool Success;
        public bool AlreadyDone;
        public string FeatureName;
        public string FeatureType;
        public double Revolutions;
        public double PitchMm;
        public string Diag;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 213 — add_helix / create_helix. Sketches a single circle on the Front plane (radius 10mm) and threads a
    /// constant-pitch helix through it via IModelDoc2.InsertHelix (pitch-and-revolution definition). A helix is a
    /// pure CURVE feature — it adds NO solid body and changes NO volume, so verification is by feature EXISTENCE +
    /// type + clean rebuild (not volume). InsertHelix returns the Feature or null; the handler INSTRUMENTS the null
    /// return and fails CLOSED on a no-op (same discipline as the FeatureRevolve2/InsertProtrusionSwept4 family).
    /// Names the feature "Forge-Helix" for idempotency; never saves.
    /// </summary>
    public static class AddHelix
    {
        private const string HelixName = "Forge-Helix";
        private const double CircleRadius = 0.010;   // 10mm -> helix diameter 20mm
        private const double PitchM = 0.005;         // 5mm pitch
        private const double Revolutions = 4.0;

        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            return Regex.IsMatch(c, @"\b(helix|helical|spiral|coil)\b");
        }

        public static async Task<AddHelixResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new AddHelixResult();
            var part = model as PartDoc;
            if (part == null) { res.Error = "Open a part (.SLDPRT) to add a helix."; return res; }

            var existing = FindFeature(model, HelixName);
            if (existing != null)
            {
                res.AlreadyDone = true; res.Success = true; res.FeatureName = SafeName(existing);
                res.FeatureType = SafeType(existing);
                res.Info = "A helix (" + res.FeatureName + ") is already here — nothing to do.";
                return res;
            }

            await emit("Builder", "threading a helix through a circle", "run", null);

            Feature feat = null;
            bool retNull = false;
            try
            {
                SelectPlane(model, "Front Plane");
                var sm = model.SketchManager;
                sm.InsertSketch(true);
                sm.CreateCircleByRadius(0, 0, 0, CircleRadius);   // the helix base circle (diameter)
                sm.InsertSketch(true);                            // exit the sketch
                model.ClearSelection2(true);

                var skFeat = model.FeatureByPositionReverse(0) as Feature;
                if (skFeat != null) skFeat.Select2(false, 0);

                // pitch-and-revolution helix: NOT reversed, clockwise, NOT tapered/outward, Height ignored (0),
                // Pitch=5mm, Revolution=4, TaperAngle=0, StartAngle=0. InsertHelix is VOID on this interop — it
                // consumes the selected circle sketch and appends a Helix feature; capture it as the last feature.
                model.InsertHelix(
                    false, true, false, false,
                    (int)swHelixDefinedBy_e.swHelixDefinedByPitchAndRevolution,
                    0, PitchM, Revolutions, 0, 0);
                model.ClearSelection2(true);
                model.ForceRebuild3(false);

                // On success the helix consumed the sketch and is now the last feature; a no-op leaves the sketch
                // (ProfileFeature) as the last feature. Only accept a non-ProfileFeature last feature as the helix.
                var last = model.FeatureByPositionReverse(0) as Feature;
                string lastType = last == null ? null : SafeType(last);
                feat = (last != null && lastType != "ProfileFeature") ? last : null;
                retNull = feat == null;
            }
            catch (Exception ex) { res.Error = "Helix failed: " + ex.Message; return res; }

            if (feat == null)
            {
                CleanupLooseSketch(model);
                res.Diag = "InsertHelix returned null (retNull=" + retNull + ")";
                res.Error = "SolidWorks refused the helix (InsertHelix returned null) — the operation may be dead headless on this build.";
                return res;
            }
            try { feat.Name = HelixName; } catch { }

            res.FeatureName = SafeName(feat);
            res.FeatureType = SafeType(feat);
            res.PitchMm = PitchM * 1000.0;
            res.Revolutions = Revolutions;
            int rw = 0; try { rw = model.Extension.GetWhatsWrongCount(); } catch { }
            bool present = FindFeature(model, HelixName) != null;
            res.Success = present && rw == 0;
            res.Diag = "helix name=" + res.FeatureName + " type=" + res.FeatureType + " pitchMm=" + res.PitchMm + " revs=" + res.Revolutions + " rebuildErr=" + rw;

            await emit("Builder", null, "done", res.Success ? "helix added" : ("helix not verified (rebuildErr=" + rw + ")"));

            res.Info = res.Success
                ? "Threaded a " + res.Revolutions + "-turn helix (" + res.FeatureName + "), " + res.PitchMm + "mm pitch. Undo removes it; nothing was saved."
                : "Helix did not verify (present=" + present + ", rebuildErr=" + rw + ").";
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

        private static string SafeName(Feature f) { try { return f.Name; } catch { return null; } }
        private static string SafeType(Feature f) { try { return f.GetTypeName2(); } catch { return null; } }
        private static void SelectPlane(IModelDoc2 model, string plane)
        { try { model.Extension.SelectByID2(plane, "PLANE", 0, 0, 0, false, 0, null, 0); } catch { } }
    }
}
