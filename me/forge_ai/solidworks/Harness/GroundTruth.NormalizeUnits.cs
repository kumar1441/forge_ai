using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for normalize_units (tool 244) — shares NO code with NormalizeUnits.cs. The handler
    /// finds the DOMINANT unit and flags every component off it. This GT just tallies the raw per-unique-part unit
    /// enums and reports how many DISTINCT systems appear and the size of the minority — a different reduction of the
    /// same readings — so the two must agree on distinct-count and mismatch. Known truth: mixed-units-assembly = 2
    /// components, 2 distinct units (1 inch + 1 mm), minority = 1. Assembly-scoped.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureNormalizeUnits(IModelDoc2 model)
        {
            var d = new JObject();
            if ((int)model.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY) { d["applicable"] = false; d["reason"] = "not an assembly"; return d; }
            d["applicable"] = true;

            var perPart = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var asm = model as AssemblyDoc;
            var comps = asm.GetComponents(true) as object[];
            foreach (var o in comps ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                try { if (c.IsSuppressed()) continue; } catch { }
                string path = null; try { path = c.GetPathName(); } catch { }
                if (string.IsNullOrEmpty(path) || perPart.ContainsKey(path)) continue;
                var md = c.GetModelDoc2() as IModelDoc2; if (md == null) continue;
                int u = -999;
                try { u = md.Extension.GetUserPreferenceInteger((int)swUserPreferenceIntegerValue_e.swUnitsLinear, (int)swUserPreferenceOption_e.swDetailingNoOptionSpecified); } catch { }
                perPart[path] = u;
            }

            var byUnit = perPart.Values.GroupBy(u => u).ToDictionary(g => g.Key, g => g.Count());
            int components = perPart.Count;
            int distinct = byUnit.Count;
            int majority = distinct == 0 ? 0 : byUnit.Values.Max();
            int minority = components - majority;   // components NOT in the largest unit group

            d["components"] = components;
            d["distinctUnits"] = distinct;
            d["minorityCount"] = minority;
            d["hasInch"] = perPart.Values.Contains((int)swLengthUnit_e.swINCHES);
            d["hasMm"] = perPart.Values.Contains((int)swLengthUnit_e.swMM);
            d["expectedVerdict"] = distinct > 1 ? "mixed" : "consistent";
            return d;
        }
    }
}
