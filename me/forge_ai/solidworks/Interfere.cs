using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    // one interfering pair, with the overlap volume the engineer actually cares about
    public class Interferer { public string A; public string B; public double VolumeMm3; public bool Self; }

    public class InterfereResult
    {
        public int Raw;            // every clash SW's native detector reported (the noise + the signal)
        public int Real;           // what's left after thread/seat-clearance is filtered out
        public int ThreadNoise;    // fastener thread / seat-clearance clashes we suppressed
        public int Dust;           // sub-threshold numerical dust we suppressed
        public double TopVolumeMm3;
        public List<Interferer> Top = new List<Interferer>();  // worst-first, capped
        // ---- cost instrumentation (2026-07-24). The native detector is a single blocking COM call: it cannot be
        //      interrupted, so the only honest control is what it is ASKED to do and a measurement of what that cost.
        public int ComponentCount;
        public int Stage1Raw;          // raw clash count from the bounded pass — compared like-for-like with the GT
        public long Stage1Ms;          // detection WITHOUT multibody-internal clashes
        public long Stage2Ms;          // detection WITH them (-1 = not attempted)
        public string Stage;           // which stage's result is being reported
        public bool Bounded;           // true = multibody-internal clashes were NOT checked, and why is in Info
        public string BoundedReason;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Interfere — demo #5 "interference that matters". SolidWorks' native detector returns hundreds of
    /// clashes (every modeled thread crossing every tapped/clearance hole, every nut torqued onto its bolt).
    /// That's noise. Forge runs the SAME detector, then filters the fastener thread/seat clearances out and
    /// reports only the REAL part-on-part interferences — worst-first, with the overlap volume of each.
    /// READ-ONLY: it never adds a mate, moves a component, or alters a config. Every number it reports is
    /// independently re-derivable by the harness (GroundTruth.MeasureInterfere) for cross-checking.
    /// </summary>
    public static class Interfere
    {
        public static bool IsInterfereIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            return System.Text.RegularExpressions.Regex.IsMatch(cmd,
                @"\b(interfere|interference|clash|collision|collide|overlap|penetrat)\w*", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        // Below this the clash is numerical dust (coincident faces read as a sliver) — never a real interference.
        private const double DustMm3 = 0.1;
        // A fastener's modeled thread crossing its tapped/clearance hole, or a bolt seating into a counterbore,
        // reads as a small clash. Above this a fastener-involved clash is a genuine crash worth surfacing.
        private const double ThreadNoiseMm3 = 20.0;
        private const int TopCap = 5;
        // If the bounded pass alone costs more than this, the full-fidelity pass is not attempted.
        private const long EscalateBudgetMs = 45000;

        public static async Task<InterfereResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new InterfereResult();
            var asm = model as AssemblyDoc;
            if (asm == null) { res.Error = "Open an assembly to check interferences."; return res; }

            try { res.ComponentCount = asm.GetComponentCount(false); } catch { }

            // STAGED ESCALATION. Measured 2026-07-24: on the seller's real #4 assemblies the single-shot full-fidelity
            // call never returned inside 300s — and `pc fan` has only FOUR components, so this is geometry cost, not
            // component count. The call is atomic COM, so it cannot be capped mid-flight; what CAN be controlled is
            // what it is asked to do. Stage 1 excludes multibody-INTERNAL clashes (the combinatorially expensive part:
            // every body of a multibody part against every other). If Stage 1 comes back inside the budget, Stage 2
            // re-runs at full fidelity and its answer is the one reported. If Stage 1 already spent the budget, the
            // run STOPS there and says so — a bounded scan that is honest about its bound beats a full scan that never
            // returns. Rule #6: what is reported is what was measured, with its limits named.
            // ===================== ALL COM RUNS BEFORE THE FIRST `await` =====================
            // WHY (2026-07-25, fixes the 0xC0000005 that took SolidWorks down mid-demo): the
            // InterferenceDetectionManager and the IInterference objects it returns are apartment-bound (STA).
            // This handler is routed through TryDemoRouteFirst so Run is ENTERED on SolidWorks' UI/STA thread —
            // but the instant we `await` anything (emit's Task.Delay), the continuation resumes on a threadpool
            // thread with no sync context, and the next COM read ACCESS-VIOLATES and crashes SW. So gather every
            // COM value into PLAIN data here, synchronously; only THEN narrate (narration touches no COM).
            try { res.ComponentCount = asm.GetComponentCount(false); } catch { }

            // STAGED ESCALATION (measured 2026-07-24): a single full-fidelity call never returns on the seller's #4
            // assemblies (geometry cost, not component count). Stage 1 excludes multibody-INTERNAL clashes (the
            // combinatorially expensive part); if it returns inside budget, Stage 2 re-runs at full fidelity and that
            // answer wins. If Stage 1 already spent the budget, we STOP there and say so (Rule #6).
            object[] raw;
            var sw1 = System.Diagnostics.Stopwatch.StartNew();
            try { raw = DetectRaw(asm, false); }
            catch (Exception ex) { res.Error = "Interference detection failed: " + ex.Message; return res; }
            sw1.Stop();
            res.Stage1Ms = sw1.ElapsedMilliseconds;
            res.Stage1Raw = raw == null ? 0 : raw.Length;
            res.Stage = "no-multibody";
            res.Stage2Ms = -1;

            if (res.Stage1Ms <= EscalateBudgetMs)
            {
                var sw2 = System.Diagnostics.Stopwatch.StartNew();
                object[] full = null;
                try { full = DetectRaw(asm, true); } catch { }
                sw2.Stop();
                res.Stage2Ms = sw2.ElapsedMilliseconds;
                if (full != null) { raw = full; res.Stage = "full"; }
            }
            else
            {
                res.Bounded = true;
                res.BoundedReason = "the bounded pass alone took " + (res.Stage1Ms / 1000.0).ToString("0.0") +
                                    "s, so multibody-internal clashes were NOT checked — a full pass on this model " +
                                    "would not have returned in usable time.";
            }

            res.Raw = raw == null ? 0 : raw.Length;

            var real = new List<Interferer>();
            foreach (var o in raw ?? new object[0])
            {
                var itf = o as IInterference;
                if (itf == null) continue;

                double volMm3 = 0; try { volMm3 = Math.Abs(itf.Volume) * 1e9; } catch { }   // SW volume is m^3

                string a, b; int fastCount; PairNames(itf, out a, out b, out fastCount);

                // classify the clash — fail toward SIGNAL (an unmeasurable clash is reported, not silently dropped)
                if (volMm3 > 0 && volMm3 < DustMm3) { res.Dust++; continue; }              // dust
                if (fastCount >= 2) { res.ThreadNoise++; continue; }                        // bolt↔nut / fastener↔fastener = intended thread/seat engagement
                if (fastCount == 1 && volMm3 > 0 && volMm3 < ThreadNoiseMm3) { res.ThreadNoise++; continue; } // thread-into-hole / seat clearance

                // Same component on both sides = a genuine MULTIBODY self-interference (two solid bodies inside the
                // one component overlap — SW returns that component twice). It's real signal, not a pair-naming bug,
                // so flag it to label it clearly ("X self-interference") instead of the confusing "X ∩ X".
                bool self = a != "?" && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
                real.Add(new Interferer { A = a, B = b, VolumeMm3 = volMm3, Self = self });
            }
            res.Real = real.Count;
            real.Sort((x, y) => y.VolumeMm3.CompareTo(x.VolumeMm3));   // worst-first
            res.TopVolumeMm3 = real.Count > 0 ? real[0].VolumeMm3 : 0;
            for (int i = 0; i < real.Count && i < TopCap; i++) res.Top.Add(real[i]);
            res.Info = BuildInfo(res);

            // ===================== COM DONE — narrate the crew steps (no COM below, safe to await) =====================
            await emit("Probe", null, "done", res.Stage1Raw + " raw clashes in " + res.Stage1Ms + "ms (bounded pass)");
            if (res.Stage2Ms >= 0)
                await emit("Probe", null, "done", res.Raw + " raw clashes at full fidelity in " + res.Stage2Ms + "ms");
            else if (res.Bounded)
                await emit("Probe", null, "done", "bounded scan kept (" + (res.Stage1Ms / 1000.0).ToString("0.0") + "s)");
            await emit("Sieve", null, "done", res.Real + " real · " + res.ThreadNoise + " thread/clearance ignored");
            return res;
        }

        // ---- run SW's native detector with read-only, thread-aware options; return the raw clash array ----
        private static object[] DetectRaw(AssemblyDoc asm, bool includeMultibody)
        {
            var idm = asm.InterferenceDetectionManager;
            if (idm == null) return new object[0];
            // read-only + drill into sub-assemblies so part-on-part clashes aren't hidden inside a subassembly unit.
            try { idm.TreatCoincidenceAsInterference = false; } catch { }        // touching faces are not a clash
            try { idm.IncludeMultibodyPartInterferences = includeMultibody; } catch { }  // the expensive option — staged
            try { idm.MakeInterferingPartsTransparent = false; } catch { }       // do NOT change the display
            try { idm.CreateFastenersFolder = false; } catch { }                 // do NOT add a folder to the tree (stay read-only)
            try { idm.IgnoreHiddenBodies = true; } catch { }
            try { idm.ShowIgnoredInterferences = false; } catch { }
            try { idm.TreatSubAssembliesAsComponents = false; } catch { }        // drill into subassemblies
            try { idm.UseTransform = false; } catch { }
            return idm.GetInterferences() as object[];
        }

        // component names of the interfering pair + how many of them are fasteners (Rule #8: from the live model)
        private static void PairNames(IInterference itf, out string a, out string b, out int fastCount)
        {
            a = "?"; b = "?"; fastCount = 0;
            object[] comps = null; try { comps = itf.Components as object[]; } catch { }
            var names = new List<string>();
            foreach (var o in comps ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                string nm = null; try { nm = c.Name2; } catch { }
                names.Add(nm ?? "?");
                if (IsFastener(nm)) fastCount++;
            }
            if (names.Count > 0) a = names[0];
            if (names.Count > 1) b = names[1];
            if (names.Count > 2) b = names[1] + " +" + (names.Count - 1);
        }

        private static bool IsFastener(string name)
        {
            var k = IntentLayer.ClassifyKind(name);
            return k == "bolt" || k == "nut" || k == "washer";
        }

        // verdict first (Character #3), one number not an adjective (Character #2)
        private static string BuildInfo(InterfereResult r)
        {
            string bound = r.Bounded ? " NOTE: this was a bounded scan — " + r.BoundedReason : "";
            if (r.Real == 0)
                return "No real interferences. " + r.Raw + " raw clashes were all thread/seat clearance (" +
                       r.ThreadNoise + ") or sub-threshold dust (" + r.Dust + ")." + bound;

            string head = r.Real + " real interference" + (r.Real == 1 ? "" : "s") +
                          " (of " + r.Raw + " raw; " + r.ThreadNoise + " thread/clearance, " + r.Dust + " dust ignored). ";
            var sb = new System.Text.StringBuilder(head);
            sb.Append("Worst: ");
            int shown = Math.Min(3, r.Top.Count);
            for (int i = 0; i < shown; i++)
            {
                var t = r.Top[i];
                // ASCII-safe rendering (the panel eats non-ASCII glyphs); label a multibody self-clash clearly.
                if (t.Self) sb.Append(t.A + " self-interference (2 bodies overlap) " + t.VolumeMm3.ToString("0.0") + " mm3");
                else sb.Append(t.A + " vs " + t.B + " " + t.VolumeMm3.ToString("0.0") + " mm3");
                sb.Append(i < shown - 1 ? "; " : ".");
            }
            sb.Append(bound);
            return sb.ToString();
        }
    }
}
