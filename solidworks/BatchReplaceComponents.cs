using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class BatchReplaceComponentsPairResult
    {
        public string Kind;
        public string File;
        public int Matched;
        public int Replaced;
        public int AlreadyReplaced;
        public int Failed;
        public bool ApiReturn;
    }

    public class BatchReplaceComponentsResult
    {
        public List<BatchReplaceComponentsPairResult> Pairs = new List<BatchReplaceComponentsPairResult>();
        public int TotalMatched;
        public int TotalReplaced;
        public int TotalFailed;
        public bool Verified;
        public int RebuildErrors;
        public bool NeedsConfirm;
        public string Question;
        public string Diag;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// BatchReplaceComponents (tool #164 batch_replace_components, WRITE) — swap MULTIPLE DIFFERENT component kinds
    /// for MULTIPLE DIFFERENT replacement files in ONE command via an explicit mapping list: 'replace the plate with
    /// "X.SLDPRT" and the bolts with "Y.SLDPRT"'. DISTINCT from replace_component (tool 31 — ONE target file
    /// broadcast to a filtered set) and change_component_config (tool 39 — a config switch, same file, cedes here
    /// only for a bare M-size pair which stays with Upsize/ChangeComponentConfig): this is the "many swaps, many
    /// distinct targets, validated as one batch" primitive the family (LH-&gt;RH filename substitution / explicit
    /// list / configuration mapping) describes. Configuration-mapping mode is already covered by
    /// change_component_config / Upsize; this handler covers the explicit-list / multi-kind file-swap mode.
    ///
    /// API: the same AssemblyDoc.ReplaceComponents(file, config, replaceAllInstances, reAttachMates) as tool 31,
    /// PROVEN LIVE headless on this build, called ONCE PER (kind,file) PAIR (each pair needs its own selection set
    /// and target file — ReplaceComponents takes a single file for whatever is currently selected). PRE-VALIDATES
    /// every target file exists BEFORE touching any component (no partial mutation from one typo'd path in a
    /// multi-swap command). FAILS CLOSED per pair via an independent per-instance GetPathName read-back.
    /// </summary>
    public static class BatchReplaceComponents
    {
        private static readonly string[] Kinds = { "plate", "bolt", "nut", "washer", "screw", "shaft", "gear", "housing", "base", "flange" };

        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (!Regex.IsMatch(c, @"\b(replace|swap|substitute)\b")) return false;
            if (!Regex.IsMatch(c, @"\band\b")) return false;
            // the distinguishing signal vs. replace_component: TWO OR MORE file references in one command
            var fileRefs = Regex.Matches(cmd, "[\"'][^\"']+[\"']|[A-Za-z]:\\\\[^\"'\\s]+?\\.sld(?:prt|asm)", RegexOptions.IgnoreCase);
            return fileRefs.Count >= 2;
        }

        public static async Task<BatchReplaceComponentsResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new BatchReplaceComponentsResult();
            var asm = model as AssemblyDoc;
            if (asm == null) { res.Error = "Open the assembly (.SLDASM) to batch-replace components."; return res; }

            string cmd = intent ?? "";
            await emit("Gauge", "resolving the swap mapping", "run", null);

            // ---- split on "and", pull a (kind, file) pair out of each clause ----
            var pairs = new List<(string Kind, string File)>();
            foreach (var clause in Regex.Split(cmd, @"\band\b", RegexOptions.IgnoreCase))
            {
                var fm = Regex.Match(clause, "[\"']([^\"']+)[\"']");
                string file = fm.Success ? fm.Groups[1].Value.Trim() : null;
                if (string.IsNullOrEmpty(file))
                {
                    var mp = Regex.Match(clause, @"([A-Za-z]:\\[^""']+?\.sld(?:prt|asm))", RegexOptions.IgnoreCase);
                    if (mp.Success) file = mp.Groups[1].Value.Trim();
                }
                if (string.IsNullOrEmpty(file)) continue;
                string lc = clause.ToLowerInvariant();
                string kind = Kinds.FirstOrDefault(k => Regex.IsMatch(lc, @"\b" + k + @"s?\b"));
                if (kind == null) continue;
                pairs.Add((kind, file));
            }

            if (pairs.Count < 2)
            {
                res.NeedsConfirm = true;
                res.Question = "Which components map to which replacement files? Name a kind (bolts / plate / ...) and a file for each swap.";
                await emit("Gauge", null, "ask", "fewer than 2 resolvable (kind,file) pairs");
                return res;
            }

            // ---- PRE-VALIDATE every target file exists BEFORE touching anything (atomic mapping) ----
            var missing = pairs.Where(p => !File.Exists(p.File)).Select(p => p.File).Distinct().ToList();
            if (missing.Count > 0)
            {
                res.Error = "Target file(s) not found, nothing changed: " + string.Join(", ", missing);
                await emit("Gauge", null, "fail", res.Error);
                return res;
            }

            var comps = new List<Component2>();
            foreach (var o in (asm.GetComponents(true) as object[]) ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { }
                if (!sup) comps.Add(c);
            }

            await emit("Gauge", null, "done", pairs.Count + " swap pair(s) resolved and pre-validated");
            await emit("Scribe", "swapping each kind's target file", "run", null);

            foreach (var pair in pairs)
            {
                var pr = new BatchReplaceComponentsPairResult { Kind = pair.Kind, File = pair.File };
                var targets = comps.Where(c => MatchesKind(NameOf(c), pair.Kind)).ToList();
                pr.Matched = targets.Count;
                if (targets.Count == 0) { res.Pairs.Add(pr); continue; }

                string want = Norm(pair.File);
                int already = targets.Count(c => Norm(PathOf(c)) == want);
                if (already == targets.Count)
                {
                    pr.AlreadyReplaced = already; pr.Replaced = already;
                    res.Pairs.Add(pr);
                    continue;
                }

                int oe = 0, ow = 0;
                try { app.OpenDoc6(pair.File, (int)swDocumentTypes_e.swDocPART, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref oe, ref ow); } catch { }
                try { model.ClearSelection2(true); } catch { }
                int sel = 0;
                foreach (var c in targets) { try { if (c.Select4(sel > 0, null, false)) sel++; } catch { } }

                bool apiRet = false;
                try { apiRet = asm.ReplaceComponents(pair.File, "", true, true); } catch { }
                pr.ApiReturn = apiRet;
                try { model.ClearSelection2(true); } catch { }

                // re-walk the tree fresh (the component list mutates after a file swap) before reading back
                var freshTargets = new List<Component2>();
                foreach (var o in (asm.GetComponents(true) as object[]) ?? new object[0])
                {
                    var c = o as Component2; if (c == null) continue;
                    bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                    if (MatchesKind(NameOf(c), pair.Kind)) freshTargets.Add(c);
                }
                int onNew = freshTargets.Count(c => Norm(PathOf(c)) == want);
                pr.Replaced = onNew;
                pr.Failed = pr.Matched - onNew;
                res.Pairs.Add(pr);
            }

            try { model.EditRebuild3(); } catch { try { model.ForceRebuild3(false); } catch { } }
            res.RebuildErrors = SafeWhatsWrong(model);

            await emit("Sentinel", "verifying every pair's targets now reference their replacement file", "run", null);
            res.TotalMatched = res.Pairs.Sum(p => p.Matched);
            res.TotalReplaced = res.Pairs.Sum(p => p.Replaced);
            res.TotalFailed = res.Pairs.Sum(p => p.Failed) + res.Pairs.Count(p => p.Matched == 0);
            res.Verified = res.Pairs.Count > 0 && res.Pairs.All(p => p.Matched > 0 && p.Replaced >= p.Matched) && res.RebuildErrors == 0;
            res.Diag = string.Join(" | ", res.Pairs.Select(p => p.Kind + ":matched=" + p.Matched + ",replaced=" + p.Replaced + ",already=" + p.AlreadyReplaced)) + " rebuildErr=" + res.RebuildErrors;

            if (!res.Verified)
            {
                var zero = res.Pairs.Where(p => p.Matched == 0).Select(p => p.Kind).ToList();
                res.Error = zero.Count > 0
                    ? "No component matched kind(s): " + string.Join(", ", zero) + ". " + res.Diag
                    : "Batch replace did not fully take (" + res.Diag + ").";
                await emit("Sentinel", null, "fail", res.Error);
                return res;
            }

            res.Info = "Swapped " + res.Pairs.Count + " kind(s): " + string.Join(", ", res.Pairs.Select(p => p.Kind + " (" + p.Replaced + "/" + p.Matched + " -> " + Path.GetFileName(p.File) + ")")) + ". One Ctrl+Z restores it; Forge didn't save.";
            await emit("Sentinel", null, "done", res.TotalReplaced + "/" + res.TotalMatched + " across " + res.Pairs.Count + " kind(s) · clean");
            return res;
        }

        private static string NameOf(Component2 c) { try { return (c.Name2 ?? "").ToLowerInvariant(); } catch { return ""; } }
        private static string PathOf(Component2 c) { try { return c.GetPathName(); } catch { return null; } }

        private static bool MatchesKind(string nm, string kind)
        {
            if (kind == "bolt" || kind == "screw") { if (nm.Contains("nut") || nm.Contains("washer") || nm.Contains("plate")) return false; return nm.Contains("bolt") || nm.Contains("screw") || nm.Contains("hcs") || nm.Contains("hex"); }
            return nm.Contains(kind);
        }

        private static string Norm(string p) { return string.IsNullOrEmpty(p) ? "" : p.Trim().ToLowerInvariant().Replace('/', '\\'); }
        private static int SafeWhatsWrong(IModelDoc2 model) { try { return model.Extension.GetWhatsWrongCount(); } catch { return 0; } }
    }
}
