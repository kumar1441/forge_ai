using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class AddSketchRelationResult
    {
        public string RelationType;   // "parallel" | "perpendicular" | "equal" | "collinear" | "coincident"
        public string SketchName;
        public double BeforeMetric;   // relation-specific measurement BEFORE the relation (deg or mm)
        public double AfterMetric;    // the SAME measurement AFTER — re-derived from the EXITED sketch, not the live handles
        public bool Applied;
        public bool AlreadyDone;
        public string Diag;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// AddSketchRelation (tool 84, "add_sketch_relation") — draws two fresh sketch LINES that deliberately do NOT
    /// already satisfy the target relation, then constrains them: "add a parallel relation between two sketch
    /// lines", "make these sketch lines perpendicular", "add an equal relation to the sketch", "make the sketch
    /// lines collinear", "add a coincident relation to the sketch". Supports parallel/perpendicular/equal(-length)/
    /// collinear/coincident — a representative subset of the tool brief's "coincident/parallel/equal/etc.", the
    /// same scoping precedent as AddSketchEntity covering line/circle/rect/point rather than every possible noun.
    ///
    /// Distinct from SetDimension (edit verbs) and AddSketchDimension (dim/dimension wording) — this owns
    /// relation/constraint wording and the relation-type nouns themselves, so the three sketch-write siblings never
    /// shadow each other.
    ///
    /// LANDMINE AVOIDED (documented in BUILD-LOG 2026-07-24, FullyDefineSketch session): the broad-brush
    /// auto-solve sketch APIs (ISketchManager.FullyDefineSketch, IModelDocExtension.SketchAddConstraints,
    /// ISketch.AutoDimension2) are confirmed SILENT NO-OPS headless on this build — do not use them. AddRelation
    /// is a DIFFERENT, granular API family (ISketch.RelationManager.AddRelation, the same "select/pass exact
    /// entities, apply one exact operation" shape as AddDimension2, already proven live this session in
    /// AddSketchDimension) — reflected live off the installed interop DLL before writing this handler, confirmed
    /// present with signature `SketchRelation AddRelation(Object Entities, Int32 RelationType)`, NOT the same
    /// symbol as the dead SketchAddConstraints.
    ///
    /// Success is judged by an INDEPENDENT geometric re-measure (angle between line directions / line lengths /
    /// point-to-infinite-line distance / endpoint-to-endpoint distance, depending on relation type) taken off the
    /// EXITED sketch feature's own segments — never the relation object's own existence, and never the live COM
    /// handles held during drawing. The BEFORE geometry is drawn deliberately off-target (a different angle/length/
    /// offset) so a rise from "violates the relation" to "satisfies the relation" is the actual proof, not a
    /// coincidental match. Idempotent via a single "Forge-SketchRelation" sketch name tag (Rule #5); never saves.
    /// </summary>
    public static class AddSketchRelation
    {
        private const string TagName = "Forge-SketchRelation";

        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (!Regex.IsMatch(c, @"\bsketch\b")) return false;
            if (Regex.IsMatch(c, @"\b(change|set|adjust|update|modify|resize|upsize)\b")) return false;   // SetDimension's territory
            if (Regex.IsMatch(c, @"\bdim(ension)?s?\b")) return false;                                     // AddSketchDimension's territory
            if (Regex.IsMatch(c, @"\b(exit|close|finish|done)\b")) return false;                           // exit_sketch's territory
            bool relWord = Regex.IsMatch(c, @"\b(relation|constraint)s?\b") ||
                           Regex.IsMatch(c, @"\b(coincident|parallel|perpendicular|equal|collinear|colinear)\b");
            if (!relWord) return false;
            bool verb = Regex.IsMatch(c, @"\b(add|insert|create|make|apply|put)\b");
            return verb;
        }

        public static async Task<AddSketchRelationResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new AddSketchRelationResult();
            var part = model as PartDoc;
            if (part == null) { res.Error = "Open a part (.SLDPRT) to add a sketch relation on."; return res; }

            string relName = ParseRelationType(intent);
            res.RelationType = relName;

            var existing = FindTaggedFeature(model);
            if (existing != null)
            {
                res.AlreadyDone = true; res.Applied = true; res.SketchName = SafeName(existing);
                res.Info = "A constrained sketch (" + res.SketchName + ") is already here — nothing to do.";
                await emit("Constrainer", null, "done", res.SketchName + " already present — nothing to do");
                return res;
            }

            await emit("Constrainer", "sketching two unrelated lines", "run", null);

            var beforeNames = new HashSet<string>(SketchFeatureNames(model));
            SketchLine lineA = null, lineB = null;
            try
            {
                SelectPlane(model, "Front Plane");
                var sm = model.SketchManager;
                sm.InsertSketch(true);
                lineA = sm.CreateLine(0, 0, 0, 0.030, 0, 0) as SketchLine;            // 30mm along X, angle 0
                lineB = sm.CreateLine(0, 0.010, 0, 0.020, 0.017, 0) as SketchLine;    // ~21.19mm, ~19.3deg, offset 10mm in Y
            }
            catch (Exception ex) { res.Error = "Drawing the lines failed: " + ex.Message; return res; }
            if (lineA == null || lineB == null)
            { try { model.SketchManager.InsertSketch(true); } catch { } res.Error = "The sketch lines weren't created."; return res; }

            res.BeforeMetric = Metric(relName, MeasureGeo(lineA, lineB));

            await emit("Constrainer", "applying a " + relName + " relation", "run", null);

            SketchRelation createdRel = null;
            try
            {
                var sk = model.SketchManager.ActiveSketch as Sketch;
                var relMgr = sk != null ? sk.RelationManager as SketchRelationManager : null;
                if (relMgr != null)
                {
                    object[] ents = relName == "coincident"
                        ? new object[] { lineA.GetEndPoint2() as SketchPoint, lineB.GetStartPoint2() as SketchPoint }
                        : new object[] { lineA, lineB };
                    createdRel = relMgr.AddRelation(ents, ConstraintType(relName)) as SketchRelation;
                }
            }
            catch (Exception ex) { res.Error = "AddRelation failed: " + ex.Message; }

            if (createdRel == null)
            {
                try { model.SketchManager.InsertSketch(true); } catch { }   // exit the sketch even on failure
                if (res.Error == null) res.Error = "SolidWorks didn't create the " + relName + " relation.";
                await emit("Constrainer", null, "fail", res.Error);
                return res;
            }

            try { model.SketchManager.InsertSketch(true); } catch { }   // exit the sketch
            try { model.ForceRebuild3(false); } catch { }

            var created = NewSketchFeature(model, beforeNames);
            if (created == null) { res.Error = "The new sketch feature was not found after exit."; return res; }
            try { created.Name = TagName; } catch { }
            res.SketchName = SafeName(created);

            // ---- FAIL CLOSED: re-measure by walking the EXITED sketch feature's own two non-construction line
            // segments fresh — proves the relation actually CONSTRAINED the geometry, not merely a mirror of the
            // write we just made. ----
            var afterPair = FindLinePair(created);
            res.AfterMetric = afterPair != null ? Metric(relName, MeasureGeo(afterPair[0], afterPair[1])) : -1;
            res.Applied = Verify(relName, res.BeforeMetric, res.AfterMetric);
            res.Diag = "before=" + Trim(res.BeforeMetric) + " after=" + Trim(res.AfterMetric) + " rel=" + relName + " name=" + res.SketchName;

            await emit("Constrainer", null, res.Applied ? "done" : "fail", res.Applied ? relName + " satisfied" : "relation didn't constrain the geometry");

            res.Info = res.Applied
                ? "Added a " + relName + " relation between two sketch lines (" + res.SketchName + ") — confirmed independently by re-measuring the geometry. One Ctrl+Z removes it; Forge didn't save."
                : "The " + relName + " relation didn't take (geometry unchanged from " + Trim(res.BeforeMetric) + " to " + Trim(res.AfterMetric) + ") — the write may not have taken.";
            return res;
        }

        // ---------- parsing ----------
        private static string ParseRelationType(string intent)
        {
            string c = (intent ?? "").ToLowerInvariant();
            if (Regex.IsMatch(c, @"\bperpendicular\b")) return "perpendicular";
            if (Regex.IsMatch(c, @"\bequal\b")) return "equal";
            if (Regex.IsMatch(c, @"\bcoincident\b")) return "coincident";
            if (Regex.IsMatch(c, @"\b(collinear|colinear)\b")) return "collinear";
            return "parallel";   // default — the most commonly requested relation
        }

        private static int ConstraintType(string relName)
        {
            switch (relName)
            {
                case "perpendicular": return (int)swConstraintType_e.swConstraintType_PERPENDICULAR;
                case "equal": return (int)swConstraintType_e.swConstraintType_SAMELENGTH;
                case "coincident": return (int)swConstraintType_e.swConstraintType_COINCIDENT;
                case "collinear": return (int)swConstraintType_e.swConstraintType_COLINEAR;
                default: return (int)swConstraintType_e.swConstraintType_PARALLEL;
            }
        }

        // ---------- geometry ----------
        private class Geo
        {
            public double AngleDeg;   // angle between the two lines' directions, 0 (parallel) .. 90 (perpendicular)
            public double Len1Mm, Len2Mm;
            public double LineDistMm; // max distance of lineB's two endpoints from lineA's infinite line
            public double PointDistMm; // distance between lineA's END point and lineB's START point
        }

        private static double Metric(string relName, Geo g)
        {
            switch (relName)
            {
                case "perpendicular": return g.AngleDeg;
                case "equal": return Math.Abs(g.Len1Mm - g.Len2Mm);
                case "coincident": return g.PointDistMm;
                case "collinear": return g.LineDistMm;
                default: return g.AngleDeg;   // parallel
            }
        }

        private static bool Verify(string relName, double before, double after)
        {
            if (after < 0) return false;
            const double epsMm = 0.02, epsDeg = 0.5;
            switch (relName)
            {
                case "perpendicular": return Math.Abs(after - 90.0) <= epsDeg && Math.Abs(before - 90.0) > 5.0;
                case "equal": return after <= epsMm && before > 1.0;
                case "coincident": return after <= epsMm && before > 1.0;
                case "collinear": return after <= epsMm && before > 1.0;
                default: return after <= epsDeg && before > 5.0;   // parallel
            }
        }

        private static Geo MeasureGeo(SketchLine a, SketchLine b)
        {
            var g = new Geo();
            var a0 = a.GetStartPoint2() as SketchPoint; var a1 = a.GetEndPoint2() as SketchPoint;
            var b0 = b.GetStartPoint2() as SketchPoint; var b1 = b.GetEndPoint2() as SketchPoint;
            if (a0 == null || a1 == null || b0 == null || b1 == null) return g;

            double ax = a1.X - a0.X, ay = a1.Y - a0.Y, az = a1.Z - a0.Z;
            double bx = b1.X - b0.X, by = b1.Y - b0.Y, bz = b1.Z - b0.Z;
            double la = Math.Sqrt(ax * ax + ay * ay + az * az), lb = Math.Sqrt(bx * bx + by * by + bz * bz);
            g.Len1Mm = la * 1000.0; g.Len2Mm = lb * 1000.0;

            if (la > 1e-9 && lb > 1e-9)
            {
                double dot = (ax * bx + ay * by + az * bz) / (la * lb);
                dot = Math.Max(-1.0, Math.Min(1.0, dot));
                g.AngleDeg = Math.Acos(Math.Abs(dot)) * 180.0 / Math.PI;   // 0 = parallel, 90 = perpendicular

                double ux = ax / la, uy = ay / la, uz = az / la;
                g.LineDistMm = Math.Max(PointLineDist(b0, a0, ux, uy, uz), PointLineDist(b1, a0, ux, uy, uz)) * 1000.0;
            }
            g.PointDistMm = Dist(a1, b0) * 1000.0;
            return g;
        }

        private static double PointLineDist(SketchPoint p, SketchPoint a0, double ux, double uy, double uz)
        {
            double px = p.X - a0.X, py = p.Y - a0.Y, pz = p.Z - a0.Z;
            double cx = py * uz - pz * uy, cy = pz * ux - px * uz, cz = px * uy - py * ux;
            return Math.Sqrt(cx * cx + cy * cy + cz * cz);
        }

        private static double Dist(SketchPoint a, SketchPoint b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        // finds the first two non-construction line segments in a sketch feature — used AFTER exiting the sketch,
        // when the live SketchLine handles held during drawing may be stale.
        private static SketchLine[] FindLinePair(Feature sketchFeature)
        {
            try
            {
                var sk = sketchFeature.GetSpecificFeature2() as Sketch;
                if (sk == null) return null;
                var found = new List<SketchLine>();
                foreach (var o in (sk.GetSketchSegments() as object[]) ?? new object[0])
                {
                    var line = o as SketchLine; if (line == null) continue;
                    var seg = o as SketchSegment;
                    bool constr = false; try { constr = seg != null && seg.ConstructionGeometry; } catch { }
                    if (constr) continue;
                    found.Add(line);
                    if (found.Count == 2) return found.ToArray();
                }
            }
            catch { }
            return null;
        }

        private static void SelectPlane(IModelDoc2 model, string plane)
        { try { model.Extension.SelectByID2(plane, "PLANE", 0, 0, 0, false, 0, null, 0); } catch { } }

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
