using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT per-part mass — shares NO code with GetComponentMass. Own unique-part traversal + IMassProperty read.
        public static JObject MeasureGetComponentMass(IModelDoc2 model)
        {
            var res = new JObject();
            var asm = model as AssemblyDoc;
            if (asm == null) { res["uniqueParts"] = -1; return res; }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int parts = 0; double total = 0, heaviest = -1;
            foreach (var o in (asm.GetComponents(false) as object[]) ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                string path = null; try { path = c.GetPathName(); } catch { }
                string key = string.IsNullOrEmpty(path) ? (c.Name2 ?? "") : path;
                if (!seen.Add(key)) continue;
                var pd = c.GetModelDoc2() as IModelDoc2; if (pd == null) continue;
                double kg = -1;
                try { var mp = pd.Extension.CreateMassProperty(); if (mp != null) kg = mp.Mass; } catch { }
                if (kg <= 0) continue;
                parts++; total += kg; if (kg > heaviest) heaviest = kg;
            }
            res["uniqueParts"] = parts;
            res["totalMassKg"] = total;
            res["heaviestKg"] = heaviest;
            return res;
        }
    }
}
