using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class AddSketchSplineResult
    {
        public bool Success;
        public int SplineSegments;   // expect 1
        public bool AlreadyDone;
        public string SketchName;
        public string Diag;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 116 — add_sketch_spline. Draws a spline through four points on the Front plane (SketchManager.CreateSpline,
    /// the live entity-creation family). NB the point array is passed as a concrete double[] — a boxed object[] marshals
    /// as SAFEARRAY(VARIANT) and is ignored (the SkippedItemArray landmine). Names the sketch feature "Forge-Spline" for
    /// idempotency (Rule #5), verifies exactly one spline segment, and never saves — one Ctrl+Z removes it.
    /// </summary>
    public static class AddSketchSpline
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (Regex.IsMatch(c, @"\b(remove|delete|suppress)\b")) return false;
            // the curve-THROUGH-POINTS feature (tool 214, CreateCurve) owns the qualified curve phrasings — a sketch
            // spline must not steal "curve through points / 3d curve / space curve / guide curve / xyz curve".
            if (Regex.IsMatch(c, @"(through\s+(the\s+)?points|through\s+xyz|3d ?curve|space\s+curve|guide\s+curve|xyz\s+curve)")) return false;
            bool verb = Regex.IsMatch(c, @"\b(add|draw|create|sketch|insert|make)\b");
            bool noun = Regex.IsMatch(c, @"\b(spline|curve|freeform|free-form)\b");
            return verb && noun;
        }

        public static async Task<AddSketchSplineResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new AddSketchSplineResult();
            var part = model as PartDoc;
            if (part == null) { res.Error = "Open a part (.SLDPRT) to add a spline sketch."; return res; }

            var existing = FindSplineFeature(model);
            if (existing != null)
            {
                res.AlreadyDone = true; res.Success = true; res.SketchName = SafeName(existing);
                res.SplineSegments = CountSplines(existing);
                res.Info = "A spline sketch (" + res.SketchName + ") is already here — nothing to do.";
                return res;
            }

            await emit("Draftsman", "sketching a spline", "run", null);

            var before = new HashSet<string>(SketchFeatureNames(model));
            try
            {
                SelectPlane(model, "Front Plane");
                var sm = model.SketchManager;
                sm.InsertSketch(true);
                // 4 through-points (mm -> m), flat x,y,z triples. Concrete double[] (NOT boxed object[]).
                double[] pts = { 0, 0, 0, 0.010, 0.015, 0, 0.025, 0.010, 0, 0.040, 0.020, 0 };
                sm.CreateSpline(pts);
                sm.InsertSketch(true);
                model.ClearSelection2(true);
                model.ForceRebuild3(false);
            }
            catch (Exception ex) { res.Error = "Spline creation failed: " + ex.Message; return res; }

            var created = NewSketchFeature(model, before);
            if (created == null) { res.Error = "The spline sketch was not created."; return res; }
            try { created.Name = "Forge-Spline"; } catch { }
            res.SketchName = SafeName(created);
            res.SplineSegments = CountSplines(created);
            res.Success = res.SplineSegments == 1;
            res.Diag = "splines=" + res.SplineSegments + " name=" + res.SketchName;

            await emit("Draftsman", null, "done", res.Success ? "spline sketched" : ("got " + res.SplineSegments + " spline segments, expected 1"));

            res.Info = res.Success
                ? "Added a spline sketch (" + res.SketchName + ") on the Front plane. Undo removes it; nothing was saved."
                : "The sketch has " + res.SplineSegments + " spline segments, expected 1 — check the model.";
            return res;
        }

        private static int CountSplines(Feature sketchFeat)
        {
            int n = 0;
            try
            {
                var sk = sketchFeat.GetSpecificFeature2() as Sketch;
                if (sk == null) return 0;
                foreach (var o in (sk.GetSketchSegments() as object[]) ?? new object[0])
                {
                    var seg = o as SketchSegment; if (seg == null) continue;
                    bool constr = false; try { constr = seg.ConstructionGeometry; } catch { }
                    if (constr) continue;
                    int t = -1; try { t = seg.GetType(); } catch { }
                    if (t == (int)swSketchSegments_e.swSketchSPLINE) n++;
                }
            }
            catch { }
            return n;
        }

        private static Feature FindSplineFeature(IModelDoc2 model)
        {
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                string nm = SafeName(f);
                if (nm != null && nm.StartsWith("Forge-Spline", StringComparison.OrdinalIgnoreCase)) return f;
                f = f.GetNextFeature() as Feature;
            }
            return null;
        }

        private static IEnumerable<string> SketchFeatureNames(IModelDoc2 model)
        {
            var list = new List<string>();
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                string tn = ""; try { tn = f.GetTypeName2(); } catch { }
                if (tn == "ProfileFeature") list.Add(SafeName(f));
                f = f.GetNextFeature() as Feature;
            }
            return list;
        }

        private static Feature NewSketchFeature(IModelDoc2 model, HashSet<string> before)
        {
            Feature found = null;
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                string tn = ""; try { tn = f.GetTypeName2(); } catch { }
                if (tn == "ProfileFeature" && !before.Contains(SafeName(f))) found = f;
                f = f.GetNextFeature() as Feature;
            }
            return found;
        }

        private static string SafeName(Feature f) { try { return f.Name; } catch { return null; } }
        private static void SelectPlane(IModelDoc2 model, string plane)
        { try { model.Extension.SelectByID2(plane, "PLANE", 0, 0, 0, false, 0, null, 0); } catch { } }
    }
}
