using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class OffsetSketchEntitiesResult
    {
        public bool Success;
        public int Segments;        // expect 2 (seed circle + its offset)
        public bool OffsetApplied;  // SketchOffset2 return
        public bool AlreadyDone;
        public string SketchName;
        public string Diag;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 199 — offset_sketch_entities. Seeds a circle on the Front plane (CreateCircleByRadius is a proven-live
    /// creation call), selects it, and offsets it outward (ISketchManager.SketchOffset2). A sketch OPERATION (not a
    /// creation) — unlike the creation family these are unproven headless, so the handler INSTRUMENTS the return + a
    /// segment recount and fails CLOSED if the offset no-ops. Names the sketch "Forge-Offset" for idempotency; never saves.
    /// </summary>
    public static class OffsetSketchEntities
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            bool verb = Regex.IsMatch(c, @"\b(offset|off-set)\b");
            bool noun = Regex.IsMatch(c, @"\b(sketch|entit|circle|profile|geometry|curve)\b");
            return verb && noun;
        }

        public static async Task<OffsetSketchEntitiesResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new OffsetSketchEntitiesResult();
            var part = model as PartDoc;
            if (part == null) { res.Error = "Open a part (.SLDPRT) to offset a sketch."; return res; }

            var existing = FindOffsetFeature(model);
            if (existing != null)
            {
                res.AlreadyDone = true; res.Success = true; res.SketchName = SafeName(existing);
                res.Segments = CountSegments(existing);
                res.Info = "An offset sketch (" + res.SketchName + ") is already here — nothing to do.";
                return res;
            }

            await emit("Draftsman", "offsetting a sketch entity", "run", null);

            var before = new HashSet<string>(SketchFeatureNames(model));
            int segsBefore = -1;
            try
            {
                SelectPlane(model, "Front Plane");
                var sm = model.SketchManager;
                sm.InsertSketch(true);
                var circle = sm.CreateCircleByRadius(0, 0, 0, 0.020) as SketchSegment;
                segsBefore = CountActiveSegments(sm);
                model.ClearSelection2(true);
                if (circle != null) circle.Select4(false, null);
                res.OffsetApplied = sm.SketchOffset2(0.005, false, false, 0, 0, false);
                res.Segments = CountActiveSegments(sm);
                sm.InsertSketch(true);
                model.ClearSelection2(true);
                model.ForceRebuild3(false);
            }
            catch (Exception ex) { res.Error = "Offset failed: " + ex.Message; return res; }

            var created = NewSketchFeature(model, before);
            if (created == null) { res.Error = "The offset sketch was not created."; return res; }
            try { created.Name = "Forge-Offset"; } catch { }
            res.SketchName = SafeName(created);
            res.Segments = CountSegments(created);
            res.Success = res.OffsetApplied && res.Segments == 2;
            res.Diag = "segsBefore=" + segsBefore + " offsetRet=" + res.OffsetApplied + " segsAfter=" + res.Segments + " name=" + res.SketchName;

            await emit("Draftsman", null, "done", res.Success ? "sketch offset added" : ("offsetRet=" + res.OffsetApplied + " segs=" + res.Segments + " (expected 2)"));

            res.Info = res.Success
                ? "Offset a seed circle by 5mm (" + res.SketchName + "): 2 segments. Undo removes it; nothing was saved."
                : "Offset did not take (SketchOffset2 returned " + res.OffsetApplied + ", segments=" + res.Segments + ", expected 2) — the operation may be dead headless on this build.";
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

        private static Feature FindOffsetFeature(IModelDoc2 model)
        {
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                string nm = SafeName(f);
                if (nm != null && nm.StartsWith("Forge-Offset", StringComparison.OrdinalIgnoreCase)) return f;
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
