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
    public class ReplaceComponentResult
    {
        public string ReplacementPath;
        public string TargetFilter;
        public int Matched;
        public int Replaced;             // instances whose file read back as the replacement
        public int AlreadyReplaced;      // instances already referencing the replacement file (idempotent rerun)
        public int Failed;
        public bool Verified;
        public int RebuildErrors;
        public bool ApiReturn;           // what ReplaceComponents itself returned
        public bool NeedsConfirm;
        public string Question;
        public string Diag;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// ReplaceComponent (tool #31 replace_component, WRITE) — swap a component's PART FILE for a different one, re-attaching
    /// mates. "replace the bolts with C:\parts\longer-bolt.SLDPRT", "swap the housing for the v2 part". DISTINCT from
    /// change_component_config (tool 39), which switches which CONFIG a component references (same file); this swaps the FILE.
    ///
    /// API: AssemblyDoc.ReplaceComponents(fileName, config, replaceAllInstances, reAttachMates) -> bool, on the SELECTED
    /// components. UNPROVEN headless on this 3DEXPERIENCE build (many assembly-write APIs no-op here), so this is instrument-
    /// first: it logs the raw API return AND an independent per-instance GetPathName read-back, and FAILS CLOSED (Rule #6) —
    /// it reports success only when the target instances actually read back as the replacement file. Forge never saves.
    /// </summary>
    public static class ReplaceComponent
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            // two bare M-sizes ("M6 ... M8") is an upsize, not a file swap
            if (Regex.IsMatch(c, @"\bm\d+\b.*\bm\d+\b")) return false;
            if (!Regex.IsMatch(c, @"\b(replace|swap|substitute|change)\b")) return false;
            if (!Regex.IsMatch(c, @"\b(component|components|part|parts|instance|instances|bolt|bolts|nut|nuts|screw|screws|washer|washers|housing|base|shaft|gear|file)\b")) return false;
            if (!Regex.IsMatch(c, @"\b(with|for|by|to)\b")) return false;
            // must reference a FILE — a quoted path or a .sldprt/.sldasm token (this separates it from a config/size swap)
            return Regex.IsMatch(c, "[\"'][^\"']+[\"']") || Regex.IsMatch(c, @"\.sld(prt|asm)\b");
        }

        public static async Task<ReplaceComponentResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new ReplaceComponentResult();
            var asm = model as AssemblyDoc;
            if (asm == null) { res.Error = "Open the assembly (.SLDASM) to replace a component."; return res; }

            string cmd = intent ?? "";
            await emit("Gauge", "resolving the replacement file and the targets", "run", null);

            // ---- replacement path: quoted first, else a bare .sldprt/.sldasm token ----
            string path = null;
            var q = Regex.Match(cmd, "[\"']([^\"']+)[\"']");
            if (q.Success) path = q.Groups[1].Value.Trim();
            if (string.IsNullOrEmpty(path)) { var mp = Regex.Match(cmd, @"([A-Za-z]:\\[^""']+?\.sld(?:prt|asm))", RegexOptions.IgnoreCase); if (mp.Success) path = mp.Groups[1].Value.Trim(); }
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                res.NeedsConfirm = true;
                res.Question = "Which file should I swap in? Give the full path to the replacement .SLDPRT.";
                await emit("Gauge", null, "ask", "replacement path missing/not found");
                return res;
            }
            res.ReplacementPath = path;

            // ---- targets ----
            var comps = new List<Component2>();
            foreach (var o in (asm.GetComponents(true) as object[]) ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { }
                if (!sup) comps.Add(c);
            }
            var targets = ResolveTargets(comps, cmd.ToLowerInvariant(), out string filter);
            res.TargetFilter = filter;
            if (targets.Count == 0)
            {
                res.NeedsConfirm = true;
                res.Question = "Which components should I replace? Name a kind (bolts / nuts) or a part.";
                await emit("Gauge", null, "ask", "no target matched");
                return res;
            }
            res.Matched = targets.Count;

            // ---- IDEMPOTENT (Rule #5): if every target already references the replacement file, nothing to do ----
            string wantPath = Norm(path);
            int already = targets.Count(c => { string p = null; try { p = c.GetPathName(); } catch { } return Norm(p) == wantPath; });
            if (already == targets.Count)
            {
                res.AlreadyReplaced = already; res.Replaced = already; res.Verified = true;
                res.Info = "All " + targets.Count + " target(s) already reference " + Path.GetFileName(path) + " — nothing to change.";
                res.Diag = "alreadyReplaced=" + already;
                await emit("Gauge", null, "done", "already on " + Path.GetFileName(path));
                return res;
            }

            // ---- pre-load the replacement part (ReplaceComponents needs it in memory, like AddComponent5) ----
            int oe = 0, ow = 0;
            try { app.OpenDoc6(path, (int)swDocumentTypes_e.swDocPART, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref oe, ref ow); } catch { }
            try { model.ClearSelection2(true); } catch { }

            // select every target instance
            int sel = 0;
            foreach (var c in targets) { try { if (c.Select4(sel > 0, null, false)) sel++; } catch { } }

            await emit("Gauge", null, "done", "swapping " + targets.Count + " instance(s) for " + Path.GetFileName(path));
            await emit("Scribe", "calling ReplaceComponents", "run", null);

            bool apiRet = false;
            try { apiRet = asm.ReplaceComponents(path, "", true, true); }
            catch (Exception ex) { res.Error = "ReplaceComponents threw (" + ex.GetType().Name + ") — the assembly is unchanged."; await emit("Scribe", null, "fail", res.Error); return res; }
            res.ApiReturn = apiRet;
            try { model.ClearSelection2(true); } catch { }
            try { model.EditRebuild3(); } catch { try { model.ForceRebuild3(false); } catch { } }
            res.RebuildErrors = SafeWhatsWrong(model);

            // ---- Sentinel: FAIL CLOSED — count instances now referencing the replacement file (independent read) ----
            await emit("Sentinel", "verifying instances now reference the replacement file", "run", null);
            int onNew = 0, total = 0;
            string want = Norm(path);
            foreach (var o in (asm.GetComponents(true) as object[]) ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                total++;
                string p = null; try { p = c.GetPathName(); } catch { }
                if (Norm(p) == want) onNew++;
            }
            res.Replaced = onNew;
            res.Failed = res.Matched - onNew;
            res.Verified = onNew >= res.Matched && res.RebuildErrors == 0;
            res.Diag = "apiRet=" + apiRet + " matched=" + res.Matched + " onNew=" + onNew + " total=" + total + " rebuildErr=" + res.RebuildErrors;

            if (!res.Verified)
            {
                res.Error = "Replace did not take (" + res.Diag + "). ReplaceComponents likely no-ops headless on this build.";
                await emit("Sentinel", null, "fail", res.Error);
                return res;
            }

            res.Info = "Replaced " + res.Replaced + " instance(s) with " + Path.GetFileName(path) + ", mates re-attached. One Ctrl+Z restores it; Forge didn't save.";
            await emit("Sentinel", null, "done", res.Replaced + "/" + res.Matched + " now on " + Path.GetFileName(path) + " · clean");
            return res;
        }

        private static List<Component2> ResolveTargets(List<Component2> comps, string cmd, out string filter)
        {
            filter = "all";
            string kind = null;
            foreach (var kw in new[] { "bolt", "nut", "washer", "screw", "shaft", "gear", "housing", "base" })
                if (cmd.Contains(kw)) { kind = kw; break; }
            if (kind != null)
            {
                filter = kind;
                var byKind = new List<Component2>();
                foreach (var c in comps)
                {
                    string nm = null; try { nm = (c.Name2 ?? "").ToLowerInvariant(); } catch { }
                    if (nm != null && MatchesKind(nm, kind)) byKind.Add(c);
                }
                return byKind;
            }
            if (Regex.IsMatch(cmd, @"\b(everything|all|every)\b") || Regex.IsMatch(cmd, @"\b(component|components|part|parts)\b")) { filter = "all"; return comps; }
            return new List<Component2>();
        }

        private static bool MatchesKind(string nm, string kind)
        {
            if (kind == "bolt" || kind == "screw") { if (nm.Contains("nut") || nm.Contains("washer") || nm.Contains("plate")) return false; return nm.Contains("bolt") || nm.Contains("screw") || nm.Contains("hcs") || nm.Contains("hex"); }
            return nm.Contains(kind);
        }

        private static string Norm(string p) { return string.IsNullOrEmpty(p) ? "" : p.Trim().ToLowerInvariant().Replace('/', '\\'); }
        private static int SafeWhatsWrong(IModelDoc2 model) { try { return model.Extension.GetWhatsWrongCount(); } catch { return 0; } }
    }
}
