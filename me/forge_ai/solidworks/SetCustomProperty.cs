using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class SetCustomPropertyResult
    {
        public string PropName;
        public string PropValue;
        public bool WasPresent;
        public bool AlreadyEqual;
        public bool Verified;      // fail closed: an independent read-back returns exactly the value we wrote
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool — set_custom_property (WRITE, metadata). Writes a file-scope custom property via CustomPropertyManager.Add3
    /// (a proven working API on this build — the fixture generator uses it). "set the custom property Reviewer to Forge",
    /// 'set property "Revision" to "B"'. Verifies fail-closed by an INDEPENDENT read-back of the resolved value (Add3's
    /// return code is NOT trusted). Idempotent (property already holds that value → no-op). Never saves the document.
    /// </summary>
    public static class SetCustomProperty
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            return Regex.IsMatch(c, @"\b(set|add|write|update)\b") &&
                   Regex.IsMatch(c, @"\b(custom )?propert(y|ies)\b") &&
                   Regex.IsMatch(c, @"\bto\b") &&
                   !Regex.IsMatch(c, @"\b(material|dimension|config|configuration)\b");   // those are their own handlers
        }

        public static async Task<SetCustomPropertyResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new SetCustomPropertyResult();
            if (model == null) { res.Error = "Open a part or assembly to set a custom property."; return res; }
            string txt = intent ?? "";

            // Parse name + value. Prefer quoted tokens; else "property <name> to <value>".
            string name = null, value = null;
            var q = Regex.Matches(txt, "[\"']([^\"']+)[\"']");
            if (q.Count >= 2) { name = q[0].Groups[1].Value.Trim(); value = q[1].Groups[1].Value.Trim(); }
            else
            {
                var m = Regex.Match(txt, @"propert(?:y|ies)\s+(?:called\s+|named\s+)?([A-Za-z0-9_\- ]+?)\s+to\s+(.+)$", RegexOptions.IgnoreCase);
                if (m.Success) { name = m.Groups[1].Value.Trim(); value = m.Groups[2].Value.Trim().Trim('.', ' ', '"', '\''); }
            }
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(value))
            { res.Error = "Which property, and to what value? e.g. \"set the custom property Reviewer to Forge\"."; return res; }
            res.PropName = name; res.PropValue = value;

            CustomPropertyManager cpm = null;
            try { cpm = model.Extension.get_CustomPropertyManager(""); } catch { }
            if (cpm == null) { res.Error = "Couldn't reach the custom-property manager for this document."; return res; }

            string before = ReadResolved(cpm, name, out res.WasPresent);
            if (res.WasPresent && string.Equals(before, value, StringComparison.Ordinal))
            { res.AlreadyEqual = true; res.Verified = true; res.Info = "'" + name + "' already = '" + value + "' — nothing to do."; await emit("Sentinel", null, "done", "already set — no-op"); return res; }

            await emit("Scribe", (res.WasPresent ? "updating" : "adding") + " property '" + name + "' = '" + value + "'", "run", null);
            try { cpm.Add3(name, (int)swCustomInfoType_e.swCustomInfoText, value, (int)swCustomPropertyAddOption_e.swCustomPropertyReplaceValue); }
            catch (Exception ex) { res.Error = "Couldn't write the property (" + ex.GetType().Name + ") — unchanged."; await emit("Scribe", null, "fail", res.Error); return res; }

            // ---- Sentinel: independent read-back (fail closed) ----
            await emit("Sentinel", "verifying by read-back", "run", null);
            bool present2; string after = ReadResolved(cpm, name, out present2);
            res.Verified = present2 && string.Equals(after, value, StringComparison.Ordinal);
            if (!res.Verified)
            {
                res.Error = !present2 ? "Property '" + name + "' didn't appear after the write." : "Read-back = '" + after + "', expected '" + value + "' — write didn't stick.";
                await emit("Sentinel", null, "fail", res.Error); return res;
            }

            await emit("Sentinel", null, "done", "'" + name + "' = '" + value + "' (verified by read-back)");
            res.Info = (res.WasPresent ? "Updated" : "Added") + " custom property '" + name + "' = '" + value + "'. Forge didn't save — the value is in the open document only.";
            return res;
        }

        // Resolve a property's value independently (presence via GetNames, value via Get4 — the API the rest of the
        // codebase uses on this build).
        private static string ReadResolved(CustomPropertyManager cpm, string name, out bool present)
        {
            present = false;
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
