using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class LightweightRow
    {
        public string Name;
        public int StateBefore;    // swComponentSuppressionState_e as reported by GetSuppression2
        public int StateAfter;
        public int Rc;             // the RAW SetSuppression2 return code — instrumented, never trusted (docs/SOLIDWORKS-GOTCHAS.md)
        public string Route;       // which API actually moved the state: "SetSuppression2" | "FullyLightweight" | "MakeLightWeight"
        public bool Changed;
        public string Skipped;     // reason, when nothing was attempted
    }

    public class SetComponentLightweightResult
    {
        public string Direction;        // "lightweight" | "resolved"
        public string TargetKind;       // what the request resolved to ("bolts", "every component", …)
        public int Matched;             // instances the target resolved to, INCLUDING ones deliberately skipped
        public int Changed;             // independently CONFIRMED in the requested state (read-back), and not there before
        public int AlreadyInState;      // idempotency: already lightweight/resolved at run start
        public int SkippedSuppressed;   // suppressed components are the USER's state — never resurrected (Rule #9)
        public int Failed;              // attempted but not confirmed afterwards (fail closed)
        public int LightweightAfter;    // handler's own census of lightweight components after the run
        public int SuppressedAfter;     // must be unchanged — proof nothing was resurrected
        public int RebuildErrors;
        public string RouteUsed;        // the API route that actually moved the state on this build
        public List<LightweightRow> Rows = new List<LightweightRow>();
        public bool AlreadyDone;
        public bool NeedsConfirm;
        public string Question;
        public bool Verified;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// SetComponentLightweight (tool #157 set_component_lightweight) — set components LIGHTWEIGHT (graphics only, no
    /// solid model in memory) or fully RESOLVED, explicitly. The load-time lever on a big assembly: "set all the
    /// fasteners to lightweight" turns a 4-minute open into a 40-second one; "resolve the bolts" gets the geometry back
    /// when a measurement or a mate needs it.
    ///
    /// Named crew:
    ///   Gauge   — resolve the target from the LIVE model (kind words bolt/nut/washer/fastener, a fuzzy name, or the
    ///             whole assembly) and read every component's CURRENT state via GetSuppression2. Nothing matched → one
    ///             question naming what IS here (Rule #2).
    ///   Loader  — SetSuppression2 per component, one at a time (Rule #4). SUPPRESSED components are SKIPPED, never
    ///             touched: a suppressed part is a state the user chose, and setting it lightweight would silently
    ///             resurrect it (Rule #9). The raw return code is recorded per row — instrumented, not believed.
    ///   Sentinel— FAIL CLOSED (Rule #6): re-read every component's state and count only what is CONFIRMED in the
    ///             requested state; separately re-count the suppressed components and refuse to call the run verified
    ///             if that number moved.
    ///
    /// IDEMPOTENT (Rule #5): a rerun finds every target already in the requested state and reports "nothing to do".
    /// State-only — no geometry is edited, one Ctrl+Z restores it, and Forge never saves.
    /// </summary>
    public static class SetComponentLightweight
    {
        // NARROW: fires on the lightweight vocabulary, or on "resolve" ONLY when it is clearly about component LOADING
        // (never "resolve the mate errors" — that is the red-wave fixer).
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (Regex.IsMatch(c, @"\b(mate|mates|error|errors|red|conflict|conflicts|interference|dangling|reference|references|sketch|dimension|feature)\b")) return false;
            if (Regex.IsMatch(c, @"\b(lightweight|light weight|lightweigh|lighten)\b")) return true;
            return Regex.IsMatch(c, @"\b(resolve|resolved|fully resolve)\b") &&
                   Regex.IsMatch(c, @"\b(component|components|part|parts|assembly|everything|all|bolt|bolts|nut|nuts|fastener|fasteners|washer|washers)\b");
        }

        public static async Task<SetComponentLightweightResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new SetComponentLightweightResult();
            var asm = model as AssemblyDoc;
            if (asm == null) { res.Error = "Lightweight/resolved is an ASSEMBLY setting — open the .SLDASM."; return res; }

            string c = (intent ?? "").ToLowerInvariant();
            bool toResolved = Regex.IsMatch(c, @"\b(resolve|resolved|fully resolve)\b") && !Regex.IsMatch(c, @"\b(lightweight|light weight|lighten)\b");
            res.Direction = toResolved ? "resolved" : "lightweight";
            int want = toResolved ? (int)swComponentSuppressionState_e.swComponentFullyResolved
                                  : (int)swComponentSuppressionState_e.swComponentLightweight;

            await emit("Gauge", "resolving which components to set " + res.Direction, "run", null);

            var all = new List<Component2>();
            foreach (var o in (asm.GetComponents(false) as object[]) ?? new object[0])
            { var comp = o as Component2; if (comp != null) all.Add(comp); }
            if (all.Count == 0) { res.Error = "This assembly has no components."; await emit("Gauge", null, "fail", "empty assembly"); return res; }

            string kind;
            var targets = ResolveTargets(all, c, out kind);
            res.TargetKind = kind;
            if (targets.Count == 0)
            {
                res.NeedsConfirm = true;
                res.Question = "Nothing here matches \"" + kind + "\". This assembly has: " +
                               string.Join(", ", all.Select(NameOf).Where(n => n != null).Take(6)) + ". Which components?";
                await emit("Gauge", null, "fail", "target matched nothing — asking");
                return res;
            }
            res.Matched = targets.Count;
            int suppressedBefore = all.Count(IsSup);
            await emit("Gauge", null, "done", res.Matched + " component" + (res.Matched == 1 ? "" : "s") + " match \"" + kind + "\"");

            // ---- Loader: one component at a time; suppressed instances are left exactly as the user left them ----
            await emit("Loader", "setting " + res.Direction, "run", null);
            var attempted = new List<Tuple<Component2, LightweightRow>>();
            foreach (var comp in targets)
            {
                var row = new LightweightRow { Name = NameOf(comp), StateBefore = State(comp), Rc = int.MinValue };
                if (IsSup(comp)) { row.Skipped = "suppressed — left as the user set it"; row.StateAfter = row.StateBefore; res.SkippedSuppressed++; res.Rows.Add(row); continue; }
                if (InState(row.StateBefore, toResolved)) { row.Skipped = "already " + res.Direction; row.StateAfter = row.StateBefore; res.AlreadyInState++; res.Rows.Add(row); continue; }
                try { row.Rc = comp.SetSuppression2(want); }
                catch (Exception ex) { row.Skipped = "SolidWorks refused (" + ex.GetType().Name + ")"; res.Failed++; res.Rows.Add(row); continue; }
                if (State(comp) == want) row.Route = "SetSuppression2";
                else if (!toResolved)
                {
                    // SetSuppression2 returned swSuppressionChangeOk (2) but the state did not move — the silent-no-op
                    // class this build is full of. Second route: the OTHER lightweight enum value.
                    try { comp.SetSuppression2((int)swComponentSuppressionState_e.swComponentFullyLightweight); } catch { }
                    if (IsLight(comp)) row.Route = "FullyLightweight";
                }
                attempted.Add(Tuple.Create(comp, row));
                res.Rows.Add(row);
            }

            // ---- third route (lightweight only): the UI's own path — SELECT the components, then IAssemblyDoc.MakeLightWeight().
            //      Only run when the per-component setters left everything untouched, so a working build never pays for it. ----
            if (!toResolved && attempted.Count > 0 && attempted.All(t => !IsLight(t.Item1)))
            {
                await emit("Loader", null, "run", "per-component setter did not move the state (rc=" + attempted[0].Item2.Rc + ") — trying MakeLightWeight on the selection");
                try
                {
                    model.ClearSelection2(true);
                    foreach (var t in attempted) { try { t.Item1.Select4(true, null, false); } catch { } }
                    asm.MakeLightWeight();
                    model.ClearSelection2(true);
                }
                catch { }
                foreach (var t in attempted) if (IsLight(t.Item1)) t.Item2.Route = "MakeLightWeight";
            }
            res.RouteUsed = res.Rows.Select(r => r.Route).FirstOrDefault(r => r != null);
            await emit("Loader", null, "done", attempted.Count + " changed · " + res.AlreadyInState + " already " + res.Direction + " · " + res.SkippedSuppressed + " suppressed (skipped)");

            if (attempted.Count == 0 && res.Failed == 0)
            {
                foreach (var r in res.Rows) r.StateAfter = State(FindByName(all, r.Name));
                res.AlreadyDone = true;
                res.Verified = true;
                res.LightweightAfter = all.Count(x => IsLight(x));
                res.SuppressedAfter = all.Count(IsSup);
                res.Info = "Every " + res.TargetKind + " is already " + res.Direction + " — nothing to do.";
                await emit("Sentinel", null, "done", "already " + res.Direction + " — nothing to do");
                return res;
            }

            // ---- Sentinel: FAIL CLOSED — re-read state; the return code is evidence, not proof ----
            await emit("Sentinel", "verifying component states", "run", null);
            foreach (var t in attempted)
            {
                t.Item2.StateAfter = State(t.Item1);
                if (InState(t.Item2.StateAfter, toResolved)) { t.Item2.Changed = true; res.Changed++; }
                else res.Failed++;
            }
            foreach (var r in res.Rows) if (r.StateAfter == 0 && r.Skipped != null) r.StateAfter = r.StateBefore;

            res.LightweightAfter = all.Count(x => IsLight(x));
            res.SuppressedAfter = all.Count(IsSup);
            try { res.RebuildErrors = model.Extension.GetWhatsWrongCount(); } catch { }

            bool nothingResurrected = res.SuppressedAfter == suppressedBefore;
            res.Verified = res.Changed > 0 && res.Failed == 0 && nothingResurrected;
            if (!res.Verified)
            {
                res.Error = !nothingResurrected
                    ? "The suppressed-component count moved (" + suppressedBefore + " → " + res.SuppressedAfter + ") — a suppressed part was resurrected; check the assembly."
                    : res.Changed == 0 ? "SolidWorks accepted the call but no component came back " + res.Direction + " (raw return codes: " + string.Join(",", res.Rows.Where(r => r.Rc != int.MinValue).Select(r => r.Rc.ToString()).Take(5)) + ")."
                    : res.Failed + " component(s) didn't reach " + res.Direction + " — reported as unconfirmed, not as done.";
                await emit("Sentinel", null, "fail", res.Error);
                return res;
            }

            await emit("Sentinel", null, "done", res.Changed + " confirmed " + res.Direction + " · " + res.SuppressedAfter + " still suppressed (unchanged) · " +
                       (res.RebuildErrors == 0 ? "rebuild clean" : res.RebuildErrors + " rebuild flag(s)"));
            res.Info = "Set " + res.Changed + " of " + res.Matched + " " + res.TargetKind + " to " + res.Direction
                     + (res.AlreadyInState > 0 ? ", " + res.AlreadyInState + " already were" : "")
                     + (res.SkippedSuppressed > 0 ? ", " + res.SkippedSuppressed + " left suppressed" : "")
                     + ". State only — one Ctrl+Z restores it, and Forge didn't save.";
            return res;
        }

        // ---- target resolution, grounded in the live model ----
        private static List<Component2> ResolveTargets(List<Component2> all, string cmd, out string kind)
        {
            bool wantFastener = Regex.IsMatch(cmd, @"\b(fastener|fasteners|hardware)\b");
            bool wantBolt = wantFastener || Regex.IsMatch(cmd, @"\b(bolt|bolts|screw|screws)\b");
            bool wantNut = wantFastener || Regex.IsMatch(cmd, @"\b(nut|nuts)\b");
            bool wantWasher = wantFastener || Regex.IsMatch(cmd, @"\b(washer|washers)\b");
            if (wantBolt || wantNut || wantWasher)
            {
                kind = wantFastener ? "fasteners" : (wantBolt ? "bolts" : wantNut ? "nuts" : "washers");
                return all.Where(x => {
                    string k = Classify(NameOf(x));
                    return (k == "bolt" && wantBolt) || (k == "nut" && wantNut) || (k == "washer" && wantWasher);
                }).ToList();
            }
            if (Regex.IsMatch(cmd, @"\b(everything|all|every|whole|entire|assembly|components|parts)\b"))
            { kind = "every component"; return new List<Component2>(all); }

            var m = Regex.Match(cmd, @"\b(?:set|make|turn)\b\s+(?:the\s+)?(.+?)\s+\b(?:to|as|lightweight|resolved)\b");
            string phrase = m.Success ? m.Groups[1].Value.Trim() : null;
            kind = phrase ?? "every component";
            if (string.IsNullOrEmpty(phrase)) return new List<Component2>(all);
            var toks = Regex.Matches(phrase, @"[a-z0-9]+").Cast<Match>().Select(x => x.Value)
                        .Where(w => w.Length > 2 && w != "the" && w != "all" && w != "part" && w != "parts").ToList();
            if (toks.Count == 0) return new List<Component2>(all);
            return all.Where(x => { var n = (NameOf(x) ?? "").ToLowerInvariant(); return toks.Any(tk => n.Contains(tk)); }).ToList();
        }

        private static readonly string[] BoltHints = { "bolt", "screw", "hcs", "shcs", "cap", "socket", "hex", "stud" };
        private static string Classify(string n)
        {
            if (string.IsNullOrEmpty(n)) return "other";
            n = n.ToLowerInvariant();
            if (n.Contains("nut")) return "nut";
            if (n.Contains("washer")) return "washer";
            foreach (var h in BoltHints) if (n.Contains(h)) return "bolt";
            return "other";
        }

        private static Component2 FindByName(List<Component2> all, string name)
        { return all.FirstOrDefault(x => string.Equals(NameOf(x), name, StringComparison.OrdinalIgnoreCase)); }

        // this build reports BOTH lightweight enum values, and both resolved ones — treat each pair as one state
        private static bool InState(int st, bool toResolved)
        {
            return toResolved
                ? st == (int)swComponentSuppressionState_e.swComponentFullyResolved || st == (int)swComponentSuppressionState_e.swComponentResolved
                : st == (int)swComponentSuppressionState_e.swComponentLightweight || st == (int)swComponentSuppressionState_e.swComponentFullyLightweight;
        }
        private static bool IsLight(Component2 c) { return InState(State(c), false); }

        private static string NameOf(Component2 c) { try { return c?.Name2; } catch { return null; } }
        // NOT IsSuppressed(): on this R2026x build IsSuppressed() returns TRUE for a LIGHTWEIGHT component too, so it
        // would report "5 parts got resurrected" the moment this handler did its job. The state enum is the only
        // honest suppression test (measured — see docs/SOLIDWORKS-GOTCHAS.md).
        private static bool IsSup(Component2 c) { return State(c) == (int)swComponentSuppressionState_e.swComponentSuppressed; }
        private static int State(Component2 c) { try { return c == null ? -1 : c.GetSuppression2(); } catch { return -1; } }
    }
}
