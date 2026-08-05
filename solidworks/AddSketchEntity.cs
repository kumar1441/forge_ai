using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class AddSketchEntityResult
    {
        public string Kind;           // "circle" | "line" | "rectangle" | "point"
        public string PlaneName;
        public string SketchName;
        public int SegmentsBefore;
        public int SegmentsAfter;
        public int PointsBefore;
        public int PointsAfter;
        public bool Applied;          // geometry-derived (the bool return can lie; count must rise)
        public bool AlreadyDone;
        public string Diag;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// AddSketchEntity (tool 82, "add_sketch_entity") — draws ONE basic sketch entity (a line, circle, rectangle,
    /// or point) at explicit coordinates in a NEW sketch on a named plane: "sketch a 20mm circle at 10,20", "draw
    /// a line from 0,0 to 50,0", "add a rectangle from -10,-10 to 10,10 on the top plane", "add a point at 5,5".
    ///
    /// Distinct from the shape-specific siblings (AddSketchArc/Ellipse/Polygon/Slot/Spline/Text — arc/ellipse/
    /// polygon/slot/spline/text words are excluded here) and from PatternSketchEntities/MirrorSketchEntities/
    /// OffsetSketchEntities/TrimExtendSketch (those require pattern/mirror/offset/trim verbs, never present here).
    ///
    /// The bool return of the Create* calls can lie (cf. SketchTrim), so success is judged purely by GEOMETRY: for
    /// line/circle/rectangle, the non-construction segment count must rise; for point, the sketch point count must
    /// rise. Fails CLOSED if the entity didn't actually appear. Names the new sketch "Forge-SketchEntity" so a
    /// rerun is a no-op (Rule #5, same shape as AddSketchArc's "Forge-Arc"); never saves.
    /// </summary>
    public static class AddSketchEntity
    {
        private const double MM = 0.001;
        private const double DefaultRadiusMm = 5.0;
        private const string TagName = "Forge-SketchEntity";

        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (Regex.IsMatch(c, @"\b(arc|ellipse|polygon|slot|spline|text)\b")) return false;
            if (Regex.IsMatch(c, @"\b(pattern|mirror|offset|trim|extend|relation|dimension|dim)\b")) return false;
            if (Regex.IsMatch(c, @"\b(component|components|bolts?|nuts?|screws?|fasteners?)\b")) return false;
            bool verb = Regex.IsMatch(c, @"\b(sketch|draw|add)\b");
            bool shape = Regex.IsMatch(c, @"\b(circle|line|rectangle|rect|point)\b");
            bool coords = Regex.IsMatch(c, @"-?\d+(\.\d+)?\s*,\s*-?\d+(\.\d+)?");
            return verb && shape && coords;
        }

        public static async Task<AddSketchEntityResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new AddSketchEntityResult();
            var part = model as PartDoc;
            if (part == null) { res.Error = "Open a part (.SLDPRT) to sketch on."; return res; }

            string kind = ParseKind(intent);
            if (kind == null) { res.Error = "What shape? Say circle, line, rectangle, or point."; return res; }
            res.Kind = kind;

            string plane = ParsePlane(intent);
            res.PlaneName = plane;

            var existing = FindTaggedFeature(model);
            if (existing != null)
            {
                res.AlreadyDone = true; res.Applied = true; res.SketchName = SafeName(existing);
                res.SegmentsAfter = res.SegmentsBefore = CountAllSegments(model);
                res.PointsAfter = res.PointsBefore = CountAllPoints(model);
                res.Info = "A sketch entity (" + res.SketchName + ") is already here — nothing to do.";
                await emit("Draftsman", null, "done", res.SketchName + " already present — nothing to do");
                return res;
            }

            await emit("Draftsman", "sketching a " + kind + " on " + plane, "run", null);

            var beforeNames = new HashSet<string>(SketchFeatureNames(model));
            res.SegmentsBefore = CountAllSegments(model);
            res.PointsBefore = CountAllPoints(model);

            try
            {
                SelectPlane(model, plane);
                var sm = model.SketchManager;
                sm.InsertSketch(true);

                switch (kind)
                {
                    case "circle":
                    {
                        double[] c0 = ParseFirstPair(intent) ?? new[] { 0.0, 0.0 };
                        double r = ParseRadiusMm(intent) * MM;
                        sm.CreateCircleByRadius(c0[0] * MM, c0[1] * MM, 0, r);
                        break;
                    }
                    case "line":
                    {
                        var pairs = ParseAllPairs(intent);
                        double[] p1 = pairs.Count > 0 ? pairs[0] : new[] { 0.0, 0.0 };
                        double[] p2 = pairs.Count > 1 ? pairs[1] : new[] { 50.0, 0.0 };
                        sm.CreateLine(p1[0] * MM, p1[1] * MM, 0, p2[0] * MM, p2[1] * MM, 0);
                        break;
                    }
                    case "rectangle":
                    {
                        var pairs = ParseAllPairs(intent);
                        double[] p1 = pairs.Count > 0 ? pairs[0] : new[] { -10.0, -10.0 };
                        double[] p2 = pairs.Count > 1 ? pairs[1] : new[] { 10.0, 10.0 };
                        sm.CreateCornerRectangle(p1[0] * MM, p1[1] * MM, 0, p2[0] * MM, p2[1] * MM, 0);
                        break;
                    }
                    case "point":
                    {
                        double[] p0 = ParseFirstPair(intent) ?? new[] { 0.0, 0.0 };
                        sm.CreatePoint(p0[0] * MM, p0[1] * MM, 0);
                        break;
                    }
                }

                sm.InsertSketch(true);
                model.ClearSelection2(true);
                model.ForceRebuild3(false);
            }
            catch (Exception ex) { res.Error = kind + " sketch failed: " + ex.Message; return res; }

            var created = NewSketchFeature(model, beforeNames);
            if (created == null) { res.Error = "The new sketch was not created."; return res; }
            try { created.Name = TagName; } catch { }
            res.SketchName = SafeName(created);
            res.SegmentsAfter = CountAllSegments(model);
            res.PointsAfter = CountAllPoints(model);

            res.Applied = kind == "point"
                ? res.PointsAfter > res.PointsBefore
                : res.SegmentsAfter > res.SegmentsBefore;
            res.Diag = "kind=" + kind + " plane=" + plane + " segBefore=" + res.SegmentsBefore + " segAfter=" + res.SegmentsAfter +
                       " ptBefore=" + res.PointsBefore + " ptAfter=" + res.PointsAfter + " name=" + res.SketchName;

            await emit("Draftsman", null, res.Applied ? "done" : "fail", res.Applied ? kind + " sketched" : "no geometry change");

            res.Info = res.Applied
                ? "Sketched a " + kind + " on " + plane + " (" + res.SketchName + "). One Ctrl+Z removes it; Forge didn't save."
                : "The " + kind + " didn't take (no segment/point count change) — the operation may be dead headless on this build.";
            return res;
        }

        // ---------- parsing ----------
        private static string ParseKind(string intent)
        {
            string c = (intent ?? "").ToLowerInvariant();
            if (Regex.IsMatch(c, @"\bcircle\b")) return "circle";
            if (Regex.IsMatch(c, @"\b(rectangle|rect)\b")) return "rectangle";
            if (Regex.IsMatch(c, @"\bline\b")) return "line";
            if (Regex.IsMatch(c, @"\bpoint\b")) return "point";
            return null;
        }

        private static string ParsePlane(string intent)
        {
            string c = (intent ?? "").ToLowerInvariant();
            if (Regex.IsMatch(c, @"\btop\s*plane\b")) return "Top Plane";
            if (Regex.IsMatch(c, @"\bright\s*plane\b")) return "Right Plane";
            return "Front Plane";
        }

        private static List<double[]> ParseAllPairs(string intent)
        {
            var list = new List<double[]>();
            var ms = Regex.Matches(intent ?? "", @"(-?\d+(?:\.\d+)?)\s*,\s*(-?\d+(?:\.\d+)?)");
            foreach (Match m in ms)
            {
                double x, y;
                if (double.TryParse(m.Groups[1].Value, out x) && double.TryParse(m.Groups[2].Value, out y))
                    list.Add(new[] { x, y });
            }
            return list;
        }

        private static double[] ParseFirstPair(string intent)
        {
            var all = ParseAllPairs(intent);
            return all.Count > 0 ? all[0] : null;
        }

        private static double ParseRadiusMm(string intent)
        {
            string c = (intent ?? "").ToLowerInvariant();
            var m = Regex.Match(c, @"(\d+(\.\d+)?)\s*mm\s*(radius|rad)\b");
            double v;
            if (m.Success && double.TryParse(m.Groups[1].Value, out v) && v > 0) return v;
            m = Regex.Match(c, @"(?:radius|rad)\s*(?:of\s*)?(\d+(\.\d+)?)\s*mm");
            if (m.Success && double.TryParse(m.Groups[1].Value, out v) && v > 0) return v;
            m = Regex.Match(c, @"(\d+(\.\d+)?)\s*mm\s*(diameter|dia)\b");
            if (m.Success && double.TryParse(m.Groups[1].Value, out v) && v > 0) return v / 2.0;
            m = Regex.Match(c, @"(?:diameter|dia)\s*(?:of\s*)?(\d+(\.\d+)?)\s*mm");
            if (m.Success && double.TryParse(m.Groups[1].Value, out v) && v > 0) return v / 2.0;
            m = Regex.Match(c, @"(\d+(\.\d+)?)\s*mm\b");
            if (m.Success && double.TryParse(m.Groups[1].Value, out v) && v > 0) return v;
            return DefaultRadiusMm;
        }

        // ---------- geometry helpers ----------
        private static void SelectPlane(IModelDoc2 model, string plane)
        { try { model.Extension.SelectByID2(plane, "PLANE", 0, 0, 0, false, 0, null, 0); } catch { } }

        // whole-part tallies (a fresh part may already have other sketches; count everything so a rise is unambiguous)
        private static int CountAllSegments(IModelDoc2 model)
        {
            int n = 0;
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                string tn = ""; try { tn = f.GetTypeName2(); } catch { }
                if (tn == "ProfileFeature")
                {
                    var sk = f.GetSpecificFeature2() as Sketch;
                    if (sk != null)
                        foreach (var o in (sk.GetSketchSegments() as object[]) ?? new object[0])
                        {
                            var seg = o as SketchSegment; if (seg == null) continue;
                            bool constr = false; try { constr = seg.ConstructionGeometry; } catch { }
                            if (!constr) n++;
                        }
                }
                f = f.GetNextFeature() as Feature;
            }
            return n;
        }

        private static int CountAllPoints(IModelDoc2 model)
        {
            int n = 0;
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                string tn = ""; try { tn = f.GetTypeName2(); } catch { }
                if (tn == "ProfileFeature")
                {
                    var sk = f.GetSpecificFeature2() as Sketch;
                    if (sk != null) { try { n += sk.GetSketchPointsCount2(); } catch { } }
                }
                f = f.GetNextFeature() as Feature;
            }
            return n;
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

        private static Feature FindTaggedFeature(IModelDoc2 model)
        {
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                string nm = SafeName(f);
                if (nm != null && nm.Equals(TagName, StringComparison.OrdinalIgnoreCase)) return f;
                f = f.GetNextFeature() as Feature;
            }
            return null;
        }

        private static string SafeName(Feature f) { try { return f.Name; } catch { return null; } }
    }
}
