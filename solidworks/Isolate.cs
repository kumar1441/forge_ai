using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class IsolateResult
    {
        public int Kept;
        public int Hidden;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Isolate — hide everything except the components the engineer selected, so they can focus on one
    /// subsystem. "show all" brings everything back. Selection-based (works regardless of part naming).
    /// THROW #1: read selection two ways (GetSelectedObjectsComponent4 / GetSelectedObject6), hide the
    /// rest via HideComponent2. Instrumented so we can see which selection read works.
    /// </summary>
    public static class Isolator
    {
        public static bool IsIsolateIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            // quarantine_file (tool 257) owns "quarantine/isolate ... file(s)/document(s)" batch-processing
            // vocabulary — this tool isolates ASSEMBLY COMPONENTS in the graphics area, never files/documents.
            if (System.Text.RegularExpressions.Regex.IsMatch(cmd, @"\bquarantine\b") ||
                (System.Text.RegularExpressions.Regex.IsMatch(cmd, @"\bisolate\b") &&
                 System.Text.RegularExpressions.Regex.IsMatch(cmd, @"\b(file|files|document|documents)\b")))
                return false;
            return System.Text.RegularExpressions.Regex.IsMatch(
                cmd, @"\b(isolate|show only|just show|hide the rest|focus on|show all|show everything|unhide)\b");
        }

        private static bool IsShowAll(string cmd) =>
            System.Text.RegularExpressions.Regex.IsMatch(cmd, @"\b(show all|show everything|unhide)\b");

        // Largest top-level component by bounding-box volume — the deterministic "biggest part" target.
        private static Component2 PickLargest(AssemblyDoc asm)
        {
            object[] top = asm.GetComponents(true) as object[];
            Component2 best = null; double bestVol = -1;
            foreach (var o in top ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                double v = Vol(c); if (v > bestVol) { bestVol = v; best = c; }
            }
            return best;
        }
        private static double Vol(Component2 c)
        {
            try { double[] b = c.GetBox(false, false) as double[]; if (b == null || b.Length < 6) return 0; return Math.Abs((b[3] - b[0]) * (b[4] - b[1]) * (b[5] - b[2])); }
            catch { return 0; }
        }

        public static async Task<IsolateResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new IsolateResult();
            var asm = model as AssemblyDoc;
            if (asm == null) { res.Error = "Open an assembly to isolate parts."; return res; }
            string cmd = (intent ?? "").ToLowerInvariant();

            // ---- show all: bring everything back ----
            if (IsShowAll(cmd))
            {
                await emit("Gauge", "restoring visibility", "run", null);
                object[] all = asm.GetComponents(false) as object[];
                model.ClearSelection2(true);
                int shown = 0;
                foreach (var o in all ?? new object[0])
                {
                    var c = o as Component2;
                    if (c == null || c.IsSuppressed()) continue;
                    try { if (c.Select4(true, null, false)) shown++; } catch { }
                }
                try { model.ShowComponent2(); } catch { }
                model.ClearSelection2(true);
                await emit("Gauge", null, "done", "everything visible again");
                res.Info = "Showed all components.";
                return res;
            }

            // ---- isolate: keep the selection, hide the rest ----
            await emit("Gauge", "reading your selection", "run", null);
            var sm = (SelectionMgr)model.SelectionManager;
            int cnt = 0;
            try { cnt = sm.GetSelectedObjectCount2(-1); } catch { }
            var keep = new HashSet<string>();
            int w1 = 0, w2 = 0;
            for (int i = 1; i <= cnt; i++)
            {
                Component2 c = null;
                try { c = sm.GetSelectedObjectsComponent4(i, -1) as Component2; if (c != null) w1++; } catch { }
                if (c == null) { try { c = sm.GetSelectedObject6(i, -1) as Component2; if (c != null) w2++; } catch { } }
                if (c != null && c.Name2 != null) keep.Add(c.Name2);
            }
            await emit("Gauge", null, "done", "selected=" + cnt + " keep=" + keep.Count + " (w1=" + w1 + " w2=" + w2 + ")");

            // No selection? Accept a natural-language target — "isolate the biggest part" — and pick it ourselves.
            if (keep.Count == 0 && System.Text.RegularExpressions.Regex.IsMatch(cmd, @"\b(biggest|largest|main body|main part)\b"))
            {
                var big = PickLargest(asm);
                if (big != null && big.Name2 != null) { keep.Add(big.Name2); await emit("Gauge", null, "done", "no selection — picked largest component: " + big.Name2); }
            }
            if (keep.Count == 0) { res.Error = "Select the part(s) you want to keep in SolidWorks first (or say \"isolate the biggest part\")."; return res; }

            await emit("Gauge", "hiding everything else", "run", null);
            object[] comps = asm.GetComponents(false) as object[];
            model.ClearSelection2(true);
            int toHide = 0;
            foreach (var o in comps ?? new object[0])
            {
                var c = o as Component2;
                if (c == null || c.IsSuppressed()) continue;
                if (keep.Contains(c.Name2)) { res.Kept++; continue; }
                try { if (c.Select4(true, null, false)) toHide++; } catch { }
            }
            try { model.HideComponent2(); } catch { }
            model.ClearSelection2(true);
            res.Hidden = toHide;
            await emit("Gauge", null, "done", "hid " + toHide + ", kept " + res.Kept);
            res.Info = "Isolated " + res.Kept + " part" + (res.Kept == 1 ? "" : "s") + " — hid " + toHide + ". Say \"show all\" to bring them back.";
            return res;
        }
    }
}
