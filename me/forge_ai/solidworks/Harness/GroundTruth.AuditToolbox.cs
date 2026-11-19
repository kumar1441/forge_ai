using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT audit measurement — shares NO code with AuditToolbox. Where the handler reuses Upsize's bolt
        // vocabulary and parses sizes out of config NAMES, this counts by a different route: configuration COUNT via
        // IModelDoc2.GetConfigurationCount(), design table via IModelDocExtension, and a size-token scan done here so
        // the two arrive at "how many switchable sizes" by different means. A baked-fixed part must read as
        // configCount<=1 OR distinctSizes<2 here too, or the handler's classification is wrong.
        public static JObject MeasureAuditToolbox(IModelDoc2 model)
        {
            var res = new JObject();
            var asm = model as AssemblyDoc;
            if (asm == null) { res["error"] = "not an assembly"; return res; }

            int fasteners = 0, liveToolbox = 0, designTable = 0, bakedFixed = 0;
            var rows = new JArray();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var o in (asm.GetComponents(false) as object[]) ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                string nm = c.Name2 ?? "";
                string low = nm.ToLowerInvariant();
                bool isNut = low.Contains("nut") || low.Contains("ecrou") || low.Contains("washer") || low.Contains("rondelle");
                bool isBolt = !isNut && Regex.IsMatch(low, @"bolt|screw|hcs|shcs|cap|socket|\bhex\b|stud|vis|boulon|din|iso|b18");
                if (!isBolt) continue;

                string path = null; try { path = c.GetPathName(); } catch { }
                string key = string.IsNullOrEmpty(path) ? nm : path;
                if (!seen.Add(key)) continue;

                var pd = c.GetModelDoc2() as IModelDoc2;
                int cfgCount = 0; bool hasTable = false; int tableRows = 0; var sizes = new HashSet<int>();
                if (pd != null)
                {
                    try { cfgCount = pd.GetConfigurationCount(); } catch { }
                    try { var dt = pd.GetDesignTable(); if (dt != null) { hasTable = true; try { tableRows = ((DesignTable)dt).GetTotalRowCount(); } catch { } } } catch { }
                    string[] names = null; try { names = pd.GetConfigurationNames() as string[]; } catch { }
                    foreach (var cf in names ?? new string[0])
                        foreach (Match m in Regex.Matches((cf ?? "").ToLowerInvariant(), @"(?<![a-z0-9])m(\d+)(?![0-9])"))
                        { int v; if (int.TryParse(m.Groups[1].Value, out v) && v >= 2 && v <= 64) sizes.Add(v); }
                }

                // same rule as the handler, reached independently: switchable-only counts. A design table with a single
                // config is baked-fixed, not design-table.
                bool tableSwitchable = hasTable && (tableRows >= 2 || sizes.Count >= 2);
                string kind;
                if (sizes.Count >= 2 && !hasTable) { kind = "live-toolbox"; liveToolbox++; }
                else if (tableSwitchable) { kind = "design-table"; designTable++; }
                else { kind = "baked-fixed"; bakedFixed++; }

                rows.Add(new JObject {
                    ["name"] = nm, ["configCount"] = cfgCount, ["distinctSizes"] = sizes.Count,
                    ["hasDesignTable"] = hasTable, ["toolboxPath"] = (path ?? "").ToLowerInvariant().Contains("toolbox"),
                    ["kind"] = kind
                });
                fasteners++;
            }

            res["fasteners"] = fasteners;
            res["liveToolbox"] = liveToolbox;
            res["designTable"] = designTable;
            res["bakedFixed"] = bakedFixed;
            res["rows"] = rows;
            return res;
        }
    }
}
