using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class CreateSketchResult
    {
        public string PlaneName;
        public string SketchName;
        public bool Applied;          // geometry-derived: a new sketch FEATURE must exist
        public bool AlreadyDone;
        public string Diag;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// CreateSketch (tool 81, "create_sketch") — starts a NEW, empty sketch on a named plane (or the Front plane
    /// by default): "start a sketch on the top plane", "create a new sketch", "sketch on the right plane". A prep
    /// step before manually drawing, or before a follow-up command (add_sketch_entity, etc.) adds geometry to it.
    ///
    /// Distinct from AddSketchEntity/AddSketchArc/etc. (those require a SHAPE noun or coordinates — this one draws
    /// no real geometry) and from PatternSketchEntities/MirrorSketchEntities (pattern/mirror verbs).
    ///
    /// LANDMINE (instrumented, not guessed): a sketch with ZERO entities is silently DISCARDED by SolidWorks the
    /// moment you exit it — InsertSketch(true) twice with nothing drawn in between leaves no new feature in the
    /// tree at all (confirmed live: the "new ProfileFeature" scan came back null every time). WORKING ROUTE: seed
    /// ONE origin point (SketchManager.CreatePoint(0,0,0), the same proven-live primitive AddSketchEntity's "point"
    /// kind uses) before exiting — a single point is enough for the sketch feature to persist, and is about as
    /// close to "an empty sketch, ready to draw in" as this build allows.
    ///
    /// Success is judged by GEOMETRY: a new sketch FEATURE (ProfileFeature) must appear in the tree. Names it
    /// "Forge-Sketch" for idempotency (Rule #5, same shape as AddSketchArc's "Forge-Arc"); never saves — one
    /// Ctrl+Z removes it.
    /// </summary>
    public static class CreateSketch
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (Regex.IsMatch(c, @"\b3[\s-]?d\b") || c.Contains("three dimensional") || c.Contains("three-dimensional")) return false;   // create_3d_sketch (123) owns those
            // create_layout_sketch (tool 231) owns explicit layout/skeleton/master-sketch wording — a master
            // skeleton sketch at the ASSEMBLY level. Defense-in-depth alongside dispatch ordering, not just
            // ordering alone (same shape as CreatePart's InsertNewPartInContext exclusion).
            if (Regex.IsMatch(c, @"\blayout\b|\bskeleton\b|\bmaster\s+sketch\b")) return false;
            if (Regex.IsMatch(c, @"\b(arc|ellipse|polygon|slot|spline|text|circle|line|rectangle|rect|point)\b")) return false;
            if (Regex.IsMatch(c, @"\b(pattern|mirror|offset|trim|extend|relation|dimension|dim|exit|close|finish|done)\b")) return false;
            if (Regex.IsMatch(c, @"\b(component|components|bolts?|nuts?|screws?|fasteners?)\b")) return false;
            bool verb = Regex.IsMatch(c, @"\b(start|open|begin|new|create|add|insert)\b");
            bool noun = Regex.IsMatch(c, @"\bsketch\b");
            return verb && noun;
        }

        public static async Task<CreateSketchResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new CreateSketchResult();
            var part = model as PartDoc;
            if (part == null) { res.Error = "Open a part (.SLDPRT) to start a sketch on."; return res; }

            string plane = ParsePlane(intent);
            res.PlaneName = plane;

            var existing = FindTaggedFeature(model);
            if (existing != null)
            {
                res.AlreadyDone = true; res.Applied = true; res.SketchName = SafeName(existing);
                res.Info = "A sketch (" + res.SketchName + ") is already here — nothing to do.";
                await emit("Draftsman", null, "done", res.SketchName + " already present — nothing to do");
                return res;
            }

            await emit("Draftsman", "starting a new sketch on " + plane, "run", null);

            var beforeNames = new HashSet<string>(SketchFeatureNames(model));
            try
            {
                SelectPlane(model, plane);
                var sm = model.SketchManager;
                sm.InsertSketch(true);
                sm.CreatePoint(0, 0, 0);   // a zero-entity sketch is silently discarded on exit — seed one point
                sm.InsertSketch(true);
                model.ClearSelection2(true);
                model.ForceRebuild3(false);
            }
            catch (Exception ex) { res.Error = "Starting the sketch failed: " + ex.Message; return res; }

            var created = NewSketchFeature(model, beforeNames);
            if (created == null)
            { res.Error = "The new sketch was not created."; await emit("Draftsman", null, "fail", res.Error); return res; }
            try { created.Name = "Forge-Sketch"; } catch { }
            res.SketchName = SafeName(created);
            res.Applied = true;
            res.Diag = "plane=" + plane + " name=" + res.SketchName;

            await emit("Draftsman", null, "done", res.SketchName + " started on " + plane);

            res.Info = "Started a new empty sketch (" + res.SketchName + ") on " + plane + ". One Ctrl+Z removes it; Forge didn't save.";
            return res;
        }

        // ---------- parsing ----------
        private static string ParsePlane(string intent)
        {
            string c = (intent ?? "").ToLowerInvariant();
            if (Regex.IsMatch(c, @"\btop\s*plane\b")) return "Top Plane";
            if (Regex.IsMatch(c, @"\bright\s*plane\b")) return "Right Plane";
            return "Front Plane";
        }

        // ---------- geometry helpers ----------
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
                if (nm != null && nm.Equals("Forge-Sketch", StringComparison.OrdinalIgnoreCase)) return f;
                f = f.GetNextFeature() as Feature;
            }
            return null;
        }

        private static string SafeName(Feature f) { try { return f.Name; } catch { return null; } }
    }
}
