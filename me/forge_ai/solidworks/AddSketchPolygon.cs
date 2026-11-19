using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class AddSketchPolygonResult
    {
        public bool Success;
        public int Sides;
        public int LineSegments;   // the polygon's straight sides (verification)
        public bool AlreadyDone;
        public string SketchName;
        public string Diag;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 196 — add_sketch_polygon. Drops a regular N-gon sketch on the Front plane (SketchManager.CreatePolygon,
    /// the same live entity-creation path the fixture generators use). Names the resulting sketch feature
    /// "Forge-Polygon-N" so a rerun is a no-op (Rule #5), verifies by counting the N straight sides, and never saves —
    /// one Ctrl+Z removes it. Sides parsed from the words (hexagon=6, pentagon=5, octagon=8, "n-sided" / "n sides").
    /// </summary>
    public static class AddSketchPolygon
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (Regex.IsMatch(c, @"\b(remove|delete|suppress)\b")) return false;
            bool verb = Regex.IsMatch(c, @"\b(add|draw|create|sketch|insert|make)\b");
            bool noun = Regex.IsMatch(c, @"\b(polygon|hexagon|pentagon|octagon|heptagon|nonagon|decagon|n-?gon|n-?sided)\b");
            return verb && noun || Regex.IsMatch(c, @"\b(polygon|hexagon|pentagon|octagon) sketch\b");
        }

        public static async Task<AddSketchPolygonResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new AddSketchPolygonResult();
            var part = model as PartDoc;
            if (part == null) { res.Error = "Open a part (.SLDPRT) to add a polygon sketch."; return res; }
            res.Sides = ParseSides(intent);

            // idempotency: a Forge-Polygon sketch already present -> nothing to do.
            var existing = FindPolygonFeature(model);
            if (existing != null)
            {
                res.AlreadyDone = true; res.Success = true; res.SketchName = SafeName(existing);
                res.LineSegments = CountLines(existing); res.Sides = res.LineSegments;
                res.Info = "A polygon sketch (" + res.SketchName + ", " + res.LineSegments + " sides) is already here — nothing to do.";
                return res;
            }

            await emit("Draftsman", "sketching a " + res.Sides + "-sided polygon", "run", null);

            var before = new HashSet<string>(SketchFeatureNames(model));
            try
            {
                SelectPlane(model, "Front Plane");
                var sm = model.SketchManager;
                sm.InsertSketch(true);
                sm.CreatePolygon(0, 0, 0, 0.02, 0, 0, res.Sides, true);   // 20mm circum-radius, inscribed
                sm.InsertSketch(true);                                     // close/commit
                model.ClearSelection2(true);
                model.ForceRebuild3(false);
            }
            catch (Exception ex) { res.Error = "Sketch creation failed: " + ex.Message; return res; }

            var created = NewSketchFeature(model, before);
            if (created == null) { res.Error = "The polygon sketch was not created."; return res; }
            string wanted = "Forge-Polygon-" + res.Sides;
            try { created.Name = wanted; } catch { }
            res.SketchName = SafeName(created);
            res.LineSegments = CountLines(created);
            res.Success = res.LineSegments == res.Sides;
            res.Diag = "sides=" + res.Sides + " lineSegments=" + res.LineSegments + " name=" + res.SketchName;

            await emit("Draftsman", null, "done", res.Success ? (res.Sides + "-sided polygon sketched") : ("polygon has " + res.LineSegments + " sides, expected " + res.Sides));

            res.Info = res.Success
                ? "Added a " + res.Sides + "-sided polygon sketch (" + res.SketchName + ") on the Front plane. Undo removes it; nothing was saved."
                : "The polygon sketched with " + res.LineSegments + " sides, expected " + res.Sides + " — check the model.";
            return res;
        }

        private static int ParseSides(string intent)
        {
            string c = (intent ?? "").ToLowerInvariant();
            if (Regex.IsMatch(c, @"\bpentagon\b")) return 5;
            if (Regex.IsMatch(c, @"\bhexagon\b")) return 6;
            if (Regex.IsMatch(c, @"\bheptagon\b")) return 7;
            if (Regex.IsMatch(c, @"\boctagon\b")) return 8;
            if (Regex.IsMatch(c, @"\bnonagon\b")) return 9;
            if (Regex.IsMatch(c, @"\bdecagon\b")) return 10;
            var m = Regex.Match(c, @"(\d+)\s*(-?\s*(sided|gon|sides))");
            if (m.Success) { int n; if (int.TryParse(m.Groups[1].Value, out n) && n >= 3 && n <= 100) return n; }
            return 6;   // sensible default
        }

        private static Feature FindPolygonFeature(IModelDoc2 model)
        {
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                string nm = SafeName(f);
                if (nm != null && nm.StartsWith("Forge-Polygon", StringComparison.OrdinalIgnoreCase)) return f;
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
                if (tn == "ProfileFeature" && !before.Contains(SafeName(f))) found = f;   // last new one
                f = f.GetNextFeature() as Feature;
            }
            return found;
        }

        private static int CountLines(Feature sketchFeat)
        {
            int lines = 0;
            try
            {
                var sk = sketchFeat.GetSpecificFeature2() as Sketch;
                if (sk == null) return 0;
                foreach (var o in (sk.GetSketchSegments() as object[]) ?? new object[0])
                {
                    var seg = o as SketchSegment; if (seg == null) continue;
                    int t = -1; try { t = seg.GetType(); } catch { }
                    bool constr = false; try { constr = seg.ConstructionGeometry; } catch { }
                    if (t == (int)swSketchSegments_e.swSketchLINE && !constr) lines++;
                }
            }
            catch { }
            return lines;
        }

        private static string SafeName(Feature f) { try { return f.Name; } catch { return null; } }
        private static void SelectPlane(IModelDoc2 model, string plane)
        { try { model.Extension.SelectByID2(plane, "PLANE", 0, 0, 0, false, 0, null, 0); } catch { } }
    }
}
