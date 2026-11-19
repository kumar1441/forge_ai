using System;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class AddEquationResult
    {
        public string Name;          // the global-variable name created
        public double Value = double.NaN;
        public int CountBefore = -1;
        public int CountAfter = -1;
        public int RebuildErrors;
        public bool AlreadyExists;   // idempotent: a global with this name already exists → report, don't duplicate
        public bool Verified;        // fail closed: count rose by 1, the new global reads the value, rebuild clean
        public string Info;
        public string Error;
    }

    /// <summary>
    /// AddEquation (tool #67 add_equation) — a parametric WRITE on a PART: create a GLOBAL VARIABLE. "add a global
    /// variable thickness = 10", "create a global wall = 5", "define a variable bolt_count = 8". Global variables are the
    /// setup for parametric control — once created, dimensions can be linked to them and edit_equation can drive them.
    ///
    /// Uses the documented, reflection-verified IEquationMgr path: GetEquationMgr() → Add3(index, '"name" = value', solve,
    /// swAllConfiguration, null) → ForceRebuild3. Named crew:
    ///   Gauge — parse "&lt;name&gt; = &lt;value&gt;"; check the name isn't already a global (idempotency).
    ///   Setter — Add3 at the end of the equation list; ONE ForceRebuild3.
    ///   Sentinel — FAIL CLOSED (Rule #6): re-read — the equation COUNT rose by exactly 1, a global named &lt;name&gt; now reads
    ///           &lt;value&gt;, and the rebuild is clean.
    ///
    /// IDEMPOTENT (Rule #5): a global with that name already present → report it (a value change is edit_equation's job).
    /// UNDO is sacred (Rule #7): one Ctrl+Z removes it; Forge never saves.
    /// </summary>
    public static class AddEquation
    {
        public static bool IsAddEquationIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            // NB: "make" is deliberately NOT here — it belongs to edit_equation ("make thickness = 5" edits); add_equation
            // owns the create verbs. add/create/define/new + a global word + an assignment.
            bool addVerb = Regex.IsMatch(c, @"\b(add|create|define|new)\b");
            bool globalWord = Regex.IsMatch(c, @"\b(global|global variable|variable|var|equation)\b");
            bool assign = Regex.IsMatch(c, @"=\s*-?\d") || Regex.IsMatch(c, @"\bto\b\s*-?\d");
            return addVerb && globalWord && assign;
        }

        public static async Task<AddEquationResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new AddEquationResult();

            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { res.Error = "Adding a global variable works on a single part — open the .SLDPRT."; return res; }

            var eqMgr = model.GetEquationMgr();
            if (eqMgr == null) { res.Error = "This part has no equation manager."; return res; }

            await emit("Gauge", "parsing the global variable", "run", null);
            string name = ParseName(intent);
            double? value = ParseValue(intent);
            if (string.IsNullOrWhiteSpace(name)) { res.Error = "Tell me the variable name, e.g. 'add a global thickness = 10'."; await emit("Gauge", null, "fail", "no name"); return res; }
            if (value == null) { res.Error = "Tell me the value, e.g. 'add a global " + name + " = 10'."; await emit("Gauge", null, "fail", "no value"); return res; }
            res.Name = name; res.Value = value.Value;

            res.CountBefore = SafeCount(eqMgr);

            // ---- IDEMPOTENT (Rule #5): already a global with this name? ----
            for (int i = 0; i < res.CountBefore; i++)
            {
                if (string.Equals(LhsName(SafeEq(eqMgr, i)), name, StringComparison.OrdinalIgnoreCase))
                {
                    res.AlreadyExists = true;
                    res.Verified = true;
                    double existing = double.NaN; try { existing = eqMgr.get_Value(i); } catch { }
                    res.Info = "A global '" + name + "' already exists (= " + Fmt(existing) + "). To change its value use 'set " + name + " = ...'. Nothing added.";
                    await emit("Setter", null, "done", "'" + name + "' already exists");
                    return res;
                }
            }

            await emit("Gauge", null, "done", "adding global '" + name + "' = " + Fmt(res.Value) + " (" + res.CountBefore + " equations present)");
            int errBefore = SafeWhatsWrong(model);

            // ---- Setter: robust add. Add3 with solve=true silently rejects the equation on this 3DEXPERIENCE build
            //      unless AutomaticSolveOrder is on, and the return index is never validated. So: enable auto-solve,
            //      then try strategies in order (Add3 all-config → Add3 this-config → Add2 → Add), rebuilding and
            //      re-counting after each, stopping at the first that actually raises the count. ----
            await emit("Setter", "creating the global variable", "run", null);
            string eqStr = "\"" + name + "\" = " + res.Value.ToString("0.###############", CultureInfo.InvariantCulture);
            try { eqMgr.AutomaticSolveOrder = true; } catch { }
            try { eqMgr.AutomaticRebuild = true; } catch { }

            // ---- BLOCKED ON THIS BUILD (2026-07-22, proven) ----
            // IEquationMgr.Add/Add2/Add3 ALL return -1 (SW rejection) and add nothing, for every insert index
            // (-1 and count), every string format ("n" = v / "n"=v / bare), and in BOTH handler and test fixture generator
            // contexts — no exception is ever thrown. Proven dead, not a format or index bug. Same dead-API class as
            // the mate-read APIs and InsertMoveFace on this R2026x build. Do NOT re-attempt blind; see docs/SOLIDWORKS-GOTCHAS.md.
            // We still ATTEMPT the documented call, then fail CLOSED with an honest reason (Rule #6).
            string pathUsed = null;
            int at = res.CountBefore;
            var diag = new System.Text.StringBuilder();

            // DIAGNOSTIC PASS: every strategy records its return code, exception and the re-read count. Swallowing
            // these is what made this bug opaque — the count never moved and we had no idea which call failed how.
            // rc=-1 from EVERY call (no exception) means SW REJECTED the request. Two candidates: the insert index
            // (SW equation APIs append with -1, not with count) and the equation string format. Sweep both.
            string v = res.Value.ToString("0.###############", CultureInfo.InvariantCulture);
            var formats = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, string>>
            {
                new System.Collections.Generic.KeyValuePair<string, string>("q-sp",   "\"" + name + "\" = " + v),
                new System.Collections.Generic.KeyValuePair<string, string>("q-nosp", "\"" + name + "\"=" + v),
                new System.Collections.Generic.KeyValuePair<string, string>("bare",   name + " = " + v),
            };
            var strategies = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, Func<int>>>();
            foreach (var idx in new[] { -1, at })
            {
                foreach (var f in formats)
                {
                    int i2 = idx; string s2 = f.Value;
                    strategies.Add(new System.Collections.Generic.KeyValuePair<string, Func<int>>(
                        "Add3[" + i2 + "]/" + f.Key,
                        () => eqMgr.Add3(i2, s2, true, (int)swInConfigurationOpts_e.swAllConfiguration, null)));
                }
            }
            strategies.Add(new System.Collections.Generic.KeyValuePair<string, Func<int>>("Add2[-1]", () => eqMgr.Add2(-1, eqStr, true)));
            strategies.Add(new System.Collections.Generic.KeyValuePair<string, Func<int>>("Add[-1]",  () => eqMgr.Add(-1, eqStr)));

            foreach (var s in strategies)
            {
                int rc = -999; string err = null;
                try { rc = s.Value(); } catch (Exception ex) { err = ex.GetType().Name + ":" + ex.Message; }
                try { model.ForceRebuild3(true); } catch { }
                int after = SafeCount(eqMgr);
                diag.Append(s.Key + " rc=" + rc + (err != null ? " EX(" + err + ")" : "") + " cnt=" + after + " | ");
                if (after > res.CountBefore) { pathUsed = s.Key; break; }
            }

            // Probe the hypothesis that GetCount() simply doesn't report standalone globals on this build:
            // if the equation IS there, reading it back another way will show it even though the count didn't move.
            try { diag.Append("eq0='" + (SafeEq(eqMgr, 0) ?? "<null>") + "' "); } catch { }
            try { object gv = eqMgr.get_GlobalVariable(0); diag.Append("gv0=" + (gv == null ? "<null>" : gv.ToString()) + " "); }
            catch (Exception ex) { diag.Append("gv0 EX:" + ex.GetType().Name + " "); }
            try { diag.Append("status=" + eqMgr.Status + " "); } catch { }
            await emit("Diag", null, "run", diag.ToString());

            try { model.ForceRebuild3(true); } catch { }
            res.RebuildErrors = SafeWhatsWrong(model);
            if (pathUsed != null) await emit("Setter", null, "done", "added via " + pathUsed);

            // ---- Sentinel: FAIL CLOSED — count rose by 1, new global reads the value ----
            await emit("Sentinel", "verifying the new global", "run", null);
            res.CountAfter = SafeCount(eqMgr);
            double readBack = double.NaN; int foundIdx = -1;
            for (int i = 0; i < res.CountAfter; i++)
            {
                if (string.Equals(LhsName(SafeEq(eqMgr, i)), name, StringComparison.OrdinalIgnoreCase))
                { foundIdx = i; try { readBack = eqMgr.get_Value(i); } catch { } break; }
            }
            bool countRose = res.CountAfter == res.CountBefore + 1;
            bool valueOk = foundIdx >= 0 && !double.IsNaN(readBack) && Math.Abs(readBack - res.Value) < Math.Max(1e-6, Math.Abs(res.Value) * 1e-6);
            bool clean = res.RebuildErrors <= errBefore;

            res.Verified = countRose && valueOk && clean;
            if (!res.Verified)
            {
                res.Error = pathUsed == null ? "This SolidWorks build refuses equation writes — every IEquationMgr add (Add/Add2/Add3, all insert indices and formats) returns -1 and changes nothing. That's an API limitation of this 3DEXPERIENCE build, not a problem with your part. Add the global manually via Tools > Equations; nothing was modified."
                          : !countRose ? "The equation count didn't rise (" + res.CountBefore + " → " + res.CountAfter + ") — the global wasn't added."
                          : !valueOk ? "The global was added but reads the wrong value — check the part."
                          : "Adding the global introduced " + (res.RebuildErrors - errBefore) + " rebuild error(s) — check the part.";
                await emit("Sentinel", null, "fail", res.Error);
                return res;
            }

            await emit("Sentinel", null, "done", "global '" + name + "' = " + Fmt(res.Value) + " added (count " + res.CountBefore + " → " + res.CountAfter + "), rebuild clean");
            res.Info = "Added global variable '" + name + "' = " + Fmt(res.Value) + " (equation count " + res.CountBefore + " → " + res.CountAfter +
                       "). Link dimensions to it, then drive them with 'set " + name + " = ...'. One Ctrl+Z removes it; Forge didn't save.";
            return res;
        }

        // ================= parsing =================

        // NAME: a quoted "name", else the token(s) after a create verb and before "=". Strips filler words.
        private static string ParseName(string intent)
        {
            string c = (intent ?? "").Trim();
            var q = Regex.Match(c, "[\"']([^\"']+)[\"']");
            if (q.Success) return q.Groups[1].Value.Trim();

            var m = Regex.Match(c, @"\b(?:add|create|define|make|new)\b\s+(?:a\s+|an\s+)?(?:global(?:\s+variable)?|variable|var|equation)?\s*(?:called\s+|named\s+)?(.+?)\s*(?:=|\bto\b)", RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            string name = m.Groups[1].Value.Trim();
            name = Regex.Replace(name, @"\b(a|an|the|global variable|global|variable|var|equation|called|named)\b", "", RegexOptions.IgnoreCase).Trim();
            name = name.Trim('"', '\'', ' ');
            return name.Length == 0 ? null : name;
        }

        private static double? ParseValue(string intent)
        {
            string c = (intent ?? "").ToLowerInvariant();
            var m = Regex.Match(c, @"(?:=|\bto\b)\s*(-?\d+(?:\.\d+)?)");
            if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return v;
            return null;
        }

        private static string LhsName(string eq)
        {
            if (string.IsNullOrEmpty(eq)) return null;
            var m = Regex.Match(eq, "^\\s*\"([^\"]+)\"");
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }

        // Run one add strategy, rebuild, and report whether the equation count actually rose. Ignores the strategy's
        // own return code (unreliable here) — trusts only the re-read count, so a silent no-op falls through to the next.
        private static bool TryAdd(IEquationMgr eqMgr, IModelDoc2 model, Action add, int countBefore)
        {
            try { add(); } catch { }
            try { model.ForceRebuild3(true); } catch { }
            return SafeCount(eqMgr) > countBefore;
        }

        private static int SafeCount(IEquationMgr m) { try { return m.GetCount(); } catch { return 0; } }
        private static string SafeEq(IEquationMgr m, int i) { try { return m.get_Equation(i); } catch { return null; } }
        private static string Fmt(double v) => double.IsNaN(v) ? "?" : v.ToString("0.###", CultureInfo.InvariantCulture);
        private static int SafeWhatsWrong(IModelDoc2 model) { try { return model.Extension.GetWhatsWrongCount(); } catch { return 0; } }
    }
}
