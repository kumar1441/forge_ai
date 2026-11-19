using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class SetFixedResult
    {
        public string Action;            // "fix" | "float"
        public string TargetFilter;      // what was targeted
        public int Matched;
        public int Changed;              // components whose fixed-state actually flipped
        public int AlreadyInState;       // already fixed (for fix) / already floating (for float)
        public int Failed;
        public bool Verified;            // every changed component independently reads back in the requested state
        public bool NeedsConfirm;
        public string Question;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// SetFixed (tools #36/#37 fix_component / float_component) — WRITE: fix (lock in place) or float (free) components
    /// in the active assembly. "fix everything", "fix the base", "float all the parts", "unfix the housing", "free the
    /// bolts". A pure STATE change (no geometry), inherently undoable. Sets the state via AssemblyDoc.FixComponent /
    /// UnfixComponent on the selected components; the harness cross-checks with an INDEPENDENT Component2.IsFixed read.
    /// </summary>
    public static class SetFixed
    {
        public static bool IsSetFixedIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            cmd = System.Text.RegularExpressions.Regex.Replace(cmd, @"[^a-zA-Z0-9\s'-]", " ");
            return System.Text.RegularExpressions.Regex.IsMatch(cmd,
                @"\b(fix|lock|anchor|float|unfix|un-fix|free|release)\b.*\b(component|comp|part|parts|everything|all|it|assembly|base|housing|bolt|nut|flange|shaft|gear|vise|vice)\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                || System.Text.RegularExpressions.Regex.IsMatch(cmd, @"\b(fix|float|unfix|un-fix)\s+(everything|all|it)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        public static async Task<SetFixedResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new SetFixedResult();
            var asm = model as AssemblyDoc;
            if (asm == null) { res.Error = "Open the assembly (.SLDASM) to fix or float components."; return res; }

            // Mangled clipboard/OS punctuation (a smart em-dash mis-decoded as e.g. "floatâ€\"no") glues directly onto
            // the keyword with no space. .NET's \b treats those mojibake bytes as word characters (Unicode letters),
            // so "floatâ€\"no" has NO boundary after "t" and \bfloat\b silently fails to match — doFix then falls back
            // to its true default and FIXES the assembly instead of floating it, the exact opposite of the ask (test-loop
            // false-success float-vise). Blank out anything that isn't ASCII letters/digits/basic punctuation BEFORE
            // matching so a keyword immediately followed by garbage still gets a real word boundary.
            string cmd = System.Text.RegularExpressions.Regex.Replace((intent ?? "").ToLowerInvariant(), @"[^a-z0-9\s'-]", " ");
            bool doFix = !System.Text.RegularExpressions.Regex.IsMatch(cmd, @"\b(float|unfix|un-fix|free|release)\b");
            res.Action = doFix ? "fix" : "float";

            await emit("Anchor", "resolving what to " + res.Action, "run", null);

            // ---- resolve the target set from the live model ----
            object[] all = asm.GetComponents(true) as object[];   // top-level
            var comps = new List<Component2>();
            foreach (var o in all ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { }
                if (!sup) comps.Add(c);
            }

            var targets = ResolveTargets(comps, cmd);
            if (targets.Count == 0)
            {
                res.NeedsConfirm = true;
                res.Question = "I couldn't tell which components to " + res.Action + ". This assembly has " + comps.Count +
                               " top-level components — say 'fix everything', or name a kind (bolts / flanges) or a part.";
                await emit("Anchor", null, "ask", "no target matched");
                return res;
            }
            res.Matched = targets.Count;

            // ---- read the CURRENT fixed-state, select the ones that need flipping, apply in one batch ----
            try { model.ClearSelection2(true); } catch { }
            int toFlip = 0;
            foreach (var c in targets)
            {
                bool isFixed = false; try { isFixed = c.IsFixed(); } catch { }
                if (isFixed == doFix) { res.AlreadyInState++; continue; }   // already in the requested state
                try { if (c.Select4(true, null, false)) toFlip++; } catch { }
            }

            if (toFlip == 0)
            {
                res.Verified = true;   // nothing to do IS the verified correct state
                res.Info = "All " + targets.Count + " target component(s) are already " + (doFix ? "fixed" : "floating") + " — nothing to change.";
                await emit("Anchor", null, "done", "already " + (doFix ? "fixed" : "floating"));
                try { model.ClearSelection2(true); } catch { }
                return res;
            }

            await emit(doFix ? "Clamp" : "Release", (doFix ? "fixing " : "floating ") + toFlip + " component(s)", "run", null);
            try
            {
                if (doFix) asm.FixComponent();
                else asm.UnfixComponent();
            }
            catch (Exception ex) { res.Error = "SolidWorks refused to " + res.Action + " the components (" + ex.GetType().Name + ")."; try { model.ClearSelection2(true); } catch { } return res; }
            try { model.ClearSelection2(true); } catch { }
            try { model.EditRebuild3(); } catch { try { model.ForceRebuild3(false); } catch { } }

            // ---- FAIL CLOSED (Rule #6): independently re-read IsFixed on every target; count only the confirmed flips ----
            int confirmed = 0;
            await emit("Sentinel", "verifying the " + res.Action + " state", "run", null);
            foreach (var c in targets)
            {
                bool isFixed = false; try { isFixed = c.IsFixed(); } catch { }
                if (isFixed == doFix) confirmed++;
            }
            res.Changed = confirmed - res.AlreadyInState;
            if (res.Changed < 0) res.Changed = 0;
            res.Failed = toFlip - res.Changed;
            res.Verified = res.Failed == 0 && (confirmed == targets.Count);

            res.Info = (doFix ? "Fixed " : "Floated ") + res.Changed + " of " + targets.Count + " component(s)" +
                       (res.AlreadyInState > 0 ? " (" + res.AlreadyInState + " already " + (doFix ? "fixed" : "floating") + ")" : "") +
                       (res.Failed > 0 ? " · " + res.Failed + " didn't take" : "") +
                       ". Reversible: one Ctrl+Z, and the document was not saved.";
            await emit("Sentinel", null, res.Verified ? "done" : "fail", confirmed + "/" + targets.Count + " now " + (doFix ? "fixed" : "floating"));
            return res;
        }

        private static List<Component2> ResolveTargets(List<Component2> comps, string cmd)
        {
            var outl = new List<Component2>();
            bool all = System.Text.RegularExpressions.Regex.IsMatch(cmd, @"\b(everything|all|whole|entire|it|assembly)\b");
            string kind = null;
            foreach (var kw in new[] { "bolt", "nut", "washer", "flange", "shaft", "gear", "housing", "base" })
                if (cmd.Contains(kw)) { kind = kw; break; }

            foreach (var c in comps)
            {
                string nm = null; try { nm = (c.Name2 ?? "").ToLowerInvariant(); } catch { }
                if (all) { outl.Add(c); continue; }
                if (kind != null && nm != null && nm.Contains(kind)) outl.Add(c);
            }
            // "fix the base" with no match, or a bare "fix it" -> treat as all (single-target assemblies)
            if (outl.Count == 0 && (all || System.Text.RegularExpressions.Regex.IsMatch(cmd, @"\bfix|float|unfix\b"))) return comps;
            return outl;
        }
    }
}
