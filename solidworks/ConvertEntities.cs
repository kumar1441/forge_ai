using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class ConvertEntitiesResult
    {
        public bool Success;
        public int Segments;        // expect 1 (the converted edge)
        public bool AlreadyDone;
        public string SketchName;
        public string Diag;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 200 — convert_entities. Sketches on a body face and projects ONE of that face's edges into the sketch
    /// (ISketchManager.ConvertEntities — a sketch OPERATION; the creation family is proven live and offset_sketch_entities
    /// proved ops survive too, but ConvertEntities returns void so the handler INSTRUMENTS via a segment recount and fails
    /// CLOSED on a no-op). Names the sketch "Forge-Convert" for idempotency; verifies exactly one segment; never saves.
    /// </summary>
    public static class ConvertEntities
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            bool verb = Regex.IsMatch(c, @"\b(convert|project)\b");
            bool noun = Regex.IsMatch(c, @"\b(entit|edge|edges|sketch|geometry)\b");
            return verb && noun;
        }

        public static async Task<ConvertEntitiesResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new ConvertEntitiesResult();
            var part = model as PartDoc;
            if (part == null) { res.Error = "Open a part (.SLDPRT) to convert edges into a sketch."; return res; }

            var existing = FindConvertFeature(model);
            if (existing != null)
            {
                res.AlreadyDone = true; res.Success = true; res.SketchName = SafeName(existing);
                res.Segments = CountSegments(existing);
                res.Info = "A converted-edge sketch (" + res.SketchName + ") is already here — nothing to do.";
                return res;
            }

            Face2 face = null; Edge edge = null;
            try
            {
                var bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[];
                foreach (var bo in bodies ?? new object[0])
                {
                    var body = bo as Body2; if (body == null) continue;
                    foreach (var fo in (body.GetFaces() as object[]) ?? new object[0])
                    {
                        var f = fo as Face2; if (f == null) continue;
                        var edges = f.GetEdges() as object[];
                        if (edges != null && edges.Length > 0) { face = f; edge = edges[0] as Edge; break; }
                    }
                    if (face != null) break;
                }
            }
            catch (Exception ex) { res.Error = "Could not read the body's faces/edges: " + ex.Message; return res; }
            if (face == null || edge == null) { res.Error = "No solid body face/edge found to convert."; return res; }

            await emit("Draftsman", "converting an edge into a sketch", "run", null);

            var before = new HashSet<string>(SketchFeatureNames(model));
            try
            {
                model.ClearSelection2(true);
                (face as Entity).Select4(false, null);
                var sm = model.SketchManager;
                sm.InsertSketch(true);
                model.ClearSelection2(true);
                (edge as Entity).Select4(false, null);
                sm.ConvertEntities();
                res.Segments = CountActiveSegments(sm);
                sm.InsertSketch(true);
                model.ClearSelection2(true);
                model.ForceRebuild3(false);
            }
            catch (Exception ex) { res.Error = "Convert failed: " + ex.Message; return res; }

            var created = NewSketchFeature(model, before);
            if (created == null) { res.Error = "The converted-edge sketch was not created."; return res; }
            try { created.Name = "Forge-Convert"; } catch { }
            res.SketchName = SafeName(created);
            res.Segments = CountSegments(created);
            res.Success = res.Segments == 1;
            res.Diag = "segsAfter=" + res.Segments + " name=" + res.SketchName;

            await emit("Draftsman", null, "done", res.Success ? "edge converted into sketch" : ("got " + res.Segments + " segments, expected 1"));

            res.Info = res.Success
                ? "Converted one body edge into the sketch (" + res.SketchName + "). Undo removes it; nothing was saved."
                : "Convert produced " + res.Segments + " segments, expected 1 — ConvertEntities may be dead headless on this build.";
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

        private static Feature FindConvertFeature(IModelDoc2 model)
        {
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                string nm = SafeName(f);
                if (nm != null && nm.StartsWith("Forge-Convert", StringComparison.OrdinalIgnoreCase)) return f;
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
    }
}
