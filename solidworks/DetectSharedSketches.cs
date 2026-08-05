using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public class DetectSharedSketchesResult
    {
        public bool IsPart;
        public int SharedCount;          // how many sketches drive >=2 features
        public string[] SharedSketches;  // names of the shared sketches
        public int MaxConsumers;         // largest consumer count among shared sketches
        public string Verdict;           // "shared" | "none"
        public string Diag;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 252 — detect_shared_sketches (READ). One sketch absorbed by / driving MULTIPLE features is the "edit or
    /// delete for feature A silently breaks feature B" hazard: the tree looks normal, but two features hang off one
    /// sketch. This surfaces them (offer, don't touch): walk the tree, and for each sketch (ProfileFeature) count the
    /// REAL features that consume it via IFeature.GetChildren — a sketch with >=2 consumers is shared. Read-only. The
    /// INDEPENDENT GT crosses the other direction (IFeature.GetParents inverted into a sketch->consumers map), so a
    /// disagreement would expose a bad walk rather than a bad rule. Part-only (sketches live in parts).
    /// </summary>
    public static class DetectSharedSketches
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            // Requires a SHARED/REUSED noun so it never shadows get_sketches (a plain sketch LIST) or diagnose_sketch.
            if (Regex.IsMatch(c, @"\bshared?\s+sketch")) return true;                       // "shared sketch(es)"
            if (Regex.IsMatch(c, @"\bsketch(es)?\s+(that|which)?\s*(is|are|get|gets)?\s*shared\b")) return true; // "sketches shared / that are shared"
            if (Regex.IsMatch(c, @"\breuse[ds]?\s+sketch")) return true;                    // "reused sketch"
            if (Regex.IsMatch(c, @"\bsketch(es)?\b") &&
                Regex.IsMatch(c, @"\b(multiple|more than one|two or more|several)\s+features?\b")) return true;   // "a sketch driving multiple features"
            return false;
        }

        public static async Task<DetectSharedSketchesResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new DetectSharedSketchesResult();
            if (model == null) { res.Error = "Open a part to check for shared sketches."; return res; }
            if (!(model is PartDoc)) { res.Error = "Shared-sketch detection is a part check - open the part, not the assembly."; return res; }
            res.IsPart = true;

            await emit("Scout", "scanning sketches for shared use", "run", null);

            // Gather every sketch feature — flat walk PLUS absorbed sub-features (a single-use sketch hides under its
            // feature; a SHARED one surfaces flat, but sweep both so nothing is missed) — deduped by name.
            var sketches = new Dictionary<string, Feature>(StringComparer.OrdinalIgnoreCase);
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                CollectSketch(f, sketches);
                var sub = f.GetFirstSubFeature() as Feature;
                while (sub != null) { CollectSketch(sub, sketches); sub = sub.GetNextSubFeature() as Feature; }
                f = f.GetNextFeature() as Feature;
            }

            var shared = new List<KeyValuePair<string, List<string>>>();
            foreach (var kv in sketches)
            {
                var consumers = RealChildrenNames(kv.Value);
                if (consumers.Count >= 2) shared.Add(new KeyValuePair<string, List<string>>(kv.Key, consumers));
            }

            res.SharedCount = shared.Count;
            res.SharedSketches = shared.Select(s => s.Key).ToArray();
            res.MaxConsumers = shared.Count == 0 ? 0 : shared.Max(s => s.Value.Count);
            res.Verdict = shared.Count > 0 ? "shared" : "none";

            var diag = new StringBuilder("verdict=" + res.Verdict + " sketchesScanned=" + sketches.Count + " shared=" + res.SharedCount);
            foreach (var s in shared) diag.Append(" | " + s.Key + "->[" + string.Join(",", s.Value) + "]");
            res.Diag = diag.ToString();

            await emit("Scout", null, "done",
                shared.Count == 0
                    ? ("no shared sketches - each of " + sketches.Count + " sketches drives one feature")
                    : (res.SharedCount + " shared sketch" + (res.SharedCount == 1 ? "" : "es") + " - " +
                       string.Join("; ", shared.Select(s => s.Key + " drives " + s.Value.Count + " features (" + string.Join(", ", s.Value) + ")"))));

            res.Info = BuildInfo(res, shared);
            return res;
        }

        private static void CollectSketch(Feature f, Dictionary<string, Feature> into)
        {
            string tn = null; try { tn = f.GetTypeName2(); } catch { }
            if (tn == "ProfileFeature" || tn == "3DProfileFeature")
            {
                string n = null; try { n = f.Name; } catch { }
                if (!string.IsNullOrEmpty(n) && !into.ContainsKey(n)) into[n] = f;
            }
        }

        // real features (not folders / origin scaffold / the sketch itself) that CONSUME this sketch
        private static List<string> RealChildrenNames(Feature sketch)
        {
            var names = new List<string>();
            object[] kids = null; try { kids = sketch.GetChildren() as object[]; } catch { }
            foreach (var o in kids ?? new object[0])
            {
                var cf = o as Feature; if (cf == null) continue;
                string tn = null; try { tn = cf.GetTypeName2(); } catch { }
                if (string.IsNullOrEmpty(tn) || !IsConsumingFeature(tn)) continue;
                string n = null; try { n = cf.Name; } catch { }
                if (!string.IsNullOrEmpty(n) && !names.Contains(n)) names.Add(n);
            }
            return names;
        }

        // a consumer is a real geometry feature, not a folder/scaffold/another sketch
        private static bool IsConsumingFeature(string tn)
        {
            if (tn.EndsWith("Folder", StringComparison.OrdinalIgnoreCase)) return false;
            switch (tn)
            {
                case "ProfileFeature": case "3DProfileFeature":
                case "RefPlane": case "RefAxis": case "OriginProfileFeature": case "CoordSys": return false;
                default: return true;
            }
        }

        private static string BuildInfo(DetectSharedSketchesResult r, List<KeyValuePair<string, List<string>>> shared)
        {
            if (r.SharedCount == 0)
                return "No shared sketches - every sketch drives exactly one feature. Editing any sketch affects only its own feature.";
            var sb = new StringBuilder();
            sb.Append(r.SharedCount + " shared sketch" + (r.SharedCount == 1 ? "" : "es") + " found - editing or deleting one silently changes multiple features:");
            foreach (var s in shared)
                sb.Append("\n  " + s.Key + " drives " + s.Value.Count + " features: " + string.Join(", ", s.Value));
            sb.Append("\nForge won't touch a shared sketch without confirmation.");
            return sb.ToString();
        }
    }
}
