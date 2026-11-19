using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for the SuppressComponents (strip-an-assembly) WRITE handler. Shares NO code with
    /// SuppressComponents.cs — it re-enumerates EVERY component (suppressed AND active) with its own traversal, reads
    /// each one's live suppression state via IsSuppressed(), and tallies suppressed-by-kind with its OWN name
    /// classification. The handler is a WRITE handler, so the "fingerprint" is not a read-only proof — instead it
    /// captures the resulting SUPPRESSION STATE, so the harness can assert the delta:
    ///
    ///     run1.suppressedComponents − run0.suppressedComponents  ==  handler.Suppressed
    ///
    /// i.e. exactly the components the handler claims to have suppressed became suppressed, counted two independent
    /// ways. run2 == run1 proves idempotency (a rerun suppresses nothing more).
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureSuppressComponents(ISldWorks app, IModelDoc2 model)
        {
            var d = new JObject();
            var asm = model as AssemblyDoc;
            if (asm == null) { d["error"] = "active doc is not an assembly"; return d; }

            try { model.ForceRebuild3(false); } catch { }   // measure the SOLVED state

            object[] all = asm.GetComponents(false) as object[];
            object[] top = asm.GetComponents(true) as object[];

            int total = 0, suppressed = 0, active = 0, suppressedFasteners = 0;
            var byKind = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);   // suppressed-only, by kind
            foreach (var o in all ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                total++;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { }
                if (!sup) { active++; continue; }
                suppressed++;
                string nm = null; try { nm = c.Name2; } catch { }
                string kind = SuppKindLocal(nm);
                int n; byKind.TryGetValue(kind, out n); byKind[kind] = n + 1;
                if (kind == "bolt" || kind == "nut" || kind == "washer") suppressedFasteners++;
            }

            var kindObj = new JObject();
            foreach (var kv in byKind) kindObj[kv.Key] = kv.Value;

            int rebuildErrors = 0; try { rebuildErrors = model.Extension.GetWhatsWrongCount(); } catch { }

            d["totalComponents"] = total;
            d["suppressedComponents"] = suppressed;      // the cross-check target: run1 − run0 == handler.Suppressed
            d["activeComponents"] = active;
            d["suppressedFasteners"] = suppressedFasteners;
            d["suppressedByKind"] = kindObj;

            // ---- suppression-state fingerprint: run1 vs run0 delta = the write; run2 == run1 = idempotent ----
            d["fingerprint"] = new JObject
            {
                ["topLevelComponents"] = top == null ? 0 : top.Length,
                ["suppressedComponents"] = suppressed,
                ["rebuildErrors"] = rebuildErrors
            };
            return d;
        }

        // OWN name classification — nothing shared with SuppressComponents.Classify (independent second opinion).
        private static readonly string[] SuppBoltHints = { "bolt", "screw", "hcs", "shcs", "cap", "socket", "hex", "stud", "vis", "boulon", "bulong", "iso", "din", "b18" };
        private static string SuppKindLocal(string n)
        {
            if (string.IsNullOrEmpty(n)) return "other";
            n = n.ToLowerInvariant();
            if (n.Contains("nut") || n.Contains("ecrou")) return "nut";
            if (n.Contains("washer") || n.Contains("rondelle")) return "washer";
            foreach (var h in SuppBoltHints) if (n.Contains(h)) return "bolt";
            return "other";
        }
    }
}
