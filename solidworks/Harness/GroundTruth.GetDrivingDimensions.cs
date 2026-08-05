using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT read for get_driving_dimensions, and deliberately NOT a second copy of the handler's rule.
        //
        // Two things are checked that the handler cannot check for itself:
        //   1. NAME RESOLUTION. The whole claim of a "named driving dimension" is that you can ADDRESS it by name.
        //      So every name found is fed back through IModelDoc2.Parameter(name) — a name-based accessor the handler
        //      never uses — and the value read that way is published. A name the handler reports that cannot be
        //      resolved by name is a name you cannot command by, and the harness will see the mismatch.
        //   2. RAW CLASSIFICATION DATA ONLY. drivenState and the bare name are published as-is; the driving/driven
        //      and named/auto splits are re-derived in PowerShell by run-harness. The handler's heuristic is never
        //      mirrored here — mirroring it would just be the same guess written twice (the ICE type-name trap).
        public static JObject MeasureGetDrivingDimensions(IModelDoc2 model)
        {
            var res = new JObject();
            if (model == null) { res["error"] = "no doc"; return res; }

            var eqs = new JArray();
            try
            {
                var eq = model.GetEquationMgr();
                if (eq != null)
                {
                    int n = 0; try { n = eq.GetCount(); } catch { }
                    for (int k = 0; k < n; k++) { try { eqs.Add(eq.Equation[k]); } catch { } }
                }
            }
            catch { }
            res["equations"] = eqs;

            var rows = new JArray();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var feat = model.FirstFeature() as Feature;
            while (feat != null)
            {
                string fname = null; try { fname = feat.Name; } catch { }
                DisplayDimension dd = null;
                try { dd = feat.GetFirstDisplayDimension() as DisplayDimension; } catch { }
                while (dd != null)
                {
                    Dimension d = null; try { d = dd.GetDimension2(0) as Dimension; } catch { }
                    if (d != null)
                    {
                        string full = null; try { full = d.FullName; } catch { }
                        string bare = null; try { bare = d.Name; } catch { }
                        if (!string.IsNullOrEmpty(full) && seen.Add(full))
                        {
                            var e = new JObject();
                            e["fullName"] = full;
                            e["name"] = bare;
                            e["feature"] = fname;
                            int ds = -1; try { ds = d.DrivenState; } catch { }
                            e["drivenState"] = ds;                       // RAW: 1 = driving, 2 = driven, on this build
                            bool ro = false; try { ro = d.ReadOnly; } catch { }
                            e["readOnly"] = ro;
                            double v = double.NaN;
                            try { var sv = d.GetSystemValue3((int)swInConfigurationOpts_e.swThisConfiguration, null) as double[]; if (sv != null && sv.Length > 0) v = sv[0] * 1000.0; } catch { }
                            e["valueMm"] = double.IsNaN(v) ? (JToken)null : v;

                            // CHECK 1: resolve the same dimension BY NAME, through a different accessor.
                            double byName = double.NaN; bool resolved = false;
                            try
                            {
                                var p = model.Parameter(full) as Dimension;
                                if (p != null)
                                {
                                    resolved = true;
                                    try { var sv2 = p.GetSystemValue3((int)swInConfigurationOpts_e.swThisConfiguration, null) as double[]; if (sv2 != null && sv2.Length > 0) byName = sv2[0] * 1000.0; } catch { }
                                }
                            }
                            catch { }
                            e["resolvesByName"] = resolved;
                            e["valueByNameMm"] = double.IsNaN(byName) ? (JToken)null : byName;
                            rows.Add(e);
                        }
                    }
                    try { dd = feat.GetNextDisplayDimension(dd) as DisplayDimension; } catch { dd = null; }
                }
                feat = feat.GetNextFeature() as Feature;
            }
            res["rows"] = rows;
            res["count"] = rows.Count;
            return res;
        }
    }
}
