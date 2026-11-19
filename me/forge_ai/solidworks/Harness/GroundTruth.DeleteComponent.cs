using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT tree recount for delete_component (tool 30). Shares no code with the handler: it walks the assembly's
        // non-suppressed top-level components and reports the total plus a bolt count (own classifier). The harness diffs
        // run0 (baseline, bolts present) against run1 (handler deleted them) — the total must drop by exactly the bolt count
        // and the bolt count must reach 0 — and run2 proves idempotency (still gone).
        public static JObject MeasureDeleteComponent(IModelDoc2 model)
        {
            var mo = new JObject();
            var asm = model as AssemblyDoc;
            var names = new JArray();
            if (asm == null) { mo["error"] = "active doc is not an assembly"; mo["total"] = 0; mo["boltCount"] = 0; mo["names"] = names; return mo; }

            int total = 0, bolts = 0, flanges = 0, plates = 0;
            foreach (var o in (asm.GetComponents(true) as object[]) ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                string nm = null; try { nm = c.Name2; } catch { }
                total++; if (DcBolt(nm)) bolts++; if (DcFlange(nm)) flanges++; if (DcPlate(nm)) plates++;
                names.Add(nm ?? "");
            }
            mo["total"] = total;
            mo["boltCount"] = bolts;
            mo["flangeCount"] = flanges;
            mo["plateCount"] = plates;
            mo["names"] = names;
            return mo;
        }

        private static bool DcBolt(string n)
        {
            if (string.IsNullOrEmpty(n)) return false; n = n.ToLowerInvariant();
            foreach (var x in new[] { "nut", "washer", "plate" }) if (n.Contains(x)) return false;
            foreach (var h in new[] { "bolt", "screw", "hcs", "shcs", "cap", "socket", "hex", "stud" })
                if (n.Contains(h)) return true;
            return false;
        }

        private static bool DcFlange(string n)
        {
            if (string.IsNullOrEmpty(n)) return false; n = n.ToLowerInvariant();
            return n.Contains("flange");
        }

        // test-loop hedge fix (delete-tamper): proves DeleteComponent's new fuzzy-name fallback (no fixed "kind"
        // keyword for "plate") against upsize-flange.SLDASM's one plate, independent of the handler's own classifier.
        private static bool DcPlate(string n)
        {
            if (string.IsNullOrEmpty(n)) return false; n = n.ToLowerInvariant();
            return n.Contains("plate");
        }
    }
}
