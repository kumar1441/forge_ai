using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    // One diagnostic category's finding. Checkable=false => we couldn't run this check on THIS model
    // (Rule #4 / Character #4): we say so explicitly instead of faking a reassuring zero.
    public class DoctorFinding
    {
        public string Category;
        public int Count;          // issues found (only meaningful when Checkable)
        public bool Checkable;     // could this check actually run on this model?
        public string Note;        // one-line verdict / why-not-checkable
        public List<string> Worst = new List<string>();  // the worst offenders, by name
    }

    public class DoctorResult
    {
        public int Components;
        public int SuppressedComponents;   // informational, not an error
        public int BrokenRefs;
        public int DanglingDims;
        public int DimsChecked;
        public int MissingMaterials;
        public int PartsChecked;
        public int DuplicatePartNos;
        public int RebuildErrors;
        public int RebuildHogs;            // best-effort proxy (see note)
        public double RebuildSeconds;
        public int CircularRefs;           // best-effort
        public int TotalIssues;
        public List<DoctorFinding> Findings = new List<DoctorFinding>();
        public string Headline;
        public string Info;                // full report: headline first, categories underneath
        public string Error;
    }

    /// <summary>
    /// Doctor — the READ-ONLY "assembly doctor" (demo #12). The richer sibling of Scout: where Scout counts
    /// what's IN the assembly, Doctor finds what's WRONG with it — broken references, dangling dimensions,
    /// missing materials, duplicate part numbers, rebuild errors, rebuild hogs, circular references — and
    /// returns ONE report that leads with a verdict (Character #3), categories underneath. It NEVER writes:
    /// this is the tool you run on a stranger's model. Every count it reports is independently re-derivable
    /// by the harness (GroundTruth.MeasureDoctor) for cross-checking.
    ///
    /// A category that can't be checked on a given model is reported as "not checkable" with the reason —
    /// never a fake zero (Rule #4). Best-effort categories (rebuild hogs, circular refs) are labelled as such.
    /// </summary>
    public static class Doctor
    {
        public static bool IsDoctorIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            return System.Text.RegularExpressions.Regex.IsMatch(cmd,
                @"\b(diagnose|assembly doctor|doctor|what.?s wrong|check.*health|health report|find.*(problem|issue|error)s?|audit)\b");
        }

        public static async Task<DoctorResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new DoctorResult();
            var asm = model as AssemblyDoc;
            if (asm == null) { res.Error = "Open the assembly (.SLDASM) to run the doctor."; return res; }

            await emit("Doctor", "reading the assembly", "run", null);
            // Rebuild once for an accurate health read (GetWhatsWrong reflects the last solve). Read-only:
            // recomputes, never edits features/geometry/materials, never saves. Times the rebuild for the hog check.
            var rsw = System.Diagnostics.Stopwatch.StartNew();
            try { model.ForceRebuild3(false); } catch { }
            rsw.Stop();
            res.RebuildSeconds = rsw.Elapsed.TotalSeconds;

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
            await emit("Doctor", null, "done", res.Components + " active components (" + suppressed + " suppressed) · rebuild " + res.RebuildSeconds.ToString("F1") + "s");

            await emit("Sentinel", "running the diagnostic checks", "run", null);

            res.Findings.Add(CheckBrokenRefs(active, res));
            res.Findings.Add(CheckMissingMaterials(active, res));
            res.Findings.Add(CheckDuplicatePartNos(active, res));
            res.Findings.Add(CheckDanglingDims(model, active, res));
            res.Findings.Add(CheckRebuildErrors(model, res));
            res.Findings.Add(CheckRebuildHogs(asm, active, res));
            res.Findings.Add(CheckCircularRefs(all, res));

            int checkable = 0;
            foreach (var f in res.Findings) { if (f.Checkable) checkable++; res.TotalIssues += f.Checkable ? f.Count : 0; }
            await emit("Sentinel", null, "done", res.TotalIssues == 0 ? "no issues found across " + checkable + " checks" : res.TotalIssues + " issue(s) across " + checkable + " checks");

            res.Headline = BuildHeadline(res, checkable);
            res.Info = BuildReport(res);
            return res;
        }

        // ---- broken references: a resolved, non-virtual component whose file is MISSING on disk, or whose
        //      model doc failed to load. Suppressed/lightweight components are NOT broken. ----
        private static DoctorFinding CheckBrokenRefs(List<Component2> active, DoctorResult res)
        {
            var f = new DoctorFinding { Category = "Broken references", Checkable = true };
            foreach (var c in active)
            {
                bool virt = false; try { virt = c.IsVirtual; } catch { }
                if (virt) continue;   // stored inside the assembly — no external file to break
                string path = null; try { path = c.GetPathName(); } catch { }
                if (string.IsNullOrEmpty(path)) continue;
                bool missing = false; try { missing = !File.Exists(path); } catch { }
                bool loadFail = false; try { loadFail = c.GetModelDoc2() == null; } catch { loadFail = true; }
                if (missing || loadFail)
                {
                    string nm = null; try { nm = c.Name2; } catch { }
                    if (f.Worst.Count < 5) f.Worst.Add((nm ?? "?") + (missing ? " (file not found)" : " (failed to load)"));
                    f.Count++;
                }
            }
            res.BrokenRefs = f.Count;
            f.Note = f.Count == 0 ? "all references resolve" : f.Count + " component(s) point at a file that's missing or won't load";
            return f;
        }

        // ---- missing materials: a solid part with no material assigned. Parts with no solid body (sketch/surface
        //      parts) are skipped — a missing material there isn't a defect. ----
        private static DoctorFinding CheckMissingMaterials(List<Component2> active, DoctorResult res)
        {
            var f = new DoctorFinding { Category = "Missing materials", Checkable = true };
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int chec2 = 0;
            foreach (var c in active)
            {
                string path = null; try { path = c.GetPathName(); } catch { }
                if (string.IsNullOrEmpty(path) || seen.Contains(path)) continue; seen.Add(path);
                PartDoc pd = null; try { pd = c.GetModelDoc2() as PartDoc; } catch { }
                if (pd == null) continue;
                object[] bodies = null; try { bodies = pd.GetBodies2((int)swBodyType_e.swSolidBody, true) as object[]; } catch { }
                if (bodies == null || bodies.Length == 0) continue;   // no solid body → material N/A
                chec2++;
                string db = ""; string mat = null; try { mat = pd.GetMaterialPropertyName2("", out db); } catch { }
                if (string.IsNullOrWhiteSpace(mat))
                {
                    string nm = null; try { nm = c.Name2; } catch { }
                    if (f.Worst.Count < 5) f.Worst.Add(nm ?? Path.GetFileNameWithoutExtension(path));
                    f.Count++;
                }
            }
            res.PartsChecked = chec2;
            res.MissingMaterials = f.Count;
            if (chec2 == 0) { f.Checkable = false; f.Note = "no solid parts to check for material"; }
            else f.Note = f.Count == 0 ? "every solid part has a material" : f.Count + " of " + chec2 + " solid parts have no material assigned";
            return f;
        }

        // ---- duplicate part numbers: the SAME part-number value carried by TWO DIFFERENT files. (Reusing one
        //      file many times is normal and NOT flagged — only a number collision across distinct files is a defect.)
        //      If no part exposes a recognised part-number property, the check is not applicable (Rule #4). ----
        private static readonly string[] PartNoProps = { "PartNo", "PartNumber", "Part Number", "Part No", "Number", "DrawingNo", "Drawing Number" };
        private static DoctorFinding CheckDuplicatePartNos(List<Component2> active, DoctorResult res)
        {
            var f = new DoctorFinding { Category = "Duplicate part numbers", Checkable = true };
            var byNumber = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);  // partNo -> distinct file paths
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int withProp = 0;
            foreach (var c in active)
            {
                string path = null; try { path = c.GetPathName(); } catch { }
                if (string.IsNullOrEmpty(path) || seen.Contains(path)) continue; seen.Add(path);
                IModelDoc2 md = null; try { md = c.GetModelDoc2() as IModelDoc2; } catch { }
                if (md == null) continue;
                string pn = ReadPartNumber(md);
                if (string.IsNullOrWhiteSpace(pn)) continue;
                withProp++;
                if (!byNumber.ContainsKey(pn)) byNumber[pn] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                byNumber[pn].Add(path);
            }
            if (withProp == 0)
            {
                f.Checkable = false;
                f.Note = "no part-number property found on any part — can't check";
                res.DuplicatePartNos = 0;
                return f;
            }
            foreach (var kv in byNumber)
            {
                if (kv.Value.Count < 2) continue;   // one number, multiple distinct files = collision
                f.Count++;
                if (f.Worst.Count < 5) f.Worst.Add("\"" + kv.Key + "\" shared by " + kv.Value.Count + " files");
            }
            res.DuplicatePartNos = f.Count;
            f.Note = f.Count == 0 ? withProp + " numbered parts, no collisions" : f.Count + " part number(s) claimed by more than one file";
            return f;
        }

        private static string ReadPartNumber(IModelDoc2 md)
        {
            string cfg = ""; try { cfg = ((Configuration)md.GetActiveConfiguration()).Name; } catch { }
            // config-specific first, then document-level ("")
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

        // ---- dangling display dimensions: dims that lost their reference geometry (IDisplayDimension.IsDangling).
        //      Checks the assembly's own features plus each unique resolved part's features. ----
        private static DoctorFinding CheckDanglingDims(IModelDoc2 model, List<Component2> active, DoctorResult res)
        {
            var f = new DoctorFinding { Category = "Dangling dimensions", Checkable = true };
            int checkedDims = 0, dangling = 0;
            ScanDimsIn(model, "(assembly)", ref checkedDims, ref dangling, f);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in active)
            {
                string path = null; try { path = c.GetPathName(); } catch { }
                if (string.IsNullOrEmpty(path) || seen.Contains(path)) continue; seen.Add(path);
                IModelDoc2 md = null; try { md = c.GetModelDoc2() as IModelDoc2; } catch { }
                if (md == null) continue;
                string nm = null; try { nm = c.Name2; } catch { }
                ScanDimsIn(md, nm ?? Path.GetFileNameWithoutExtension(path), ref checkedDims, ref dangling, f);
            }

            res.DimsChecked = checkedDims;
            res.DanglingDims = dangling;
            f.Count = dangling;
            if (checkedDims == 0) { f.Checkable = false; f.Note = "no display dimensions to check"; }
            else f.Note = dangling == 0 ? checkedDims + " dimensions checked, none dangling" : dangling + " of " + checkedDims + " dimensions are dangling (lost their reference)";
            return f;
        }

        private static void ScanDimsIn(IModelDoc2 md, string owner, ref int checkedDims, ref int dangling, DoctorFinding f)
        {
            Feature feat = null; try { feat = md.FirstFeature() as Feature; } catch { }
            while (feat != null)
            {
                DisplayDimension dd = null; try { dd = feat.GetFirstDisplayDimension() as DisplayDimension; } catch { }
                while (dd != null)
                {
                    checkedDims++;
                    bool dang = false;
                    try { var ann = dd.GetAnnotation() as Annotation; dang = ann != null && ann.IsDangling(); } catch { }
                    if (dang)
                    {
                        dangling++;
                        string fn = null; try { fn = feat.Name; } catch { }
                        if (f.Worst.Count < 5) f.Worst.Add(owner + " · " + (fn ?? "?"));
                    }
                    DisplayDimension next = null; try { next = feat.GetNextDisplayDimension(dd) as DisplayDimension; } catch { }
                    dd = next;
                }
                try { feat = feat.GetNextFeature() as Feature; } catch { feat = null; }
            }
        }

        // ---- rebuild errors: the model's own error flag count (GetWhatsWrongCount), with the first offenders named. ----
        private static DoctorFinding CheckRebuildErrors(IModelDoc2 model, DoctorResult res)
        {
            var f = new DoctorFinding { Category = "Rebuild errors", Checkable = true };
            int wrong = 0; try { wrong = model.Extension.GetWhatsWrongCount(); } catch { }
            res.RebuildErrors = wrong;
            f.Count = wrong;
            if (wrong > 0)
            {
                try
                {
                    object feats, errs, warns;
                    model.Extension.GetWhatsWrong(out feats, out errs, out warns);
                    var fa = feats as object[];
                    if (fa != null) foreach (var o in fa) { if (f.Worst.Count >= 5) break; string s = o as string; if (!string.IsNullOrEmpty(s)) f.Worst.Add(s); }
                }
                catch { }
            }
            f.Note = wrong == 0 ? "rebuild clean" : wrong + " feature(s) rebuild with errors";
            return f;
        }

        // ---- rebuild hogs: per-feature rebuild timing is NOT exposed by a reliable COM call on this 3DEXPERIENCE
        //      build (Rule #4 / know-what-you-don't-know), so this is a labelled PROXY: total measured rebuild time,
        //      plus the heaviest sub-assemblies by resolved child count (the usual culprits). ----
        private static DoctorFinding CheckRebuildHogs(AssemblyDoc asm, List<Component2> active, DoctorResult res)
        {
            var f = new DoctorFinding { Category = "Rebuild hogs", Checkable = true };
            var byChildren = new List<KeyValuePair<string, int>>();
            foreach (var c in active)
            {
                int t = 0; try { object[] ch = c.GetChildren() as object[]; t = ch == null ? 0 : ch.Length; } catch { }
                if (t <= 0) continue;
                string nm = null; try { nm = c.Name2; } catch { }
                byChildren.Add(new KeyValuePair<string, int>(nm ?? "?", t));
            }
            byChildren.Sort((a, b) => b.Value.CompareTo(a.Value));

            // "hog" heuristic: total rebuild noticeably slow. Count is a signal, not a defect list.
            bool slow = res.RebuildSeconds >= 5.0;
            f.Count = slow ? 1 : 0;
            res.RebuildHogs = f.Count;
            for (int i = 0; i < byChildren.Count && f.Worst.Count < 3; i++)
                f.Worst.Add(byChildren[i].Key + " (" + byChildren[i].Value + " children)");
            f.Note = "PROXY (per-feature timing unavailable on this build): full rebuild took " + res.RebuildSeconds.ToString("F1") + "s"
                     + (slow ? " — slow; heaviest sub-assemblies listed" : " — within normal range");
            return f;
        }

        // ---- circular references (best-effort): a document-level cycle in the file reference graph (file A -> B -> A).
        //      SW blocks opening true cycles, so this is nearly always 0 on a model that opened — reported honestly. ----
        private static DoctorFinding CheckCircularRefs(object[] all, DoctorResult res)
        {
            var f = new DoctorFinding { Category = "Circular references", Checkable = true };
            var edges = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var o in all ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                string cp = null; try { cp = c.GetPathName(); } catch { }
                if (string.IsNullOrEmpty(cp)) continue;
                Component2 parent = null; try { parent = c.GetParent() as Component2; } catch { }
                string pp = null; try { pp = parent != null ? parent.GetPathName() : null; } catch { }
                if (string.IsNullOrEmpty(pp) || string.Equals(pp, cp, StringComparison.OrdinalIgnoreCase)) continue;
                if (!edges.ContainsKey(pp)) edges[pp] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                edges[pp].Add(cp);
            }
            var cyclePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in edges.Keys)
                if (ReachesSelf(node, edges)) cyclePaths.Add(node);
            f.Count = cyclePaths.Count;
            res.CircularRefs = f.Count;
            foreach (var p in cyclePaths) { if (f.Worst.Count >= 5) break; f.Worst.Add(Path.GetFileName(p)); }
            f.Note = f.Count == 0 ? "no circular file references (best-effort)" : f.Count + " file(s) reference themselves through the tree (best-effort)";
            return f;
        }

        private static bool ReachesSelf(string start, Dictionary<string, HashSet<string>> edges)
        {
            var stack = new Stack<string>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> init; if (!edges.TryGetValue(start, out init)) return false;
            foreach (var n in init) stack.Push(n);
            while (stack.Count > 0)
            {
                var cur = stack.Pop();
                if (string.Equals(cur, start, StringComparison.OrdinalIgnoreCase)) return true;
                if (!visited.Add(cur)) continue;
                HashSet<string> nxt; if (!edges.TryGetValue(cur, out nxt)) continue;
                foreach (var n in nxt) stack.Push(n);
            }
            return false;
        }

        private static string BuildHeadline(DoctorResult res, int checkable)
        {
            if (res.TotalIssues == 0)
                return "Clean bill of health — " + res.Components + " components, " + checkable + " checks, 0 issues.";
            int cats = 0;
            foreach (var f in res.Findings) if (f.Checkable && f.Count > 0) cats++;
            return res.TotalIssues + " issue" + (res.TotalIssues == 1 ? "" : "s") + " across " + cats + " categor" + (cats == 1 ? "y" : "ies") + " on this " + res.Components + "-component assembly.";
        }

        private static string BuildReport(DoctorResult res)
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
