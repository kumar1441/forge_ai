using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class EditEquationResult
    {
        public string Name;          // the equation / global-variable name that was edited
        public double OldValue = double.NaN;
        public double NewValue = double.NaN;
        public bool IsGlobal;        // true if it's a global variable (vs a dimension-driving equation)
        public int RebuildErrors;
        public bool AlreadyDone;     // idempotent: already at the requested value
        public bool NeedsConfirm;    // zero/ambiguous match → ask ONE question, wrote nothing
        public string Question;
        public bool Verified;        // fail closed: the equation re-reads at NewValue and the rebuild is clean
        public string Info;
        public string Error;
    }

    /// <summary>
    /// EditEquation (tool #68 edit_equation) — a parametric WRITE on a PART: change the value of an existing EQUATION or
    /// GLOBAL VARIABLE. "change the wall thickness to 3", "set the D1 global to 50", "make thickness = 5", "set the
    /// bolt_count variable to 8". The most leverage-per-keystroke edit — one global variable can drive dozens of
    /// dimensions.
    ///
    /// Uses the documented, proven IEquationMgr path (see the docs/SOLIDWORKS-GOTCHAS.md landmine): GetEquationMgr() → iterate GetCount()
    /// equations → match the target NAME (the quoted left-hand side, e.g. "thickness") → set Equation(i) = '"name"= value'
    /// → ForceRebuild3. Named crew:
    ///   Gauge — parse "&lt;name&gt; to/= &lt;value&gt;"; resolve the equation by exact/fuzzy name against the LIVE equation list.
    ///           Zero or multiple matches → ask ONE question listing the actual equation names (Rule #2), edit nothing.
    ///   Setter — set the equation string, ONE ForceRebuild3.
    ///   Sentinel — FAIL CLOSED (Rule #6): re-read the equation's Value(i) and confirm it now equals NewValue (within a
    ///           tight tolerance) and the rebuild is clean.
    ///
    /// IDEMPOTENT (Rule #5): if the equation is already at NewValue, report "already set". UNDO is sacred (Rule #7):
    /// one Ctrl+Z restores the old value; Forge never saves. A PART with NO equations is reported honestly.
    /// </summary>
    public static class EditEquation
    {
        public static bool IsEditEquationIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            // must reference an equation/global/variable, OR be a "set <name> = <num>" / "change <name> to <num>" form.
            bool eqWord = Regex.IsMatch(c, @"\b(equation|global|global variable|variable|var)\b");
            bool assignForm = Regex.IsMatch(c, @"\b(set|change|make|update)\b.*\b(to|=)\b\s*-?\d") || Regex.IsMatch(c, @"=\s*-?\d");
            // guard: dimension edits ("change the length to 100", a DIMENSION) go to set_dimension; only route here when an
            // equation/global is explicitly named, to avoid stealing set_dimension's intents.
            return eqWord && (assignForm || Regex.IsMatch(c, @"\b(set|change|make|update|edit)\b"));
        }

        public static async Task<EditEquationResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new EditEquationResult();

            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { res.Error = "Editing an equation works on a single part — open the .SLDPRT whose equation/global variable you want to change."; return res; }

            var eqMgr = model.GetEquationMgr();
            if (eqMgr == null) { res.Error = "This part has no equation manager."; return res; }
            int count = 0; try { count = eqMgr.GetCount(); } catch { }
            if (count == 0)
            { res.Error = "This part has no equations or global variables to edit — add one first, or use 'set dimension' to change a dimension directly."; await emit("Gauge", null, "fail", "no equations"); return res; }

            await emit("Gauge", "reading equations and parsing the target", "run", null);

            string targetName = ParseName(intent);
            double? newValue = ParseValue(intent);
            if (newValue == null)
            { res.Error = "Tell me the new value, e.g. 'set the thickness global to 3'."; await emit("Gauge", null, "fail", "no value"); return res; }
            res.NewValue = newValue.Value;

            // ---- collect (index, name, value, isGlobal) for every equation ----
            var items = new List<Tuple<int, string, double, bool>>();
            for (int i = 0; i < count; i++)
            {
                string eq = null; try { eq = eqMgr.get_Equation(i); } catch { }
                string nm = LhsName(eq);
                double val = double.NaN; try { val = eqMgr.get_Value(i); } catch { }
                bool glob = false; try { glob = eqMgr.get_GlobalVariable(i); } catch { }
                if (!string.IsNullOrEmpty(nm)) items.Add(Tuple.Create(i, nm, val, glob));
            }

            // ---- resolve the target equation by name ----
            Tuple<int, string, double, bool> hit = null;
            if (!string.IsNullOrWhiteSpace(targetName))
            {
                foreach (var it in items) if (string.Equals(it.Item2, targetName, StringComparison.OrdinalIgnoreCase)) { hit = it; break; }
                if (hit == null) foreach (var it in items) if (it.Item2.ToLowerInvariant().Contains(targetName.ToLowerInvariant())) { hit = it; break; }
            }
            // if exactly one equation exists and no name was clear, use it
            if (hit == null && items.Count == 1 && string.IsNullOrWhiteSpace(targetName)) hit = items[0];

            if (hit == null)
            {
                res.NeedsConfirm = true;
                var names = new List<string>();
                foreach (var it in items) { names.Add(it.Item2); if (names.Count >= 8) break; }
                res.Question = "Which equation should I change" + (targetName != null ? " (couldn't match '" + targetName + "')" : "") +
                               "? This part has: " + string.Join(", ", names) + ".";
                await emit("Gauge", null, "fail", "target not resolved — asking");
                return res;
            }

            res.Name = hit.Item2;
            res.OldValue = hit.Item3;
            res.IsGlobal = hit.Item4;
            await emit("Gauge", null, "done", "'" + res.Name + "' " + Fmt(res.OldValue) + " → " + Fmt(res.NewValue) + (res.IsGlobal ? " (global variable)" : ""));

            // ---- IDEMPOTENT (Rule #5) ----
            if (!double.IsNaN(res.OldValue) && Math.Abs(res.OldValue - res.NewValue) < 1e-9)
            {
                res.AlreadyDone = true;
                res.Verified = true;
                res.Info = "'" + res.Name + "' is already " + Fmt(res.NewValue) + " — nothing to do.";
                await emit("Setter", null, "done", "already " + Fmt(res.NewValue));
                return res;
            }

            int errBefore = SafeWhatsWrong(model);

            // ---- Setter: rewrite the equation string, keeping its name; ONE rebuild ----
            await emit("Setter", "setting '" + res.Name + "' = " + Fmt(res.NewValue), "run", null);
            try
            {
                // preserve the exact left-hand side quoting; only swap the RHS to the new numeric value.
                eqMgr.set_Equation(hit.Item1, "\"" + res.Name + "\" = " + res.NewValue.ToString("0.###############", CultureInfo.InvariantCulture));
            }
            catch (Exception ex) { res.Error = "Couldn't set the equation (" + ex.GetType().Name + ") — the part is unchanged."; await emit("Setter", null, "fail", res.Error); return res; }
            try { model.ForceRebuild3(false); } catch { }
            res.RebuildErrors = SafeWhatsWrong(model);

            // ---- Sentinel: FAIL CLOSED — re-read the value ----
            await emit("Sentinel", "verifying the new value", "run", null);
            double readBack = double.NaN;
            try { readBack = eqMgr.get_Value(hit.Item1); } catch { }
            bool valueOk = !double.IsNaN(readBack) && Math.Abs(readBack - res.NewValue) < Math.Max(1e-6, Math.Abs(res.NewValue) * 1e-6);
            bool clean = res.RebuildErrors <= errBefore;

            res.Verified = valueOk && clean;
            if (!res.Verified)
            {
                res.Error = !valueOk ? "The value didn't take (reads " + Fmt(readBack) + ", expected " + Fmt(res.NewValue) + ") — check the part."
                          : "The change introduced " + (res.RebuildErrors - errBefore) + " rebuild error(s) — the equation may drive a dimension that can't reach that value.";
                await emit("Sentinel", null, "fail", res.Error);
                return res;
            }

            await emit("Sentinel", null, "done", "'" + res.Name + "' = " + Fmt(res.NewValue) + " confirmed, rebuild clean");
            res.Info = "Set '" + res.Name + "' from " + Fmt(res.OldValue) + " to " + Fmt(res.NewValue) +
                       (res.IsGlobal ? " (global variable — drives every dimension linked to it)" : "") + ", rebuild clean. One Ctrl+Z restores it; Forge didn't save.";
            return res;
        }

        // ================= parsing =================

        // the equation/global NAME: the token(s) before "to"/"=" after a set/change verb, or a quoted "name".
        private static string ParseName(string intent)
        {
            string c = (intent ?? "").Trim();
            var q = Regex.Match(c, "[\"']([^\"']+)[\"']");
            if (q.Success) return q.Groups[1].Value.Trim();

            var m = Regex.Match(c, @"\b(?:set|change|make|update|edit)\b\s+(?:the\s+)?(.+?)\s+(?:\b(?:global(?:\s+variable)?|variable|var|equation)\b\s+)?\b(?:to|=)\b", RegexOptions.IgnoreCase);
            if (!m.Success) m = Regex.Match(c, @"(.+?)\s*=\s*-?\d");
            if (!m.Success) return null;
            string name = m.Groups[1].Value.Trim();
            name = Regex.Replace(name, @"\b(the|a|an|global variable|global|variable|var|equation|value of|value)\b", "", RegexOptions.IgnoreCase).Trim();
            name = name.Trim('"', '\'', ' ');
            return name.Length == 0 ? null : name;
        }

        // the new numeric value (after "to"/"="). Strips a trailing unit word/symbol; value is taken as the equation's own unit.
        private static double? ParseValue(string intent)
        {
            string c = (intent ?? "").ToLowerInvariant();
            var m = Regex.Match(c, @"\b(?:to|=)\b\s*(-?\d+(?:\.\d+)?)");
            if (!m.Success) m = Regex.Match(c, @"=\s*(-?\d+(?:\.\d+)?)");
            if (!m.Success) m = Regex.Match(c, @"(-?\d+(?:\.\d+)?)\s*(?:mm|cm|in|inch|inches|deg|degrees)?\s*$");
            if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return v;
            return null;
        }

        // the quoted left-hand side of an equation string: '"thickness" = 5' → "thickness"; '"D1@Sketch1" = 10' → "D1@Sketch1".
        private static string LhsName(string eq)
        {
            if (string.IsNullOrEmpty(eq)) return null;
            var m = Regex.Match(eq, "^\\s*\"([^\"]+)\"");
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }

        private static string Fmt(double v) => double.IsNaN(v) ? "?" : v.ToString("0.###", CultureInfo.InvariantCulture);
        private static int SafeWhatsWrong(IModelDoc2 model) { try { return model.Extension.GetWhatsWrongCount(); } catch { return 0; } }
    }
}
