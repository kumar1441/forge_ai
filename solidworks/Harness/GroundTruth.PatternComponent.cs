using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for the PatternComponent (pattern-a-component) handler. Shares NO code with
    /// PatternComponent.cs — it re-counts components, re-derives the seed file, and re-reads over-defined /
    /// rebuild status with its own traversal and its own fastener vocabulary.
    ///
    /// The harness diffs run0 (baseline) → run1 (after Run) → run2 (after a second Run):
    ///   • seedFileInstanceCount and totalComponents RISE by the number of instances the pattern added (N-1 for an
    ///     N-hole circle seeded by one bolt), then hold flat on run2 (idempotent — the Forge-Pattern feature blocks
    ///     a second pattern).
    ///   • overDefinedComponents stays 0, rebuildErrors stays 0.
    ///   • hasForgePattern is false at run0, true at run1 and run2.
    ///
    /// The seed FILE is chosen the same way the handler documents (the first non-pattern-instance fastener), but with
    /// wholly independent code — so the two instance counts are two unrelated tallies of the same physical thing.
    /// </summary>
    public static partial class GroundTruth
    {
        // own fastener vocabulary (declared here so a change to the handler can't silently move this count)
        private static readonly string[] PcFastenerHints =
            { "bolt", "screw", "hcs", "shcs", "capscrew", "fastener", "hex", "socket", "machine screw",
              "stud", "sems", "allen", "grub", "cheese", "din", "iso", "b18", "bulong", "vit", "vis", "boulon", "goujon" };
        private static readonly string[] PcNotFastener =
            { "nut", "ecrou", "washer", "rondelle", "clavette", "key", "pin", "goupille",
              "4035", "4032", "4033", "4034", "7089", "7090", "7091", "8738" };

        public static JObject MeasurePatternComponent(ISldWorks app, IModelDoc2 model)
        {
            var d = new JObject();
            var asm = model as AssemblyDoc;
            if (asm == null) { d["error"] = "active doc is not an assembly"; return d; }

            object[] all = asm.GetComponents(false) as object[];
            object[] top = asm.GetComponents(true) as object[];

            // ---- component tallies + per-path instance counts (own traversal) ----
            int totalComponents = 0;
            var pathCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            string seedPath = null;   // first non-pattern-instance fastener's file, in traversal order
            foreach (var o in all ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { }
                if (sup) continue;
                totalComponents++;
                string path = null; try { path = c.GetPathName(); } catch { }
                if (!string.IsNullOrEmpty(path)) { int n; pathCounts.TryGetValue(path, out n); pathCounts[path] = n + 1; }

                if (seedPath == null && PcIsFastener(PcName(c)))
                {
                    bool inst = false; try { inst = c.IsPatternInstance(); } catch { }
                    if (!inst && !string.IsNullOrEmpty(path)) seedPath = path;
                }
            }
            int seedFileInstanceCount = 0;
            if (seedPath != null) pathCounts.TryGetValue(seedPath, out seedFileInstanceCount);

            // ---- over-defined (top-level) + rebuild errors (independent read) ----
            int overDefined = 0;
            foreach (var o in top ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                int st = (int)swConstrainedStatus_e.swUnderConstrained; try { st = c.GetConstrainedStatus(); } catch { }
                if (st == (int)swConstrainedStatus_e.swOverConstrained || st == (int)swConstrainedStatus_e.swNoSolution || st == (int)swConstrainedStatus_e.swInvalidSolution) overDefined++;
            }
            int rebuildErrors = 0; try { rebuildErrors = model.Extension.GetWhatsWrongCount(); } catch { }

            // ---- the Forge-Pattern feature (own feature walk) ----
            bool hasForgePattern = false;
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                string nm = null; try { nm = f.Name; } catch { }
                if (string.Equals(nm, "Forge-Pattern", StringComparison.OrdinalIgnoreCase)) { hasForgePattern = true; break; }
                f = f.GetNextFeature() as Feature;
            }

            d["topLevelComponents"] = top == null ? 0 : top.Length;
            d["totalComponents"] = totalComponents;
            d["seedFileInstanceCount"] = seedFileInstanceCount;
            d["overDefinedComponents"] = overDefined;
            d["rebuildErrors"] = rebuildErrors;
            d["hasForgePattern"] = hasForgePattern;

            d["fingerprint"] = new JObject
            {
                ["topLevelComponents"] = top == null ? 0 : top.Length,
                ["totalComponents"] = totalComponents,
                ["overDefinedComponents"] = overDefined
            };
            return d;
        }

        private static bool PcIsFastener(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.ToLowerInvariant();
            foreach (var x in PcNotFastener) if (n.Contains(x)) return false;
            foreach (var h in PcFastenerHints) if (n.Contains(h)) return true;
            return false;
        }
        private static string PcName(Component2 c) { try { return c.Name2; } catch { return null; } }
    }
}
