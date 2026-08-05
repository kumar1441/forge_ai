using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class AddSketchEllipseResult
    {
        public bool Success;
        public int EllipseSegments;   // expect exactly 1
        public bool AlreadyDone;
        public string SketchName;
        public string Diag;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 197 — add_sketch_ellipse. Draws an ellipse on the Front plane (SketchManager.CreateEllipse, the live
    /// entity-creation family). Names the sketch feature "Forge-Ellipse" so a rerun is a no-op (Rule #5), verifies
    /// exactly one ellipse segment, and never saves — one Ctrl+Z removes it.
    /// </summary>
    public static class AddSketchEllipse
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (Regex.IsMatch(c, @"\b(remove|delete|suppress)\b")) return false;
            bool verb = Regex.IsMatch(c, @"\b(add|draw|create|sketch|insert|make)\b");
            bool noun = Regex.IsMatch(c, @"\b(ellipse|elliptical|oval)\b");
            return verb && noun;
        }

        public static async Task<AddSketchEllipseResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new AddSketchEllipseResult();
            var part = model as PartDoc;
            if (part == null) { res.Error = "Open a part (.SLDPRT) to add an ellipse sketch."; return res; }

            var existing = FindEllipseFeature(model);
            if (existing != null)
            {
                res.AlreadyDone = true; res.Success = true; res.SketchName = SafeName(existing);
                res.EllipseSegments = CountEllipses(existing);
                res.Info = "An ellipse sketch (" + res.SketchName + ") is already here — nothing to do.";
                return res;
            }

            await emit("Draftsman", "sketching an ellipse", "run", null);

            var before = new HashSet<string>(SketchFeatureNames(model));
            try
            {
                SelectPlane(model, "Front Plane");
                var sm = model.SketchManager;
                sm.InsertSketch(true);
                // centre (0,0); major axis endpoint 30mm along X; minor axis endpoint 20mm along Y.
                sm.CreateEllipse(0, 0, 0, 0.03, 0, 0, 0, 0.02, 0);
                sm.InsertSketch(true);
                model.ClearSelection2(true);
                model.ForceRebuild3(false);
            }
            catch (Exception ex) { res.Error = "Ellipse creation failed: " + ex.Message; return res; }

            var created = NewSketchFeature(model, before);
            if (created == null) { res.Error = "The ellipse sketch was not created."; return res; }
            try { created.Name = "Forge-Ellipse"; } catch { }
            res.SketchName = SafeName(created);
            res.EllipseSegments = CountEllipses(created);
            res.Success = res.EllipseSegments == 1;
            res.Diag = "ellipses=" + res.EllipseSegments + " name=" + res.SketchName;

            await emit("Draftsman", null, "done", res.Success ? "ellipse sketched" : ("got " + res.EllipseSegments + " ellipse segments, expected 1"));

            res.Info = res.Success
                ? "Added an ellipse sketch (" + res.SketchName + ") on the Front plane. Undo removes it; nothing was saved."
                : "The sketch has " + res.EllipseSegments + " ellipse segments, expected 1 — check the model.";
            return res;
        }

        private static int CountEllipses(Feature sketchFeat)
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
                    if (t == (int)swSketchSegments_e.swSketchELLIPSE) n++;
                }
            }
            catch { }
            return n;
        }

        private static Feature FindEllipseFeature(IModelDoc2 model)
        {
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                string nm = SafeName(f);
                if (nm != null && nm.StartsWith("Forge-Ellipse", StringComparison.OrdinalIgnoreCase)) return f;
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
