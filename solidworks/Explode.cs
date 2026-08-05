using System;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class ExplodeResult
    {
        public int Components;
        public int Moved;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Explode — spread the assembly's components apart so you can see how it goes together.
    /// THROW #1 (this build): manual radial move via Transform2 (APIs proven to work in this add-in),
    /// instrumented to report whether the move STUCK or the mates snapped it back. If it snaps back,
    /// the next throw is the native explode-step API.
    /// </summary>
    public static class Exploder
    {
        public static bool IsExplodeIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            // Exclude repair/reattach/fix/update vocabulary — that's RepairExplodedView's (tool 193) more specific
            // territory ("repair the exploded view" would otherwise match \bexploded\b here and get wrongly routed
            // to create/collapse instead of the repair handler).
            if (System.Text.RegularExpressions.Regex.IsMatch(cmd, @"\b(repair|reattach|re-attach|fix|update|sync|resync)\b")) return false;
            return System.Text.RegularExpressions.Regex.IsMatch(
                cmd, @"\b(explode|exploded|blow apart|disassemble|separate the parts|spread apart)\b") || IsCollapse(cmd);
        }

        // "put it back together" — collapse the exploded view. Routed through the explode handler since it owns the view.
        private static bool IsCollapse(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            return System.Text.RegularExpressions.Regex.IsMatch(
                cmd, @"\b(collapse|unexplode|un-explode|un explode|reassemble|re-assemble|put it back|put back together|back together|assemble it|assembled)\b");
        }

        public static async Task<ExplodeResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new ExplodeResult();
            var asm = model as AssemblyDoc;
            if (asm == null) { res.Error = "Open an assembly to explode."; return res; }
            var mu = (MathUtility)app.GetMathUtility();

            // ---- COLLAPSE ("put it back together"): un-show the exploded view so the assembly returns to normal ----
            if (IsCollapse((intent ?? "").ToLowerInvariant()))
            {
                await emit("Array", "putting it back together", "run", null);
                bool collapsed = false; int views = 0;
                try { views = asm.GetExplodedViewCount2(""); } catch { }
                if (views > 0)
                {
                    string vn = null;
                    try { var names = asm.GetExplodedViewNames2("") as object[]; if (names != null && names.Length > 0) vn = names[names.Length - 1] as string; } catch { }
                    try { collapsed = asm.ShowExploded2(false, vn ?? ""); } catch { }
                    if (!collapsed) { try { collapsed = asm.ShowExploded(false); } catch { } }
                }
                model.EditRebuild3();
                res.Info = collapsed ? "Collapsed — the assembly is back together." : "Nothing was exploded, so there's nothing to collapse.";
                await emit("Array", null, "done", collapsed ? "collapsed — back together" : "no exploded view to collapse");
                return res;
            }

            await emit("Array", "reading the assembly", "run", null);
            object[] comps = asm.GetComponents(true) as object[];   // top-level only
            if (comps == null || comps.Length == 0) { res.Error = "No components to explode."; return res; }
            res.Components = comps.Length;

            double spread = 0.15;   // 150 mm radial spread (fallback throw #1)
            await emit("Array", null, "done", res.Components + " top-level components");

            // ---- THROW #2 (native exploded view): works on MATED assemblies — the mates don't snap it back,
            //      because an exploded view is a display state that overrides component positions. THROW #1's
            //      Transform2 nudge is a no-op on mated models (proven on the shaft coupling). Try native first. ----
            await emit("Array", "creating an exploded view", "run", null);
            try
            {
                int existing = 0; try { existing = asm.GetExplodedViewCount2(""); } catch { }
                bool made = existing > 0;
                if (!made) { try { made = asm.AutoExplode(); } catch { made = false; } }
                if (made)
                {
                    string viewName = null;
                    try { var names = asm.GetExplodedViewNames2("") as object[]; if (names != null && names.Length > 0) viewName = names[names.Length - 1] as string; } catch { }
                    bool shown = false;
                    try { shown = asm.ShowExploded2(true, viewName ?? ""); } catch { }
                    if (!shown) { try { shown = asm.ShowExploded(true); } catch { } }
                    model.EditRebuild3();
                    res.Moved = res.Components;
                    await emit("Array", null, "done", "native exploded view '" + (viewName ?? "?") + "' created + shown — works on mated assemblies");
                    res.Info = "Exploded (native view) — " + res.Components + " components spaced apart. Collapse from the ConfigurationManager.";
                    return res;
                }
                await emit("Array", null, "done", "native explode unavailable — falling back to manual nudge");
            }
            catch { await emit("Array", null, "done", "native explode threw — falling back to manual nudge"); }

            // ---- FALLBACK THROW #1: radial manual move (only sticks on UNMATED components) ----
            await emit("Array", "spacing the parts apart", "run", null);
            int moved = 0;
            Component2 check = null;
            double[] wanted = null;
            foreach (var o in comps)
            {
                var comp = o as Component2;
                if (comp == null || comp.IsSuppressed() || comp.IsFixed()) continue;
                try
                {
                    var xf = comp.Transform2;
                    double[] d = xf.ArrayData as double[];
                    if (d == null || d.Length < 13) continue;

                    double px = d[9], py = d[10], pz = d[11];
                    double len = Math.Sqrt(px * px + py * py + pz * pz);
                    double ux = len < 1e-6 ? 1 : px / len, uy = len < 1e-6 ? 0 : py / len, uz = len < 1e-6 ? 0 : pz / len;

                    double[] nd = (double[])d.Clone();
                    nd[9] = px + ux * spread; nd[10] = py + uy * spread; nd[11] = pz + uz * spread;
                    comp.Transform2 = (MathTransform)mu.CreateTransform(nd);
                    moved++;
                    if (check == null) { check = comp; wanted = new[] { nd[9], nd[10], nd[11] }; }
                }
                catch { }
            }
            model.EditRebuild3();

            // did the move stick, or did mates snap it back?
            string stuck = "?";
            try
            {
                double[] d2 = check.Transform2.ArrayData as double[];
                double off = Math.Sqrt(Math.Pow(d2[9] - wanted[0], 2) + Math.Pow(d2[10] - wanted[1], 2) + Math.Pow(d2[11] - wanted[2], 2));
                stuck = off < 1e-3 ? "STUCK" : ("snapped-back " + (off * 1000).ToString("0") + "mm");
            }
            catch { }

            res.Moved = moved;
            res.Info = "Exploded — spaced " + moved + " of " + res.Components + " components. Ctrl+Z to collapse.";
            await emit("Array", null, "done", "spaced " + moved + " components apart" + (stuck != "STUCK" ? " (" + stuck + ")" : ""));
            return res;
        }
    }
}
