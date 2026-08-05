using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class Create3DSketchResult
    {
        public string SketchName;
        public bool Applied;          // geometry-derived: a new 3DProfileFeature must exist
        public bool AlreadyDone;
        public string Diag;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Create3DSketch (tool 123, "create_3d_sketch") — starts a NEW 3D sketch, not tied to any plane: "start a 3D
    /// sketch", "create a 3d sketch for a sweep path", "new 3-D sketch". A prep step for routing/sweep-path work
    /// that lives in free 3D space rather than on a single planar sketch (CreateSketch, tool 81).
    ///
    /// API is `ISketchManager.Insert3DSketch(bool)` — PROVEN LIVE already in this codebase (used by
    /// the test fixture generator to build an assembly-level 3-point 3D sketch fixture), so this is a
    /// low-risk build, not a blind guess. Same discard-on-exit landmine as CreateSketch (tool 81): an EMPTY sketch
    /// is silently dropped on exit, so this seeds TWO points off a single axis (0,0,0) and (20,15,10 mm) — enough
    /// to persist AND to prove genuine 3D placement (not a planar 2-point line). Success is judged by GEOMETRY: a
    /// new feature whose type name is specifically "3DProfileFeature" (not the 2D "ProfileFeature" CreateSketch
    /// produces) must appear in the tree.
    ///
    /// Works on either a PART or an ASSEMBLY (Insert3DSketch is proven live on both — assembly-level 3D sketches are
    /// the normal home for routing paths). Tagged "Forge-3DSketch" for idempotency (Rule #5); never saves — one
    /// Ctrl+Z removes it.
    /// </summary>
    public static class Create3DSketch
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            bool is3d = Regex.IsMatch(c, @"\b3[\s-]?d\b") || c.Contains("three dimensional") || c.Contains("three-dimensional");
            if (!is3d) return false;
            bool verb = Regex.IsMatch(c, @"\b(start|open|begin|new|create|add|insert)\b");
            bool noun = Regex.IsMatch(c, @"\bsketch\b");
            return verb && noun;
        }

        public static async Task<Create3DSketchResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new Create3DSketchResult();
            if (model == null) { res.Error = "Open a part or assembly to start a 3D sketch on."; return res; }

            var existing = FindTaggedFeature(model);
            if (existing != null)
            {
                res.AlreadyDone = true; res.Applied = true; res.SketchName = SafeName(existing);
                res.Info = "A 3D sketch (" + res.SketchName + ") is already here — nothing to do.";
                await emit("Draftsman", null, "done", res.SketchName + " already present — nothing to do");
                return res;
            }

            await emit("Draftsman", "starting a new 3D sketch", "run", null);

            var beforeNames = new HashSet<string>(SketchFeatureNames(model, "3DProfileFeature"));
            try
            {
                var sm = model.SketchManager;
                sm.Insert3DSketch(true);
                sm.CreatePoint(0, 0, 0);              // seed 2 points (not 1 — a single point reads as an
                sm.CreatePoint(0.02, 0.015, 0.01);     // Origin false-positive elsewhere in Forge) off-axis so the
                sm.Insert3DSketch(true);               // sketch is genuinely 3D, not a flat 2-point line
                model.ClearSelection2(true);
                model.ForceRebuild3(false);
            }
            catch (Exception ex) { res.Error = "Starting the 3D sketch failed: " + ex.Message; return res; }

            var created = NewSketchFeature(model, beforeNames, "3DProfileFeature");
            if (created == null)
            { res.Error = "The new 3D sketch was not created."; await emit("Draftsman", null, "fail", res.Error); return res; }
            try { created.Name = "Forge-3DSketch"; } catch { }
            res.SketchName = SafeName(created);
            res.Applied = true;
            res.Diag = "name=" + res.SketchName;

            await emit("Draftsman", null, "done", res.SketchName + " started");

            res.Info = "Started a new 3D sketch (" + res.SketchName + "). One Ctrl+Z removes it; Forge didn't save.";
            return res;
        }

        // ---------- geometry helpers ----------
        private static IEnumerable<string> SketchFeatureNames(IModelDoc2 model, string typeName)
        {
            var list = new List<string>();
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                string tn = ""; try { tn = f.GetTypeName2(); } catch { }
                if (tn == typeName) list.Add(SafeName(f));
                f = f.GetNextFeature() as Feature;
            }
            return list;
        }

        private static Feature NewSketchFeature(IModelDoc2 model, HashSet<string> before, string typeName)
        {
            Feature found = null;
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                string tn = ""; try { tn = f.GetTypeName2(); } catch { }
                if (tn == typeName && !before.Contains(SafeName(f))) found = f;
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
                if (nm != null && nm.Equals("Forge-3DSketch", StringComparison.OrdinalIgnoreCase)) return f;
                f = f.GetNextFeature() as Feature;
            }
            return null;
        }

        private static string SafeName(Feature f) { try { return f.Name; } catch { return null; } }
    }
}
