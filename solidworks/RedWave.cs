using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class RedWaveResult
    {
        public int ErrorsBefore;        // GetWhatsWrongCount at the start
        public int ErrorsAfter;         // GetWhatsWrongCount at the end
        public int OverDefinedBefore;   // components reporting over/no-solution/invalid at the start
        public int OverDefinedAfter;    // ... at the end
        public int Removed;             // mate features removed (the root cause(s))
        public int Cleared;             // headline errors cleared (the number in the result line)
        public bool RebuildClean = true;
        public string RemovedMateName;  // name of the first mate removed (for the card / log)
        public string RemovedMateType;  // friendly noun ("distance", "coincident", ...) of the first removed mate
        public string Info;             // one-line honest verdict
        public string Error;

        // ---- ddmin / suppress-pending-confirm gate surface ----
        public List<string> CulpritNames = new List<string>();  // the isolated culprit SET, by name (identity, not just count)
        public string SurvivingMateSample;                      // a mate deliberately left untouched (the locating one)
        public bool SuppressedPendingConfirm;                   // culprits are SUPPRESSED and await the user's delete confirm
        public int Deleted;                                     // mates DELETED during the run - must be 0 while searching (Rule #9)
        public double ElapsedSec;                               // wall-clock of the whole run (budget gate)

        // ---- classification pre-pass (H-2 wrapper): not every "red wave" is an over-define ----
        public string WaveKind;                                 // "clean" | "refs" | "conflicts" | "mixed"
        public List<string> DanglingMateNames = new List<string>();  // mates pointing at suppressed/missing components
        public int GhostRefCount;                               // dangling mate references found by the pre-pass
        public bool DdminRan;                                   // did the set-search actually execute? (refs-only must NOT run it)
        public List<string> MissingFiles = new List<string>();  // WHICH component files are missing (name them - character rule)
        public int OpenErrors;                                  // OpenDoc6 error code - the ONLY missing-ref signal on this build
    }

    /// <summary>
    /// Red Wave - Forge's mate-error medic (demo #8 "fix the red wave"). A cascade of red/yellow mate flags almost
    /// always traces to ONE over-defining or dangling mate: remove that single root cause and the whole wave clears.
    /// A named crew runs in sequence:
    ///   Gauge   -> reads the assembly, inventories mate features (tree traversal of the Mates folder - the mate-read
    ///             COM APIs are dead on this 3DEXPERIENCE build), counts rebuild flags + over-defined components.
    ///   Tracer  -> isolates the root cause by EXPERIMENT: suppress each candidate mate, ForceRebuild3, re-count the
    ///             flags, restore it; the mate whose removal clears the MOST is the culprit (short-circuits the moment
    ///             one removal drops the count to zero).
    ///   Mender  -> removes ONLY that mate (a clean, Ctrl+Z-undoable delete - Forge never saves the doc), then loops
    ///             in case a second independent root cause remains.
    ///   Sentinel-> re-measures: errors cleared, assembly solves, no other mate harmed.
    ///
    /// HONEST by construction: the verdict is the MEASURED post-removal flag count, never the operation's own say-so.
    /// If no single removal reduces the errors, nothing is deleted and Forge says so (Rule #4 / know-what-you-don't-know).
    /// Idempotent: on a clean assembly it removes nothing and reports "no errors to fix".
    /// </summary>
    public static class RedWave
    {
        // Offline fallback matcher (the AI intent layer routes action "fix_red_wave"; this covers the no-cloud path).
        public static bool IsFixIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            return System.Text.RegularExpressions.Regex.IsMatch(cmd,
                @"\b(fix|repair|clear|clean\s*up|resolve|remove|kill)\b.*\b(error|errors|mate|mates|over.?defin|red|dangling|constrain|flag|flags)\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                || System.Text.RegularExpressions.Regex.IsMatch(cmd,
                    @"\b(mate error|mate errors|over.?defined|red wave|dangling mate)\b",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        private class MateFeat { public Feature Feat; public string Name; public int Type; }

        public static async Task<RedWaveResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new RedWaveResult();
            var asm = model as AssemblyDoc;
            if (asm == null) { res.Error = "Open the assembly (.SLDASM) to fix mate errors."; return res; }

            // ---- Gauge: read the assembly, inventory mates, count the damage ----
            await emit("Gauge", "reading the assembly", "run", null);
            int wrong0, over0; Measure(model, out wrong0, out over0);
            var mates = CollectMates(model);
            res.ErrorsBefore = wrong0; res.OverDefinedBefore = over0;
            await emit("Gauge", null, "done",
                mates.Count + " mate" + (mates.Count == 1 ? "" : "s") + " | " +
                wrong0 + " rebuild flag" + (wrong0 == 1 ? "" : "s") + " | " +
                over0 + " over-defined component" + (over0 == 1 ? "" : "s"));

            // Idempotent / already-clean: touch nothing.
            if (wrong0 + over0 == 0)
            {
                res.WaveKind = "clean";
                await emit("Sentinel", null, "done", "no errors to fix - the assembly already solves cleanly");
                res.RebuildClean = true;
                res.Info = "No mate errors found - the rebuild is clean. Nothing to fix.";
                return res;
            }

            // ---- CLASSIFY PRE-PASS (H-2): not every red wave is an over-define. ----
            // A mate pointing at a SUPPRESSED or MISSING component is a dangling/ghost reference: ddmin would be
            // actively wrong there (suppressing more mates can't restore a missing reference) and would burn the
            // budget proving it. Classify first, then route: refs -> repair path, conflicts -> ddmin, mixed -> refs
            // first then re-count and ddmin whatever survives.
            await emit("Scout", "checking for dangling references before touching mates", "run", null);
            var missingFiles = new List<string>();
            res.DanglingMateNames = GhostRefMates(model, mates, missingFiles);
            res.MissingFiles = missingFiles;
            res.GhostRefCount = res.DanglingMateNames.Count;

            // OPEN-TIME SIGNAL. On this build a missing reference is invisible after opening - SW auto-suppresses the
            // orphaned components and the document reports zero errors. The ONLY place it ever showed up was
            // OpenDoc6's return code, so consult that too; without this, a genuine broken-reference assembly would be
            // classified as a mate conflict and ddmin would hunt culprits that don't exist.
            string docPath = null; try { docPath = model.GetPathName(); } catch { }
            res.OpenErrors = OpenState.ErrorsFor(docPath);
            if (res.OpenErrors > 0 && res.GhostRefCount == 0)
            {
                res.GhostRefCount = res.OpenErrors;       // reference faults SW reported at open but hides afterwards
                await emit("Scout", null, "done",
                    "SolidWorks reported " + res.OpenErrors + " problem(s) opening this file - reference fault, not a mate conflict");
            }

            bool anyConflict = over0 > 0;

            if (res.GhostRefCount > 0 && !anyConflict)
            {
                // REFS ONLY - do NOT run ddmin (Rule #4: know what you don't know).
                res.WaveKind = "refs"; res.DdminRan = false;
                res.ErrorsAfter = wrong0; res.OverDefinedAfter = over0; res.RebuildClean = wrong0 == 0;
                string names = string.Join(", ", res.DanglingMateNames.ToArray());
                await emit("Scout", null, "done", res.GhostRefCount + " dangling reference(s) - not an over-define");
                await emit("Sentinel", null, "done", "routed to reference repair; no mates were touched");
                string whichFiles = res.MissingFiles.Count > 0
                    ? " The missing file" + (res.MissingFiles.Count == 1 ? " is " : "s are ") + string.Join(", ", res.MissingFiles.ToArray()) + "."
                    : "";
                res.Info = "This isn't a mate conflict - it's " + res.GhostRefCount + " dangling reference" +
                           (res.GhostRefCount == 1 ? "" : "s") + " (" + names + ")." + whichFiles +
                           " Those mates point at a component that is missing or unresolved, so deleting mates would not fix it - " +
                           (res.MissingFiles.Count == 0 && res.OpenErrors > 0 ? "SolidWorks flagged this when the file was opened. " : "") +
                           "it would just destroy good mates. Restore the file (or point me at its new location) and the wave clears itself. Nothing was changed.";
                return res;
            }

            if (res.GhostRefCount > 0)
            {
                res.WaveKind = "mixed";
                await emit("Scout", null, "done",
                    res.GhostRefCount + " dangling reference(s) AND " + over0 + " over-defined - reporting refs first, then solving the conflicts");
            }
            else
            {
                res.WaveKind = "conflicts";
                await emit("Scout", null, "done", "no dangling references - this is a mate conflict");
            }
            if (mates.Count == 0)
            {
                await emit("Sentinel", null, "done", over0 + " over-defined but no mates to remove - the fault is elsewhere (fixed geometry / references)");
                res.RebuildClean = wrong0 == 0; res.ErrorsAfter = wrong0; res.OverDefinedAfter = over0;
                res.Info = wrong0 + " error(s) present but no mates to remove - this isn't a mate over-define; needs your eyes.";
                return res;
            }

            // ---- Tracer: DELTA DEBUGGING (ddmin) over mate SETS, not single mates ----
            // The old search asked "does removing THIS ONE mate help?". On a real assembly a component is usually
            // pinned by 2-3 conflicting mates, so removing any single one changes nothing and EVERY candidate scores
            // zero - it found nothing while spending a full rebuild per mate (19 min of CPU on the seller's gripper).
            // ddmin searches over SETS, so it finds culprit COMBINATIONS, and complement-reduction converges in
            // ~log2(n) rebuilds instead of n. Search SUPPRESSES only - never deletes (Rule #9: a crash mid-search must
            // never cost the user a mate). Deletion happens only after the user confirms.
            const double TimeBudgetSec = 90;
            const int MaxTests = 12;              // each test == one ForceRebuild3 (up to ~10s on a real assembly)
            var clock = System.Diagnostics.Stopwatch.StartNew();
            int tests = 0;
            bool budgetHit = false;

            // (1) NARROW FIRST. Don't bisect all 47 mates - only those touching a component that is actually in a bad
            // state. On a real model that's typically 5-15 candidates, and it makes the whole search tractable.
            var badComps = BadComponents(model);
            var candidates = new List<MateFeat>();
            foreach (var mf in mates) if (TouchesAny(mf, badComps)) candidates.Add(mf);
            bool narrowed = candidates.Count > 0 && candidates.Count < mates.Count;
            if (candidates.Count == 0) candidates = new List<MateFeat>(mates);   // fail open: nothing identifiable -> test all
            await emit("Tracer", null, "done",
                candidates.Count + " suspect mate(s) of " + mates.Count +
                (narrowed ? " (touching " + badComps.Count + " bad component(s))" : " (couldn't narrow - testing all)") +
                " - group-testing");

            // (3a) Sanity gate: if suppressing EVERY candidate still leaves errors, the fault isn't the mates at all.
            res.DdminRan = true;
            int allErr = TestSuppress(model, candidates); tests++;
            if (allErr != 0)
            {
                try { model.ForceRebuild3(false); } catch { }
                await emit("Tracer", null, "done",
                    "suppressing all " + candidates.Count + " suspect mates STILL leaves " + allErr + " error(s) - not a mate fault");
                res.ErrorsAfter = wrong0; res.OverDefinedAfter = over0; res.RebuildClean = wrong0 == 0;
                res.Info = "These errors don't come from the mates. Suppressing all " + candidates.Count +
                           " suspect mates still leaves " + allErr + " problem(s), so this is geometry or broken references, " +
                           "not an over-define. Nothing was changed.";
                return res;
            }

            // (3b) ddmin complement reduction - finds minimal SETS, handling multiple simultaneous culprits.
            var S = new List<MateFeat>(candidates);
            int n = 2;
            while (S.Count > 1)
            {
                if (tests >= MaxTests || clock.Elapsed.TotalSeconds > TimeBudgetSec) { budgetHit = true; break; }
                bool reduced = false;
                int chunk = (int)Math.Ceiling(S.Count / (double)n);
                for (int i = 0; i < n && i * chunk < S.Count; i++)
                {
                    if (tests >= MaxTests || clock.Elapsed.TotalSeconds > TimeBudgetSec) { budgetHit = true; break; }
                    int lo = i * chunk, hi = Math.Min((i + 1) * chunk, S.Count);
                    var complement = new List<MateFeat>();
                    for (int k = 0; k < S.Count; k++) if (k < lo || k >= hi) complement.Add(S[k]);
                    if (complement.Count == 0) continue;
                    int e = TestSuppress(model, complement); tests++;
                    await emit(null, null, "done",
                        "group test " + tests + "/" + MaxTests + ": " + complement.Count + " mates -> " + e + " error(s)");
                    if (e == 0) { S = complement; n = Math.Max(n - 1, 2); reduced = true; break; }
                }
                if (budgetHit) break;
                if (!reduced)
                {
                    if (n >= S.Count) break;         // finest granularity reached - S is already minimal-ish
                    n = Math.Min(n * 2, S.Count);    // split finer and test complements again
                }
            }

            // (4) VERIFY MINIMALITY. With the set suppressed and errors at zero, unsuppress each member in turn - any
            // member whose absence does NOT bring the errors back was never a culprit, so drop it from the report.
            foreach (var m in S) Suppress(m.Feat);
            int curErr; { int w, ov; Measure(model, out w, out ov); curErr = w + ov; }
            var culprits = new List<MateFeat>();
            if (curErr == 0)
            {
                foreach (var m in new List<MateFeat>(S))
                {
                    if (clock.Elapsed.TotalSeconds > TimeBudgetSec + 45) { budgetHit = true; culprits.Add(m); continue; }
                    Unsuppress(m.Feat);
                    int w2, ov2; Measure(model, out w2, out ov2); tests++;
                    if (w2 + ov2 == 0) { await emit(null, null, "done", "'" + m.Name + "' wasn't a culprit - dropped"); }
                    else { Suppress(m.Feat); culprits.Add(m); }
                }
                { int w3, ov3; Measure(model, out w3, out ov3); curErr = w3 + ov3; }
            }
            else culprits.AddRange(S);            // couldn't reach zero - report the narrowed set as suspects

            // ---- Sentinel: report what was MEASURED. Nothing is deleted; the culprits are left SUPPRESSED so the
            //      user sees a clean assembly and confirms the delete (Rule #5/#9, one Ctrl+Z undoes it). ----
            await emit("Sentinel", "verifying the assembly solves", "run", null);
            int wNow, oNow; Measure(model, out wNow, out oNow);
            res.ErrorsAfter = wNow; res.OverDefinedAfter = oNow; res.RebuildClean = wNow == 0;
            res.Removed = 0;                                     // suppressed, NOT deleted - deletion awaits confirm
            res.Cleared = (wrong0 + over0) - (wNow + oNow);

            if (culprits.Count == 0)
            {
                try { model.ForceRebuild3(false); } catch { }
                res.Info = "Couldn't isolate a culprit mate within the time budget. Nothing was changed.";
                await emit("Sentinel", null, "done", "no culprit isolated - nothing changed");
                return res;
            }

            res.RemovedMateName = culprits[0].Name;
            res.RemovedMateType = NounOf(culprits[0].Type);

            // Gate surface: the culprit SET by name, proof nothing was deleted, and a sample of a mate left alone.
            foreach (var m in culprits) res.CulpritNames.Add(m.Name);
            res.SuppressedPendingConfirm = wNow + oNow == 0 && culprits.Count > 0;
            res.Deleted = 0;                                     // search suppresses only - deletion awaits confirm
            res.ElapsedSec = clock.Elapsed.TotalSeconds;
            foreach (var m in mates)
            {
                bool isCulprit = false;
                foreach (var c in culprits) if (ReferenceEquals(c, m)) { isCulprit = true; break; }
                if (!isCulprit) { res.SurvivingMateSample = m.Name; break; }
            }
            var nameList = new List<string>();
            foreach (var m in culprits) nameList.Add("'" + m.Name + "'");
            string list = string.Join(", ", nameList.ToArray());
            string plural = culprits.Count == 1 ? "" : "s";

            if (wNow + oNow == 0)
            {
                res.Info = "Root cause: " + culprits.Count + " conflicting mate" + plural + " " + list +
                           ". Suppressing them clears all " + (wrong0 + over0) + " error" + ((wrong0 + over0) == 1 ? "" : "s") +
                           ". They're suppressed now - delete them?";
                await emit("Sentinel", null, "done",
                    "clean - " + (wrong0 + over0) + " error(s) cleared by suppressing " + culprits.Count + " mate" + plural + "; every other mate untouched");
            }
            else
            {
                // (3c) Budget hit mid-search: a NARROWED answer beats a timeout.
                // A narrowed answer beats silence: name the shortlist rather than reporting failure.
                res.Info = (budgetHit ? "Time budget reached - but I narrowed it down. " : "") +
                           "The culprits are among these " + culprits.Count + ": " + list + ". Suppressing them clears " +
                           res.Cleared + " of " + (wrong0 + over0) + " error(s); " + (wNow + oNow) + " remain. " +
                           (res.GhostRefCount > 0 ? "Note " + res.GhostRefCount + " dangling reference(s) are also present and need the component restored. " : "") +
                           "Delete these, or point me at a sub-assembly and I'll go deeper there.";
                await emit("Sentinel", null, "done",
                    "narrowed to " + culprits.Count + " suspect mate" + plural + " - " + res.Cleared + " of " + (wrong0 + over0) + " error(s) cleared");
            }
            return res;
        }

        // Test one candidate SET: suppress all of it, rebuild+measure, then restore. Returns the total error signal
        // (rebuild flags + over-defined). Restore deliberately skips a rebuild - the next Measure() rebuilds anyway,
        // so each test costs exactly ONE rebuild.
        private static int TestSuppress(IModelDoc2 model, List<MateFeat> set)
        {
            foreach (var m in set) Suppress(m.Feat);
            int w, ov; Measure(model, out w, out ov);
            foreach (var m in set) Unsuppress(m.Feat);
            return w + ov;
        }

        // Components SolidWorks reports as over-defined / no-solution / invalid - the mates worth suspecting.
        private static HashSet<string> BadComponents(IModelDoc2 model)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var asm = model as AssemblyDoc; if (asm == null) return set;
            foreach (var o in (asm.GetComponents(true) as object[]) ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                int st = (int)swConstrainedStatus_e.swUnderConstrained; try { st = c.GetConstrainedStatus(); } catch { }
                if (st == (int)swConstrainedStatus_e.swOverConstrained || st == (int)swConstrainedStatus_e.swNoSolution ||
                    st == (int)swConstrainedStatus_e.swInvalidSolution)
                { try { set.Add(c.Name2); } catch { } }
            }
            return set;
        }

        // Does this mate reference any of the bad components?
        private static bool TouchesAny(MateFeat mf, HashSet<string> bad)
        {
            if (bad == null || bad.Count == 0) return false;
            try
            {
                var m = mf.Feat.GetSpecificFeature2() as Mate2;
                if (m == null) return false;
                int n = m.GetMateEntityCount();
                for (int i = 0; i < n; i++)
                {
                    var me = m.MateEntity(i) as MateEntity2; if (me == null) continue;
                    var c = me.ReferenceComponent as Component2; if (c == null) continue;
                    string nm = null; try { nm = c.Name2; } catch { }
                    if (nm != null && bad.Contains(nm)) return true;
                }
            }
            catch { }
            return false;
        }

        // GHOST / DANGLING REFERENCES (tool 247): a mate whose referenced component is SUPPRESSED or missing. These
        // look identical to an over-define in the flag count, but no amount of mate removal fixes them - the fix is
        // to restore the component. Detecting this is what stops ddmin from confidently solving the wrong problem.
        private static List<string> GhostRefMates(IModelDoc2 model, List<MateFeat> mates, List<string> missingFiles)
        {
            var bad = new List<string>();
            foreach (var mf in mates)
            {
                bool dangling = false;
                try
                {
                    var m = mf.Feat.GetSpecificFeature2() as Mate2;
                    if (m == null) continue;
                    int n = m.GetMateEntityCount();
                    if (n == 0) dangling = true;
                    for (int i = 0; i < n && !dangling; i++)
                    {
                        var me = m.MateEntity(i) as MateEntity2;
                        if (me == null) { dangling = true; break; }
                        var c = me.ReferenceComponent as Component2;
                        if (c == null) { dangling = true; break; }          // reference gone entirely
                        bool sup = false; try { sup = c.IsSuppressed(); } catch { }
                        if (sup) dangling = true;                            // points at a suppressed component
                        // MISSING FILE is a DIFFERENT state from suppressed: the component is still in the tree but
                        // unresolved because its .SLDPRT was renamed/moved/deleted. IsSuppressed() is FALSE for these,
                        // so checking suppression alone misses exactly the case that produces a real ghost reference.
                        if (!dangling)
                        {
                            try { if (c.GetModelDoc2() == null) dangling = true; } catch { dangling = true; }
                            if (dangling && missingFiles != null)
                            {
                                // Name the actual file - "Bolt-3.SLDPRT is missing" is worth ten "reference errors".
                                string p = null; try { p = c.GetPathName(); } catch { }
                                if (!string.IsNullOrEmpty(p))
                                {
                                    string leaf = p; int ix = p.LastIndexOf('\\'); if (ix >= 0 && ix < p.Length - 1) leaf = p.Substring(ix + 1);
                                    if (!missingFiles.Contains(leaf)) missingFiles.Add(leaf);
                                }
                            }
                        }
                        if (!dangling)
                        {
                            int ss = -1; try { ss = c.GetSuppression2(); } catch { }
                            // swComponentSuppressionState_e: 0 = SuppressedIdMismatch (unresolved / file not found)
                            if (ss == 0) dangling = true;
                        }
                    }
                }
                catch { dangling = true; }
                if (dangling) { try { bad.Add(mf.Name); } catch { } }
            }
            return bad;
        }

        // ---- inventory: every mate feature in the Mates folder (tree traversal - the mate-read APIs are dead here) ----
        private static List<MateFeat> CollectMates(IModelDoc2 model)
        {
            var list = new List<MateFeat>();
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    string tn = ""; try { tn = f.GetTypeName2(); } catch { }
                    if (tn == "MateGroup")
                    {
                        var s = f.GetFirstSubFeature() as Feature;
                        while (s != null)
                        {
                            var mf = new MateFeat { Feat = s };
                            try { mf.Name = s.Name; } catch { }
                            try { var m = s.GetSpecificFeature2() as Mate2; if (m != null) mf.Type = m.Type; } catch { }
                            list.Add(mf);
                            s = s.GetNextSubFeature() as Feature;
                        }
                    }
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch { }
            return list;
        }

        // rebuild first (so the flags/status are current), then read the two independent damage signals
        private static void Measure(IModelDoc2 model, out int wrong, out int over)
        {
            try { model.ForceRebuild3(false); } catch { }
            wrong = 0; try { wrong = model.Extension.GetWhatsWrongCount(); } catch { }
            over = OverDefinedCount(model);
        }

        private static int OverDefinedCount(IModelDoc2 model)
        {
            int over = 0;
            var asm = model as AssemblyDoc; if (asm == null) return 0;
            foreach (var o in (asm.GetComponents(true) as object[]) ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                int st = (int)swConstrainedStatus_e.swUnderConstrained; try { st = c.GetConstrainedStatus(); } catch { }
                if (st == (int)swConstrainedStatus_e.swOverConstrained || st == (int)swConstrainedStatus_e.swNoSolution || st == (int)swConstrainedStatus_e.swInvalidSolution) over++;
            }
            return over;
        }

        private static bool Suppress(Feature f)
        { try { return f.SetSuppression2((int)swFeatureSuppressionAction_e.swSuppressFeature, (int)swInConfigurationOpts_e.swThisConfiguration, null); } catch { return false; } }
        private static bool Unsuppress(Feature f)
        { try { return f.SetSuppression2((int)swFeatureSuppressionAction_e.swUnSuppressFeature, (int)swInConfigurationOpts_e.swThisConfiguration, null); } catch { return false; } }

        // delete a mate feature (clean, single-Ctrl+Z-undoable - Forge never saves the doc; the user does)
        private static void DeleteFeature(IModelDoc2 model, Feature feat)
        {
            if (feat == null) return;
            try
            {
                model.ClearSelection2(true);
                feat.Select2(false, 0);
                model.EditDelete();
                model.ClearSelection2(true);
            }
            catch { }
        }

        private static string NounOf(int type)
        {
            switch ((swMateType_e)type)
            {
                case swMateType_e.swMateCOINCIDENT: return "coincident";
                case swMateType_e.swMateCONCENTRIC: return "concentric";
                case swMateType_e.swMatePERPENDICULAR: return "perpendicular";
                case swMateType_e.swMatePARALLEL: return "parallel";
                case swMateType_e.swMateTANGENT: return "tangent";
                case swMateType_e.swMateDISTANCE: return "distance";
                case swMateType_e.swMateANGLE: return "angle";
                case swMateType_e.swMateSYMMETRIC: return "symmetric";
                case swMateType_e.swMateWIDTH: return "width";
                default: return "";
            }
        }
        private static string Kind(MateFeat m) { string n = NounOf(m.Type); return (n == "" ? "" : n + " ") + "mate"; }
        private static string Full(MateFeat m)
        { string nm = m.Name; return Kind(m) + (string.IsNullOrEmpty(nm) ? "" : " '" + (nm.Length > 24 ? nm.Substring(0, 24) : nm) + "'"); }
    }
}
