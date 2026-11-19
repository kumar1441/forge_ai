using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class PatternSketchEntitiesResult
    {
        public bool Success;
        public int Segments;          // non-construction segments after pattern (expect 3: seed + 2 copies)
        public bool PatternApplied;   // geometry-derived (the bool return can lie; count must rise)
        public bool AlreadyDone;
        public string SketchName;
        public string Diag;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 203 — pattern_sketch_entities. Seeds a circle at the origin (proven-live creation) and replicates it into a
    /// 3x1 LINEAR array via ISketchManager.CreateLinearSketchStepAndRepeat (a live sketch-family op like SketchMirror /
    /// SketchOffset2). The bool return can lie (cf. SketchTrim), so success is judged purely by GEOMETRY: the
    /// non-construction segment count rises 1 -> 3 (seed + 2 copies). Fails CLOSED if the pattern no-ops. Requires an
    /// explicit "sketch" noun so it never shadows pattern_feature / pattern_components. Names the sketch
    /// "Forge-SketchPattern" for idempotency; never saves.
    /// </summary>
    public static class PatternSketchEntities
    {
        private const int NumX = 3;
        private const double SpacingX = 0.020;   // 20mm pitch

        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (Regex.IsMatch(c, @"\b(component|components|bolts?|nuts?|screws?|fasteners?)\b")) return false;
            bool verb = Regex.IsMatch(c, @"\b(pattern|array|replicate|repeat|step\s*and\s*repeat)\b");
            bool noun = Regex.IsMatch(c, @"\bsketch\b");
            return verb && noun;
        }

        public static async Task<PatternSketchEntitiesResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new PatternSketchEntitiesResult();
            var part = model as PartDoc;
            if (part == null) { res.Error = "Open a part (.SLDPRT) to pattern a sketch."; return res; }

            var existing = FindPatternFeature(model);
            if (existing != null)
            {
                res.AlreadyDone = true; res.Success = true; res.SketchName = SafeName(existing);
                res.Segments = CountSegments(existing);
                res.Info = "A patterned sketch (" + res.SketchName + ") is already here — nothing to do.";
                return res;
            }

            await emit("Draftsman", "patterning a sketch entity (3x linear)", "run", null);

            var before = new HashSet<string>(SketchFeatureNames(model));
            int segsBefore = -1; bool rawOk = false;
            try
            {
                SelectPlane(model, "Front Plane");
                var sm = model.SketchManager;
                sm.InsertSketch(true);
                var circle = sm.CreateCircleByRadius(0, 0, 0, 0.005) as SketchSegment;   // 5mm seed circle at origin
                segsBefore = CountActiveSegments(sm);
                model.ClearSelection2(true);
                if (circle != null) circle.Select4(false, null);
                // 3 in X @ 20mm, 1 in Y, angles 0; no deleted instances, no dims.
                sm.CreateLinearSketchStepAndRepeat(NumX, 1, SpacingX, 0, 0, 0, "", false, false, false, false, false);
                rawOk = CountActiveSegments(sm) > segsBefore;
                sm.InsertSketch(true);
                model.ClearSelection2(true);
                model.ForceRebuild3(false);
            }
            catch (Exception ex) { res.Error = "Sketch pattern failed: " + ex.Message; return res; }

            var created = NewSketchFeature(model, before);
            if (created == null) { res.Error = "The patterned sketch was not created."; return res; }
            try { created.Name = "Forge-SketchPattern"; } catch { }
            res.SketchName = SafeName(created);
            res.Segments = CountSegments(created);
            res.PatternApplied = res.Segments == NumX;   // seed + 2 copies = 3 non-construction circles
            res.Success = res.PatternApplied;
            res.Diag = "segsBefore(active)=" + segsBefore + " rawOk=" + rawOk + " segsAfter=" + res.Segments + " expected=" + NumX + " name=" + res.SketchName;

            await emit("Draftsman", null, "done", res.Success ? "sketch patterned" : ("segs=" + res.Segments + " (expected " + NumX + ")"));

            res.Info = res.Success
                ? "Patterned a circle into a 3x1 linear array (" + res.SketchName + "): " + res.Segments + " circles. Undo removes it; nothing was saved."
                : "Sketch pattern did not take (segments=" + res.Segments + ", expected " + NumX + ") — the operation may be dead headless on this build.";
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

        private static Feature FindPatternFeature(IModelDoc2 model)
        {
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                string nm = SafeName(f);
                if (nm != null && nm.StartsWith("Forge-SketchPattern", StringComparison.OrdinalIgnoreCase)) return f;
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
