using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class RenameComponentResult
    {
        public string OldName;
        public string NewName;
        public bool Renamed;
        public bool AlreadyNamed;   // idempotency: the target already has the new name
        public int TotalComponents; // unchanged by a rename (proves nothing was added/lost)
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 38 — rename_component (WRITE, metadata). Renames a component INSTANCE in the tree (Component2.Name2), e.g.
    /// "rename the plate to BasePlate". Distinct from rename_feature (which renames a feature inside a part). Obeys the
    /// robustness rules: resolves the target from the LIVE tree, asks ONE question on 0/many matches (Rule #2), verifies
    /// by READING THE NAME BACK (fail closed), is idempotent (already the new name → nothing to do), and never saves.
    /// </summary>
    public static class RenameComponent
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            return Regex.IsMatch(c, @"\brename\b") &&
                   Regex.IsMatch(c, @"\b(component|part|instance|the|this)\b") &&
                   Regex.IsMatch(c, @"\bto\b") &&
                   !Regex.IsMatch(c, @"\b(feature|fillet|extrude|cut|hole|sketch|dimension|mate|config|configuration|configs|configurations|file)\b");   // those are rename_feature/dim/configuration/file-with-references
        }

        public static async Task<RenameComponentResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new RenameComponentResult();
            var asm = model as AssemblyDoc;
            if (asm == null) { res.Error = "Open the assembly (.SLDASM) to rename a component."; return res; }

            // parse "rename <old> to <new>"
            var m = Regex.Match(intent ?? "", @"rename\s+(?:the\s+|a\s+|component\s+|part\s+)*(.+?)\s+to\s+(.+)$", RegexOptions.IgnoreCase);
            if (!m.Success) { res.Error = "Tell me what to rename, e.g. \"rename the plate to BasePlate\"."; return res; }
            string oldFrag = m.Groups[1].Value.Trim().Trim('"', '\'');
            string newName = m.Groups[2].Value.Trim().Trim('"', '\'', '.', ' ');
            if (string.IsNullOrWhiteSpace(oldFrag) || string.IsNullOrWhiteSpace(newName))
            { res.Error = "I need both the current name and the new name, e.g. \"rename the plate to BasePlate\"."; return res; }
            res.NewName = newName;

            await emit("Gauge", "finding the component", "run", null);
            var comps = new List<Component2>();
            foreach (var o in (asm.GetComponents(true) as object[]) ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                res.TotalComponents++;
                comps.Add(c);
            }

            // idempotency: a component already named exactly newName?
            foreach (var c in comps)
            {
                string nm = null; try { nm = c.Name2; } catch { }
                if (string.Equals(nm, newName, StringComparison.OrdinalIgnoreCase))
                {
                    res.AlreadyNamed = true; res.Renamed = true; res.OldName = newName;
                    res.Info = "A component is already named '" + newName + "' — nothing to do.";
                    await emit("Scribe", null, "done", "already named '" + newName + "'");
                    return res;
                }
            }

            // resolve the target by name fragment (fail on 0 or many — Rule #2)
            var matches = new List<Component2>();
            foreach (var c in comps)
            {
                string nm = null; try { nm = c.Name2; } catch { }
                if (nm != null && nm.IndexOf(oldFrag, StringComparison.OrdinalIgnoreCase) >= 0) matches.Add(c);
            }
            if (matches.Count == 0) { res.Error = "No component matches '" + oldFrag + "'. Names in this assembly don't include that."; await emit("Gauge", null, "fail", "no match"); return res; }
            if (matches.Count > 1)
            {
                var names = new List<string>();
                foreach (var c in matches) { try { names.Add(c.Name2); } catch { } if (names.Count >= 5) break; }
                res.Error = "'" + oldFrag + "' matches " + matches.Count + " components (" + string.Join(", ", names.ToArray()) + "…). Which one?";
                await emit("Gauge", null, "fail", "ambiguous (" + matches.Count + ")");
                return res;
            }

            var target = matches[0];
            try { res.OldName = target.Name2; } catch { }
            await emit("Gauge", null, "done", "renaming '" + res.OldName + "' → '" + newName + "'");

            // ---- Scribe: set the instance name. Component2.Name2 can silently no-op headless on this build, so try
            //      several routes and confirm by read-back: (1) Name2 setter, (2) setter + ForceRebuild, (3) rename the
            //      component's tree FEATURE (a component instance is also a Feature). Stop at the first that sticks. ----
            await emit("Scribe", "applying the new name", "run", null);
            string diag = "";
            bool Took() { string bk = null; try { bk = target.Name2; } catch { } return string.Equals(bk, newName, StringComparison.OrdinalIgnoreCase); }

            try { target.Name2 = newName; } catch (Exception ex) { diag += "Name2:EX(" + ex.GetType().Name + ") "; }
            diag += "afterName2=" + (Took() ? "ok" : "no") + " ";
            if (!Took())
            {
                try { model.ForceRebuild3(false); } catch { }
                diag += "afterRebuild=" + (Took() ? "ok" : "no") + " ";
            }
            if (!Took())
            {
                // rename via the component's tree feature
                try
                {
                    var feat = target.FeatureByName(target.Name2) as Feature;   // the component's own feature
                    if (feat == null)
                    {
                        // walk the tree for the feature whose specific-feature IS this component
                        var f = model.FirstFeature() as Feature;
                        while (f != null)
                        {
                            try { if (ReferenceEquals(f.GetSpecificFeature2(), target)) { feat = f; break; } } catch { }
                            f = f.GetNextFeature() as Feature;
                        }
                    }
                    if (feat != null) { feat.Name = newName; diag += "feat.Name set "; }
                    else diag += "noFeat ";
                }
                catch (Exception ex) { diag += "featEX(" + ex.GetType().Name + ") "; }
                try { model.ForceRebuild3(false); } catch { }
                diag += "afterFeat=" + (Took() ? "ok" : "no") + " ";
            }
            await emit("Scribe", null, "done", "renamed via " + (diag.Contains("afterName2=ok") ? "Name2" : "tree feature"));

            // ---- Sentinel: verify by READING BACK (fail closed) ----
            await emit("Sentinel", "verifying the rename", "run", null);
            int totalAfter = 0; bool newPresent = false, oldGone = true;
            foreach (var o in (asm.GetComponents(true) as object[]) ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue; totalAfter++;
                string nm = null; try { nm = c.Name2; } catch { }
                if (string.Equals(nm, newName, StringComparison.OrdinalIgnoreCase)) newPresent = true;
                if (string.Equals(nm, res.OldName, StringComparison.OrdinalIgnoreCase)) oldGone = false;
            }
            res.Renamed = newPresent && oldGone && totalAfter == res.TotalComponents;
            if (!res.Renamed)
            {
                res.Error = !newPresent ? "The new name didn't take — component unchanged."
                          : !oldGone ? "The old name is still present — rename didn't apply cleanly."
                          : "Component count changed during the rename — check the assembly.";
                await emit("Sentinel", null, "fail", res.Error);
                return res;
            }

            await emit("Sentinel", null, "done", "'" + res.OldName + "' → '" + newName + "', " + totalAfter + " components intact");
            res.Info = "Renamed '" + res.OldName + "' → '" + newName + "'. One Ctrl+Z undoes it; Forge didn't save.";
            return res;
        }
    }
}
