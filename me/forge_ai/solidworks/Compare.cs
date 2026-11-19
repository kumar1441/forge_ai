using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class DimDelta
    {
        public string Name;     // dimension full name, e.g. "D1@Sketch1"
        public string Feature;  // owning feature (for display)
        public string Type;     // "diameter" | "linear" | "angular" | ...
        public double OldMm;
        public double NewMm;
        public double DeltaMm => NewMm - OldMm;
    }

    public class PartChange
    {
        public string Name;                                 // component Name2 (the part that changed)
        public List<DimDelta> Dims = new List<DimDelta>();
        public string Summary;                              // one-line human description
    }

    public class CompareResult
    {
        public int TotalParts;                              // M — parts compared in the OPEN version
        public int ChangedParts;                            // N — parts with a dimensional change
        public List<PartChange> Changes = new List<PartChange>();
        public List<string> Added = new List<string>();     // in the OTHER version, not in this one
        public List<string> Removed = new List<string>();   // in this version, not in the other
        public List<string> Renamed = new List<string>();   // "old → new" (same part file, new instance name)
        public string OtherPath;                            // the second version we compared against
        public string Info;
        public string Error;
        public bool NeedsConfirm;                           // Rule #2 — couldn't resolve a second version; ask one question
        public string Question;
    }

    /// <summary>
    /// Compare — READ-ONLY diff of the currently-open version against a SECOND version (demo #2 "What changed in
    /// Rev G?"). Reports (a) the component set — parts added / removed / renamed — and (b) per-part dimension deltas
    /// (name → old vs new, Δ in mm), so an engineer sees "3 of 214 parts changed: bracket_07, hole moved 4mm".
    ///
    /// The second version's path comes from the prompt (an explicit .SLDASM/.SLDPRT path) or a same-folder sibling.
    /// It is opened read-only via OpenDoc6 and closed WITHOUT saving; the open version is never rebuilt or written.
    /// Dimensions are read with the SAME reader the variant loop uses (VariantGenerator.ReadDimensions) on both
    /// versions. If no second version resolves, it asks ONE question (Rule #2) and changes nothing.
    /// </summary>
    public static class Compare
    {
        public static bool IsCompareIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            return Regex.IsMatch(cmd,
                @"\b(compare|what changed|whats changed|what did .* change|diff|difference|rev [a-z]|revision|these two versions|the two versions|other version|previous version)\b",
                RegexOptions.IgnoreCase);
        }

        public static async Task<CompareResult> Run(ISldWorks app, IModelDoc2 model, string intent, string attachedFile, Func<string, string, string, string, Task> emit)
        {
            var res = new CompareResult();
            if (model == null) { res.Error = "Open a version first, then tell me which other version to compare it against."; return res; }
            int thisType = (int)model.GetType();
            if (thisType != (int)swDocumentTypes_e.swDocASSEMBLY && thisType != (int)swDocumentTypes_e.swDocPART)
            { res.Error = "Open a part or an assembly to compare versions."; return res; }
            string thisPath = null; try { thisPath = model.GetPathName(); } catch { }

            // ---- resolve the SECOND version. A real user never types a PC path (panel-testing.md): resolve, in order,
            //      a typed path (dev) -> an ATTACHED file (📎 button) -> another OPEN document -> a same-folder rev
            //      sibling. Only ask ONE question (Rule #2) when none of those resolve. ----
            await emit("Ledger", "finding the version to compare against", "run", null);
            string ask; List<string> options;
            string other = ResolveOtherPath(app, intent, thisPath, thisType, attachedFile, out ask, out options);
            if (other == null)
            {
                await emit("Ledger", null, "done", "no second version resolved");
                res.NeedsConfirm = true; res.Question = ask; return res;
            }
            res.OtherPath = other;
            await emit("Ledger", null, "done", "comparing against " + Path.GetFileName(other));

            // ---- open the second version READ-ONLY (or REUSE it if the user already has it open), diff, then close
            //      ONLY what we opened. If it was already open, we leave it exactly as we found it (Rule #7). ----
            int otherType = other.EndsWith(".sldprt", StringComparison.OrdinalIgnoreCase)
                ? (int)swDocumentTypes_e.swDocPART : (int)swDocumentTypes_e.swDocASSEMBLY;
            if (otherType != thisType)
            { res.Error = "Those are different document types — compare a part to a part, or an assembly to an assembly."; return res; }

            int errs = 0, warns = 0; string otherTitle = null; IModelDoc2 v2 = null; bool wasOpen = false;
            try
            {
                try { v2 = app.GetOpenDocumentByName(other) as IModelDoc2; if (v2 != null) wasOpen = true; } catch { }
                if (v2 == null)
                    v2 = app.OpenDoc6(other, otherType,
                        (int)swOpenDocOptions_e.swOpenDocOptions_Silent | (int)swOpenDocOptions_e.swOpenDocOptions_ReadOnly,
                        "", ref errs, ref warns) as IModelDoc2;
                if (v2 == null) { res.Error = "Couldn't open " + Path.GetFileName(other) + " (errs=" + errs + ", warns=" + warns + ")."; return res; }
                try { otherTitle = v2.GetTitle(); } catch { }

                if (thisType == (int)swDocumentTypes_e.swDocASSEMBLY)
                    await DiffAssemblies(model as AssemblyDoc, v2 as AssemblyDoc, res, emit);
                else
                    await DiffParts(model, v2, res, emit);
            }
            catch (Exception ex) { res.Error = ex.Message; }
            finally
            {
                // READ-ONLY GUARANTEE: close only the copy WE opened; never close a document the user already had open.
                try { if (!wasOpen && otherTitle != null) app.CloseDoc(otherTitle); } catch { }
            }
            if (res.Error != null) return res;

            res.Info = BuildInfo(res);
            return res;
        }

        // ---- assembly diff: component set (add/remove/rename) + per-common-part dimension deltas ----
        private static async Task DiffAssemblies(AssemblyDoc a1, AssemblyDoc a2, CompareResult res, Func<string, string, string, string, Task> emit)
        {
            await emit("Diff", "reading both component trees", "run", null);
            var m1 = Inventory(a1);
            var m2 = Inventory(a2);
            res.TotalParts = m1.Count;

            var removed = m1.Keys.Where(k => !m2.ContainsKey(k)).ToList();
            var added = m2.Keys.Where(k => !m1.ContainsKey(k)).ToList();

            // rename: a removed instance + an added instance that reference the SAME part file → a renamed instance.
            var renamedFrom = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var renamedTo = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in removed)
            {
                if (string.IsNullOrEmpty(m1[r].File)) continue;
                foreach (var ad in added)
                {
                    if (renamedTo.Contains(ad)) continue;
                    if (string.Equals(m1[r].File, m2[ad].File, StringComparison.OrdinalIgnoreCase))
                    { res.Renamed.Add(r + " -> " + ad); renamedFrom.Add(r); renamedTo.Add(ad); break; }
                }
            }
            res.Removed.AddRange(removed.Where(r => !renamedFrom.Contains(r)));
            res.Added.AddRange(added.Where(a => !renamedTo.Contains(a)));
            await emit("Diff", null, "done", m1.Count + " parts here · " + m2.Count + " there · " +
                res.Added.Count + " added, " + res.Removed.Count + " removed" + (res.Renamed.Count > 0 ? ", " + res.Renamed.Count + " renamed" : ""));

            // per-common-part dimension diff (grounds every claim in the two live models)
            var common = m1.Keys.Where(k => m2.ContainsKey(k)).ToList();
            await emit("Diff", "checking dimensions on " + common.Count + " shared parts", "run", null);
            int idx = 0;
            foreach (var name in common)
            {
                idx++;
                if (common.Count > 40 && idx % 25 == 0) await emit("Diff", null, "run", "checked " + idx + "/" + common.Count);
                var deltas = DiffComponentDims(m1[name].Comp, m2[name].Comp);
                if (deltas.Count > 0)
                    res.Changes.Add(new PartChange { Name = name, Dims = deltas, Summary = DescribeDeltas(name, deltas) });
            }
            res.ChangedParts = res.Changes.Count;
            await emit("Diff", null, "done", res.ChangedParts + " of " + res.TotalParts + " parts changed dimensionally");
        }

        // ---- part-vs-part diff: dimension deltas only (no component set) ----
        private static async Task DiffParts(IModelDoc2 p1, IModelDoc2 p2, CompareResult res, Func<string, string, string, string, Task> emit)
        {
            await emit("Diff", "reading both parts' dimensions", "run", null);
            var deltas = DiffDimLists(VariantGenerator.ReadDimensions(p1), VariantGenerator.ReadDimensions(p2));
            res.TotalParts = 1;
            if (deltas.Count > 0)
            {
                string nm = null; try { nm = Path.GetFileNameWithoutExtension(p1.GetPathName()); } catch { }
                if (string.IsNullOrEmpty(nm)) nm = "this part";
                res.Changes.Add(new PartChange { Name = nm, Dims = deltas, Summary = DescribeDeltas(nm, deltas) });
                res.ChangedParts = 1;
            }
            await emit("Diff", null, "done", deltas.Count + " dimension" + (deltas.Count == 1 ? "" : "s") + " changed");
        }

        private class Inv { public Component2 Comp; public string File; }

        private static Dictionary<string, Inv> Inventory(AssemblyDoc asm)
        {
            var d = new Dictionary<string, Inv>(StringComparer.OrdinalIgnoreCase);
            if (asm == null) return d;
            object[] comps = asm.GetComponents(false) as object[];
            foreach (var o in comps ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                string nm = null; try { nm = c.Name2; } catch { } if (string.IsNullOrEmpty(nm)) continue;
                string f = null; try { f = Path.GetFileName(c.GetPathName()); } catch { }
                if (!d.ContainsKey(nm)) d[nm] = new Inv { Comp = c, File = f };
            }
            return d;
        }

        // read each version of the part via the SHARED reader (VariantGenerator.ReadDimensions), then diff by dim name.
        private static List<DimDelta> DiffComponentDims(Component2 c1, Component2 c2)
        {
            IModelDoc2 d1 = null, d2 = null;
            try { d1 = c1.GetModelDoc2() as IModelDoc2; } catch { }
            try { d2 = c2.GetModelDoc2() as IModelDoc2; } catch { }
            if (d1 == null || d2 == null) return new List<DimDelta>();  // unmeasurable (lightweight/unloaded) → no false claim
            return DiffDimLists(VariantGenerator.ReadDimensions(d1), VariantGenerator.ReadDimensions(d2));
        }

        // Dimension.FullName carries the OWNING DOCUMENT as its last segment ("D1@Boss-Extrude1@plate-v1.Part").
        // Two revisions are, by definition, two differently-named files — so keying the diff on the raw FullName
        // matched NOTHING and the handler reported "0 dimensions changed" on a pair whose thickness had moved
        // 12mm → 16mm. Key on everything BUT the document segment. (Measured on the twoversion fixture, 2026-07-24.)
        private static string DimKey(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return fullName;
            var parts = fullName.Split('@');
            if (parts.Length < 3) return fullName;
            return string.Join("@", parts, 0, parts.Length - 1);
        }

        private static List<DimDelta> DiffDimLists(List<DimInfo> oldDims, List<DimInfo> newDims)
        {
            var deltas = new List<DimDelta>();
            var byName = new Dictionary<string, DimInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in newDims) { var k = DimKey(d.Name); if (k != null && !byName.ContainsKey(k)) byName[k] = d; }
            foreach (var d in oldDims)
            {
                DimInfo nd; if (d.Name == null || !byName.TryGetValue(DimKey(d.Name), out nd)) continue;   // value-change focus
                if (Math.Abs(nd.ValueMm - d.ValueMm) > 0.01)
                    deltas.Add(new DimDelta { Name = d.Name, Feature = d.Feature, Type = d.Type, OldMm = d.ValueMm, NewMm = nd.ValueMm });
            }
            return deltas;
        }

        private static string DescribeDeltas(string name, List<DimDelta> ds)
        {
            ds.Sort((x, y) => Math.Abs(y.DeltaMm).CompareTo(Math.Abs(x.DeltaMm)));
            string s = name + ": " + Friendly(ds[0]);
            if (ds.Count > 1) s += " (+" + (ds.Count - 1) + " more dim" + (ds.Count - 1 == 1 ? "" : "s") + ")";
            return s;
        }

        // Human delta — mm for linear/diameter, degrees for angular (VariantGenerator stores angular ValueMm as rad×1000).
        private static string Friendly(DimDelta d)
        {
            if (d.Type == "angular")
            {
                double oldDeg = d.OldMm / 1000.0 * 180.0 / Math.PI, newDeg = d.NewMm / 1000.0 * 180.0 / Math.PI, dd = newDeg - oldDeg;
                // ASCII " -> " (not the → glyph, which the panel encoding eats — that turned "12->16" into "1216").
                return ShortFeature(d.Feature) + " " + F(oldDeg) + " deg -> " + F(newDeg) + " deg (" + (dd >= 0 ? "+" : "") + F(dd) + " deg)";
            }
            return ShortFeature(d.Feature) + " " + F(d.OldMm) + "mm -> " + F(d.NewMm) + "mm (" + (d.DeltaMm >= 0 ? "+" : "") + F(d.DeltaMm) + "mm)";
        }
        private static string F(double v) => Math.Abs(v - Math.Round(v)) < 1e-6 ? ((long)Math.Round(v)).ToString() : v.ToString("0.##");
        private static string ShortFeature(string f) => string.IsNullOrEmpty(f) ? "a dimension" : f;

        // ---- verdict first (Character #3), then the specific numbers (Character #2) ----
        private static string BuildInfo(CompareResult res)
        {
            int structural = res.Added.Count + res.Removed.Count + res.Renamed.Count;
            if (res.ChangedParts == 0 && structural == 0)
                return "No differences — the two versions match in components and dimensions.";

            var sb = new StringBuilder();
            sb.Append(res.ChangedParts + " of " + res.TotalParts + " part" + (res.TotalParts == 1 ? "" : "s") + " changed");
            if (structural > 0)
            {
                var bits = new List<string>();
                if (res.Added.Count > 0) bits.Add(res.Added.Count + " added");
                if (res.Removed.Count > 0) bits.Add(res.Removed.Count + " removed");
                if (res.Renamed.Count > 0) bits.Add(res.Renamed.Count + " renamed");
                sb.Append(" · " + string.Join(", ", bits));
            }
            sb.Append(".");
            if (res.Changes.Count > 0)
            {
                var top = res.Changes.OrderByDescending(c => c.Dims.Max(d => Math.Abs(d.DeltaMm))).Take(5).Select(c => c.Summary);
                sb.Append(" " + string.Join("; ", top) + ".");
            }
            if (res.Added.Count > 0) sb.Append(" Added: " + string.Join(", ", res.Added.Take(5)) + (res.Added.Count > 5 ? ", …" : "") + ".");
            if (res.Removed.Count > 0) sb.Append(" Removed: " + string.Join(", ", res.Removed.Take(5)) + (res.Removed.Count > 5 ? ", …" : "") + ".");
            if (res.Renamed.Count > 0) sb.Append(" Renamed: " + string.Join(", ", res.Renamed.Take(5)) + (res.Renamed.Count > 5 ? ", …" : "") + ".");
            return sb.ToString();
        }

        // ---- resolve the second version: a typed path (dev) -> an ATTACHED file -> another OPEN document ->
        //      an unambiguous same-folder rev sibling. A human never types a path, so the last three carry the demo. ----
        private static string ResolveOtherPath(ISldWorks app, string intent, string thisPath, int thisType, string attachedFile, out string ask, out List<string> options)
        {
            ask = null; options = new List<string>();

            // (1) an explicit path typed in the prompt (developer/legacy path — a human never does this).
            string p = ExtractPath(intent);
            if (p != null && File.Exists(p) && !SamePath(p, thisPath)) return p;
            if (p != null && !File.Exists(p)) { ask = "I couldn't find \"" + p + "\". Double-check the path to the other version."; return null; }

            // (2) a file the user handed us via the 📎 attach button — the human way to point Forge at a specific file.
            if (!string.IsNullOrEmpty(attachedFile))
            {
                if (File.Exists(attachedFile) && !SamePath(attachedFile, thisPath)) return attachedFile;
                if (!File.Exists(attachedFile)) { ask = "The attached file \"" + Path.GetFileName(attachedFile) + "\" is no longer where I saw it — attach it again?"; return null; }
            }

            // (3) another document the user already has OPEN of the same type (the recordable demo: both revisions
            //     open, "compare the two open versions"). One other open doc -> use it; several -> prefer a rev-name
            //     sibling of the open version, else ask which.
            var openOthers = OpenDocsOfType(app, thisType, thisPath);
            if (openOthers.Count == 1) return openOthers[0];
            if (openOthers.Count > 1)
            {
                var strongOpen = SiblingsByName(openOthers, thisPath);
                if (strongOpen.Count == 1) return strongOpen[0];
                options = openOthers; ask = AskWhich(openOthers); return null;
            }

            // (4) a same-folder sibling on disk (rev A/B naming), for the case where only one version is open.
            if (!string.IsNullOrEmpty(thisPath))
            {
                string dir = null, ext = null, baseName = null;
                try { dir = Path.GetDirectoryName(thisPath); ext = Path.GetExtension(thisPath); baseName = Path.GetFileNameWithoutExtension(thisPath); } catch { }
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    // Exclude SolidWorks lock/owner files (~$name.SLDPRT), which exist while a doc is open and would
                    // otherwise normalize to the same rev-name as the real part → a phantom second sibling → a false ask.
                    var sibs = Directory.GetFiles(dir, "*" + ext)
                        .Where(f => !SamePath(f, thisPath) && !Path.GetFileName(f).StartsWith("~"))
                        .ToList();
                    string norm = Normalize(baseName);
                    var strong = sibs.Where(f => Normalize(Path.GetFileNameWithoutExtension(f)) == norm).ToList();
                    if (strong.Count == 1) return strong[0];
                    if (strong.Count > 1) { options = strong; ask = AskWhich(strong); return null; }
                    if (sibs.Count == 1) return sibs[0];
                    if (sibs.Count > 1) { options = sibs; ask = AskWhich(sibs); return null; }
                }
            }
            ask = "I couldn't find a second version to compare against. Open the other version too (then say \"compare the two open versions\"), or attach it with the 📎 button.";
            return null;
        }

        // Paths of every OPEN document of the given type except the active one (saved docs only — an unsaved doc
        // has no path to diff against). The enumeration SolidWorks itself trusts here (GetDocuments works headless).
        private static List<string> OpenDocsOfType(ISldWorks app, int thisType, string thisPath)
        {
            var outl = new List<string>();
            try
            {
                var docs = app.GetDocuments() as object[];
                foreach (var o in docs ?? new object[0])
                {
                    var d = o as IModelDoc2; if (d == null) continue;
                    int dt; try { dt = (int)d.GetType(); } catch { continue; }
                    if (dt != thisType) continue;
                    string dp = null; try { dp = d.GetPathName(); } catch { }
                    if (string.IsNullOrEmpty(dp) || SamePath(dp, thisPath)) continue;
                    if (!outl.Any(x => SamePath(x, dp))) outl.Add(dp);
                }
            }
            catch { }
            return outl;
        }

        // Of the given paths, the ones whose rev-normalized name matches the open version's — "part-v1" vs "part-v2",
        // "…-A" vs "…-B", etc. (Normalize drops version words + digits, so two revisions of the same part collapse.)
        private static List<string> SiblingsByName(List<string> paths, string thisPath)
        {
            string baseName = null; try { baseName = Path.GetFileNameWithoutExtension(thisPath); } catch { }
            string norm = Normalize(baseName);
            if (string.IsNullOrEmpty(norm)) return new List<string>();
            return paths.Where(f => Normalize(Path.GetFileNameWithoutExtension(f)) == norm).ToList();
        }

        private static string AskWhich(List<string> files)
        {
            var names = files.Take(5).Select(Path.GetFileName);
            return "A few versions sit in that folder — which should I compare against? " +
                   string.Join(", ", names) + (files.Count > 5 ? ", …" : "") + " (give me the name or full path).";
        }

        private static string ExtractPath(string intent)
        {
            if (string.IsNullOrEmpty(intent)) return null;
            var q = Regex.Match(intent, "[\"']([^\"']+\\.(?:sldasm|sldprt))[\"']", RegexOptions.IgnoreCase);
            if (q.Success) return q.Groups[1].Value.Trim();
            var m = Regex.Match(intent, @"([a-zA-Z]:\\.+?\.(?:sldasm|sldprt)|\\\\[^\s].+?\.(?:sldasm|sldprt))", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value.Trim();
            return null;
        }

        // normalize a filename for "same part, different rev" matching: drop version words, then digits/punctuation.
        private static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.ToLowerInvariant();
            s = Regex.Replace(s, @"\b(rev|revision|ver|version|v|copy|old|new|final|draft)\b", " ");
            s = Regex.Replace(s, @"[^a-z]", "");
            return s;
        }

        private static bool SamePath(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
            catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        }
    }
}
