using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    // One release-readiness category's finding. Checkable=false => we couldn't run this check on THIS model
    // (Rule #4 / Character #4): say so explicitly instead of faking a reassuring zero.
    public class ValidatePropFinding
    {
        public string Category;
        public int Count;          // offenders found (only meaningful when Checkable)
        public bool Checkable;     // could this check actually run on this model?
        public string Note;        // one-line verdict / why-not-checkable
        public List<string> Worst = new List<string>();  // the worst offenders, by name
    }

    public class ValidatePropsResult
    {
        public int Components;
        public int SuppressedComponents;   // informational, not an error
        public int UniqueParts;            // distinct part FILES examined (a part reused N times is examined once)
        public int PartsChecked;           // unique solid parts eligible for material/weight
        public int MissingMaterials;
        public int MissingPartNos;
        public int DuplicatePartNos;       // part-number VALUES carried by >1 distinct file
        public int NoWeightParts;          // mass<=0 or no solid body => nothing to weigh for a BOM
        public int TotalIssues;
        public bool ReleaseReady;
        public List<ValidatePropFinding> Findings = new List<ValidatePropFinding>();
        public string Headline;
        public string Info;                // full report: headline first, categories underneath
        public string Error;
    }

    /// <summary>
    /// ValidateProps (tool #143) — the READ-ONLY release-readiness health check. Where Doctor is the broad
    /// "what's wrong with this" audit, ValidateProps is the focused BOM/release subset: for every UNIQUE part it
    /// answers three questions a shop asks before a model ships — does it have a material, does it have a unique
    /// part number, and can we compute its weight? It returns ONE report that leads with a release verdict
    /// (Character #3), categories (missing materials, missing part numbers, duplicate part numbers, no-weight
    /// parts) underneath with counts + named offenders. It NEVER writes.
    ///
    /// A category that can't be checked on a given model (e.g. no numbered parts at all) is reported as "not
    /// checkable" with the reason — never a fake zero (Rule #4). Every count is independently re-derivable by the
    /// harness (GroundTruth.MeasureValidateProps) for cross-checking; the two share no code.
    /// </summary>
    public static class ValidateProps
    {
        public static bool IsValidatePropsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            // NARROWER than Doctor's "diagnose": this is the properties/release check only. Deliberately does NOT
            // match generic "what's wrong"/"audit" — those belong to Doctor. Offline dispatch runs this BEFORE
            // Doctor so an explicit "check properties" never falls through to the broad doctor.
            return System.Text.RegularExpressions.Regex.IsMatch(cmd,
                @"(\bcheck\s+(this\s+|that\s+|the\s+|its\s+)?(assembly.?s?\s+)?propert|\bvalidate\s+(this|the|propert)|\bpropert(y|ies)\s+(check|health|report)|\brelease[\s-]?read|\bmissing\s+material|\bduplicate\s+part\s*number|\bmissing\s+part\s*number|\bbom\s+ready)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        // config-specific first, then document-level; the recognised part-number property names (superset of Doctor's)
        private static readonly string[] PartNoProps = { "PartNo", "PartNumber", "Part Number", "Part No", "Number", "DrawingNo", "Drawing Number" };

        public static async Task<ValidatePropsResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new ValidatePropsResult();
            var asm = model as AssemblyDoc;
            if (asm == null) { res.Error = "Open the assembly (.SLDASM) to validate its properties."; return res; }

            await emit("Auditor", "reading the assembly", "run", null);

            object[] all = asm.GetComponents(false) as object[];
            var active = new List<Component2>();
            int suppressed = 0;
            foreach (var o in all ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { }
                if (sup) { suppressed++; continue; }
                active.Add(c);
            }
            res.Components = active.Count;
            res.SuppressedComponents = suppressed;

            // ---- collect ONE record per unique part file (a part reused N times is a single release item) ----
            var parts = new List<PartRec>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in active)
            {
                string path = null; try { path = c.GetPathName(); } catch { }
                if (string.IsNullOrEmpty(path) || seen.Contains(path)) continue; seen.Add(path);
                PartDoc pd = null; try { pd = c.GetModelDoc2() as PartDoc; } catch { }
                if (pd == null) continue;   // sub-assembly or unresolved: not a release PART line
                string nm = null; try { nm = c.Name2; } catch { }
                parts.Add(BuildRec(pd, path, string.IsNullOrEmpty(nm) ? Path.GetFileNameWithoutExtension(path) : nm));
            }
            res.UniqueParts = parts.Count;
            await emit("Auditor", null, "done", res.Components + " active components · " + res.UniqueParts + " unique parts (" + suppressed + " suppressed)");

            await emit("Sentinel", "checking materials, part numbers and weight", "run", null);
            res.Findings.Add(CheckMissingMaterials(parts, res));
            res.Findings.Add(CheckMissingPartNos(parts, res));
            res.Findings.Add(CheckDuplicatePartNos(parts, res));
            res.Findings.Add(CheckNoWeight(parts, res));

            int checkable = 0;
            foreach (var f in res.Findings) { if (f.Checkable) { checkable++; res.TotalIssues += f.Count; } }
            res.ReleaseReady = res.TotalIssues == 0 && checkable > 0;
            await emit("Sentinel", null, "done", res.TotalIssues == 0 ? "no property gaps across " + checkable + " checks" : res.TotalIssues + " property gap(s) across " + checkable + " checks");

            res.Headline = BuildHeadline(res, checkable);
            res.Info = BuildReport(res);
            return res;
        }

        // per-unique-part record, read ONCE (COM reads are the expensive part on a big assembly)
        private class PartRec
        {
            public string Name;
            public string Path;
            public int SolidBodies;
            public string Material;        // null/empty => none assigned
            public string ExplicitPN;      // the part-number PROPERTY value, null if none
            public string EffectivePN;     // property value, else filename (used for collision grouping)
            public double Mass;            // kg; <=0 or NaN => no computable weight
            public bool MassMeasured;      // did the mass read actually run?
        }

        private static PartRec BuildRec(PartDoc pd, string path, string name)
        {
            var r = new PartRec { Name = name, Path = path };
            var md = pd as IModelDoc2;

            object[] bodies = null; try { bodies = pd.GetBodies2((int)swBodyType_e.swSolidBody, true) as object[]; } catch { }
            r.SolidBodies = bodies == null ? 0 : bodies.Length;

            string db = ""; try { r.Material = pd.GetMaterialPropertyName2("", out db); } catch { r.Material = null; }

            r.ExplicitPN = ReadPartNumber(md);
            r.EffectivePN = !string.IsNullOrWhiteSpace(r.ExplicitPN) ? r.ExplicitPN.Trim() : Path.GetFileNameWithoutExtension(path);

            // weight: only meaningful with a solid body. Fail closed — an unmeasurable mass counts as "no computable
            // weight", never as a silent pass.
            r.Mass = -1; r.MassMeasured = false;
            if (r.SolidBodies > 0)
            {
                try
                {
                    var mp = md.Extension.CreateMassProperty();
                    if (mp != null) { r.Mass = mp.Mass; r.MassMeasured = true; }
                }
                catch { r.MassMeasured = false; }
            }
            return r;
        }

        private static string ReadPartNumber(IModelDoc2 md)
        {
            if (md == null) return null;
            string cfg = ""; try { cfg = ((Configuration)md.GetActiveConfiguration()).Name; } catch { }
            foreach (var scope in new[] { cfg, "" })   // config-specific first, then document-level ("")
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

        // ---- missing materials: a solid part with no material assigned. Parts with no solid body (sketch/surface
        //      parts) are skipped — a missing material there isn't a release defect. ----
        private static ValidatePropFinding CheckMissingMaterials(List<PartRec> parts, ValidatePropsResult res)
        {
            var f = new ValidatePropFinding { Category = "Missing materials", Checkable = true };
            int solids = 0;
            foreach (var p in parts)
            {
                if (p.SolidBodies == 0) continue;   // no solid body => material N/A
                solids++;
                if (string.IsNullOrWhiteSpace(p.Material))
                {
                    if (f.Worst.Count < 5) f.Worst.Add(p.Name);
                    f.Count++;
                }
            }
            res.PartsChecked = solids;
            res.MissingMaterials = f.Count;
            if (solids == 0) { f.Checkable = false; f.Note = "no solid parts to check for material"; }
            else f.Note = f.Count == 0 ? "every solid part has a material" : f.Count + " of " + solids + " solid parts have no material assigned";
            return f;
        }

        // ---- missing part numbers: a unique part with NO explicit part-number property (it would BOM by filename,
        //      which is not a controlled number). ----
        private static ValidatePropFinding CheckMissingPartNos(List<PartRec> parts, ValidatePropsResult res)
        {
            var f = new ValidatePropFinding { Category = "Missing part numbers", Checkable = true };
            if (parts.Count == 0) { f.Checkable = false; f.Note = "no parts to check"; return f; }
            foreach (var p in parts)
            {
                if (string.IsNullOrWhiteSpace(p.ExplicitPN))
                {
                    if (f.Worst.Count < 5) f.Worst.Add(p.Name);
                    f.Count++;
                }
            }
            res.MissingPartNos = f.Count;
            f.Note = f.Count == 0 ? "all " + parts.Count + " parts carry a part number"
                                  : f.Count + " of " + parts.Count + " parts have no part-number property (would BOM by filename)";
            return f;
        }

        // ---- duplicate part numbers: the SAME part-number VALUE carried by TWO DIFFERENT files. Reusing one file
        //      many times is normal and NOT flagged — only a number collision across distinct files is a defect. ----
        private static ValidatePropFinding CheckDuplicatePartNos(List<PartRec> parts, ValidatePropsResult res)
        {
            var f = new ValidatePropFinding { Category = "Duplicate part numbers", Checkable = true };
            if (parts.Count == 0) { f.Checkable = false; f.Note = "no parts to check"; return f; }
            var byNumber = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);  // PN -> distinct file paths
            foreach (var p in parts)
            {
                if (string.IsNullOrWhiteSpace(p.EffectivePN)) continue;
                if (!byNumber.ContainsKey(p.EffectivePN)) byNumber[p.EffectivePN] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                byNumber[p.EffectivePN].Add(p.Path);
            }
            foreach (var kv in byNumber)
            {
                if (kv.Value.Count < 2) continue;   // one number, multiple distinct files = collision
                f.Count++;
                if (f.Worst.Count < 5) f.Worst.Add("\"" + kv.Key + "\" shared by " + kv.Value.Count + " files");
            }
            res.DuplicatePartNos = f.Count;
            f.Note = f.Count == 0 ? byNumber.Count + " part numbers, no collisions"
                                  : f.Count + " part number(s) claimed by more than one file";
            return f;
        }

        // ---- no computable weight: a part whose mass is zero / unmeasurable, or which has no solid body at all —
        //      nothing to put on a weight BOM. Fail closed: an unreadable mass is a no-weight flag, not a pass. ----
        private static ValidatePropFinding CheckNoWeight(List<PartRec> parts, ValidatePropsResult res)
        {
            var f = new ValidatePropFinding { Category = "No computable weight", Checkable = true };
            if (parts.Count == 0) { f.Checkable = false; f.Note = "no parts to check"; return f; }
            foreach (var p in parts)
            {
                bool noWeight = p.SolidBodies == 0 || !p.MassMeasured || double.IsNaN(p.Mass) || p.Mass <= 0.0;
                if (noWeight)
                {
                    string why = p.SolidBodies == 0 ? "no solid body" : (!p.MassMeasured ? "mass unreadable" : "zero mass");
                    if (f.Worst.Count < 5) f.Worst.Add(p.Name + " (" + why + ")");
                    f.Count++;
                }
            }
            res.NoWeightParts = f.Count;
            f.Note = f.Count == 0 ? "every part has a computable weight"
                                  : f.Count + " of " + parts.Count + " parts have no computable weight (no solid body or zero mass)";
            return f;
        }

        private static string BuildHeadline(ValidatePropsResult res, int checkable)
        {
            if (checkable == 0)
                return "Nothing to validate — no unique parts found on this " + res.Components + "-component assembly.";
            if (res.TotalIssues == 0)
                return "Release-ready — " + res.UniqueParts + " unique parts, " + checkable + " checks, 0 property gaps.";
            int cats = 0;
            foreach (var f in res.Findings) if (f.Checkable && f.Count > 0) cats++;
            return "NOT release-ready: " + res.TotalIssues + " property gap" + (res.TotalIssues == 1 ? "" : "s")
                   + " across " + cats + " categor" + (cats == 1 ? "y" : "ies") + " on " + res.UniqueParts + " unique parts.";
        }

        private static string BuildReport(ValidatePropsResult res)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(res.Headline);
            foreach (var f in res.Findings)
            {
                sb.Append("\n• ").Append(f.Category).Append(": ");
                if (!f.Checkable) sb.Append("not checked — ").Append(f.Note);
                else
                {
                    sb.Append(f.Count == 0 ? "clean" : f.Count.ToString()).Append(" — ").Append(f.Note);
                    if (f.Count > 0 && f.Worst.Count > 0) sb.Append(" [").Append(string.Join("; ", f.Worst)).Append("]");
                }
            }
            return sb.ToString();
        }
    }
}
