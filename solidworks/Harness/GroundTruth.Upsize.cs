using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground-truth for the bolt-upsize handler. Shares NO code with Upsize.cs: it re-reads each bolt
    /// instance's ACTIVE configuration straight from Component2.ReferencedConfiguration, parses the size token itself,
    /// and reports the size distribution (how many bolts are each M-size). The harness diffs run0 (baseline) against
    /// run1 to prove the target bolts moved from the old size to the new size, and run1 against run2 for idempotency.
    /// Deliberately size-agnostic — it does not know the requested from/to, so it can never "agree by construction"
    /// with the handler; it just tells the truth about what size each bolt currently is.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureUpsize(ISldWorks app, IModelDoc2 model)
        {
            var mo = new JObject();
            var asm = model as AssemblyDoc;
            if (asm == null) { mo["error"] = "active doc is not an assembly"; return mo; }

            var arr = new JArray();
            var bySize = new Dictionary<string, int>();
            int boltCount = 0;
            foreach (var o in (asm.GetComponents(false) as object[]) ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                string nm = null; try { nm = c.Name2; } catch { }
                if (!UpBolt(nm)) continue;

                string cfg = null; try { cfg = c.ReferencedConfiguration; } catch { }
                string sizeTok = ParseSize(cfg);              // prefer the ACTIVE config (that's what the handler switches)
                if (sizeTok == null) sizeTok = ParseSize(nm); // fall back to the component name
                string key = sizeTok ?? "?";

                boltCount++;
                bySize[key] = (bySize.TryGetValue(key, out int n) ? n : 0) + 1;
                var jo = new JObject { ["name"] = nm, ["config"] = cfg, ["sizeMm"] = sizeTok ?? "" };
                arr.Add(jo);
            }

            var sizes = new JObject();
            foreach (var kv in bySize) sizes[kv.Key] = kv.Value;

            mo["boltCount"] = boltCount;
            mo["bySize"] = sizes;         // e.g. { "6": 0, "8": 4 } after a full M6->M8 upsize
            mo["boltConfigs"] = arr;      // per-instance name/config/size for eyeballing
            return mo;
        }

        // own metric-size parse — "M8" / "M8x30" / "-M8-", not "M80"; returns the nominal digits ("8") or null
        private static string ParseSize(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var m = Regex.Match(text.ToLowerInvariant(), @"(?<![a-z0-9])m(\d+)(?![0-9])");
            return m.Success ? m.Groups[1].Value : null;
        }

        // own bolt/screw classifier (excludes nuts/washers) — independent of Upsize.IsBoltName
        private static bool UpBolt(string n)
        {
            if (string.IsNullOrEmpty(n)) return false; n = n.ToLowerInvariant();
            foreach (var x in new[] { "nut", "ecrou", "washer", "rondelle" }) if (n.Contains(x)) return false;
            foreach (var h in new[] { "bolt", "screw", "hcs", "shcs", "cap", "socket", "hex", "stud", "vis", "boulon", "din", "iso", "b18" })
                if (n.Contains(h)) return true;
            return false;
        }
    }
}
