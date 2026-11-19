using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground-truth for the interference handler (demo #5). Shares NO code with Interfere.cs — it
    /// runs SolidWorks' native detector from scratch with its own settings, applies its own thread/clearance
    /// filter, and reports raw + filtered counts. It also proves the handler is READ-ONLY by re-reading the
    /// assembly's structural fingerprint (component count, mate count, rebuild flags) before and after its own
    /// detection pass and confirming nothing moved. Fastener classification here reuses GroundTruth's OWN
    /// IsFastenerName (partial-class private member) — that lives in GroundTruth.cs, not Interfere.cs.
    /// </summary>
    public static partial class GroundTruth
    {
        // Independent thresholds — deliberately identical to the handler's so the two counts AGREE (both drive the
        // same native detector); the harness still asserts only "within tolerance" to absorb SW nondeterminism.
        private const double ItfDustMm3 = 0.1;
        private const double ItfThreadNoiseMm3 = 20.0;

        public static JObject MeasureInterfere(ISldWorks app, IModelDoc2 model)
        {
            var mo = new JObject();
            var asm = model as AssemblyDoc;
            if (asm == null) { mo["error"] = "active doc is not an assembly"; return mo; }

            // ---- structural fingerprint BEFORE (read-only proof) ----
            int compBefore = 0; try { compBefore = asm.GetComponentCount(false); } catch { }
            int mateBefore, forgeBefore; CountMates(model, out mateBefore, out forgeBefore);
            int rebuildBefore = 0; try { rebuildBefore = model.Extension.GetWhatsWrongCount(); } catch { }

            int raw = 0, filtered = 0, threadNoise = 0, dust = 0; double topVol = 0;
            // BOUNDED PASS ONLY (2026-07-24). Measure() runs on run0/run1/run2, so a full-fidelity detection here cost
            // three more of the most expensive call in the harness on top of the handler's own - which is a large part
            // of why the real #4 assemblies never finished. This measures the same thing the handler's STAGE 1
            // measures, so the two are compared like-for-like (handler.Stage1Raw vs rawCount), and the elapsed time is
            // published so the cost is visible instead of inferred.
            var swDetect = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var idm = asm.InterferenceDetectionManager;
                if (idm != null)
                {
                    try { idm.TreatCoincidenceAsInterference = false; } catch { }
                    try { idm.IncludeMultibodyPartInterferences = false; } catch { }
                    try { idm.MakeInterferingPartsTransparent = false; } catch { }
                    try { idm.CreateFastenersFolder = false; } catch { }
                    try { idm.IgnoreHiddenBodies = true; } catch { }
                    try { idm.ShowIgnoredInterferences = false; } catch { }
                    try { idm.TreatSubAssembliesAsComponents = false; } catch { }
                    try { idm.UseTransform = false; } catch { }

                    object[] clashes = idm.GetInterferences() as object[];
                    raw = clashes == null ? 0 : clashes.Length;
                    foreach (var o in clashes ?? new object[0])
                    {
                        var itf = o as IInterference; if (itf == null) continue;
                        double volMm3 = 0; try { volMm3 = Math.Abs(itf.Volume) * 1e9; } catch { }
                        int fast = ItfFastenerCount(itf);

                        if (volMm3 > 0 && volMm3 < ItfDustMm3) { dust++; continue; }
                        if (fast >= 2) { threadNoise++; continue; }
                        if (fast == 1 && volMm3 > 0 && volMm3 < ItfThreadNoiseMm3) { threadNoise++; continue; }
                        filtered++;
                        if (volMm3 > topVol) topVol = volMm3;
                    }
                }
            }
            catch (Exception ex) { mo["detectError"] = ex.GetType().Name + ": " + ex.Message; }
            swDetect.Stop();
            mo["detectMs"] = swDetect.ElapsedMilliseconds;
            mo["multibodyIncluded"] = false;

            // ---- structural fingerprint AFTER (must be unchanged) ----
            int compAfter = 0; try { compAfter = asm.GetComponentCount(false); } catch { }
            int mateAfter, forgeAfter; CountMates(model, out mateAfter, out forgeAfter);
            int rebuildAfter = 0; try { rebuildAfter = model.Extension.GetWhatsWrongCount(); } catch { }

            mo["rawCount"] = raw;
            mo["filteredCount"] = filtered;       // the independent "real interference" count
            mo["threadNoise"] = threadNoise;
            mo["dust"] = dust;
            mo["topVolumeMm3"] = topVol;
            mo["readOnly"] = compBefore == compAfter && mateBefore == mateAfter && rebuildBefore == rebuildAfter;
            mo["componentCount"] = compAfter;
            mo["mateCount"] = mateAfter;
            return mo;
        }

        // fasteners in an interfering pair, using GroundTruth's OWN vocabulary (IsFastenerName in GroundTruth.cs)
        private static int ItfFastenerCount(IInterference itf)
        {
            int n = 0;
            object[] comps = null; try { comps = itf.Components as object[]; } catch { }
            foreach (var o in comps ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                string nm = null; try { nm = c.Name2; } catch { }
                if (IsFastenerName(nm)) n++;
            }
            return n;
        }
    }
}
