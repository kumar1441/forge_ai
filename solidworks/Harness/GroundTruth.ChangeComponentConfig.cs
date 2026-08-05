using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT per-instance referenced-config census for change_component_config (tool 39). Shares NO code with
        // the handler: it re-reads every non-suppressed top-level component's Component2.ReferencedConfiguration straight
        // from the tree and publishes name -> config plus a bolt flag (its own classifier). The harness diffs run0
        // (baseline) against run1 to prove the TARGETED instances moved to the requested config while the NON-targeted
        // ones (e.g. the plate) held byte-for-byte, and run1 against run2 for idempotency. It does not know the requested
        // from/to config, so it can never agree with the handler by construction — it just reports what each instance is.
        public static JObject MeasureChangeComponentConfig(IModelDoc2 model)
        {
            var mo = new JObject();
            var asm = model as AssemblyDoc;
            var arr = new JArray();
            var byConfig = new JObject();
            if (asm == null) { mo["error"] = "active doc is not an assembly"; mo["components"] = arr; mo["byConfig"] = byConfig; return mo; }

            int total = 0, bolts = 0;
            foreach (var o in (asm.GetComponents(true) as object[]) ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                string nm = null; try { nm = c.Name2; } catch { }
                string cfg = null; try { cfg = c.ReferencedConfiguration; } catch { }
                cfg = string.IsNullOrEmpty(cfg) ? "(none)" : cfg;
                bool isBolt = CccBolt(nm);
                total++; if (isBolt) bolts++;
                arr.Add(new JObject { ["name"] = nm ?? "", ["config"] = cfg, ["isBolt"] = isBolt });
                byConfig[cfg] = (byConfig[cfg] != null ? (int)byConfig[cfg] : 0) + 1;
            }

            mo["total"] = total;
            mo["boltCount"] = bolts;
            mo["components"] = arr;      // per-instance name/config/isBolt for the scoping proof
            mo["byConfig"] = byConfig;   // config -> count spread
            return mo;
        }

        // own bolt/screw classifier (excludes nuts/washers/plates) — independent of the handler's MatchesKind
        private static bool CccBolt(string n)
        {
            if (string.IsNullOrEmpty(n)) return false; n = n.ToLowerInvariant();
            foreach (var x in new[] { "nut", "washer", "plate" }) if (n.Contains(x)) return false;
            foreach (var h in new[] { "bolt", "screw", "hcs", "shcs", "cap", "socket", "hex", "stud" })
                if (n.Contains(h)) return true;
            return false;
        }
    }
}
