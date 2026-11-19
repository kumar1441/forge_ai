using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class AddSketchTextResult
    {
        public bool Success;
        public int TextSegments;   // expect 1
        public bool AlreadyDone;
        public string SketchName;
        public string Diag;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 198 — add_sketch_text. Inserts a sketch-text note on the Front plane (IModelDoc2.InsertSketchText — sketch
    /// text is NOT a swSketchSegments_e segment, it lives in ISketch.GetSketchTextSegments). Names the sketch feature
    /// "Forge-Text" for idempotency (Rule #5), verifies exactly one text segment, and never saves — one Ctrl+Z removes it.
    /// </summary>
    public static class AddSketchText
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (Regex.IsMatch(c, @"\b(remove|delete|suppress)\b")) return false;
            bool verb = Regex.IsMatch(c, @"\b(add|draw|create|sketch|insert|write|make|place)\b");
            bool noun = Regex.IsMatch(c, @"\b(text|note|label|lettering|caption)\b");
            return verb && noun;
        }

        public static async Task<AddSketchTextResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new AddSketchTextResult();
            var part = model as PartDoc;
            if (part == null) { res.Error = "Open a part (.SLDPRT) to add a text sketch."; return res; }

            var existing = FindTextFeature(model);
            if (existing != null)
            {
                res.AlreadyDone = true; res.Success = true; res.SketchName = SafeName(existing);
                res.TextSegments = CountText(existing);
                res.Info = "A text sketch (" + res.SketchName + ") is already here — nothing to do.";
                return res;
            }

            await emit("Draftsman", "adding sketch text", "run", null);

            var before = new HashSet<string>(SketchFeatureNames(model));
            try
            {
                SelectPlane(model, "Front Plane");
                var sm = model.SketchManager;
                sm.InsertSketch(true);
                // note at origin. Alignment=0 (left), FlipDir=0, HMirror=0, WidthFactor=100, SpaceBetweenChars=100.
                model.InsertSketchText(0, 0, 0, "Forge", 0, 0, 0, 100, 100);
                sm.InsertSketch(true);
                model.ClearSelection2(true);
                model.ForceRebuild3(false);
            }
            catch (Exception ex) { res.Error = "Sketch text creation failed: " + ex.Message; return res; }

            var created = NewSketchFeature(model, before);
            if (created == null) { res.Error = "The text sketch was not created."; return res; }
            try { created.Name = "Forge-Text"; } catch { }
            res.SketchName = SafeName(created);
            res.TextSegments = CountText(created);
            res.Success = res.TextSegments == 1;
            res.Diag = "text=" + res.TextSegments + " name=" + res.SketchName;

            await emit("Draftsman", null, "done", res.Success ? "sketch text added" : ("got " + res.TextSegments + " text segments, expected 1"));

            res.Info = res.Success
                ? "Added a text sketch (" + res.SketchName + ") on the Front plane. Undo removes it; nothing was saved."
                : "The sketch has " + res.TextSegments + " text segments, expected 1 — check the model.";
            return res;
        }

        private static int CountText(Feature sketchFeat)
        {
            try
            {
                var sk = sketchFeat.GetSpecificFeature2() as Sketch;
                if (sk == null) return 0;
                var arr = sk.GetSketchTextSegments() as object[];
                return arr == null ? 0 : arr.Length;
            }
            catch { return 0; }
        }

        private static Feature FindTextFeature(IModelDoc2 model)
        {
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                string nm = SafeName(f);
                if (nm != null && nm.StartsWith("Forge-Text", StringComparison.OrdinalIgnoreCase)) return f;
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
