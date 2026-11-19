using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class TrimExtendSketchResult
    {
        public bool Success;
        public int Segments;         // non-construction segments after trim (still 2: one line shortened)
        public bool TrimApplied;     // SketchTrim return
        public double LenBeforeMm;   // total non-construction length before trim (expect 200mm = two 100mm lines)
        public double LenAfterMm;    // total after trim (expect ~150mm = right half of the horizontal line gone)
        public bool AlreadyDone;
        public string SketchName;
        public string Diag;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 201 — trim_extend. Seeds two crossing lines (CreateLine, proven-live) forming a plus, then trims the
    /// right dangling half of the horizontal line (ISketchManager.SketchTrim — a sketch OPERATION, unproven headless).
    /// Like offset_sketch_entities it INSTRUMENTS the bool return + an independent total-length recount and fails CLOSED
    /// if the trim no-ops (cf. ConvertEntities/FullyDefineSketch no-ops). Known truth: 200mm -> ~150mm (0.05m removed).
    /// Names the sketch "Forge-Trim" for idempotency; never saves.
    /// </summary>
    public static class TrimExtendSketch
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            bool verb = Regex.IsMatch(c, @"\b(trim|extend)\b");
            bool noun = Regex.IsMatch(c, @"\b(sketch|entit|line|segment|geometry|curve)\b");
            return verb && noun;
        }

        public static async Task<TrimExtendSketchResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new TrimExtendSketchResult();
            var part = model as PartDoc;
            if (part == null) { res.Error = "Open a part (.SLDPRT) to trim a sketch."; return res; }

            var existing = FindTrimFeature(model);
            if (existing != null)
            {
                res.AlreadyDone = true; res.Success = true; res.SketchName = SafeName(existing);
                res.Segments = CountSegments(existing);
                res.LenAfterMm = Math.Round(TotalLength(existing) * 1000.0, 2);
                res.Info = "A trimmed sketch (" + res.SketchName + ") is already here — nothing to do.";
                return res;
            }

            await emit("Draftsman", "trimming a sketch entity", "run", null);

            var before = new HashSet<string>(SketchFeatureNames(model));
            double lenBefore = -1, lenAfter = -1;
            try
            {
                SelectPlane(model, "Front Plane");
                var sm = model.SketchManager;
                sm.InsertSketch(true);
                // a plus: horizontal line crosses vertical line at the origin (each 100mm long)
                var hz = sm.CreateLine(-0.05, 0, 0, 0.05, 0, 0) as SketchSegment;
                sm.CreateLine(0, -0.05, 0, 0, 0.05, 0);
                lenBefore = ActiveTotalLength(sm);
                // LANDMINE: SketchTrim only takes effect when the target segment is PRE-SELECTED, and it returns
                // FALSE even on a successful trim (a "return code lies" API, like AddMate5 NoError=1). So we judge
                // success by GEOMETRY (total length dropped), never by the bool. Trim the right half of the
                // horizontal line back to the origin crossing (known truth: 200mm -> ~150mm).
                model.ClearSelection2(true);
                if (hz != null) { try { hz.Select4(false, null); } catch { } }
                bool rawRet = false;
                try { rawRet = sm.SketchTrim((int)swSketchTrimChoice_e.swSketchTrimClosest, 0.03, 0, 0); } catch { }
                lenAfter = ActiveTotalLength(sm);
                res.TrimApplied = lenAfter < lenBefore - 0.001;   // geometry is the truth, not rawRet
                res.Diag = "rawRet=" + rawRet;
                sm.InsertSketch(true);
                model.ClearSelection2(true);
                model.ForceRebuild3(false);
            }
            catch (Exception ex) { res.Error = "Trim failed: " + ex.Message; return res; }

            var created = NewSketchFeature(model, before);
            if (created == null) { res.Error = "The trimmed sketch was not created."; return res; }
            try { created.Name = "Forge-Trim"; } catch { }
            res.SketchName = SafeName(created);
            res.Segments = CountSegments(created);
            lenAfter = TotalLength(created);       // re-read from the committed feature
            res.LenBeforeMm = Math.Round(lenBefore * 1000.0, 2);
            res.LenAfterMm = Math.Round(lenAfter * 1000.0, 2);
            // success = the trim actually REMOVED length (a real op, not a no-op) and the sketch survives
            res.Success = res.TrimApplied && lenAfter > 0 && lenAfter < lenBefore - 0.001;
            res.Diag = "lenBeforeMm=" + res.LenBeforeMm + " trimRet=" + res.TrimApplied + " lenAfterMm=" + res.LenAfterMm + " segs=" + res.Segments + " name=" + res.SketchName + " | " + res.Diag;

            await emit("Draftsman", null, "done", res.Success ? "sketch trimmed" : ("trimRet=" + res.TrimApplied + " len " + res.LenBeforeMm + "->" + res.LenAfterMm + "mm (expected a decrease)"));

            res.Info = res.Success
                ? "Trimmed a crossing line (" + res.SketchName + "): " + res.LenBeforeMm + "mm -> " + res.LenAfterMm + "mm. Undo removes it; nothing was saved."
                : "Trim did not take (SketchTrim returned " + res.TrimApplied + ", length " + res.LenBeforeMm + "mm -> " + res.LenAfterMm + "mm, expected a decrease) — the operation may be dead headless on this build.";
            return res;
        }

        private static double ActiveTotalLength(ISketchManager sm)
        {
            double total = 0;
            try
            {
                var sk = sm.ActiveSketch as Sketch;
                if (sk == null) return 0;
                foreach (var o in (sk.GetSketchSegments() as object[]) ?? new object[0])
                {
                    var seg = o as SketchSegment; if (seg == null) continue;
                    bool constr = false; try { constr = seg.ConstructionGeometry; } catch { }
                    if (constr) continue;
                    try { total += seg.GetLength(); } catch { }
                }
            }
            catch { }
            return total;
        }

        private static double TotalLength(Feature sketchFeat)
        {
            double total = 0;
            try
            {
                var sk = sketchFeat.GetSpecificFeature2() as Sketch;
                if (sk == null) return 0;
                foreach (var o in (sk.GetSketchSegments() as object[]) ?? new object[0])
                {
                    var seg = o as SketchSegment; if (seg == null) continue;
                    bool constr = false; try { constr = seg.ConstructionGeometry; } catch { }
                    if (constr) continue;
                    try { total += seg.GetLength(); } catch { }
                }
            }
            catch { }
            return total;
        }

        private static int CountSegments(Feature sketchFeat)
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
                    if (!constr) n++;
                }
            }
            catch { }
            return n;
        }

        private static Feature FindTrimFeature(IModelDoc2 model)
        {
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                string nm = SafeName(f);
                if (nm != null && nm.StartsWith("Forge-Trim", StringComparison.OrdinalIgnoreCase)) return f;
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
