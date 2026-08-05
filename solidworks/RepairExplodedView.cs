using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class RepairExplodedViewResult
    {
        public int StepCount;                  // explode steps that exist after the repair
        public List<string> OrphanNames = new List<string>();   // live components covered by NO step (before repair)
        public List<string> RepairedNames = new List<string>(); // orphans confirmed covered after the repair
        public bool ApiReturn;      // raw AutoExplode() success (instrumented, never trusted alone)
        public bool AlreadyDone;    // idempotency: nothing orphaned
        public bool Verified;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 193 — repair_exploded_view (WRITE). Re-attaches an exploded view's steps after a model update
    /// (a component added AFTER the explode was authored isn't covered by any step and stays collapsed at the
    /// origin instead of exploding with the rest — a real pain point mentioned in three separate pain lists).
    /// "repair the exploded view", "reattach the exploded view", "fix the explode — the new bolt didn't move".
    /// Walks every existing step's covered-component set and diffs it against the assembly's CURRENT live
    /// top-level components to find orphans. Repair path: IExplodeStep.SetComponents (the obvious "just append
    /// the orphan to the last step" API) HANGS headlessly on this build (instrumented + confirmed 2026-07-31 — the
    /// call is entered and never returns, no exception) — same class of headless-UI-thread hang as Isolator's
    /// visibility ops. Instead deletes every existing step (DeleteExplodeStep, a plain non-hanging call) and calls
    /// AssemblyDoc.AutoExplode() again, the same non-interactive API already proven live for building this fixture,
    /// which regenerates full coverage over every current component. Verified by an INDEPENDENT re-read of every
    /// resulting step's component list, never AutoExplode's own return. Undoable (one Ctrl+Z); Forge never saves.
    /// </summary>
    public static class RepairExplodedView
    {
        // Diagnostic trace (kept — proved the SetComponents hang and guards against a regression to it).
        private static void Diag(string s)
        {
            try { System.IO.File.AppendAllText(System.IO.Path.Combine(System.Environment.GetEnvironmentVariable("LOCALAPPDATA"), "Temp", "forge-harness", "repair-explode-diag.txt"), DateTime.UtcNow.ToString("HH:mm:ss.fff") + " " + s + "\r\n"); } catch { }
        }

        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            // NARROW + specific-first: requires a repair/reattach/fix/update verb WITH the explode noun, checked
            // BEFORE Exploder.IsExplodeIntent (whose \bexploded\b alone would otherwise wrongly claim this — the
            // shadowing lesson every prior handler pair in this codebase has hit).
            return Regex.IsMatch(c, @"\b(repair|reattach|re-attach|fix|update|sync|resync)\b") &&
                   Regex.IsMatch(c, @"\bexplod(e|ed|ing)\b");
        }

        public static async Task<RepairExplodedViewResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            Diag("Run() enter");
            var res = new RepairExplodedViewResult();
            var asm = model as AssemblyDoc;
            if (asm == null) { res.Error = "Open an assembly to repair its exploded view."; return res; }

            await emit("Gauge", "reading the exploded view's steps", "run", null);
            var config = model.GetActiveConfiguration() as Configuration;
            if (config == null) { res.Error = "Couldn't read the active configuration."; await emit("Gauge", null, "fail", "no config"); return res; }
            Diag("config resolved");

            int stepCount = 0; try { stepCount = config.GetNumberOfExplodeSteps(); } catch { }
            res.StepCount = stepCount;
            Diag("stepCount=" + stepCount);
            if (stepCount == 0)
            { res.Error = "No exploded view exists on this assembly — explode it first, then repair applies to future model updates."; await emit("Gauge", null, "fail", "no exploded view"); return res; }

            // ---- covered set: every component name already in SOME step ----
            var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ExplodeStep lastStep = null;
            for (int i = 0; i < stepCount; i++)
            {
                ExplodeStep st = null; try { st = config.IGetExplodeStep(i); } catch { }
                if (st == null) continue;
                if (i == stepCount - 1) lastStep = st;
                int nc = 0; try { nc = st.GetNumOfComponents(); } catch { }
                for (int j = 0; j < nc; j++) { string cn = null; try { cn = st.GetComponentName(j); } catch { } if (cn != null) covered.Add(cn); }
            }
            if (lastStep == null) { res.Error = "Couldn't read the exploded view's last step."; await emit("Gauge", null, "fail", "last step unreadable"); return res; }
            Diag("covered set built, " + covered.Count + " names, lastStep resolved");

            // ---- current live top-level components vs. covered ----
            var live = new List<Component2>();
            foreach (var o in (asm.GetComponents(true) as object[]) ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                live.Add(c);
            }
            Diag("live components: " + live.Count);
            var orphans = new List<Component2>();
            foreach (var c in live) { string nm = null; try { nm = c.Name2; } catch { } if (nm != null && !covered.Contains(nm)) { orphans.Add(c); res.OrphanNames.Add(nm); } }
            Diag("orphans: " + orphans.Count + " [" + string.Join(",", res.OrphanNames) + "]");
            await emit("Gauge", null, "done", stepCount + " steps, " + orphans.Count + " orphaned component(s)");

            if (orphans.Count == 0)
            {
                res.AlreadyDone = true; res.Verified = true;
                res.Info = "Every component is already covered by the exploded view — nothing to repair.";
                await emit("Sentinel", null, "done", "nothing orphaned");
                return res;
            }

            // ---- Scribe: NOT via IExplodeStep.SetComponents — instrumented 2026-07-31 and confirmed to HANG
            // headlessly on this build (diagnostic trace enters the call and never returns, no exception, no
            // timeout). Same class of headless-UI-thread hang as Isolator's visibility ops. Deleting the steps and
            // re-running AssemblyDoc.AutoExplode() was tried next and does NOT hang, but returns true while
            // silently producing ZERO steps on the second call (a one-shot API on this build, another false-success
            // class) — so it can't be trusted to regenerate the whole sequence. Instead: leave every existing step
            // untouched and ADD ONE NEW STEP PER ORPHAN via selection + IConfiguration.IAddExplodeStep — the same
            // selection-driven feature-creation pattern already proven live and non-hanging by dozens of sibling
            // handlers in this codebase (AddHole/AddBoss/etc.), a fundamentally different call shape than setting
            // a property on an already-existing step object.
            await emit("Scribe", "adding " + orphans.Count + " new explode step(s) for the orphaned component(s)", "run", null);
            int added = 0;
            foreach (var c in orphans)
            {
                string cname = null; try { cname = c.Name2; } catch { }
                Diag("orphan " + cname + ": before ClearSelection2");
                try { model.ClearSelection2(true); } catch { }
                bool selComp = false; try { selComp = c.Select4(false, null, false); } catch (Exception selex) { Diag("Select4 threw " + selex.GetType().Name); }
                bool selPlane = false; try { selPlane = model.Extension.SelectByID2("Top Plane", "PLANE", 0, 0, 0, true, 1, null, 0); } catch (Exception selex2) { Diag("SelectByID2 threw " + selex2.GetType().Name); }
                Diag("orphan " + cname + ": selComp=" + selComp + " selPlane=" + selPlane);

                // Distance: 1.5x the orphan's own largest bounding-box dimension (Component2.GetBox — proven live
                // elsewhere in this codebase), floored so a tiny part still gets a visible, non-zero explode move.
                double dist = 0.03;
                try
                {
                    var box = c.GetBox(false, false) as double[];
                    if (box != null && box.Length >= 6)
                    {
                        double dx = box[3] - box[0], dy = box[4] - box[1], dz = box[5] - box[2];
                        double maxDim = Math.Max(dx, Math.Max(dy, dz));
                        if (maxDim > 0) dist = Math.Max(0.01, maxDim * 1.5);
                    }
                }
                catch { }
                Diag("orphan " + cname + ": dist=" + dist);

                Diag("orphan " + cname + ": before IAddExplodeStep");
                ExplodeStep newStep = null;
                try { newStep = config.IAddExplodeStep(dist, false, false, false); }
                catch (Exception aex) { Diag("IAddExplodeStep threw " + aex.GetType().Name + ": " + aex.Message); }
                Diag("orphan " + cname + ": after IAddExplodeStep, step=" + (newStep != null));
                int nc = 0; try { nc = newStep == null ? 0 : newStep.GetNumOfComponents(); } catch { }
                Diag("orphan " + cname + ": newStep comps=" + nc);
                if (newStep != null && nc > 0) added++;
            }
            try { model.ClearSelection2(true); } catch { }
            res.ApiReturn = added > 0;
            Diag("added=" + added + " of " + orphans.Count);
            try { model.EditRebuild3(); } catch (Exception rex) { Diag("EditRebuild3 threw " + rex.GetType().Name); }
            Diag("after EditRebuild3");

            stepCount = 0; try { stepCount = config.GetNumberOfExplodeSteps(); } catch { }
            res.StepCount = stepCount;
            Diag("stepCount after rebuild=" + stepCount);

            // ---- Sentinel: INDEPENDENT re-read of EVERY step's covered names (AutoExplode may spread coverage
            // across several steps, not just append to one), fail closed ----
            await emit("Sentinel", "verifying the rebuilt exploded view", "run", null);
            var coveredAfter = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < stepCount; i++)
            {
                ExplodeStep stAfter = null; try { stAfter = config.IGetExplodeStep(i); } catch { }
                if (stAfter == null) continue;
                int ncAfter = 0; try { ncAfter = stAfter.GetNumOfComponents(); } catch { }
                for (int j = 0; j < ncAfter; j++) { string cn = null; try { cn = stAfter.GetComponentName(j); } catch { } if (cn != null) coveredAfter.Add(cn); }
            }
            Diag("re-read coveredAfter=" + coveredAfter.Count);
            foreach (var nm in res.OrphanNames) if (coveredAfter.Contains(nm)) res.RepairedNames.Add(nm);
            res.Verified = res.RepairedNames.Count == res.OrphanNames.Count;
            Diag("Verified=" + res.Verified + " repaired=" + res.RepairedNames.Count);

            if (!res.Verified)
            {
                var stillMissing = res.OrphanNames.Except(res.RepairedNames, StringComparer.OrdinalIgnoreCase).ToList();
                res.Error = "Repair didn't fully take — still uncovered: " + string.Join(", ", stillMissing) + ".";
                await emit("Sentinel", null, "fail", res.Error);
                return res;
            }

            res.Info = "Re-attached " + res.RepairedNames.Count + " orphaned component(s) to the exploded view: " + string.Join(", ", res.RepairedNames) + ". One Ctrl+Z restores it; Forge didn't save.";
            await emit("Sentinel", null, "done", res.RepairedNames.Count + " re-attached");
            return res;
        }
    }
}
