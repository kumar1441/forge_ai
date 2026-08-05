using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public class BatchUpdateMaterialsResult
    {
        public string Kind;          // "bolt" | "nut" | "washer" | "flange"
        public string MaterialName;
        public int Applied;          // unique parts changed
        public int Failed;
        public int Skipped;          // matched but already at the target material
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 141 — batch_update_materials (WRITE, "filter -> material"). "Set all the bolts to steel" — unlike
    /// Materializer (set_material, "make everything brass"), this scopes the change to ONE component KIND so the rest
    /// of the assembly is untouched. Same proven route as set_material (PartDoc.SetMaterialPropertyName2), applied
    /// once per UNIQUE part file among the matched, non-suppressed components (matching every instance of that part
    /// automatically — a shared fastener file only needs one write). Verified by an INDEPENDENT per-part read-back
    /// (fail closed); idempotent (a part already at the target material is skipped, not re-applied); undoable via one
    /// material-property clear per changed part; Forge never saves.
    /// </summary>
    public static class BatchUpdateMaterials
    {
        private static readonly Dictionary<string, string[]> KindWords = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["bolt"] = new[] { "bolt", "screw", "hcs", "shcs", "cap", "socket", "hex", "stud", "b18", "din", "iso" },
            ["nut"] = new[] { "nut", "ecrou" },
            ["washer"] = new[] { "washer", "rondelle" },
            ["flange"] = new[] { "flange", "plate" },
        };

        private static readonly Dictionary<string, string> Mat = new Dictionary<string, string>
        {
            { "aluminium", "6061 Alloy" }, { "aluminum", "6061 Alloy" },
            { "stainless", "AISI 304" }, { "steel", "AISI 1020" },
            { "brass", "Brass" }, { "copper", "Copper" }, { "bronze", "Tin Bearing Bronze" },
            { "titanium", "Titanium" }, { "cast iron", "Gray Cast Iron" }, { "iron", "Gray Cast Iron" },
            { "abs", "ABS" }, { "nylon", "Nylon 101" }, { "acrylic", "Acrylic (Medium-high impact)" },
        };

        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            bool hasKind = Regex.IsMatch(c, @"\b(bolts?|nuts?|washers?|screws?|flanges?)\b");
            bool hasVerb = Regex.IsMatch(c, @"\b(set|change|make|apply|assign|switch)\b");
            bool hasMat = c.Contains("material");
            foreach (var k in Mat.Keys) if (c.Contains(k)) { hasMat = true; break; }
            return hasKind && hasVerb && hasMat;
        }

        public static async Task<BatchUpdateMaterialsResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new BatchUpdateMaterialsResult();
            var asm = model as AssemblyDoc;
            if (asm == null) { res.Error = "Open the assembly (.SLDASM) to batch-update materials by kind."; return res; }

            string c = (intent ?? "").ToLowerInvariant();
            string kind = null; string[] tokens = null;
            foreach (var kv in KindWords) if (Regex.IsMatch(c, @"\b" + kv.Key + @"s?\b")) { kind = kv.Key; tokens = kv.Value; break; }
            if (kind == null) { res.Error = "Which kind? e.g. \"set all the bolts to steel\" (bolt/nut/washer/flange)."; return res; }
            res.Kind = kind;

            string mat = null;
            foreach (var kv in Mat) if (c.Contains(kv.Key)) { mat = kv.Value; break; }
            if (mat == null) { res.Error = "Which material? Try steel, stainless, aluminum, brass, copper, titanium, cast iron, ABS, nylon, acrylic."; return res; }
            res.MaterialName = mat;

            await emit("Filter", "resolving '" + kind + "' components", "run", null);
            object[] comps = asm.GetComponents(false) as object[];
            var parts = new List<PartDoc>();
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var o in comps ?? new object[0])
            {
                var comp = o as Component2; if (comp == null) continue;
                bool sup = false; try { sup = comp.IsSuppressed(); } catch { } if (sup) continue;
                string nm = null; try { nm = comp.Name2; } catch { } if (nm == null) continue;
                string low = nm.ToLowerInvariant();
                if (kind == "bolt" && (low.Contains("nut") || low.Contains("washer"))) continue;
                bool hit = false; foreach (var t in tokens) if (low.Contains(t)) { hit = true; break; }
                if (!hit) continue;
                string path = null; try { path = comp.GetPathName(); } catch { }
                if (string.IsNullOrEmpty(path) || !seenPaths.Add(path)) continue;
                var pd = comp.GetModelDoc2() as PartDoc; if (pd == null) continue;
                parts.Add(pd);
            }
            await emit("Filter", null, "done", parts.Count + " unique part(s) match '" + kind + "'");
            if (parts.Count == 0) { res.Error = "No " + kind + " components found to update."; return res; }

            await emit("Ripple", "applying " + mat + " to " + kind + " part(s)", "run", null);
            foreach (var pd in parts)
            {
                string before = null; try { before = pd.GetMaterialPropertyName2("", out _); } catch { }
                if (!string.IsNullOrEmpty(before) && string.Equals(before, mat, StringComparison.OrdinalIgnoreCase)) { res.Skipped++; continue; }
                try { pd.SetMaterialPropertyName2("", "SOLIDWORKS Materials", mat); }
                catch { res.Failed++; continue; }
                string after = null; try { after = pd.GetMaterialPropertyName2("", out _); } catch { }
                if (string.Equals(after, mat, StringComparison.OrdinalIgnoreCase)) res.Applied++; else res.Failed++;
            }
            model.ForceRebuild3(false);
            try { model.GraphicsRedraw2(); } catch { }

            if (res.Failed > 0)
            { res.Error = res.Applied + " applied, " + res.Failed + " failed to verify — some " + kind + " part(s) didn't take " + mat + "."; await emit("Ripple", null, "fail", res.Error); return res; }

            await emit("Ripple", null, "done", res.Applied + " " + kind + " part(s) set to " + mat + (res.Skipped > 0 ? " (" + res.Skipped + " already were)" : ""));
            res.Info = res.Applied == 0
                ? "All " + res.Skipped + " " + kind + " part(s) were already " + mat + " — nothing to change."
                : "Set " + res.Applied + " " + kind + " part" + (res.Applied == 1 ? "" : "s") + " to " + mat +
                  (res.Skipped > 0 ? " (" + res.Skipped + " already were)" : "") + ". Other components untouched. One Ctrl+Z undoes it; Forge didn't save.";
            return res;
        }
    }
}
