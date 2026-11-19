using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class AddSketchSlotResult
    {
        public bool Success;
        public int LineSegments;   // a straight slot's two straight sides
        public int ArcSegments;    // its two end caps
        public bool AlreadyDone;
        public string SketchName;
        public string Diag;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 195 — add_sketch_slot. Draws a straight (line) slot on the Front plane (SketchManager.CreateSketchSlot, the
    /// live entity-creation family). A straight slot is 2 straight sides + 2 end-cap arcs; the handler verifies exactly
    /// that. Names the sketch feature "Forge-Slot" so a rerun is a no-op (Rule #5). Never saves — one Ctrl+Z removes it.
    /// </summary>
    public static class AddSketchSlot
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (Regex.IsMatch(c, @"\b(remove|delete|suppress)\b")) return false;
            bool verb = Regex.IsMatch(c, @"\b(add|draw|create|sketch|insert|make)\b");
            bool noun = Regex.IsMatch(c, @"\bslot\b");
            return verb && noun;
        }

        public static async Task<AddSketchSlotResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new AddSketchSlotResult();
            var part = model as PartDoc;
            if (part == null) { res.Error = "Open a part (.SLDPRT) to add a slot sketch."; return res; }

            var existing = FindSlotFeature(model);
            if (existing != null)
            {
                res.AlreadyDone = true; res.Success = true; res.SketchName = SafeName(existing);
                CountSegs(existing, out res.LineSegments, out res.ArcSegments);
                res.Info = "A slot sketch (" + res.SketchName + ") is already here — nothing to do.";
                return res;
            }

            await emit("Draftsman", "sketching a straight slot", "run", null);

            const double w = 0.010;   // 10mm wide
            var before = new HashSet<string>(SketchFeatureNames(model));
            try
            {
                SelectPlane(model, "Front Plane");
                var sm = model.SketchManager;
                sm.InsertSketch(true);
                // line slot, centre-to-centre: two centre endpoints (-15,0)-(15,0), width 10; 3rd point sets the width side.
                sm.CreateSketchSlot((int)swSketchSlotCreationType_e.swSketchSlotCreationType_line,
                    (int)swSketchSlotLengthType_e.swSketchSlotLengthType_CenterCenter, w,
                    -0.015, 0, 0, 0.015, 0, 0, 0, w / 2, 0, 1, false);
                sm.InsertSketch(true);
                model.ClearSelection2(true);
                model.ForceRebuild3(false);
            }
            catch (Exception ex) { res.Error = "Slot creation failed: " + ex.Message; return res; }

            var created = NewSketchFeature(model, before);
            if (created == null) { res.Error = "The slot sketch was not created."; return res; }
            try { created.Name = "Forge-Slot"; } catch { }
            res.SketchName = SafeName(created);
            CountSegs(created, out res.LineSegments, out res.ArcSegments);
            res.Success = res.LineSegments == 2 && res.ArcSegments == 2;
            res.Diag = "lines=" + res.LineSegments + " arcs=" + res.ArcSegments + " name=" + res.SketchName;

            await emit("Draftsman", null, "done", res.Success ? "straight slot sketched (2 sides + 2 caps)" : ("slot has " + res.LineSegments + " lines / " + res.ArcSegments + " arcs"));

            res.Info = res.Success
                ? "Added a straight slot sketch (" + res.SketchName + ") on the Front plane. Undo removes it; nothing was saved."
                : "The slot sketched with " + res.LineSegments + " sides / " + res.ArcSegments + " caps, expected 2 / 2 — check the model.";
            return res;
        }

        private static void CountSegs(Feature sketchFeat, out int lines, out int arcs)
        {
            lines = 0; arcs = 0;
            try
            {
                var sk = sketchFeat.GetSpecificFeature2() as Sketch;
                if (sk == null) return;
                foreach (var o in (sk.GetSketchSegments() as object[]) ?? new object[0])
                {
                    var seg = o as SketchSegment; if (seg == null) continue;
                    bool constr = false; try { constr = seg.ConstructionGeometry; } catch { }
                    if (constr) continue;
                    int t = -1; try { t = seg.GetType(); } catch { }
                    if (t == (int)swSketchSegments_e.swSketchLINE) lines++;
                    else if (t == (int)swSketchSegments_e.swSketchARC) arcs++;
                }
            }
            catch { }
        }

        private static Feature FindSlotFeature(IModelDoc2 model)
        {
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                string nm = SafeName(f);
                if (nm != null && nm.StartsWith("Forge-Slot", StringComparison.OrdinalIgnoreCase)) return f;
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
