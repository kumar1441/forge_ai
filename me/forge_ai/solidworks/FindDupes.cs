using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    // one referenced part file and how many times it's instanced in the assembly
    public class DupeGroup { public string Name; public string Path; public int Count; }

    // the SAME part-number value carried by MORE THAN ONE distinct file — a possible duplicate-modeled part
    public class DupePnCollision { public string PartNo; public List<string> Files = new List<string>(); }

    public class FindDupesResult
    {
        public int TotalComponents;    // active (non-suppressed) component instances
        public int UniqueParts;        // distinct external file paths (the real "how many different parts")
        public int VirtualParts;       // in-place / virtual components with no external file (each unique, not file-grouped)
        public int ReusedParts;        // unique files instanced >1 time (legitimate reuse — normal, not a defect)
        public List<DupeGroup> TopGroups = new List<DupeGroup>();   // most-reused first, capped
        public bool PnCheckable;       // did ANY part expose a recognised part-number property?
        public int PnCollisions;       // part numbers claimed by >1 DISTINCT file (suspicious duplicate-modeled parts)
        public List<DupePnCollision> Collisions = new List<DupePnCollision>();
        public string Info;
        public string Error;
    }

    /// <summary>
    /// FindDupes (tool #136 "find duplicate components"). READ-ONLY. Two independent lenses on redundancy:
    ///   1) File reuse — how many component INSTANCES reference the same part file. "240 components, 38 unique
    ///      parts; the M6x20 bolt appears 44 times." Reusing one file many times is NORMAL, not a defect — it's
    ///      the headline "uses N but only M unique" number an engineer wants.
    ///   2) Part-number collisions — the SAME part-number custom property carried by TWO DIFFERENT files. That's
    ///      suspicious: the same part modeled twice under one number. Flagged separately from legitimate reuse.
    /// It never adds a mate, moves a component, or alters a config. Every number is independently re-derivable by
    /// the harness (GroundTruth.MeasureFindDupes) for cross-checking.
    /// </summary>
    public static class FindDupes
    {
        public static bool IsFindDupesIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            return System.Text.RegularExpressions.Regex.IsMatch(cmd,
                @"\b(duplicate|duplicat\w*|redundant|redundanc\w*|unique parts|how many (?:unique|different) parts|same part|repeated parts)\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        private const int TopCap = 5;

        // recognised part-number custom properties (same vocabulary the Doctor uses, re-declared here so the two
        // handlers stay independent — a change to one can't silently move the other's result)
        private static readonly string[] PartNoProps = { "PartNo", "PartNumber", "Part Number", "Part No", "Number", "DrawingNo", "Drawing Number" };

        public static async Task<FindDupesResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new FindDupesResult();
            var asm = model as AssemblyDoc;
            if (asm == null) { res.Error = "Open an assembly to find duplicate components."; return res; }

            await emit("Tally", "counting components by referenced part file", "run", null);
            object[] comps = asm.GetComponents(false) as object[];
            var byPath = new Dictionary<string, DupeGroup>(StringComparer.OrdinalIgnoreCase);   // file path -> reuse group
            int total = 0, virt = 0;
            foreach (var o in comps ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { }
                if (sup) continue;                       // suppressed = not in the working assembly (matches Scout/GroundTruth)
                total++;
                bool isVirt = false; try { isVirt = c.IsVirtual; } catch { }
                string path = null; try { path = c.GetPathName(); } catch { }
                if (isVirt || string.IsNullOrEmpty(path)) { virt++; continue; }   // no external file to group by
                DupeGroup g;
                if (!byPath.TryGetValue(path, out g)) { g = new DupeGroup { Path = path, Name = FileLabel(path) }; byPath[path] = g; }
                g.Count++;
            }
            res.TotalComponents = total;
            res.VirtualParts = virt;
            res.UniqueParts = byPath.Count;

            var groups = new List<DupeGroup>(byPath.Values);
            groups.Sort((x, y) => y.Count.CompareTo(x.Count));   // most-reused first
            foreach (var g in groups) { if (g.Count > 1) res.ReusedParts++; }
            for (int i = 0; i < groups.Count && i < TopCap && groups[i].Count > 1; i++) res.TopGroups.Add(groups[i]);
            await emit("Tally", null, "done",
                res.TotalComponents + " components · " + res.UniqueParts + " unique part" + (res.UniqueParts == 1 ? "" : "s") +
                (res.VirtualParts > 0 ? " · " + res.VirtualParts + " in-place" : ""));

            // ---- part-number collisions: same PN on DIFFERENT files (a possible duplicate-modeled part) ----
            await emit("Sieve", "checking part numbers for cross-file collisions", "run", null);
            var byNumber = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);   // PN -> distinct file paths
            int withProp = 0;
            foreach (var kv in byPath)   // unique files only (one read per part, not per instance)
            {
                IModelDoc2 md = null; try { md = FirstResolvedDoc(asm, kv.Key); } catch { }
                if (md == null) continue;
                string pn = ReadPartNumber(md);
                if (string.IsNullOrWhiteSpace(pn)) continue;
                withProp++;
                if (!byNumber.ContainsKey(pn)) byNumber[pn] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                byNumber[pn].Add(kv.Key);
            }
            res.PnCheckable = withProp > 0;
            foreach (var kv in byNumber)
            {
                if (kv.Value.Count < 2) continue;   // one PN across >1 distinct file = collision
                res.PnCollisions++;
                if (res.Collisions.Count < TopCap)
                {
                    var col = new DupePnCollision { PartNo = kv.Key };
                    foreach (var p in kv.Value) col.Files.Add(FileLabel(p));
                    res.Collisions.Add(col);
                }
            }
            await emit("Sieve", null, "done",
                !res.PnCheckable ? "no part-number property found — reuse only"
                : res.PnCollisions == 0 ? "no part-number collisions"
                : res.PnCollisions + " part number" + (res.PnCollisions == 1 ? "" : "s") + " on multiple files");

            res.Info = BuildInfo(res);
            return res;
        }

        // resolve the model doc for a given file path from any instance that references it (read-only)
        private static IModelDoc2 FirstResolvedDoc(AssemblyDoc asm, string path)
        {
            object[] comps = asm.GetComponents(false) as object[];
            foreach (var o in comps ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                string p = null; try { p = c.GetPathName(); } catch { }
                if (!string.Equals(p, path, StringComparison.OrdinalIgnoreCase)) continue;
                IModelDoc2 md = null; try { md = c.GetModelDoc2() as IModelDoc2; } catch { }
                if (md != null) return md;
            }
            return null;
        }

        private static string ReadPartNumber(IModelDoc2 md)
        {
            string cfg = ""; try { cfg = ((Configuration)md.GetActiveConfiguration()).Name; } catch { }
            foreach (var scope in new[] { cfg, "" })   // config-specific first, then document-level
            {
                CustomPropertyManager cpm = null;
                try { cpm = md.Extension.CustomPropertyManager[scope ?? ""]; } catch { }
                if (cpm == null) continue;
                foreach (var prop in PartNoProps)
                {
                    string val = null, resolved = null;
                    try { cpm.Get4(prop, false, out val, out resolved); } catch { }
                    string v = !string.IsNullOrWhiteSpace(resolved) ? resolved : val;
                    if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
                }
            }
            return null;
        }

        private static string FileLabel(string path)
        {
            try { return Path.GetFileNameWithoutExtension(path); } catch { return path; }
        }

        // verdict first (Character #3), the number not the adjective (Character #2)
        private static string BuildInfo(FindDupesResult r)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(r.TotalComponents + " component" + (r.TotalComponents == 1 ? "" : "s") + ", " +
                      r.UniqueParts + " unique part" + (r.UniqueParts == 1 ? "" : "s") + ". ");

            if (r.TopGroups.Count == 0)
                sb.Append("No part is used more than once — every component is a distinct file.");
            else
            {
                sb.Append("Most reused: ");
                for (int i = 0; i < r.TopGroups.Count; i++)
                {
                    var g = r.TopGroups[i];
                    sb.Append(g.Name + " ×" + g.Count);
                    sb.Append(i < r.TopGroups.Count - 1 ? ", " : ".");
                }
            }

            // reuse is normal; a part-number collision is the actionable flag — surface it explicitly
            if (r.PnCheckable && r.PnCollisions > 0)
            {
                sb.Append(" " + r.PnCollisions + " part number" + (r.PnCollisions == 1 ? "" : "s") +
                          " shared across different files (possible duplicate-modeled parts): ");
                int shown = Math.Min(3, r.Collisions.Count);
                for (int i = 0; i < shown; i++)
                {
                    var c = r.Collisions[i];
                    sb.Append("\"" + c.PartNo + "\" on " + c.Files.Count + " files");
                    sb.Append(i < shown - 1 ? "; " : ".");
                }
            }
            else if (!r.PnCheckable)
                sb.Append(" (No part-number property on any part, so cross-file part-number collisions can't be checked.)");

            return sb.ToString();
        }
    }
}
