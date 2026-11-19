using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class ImpactResult
    {
        public string Target;        // resolved feature (or the feature owning the named dimension)
        public string TargetType;    // SW type name, e.g. "Extrusion" — so the answer says WHAT it is
        public bool ByDimension;     // the user pointed at a dimension, not the feature itself
        public int Sketches;         // downstream sketches built on the target
        public int Patterns;         // patterns/mirrors that repeat it
        public int Features;         // other downstream features (fillets, cuts, shells…)
        public int Mates;            // assembly mates that reference the target
        public int DrawingDims;      // drawing dimensions driven by the target (sibling .SLDDRW)
        public int Total => Sketches + Patterns + Features + Mates + DrawingDims;
        public List<string> Names = new List<string>(); // sample dependent names (capped)
        // NAMED dependents per category (Rule: say the name, not just the count) — e.g. "LPattern1 (a 3-instance linear pattern)".
        public List<string> PatternDescs = new List<string>();
        public List<string> SketchNames = new List<string>();
        public List<string> FeatureNames = new List<string>();
        public List<string> MateNames = new List<string>();
        public string PrimaryDependent;  // the single most useful dependent to "show" on a follow-up (usually the pattern)
        public bool Available = true;   // Rule #4: false when this model has no dependency graph to read
        public bool DrawingsScanned;    // false => no sibling drawing on disk, so DrawingDims is "not checked"
        public string Question;         // Rule #2: set => Forge asked one question and ran nothing
        public string Info;             // one-line verdict, answer-first
        public string Error;
    }

    /// <summary>
    /// Impact — "What breaks if I touch this?" (demo #3). BEFORE any edit, it traces everything that DEPENDS on a
    /// feature/dimension the engineer names (or has selected) and reports the blast radius: N sketches, M mates,
    /// K drawing dims. Strictly READ-ONLY — it never changes a dimension, never rebuilds geometry, never saves.
    ///
    /// Grounded in the live model (Rule #8): the target is resolved from the actual selection or the actual feature
    /// tree, never assumed. Dependents come from the feature dependency graph (IFeature.GetChildren — the same
    /// traversal VariantGenerator.ReadDependencies trusts), assembly mates that reference the target, and dimensions
    /// on sibling drawings driven by it. Ambiguous target => ONE question (Rule #2); no dependency graph on this
    /// model (dumb imported solid) => say so, don't fake a number (Rule #4). Verified independently by GroundTruth
    /// (MeasureImpact re-reads feature count + every dim value to assert the model is byte-for-byte unchanged).
    /// </summary>
    public static class Impact
    {
        public static async Task<ImpactResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new ImpactResult();
            if (model == null) { res.Error = "Open a part or assembly first."; return res; }

            await emit("Tracer", "finding what you're pointing at", "run", null);

            // ---- RESOLVE the target: selection first (the engineer may have it picked in SW), else the named feature. ----
            var feats = AllFeatures(model);
            if (feats.Count == 0)
            {
                // No feature tree at all — an imported dumb solid. Rule #4: name the limit, don't fake a dependency count.
                res.Available = false;
                res.Info = "This model has no feature tree (imported solid), so there are no feature dependencies to trace. It would work on a native part with a rebuildable history.";
                await emit("Tracer", null, "done", "no feature tree — nothing to trace");
                return res;
            }

            Feature target = FromSelection(model, out bool byDim);
            if (target == null)
            {
                var hits = MatchByIntent(feats, intent);
                if (hits.Count == 0)
                {
                    res.Question = "I couldn't find that feature. Which one do you mean — " + Sample(feats.Select(FName), 4) + "?";
                    await emit("Tracer", null, "ask", "target not found");
                    return res;
                }
                if (hits.Count > 1)
                {
                    res.Question = "Which one — " + Sample(hits.Select(FName), 4) + "?";
                    await emit("Tracer", null, "ask", hits.Count + " candidates");
                    return res;
                }
                target = hits[0];
            }

            res.Target = FName(target);
            res.TargetType = SafeType(target);
            res.ByDimension = byDim;
            await emit("Tracer", null, "done", "target: " + res.Target + " (" + res.TargetType + ")");

            // ---- TRACE downstream features via the dependency graph (GetChildren closure — the traversal the codebase trusts). ----
            await emit("Tracer", "tracing the dependency graph", "run", null);
            var down = Downstream(target);
            var names = new List<string>();
            foreach (var f in down)
            {
                string tn = SafeType(f);
                if (IsSketch(tn)) { res.Sketches++; names.Add(FName(f)); res.SketchNames.Add(FName(f)); }
                else if (IsPattern(tn)) { res.Patterns++; names.Add(FName(f)); res.PatternDescs.Add(DescribePattern(f)); if (res.PrimaryDependent == null) res.PrimaryDependent = FName(f); }
                else if (IsMate(tn)) { res.Mates++; names.Add(FName(f)); res.MateNames.Add(FName(f)); }
                else { res.Features++; names.Add(FName(f)); res.FeatureNames.Add(FName(f)); }
            }
            if (res.PrimaryDependent == null) res.PrimaryDependent = names.FirstOrDefault();

            // ---- MATES: independently sweep the assembly's Mates folder for mates that reference the target (part
            //      features and assembly mates live in different docs, so GetChildren alone can under-count). ----
            var matchSet = MatchNames(target);
            var asm = model as AssemblyDoc;
            if (asm != null)
            {
                foreach (var m in AssemblyMatesReferencing(model, matchSet))
                { res.Mates++; names.Add(m); res.MateNames.Add(m); }
            }
            await emit("Tracer", null, "done",
                res.Sketches + " sketches · " + res.Patterns + " patterns · " + res.Features + " features" + (asm != null ? " · " + res.Mates + " mates" : ""));

            // ---- DRAWINGS: dimensions on sibling .SLDDRW files driven by the target (opened read-only, closed unsaved). ----
            await emit("Ledger", "checking drawings", "run", null);
            res.DrawingDims = CountDrawingDims(app, model, matchSet, out bool scanned, names);
            res.DrawingsScanned = scanned;
            await emit("Ledger", null, "done", scanned ? res.DrawingDims + " drawing dims" : "no drawing on disk");

            // sample the dependent names, capped (Character: say the number; don't dump 200 rows)
            foreach (var n in names) if (!res.Names.Contains(n)) res.Names.Add(n);
            if (res.Names.Count > 8) res.Names = res.Names.Take(8).ToList();

            res.Info = Verdict(res, asm != null);
            await emit("Tracer", null, "done", "read-only — nothing was changed");
            return res;
        }

        // ---- verdict line: answer first (Character #3), NAME the dependent not just the count (Character #2). ----
        private static string Verdict(ImpactResult r, bool isAsm)
        {
            string what = r.ByDimension ? "that dimension on " + r.Target : r.Target;
            if (r.Total == 0)
                return "Nothing depends on " + what + " — safe to change" + (r.DrawingsScanned ? "." : " (no drawing on disk to check).");
            var parts = new List<string>();
            // Patterns lead and are NAMED with their instance count ("LPattern1, a 3-instance linear pattern") — a
            // named dependent is what stops the user having to ask a follow-up.
            foreach (var d in r.PatternDescs) parts.Add(d);
            if (r.Sketches > 0) parts.Add(Count(r.Sketches, "sketch", "sketches") + Named(r.SketchNames));
            if (r.Features > 0) parts.Add(Count(r.Features, "feature", "features") + Named(r.FeatureNames));
            if (isAsm && r.Mates > 0) parts.Add(Count(r.Mates, "mate", "mates") + Named(r.MateNames));
            if (r.DrawingDims > 0) parts.Add(r.DrawingDims + (r.DrawingDims == 1 ? " drawing dim" : " drawing dims"));
            string tail = r.DrawingsScanned ? "" : " (no drawing on disk to check)";
            return "Changing " + what + " affects " + JoinParts(parts) + tail + ".";
        }

        private static string Count(int n, string one, string many) => n + " " + (n == 1 ? one : many);
        // name up to two dependents inline: "2 sketches (Sketch2, Sketch3)"
        private static string Named(List<string> ns)
        {
            var use = ns.Where(s => !string.IsNullOrEmpty(s)).Distinct().Take(2).ToList();
            if (use.Count == 0) return "";
            return " (" + string.Join(", ", use) + ")";
        }
        private static string JoinParts(List<string> parts)
        {
            if (parts.Count == 0) return "";
            if (parts.Count == 1) return parts[0];
            return string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts.Last();
        }

        // "LPattern1, a 3-instance linear pattern" — name + shape so the user never has to ask "which pattern?".
        private static string DescribePattern(Feature f)
        {
            string nm = FName(f);
            string tn = SafeType(f);
            bool mirror = tn.StartsWith("Mirror", StringComparison.OrdinalIgnoreCase);
            string kind = mirror ? "mirror" : (tn.IndexOf("Circular", StringComparison.OrdinalIgnoreCase) >= 0 ? "circular pattern" : "linear pattern");
            int inst = mirror ? 0 : PatternInstanceCount(f);
            return inst > 1 ? nm + ", a " + inst + "-instance " + kind : nm + " (" + kind + ")";
        }

        // total pattern instances (best-effort read; 0 if the feature-data can't be reached on this build).
        private static int PatternInstanceCount(Feature f)
        {
            try
            {
                object data = f.GetSpecificFeature2();
                var lin = data as ILinearPatternFeatureData;
                if (lin != null)
                {
                    int d1 = 0, d2 = 0;
                    try { d1 = lin.D1TotalInstances; } catch { }
                    try { d2 = lin.D2TotalInstances; } catch { }
                    if (d1 <= 0) d1 = 1; if (d2 <= 0) d2 = 1;
                    return d1 * d2;
                }
                var cir = data as ICircularPatternFeatureData;
                if (cir != null) { try { return cir.TotalInstances; } catch { return 0; } }
            }
            catch { }
            return 0;
        }

        // ---- selection: a picked feature, or a picked dimension whose owning feature we trace. ----
        private static Feature FromSelection(IModelDoc2 model, out bool byDim)
        {
            byDim = false;
            try
            {
                var sm = model.SelectionManager as SelectionMgr;
                if (sm == null) return null;
                int n = sm.GetSelectedObjectCount2(-1);
                for (int i = 1; i <= n; i++)
                {
                    object o = sm.GetSelectedObject6(i, -1);
                    var f = o as Feature;
                    if (f != null) return f;
                    var dd = o as DisplayDimension;
                    if (dd != null)
                    {
                        var dim = dd.GetDimension2(0) as Dimension;
                        if (dim != null) { var owner = OwnerOfDim(model, dim.FullName); if (owner != null) { byDim = true; return owner; } }
                    }
                }
            }
            catch { }
            return null;
        }

        // ---- intent match: the parser hands language; here we ground it against the REAL tree (name or kind synonym). ----
        private static List<Feature> MatchByIntent(List<Feature> feats, string intent)
        {
            var low = (intent ?? "").ToLowerInvariant();
            var hits = new List<Feature>();
            // 1) a real feature name spoken verbatim ("boss-extrude1")
            foreach (var f in feats) { var nm = FName(f).ToLowerInvariant(); if (nm.Length > 1 && low.Contains(nm)) hits.Add(f); }
            if (hits.Count > 0) return Distinct(hits);
            // 2) a kind word ("boss", "fillet", "hole"…) mapped to feature type/name hints
            foreach (var kv in KindHints)
                if (low.Contains(kv.Key))
                    foreach (var f in feats)
                    {
                        string t = SafeType(f).ToLowerInvariant(), nm = FName(f).ToLowerInvariant();
                        foreach (var h in kv.Value) if (t.Contains(h) || nm.Contains(h)) { hits.Add(f); break; }
                    }
            return Distinct(hits);
        }

        // kind-word -> type/name hints. Keeps "boss height" resolvable to a Boss-Extrude without guessing.
        private static readonly Dictionary<string, string[]> KindHints = new Dictionary<string, string[]>
        {
            { "boss", new[] { "boss", "extru" } },
            { "extrude", new[] { "extru" } },
            { "extrusion", new[] { "extru" } },
            { "pad", new[] { "extru", "boss" } },
            { "cut", new[] { "cut" } },
            { "pocket", new[] { "cut" } },
            { "hole", new[] { "hole" } },
            { "fillet", new[] { "fillet" } },
            { "round", new[] { "fillet" } },
            { "chamfer", new[] { "chamfer" } },
            { "revolve", new[] { "revol" } },
            { "shell", new[] { "shell" } },
            { "rib", new[] { "rib" } },
            { "sketch", new[] { "sketch", "profilefeature" } },
            { "pattern", new[] { "pattern" } },
            { "mirror", new[] { "mirror" } },
        };

        // ---- downstream closure: BFS over GetChildren (children = features that DEPEND on this one). Deduped, cycle-safe. ----
        private static List<Feature> Downstream(Feature target)
        {
            var outl = new List<Feature>();
            var seen = new HashSet<string> { FName(target) };
            var q = new Queue<Feature>();
            Enqueue(target, q, seen);
            while (q.Count > 0)
            {
                var f = q.Dequeue();
                outl.Add(f);
                Enqueue(f, q, seen);
            }
            return outl;
        }

        private static void Enqueue(Feature feat, Queue<Feature> q, HashSet<string> seen)
        {
            object ch = null; try { ch = feat.GetChildren(); } catch { }
            var arr = ch as object[];
            if (arr == null) return;
            foreach (var k in arr)
            {
                var kf = k as Feature; if (kf == null) continue;
                string nm = FName(kf);
                if (string.IsNullOrEmpty(nm) || seen.Contains(nm)) continue;
                seen.Add(nm); q.Enqueue(kf);
            }
        }

        // names a drawing dim / mate could reference back to the target: the target itself plus its parent sketches
        // (a boss's driving dims live on ITS sketch, which is a PARENT, not a child).
        private static HashSet<string> MatchNames(Feature target)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { FName(target) };
            object ps = null; try { ps = target.GetParents(); } catch { }
            var arr = ps as object[];
            if (arr != null)
                foreach (var p in arr)
                { var pf = p as Feature; if (pf != null && IsSketch(SafeType(pf))) set.Add(FName(pf)); }
            if (IsSketch(SafeType(target))) set.Add(FName(target));
            return set;
        }

        // ---- assembly mates that reference the target (walk the Mates folder — tree traversal works on this build;
        //      GetMates() does not — and check each mate's parents for a reference back to the target/its sketch). ----
        private static List<string> AssemblyMatesReferencing(IModelDoc2 model, HashSet<string> matchSet)
        {
            var found = new List<string>();
            try
            {
                var feat = model.FirstFeature() as Feature;
                while (feat != null)
                {
                    string tn = ""; try { tn = feat.GetTypeName2(); } catch { }
                    if (tn == "MateGroup")
                    {
                        var sub = feat.GetFirstSubFeature() as Feature;
                        while (sub != null)
                        {
                            object mp = null; try { mp = sub.GetParents(); } catch { }
                            var arr = mp as object[];
                            if (arr != null)
                                foreach (var p in arr)
                                { var pf = p as Feature; if (pf != null && matchSet.Contains(FName(pf))) { found.Add(FName(sub)); break; } }
                            sub = sub.GetNextSubFeature() as Feature;
                        }
                    }
                    feat = feat.GetNextFeature() as Feature;
                }
            }
            catch { }
            return found;
        }

        // ---- drawing dims driven by the target: sibling .SLDDRW on disk, opened read-only, closed WITHOUT saving. ----
        private static int CountDrawingDims(ISldWorks app, IModelDoc2 model, HashSet<string> matchSet, out bool scanned, List<string> names)
        {
            scanned = false; int count = 0;
            string modelPath = null; try { modelPath = model.GetPathName(); } catch { }
            if (string.IsNullOrEmpty(modelPath)) return 0;
            string dir = null; try { dir = Path.GetDirectoryName(modelPath); } catch { }
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return 0;

            string[] drawings; try { drawings = Directory.GetFiles(dir, "*.slddrw"); } catch { return 0; }
            if (drawings.Length == 0) return 0;

            foreach (var path in drawings)
            {
                bool wasOpen; var doc = OpenDrawing(app, path, out wasOpen);
                if (doc == null) continue;
                scanned = true;
                try
                {
                    var drw = doc as IDrawingDoc;
                    var view = drw.GetFirstView() as IView;
                    while (view != null)
                    {
                        object[] dims = null; try { dims = view.GetDisplayDimensions() as object[]; } catch { }
                        if (dims != null)
                            foreach (var o in dims)
                            {
                                var dd = o as DisplayDimension; if (dd == null) continue;
                                var dim = dd.GetDimension2(0) as Dimension; if (dim == null) continue;
                                if (DimBelongsTo(dim.FullName, matchSet)) { count++; names.Add(dim.FullName); }
                            }
                        view = view.GetNextView() as IView;
                    }
                }
                catch { }
                // NEVER save; close only what we opened, leaving the engineer's open docs alone (Rule #7).
                if (!wasOpen) { try { app.CloseDoc(path); } catch { } }
            }
            return count;
        }

        // a dim FullName is "D1@Sketch1@Part1" — the owner scopes are the '@'-segments after the dim id.
        private static bool DimBelongsTo(string fullName, HashSet<string> matchSet)
        {
            if (string.IsNullOrEmpty(fullName)) return false;
            var segs = fullName.Split('@');
            for (int i = 1; i < segs.Length; i++) if (matchSet.Contains(segs[i])) return true;
            return false;
        }

        private static IModelDoc2 OpenDrawing(ISldWorks app, string path, out bool wasOpen)
        {
            wasOpen = false;
            try { var ex = app.GetOpenDocumentByName(path) as IModelDoc2; if (ex != null) { wasOpen = true; return ex; } }
            catch { }
            int e = 0, w = 0;
            try
            {
                return app.OpenDoc6(path, (int)swDocumentTypes_e.swDocDRAWING,
                    (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref e, ref w) as IModelDoc2;
            }
            catch { return null; }
        }

        // ---- tree + type helpers ----
        private static List<Feature> AllFeatures(IModelDoc2 model)
        {
            var list = new List<Feature>();
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    // skip the default reference geometry / origin so intent matching + candidate lists stay clean
                    string tn = SafeType(f);
                    if (tn != "OriginProfileFeature" && tn != "CoordSys" && tn != "RefPlane" && tn != "DetailCabinet")
                        list.Add(f);
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch { }
            return list;
        }

        private static Feature OwnerOfDim(IModelDoc2 model, string dimFullName)
        {
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    var dd = f.GetFirstDisplayDimension() as DisplayDimension;
                    while (dd != null)
                    {
                        var d = dd.GetDimension2(0) as Dimension;
                        if (d != null && d.FullName == dimFullName) return f;
                        dd = f.GetNextDisplayDimension(dd) as DisplayDimension;
                    }
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch { }
            return null;
        }

        private static bool IsSketch(string tn) => tn == "ProfileFeature" || tn == "3DProfileFeature";
        private static bool IsPattern(string tn) => tn != null && (tn.IndexOf("Pattern", StringComparison.OrdinalIgnoreCase) >= 0 || tn.StartsWith("Mirror", StringComparison.OrdinalIgnoreCase));
        private static bool IsMate(string tn) => tn != null && tn.IndexOf("Mate", StringComparison.OrdinalIgnoreCase) >= 0;

        private static string FName(Feature f) { try { return f.Name ?? ""; } catch { return ""; } }
        private static string SafeType(Feature f) { try { return f.GetTypeName2() ?? ""; } catch { return ""; } }

        private static List<Feature> Distinct(List<Feature> fs)
        {
            var seen = new HashSet<string>(); var outl = new List<Feature>();
            foreach (var f in fs) { var n = FName(f); if (seen.Add(n)) outl.Add(f); }
            return outl;
        }

        private static string Sample(IEnumerable<string> names, int cap)
        {
            var list = names.Where(s => !string.IsNullOrEmpty(s)).Distinct().Take(cap).ToList();
            return list.Count == 0 ? "(none found)" : string.Join(", ", list);
        }
    }
}
