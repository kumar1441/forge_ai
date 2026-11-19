using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for the AutoNumberParts (tools #138/#139) WRITE handler. Shares NO code with
    /// AutoNumberParts.cs — it re-enumerates every unique part FILE with its own traversal and its OWN property read
    /// (a separate name literal, AnPartNoProps), and tallies how many parts carry a part number vs how many are still
    /// missing one. Because it never writes, rebuilds, or saves, run0 → run1 → run2 deltas prove the write invariants:
    ///
    ///     run1.numberedParts − run0.numberedParts  ==  handler.Assigned      (exactly the parts it claims it numbered)
    ///     run1.missingParts   ==  run0.missingParts − handler.Assigned        (missing count drops by that many)
    ///     run1.collisions == 0                                                 (no duplicate PN value introduced)
    ///     run2 == run1  AND  run2Result.Assigned == 0                          (idempotent — a rerun numbers nothing)
    ///
    /// NOTE: the harness opens the model and closes WITHOUT saving, so within one session run1/run2 observe the
    /// in-memory property writes — sufficient for this verification; nothing persists to disk.
    /// </summary>
    public static partial class GroundTruth
    {
        // own recognised part-number property names — a separate literal from the handler's PartNoProps
        private static readonly string[] AnPartNoProps = { "PartNo", "PartNumber", "Part Number", "Part No", "Number", "DrawingNo", "Drawing Number" };

        public static JObject MeasureAutoNumberParts(ISldWorks app, IModelDoc2 model)
        {
            var d = new JObject();
            var asm = model as AssemblyDoc;
            if (asm == null) { d["error"] = "active doc is not an assembly"; return d; }

            object[] all = asm.GetComponents(false) as object[];

            int uniqueParts = 0, numberedParts = 0, missingParts = 0;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pnToFiles = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);  // PN value -> distinct files
            var missingNames = new JArray();

            foreach (var o in all ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { }
                if (sup) continue;
                string path = null; try { path = c.GetPathName(); } catch { }
                if (string.IsNullOrEmpty(path) || seen.Contains(path)) continue; seen.Add(path);
                var pd = c.GetModelDoc2() as PartDoc; if (pd == null) continue;
                var md = pd as IModelDoc2;
                uniqueParts++;
                string nm = null; try { nm = c.Name2; } catch { }
                if (string.IsNullOrEmpty(nm)) nm = Path.GetFileNameWithoutExtension(path);

                string pn = AnReadPartNumber(md);
                if (string.IsNullOrWhiteSpace(pn))
                {
                    missingParts++;
                    if (missingNames.Count < 10) missingNames.Add(nm);
                }
                else
                {
                    numberedParts++;
                    string v = pn.Trim();
                    if (!pnToFiles.ContainsKey(v)) pnToFiles[v] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    pnToFiles[v].Add(path);
                }
            }

            int collisions = 0;                    // same PN value carried by >1 distinct file
            foreach (var kv in pnToFiles) if (kv.Value.Count > 1) collisions++;
            int distinctNumbers = pnToFiles.Count;

            d["uniqueParts"] = uniqueParts;
            d["numberedParts"] = numberedParts;
            d["missingParts"] = missingParts;
            d["distinctNumbers"] = distinctNumbers;
            d["collisions"] = collisions;
            d["missingNames"] = missingNames;
            d["fingerprint"] = new JObject
            {
                ["uniqueParts"] = uniqueParts,
                ["numberedParts"] = numberedParts
            };
            return d;
        }

        // own property scan — separate literal + separate walk from the handler's ReadPartNumber
        private static string AnReadPartNumber(IModelDoc2 md)
        {
            if (md == null) return null;
            string cfg = ""; try { cfg = ((Configuration)md.GetActiveConfiguration()).Name; } catch { }
            foreach (var scope in new[] { cfg, "" })
            {
                CustomPropertyManager cpm = null;
                try { cpm = md.Extension.CustomPropertyManager[scope ?? ""]; } catch { }
                if (cpm == null) continue;
                foreach (var prop in AnPartNoProps)
                {
                    string val = null, resolved = null;
                    try { cpm.Get4(prop, false, out val, out resolved); } catch { }
                    string v = !string.IsNullOrWhiteSpace(resolved) ? resolved : val;
                    if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
                }
            }
            return null;
        }
    }
}
