using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class CreateLayoutSketchResult
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
    /// CreateLayoutSketch (tool 231, "create_layout_sketch") — master skeleton sketch at the ASSEMBLY level
    /// ("create a layout sketch", "start a master skeleton sketch") that other components can later reference
    /// for top-down positioning/sizing. Reflection confirmed there is NO distinct SolidWorks API type for a
    /// "Layout" sketch — `Insert > Layout` in the UI is the exact same `ISketchManager.InsertSketch` primitive
    /// `create_sketch` (tool 81) already proved live, just placed directly in the ASSEMBLY's own feature tree
    /// instead of inside a part. So this handler reuses CreateSketch's exact proven recipe (seed one origin point
    /// before exiting, since a zero-entity sketch is silently discarded on exit) with the scope flipped from
    /// PartDoc to AssemblyDoc, and a distinct tag name so it's never confused with a part-level Forge-Sketch.
    ///
    /// Distinct from create_sketch (81, explicitly PART-only, refuses an assembly) — IsIntent requires explicit
    /// layout/skeleton/master vocabulary AND is dispatched BEFORE CreateSketch (which also carries a matching
    /// exclusion, defense-in-depth) so "create a sketch" and "create a layout sketch" never collide.
    ///
    /// Success is judged by GEOMETRY: a new sketch FEATURE (ProfileFeature) must appear in the assembly's OWN
    /// top-level feature tree (never a component's). Named "Forge-LayoutSketch" for idempotency (Rule #5); never
    /// saves — one Ctrl+Z removes it.
    /// </summary>
    public static class CreateLayoutSketch
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            bool verb = Regex.IsMatch(c, @"\b(start|open|begin|new|create|add|insert)\b");
            bool noun = Regex.IsMatch(c, @"\bsketch\b");
            bool layoutWord = Regex.IsMatch(c, @"\blayout\b|\bskeleton\b|\bmaster\s+sketch\b");
            return verb && noun && layoutWord;
        }

        public static async Task<CreateLayoutSketchResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new CreateLayoutSketchResult();
            if (model == null) { res.Error = "Open an assembly to add a layout sketch to."; return res; }
            int docType = 0; try { docType = model.GetType(); } catch { }
            if (docType != (int)swDocumentTypes_e.swDocASSEMBLY)
            { res.Error = "A layout/skeleton sketch belongs at the ASSEMBLY level, not this document type."; return res; }

            string plane = ParsePlane(intent);
            res.PlaneName = plane;

            var existing = FindTaggedFeature(model);
            if (existing != null)
            {
                res.AlreadyDone = true; res.Applied = true; res.SketchName = SafeName(existing);
                res.Info = "A layout sketch (" + res.SketchName + ") is already here — nothing to do.";
                await emit("Draftsman", null, "done", res.SketchName + " already present — nothing to do");
                return res;
            }

            await emit("Draftsman", "starting a master layout sketch on " + plane, "run", null);

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
            catch (Exception ex) { res.Error = "Starting the layout sketch failed: " + ex.Message; return res; }

            var created = NewSketchFeature(model, beforeNames);
            if (created == null)
            { res.Error = "The new layout sketch was not created."; await emit("Draftsman", null, "fail", res.Error); return res; }
            try { created.Name = "Forge-LayoutSketch"; } catch { }
            res.SketchName = SafeName(created);
            res.Applied = true;
            res.Diag = "plane=" + plane + " name=" + res.SketchName;

            await emit("Draftsman", null, "done", res.SketchName + " started on " + plane);

            res.Info = "Started a new master layout sketch (" + res.SketchName + ") on " + plane + " at the assembly level — other components can now reference it for top-down positioning. One Ctrl+Z removes it; Forge didn't save.";
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
                if (nm != null && nm.Equals("Forge-LayoutSketch", StringComparison.OrdinalIgnoreCase)) return f;
                f = f.GetNextFeature() as Feature;
            }
            return null;
        }

        private static string SafeName(Feature f) { try { return f.Name; } catch { return null; } }
    }
}
