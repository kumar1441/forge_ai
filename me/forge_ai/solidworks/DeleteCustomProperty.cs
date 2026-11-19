using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public class DeleteCustomPropertyResult
    {
        public string PropName;
        public bool WasPresent;
        public bool Verified;      // fail closed: an independent read-back shows the name gone
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool — delete_custom_property (WRITE, metadata). Removes a file-scope custom property via
    /// CustomPropertyManager.Delete2. "delete the custom property Revision", 'remove property "PartNo"'. Completes the
    /// metadata family (get/set/delete). Idempotent (name absent → nothing to do). Verifies fail-closed by an
    /// INDEPENDENT read-back (the name is gone). Never saves the document.
    /// </summary>
    public static class DeleteCustomProperty
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            return Regex.IsMatch(c, @"\b(delete|remove|drop|clear)\b") &&
                   Regex.IsMatch(c, @"\b(custom )?propert(y|ies)\b") &&
                   !Regex.IsMatch(c, @"\b(config|configuration|feature|mate|dimension)\b");   // those are their own handlers
        }

        public static async Task<DeleteCustomPropertyResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new DeleteCustomPropertyResult();
            if (model == null) { res.Error = "Open a part or assembly to delete a custom property."; return res; }
            string txt = intent ?? "";

            string name = null;
            var q = Regex.Match(txt, "[\"']([^\"']+)[\"']");
            if (q.Success) name = q.Groups[1].Value.Trim();
            else
            {
                var m = Regex.Match(txt, @"propert(?:y|ies)\s+(?:called\s+|named\s+)?([A-Za-z0-9_\-]+)", RegexOptions.IgnoreCase);
                if (m.Success) name = m.Groups[1].Value.Trim();
                else { var m2 = Regex.Match(txt, @"(?:delete|remove|drop|clear)\s+(?:the\s+)?([A-Za-z0-9_\-]+)\s+propert", RegexOptions.IgnoreCase); if (m2.Success) name = m2.Groups[1].Value.Trim(); }
            }
            if (string.IsNullOrWhiteSpace(name)) { res.Error = "Which property? e.g. \"delete the custom property Revision\"."; return res; }
            res.PropName = name;

            CustomPropertyManager cpm = null;
            try { cpm = model.Extension.get_CustomPropertyManager(""); } catch { }
            if (cpm == null) { res.Error = "Couldn't reach the custom-property manager for this document."; return res; }

            res.WasPresent = HasProp(cpm, name);
            if (!res.WasPresent)
            { res.Verified = true; res.Info = "No custom property named '" + name + "' — nothing to delete."; await emit("Sentinel", null, "done", "not present — nothing to do"); return res; }

            await emit("Scribe", "deleting property '" + name + "'", "run", null);
            try { cpm.Delete2(name); }
            catch (Exception ex) { res.Error = "Couldn't delete the property (" + ex.GetType().Name + ") — unchanged."; await emit("Scribe", null, "fail", res.Error); return res; }

            // ---- Sentinel: independent read-back (fail closed) ----
            await emit("Sentinel", "verifying by read-back", "run", null);
            res.Verified = !HasProp(cpm, name);
            if (!res.Verified) { res.Error = "Property '" + name + "' is still present — delete didn't apply."; await emit("Sentinel", null, "fail", res.Error); return res; }

            await emit("Sentinel", null, "done", "'" + name + "' deleted (verified absent)");
            res.Info = "Deleted custom property '" + name + "'. Forge didn't save — the change is in the open document only.";
            return res;
        }

        private static bool HasProp(CustomPropertyManager cpm, string name)
        {
            try { var names = cpm.GetNames() as string[]; if (names != null) foreach (var n in names) if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return true; } catch { }
            return false;
        }
    }
}
