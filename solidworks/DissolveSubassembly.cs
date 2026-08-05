using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class DissolveSubassemblyResult
    {
        public string SubName;
        public int TopBefore, TopAfter;
        public int SubsBefore, SubsAfter;
        public int ChildrenPromoted;
        public bool ApiReturn;      // raw AssemblyDoc.DissolveSubAssembly() return — instrumented (headless liveness unproven)
        public bool AlreadyDone;    // idempotency: no sub-assembly left to dissolve (already flat / already dissolved)
        public bool Verified;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 40 — dissolve_subassembly (WRITE). Flattens ONE sub-assembly, promoting its children to the parent's top
    /// level. "dissolve the crank shaft sub-assembly" / "dissolve the sub-assembly". Resolves the target from the live
    /// tree (a component whose referenced doc is itself an assembly); ONE question on 0/ambiguous (Rule #2). Selects the
    /// sub-assembly component and calls AssemblyDoc.DissolveSubAssembly() (operates on the current selection), then
    /// verifies by an INDEPENDENT top-level recount: the sub-assembly count fell by exactly 1 and the top-level count
    /// rose (children promoted). Naturally idempotent — a rerun finds no matching sub-assembly and does nothing.
    /// Undoable (one Ctrl+Z); Forge never saves.
    /// INSTRUMENT-FIRST: many assembly writes are silent no-ops on this build, so success is judged by the independent
    /// recount, never by the DissolveSubAssembly() return (which is captured in ApiReturn for diagnosis only).
    /// </summary>
    public static class DissolveSubassembly
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            // NARROW + specific-first: the distinctive verb "dissolve"/"flatten"/"ungroup"/"break out" WITH a
            // sub-assembly noun. Excludes "pattern" so it never collides with a (future) dissolve_pattern, and the
            // sub-assembly requirement keeps it clear of list_subassemblies (which needs "how many/count/list/is this").
            return Regex.IsMatch(c, @"\b(dissolve|flatten|ungroup|break\s*out)\b") &&
                   Regex.IsMatch(c, @"\bsub.?assembl(y|ies)\b") &&
                   !Regex.IsMatch(c, @"\bpattern\b");
        }

        public static async Task<DissolveSubassemblyResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new DissolveSubassemblyResult();
            var asm = model as AssemblyDoc;
            if (asm == null) { res.Error = "Open the assembly (.SLDASM) to dissolve a sub-assembly."; return res; }

            await emit("Gauge", "scanning the top level for sub-assemblies", "run", null);
            var subs = TopLevelSubassemblies(asm, out int topLevel);
            res.TopBefore = topLevel; res.SubsBefore = subs.Count;

            // ---- idempotency: nothing left to dissolve ----
            if (subs.Count == 0)
            {
                res.AlreadyDone = true; res.Verified = true;
                res.TopAfter = topLevel; res.SubsAfter = 0;
                res.Info = "No sub-assembly to dissolve — this is already a flat assembly.";
                await emit("Sentinel", null, "done", "already flat — nothing to dissolve");
                return res;
            }

            // ---- resolve WHICH sub-assembly ----
            string frag = TargetFragment(intent);
            Component2 target = null;
            if (!string.IsNullOrEmpty(frag))
            {
                var hits = new List<Component2>();
                foreach (var c in subs) { string n = null; try { n = c.Name2; } catch { } if (n != null && n.IndexOf(frag, StringComparison.OrdinalIgnoreCase) >= 0) hits.Add(c); }
                if (hits.Count == 1) target = hits[0];
                else if (hits.Count == 0 && subs.Count == 1) target = subs[0];   // named filler didn't match, but there's only one anyway
                else if (hits.Count == 0) { res.Error = "No sub-assembly matches \"" + frag + "\" — which one? (" + NameList(subs) + ")"; await emit("Gauge", null, "fail", "no match"); return res; }
                else { res.Error = hits.Count + " sub-assemblies match \"" + frag + "\" — which one? (" + NameList(hits) + ")"; await emit("Gauge", null, "fail", "ambiguous"); return res; }
            }
            else if (subs.Count == 1) target = subs[0];
            else { res.Error = "Which sub-assembly? (" + NameList(subs) + ")"; await emit("Gauge", null, "fail", "need a target"); return res; }

            try { res.SubName = target.Name2; } catch { }
            res.ChildrenPromoted = CountChildren(target);
            await emit("Gauge", "dissolving '" + res.SubName + "' (" + res.ChildrenPromoted + " children)", "done", null);

            // ---- Scribe: select the sub-assembly and dissolve it ----
            await emit("Scribe", "flattening the sub-assembly", "run", null);
            model.ClearSelection2(true);
            bool sel = false; try { sel = target.Select4(false, null, false); } catch { }
            if (!sel) { model.ClearSelection2(true); res.Error = "Couldn't select '" + res.SubName + "' — unchanged."; await emit("Scribe", null, "fail", "selection"); return res; }
            try { res.ApiReturn = asm.DissolveSubAssembly(); }
            catch (Exception ex) { model.ClearSelection2(true); res.Error = "DissolveSubAssembly threw (" + ex.GetType().Name + ") — unchanged."; await emit("Scribe", null, "fail", res.Error); return res; }
            model.ClearSelection2(true);
            try { model.ForceRebuild3(false); } catch { }

            // ---- Sentinel: INDEPENDENT recount, fail closed (never trust ApiReturn) ----
            await emit("Sentinel", "verifying the flatten", "run", null);
            var subsAfter = TopLevelSubassemblies(asm, out int topAfter);
            res.TopAfter = topAfter; res.SubsAfter = subsAfter.Count;
            bool subGone = res.SubsAfter == res.SubsBefore - 1;
            bool promoted = res.TopAfter > res.TopBefore;   // children raised to the top level
            res.Verified = subGone && promoted;
            if (!res.Verified)
            {
                res.Error = "Dissolve didn't take (subs " + res.SubsBefore + "->" + res.SubsAfter + ", top " + res.TopBefore + "->" + res.TopAfter + ", DissolveSubAssembly()=" + res.ApiReturn + ") — the sub-assembly wasn't flattened.";
                await emit("Sentinel", null, "fail", res.Error);
                return res;
            }

            res.Info = "Dissolved '" + res.SubName + "' — " + res.ChildrenPromoted + " components promoted to the top level (" + res.TopBefore + " -> " + res.TopAfter + " top-level, " + res.SubsBefore + " -> " + res.SubsAfter + " sub-assemblies). One Ctrl+Z restores it; Forge didn't save.";
            await emit("Sentinel", null, "done", "dissolved (" + res.SubsBefore + " -> " + res.SubsAfter + " sub-assemblies)");
            return res;
        }

        // top-level components whose referenced document is itself an assembly (skips suppressed)
        private static List<Component2> TopLevelSubassemblies(AssemblyDoc asm, out int topLevel)
        {
            var subs = new List<Component2>(); topLevel = 0;
            foreach (var o in (asm.GetComponents(true) as object[]) ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                topLevel++;
                var pd = c.GetModelDoc2() as IModelDoc2;
                bool isAsm = false; try { isAsm = pd != null && (int)pd.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY; } catch { }
                if (isAsm) subs.Add(c);
            }
            return subs;
        }

        private static int CountChildren(Component2 sub)
        {
            int n = 0;
            try { foreach (var o in (sub.GetChildren() as object[]) ?? new object[0]) { if (o is Component2) n++; } } catch { }
            return n;
        }

        // "dissolve the crank shaft sub-assembly" -> "crank shaft" (strip verbs, articles, the sub-assembly noun/filler)
        private static string TargetFragment(string intent)
        {
            if (string.IsNullOrEmpty(intent)) return null;
            string s = intent.ToLowerInvariant();
            s = Regex.Replace(s, @"\b(dissolve|flatten|ungroup|break\s*out|the|a|an|please|sub.?assembl(y|ies)|component|into|parts?)\b", " ", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"[^a-z0-9 ]", " ").Trim();
            s = Regex.Replace(s, @"\s+", " ").Trim();
            return s.Length == 0 ? null : s;
        }

        private static string NameList(List<Component2> comps)
        {
            var ns = new List<string>();
            foreach (var c in comps) { try { ns.Add(c.Name2); } catch { } if (ns.Count >= 5) break; }
            return string.Join(", ", ns.ToArray());
        }
    }
}
