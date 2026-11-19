using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class AddSketchDimensionResult
    {
        public string SketchName;
        public double RequestedValueMm;
        public double BeforeMm;   // the line's own measured length before the dimension is set (independent of the dim itself)
        public double AfterMm;    // the line's own measured length after — re-derived from the SKETCH, not the dim's own value
        public bool Applied;
        public bool AlreadyDone;
        public string Diag;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// AddSketchDimension (tool 83, "add_sketch_dimension") — draws a fresh, UNDIMENSIONED sketch line and adds a
    /// brand-new linear dimension to it with an explicit value: "add a 40mm dimension to a sketch line", "dimension
    /// the sketch to 25mm". Distinct from SetDimension (tool 63, which only EDITS a dimension that already exists —
    /// excludes change/set/adjust/modify/resize wording) and from AddSketchEntity (tool 82, which draws geometry but
    /// never dimensions it — that tool excludes "dim(ension)" wording so the two never shadow each other). Requires
    /// the literal word "sketch" so it never collides with AddDrawingDimension (tool 109, drawing-only).
    ///
    /// LANDMINE (instrumented live in the test fixture generator's MakeDimensionedHole, the same idiom reused
    /// here): by default AddDimension2 pops the interactive "Modify" value box and BLOCKS headless automation
    /// forever — must toggle swUserPreferenceToggle_e.swInputDimValOnCreate OFF before the call and restore it after.
    ///
    /// Draws the line at a length DELIBERATELY different from the requested value (target+20mm), then sets the new
    /// dimension's SystemValue. Success is judged by an INDEPENDENT re-measure of the line's own endpoints (re-read
    /// fresh off the exited sketch's SketchSegment, not the live COM handle held during drawing, and not the
    /// dimension object's own read-back) — proving the dimension actually DRIVES the geometry rather than merely
    /// existing alongside it. Equation-linking ("with... equation link" in the tool brief) is out of scope for this
    /// pass — an honest documented scope limit; only an explicit numeric value is supported. Idempotent via a single
    /// "Forge-SketchDim" sketch name tag (Rule #5); never saves.
    /// </summary>
    public static class AddSketchDimension
    {
        private const double MM = 0.001;
        private const string TagName = "Forge-SketchDim";
        private const double DefaultValueMm = 30.0;

        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (!Regex.IsMatch(c, @"\bsketch\b")) return false;
            if (Regex.IsMatch(c, @"\b(change|set|adjust|update|modify|resize|upsize)\b")) return false; // SetDimension's territory
            if (Regex.IsMatch(c, @"\b(relation|coincident|perpendicular|tangent|concentric|collinear)\b")) return false; // add_sketch_relation's territory
            if (Regex.IsMatch(c, @"\b(exit|close|finish|done)\b")) return false; // exit_sketch's territory
            bool verb = Regex.IsMatch(c, @"\b(add|insert|create|put|dimension)\b");
            bool dimWord = Regex.IsMatch(c, @"\bdim(ension)?s?\b");
            return verb && dimWord;
        }

        public static async Task<AddSketchDimensionResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new AddSketchDimensionResult();
            var part = model as PartDoc;
            if (part == null) { res.Error = "Open a part (.SLDPRT) to dimension a sketch on."; return res; }

            double targetMm = ParseValueMm(intent) ?? DefaultValueMm;
            res.RequestedValueMm = targetMm;

            var existing = FindTaggedFeature(model);
            if (existing != null)
            {
                res.AlreadyDone = true; res.Applied = true; res.SketchName = SafeName(existing);
                res.AfterMm = res.BeforeMm = MeasureFirstLineMm(existing);
                res.Info = "A dimensioned sketch (" + res.SketchName + ") is already here — nothing to do.";
                await emit("Dimensioner", null, "done", res.SketchName + " already present — nothing to do");
                return res;
            }

            await emit("Dimensioner", "sketching an undimensioned line", "run", null);

            double initialLenMm = targetMm + 20.0;   // deliberately different so the drive-through is provable

            var beforeNames = new HashSet<string>(SketchFeatureNames(model));
            SketchSegment seg = null;
            try
            {
                SelectPlane(model, "Front Plane");
                var sm = model.SketchManager;
                sm.InsertSketch(true);
                seg = sm.CreateLine(0, 0, 0, initialLenMm * MM, 0, 0) as SketchSegment;
            }
            catch (Exception ex) { res.Error = "Drawing the line failed: " + ex.Message; return res; }
            if (seg == null) { try { model.SketchManager.InsertSketch(true); } catch { } res.Error = "The sketch line wasn't created."; return res; }

            res.BeforeMm = LineLengthMm(seg);

            await emit("Dimensioner", "adding a " + Trim(targetMm) + "mm dimension", "run", null);

            bool prevToggle = true;
            try { prevToggle = app.GetUserPreferenceToggle((int)swUserPreferenceToggle_e.swInputDimValOnCreate); } catch { }
            try { app.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swInputDimValOnCreate, false); } catch { }
            DisplayDimension createdDim = null;
            try
            {
                model.ClearSelection2(true);
                seg.Select4(false, null);
                createdDim = model.AddDimension2(initialLenMm * MM / 2.0, 0.008, 0) as DisplayDimension;
                model.ClearSelection2(true);
            }
            catch (Exception ex) { res.Error = "AddDimension2 failed: " + ex.Message; }
            finally { try { app.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swInputDimValOnCreate, prevToggle); } catch { } }

            if (createdDim == null)
            {
                try { model.SketchManager.InsertSketch(true); } catch { }   // exit the sketch even on failure
                if (res.Error == null) res.Error = "SolidWorks didn't create a dimension on the new line.";
                await emit("Dimensioner", null, "fail", res.Error);
                return res;
            }

            try
            {
                var dim = createdDim.GetDimension2(0) as Dimension;
                if (dim != null) dim.SystemValue = targetMm * MM;
            }
            catch (Exception ex) { res.Error = "Couldn't set the new dimension's value: " + ex.Message; }

            try { model.SketchManager.InsertSketch(true); } catch { }   // exit the sketch
            try { model.ForceRebuild3(false); } catch { }

            var created = NewSketchFeature(model, beforeNames);
            if (created == null) { res.Error = "The new sketch feature was not found after exit."; return res; }
            try { created.Name = TagName; } catch { }
            res.SketchName = SafeName(created);

            // ---- FAIL CLOSED: re-measure the line by walking the EXITED sketch feature's own segments fresh (a
            // different path than the live `seg` handle held during drawing) — proves the dim actually DROVE the
            // geometry, not merely a mirror of the write we just made. ----
            res.AfterMm = MeasureFirstLineMm(created);
            res.Applied = res.AfterMm > 0 && Math.Abs(res.AfterMm - targetMm) <= 0.01 && Math.Abs(res.BeforeMm - targetMm) > 0.01;
            res.Diag = "before=" + Trim(res.BeforeMm) + "mm after=" + Trim(res.AfterMm) + "mm target=" + Trim(targetMm) + "mm name=" + res.SketchName;

            await emit("Dimensioner", null, res.Applied ? "done" : "fail", res.Applied ? "line now " + Trim(res.AfterMm) + "mm" : "dimension didn't drive the geometry");

            res.Info = res.Applied
                ? "Added a " + Trim(targetMm) + "mm dimension to a new sketch line (" + res.SketchName + ") — the line measures " + Trim(res.AfterMm) + "mm, confirmed independently. One Ctrl+Z removes it; Forge didn't save."
                : "The dimension didn't drive the line to " + Trim(targetMm) + "mm (measured " + Trim(res.AfterMm) + "mm) — the write may not have taken.";
            return res;
        }

        // ---------- parsing ----------
        private static double? ParseValueMm(string intent)
        {
            string c = (intent ?? "").ToLowerInvariant();
            var m = Regex.Match(c, @"(\d+(\.\d+)?)\s*mm\b");
            if (m.Success && double.TryParse(m.Groups[1].Value, out double v) && v > 0) return v;
            m = Regex.Match(c, @"(?<![a-z0-9])(\d+(\.\d+)?)\b");
            if (m.Success && double.TryParse(m.Groups[1].Value, out v) && v > 0) return v;
            return null;
        }

        // ---------- geometry helpers ----------
        private static void SelectPlane(IModelDoc2 model, string plane)
        { try { model.Extension.SelectByID2(plane, "PLANE", 0, 0, 0, false, 0, null, 0); } catch { } }

        private static double LineLengthMm(SketchSegment seg)
        {
            try
            {
                var line = seg as SketchLine;
                if (line == null) return -1;
                var p0 = line.GetStartPoint2() as SketchPoint;
                var p1 = line.GetEndPoint2() as SketchPoint;
                if (p0 == null || p1 == null) return -1;
                double dx = p1.X - p0.X, dy = p1.Y - p0.Y, dz = p1.Z - p0.Z;
                return Math.Sqrt(dx * dx + dy * dy + dz * dz) * 1000.0;
            }
            catch { return -1; }
        }

        // re-derives the length of the first non-construction line segment straight off a sketch FEATURE (used
        // after exiting the sketch, when the live SketchSegment handle held during drawing may be stale).
        private static double MeasureFirstLineMm(Feature sketchFeature)
        {
            try
            {
                var sk = sketchFeature.GetSpecificFeature2() as Sketch;
                if (sk == null) return -1;
                foreach (var o in (sk.GetSketchSegments() as object[]) ?? new object[0])
                {
                    var seg = o as SketchSegment; if (seg == null) continue;
                    bool constr = false; try { constr = seg.ConstructionGeometry; } catch { }
                    if (constr) continue;
                    double len = LineLengthMm(seg);
                    if (len > 0) return len;
                }
            }
            catch { }
            return -1;
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

        private static string Trim(double v) => v.ToString("0.###");
    }
}
