using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class SketchDrivenPatternComponentResult
    {
        public string SeedComponent;
        public string SketchName;
        public int PointCount;         // total sketch points = target total instances
        public int InstancesAdded;
        public int ExpectedInstances;
        public int OverDefined;
        public int RebuildErrors;
        public bool RolledBack;
        public bool AlreadyPatterned;
        public string Info;
        public string Error;
        public string Question;
        public bool NeedsConfirm;
        public string Diag;
    }

    /// <summary>
    /// SketchDrivenPatternComponent (tool 45, "sketch_driven_pattern") — a WRITE handler that places copies of ONE
    /// named component at every point of an existing SKETCH (an assembly-level 3D sketch, the classic "layout
    /// sketch with points" component-pattern source) — the third and final member of the component-pattern family:
    /// unlike 41/42 (count/spacing/angle given directly by the user) and 44 (follows an existing FEATURE pattern),
    /// this one follows a set of explicit sketch POINTS.
    ///
    /// LANDMINE (do not re-attempt blind — the same class confirmed three times already for 41/42/44): a
    /// Component2-seeded FeatureManager pattern call returns null on this build. WORKING ROUTE (identical shape):
    /// AssemblyDoc.AddComponent5 at each sketch point's own coordinates (a 3D sketch's ISketchPoint.X/Y/Z ARE
    /// absolute model-space metres when the sketch lives directly in the assembly — no plane transform needed).
    ///
    /// Sketch resolution: the first top-level assembly feature whose GetSpecificFeature2() is a Sketch with >=2
    /// points (the same generic, name-independent detection GetSketches.cs already proved live). The point
    /// CLOSEST to the seed's current position is treated as "already occupied" (the seed's own spot); every other
    /// point is a target. Component resolution reuses SelectComponent's normalized-name matcher. No single pattern
    /// feature exists to Ctrl+Z as a unit — each insert is its own undo step; Forge never saves. Idempotent: if the
    /// seed's file already has >= the sketch's own point count, nothing is added.
    /// </summary>
    public static class SketchDrivenPatternComponent
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (!Regex.IsMatch(c, @"\bpattern\b")) return false;
            bool sketchPts = Regex.IsMatch(c, @"\bsketch\b") && Regex.IsMatch(c, @"\bpoints?\b");
            if (!sketchPts) return false;
            return Regex.IsMatch(c, @"\bcomponent\b") || Regex.IsMatch(c, "[\"']([^\"']{2,})[\"']");
        }

        public static async Task<SketchDrivenPatternComponentResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new SketchDrivenPatternComponentResult();
            var asm = model as AssemblyDoc;
            if (asm == null) { res.Error = "Open the assembly (.SLDASM) to pattern a component."; return res; }

            await emit("Gauge", "reading the assembly", "run", null);

            string query = ParseComponentName(intent);
            if (string.IsNullOrEmpty(query))
            { res.Error = "Which component? Name it (e.g. \"pattern the pin component at the sketch points\")."; await emit("Gauge", null, "fail", res.Error); return res; }

            object[] comps = asm.GetComponents(false) as object[];
            Component2 exact = null;
            var candidates = new List<Component2>();
            string normQuery = SelectComponent.Normalize(query);
            foreach (var o in comps ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { }
                if (sup) continue;
                string nm = SafeName(c); if (string.IsNullOrEmpty(nm)) continue;
                string norm = SelectComponent.Normalize(nm);
                if (norm == normQuery) { if (exact == null) exact = c; candidates.Add(c); }
                else if (norm.Contains(normQuery) || normQuery.Contains(norm)) candidates.Add(c);
            }
            Component2 seed = exact;
            if (seed == null && candidates.Count == 1) seed = candidates[0];
            if (seed == null && candidates.Count > 1)
            {
                res.NeedsConfirm = true;
                res.Question = candidates.Count + " components match '" + query + "' — which one? (e.g. include the instance number.)";
                await emit("Gauge", null, "fail", res.Question);
                return res;
            }
            if (seed == null)
            { res.Error = "Couldn't find a component matching '" + query + "'."; await emit("Gauge", null, "fail", res.Error); return res; }
            res.SeedComponent = SafeName(seed);

            string seedPath = SafePath(seed);
            if (string.IsNullOrEmpty(seedPath))
            { res.Error = "Couldn't resolve '" + res.SeedComponent + "'s file path."; await emit("Gauge", null, "fail", res.Error); return res; }

            // ---- find the sketch: the first top-level assembly feature whose specific-feature is a Sketch
            //      carrying >=2 points (name-independent — same generic detection GetSketches.cs already proved). ----
            string sketchName = null;
            List<double[]> points = null;
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                Sketch sk = null; try { sk = f.GetSpecificFeature2() as Sketch; } catch { }
                if (sk != null)
                {
                    int pc = 0; try { pc = sk.GetSketchPointsCount2(); } catch { }
                    if (pc >= 2)
                    {
                        var pts = ReadSketchPoints(sk);
                        if (pts.Count >= 2) { sketchName = SafeName(f); points = pts; break; }
                    }
                }
                f = f.GetNextFeature() as Feature;
            }
            if (points == null)
            { res.Error = "No sketch with points found in the assembly to place '" + res.SeedComponent + "' at — add a 3D sketch with points first."; await emit("Gauge", null, "fail", res.Error); return res; }
            res.SketchName = sketchName;
            res.PointCount = points.Count;

            int existingCount = CountByPath(comps, seedPath);
            if (existingCount >= res.PointCount)
            {
                res.AlreadyPatterned = true;
                res.Info = "Already " + existingCount + " instance(s) of " + res.SeedComponent + " present (>= the sketch's " + res.PointCount + " points) — nothing to do.";
                await emit("Gauge", null, "done", res.Info);
                return res;
            }
            res.ExpectedInstances = res.PointCount - existingCount;

            double[] seedXf = null; try { seedXf = seed.Transform2.ArrayData as double[]; } catch { }
            if (seedXf == null || seedXf.Length < 12)
            { res.Error = "Couldn't read '" + res.SeedComponent + "'s placement transform."; await emit("Gauge", null, "fail", res.Error); return res; }
            double[] seedPos = { seedXf[9], seedXf[10], seedXf[11] };

            // the point closest to the seed's current position is "occupied"; every other point is a target,
            // taken in traversal order.
            int occupiedIdx = ClosestIndex(points, seedPos);
            var targets = new List<double[]>();
            for (int i = 0; i < points.Count; i++) if (i != occupiedIdx) targets.Add(points[i]);
            int toAdd = res.ExpectedInstances;
            if (toAdd > targets.Count) toAdd = targets.Count;

            await emit("Gauge", null, "done",
                "sketch '" + res.SketchName + "' · " + res.PointCount + " points · seed at 1 · adding " + toAdd + " new cop" + (toAdd == 1 ? "y" : "ies"));

            // =================== Stamp: insert the new instances at the remaining sketch points ===================
            await emit("Stamp", "inserting copies at the sketch points", "run", null);
            int oe = 0, ow = 0; try { app.OpenDoc6(seedPath, (int)swDocumentTypes_e.swDocPART, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref oe, ref ow); } catch { }
            try { model.ClearSelection2(true); } catch { }
            for (int i = 0; i < toAdd; i++)
            {
                double[] p = targets[i];
                try { asm.AddComponent5(seedPath, (int)swAddComponentConfigOptions_e.swAddComponentConfigOptions_CurrentSelectedConfig, "", false, "", p[0], p[1], p[2]); }
                catch (Exception ex) { res.Error = "AddComponent5 threw: " + ex.Message; await emit("Stamp", null, "fail", res.Error); return res; }
            }
            try { model.ClearSelection2(true); } catch { }
            try { model.EditRebuild3(); } catch { try { model.ForceRebuild3(false); } catch { } }
            await emit("Stamp", null, "done", "copies inserted — verifying they landed");

            // =================== Sentinel: INDEPENDENT post-rebuild verification ===================
            await emit("Sentinel", "confirming the new instances landed", "run", null);
            object[] comps2 = asm.GetComponents(false) as object[];
            int afterCount = CountByPath(comps2, seedPath);
            res.InstancesAdded = afterCount - existingCount;
            res.RebuildErrors = SafeWrong(model);

            int over = 0;
            foreach (var o in comps2 ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                int st = (int)swConstrainedStatus_e.swUnderConstrained; try { st = c.GetConstrainedStatus(); } catch { }
                if (st == (int)swConstrainedStatus_e.swOverConstrained || st == (int)swConstrainedStatus_e.swNoSolution || st == (int)swConstrainedStatus_e.swInvalidSolution) over++;
            }
            res.OverDefined = over;
            res.Diag = "points=" + res.PointCount + " existing=" + existingCount + " added=" + res.InstancesAdded + " overDefined=" + over + " rebuildErr=" + res.RebuildErrors;

            bool countOk = res.InstancesAdded == toAdd;
            bool clean = res.RebuildErrors == 0 && res.OverDefined == 0;
            if (!(countOk && clean))
            {
                RemoveExtraInstances(model, asm, seedPath, existingCount);
                try { model.ForceRebuild3(false); } catch { }
                res.RolledBack = true;
                res.Error = !clean
                    ? ("Insert " + (res.OverDefined > 0 ? "over-defined " + res.OverDefined + " component(s)" : "left " + res.RebuildErrors + " rebuild error(s)") + " — rolled back. " + res.Diag)
                    : ("Added " + res.InstancesAdded + " instance(s), expected " + toAdd + " — rolled back. " + res.Diag);
                await emit("Sentinel", null, "fail", res.Error);
                return res;
            }

            res.Info = "Patterned " + res.SeedComponent + " at '" + res.SketchName + "'s " + res.PointCount + " points (" +
                       res.InstancesAdded + " new) — no over-define, rebuild clean. Forge never saves.";
            await emit("Sentinel", null, "done", res.InstancesAdded + " instance(s) verified in place · rebuild clean");
            return res;
        }

        // ---------- parsing ----------
        private static string ParseComponentName(string intent)
        {
            string raw = intent ?? "";
            var qm = Regex.Match(raw, "[\"']([^\"']{2,})[\"']");
            if (qm.Success) return qm.Groups[1].Value.Trim();
            var m = Regex.Match(raw, @"pattern\s+(?:the\s+)?(.+?)\s+component\b", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value.Trim();
            return null;
        }

        // ---- sketch points: for a 3D sketch (the common assembly-level layout case), ISketchPoint.X/Y/Z ARE
        //      absolute model-space metres (no plane transform to apply). ----
        private static List<double[]> ReadSketchPoints(Sketch sk)
        {
            var list = new List<double[]>();
            object[] pts = null; try { pts = sk.GetSketchPoints2() as object[]; } catch { }
            foreach (var o in pts ?? new object[0])
            {
                var sp = o as SketchPoint; if (sp == null) continue;
                try { list.Add(new[] { sp.X, sp.Y, sp.Z }); } catch { }
            }
            return list;
        }

        private static int ClosestIndex(List<double[]> points, double[] pos)
        {
            int best = 0; double bestD = double.MaxValue;
            for (int i = 0; i < points.Count; i++)
            {
                var p = points[i];
                double dx = p[0] - pos[0], dy = p[1] - pos[1], dz = p[2] - pos[2];
                double d = dx * dx + dy * dy + dz * dz;
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        // ---------- misc ----------
        private static string SafeName(Component2 c) { try { return c.Name2; } catch { return null; } }
        private static string SafeName(Feature f) { try { return f?.Name; } catch { return null; } }
        private static string SafePath(Component2 c) { try { return c.GetPathName(); } catch { return null; } }

        private static int CountByPath(object[] comps, string path)
        {
            if (string.IsNullOrEmpty(path)) return 0;
            int n = 0;
            foreach (var o in comps ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                string p = null; try { p = c.GetPathName(); } catch { }
                if (string.Equals(p, path, StringComparison.OrdinalIgnoreCase)) n++;
            }
            return n;
        }

        private static void RemoveExtraInstances(IModelDoc2 model, AssemblyDoc asm, string seedPath, int keepCount)
        {
            try
            {
                object[] comps = asm.GetComponents(false) as object[];
                var mine = new List<Component2>();
                foreach (var o in comps ?? new object[0])
                {
                    var c = o as Component2; if (c == null) continue;
                    bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                    string p = null; try { p = c.GetPathName(); } catch { }
                    if (string.Equals(p, seedPath, StringComparison.OrdinalIgnoreCase)) mine.Add(c);
                }
                model.ClearSelection2(true);
                for (int i = keepCount; i < mine.Count; i++) { try { mine[i].Select4(false, null, false); } catch { } }
                try { model.EditDelete(); } catch { }
                model.ClearSelection2(true);
            }
            catch { }
        }

        private static int SafeWrong(IModelDoc2 model) { try { return model.Extension.GetWhatsWrongCount(); } catch { return 0; } }
    }
}
