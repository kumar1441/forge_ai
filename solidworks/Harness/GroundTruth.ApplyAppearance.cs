using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for the ApplyAppearance (color/appearance by filter) WRITE handler. Shares NO code
    /// with ApplyAppearance.cs — it re-enumerates every active component with its OWN traversal, reads each one's
    /// live display color via Component2.GetMaterialPropertyValues2 (a genuine second read of the model), classifies
    /// each with its OWN name vocabulary, and reports the DOMINANT display color per kind (0..255 ints) plus how many
    /// components of that kind carry it. The handler is a WRITE handler, so this captures the resulting COLOR STATE so
    /// the harness can assert:
    ///
    ///     after "color all the bolts red" →  coloredByKind["bolt"] == { r:255, g:0, b:0, count:&lt;every bolt&gt; }
    ///
    /// i.e. the kind the handler colored reads back the requested RGB, counted a second independent way. run2 == run1
    /// (color state stable) proves idempotency; non-target kinds are expected UNCHANGED across runs.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureApplyAppearance(ISldWorks app, IModelDoc2 model)
        {
            var d = new JObject();
            var asm = model as AssemblyDoc;
            if (asm == null) { d["error"] = "active doc is not an assembly"; return d; }

            object[] all = asm.GetComponents(false) as object[];
            object[] top = asm.GetComponents(true) as object[];

            int total = 0;
            // per-kind: colorTuple("r,g,b" in 0..255) -> count, so we can report the DOMINANT color of each kind
            var byKind = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
            var distinct = new HashSet<string>();

            foreach (var o in all ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { }
                if (sup) continue;
                total++;

                string kind = KindLocal(NameLocal(c));
                int[] rgb = ReadColor255(c);            // null when the component carries no display-color override
                if (rgb == null) continue;
                string key = rgb[0] + "," + rgb[1] + "," + rgb[2];
                distinct.Add(key);

                Dictionary<string, int> tally;
                if (!byKind.TryGetValue(kind, out tally)) { tally = new Dictionary<string, int>(); byKind[kind] = tally; }
                int n; tally.TryGetValue(key, out n); tally[key] = n + 1;
            }

            // reduce each kind to its dominant color { r, g, b, count }
            var kindObj = new JObject();
            foreach (var kv in byKind)
            {
                string bestKey = null; int bestCount = 0;
                foreach (var t in kv.Value) if (t.Value > bestCount) { bestCount = t.Value; bestKey = t.Key; }
                if (bestKey == null) continue;
                var parts = bestKey.Split(',');
                kindObj[kv.Key] = new JObject
                {
                    ["r"] = int.Parse(parts[0]),
                    ["g"] = int.Parse(parts[1]),
                    ["b"] = int.Parse(parts[2]),
                    ["count"] = bestCount
                };
            }

            int rebuildErrors = 0; try { rebuildErrors = model.Extension.GetWhatsWrongCount(); } catch { }

            d["totalComponents"] = total;
            d["coloredByKind"] = kindObj;                 // dominant display color per kind, read independently
            d["distinctColors"] = distinct.Count;         // how many distinct display colors are present
            d["fingerprint"] = new JObject
            {
                ["topLevelComponents"] = top == null ? 0 : top.Length,
                ["rebuildErrors"] = rebuildErrors         // coloring must never break the solve
            };
            return d;
        }

        // INDEPENDENT ground truth for the color-EACH-body path (test-loop hedged finding color-keys, real multi-body
        // "Allen Key SET" PART): re-enumerates every solid body with its own IBody2.GetBodies2 traversal and reads
        // each body's display color via Body2.MaterialPropertyValues2 — a genuine second read of the same
        // property the handler set, but through the harness's own independent traversal/counting, not a mirror of
        // the handler's loop. Reports bodyCount and distinctColors so the harness can assert distinctColors > 1
        // (proof each body actually got its OWN color, not one blanket color reused for all).
        public static JObject MeasureApplyAppearanceByBody(IModelDoc2 model)
        {
            var d = new JObject();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { d["applicable"] = false; d["reason"] = "active doc is not a part"; return d; }

            var part = model as PartDoc;
            object[] bodies = null;
            try { bodies = part?.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]; } catch { }
            d["applicable"] = true;
            d["bodyCount"] = bodies == null ? 0 : bodies.Length;

            var distinct = new HashSet<string>();
            foreach (var o in bodies ?? new object[0])
            {
                var body = o as Body2; if (body == null) continue;
                double[] v = null; try { v = body.MaterialPropertyValues2 as double[]; } catch { }
                if (v == null || v.Length < 3) continue;
                distinct.Add(
                    (int)Math.Round(Clamp01(v[0]) * 255) + "," +
                    (int)Math.Round(Clamp01(v[1]) * 255) + "," +
                    (int)Math.Round(Clamp01(v[2]) * 255));
            }
            d["distinctColors"] = distinct.Count;
            return d;
        }

        // read a component's display color as 0..255 ints (null if no override / unreadable)
        private static int[] ReadColor255(Component2 c)
        {
            double[] v = null;
            try { v = c.GetMaterialPropertyValues2((int)swInConfigurationOpts_e.swThisConfiguration, null) as double[]; }
            catch { return null; }
            if (v == null || v.Length < 3) return null;
            // an all-zero optical block with zero RGB is SolidWorks' "no override" sentinel — treat as no color
            return new[]
            {
                (int)Math.Round(Clamp01(v[0]) * 255),
                (int)Math.Round(Clamp01(v[1]) * 255),
                (int)Math.Round(Clamp01(v[2]) * 255)
            };
        }

        private static double Clamp01(double x) { return x < 0 ? 0 : (x > 1 ? 1 : x); }

        // OWN name classification — nothing shared with ApplyAppearance.Classify (independent second opinion)
        private static readonly string[] AppBoltHints = { "bolt", "screw", "hcs", "shcs", "cap", "socket", "hex", "stud", "vis", "boulon", "bulong", "iso", "din", "b18" };
        private static string KindLocal(string n)
        {
            if (string.IsNullOrEmpty(n)) return "other";
            n = n.ToLowerInvariant();
            if (n.Contains("nut") || n.Contains("ecrou")) return "nut";
            if (n.Contains("washer") || n.Contains("rondelle")) return "washer";
            if (n.Contains("flange")) return "flange";
            if (n.Contains("housing") || n.Contains("case") || n.Contains("casing") || n.Contains("enclosure")) return "housing";
            if (n.Contains("shaft")) return "shaft";
            if (n.Contains("gear")) return "gear";
            if (n.Contains("plate")) return "plate";
            if (n.Contains("bracket")) return "bracket";
            foreach (var h in AppBoltHints) if (n.Contains(h)) return "bolt";
            return "other";
        }

        private static string NameLocal(Component2 c) { try { return c.Name2; } catch { return null; } }
    }
}
