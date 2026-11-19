using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT filter resolution — shares NO code with SelectByFilter. Given the kind fragment (parsed from the
        // intent), counts matching components by its OWN name test, returning matched + active. A handler that over/
        // under-matches shows as a count mismatch. Only the "bolt" kind is needed for the current fixture, but the
        // shape generalises.
        // parse which kind the intent asks for (mirrors the handler's kind set; the COUNT is what's verified)
        public static string FilterKind(string intent)
        {
            string c = (intent ?? "").ToLowerInvariant();
            foreach (var k in new[] { "bolt", "nut", "washer", "flange", "shaft", "gear" })
                if (System.Text.RegularExpressions.Regex.IsMatch(c, @"\b" + k + @"s?\b")) return k;
            return null;
        }

        public static JObject MeasureSelectByFilter(IModelDoc2 model, string kind)
        {
            var res = new JObject();
            var asm = model as AssemblyDoc;
            if (asm == null || string.IsNullOrEmpty(kind)) { res["matched"] = -1; return res; }

            int matched = 0, active = 0;
            foreach (var o in (asm.GetComponents(true) as object[]) ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                string nm = null; try { nm = c.Name2; } catch { }
                if (nm == null) continue;
                string low = nm.ToLowerInvariant();
                bool hit;
                switch (kind)
                {
                    case "bolt": hit = (low.Contains("bolt") || low.Contains("screw")) && !low.Contains("nut") && !low.Contains("washer"); break;
                    case "nut": hit = low.Contains("nut"); break;
                    case "flange": hit = low.Contains("flange") || low.Contains("plate"); break;
                    default: hit = low.Contains(kind); break;
                }
                if (!hit) continue;
                matched++;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { }
                if (!sup) active++;
            }
            res["matched"] = matched;
            res["active"] = active;
            return res;
        }
    }
}
