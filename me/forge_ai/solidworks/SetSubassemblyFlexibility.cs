using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class SetSubassemblyFlexibilityResult
    {
        public string SubName;
        public int StateBefore, StateAfter;   // swComponentSolvingOption_e: 0=rigid, 1=flexible
        public int TargetState;
        public bool ApiReturn;      // raw RunCommand() return — instrumented (headless liveness unproven)
        public bool AlreadyDone;    // idempotency: already at the requested state
        public bool Verified;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 158 — set_subassembly_flexibility (WRITE). Toggles ONE sub-assembly between RIGID and FLEXIBLE solving.
    /// "make the crank shaft sub-assembly flexible" / "set the sub-assembly to rigid" / "toggle the sub-assembly's
    /// flexibility". IComponent2.Solving is EXHAUSTIVELY get-only — a full reflection sweep of sldworks.dll found no
    /// IComponent2/IAssemblyDoc member accepting a swComponentSolvingOption_e, so there is no direct property
    /// setter. The only automation route is swCommands_e.swCommands_SolveAsFlexibleOrRigid=3095 (from the separate
    /// SolidWorks.Interop.swcommands.dll redist, not swconst — hardcoded here as a plain int so no new project
    /// reference is needed) via ISldWorks.RunCommand — it is a TOGGLE bound to the current selection, not a
    /// directional setter, so it is only invoked when the current state already differs from the requested one
    /// (idempotent no-op otherwise). Verified by an INDEPENDENT get_Solving() re-read after the call, never by
    /// RunCommand's own boolean. Undoable (one Ctrl+Z); Forge never saves.
    /// </summary>
    public static class SetSubassemblyFlexibility
    {
        private const int CMD_SolveAsFlexibleOrRigid = 3095;

        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            // NARROW + specific-first: requires a flexib/rigid keyword WITH a sub-assembly noun, so plain
            // "dissolve the sub-assembly" (DissolveSubassembly) and "how many sub-assemblies" (ListSubassemblies)
            // are never shadowed — neither of THEIR verbs ever appears here.
            return Regex.IsMatch(c, @"\b(flex(ible|ibility)?|rigid)\b") &&
                   Regex.IsMatch(c, @"\bsub.?assembl(y|ies)\b");
        }

        public static async Task<SetSubassemblyFlexibilityResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new SetSubassemblyFlexibilityResult();
            var asm = model as AssemblyDoc;
            if (asm == null) { res.Error = "Open the assembly (.SLDASM) to set a sub-assembly's flexibility."; return res; }

            await emit("Gauge", "scanning the top level for sub-assemblies", "run", null);
            var subs = TopLevelSubassemblies(asm);
            if (subs.Count == 0) { res.Error = "No sub-assembly found at the top level — nothing to set flexible/rigid."; await emit("Gauge", null, "fail", "no sub-assembly"); return res; }

            // ---- resolve WHICH sub-assembly (same fragment-match idiom as DissolveSubassembly) ----
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
            try { res.StateBefore = target.Solving; } catch { }

            // ---- resolve the TARGET state: explicit flexible/rigid keyword, else toggle from the current state ----
            string cl = (intent ?? "").ToLowerInvariant();
            bool wantsFlexible = Regex.IsMatch(cl, @"\bflex(ible|ibility)?\b");
            bool wantsRigid = Regex.IsMatch(cl, @"\brigid\b");
            if (wantsFlexible && wantsRigid) { res.Error = "Both flexible and rigid mentioned — which one?"; await emit("Gauge", null, "fail", "ambiguous state"); return res; }
            res.TargetState = wantsFlexible ? (int)swComponentSolvingOption_e.swComponentFlexibleSolving
                             : wantsRigid ? (int)swComponentSolvingOption_e.swComponentRigidSolving
                             : (res.StateBefore == (int)swComponentSolvingOption_e.swComponentFlexibleSolving
                                ? (int)swComponentSolvingOption_e.swComponentRigidSolving
                                : (int)swComponentSolvingOption_e.swComponentFlexibleSolving); // no keyword -> toggle
            string wantName = res.TargetState == (int)swComponentSolvingOption_e.swComponentFlexibleSolving ? "flexible" : "rigid";
            string haveName = res.StateBefore == (int)swComponentSolvingOption_e.swComponentFlexibleSolving ? "flexible" : "rigid";
            await emit("Gauge", "'" + res.SubName + "' is currently " + haveName + ", target " + wantName, "done", null);

            // ---- idempotency: already at the target state ----
            if (res.StateBefore == res.TargetState)
            {
                res.AlreadyDone = true; res.Verified = true; res.StateAfter = res.StateBefore;
                res.Info = "'" + res.SubName + "' is already " + wantName + " — unchanged.";
                await emit("Sentinel", null, "done", "already " + wantName);
                return res;
            }

            // ---- Scribe: select the sub-assembly and toggle via RunCommand (3095 is a TOGGLE, not directional) ----
            await emit("Scribe", "setting '" + res.SubName + "' to " + wantName, "run", null);
            model.ClearSelection2(true);
            bool sel = false; try { sel = target.Select4(false, null, false); } catch { }
            if (!sel) { model.ClearSelection2(true); res.Error = "Couldn't select '" + res.SubName + "' — unchanged."; await emit("Scribe", null, "fail", "selection"); return res; }
            try { res.ApiReturn = app.RunCommand(CMD_SolveAsFlexibleOrRigid, ""); }
            catch (Exception ex) { model.ClearSelection2(true); res.Error = "RunCommand(SolveAsFlexibleOrRigid) threw (" + ex.GetType().Name + ") — unchanged."; await emit("Scribe", null, "fail", res.Error); return res; }
            model.ClearSelection2(true);
            try { model.ForceRebuild3(false); } catch { }

            // ---- Sentinel: INDEPENDENT re-read of Solving, fail closed (never trust ApiReturn) ----
            await emit("Sentinel", "verifying the flexibility change", "run", null);
            try { res.StateAfter = target.Solving; } catch { }
            res.Verified = res.StateAfter == res.TargetState;
            if (!res.Verified)
            {
                string stillName = res.StateAfter == (int)swComponentSolvingOption_e.swComponentFlexibleSolving ? "flexible" : "rigid";
                res.Error = "Flexibility change didn't take ('" + res.SubName + "' still " + stillName + ", RunCommand()=" + res.ApiReturn + ") — unchanged.";
                await emit("Sentinel", null, "fail", res.Error);
                return res;
            }

            res.Info = "'" + res.SubName + "' set to " + wantName + " (was " + haveName + "). One Ctrl+Z restores it; Forge didn't save.";
            await emit("Sentinel", null, "done", "now " + wantName);
            return res;
        }

        // top-level components whose referenced document is itself an assembly (skips suppressed) — same idiom as
        // DissolveSubassembly.TopLevelSubassemblies, duplicated (not shared) per this codebase's per-tool convention.
        private static List<Component2> TopLevelSubassemblies(AssemblyDoc asm)
        {
            var subs = new List<Component2>();
            foreach (var o in (asm.GetComponents(true) as object[]) ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                var pd = c.GetModelDoc2() as IModelDoc2;
                bool isAsm = false; try { isAsm = pd != null && (int)pd.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY; } catch { }
                if (isAsm) subs.Add(c);
            }
            return subs;
        }

        // "make the crank shaft sub-assembly flexible" -> "crank shaft" (strip verbs, articles, state words, noun/filler)
        private static string TargetFragment(string intent)
        {
            if (string.IsNullOrEmpty(intent)) return null;
            string s = intent.ToLowerInvariant();
            s = Regex.Replace(s, @"\b(make|set|toggle|change|switch|the|a|an|please|to|as|sub.?assembl(y|ies)|component|flex(ible|ibility)?|rigid)\b", " ", RegexOptions.IgnoreCase);
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
