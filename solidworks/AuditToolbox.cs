using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class ToolboxFastener
    {
        public string Name;
        public string CurrentConfig;
        public int ConfigCount;
        public int DistinctSizes;      // how many DIFFERENT M-sizes exist across this part's configs
        public bool HasDesignTable;
        public bool ToolboxPathHint;   // the file lives under a Toolbox folder
        public string Kind;            // "live-toolbox" | "design-table" | "baked-fixed"
        public string Note;
        public string PartFile;        // the .SLDPRT leaf — distinct files matter (3 instances may be 1 or 3 files)
        public string ConfigNames;     // all config names on the part (reveals whether a target size exists)
        public int DesignTableRows;    // design-table row count (how many sizes the table CAN produce)
    }

    public class AuditToolboxResult
    {
        public int Fasteners;
        public int LiveToolbox;        // multiple size configs → a config switch upsizes it (tool 39 path)
        public int DesignTable;        // a design table drives the sizes → switch via the table (tool 194 path)
        public int BakedFixed;         // one size only, no table → CANNOT config-switch; needs file replacement (tool 164)
        public List<ToolboxFastener> Details = new List<ToolboxFastener>();
        public string Headline;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 249 — audit_toolbox_integrity (READ, H-3 pre-pass). "Shops break Toolbox constantly, and a Toolbox part
    /// that's actually a baked copy has no configs to switch." Before upsize touches anything, this classifies every
    /// fastener into the THREE kinds that each need a DIFFERENT upsize path:
    ///   live-toolbox  — carries configs for multiple sizes → upsize by switching the size config (tool 39).
    ///   design-table  — a design table drives the size → upsize by switching the table config (tool 194).
    ///   baked-fixed   — one size, no table → NO config to switch to; upsize needs a file replacement (tool 164).
    /// It is the evidence that decides which upsize path is even possible. Read-only, never modifies the model.
    /// Reuses Upsize's bolt vocabulary so the two agree on what a fastener is.
    /// </summary>
    public static class AuditToolbox
    {
        public static bool IsAuditIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            return Regex.IsMatch(c, @"\b(audit|check|inspect|integrity|health)\b") &&
                   Regex.IsMatch(c, @"\b(toolbox|fastener|fasteners|bolt|bolts|hardware)\b");
        }

        private static readonly string[] BoltHints =
            { "bolt", "screw", "hcs", "shcs", "cap", "socket", "hex", "stud", "vis", "boulon", "din", "iso", "b18" };
        private static readonly string[] NotBolt =
            { "nut", "ecrou", "washer", "rondelle", "4032", "4034", "4035", "934", "985" };

        private static bool IsBoltName(string n)
        {
            if (string.IsNullOrEmpty(n)) return false;
            n = n.ToLowerInvariant();
            foreach (var x in NotBolt) if (n.Contains(x)) return false;
            foreach (var h in BoltHints) if (n.Contains(h)) return true;
            return false;
        }

        // count DISTINCT metric sizes across a set of config names ("...M6 x 1.0..." → 6). This is the real signal:
        // a live Toolbox part has M5/M6/M8/... as configs; a baked copy has only its own single size.
        private static int DistinctSizes(string[] cfgs)
        {
            var sizes = new HashSet<int>();
            foreach (var cf in cfgs ?? new string[0])
                foreach (Match m in Regex.Matches((cf ?? "").ToLowerInvariant(), @"(?<![a-z0-9])m(\d+)(?![0-9])"))
                {
                    int v; if (int.TryParse(m.Groups[1].Value, out v) && v >= 2 && v <= 64) sizes.Add(v);
                }
            return sizes.Count;
        }

        public static async Task<AuditToolboxResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new AuditToolboxResult();
            var asm = model as AssemblyDoc;
            if (asm == null) { res.Error = "Open the assembly (.SLDASM) to audit its fasteners."; return res; }

            await emit("Auditor", "finding the fasteners", "run", null);
            object[] comps = asm.GetComponents(false) as object[];
            var seenParts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var o in comps ?? new object[0])
            {
                var c = o as Component2;
                if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { }
                if (sup) continue;
                string nm = c.Name2 ?? "";
                if (!IsBoltName(nm)) continue;

                // audit each distinct PART file once (an assembly has many instances of the same bolt)
                string path = null; try { path = c.GetPathName(); } catch { }
                string key = string.IsNullOrEmpty(path) ? nm : path;
                if (!seenParts.Add(key)) continue;

                var f = new ToolboxFastener { Name = nm };
                try { f.CurrentConfig = c.ReferencedConfiguration; } catch { }
                f.ToolboxPathHint = !string.IsNullOrEmpty(path) && path.ToLowerInvariant().Contains("toolbox");

                f.PartFile = System.IO.Path.GetFileName(path ?? nm);
                var pd = null as IModelDoc2; try { pd = c.GetModelDoc2() as IModelDoc2; } catch { }
                string[] cfgs = null;
                if (pd != null)
                {
                    try { cfgs = pd.GetConfigurationNames() as string[]; } catch { }
                    try { var dt = pd.GetDesignTable(); if (dt != null) { f.HasDesignTable = true; try { f.DesignTableRows = ((DesignTable)dt).GetTotalRowCount(); } catch { } } } catch { }
                }
                f.ConfigCount = cfgs?.Length ?? 0;
                f.DistinctSizes = DistinctSizes(cfgs);
                f.ConfigNames = cfgs != null ? string.Join(" | ", cfgs) : "";

                // classify by what's actually SWITCHABLE, proven by evidence (2026-07-22 seller steam engine):
                //   live-toolbox : ≥2 size configs IN ONE FILE → a config switch upsizes it.
                //   design-table : a table that exposes ≥2 rows/sizes → switch the size through the table.
                //   baked-fixed  : ONE size only → no config to switch to; needs a FILE REPLACEMENT.
                // A design table with a SINGLE config is NOT switchable — it's a baked Toolbox copy wearing a leftover
                // table. Requiring the table to expose real alternatives is what stops that false "design-table" label.
                bool tableSwitchable = f.HasDesignTable && (f.DesignTableRows >= 2 || f.DistinctSizes >= 2);
                if (f.DistinctSizes >= 2 && !f.HasDesignTable)
                { f.Kind = "live-toolbox"; f.Note = f.DistinctSizes + " sizes as configs — a config switch upsizes it"; res.LiveToolbox++; }
                else if (tableSwitchable)
                { f.Kind = "design-table"; f.Note = "design table exposes " + Math.Max(f.DesignTableRows, f.DistinctSizes) + " sizes — switch via the table"; res.DesignTable++; }
                else
                {
                    f.Kind = "baked-fixed";
                    f.Note = f.ToolboxPathHint || f.HasDesignTable
                        ? "single size only (baked/broken Toolbox copy" + (f.HasDesignTable ? ", leftover table exposes no other size" : "") + ") — needs file replacement"
                        : "single fixed size, no config to switch to — needs file replacement";
                    res.BakedFixed++;
                }

                res.Details.Add(f);
                res.Fasteners++;
            }

            await emit("Auditor", null, "done",
                res.Fasteners + " fastener part" + (res.Fasteners == 1 ? "" : "s") + " · " +
                res.LiveToolbox + " live-toolbox · " + res.DesignTable + " design-table · " + res.BakedFixed + " baked/fixed");

            if (res.Fasteners == 0) { res.Error = "No fasteners found to audit."; return res; }

            res.Headline = res.Fasteners + " fastener types: " + res.LiveToolbox + " live Toolbox, " +
                           res.DesignTable + " design-table, " + res.BakedFixed + " baked/fixed.";
            var sb = new System.Text.StringBuilder(res.Headline);
            if (res.BakedFixed > 0)
                sb.Append("\n" + res.BakedFixed + " fastener type(s) can't be upsized by a config switch — they'd need a file replacement.");
            foreach (var f in res.Details)
                sb.Append("\n• " + f.PartFile + " [" + (f.CurrentConfig ?? "?") + "] → " + f.Kind + " — " + f.Note +
                          " · configs: " + (string.IsNullOrEmpty(f.ConfigNames) ? "(none)" : f.ConfigNames) +
                          (f.DesignTableRows > 0 ? " · table rows=" + f.DesignTableRows : ""));
            res.Info = sb.ToString();
            return res;
        }
    }
}
