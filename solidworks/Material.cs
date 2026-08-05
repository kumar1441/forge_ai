using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class MaterialResult
    {
        public int Applied;
        public int Failed;
        public string MaterialName;
        public string Info;
        public string Error;
        public bool NeedsConfirm;   // ambiguity -> ask, do not execute (Rule #2)
        public string Question;     // the ONE short question to ask
        public string PlanLine;     // one-line plan when clear (Character #3)
    }

    /// <summary>
    /// Bulk material change - "make everything brass / aluminum / steel". Sets the material on every
    /// part (or the active part). Visual: parts take on the material's appearance.
    /// THROW #1: SetMaterialPropertyName2 with the standard "SOLIDWORKS Materials" database; if 0 apply,
    /// we throw other database names next.
    /// </summary>
    public static class Materializer
    {
        // spoken word -> SolidWorks material name (in the SOLIDWORKS Materials database). internal: MaterialLibrary
        // uses this as the CANONICAL default for a bare, unqualified word (see MaterialLibrary.Resolve) so a plain
        // "steel" resolves straight to AISI 1020 instead of asking the user to pick among every library alloy whose
        // name or category happens to fuzzy-match "steel".
        internal static readonly Dictionary<string, string> Mat = new Dictionary<string, string>
        {
            { "aluminium", "6061 Alloy" }, { "aluminum", "6061 Alloy" },
            { "stainless", "AISI 304" }, { "steel", "AISI 1020" },
            { "brass", "Brass" }, { "copper", "Copper" }, { "bronze", "Tin Bearing Bronze" },
            { "titanium", "Titanium" }, { "cast iron", "Gray Cast Iron" }, { "iron", "Gray Cast Iron" },
            { "abs", "ABS" }, { "nylon", "Nylon 101" }, { "acrylic", "Acrylic (Medium-high impact)" },
            { "fiberglass", "E-Glass Fiber" }, { "fibreglass", "E-Glass Fiber" }, { "rubber", "Rubber" },
        };

        public static bool IsMaterialIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            if (cmd.Contains("material")) return true;
            foreach (var k in Mat.Keys) if (cmd.Contains(k)) return true;
            return false;
        }

        private static string Resolve(string cmd)
        {
            foreach (var kv in Mat) if (cmd.Contains(kv.Key)) return kv.Value;
            return null;
        }

        public static async Task<MaterialResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new MaterialResult();
            string cmd = (intent ?? "").ToLowerInvariant();
            string mat = Resolve(cmd);
            if (mat == null) { res.Error = "Which material? Try \"make everything brass\" (aluminum, steel, brass, copper, titanium...)."; return res; }
            res.MaterialName = mat;

            await emit("Ripple", "reading the parts", "run", null);
            var parts = new List<PartDoc>();
            if (model is AssemblyDoc asm)
            {
                object[] comps = asm.GetComponents(false) as object[];
                if (comps != null)
                    foreach (var o in comps)
                    {
                        var c = o as Component2;
                        if (c == null || c.IsSuppressed()) continue;
                        if (c.GetModelDoc2() is PartDoc pd && !parts.Contains(pd)) parts.Add(pd);
                    }
            }
            else if (model is PartDoc p) parts.Add(p);

            await emit("Ripple", null, "done", "found " + parts.Count + " part" + (parts.Count == 1 ? "" : "s"));
            if (parts.Count == 0) { res.Error = "No parts to change."; return res; }

            await emit("Ripple", "applying " + mat, "run", null);
            int cleared = 0;
            foreach (var pd in parts)
            {
                try
                {
                    // Clear any manual appearance/color override so the material's OWN color shows in the viewport.
                    // Many imported/purchased parts carry a custom color that otherwise hides the new material.
                    try { if ((pd as IModelDoc2)?.Extension?.RemoveMaterialProperty((int)swInConfigurationOpts_e.swAllConfiguration, null) == true) cleared++; } catch { }
                    pd.SetMaterialPropertyName2("", "SOLIDWORKS Materials", mat); res.Applied++;
                }
                catch { res.Failed++; }
            }
            model.ForceRebuild3(false);
            try { model.GraphicsRedraw2(); } catch { }
            await emit("Ripple", null, "done", res.Applied + " part" + (res.Applied == 1 ? "" : "s") + " set to " + mat +
                (cleared > 0 ? " | cleared " + cleared + " color override" + (cleared == 1 ? "" : "s") : "") +
                (res.Failed > 0 ? ", " + res.Failed + " failed" : ""));

            res.Info = "Set " + res.Applied + " part" + (res.Applied == 1 ? "" : "s") + " to " + mat +
                (cleared > 0 ? " and cleared " + cleared + " custom-color override" + (cleared == 1 ? "" : "s") + " so the " + mat + " color shows" : "") + ".";
            return res;
        }

        // ---- INTENT-DRIVEN executor: consumes the parsed plan. Resolves targets + materials against the LIVE model
        //      and library, asks ONE question on any ambiguity (no execution), else shows a one-line plan, executes
        //      per-part, and VERIFIES by reading the material back. Supports DIFFERENT materials per part. ----
        public static async Task<MaterialResult> RunIntent(ISldWorks app, IModelDoc2 model, IntentPlan plan, Func<string, string, string, string, Task> emit)
        {
            var res = new MaterialResult();
            var asm = model as AssemblyDoc;
            var matOps = plan.Operations.Where(o => o.Action == "set_material").ToList();
            if (matOps.Count == 0) { res.Error = "That wasn't a material change."; return res; }

            await emit("Ripple", "working out the plan", "run", null);
            var assignments = new List<KeyValuePair<List<PartDoc>, MaterialMatch>>();
            // ACT, DON'T HEDGE (Rule #2, same doctrine as ForgePanel.Pipeline.cs RunViaPipeline): plan.Ambiguities is
            // the cloud parser's own soft commentary (typo notes, "is this a part name?" musings) and is too eager to
            // flag non-issues (test-loop hedge curveball-rubber-material: conf=0.92, material resolved cleanly, yet a
            // typo-and-single-part-doc "ambiguity" note blocked it anyway). Only ask when THIS handler's own target/
            // material resolution genuinely fails below - never seed questions from the parser's free-text notes.
            var questions = new List<string>();
            var singlePart = asm == null ? model as PartDoc : null;
            foreach (var op in matOps)
            {
                var parts = new List<PartDoc>();
                if (asm != null)
                    foreach (var t in op.Targets)
                    {
                        string note; var hit = IntentLayer.ResolveTarget(asm, t, out note);
                        if (hit.Count == 0 && note != null) questions.Add(note);
                        foreach (var c in hit)
                        {
                            var pd = c.GetModelDoc2() as PartDoc;
                            if (pd != null && !parts.Contains(pd)) parts.Add(pd);
                        }
                    }
                else if (singlePart != null) parts.Add(singlePart); // single-part doc: the whole doc IS the target, no sub-component to resolve
                var mm = MaterialLibrary.Resolve(app, op.Material);
                if (mm.Ambiguous) { questions.Add(mm.Note + (mm.Options.Count > 0 ? " - options: " + string.Join(", ", mm.Options) : "")); continue; }
                if (parts.Count == 0) { questions.Add("couldn't find what to set to " + (op.Material ?? "that")); continue; }
                assignments.Add(new KeyValuePair<List<PartDoc>, MaterialMatch>(parts, mm));
            }

            if (questions.Count > 0 || plan.Confidence < 0.45)
            {
                res.NeedsConfirm = true; res.Question = questions.Count > 0 ? questions[0] : "I'm not sure what you meant - can you rephrase?";
                await emit("Ripple", null, "ask", res.Question);
                return res;
            }
            if (assignments.Count == 0) { res.Error = "Nothing to change."; return res; }

            res.PlanLine = string.Join(", ", assignments.Select(a => a.Value.Name + " -> " + a.Key.Count + " part" + (a.Key.Count == 1 ? "" : "s")));
            await emit("Ripple", null, "done", "plan: " + res.PlanLine);

            await emit("Ripple", "applying materials", "run", null);
            int applied = 0, cleared = 0;
            foreach (var a in assignments)
                foreach (var pd in a.Key)
                {
                    try
                    {
                        try { if ((pd as IModelDoc2)?.Extension?.RemoveMaterialProperty((int)swInConfigurationOpts_e.swAllConfiguration, null) == true) cleared++; } catch { }
                        pd.SetMaterialPropertyName2("", a.Value.Database ?? "SOLIDWORKS Materials", a.Value.Name); applied++;
                    }
                    catch { res.Failed++; }
                }
            model.ForceRebuild3(false); try { model.GraphicsRedraw2(); } catch { }
            res.Applied = applied;

            int verified = 0, total = 0;
            foreach (var a in assignments)
                foreach (var pd in a.Key)
                {
                    total++;
                    try { string db; if (string.Equals(pd.GetMaterialPropertyName2("", out db), a.Value.Name, StringComparison.OrdinalIgnoreCase)) verified++; } catch { }
                }
            res.Info = verified + " of " + total + " part" + (total == 1 ? "" : "s") + " set - " + res.PlanLine + (cleared > 0 ? " (cleared " + cleared + " color override" + (cleared == 1 ? "" : "s") + ")" : "") + ".";
            await emit("Sentinel", null, "done", verified + "/" + total + " verified by read-back");
            return res;
        }
    }
}
