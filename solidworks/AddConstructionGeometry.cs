using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class AddConstructionGeometryResult
    {
        public bool Success;
        public int ConstructionSegments;   // expect 1 (a construction centerline)
        public bool AlreadyDone;
        public string SketchName;
        public string Diag;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 204 — add_construction_geometry. Drops a construction centerline on the Front plane (a normal line segment
    /// flagged ConstructionGeometry=true — the design-intent scaffolding real sketches hang symmetry/patterns off).
    /// Names the sketch feature "Forge-Construction" so a rerun is a no-op (Rule #5), verifies exactly one construction
    /// segment, and never saves — one Ctrl+Z removes it.
    /// </summary>
    public static class AddConstructionGeometry
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (Regex.IsMatch(c, @"\b(remove|delete|suppress)\b")) return false;
            bool verb = Regex.IsMatch(c, @"\b(add|draw|create|sketch|insert|make)\b");
            bool noun = Regex.IsMatch(c, @"\b(construction (line|geometry|circle)|centerline|centre.?line|center.?line)\b");
            return verb && noun;
        }

        public static async Task<AddConstructionGeometryResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new AddConstructionGeometryResult();
            var part = model as PartDoc;
            if (part == null) { res.Error = "Open a part (.SLDPRT) to add construction geometry."; return res; }

            var existing = FindConstructionFeature(model);
            if (existing != null)
            {
                res.AlreadyDone = true; res.Success = true; res.SketchName = SafeName(existing);
                res.ConstructionSegments = CountConstruction(existing);
                res.Info = "Construction geometry (" + res.SketchName + ") is already here — nothing to do.";
                return res;
            }

            await emit("Draftsman", "sketching a construction centerline", "run", null);

            var before = new HashSet<string>(SketchFeatureNames(model));
            try
            {
                SelectPlane(model, "Front Plane");
                var sm = model.SketchManager;
                sm.InsertSketch(true);
                var seg = sm.CreateLine(-0.025, 0, 0, 0.025, 0, 0) as SketchSegment;   // 50mm horizontal
                if (seg != null) { try { seg.ConstructionGeometry = true; } catch { } }
                sm.InsertSketch(true);
                model.ClearSelection2(true);
                model.ForceRebuild3(false);
            }
            catch (Exception ex) { res.Error = "Construction geometry creation failed: " + ex.Message; return res; }

            var created = NewSketchFeature(model, before);
            if (created == null) { res.Error = "The construction sketch was not created."; return res; }
            try { created.Name = "Forge-Construction"; } catch { }
            res.SketchName = SafeName(created);
            res.ConstructionSegments = CountConstruction(created);
            res.Success = res.ConstructionSegments == 1;
            res.Diag = "construction=" + res.ConstructionSegments + " name=" + res.SketchName;

            await emit("Draftsman", null, "done", res.Success ? "construction centerline sketched" : ("got " + res.ConstructionSegments + " construction segments, expected 1"));

            res.Info = res.Success
                ? "Added a construction centerline (" + res.SketchName + ") on the Front plane. Undo removes it; nothing was saved."
                : "The sketch has " + res.ConstructionSegments + " construction segments, expected 1 — check the model.";
            return res;
        }

        private static int CountConstruction(Feature sketchFeat)
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
                    if (constr) n++;
                }
            }
            catch { }
            return n;
        }

        private static Feature FindConstructionFeature(IModelDoc2 model)
        {
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                string nm = SafeName(f);
                if (nm != null && nm.StartsWith("Forge-Construction", StringComparison.OrdinalIgnoreCase)) return f;
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
