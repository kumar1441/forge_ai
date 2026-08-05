using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class MirrorSketchEntitiesResult
    {
        public bool Success;
        public int Segments;         // non-construction segments after mirror (expect 2: circle + its mirror)
        public bool MirrorApplied;   // geometry-derived (SketchMirror is void; count must rise)
        public double CenterXSumMm;  // sum of the two arc X-centers (expect ~0: +30 and -30mm => symmetric)
        public bool AlreadyDone;
        public string SketchName;
        public string Diag;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 202 — mirror_sketch_entities. Seeds a circle offset to +X and a construction centerline on Y (both proven-live
    /// creation calls), selects the circle then the centerline, and mirrors (IModelDoc2.SketchMirror — void, a sketch
    /// OPERATION). Since SketchMirror has NO return, success is judged purely by GEOMETRY: the non-construction segment
    /// count rises 1 -> 2 and the two circle centers are symmetric (X-sum ~0). Fails CLOSED if the mirror no-ops (cf.
    /// ConvertEntities). Names the sketch "Forge-Mirror" for idempotency; never saves.
    /// </summary>
    public static class MirrorSketchEntities
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            bool verb = Regex.IsMatch(c, @"\b(mirror)\b");
            bool noun = Regex.IsMatch(c, @"\b(sketch|entit|circle|profile|geometry|curve|line|segment)\b");
            return verb && noun;
        }

        public static async Task<MirrorSketchEntitiesResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new MirrorSketchEntitiesResult();
            var part = model as PartDoc;
            if (part == null) { res.Error = "Open a part (.SLDPRT) to mirror a sketch."; return res; }

            var existing = FindMirrorFeature(model);
            if (existing != null)
            {
                res.AlreadyDone = true; res.Success = true; res.SketchName = SafeName(existing);
                res.Segments = CountSegments(existing);
                res.Info = "A mirrored sketch (" + res.SketchName + ") is already here — nothing to do.";
                return res;
            }

            await emit("Draftsman", "mirroring a sketch entity", "run", null);

            var before = new HashSet<string>(SketchFeatureNames(model));
            int segsBefore = -1; bool rawMirrorOk = false;
            try
            {
                SelectPlane(model, "Front Plane");
                var sm = model.SketchManager;
                sm.InsertSketch(true);
                var axis = sm.CreateCenterLine(0, -0.05, 0, 0, 0.05, 0) as SketchSegment;   // construction mirror axis
                var circle = sm.CreateCircleByRadius(0.03, 0, 0, 0.010) as SketchSegment;    // 10mm circle at +30mm
                segsBefore = CountActiveSegments(sm);
                // select the entity to mirror, then the centerline LAST (append) — the SketchMirror convention
                model.ClearSelection2(true);
                if (circle != null) circle.Select4(false, null);
                if (axis != null) axis.Select4(true, null);
                model.SketchMirror();
                rawMirrorOk = CountActiveSegments(sm) > segsBefore;
                sm.InsertSketch(true);
                model.ClearSelection2(true);
                model.ForceRebuild3(false);
            }
            catch (Exception ex) { res.Error = "Mirror failed: " + ex.Message; return res; }

            var created = NewSketchFeature(model, before);
            if (created == null) { res.Error = "The mirrored sketch was not created."; return res; }
            try { created.Name = "Forge-Mirror"; } catch { }
            res.SketchName = SafeName(created);
            res.Segments = CountSegments(created);
            res.CenterXSumMm = Math.Round(ArcCenterXSum(created) * 1000.0, 3);
            res.MirrorApplied = res.Segments == 2;   // circle + its mirror (centerline is construction, excluded)
            res.Success = res.MirrorApplied && Math.Abs(res.CenterXSumMm) < 0.1;
            res.Diag = "segsBefore(active)=" + segsBefore + " rawMirrorOk=" + rawMirrorOk + " segsAfter=" + res.Segments + " centerXSumMm=" + res.CenterXSumMm + " name=" + res.SketchName;

            await emit("Draftsman", null, "done", res.Success ? "sketch mirrored" : ("segs=" + res.Segments + " (expected 2) centerXSum=" + res.CenterXSumMm + "mm"));

            res.Info = res.Success
                ? "Mirrored a circle across a centerline (" + res.SketchName + "): 2 symmetric circles. Undo removes it; nothing was saved."
                : "Mirror did not take (segments=" + res.Segments + ", expected 2; centerX-sum=" + res.CenterXSumMm + "mm) — the operation may be dead headless on this build.";
            return res;
        }

        private static int CountActiveSegments(ISketchManager sm)
        {
            int n = 0;
            try
            {
                var sk = sm.ActiveSketch as Sketch;
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

        // sum the X-centers of the non-construction arc/circle segments (two symmetric circles => ~0)
        private static double ArcCenterXSum(Feature sketchFeat)
        {
            double sum = 0;
            try
            {
                var sk = sketchFeat.GetSpecificFeature2() as Sketch;
                if (sk == null) return 0;
                foreach (var o in (sk.GetSketchSegments() as object[]) ?? new object[0])
                {
                    var seg = o as SketchSegment; if (seg == null) continue;
                    bool constr = false; try { constr = seg.ConstructionGeometry; } catch { }
                    if (constr) continue;
                    var arc = seg as SketchArc;
                    if (arc != null)
                    {
                        var c = arc.GetCenterPoint2() as double[];
                        if (c != null && c.Length >= 1) sum += c[0];
                    }
                }
            }
            catch { }
            return sum;
        }

        private static Feature FindMirrorFeature(IModelDoc2 model)
        {
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                string nm = SafeName(f);
                if (nm != null && nm.StartsWith("Forge-Mirror", StringComparison.OrdinalIgnoreCase)) return f;
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
