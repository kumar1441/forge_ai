using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for the FindDupes (find-duplicate-components) handler. Shares NO code with
    /// FindDupes.cs — it re-counts total active components and distinct external file paths from its own
    /// traversal, and independently tallies part-number → distinct-file collisions with its own property read.
    /// The harness asserts handler.UniqueParts == this.uniqueFilePaths (two unrelated counts of the same thing).
    ///
    /// Because MeasureFindDupes never rebuilds, edits, or saves, an identical fingerprint on run1 and run2 proves
    /// the handler is read-only (the harness diffs the two GroundTruth blobs).
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureFindDupes(ISldWorks app, IModelDoc2 model)
        {
            var d = new JObject();
            var asm = model as AssemblyDoc;
            if (asm == null) { d["error"] = "active doc is not an assembly"; return d; }

            object[] all = asm.GetComponents(false) as object[];
            object[] top = asm.GetComponents(true) as object[];

            int totalComponents = 0, virtualParts = 0;
            var pathCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);   // file path -> instance count
            foreach (var o in all ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { }
                if (sup) continue;
                totalComponents++;
                bool virt = false; try { virt = c.IsVirtual; } catch { }
                string path = null; try { path = c.GetPathName(); } catch { }
                if (virt || string.IsNullOrEmpty(path)) { virtualParts++; continue; }
                int n; pathCounts.TryGetValue(path, out n); pathCounts[path] = n + 1;
            }

            int reusedParts = 0, maxReuse = 0;
            foreach (var kv in pathCounts) { if (kv.Value > 1) reusedParts++; if (kv.Value > maxReuse) maxReuse = kv.Value; }

            // ---- INDEPENDENT part-number collision tally: same PN value on >1 DISTINCT file ----
            string[] pnProps = { "PartNo", "PartNumber", "Part Number", "Part No", "Number", "DrawingNo", "Drawing Number" };
            var pnToPaths = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var readDoc = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // path -> PN (one read per file)
            int numbered = 0;
            foreach (var o in all ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                bool virt = false; try { virt = c.IsVirtual; } catch { }
                string path = null; try { path = c.GetPathName(); } catch { }
                if (virt || string.IsNullOrEmpty(path) || readDoc.ContainsKey(path)) continue;
                IModelDoc2 md = null; try { md = c.GetModelDoc2() as IModelDoc2; } catch { }
                string pn = md == null ? null : ReadPnLocal(md, pnProps);
                readDoc[path] = pn ?? "";
                if (string.IsNullOrWhiteSpace(pn)) continue;
                numbered++;
                if (!pnToPaths.ContainsKey(pn)) pnToPaths[pn] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                pnToPaths[pn].Add(path);
            }
            int pnCollisions = 0;
            foreach (var kv in pnToPaths) if (kv.Value.Count > 1) pnCollisions++;

            d["totalComponents"] = totalComponents;
            d["uniqueFilePaths"] = pathCounts.Count;      // the cross-check target for handler.UniqueParts
            d["virtualParts"] = virtualParts;
            d["reusedParts"] = reusedParts;
            d["maxReuse"] = maxReuse;
            d["numberedParts"] = numbered;
            d["pnCheckable"] = numbered > 0;
            d["pnCollisions"] = pnCollisions;

            // ---- read-only fingerprint: unchanged across run1/run2 iff the handler wrote nothing ----
            int mateFeatures = CountMateFeaturesLocal(model);
            int featCount = CountFeaturesLocal(model);
            d["fingerprint"] = new JObject
            {
                ["totalComponents"] = totalComponents,
                ["topLevelComponents"] = top == null ? 0 : top.Length,
                ["uniqueFilePaths"] = pathCounts.Count,
                ["mateFeatures"] = mateFeatures,
                ["featureCount"] = featCount
            };
            return d;
        }

        // own inline part-number read (nothing shared with FindDupes.ReadPartNumber)
        private static string ReadPnLocal(IModelDoc2 md, string[] props)
        {
            string cfg = ""; try { cfg = ((Configuration)md.GetActiveConfiguration()).Name; } catch { }
            foreach (var scope in new[] { cfg, "" })
            {
                CustomPropertyManager cpm = null;
                try { cpm = md.Extension.CustomPropertyManager[scope ?? ""]; } catch { }
                if (cpm == null) continue;
                foreach (var p in props)
                {
                    string val = null, resolved = null;
                    try { cpm.Get4(p, false, out val, out resolved); } catch { }
                    string v = !string.IsNullOrWhiteSpace(resolved) ? resolved : val;
                    if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
                }
            }
            return null;
        }
    }
}
