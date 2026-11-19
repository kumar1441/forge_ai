using System;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class DeleteEquationResult
    {
        public string Name;          // the equation / global-variable name deleted
        public int CountBefore = -1;
        public int CountAfter = -1;
        public int RebuildErrors;
        public bool NotFound;        // idempotent: no equation with that name → nothing to delete
        public bool NeedsConfirm;    // ambiguous target → ask ONE question, deleted nothing
        public string Question;
        public bool Verified;        // fail closed: count dropped by 1, the name is gone, rebuild clean
        public string Info;
        public string Error;
    }

    /// <summary>
    /// DeleteEquation (tool #69 delete_equation) — a parametric WRITE on a PART: permanently remove an EQUATION or GLOBAL
    /// VARIABLE. "delete the thickness global", "remove the wall equation", "delete the bolt_count variable". Completes
    /// the equation CRUD family (add_equation / edit_equation / delete_equation).
    ///
    /// Uses the reflection-verified IEquationMgr path: GetEquationMgr() → find the index whose quoted left-hand side
    /// matches the target name → Delete(index) → ForceRebuild3. FAIL CLOSED (Rule #6): after the rebuild, confirm the
    /// count dropped by exactly 1, no equation with that name remains, and the rebuild is clean. A delete that ORPHANS a
    /// driven dimension (rebuild errors RISE) is reported honestly, never a silent success.
    ///
    /// IDEMPOTENT (Rule #5): no equation with that name → "nothing to delete". Zero name given but multiple equations →
    /// ask ONE question (Rule #2). UNDO is sacred (Rule #7): one Ctrl+Z restores it; Forge never saves.
    /// </summary>
    public static class DeleteEquation
    {
        public static bool IsDeleteEquationIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            bool delVerb = Regex.IsMatch(c, @"\b(delete|remove|drop|get rid of)\b");
            bool eqWord = Regex.IsMatch(c, @"\b(global|global variable|variable|var|equation)\b");
            return delVerb && eqWord;
        }

        public static async Task<DeleteEquationResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new DeleteEquationResult();

            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { res.Error = "Deleting an equation works on a single part — open the .SLDPRT."; return res; }

            var eqMgr = model.GetEquationMgr();
            if (eqMgr == null) { res.Error = "This part has no equation manager."; return res; }
            res.CountBefore = SafeCount(eqMgr);
            if (res.CountBefore == 0)
            { res.Error = "This part has no equations or global variables to delete."; await emit("Gauge", null, "fail", "no equations"); return res; }

            await emit("Gauge", "finding the equation to delete", "run", null);
            string name = ParseName(intent);

            // ---- resolve the index whose LHS matches the name ----
            int idx = -1;
            if (!string.IsNullOrWhiteSpace(name))
            {
                for (int i = 0; i < res.CountBefore; i++)
                    if (string.Equals(LhsName(SafeEq(eqMgr, i)), name, StringComparison.OrdinalIgnoreCase)) { idx = i; break; }
                if (idx < 0)
                    for (int i = 0; i < res.CountBefore; i++)
                    { var nm = LhsName(SafeEq(eqMgr, i)); if (nm != null && nm.ToLowerInvariant().Contains(name.ToLowerInvariant())) { idx = i; break; } }
            }
            else if (res.CountBefore == 1) idx = 0;   // only one → unambiguous

            if (idx < 0)
            {
                // no name matched (or none given with multiple present) → ask
                res.NotFound = !string.IsNullOrWhiteSpace(name);   // named but not found = idempotent "nothing to delete"
                if (res.NotFound)
                {
                    res.Verified = true;   // the requested state (that equation gone) already holds
                    res.Info = "No equation named '" + name + "' — nothing to delete.";
                    await emit("Gauge", null, "done", "no '" + name + "' — nothing to delete");
                    return res;
                }
                res.NeedsConfirm = true;
                var names = new System.Collections.Generic.List<string>();
                for (int i = 0; i < res.CountBefore && names.Count < 8; i++) { var nm = LhsName(SafeEq(eqMgr, i)); if (nm != null) names.Add(nm); }
                res.Question = "Which equation should I delete? This part has: " + string.Join(", ", names) + ".";
                await emit("Gauge", null, "fail", "target ambiguous — asking");
                return res;
            }

            res.Name = LhsName(SafeEq(eqMgr, idx));
            await emit("Gauge", null, "done", "deleting '" + res.Name + "' (" + res.CountBefore + " equations present)");
            int errBefore = SafeWhatsWrong(model);

            // ---- Deleter: remove it ----
            await emit("Deleter", "deleting '" + res.Name + "'", "run", null);
            try { eqMgr.Delete(idx); }
            catch (Exception ex) { res.Error = "Couldn't delete the equation (" + ex.GetType().Name + ") — the part is unchanged."; await emit("Deleter", null, "fail", res.Error); return res; }
            try { model.ForceRebuild3(false); } catch { }
            res.RebuildErrors = SafeWhatsWrong(model);

            // ---- Sentinel: FAIL CLOSED — count dropped by 1, name gone, rebuild clean ----
            await emit("Sentinel", "verifying the deletion", "run", null);
            res.CountAfter = SafeCount(eqMgr);
            bool gone = true;
            for (int i = 0; i < res.CountAfter; i++)
                if (string.Equals(LhsName(SafeEq(eqMgr, i)), res.Name, StringComparison.OrdinalIgnoreCase)) { gone = false; break; }
            bool countDropped = res.CountAfter == res.CountBefore - 1;
            bool clean = res.RebuildErrors <= errBefore;   // a delete must not ORPHAN a driven dimension

            res.Verified = countDropped && gone && clean;
            if (!res.Verified)
            {
                res.Error = !countDropped ? "The equation count didn't drop (" + res.CountBefore + " → " + res.CountAfter + ") — the delete didn't apply."
                          : !gone ? "An equation named '" + res.Name + "' is still present — the delete may not have applied cleanly."
                          : "Deleting '" + res.Name + "' orphaned " + (res.RebuildErrors - errBefore) + " driven dimension(s) — a dimension still references it. Undo recommended.";
                await emit("Sentinel", null, "fail", res.Error);
                return res;
            }

            await emit("Sentinel", null, "done", "deleted '" + res.Name + "' (count " + res.CountBefore + " → " + res.CountAfter + "), rebuild clean");
            res.Info = "Deleted '" + res.Name + "' (equation count " + res.CountBefore + " → " + res.CountAfter + ", rebuild clean). One Ctrl+Z restores it; Forge didn't save.";
            return res;
        }

        // ================= parsing =================

        private static string ParseName(string intent)
        {
            string c = (intent ?? "").Trim();
            var q = Regex.Match(c, "[\"']([^\"']+)[\"']");
            if (q.Success) return q.Groups[1].Value.Trim();

            var m = Regex.Match(c, @"\b(?:delete|remove|drop|get rid of)\b\s+(?:the\s+)?(.+?)\s*(?:\b(?:global(?:\s+variable)?|variable|var|equation)\b)?\s*$", RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            string name = m.Groups[1].Value.Trim();
            name = Regex.Replace(name, @"\b(the|a|an|global variable|global|variable|var|equation)\b", "", RegexOptions.IgnoreCase).Trim();
            name = name.Trim('"', '\'', ' ');
            return name.Length == 0 ? null : name;
        }

        private static string LhsName(string eq)
        {
            if (string.IsNullOrEmpty(eq)) return null;
            var m = Regex.Match(eq, "^\\s*\"([^\"]+)\"");
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }

        private static int SafeCount(IEquationMgr m) { try { return m.GetCount(); } catch { return 0; } }
        private static string SafeEq(IEquationMgr m, int i) { try { return m.get_Equation(i); } catch { return null; } }
        private static int SafeWhatsWrong(IModelDoc2 model) { try { return model.Extension.GetWhatsWrongCount(); } catch { return 0; } }
    }
}
