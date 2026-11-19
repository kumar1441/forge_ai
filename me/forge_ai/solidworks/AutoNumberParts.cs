using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class AutoNumberPartsResult
    {
        public int UniqueParts;        // distinct part FILES examined (a part reused N times is examined once)
        public int MissingBefore;      // unique parts with NO part-number property at run start
        public int Assigned;           // parts newly written AND independently confirmed to now carry the assigned PN
        public int AlreadyNumbered;    // unique parts that already had a part number (skipped, never renumbered)
        public int Failed;             // attempted but NOT confirmed (write failed, read-back mismatch, or collision)
        public string Scheme;          // human label of the numbering scheme used, e.g. "PN-0001.."
        public string Info;            // verdict-first one-liner
        public string Error;           // set => nothing was written (wrong doc / could not resolve)
    }

    /// <summary>
    /// AutoNumberParts (tools #138/#139 — "assign part numbers to parts missing one"). The WRITE fix that pairs with
    /// ValidateProps (the READ check that FINDS parts missing a part number). For every UNIQUE part file that carries
    /// NO part-number property (checked against the same name list ValidateProps uses), it ASSIGNS a sequential number
    /// from a scheme parsed out of the command ("PN-0001", "PN-0002", … by default; honours a user prefix like
    /// "number them BRK-001 up"). It writes the "PartNo" custom property at the document scope via
    /// CustomPropertyManager.Add3 — a safe, undoable property write, no geometry, and Forge never saves.
    ///
    /// Robustness: parts that ALREADY carry a number are SKIPPED, never renumbered (Rule #5 — a rerun reports
    /// "all parts already numbered, nothing to do"); per-part try/continue so a single failure never aborts the run
    /// (Rule #4); a preview leads any broad write (Rule #3); and it is FAIL-CLOSED — after writing, every part's PN is
    /// INDEPENDENTLY re-read and must match what was assigned, with no value collisions, or the part counts as Failed
    /// (Rule #6). Assignment never reuses a number already present in the assembly.
    /// </summary>
    public static class AutoNumberParts
    {
        // "auto-number the parts" / "assign part numbers to everything missing one" / "give every part a part number".
        // Deliberately does NOT match ValidateProps' READ phrasing "check part numbers" — this is the WRITE (assign).
        public static bool IsAutoNumberPartsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            return Regex.IsMatch(cmd,
                @"(\bauto[\s-]?number\b|\bassign\s+part\s*numbers?\b|\bnumber\s+(the|all|every|these|those)?\s*parts?\b|\bgive\s+.*\bpart\s*numbers?\b|\bpart\s*numbers?\s+(for|to)\s+.*missing)",
                RegexOptions.IgnoreCase);
        }

        // config-specific first, then document-level; same recognised part-number property names ValidateProps reads
        private static readonly string[] PartNoProps = { "PartNo", "PartNumber", "Part Number", "Part No", "Number", "DrawingNo", "Drawing Number" };
        private const string WriteProp = "PartNo";   // the standard BOM part-number property we write

        private class Scheme { public string Prefix; public int Width; public int Start; public string Label { get { return Prefix + Start.ToString().PadLeft(Width, '0') + ".."; } } }

        private class Assignment { public Component2 Comp; public IModelDoc2 Md; public string Name; public string Value; }

        private class Plan
        {
            public int UniqueParts;
            public int AlreadyNumbered;
            public Scheme Scheme;
            public List<Assignment> Assignments = new List<Assignment>();
        }

        // ---- default PN-0001; honour a user-given scheme token like "BRK-001" / "PN-0001" (>=2 letters, >=2 digits) ----
        private static Scheme ParseScheme(string cmd)
        {
            var s = new Scheme { Prefix = "PN-", Width = 4, Start = 1 };
            if (string.IsNullOrEmpty(cmd)) return s;
            var m = Regex.Match(cmd, @"\b([A-Za-z]{2,10})[\s_-]?(\d{2,6})\b");
            if (m.Success)
            {
                string letters = m.Groups[1].Value.ToUpperInvariant();
                string digits = m.Groups[2].Value;
                // reconstruct the separator the user typed between letters and digits (default '-')
                string between = m.Value.Substring(letters.Length, m.Value.Length - letters.Length - digits.Length);
                if (string.IsNullOrEmpty(between)) between = "-";
                s.Prefix = letters + between;
                s.Width = digits.Length;
                int start; s.Start = int.TryParse(digits, out start) ? start : 1;
            }
            return s;
        }

        // ---- read-only resolution: collect unique parts, find the missing ones, pre-compute their assigned numbers
        //      (skipping any number already used in the assembly). Safe to call twice (Preview + Run) — writes nothing. ----
        private static Plan Resolve(AssemblyDoc asm, string intent)
        {
            var p = new Plan { Scheme = ParseScheme((intent ?? "").ToLowerInvariant()) };
            object[] all = asm.GetComponents(false) as object[];

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var missing = new List<Assignment>();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // PN values already present -> never reissue
            foreach (var o in all ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { }
                if (sup) continue;
                string path = null; try { path = c.GetPathName(); } catch { }
                if (string.IsNullOrEmpty(path) || seen.Contains(path)) continue; seen.Add(path);
                IModelDoc2 md = null; try { md = c.GetModelDoc2() as IModelDoc2; } catch { }
                if (md == null || (int)md.GetType() != (int)swDocumentTypes_e.swDocPART) continue;   // sub-assembly / unresolved
                p.UniqueParts++;
                string nm = null; try { nm = c.Name2; } catch { }
                if (string.IsNullOrEmpty(nm)) nm = Path.GetFileNameWithoutExtension(path);

                string pn = ReadPartNumber(md);
                if (!string.IsNullOrWhiteSpace(pn)) { p.AlreadyNumbered++; used.Add(pn.Trim()); }
                else missing.Add(new Assignment { Comp = c, Md = md, Name = nm });
            }

            // ---- assign sequential numbers, skipping any value already taken and never colliding with each other ----
            int next = p.Scheme.Start;
            foreach (var a in missing)
            {
                string val;
                while (true)
                {
                    val = p.Scheme.Prefix + next.ToString().PadLeft(p.Scheme.Width, '0');
                    next++;
                    if (!used.Contains(val)) break;   // guarantee uniqueness against existing + already-assigned
                }
                used.Add(val);
                a.Value = val;
                p.Assignments.Add(a);
            }
            return p;
        }

        // config-specific first, then document-level ("") — same order ValidateProps reads
        private static string ReadPartNumber(IModelDoc2 md)
        {
            if (md == null) return null;
            string cfg = ""; try { cfg = ((Configuration)md.GetActiveConfiguration()).Name; } catch { }
            foreach (var scope in new[] { cfg, "" })
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

        // ---- preview before a broad write (Rule #3): only when >3 parts get numbered, else null (execute directly) ----
        public static string PreviewLine(IModelDoc2 model, string intent)
        {
            var asm = model as AssemblyDoc; if (asm == null) return null;
            var p = Resolve(asm, intent);
            if (p.Assignments.Count <= 3) return null;   // small unambiguous write → run directly
            string first = p.Assignments[0].Value;
            string last = p.Assignments[p.Assignments.Count - 1].Value;
            return p.Assignments.Count + " of " + p.UniqueParts + " parts have no part number — assigning " + first + ".." + last + "? (undoable, and Forge never saves)";
        }

        public static async Task<AutoNumberPartsResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new AutoNumberPartsResult();
            var asm = model as AssemblyDoc;
            if (asm == null) { res.Error = "Open the assembly (.SLDASM) whose parts you want to number."; return res; }

            await emit("Gauge", "finding parts missing a part number", "run", null);
            var plan = Resolve(asm, intent);
            res.UniqueParts = plan.UniqueParts;
            res.AlreadyNumbered = plan.AlreadyNumbered;
            res.MissingBefore = plan.Assignments.Count;
            res.Scheme = plan.Scheme.Label;

            if (plan.UniqueParts == 0) { res.Error = "No unique parts found in this assembly to number."; await emit("Gauge", null, "fail", res.Error); return res; }
            await emit("Gauge", null, "done", res.MissingBefore + " of " + res.UniqueParts + " parts have no part number" + (res.AlreadyNumbered > 0 ? " · " + res.AlreadyNumbered + " already numbered" : ""));

            if (res.MissingBefore == 0)
            {
                res.Info = "All " + res.UniqueParts + " parts already carry a part number — nothing to do.";
                return res;
            }

            // ---- write the assigned number to each missing part's PartNo property, one at a time (Rule #4) ----
            await emit("Scribe", "assigning part numbers (" + plan.Scheme.Prefix + ")", "run", null);
            int idx = 0;
            foreach (var a in plan.Assignments)
            {
                idx++;
                try
                {
                    CustomPropertyManager cpm = a.Md.Extension.CustomPropertyManager[""];   // document scope
                    // Add3(FieldName, swCustomInfoType_e, FieldValue, swCustomPropertyAddOption_e). Text field; replace
                    // the value if the PartNo field already exists empty, else add it. Return code captured but NOT
                    // trusted — verification is the independent read-back below (Rule #6).
                    cpm.Add3(WriteProp, (int)swCustomInfoType_e.swCustomInfoText, a.Value,
                             (int)swCustomPropertyAddOption_e.swCustomPropertyReplaceValue);
                }
                catch { res.Failed++; a.Value = null; }   // null => don't count as verifiable below
                if (res.MissingBefore > 25 && idx % 10 == 0) await emit(null, null, "done", "numbering… " + idx + "/" + res.MissingBefore);
            }
            await emit("Scribe", null, "done", plan.Assignments.Count + " parts written");

            // ---- FAIL CLOSED (Rule #6): re-read each written part's PN independently; it must now be non-empty AND
            //      match the assigned value, with NO value collision across the parts we just numbered. ----
            await emit("Sentinel", "verifying assigned numbers", "run", null);
            var confirmedValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);  // value -> part name (collision guard)
            int collisions = 0;
            foreach (var a in plan.Assignments)
            {
                if (a.Value == null) continue;   // write threw
                string readBack = ReadPartNumber(a.Md);
                if (string.IsNullOrWhiteSpace(readBack) || !string.Equals(readBack.Trim(), a.Value, StringComparison.OrdinalIgnoreCase))
                {
                    res.Failed++;   // not confirmed
                    continue;
                }
                if (confirmedValues.ContainsKey(a.Value)) { collisions++; res.Failed++; continue; }  // duplicate PN introduced
                confirmedValues[a.Value] = a.Name;
                res.Assigned++;
            }
            await emit("Sentinel", null, "done",
                res.Assigned + " confirmed" + (res.Failed > 0 ? " · " + res.Failed + " unconfirmed" : "") + (collisions > 0 ? " · " + collisions + " collision(s)" : ""));

            res.Info = BuildInfo(res, collisions);
            return res;
        }

        // verdict first (Character #3), the number not the adjective (Character #2), honest about what was VERIFIED
        private static string BuildInfo(AutoNumberPartsResult r, int collisions)
        {
            var sb = new StringBuilder();
            sb.Append("Assigned " + r.Assigned + " part number" + (r.Assigned == 1 ? "" : "s") + " (" + r.Scheme + ").");
            if (r.AlreadyNumbered > 0) sb.Append(" " + r.AlreadyNumbered + " already numbered, left as-is.");
            if (r.Failed > 0) sb.Append(" " + r.Failed + " couldn't be confirmed" + (collisions > 0 ? " (" + collisions + " would collide)" : "") + " — left for review.");
            sb.Append(" Undoable (one Ctrl+Z per part), and the document was not saved.");
            return sb.ToString();
        }
    }
}
