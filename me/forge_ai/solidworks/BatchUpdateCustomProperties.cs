using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class BatchUpdateCustomPropertiesResult
    {
        public string PropName;
        public string PropValue;
        public int Applied;      // unique parts changed
        public int Skipped;      // already equal
        public int Failed;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 139 — batch_update_custom_properties (WRITE, "across selected files/components"). Unlike set_custom_property
    /// (one document), this writes the SAME property name/value onto EVERY unique part in the open assembly — the bulk
    /// counterpart, same relationship as batch_update_materials to set_material. Reuses the proven
    /// CustomPropertyManager.Add3 route (validated by set_custom_property) once per unique part file among the
    /// non-suppressed components. Verified by an INDEPENDENT per-part read-back (fail closed); idempotent (a part
    /// already at the target value is skipped); undoable; Forge never saves.
    /// </summary>
    public static class BatchUpdateCustomProperties
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            bool scope = Regex.IsMatch(c, @"\b(all|every|each)\b") && Regex.IsMatch(c, @"\b(part|parts|component|components)\b");
            bool prop = Regex.IsMatch(c, @"\b(set|add|write|update|tag)\b") &&
                        Regex.IsMatch(c, @"\b(custom )?propert(y|ies)\b") &&
                        Regex.IsMatch(c, @"\bto\b") &&
                        !Regex.IsMatch(c, @"\b(material|dimension|config|configuration)\b");
            return scope && prop;
        }

        public static async Task<BatchUpdateCustomPropertiesResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new BatchUpdateCustomPropertiesResult();
            var asm = model as AssemblyDoc;
            if (asm == null) { res.Error = "Open the assembly (.SLDASM) to batch-update a property across its parts."; return res; }

            string txt = intent ?? "";
            string name = null, value = null;
            var q = Regex.Matches(txt, "[\"']([^\"']+)[\"']");
            if (q.Count >= 2) { name = q[0].Groups[1].Value.Trim(); value = q[1].Groups[1].Value.Trim(); }
            else
            {
                var m = Regex.Match(txt, @"propert(?:y|ies)\s+(?:called\s+|named\s+)?([A-Za-z0-9_\- ]+?)\s+to\s+(.+)$", RegexOptions.IgnoreCase);
                if (m.Success) { name = m.Groups[1].Value.Trim(); value = m.Groups[2].Value.Trim().Trim('.', ' ', '"', '\''); }
            }
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(value))
            { res.Error = "Which property, and to what value? e.g. \"set the property Reviewer to Forge on every part\"."; return res; }
            res.PropName = name; res.PropValue = value;

            await emit("Gauge", "reading the parts", "run", null);
            object[] comps = asm.GetComponents(false) as object[];
            var parts = new List<IModelDoc2>();
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var o in comps ?? new object[0])
            {
                var comp = o as Component2; if (comp == null) continue;
                bool sup = false; try { sup = comp.IsSuppressed(); } catch { } if (sup) continue;
                string path = null; try { path = comp.GetPathName(); } catch { }
                if (string.IsNullOrEmpty(path) || !seenPaths.Add(path)) continue;
                var pd = comp.GetModelDoc2() as IModelDoc2; if (pd == null) continue;
                parts.Add(pd);
            }
            await emit("Gauge", null, "done", "found " + parts.Count + " unique part" + (parts.Count == 1 ? "" : "s"));
            if (parts.Count == 0) { res.Error = "No parts to tag."; return res; }

            await emit("Scribe", "writing '" + name + "' = '" + value + "' on every part", "run", null);
            foreach (var pd in parts)
            {
                CustomPropertyManager cpm = null; try { cpm = pd.Extension.get_CustomPropertyManager(""); } catch { }
                if (cpm == null) { res.Failed++; continue; }

                string before = ReadResolved(cpm, name);
                if (before != null && string.Equals(before, value, StringComparison.Ordinal)) { res.Skipped++; continue; }

                try { cpm.Add3(name, (int)swCustomInfoType_e.swCustomInfoText, value, (int)swCustomPropertyAddOption_e.swCustomPropertyReplaceValue); }
                catch { res.Failed++; continue; }

                string after = ReadResolved(cpm, name);
                if (string.Equals(after, value, StringComparison.Ordinal)) res.Applied++; else res.Failed++;
            }

            if (res.Failed > 0)
            { res.Error = res.Applied + " applied, " + res.Skipped + " already set, " + res.Failed + " failed to verify."; await emit("Scribe", null, "fail", res.Error); return res; }

            await emit("Scribe", null, "done", res.Applied + " part(s) tagged" + (res.Skipped > 0 ? " (" + res.Skipped + " already were)" : ""));
            res.Info = res.Applied == 0
                ? "All " + res.Skipped + " part(s) already had '" + name + "' = '" + value + "' — nothing to change."
                : "Set '" + name + "' = '" + value + "' on " + res.Applied + " part" + (res.Applied == 1 ? "" : "s") +
                  (res.Skipped > 0 ? " (" + res.Skipped + " already were)" : "") + ". One Ctrl+Z per part undoes it; Forge didn't save.";
            return res;
        }

        private static string ReadResolved(CustomPropertyManager cpm, string name)
        {
            bool present = false;
            try
            {
                var names = cpm.GetNames() as string[];
                if (names != null) foreach (var n in names) if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) { present = true; break; }
            }
            catch { }
            if (!present) return null;
            string val = null, resolved = null;
            try { cpm.Get4(name, false, out val, out resolved); } catch { }
            return string.IsNullOrEmpty(resolved) ? val : resolved;
        }
    }
}
