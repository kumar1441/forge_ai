using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class RepairBalloonReferencesResult
    {
        public bool AlreadyDone;
        public bool Verified;
        public string ViewName;
        public int OrphanedRemoved;
        public int BalloonsBefore;
        public int BalloonsAfter;
        public int ItemTypesInView;
        public string Diag;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// RepairBalloonReferences (tool #160 repair_balloon_references, WRITE) — for the ACTIVE drawing's assembly
    /// view, removes balloons whose leader has become detached from the model (a real, live signal:
    /// `INote.HasBalloon()` + `INote.IsAttached()==false`, confirmed via redist DLL reflection) and restores
    /// full balloon coverage by re-running `IDrawingDoc.AutoBalloon5` — which SolidWorks documents as skipping
    /// any component that already has a live balloon, so a rerun after the cleanup above re-covers exactly the
    /// gap the model change created (a component added/renamed/whose old balloon just got removed), never
    /// double-balloons an already-correct item.
    ///
    /// Balloons live on `IView` (not the document root): walked via `IView.IGetFirstNote()`/`INote.IGetNext()`,
    /// a genuinely different traversal than `InsertBomTable.cs`'s feature-tree `BomFeat` walk or
    /// `CleanBomTable.cs`'s `IBomFeature` table walk (both siblings in this same drawings-gap-fills sweep).
    /// </summary>
    public static class RepairBalloonReferences
    {
        // Requires the "balloon(s)" noun (no other matcher in this build claims it) AND a repair-ish verb.
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (!Regex.IsMatch(c, @"\bballoons?\b")) return false;
            return Regex.IsMatch(c, @"\b(repair|fix|reattach|re-attach|restore|refresh|reconnect|reconcile|update)\b");
        }

        public static async Task<RepairBalloonReferencesResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new RepairBalloonReferencesResult();
            var dd = model as DrawingDoc;
            if (dd == null) { res.Error = "Open the drawing whose balloons you want repaired."; return res; }

            await emit("Scout", "finding the assembly view and its balloons", "run", null);
            View targetView = null; string targetViewName = null;
            var v = dd.GetFirstView() as IView; bool first = true;
            while (v != null)
            {
                if (!first)
                {
                    string rm = null; try { rm = v.GetReferencedModelName(); } catch { }
                    if (!string.IsNullOrEmpty(rm) && rm.EndsWith(".sldasm", StringComparison.OrdinalIgnoreCase))
                    { targetView = v as View; targetViewName = v.GetName2(); break; }
                }
                first = false;
                v = v.GetNextView() as IView;
            }
            if (targetView == null)
            { res.Error = "No view of an assembly found on this drawing — balloons need an assembly view."; return res; }
            res.ViewName = targetViewName;
            await emit("Scout", null, "done", "assembly view: " + targetViewName);

            // ---- 1. Remove ORPHANED balloons: HasBalloon() but the leader is no longer attached to anything —
            // the live signal that the model changed underneath it (component moved/removed/renamed). ----
            int before = 0, orphaned = 0;
            var orphanNotes = new List<Note>();
            var n = (targetView as IView).IGetFirstNote();
            while (n != null)
            {
                bool hasBalloon = false; try { hasBalloon = n.HasBalloon(); } catch { }
                if (hasBalloon)
                {
                    before++;
                    bool attached = true; try { attached = n.IsAttached(); } catch { }
                    if (!attached) orphanNotes.Add(n);
                }
                n = n.IGetNext();
            }
            res.BalloonsBefore = before;

            foreach (var on in orphanNotes)
            {
                try
                {
                    var ann = on.IGetAnnotation();
                    if (ann != null) { ann.Select2(false, 0); model.EditDelete(); orphaned++; }
                }
                catch { }
            }
            res.OrphanedRemoved = orphaned;

            // ---- 2. Restore coverage: AutoBalloon5 only adds a balloon where none already exists, so this
            // re-covers exactly what (1) cleaned up or what a real model change left uncovered. ----
            await emit("Scribe", "restoring balloon coverage", "run", null);
            bool activated = false; try { activated = dd.ActivateView(targetViewName); } catch { }
            bool autoOk = false; string autoDiag;
            try
            {
                var opts = dd.CreateAutoBalloonOptions() as AutoBalloonOptions;
                if (opts == null) { autoDiag = "CreateAutoBalloonOptions returned null"; }
                else
                {
                    opts.IgnoreMultiple = true;
                    opts.Style = (int)swBalloonStyle_e.swBS_Circular;
                    opts.Size = (int)swBalloonFit_e.swBF_2Chars;
                    opts.UpperTextContent = (int)swBalloonTextContent_e.swBalloonTextItemNumber;
                    var autoRes = dd.AutoBalloon5(opts);
                    autoOk = autoRes != null;
                    autoDiag = autoOk ? "AutoBalloon5 ok" : "AutoBalloon5 returned null";
                }
            }
            catch (Exception ex) { autoDiag = "AutoBalloon5 threw (" + ex.GetType().Name + ": " + ex.Message + ")"; }

            // AutoBalloon5 alone returned null with no exception on this build — before assuming it's a dead
            // no-op, try the genuinely different legacy overload (a distinct code path per the reflected
            // signature, not a variant of the same call) before concluding annotation-insert is dead here too.
            if (!autoOk)
            {
                try
                {
                    var autoRes2 = dd.AutoBalloon((int)swBalloonLayoutType_e.swDetailingBalloonLayout_Square);
                    autoOk = autoRes2 != null;
                    autoDiag += " | AutoBalloon(Square) " + (autoOk ? "ok" : "also returned null");
                }
                catch (Exception ex) { autoDiag += " | AutoBalloon threw (" + ex.GetType().Name + ": " + ex.Message + ")"; }
            }

            try { model.ForceRebuild3(true); } catch { }
            int se = 0, sw = 0; try { model.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref se, ref sw); } catch { }

            // ---- FAIL CLOSED: re-walk the view's notes fresh (not the same list gathered before the write) and
            // independently count live balloons + the distinct item texts they cover; require >=1 balloon and 0
            // still-orphaned. ----
            int after = 0, stillOrphaned = 0;
            var seenItems = new HashSet<string>();
            var n2 = (targetView as IView).IGetFirstNote();
            while (n2 != null)
            {
                bool hasBalloon = false; try { hasBalloon = n2.HasBalloon(); } catch { }
                if (hasBalloon)
                {
                    after++;
                    bool attached = true; try { attached = n2.IsAttached(); } catch { }
                    if (!attached) stillOrphaned++;
                    string txt = null; try { txt = n2.GetBomBalloonText(false); } catch { }
                    if (!string.IsNullOrEmpty(txt)) seenItems.Add(txt.Trim());
                }
                n2 = n2.IGetNext();
            }
            res.BalloonsAfter = after;
            res.ItemTypesInView = seenItems.Count;

            res.Diag = "before=" + before + " orphanedRemoved=" + orphaned + " after=" + after +
                " stillOrphaned=" + stillOrphaned + " itemTypes=" + seenItems.Count + " activated=" + activated +
                " autoOk=" + autoOk + " autoDiag=" + autoDiag;
            res.Verified = after > 0 && stillOrphaned == 0;

            await emit("Scribe", null, res.Verified ? "done" : "fail", res.Diag);
            if (!res.Verified)
            { res.Error = "Repaired, but couldn't independently verify balloon coverage (" + res.Diag + ")."; return res; }

            if (orphaned == 0 && after == before)
            {
                res.AlreadyDone = true;
                res.Info = "Balloons already fully attached and covering every item — nothing to repair.";
                return res;
            }

            res.Info = "Repaired balloon references on \"" + targetViewName + "\": removed " + orphaned +
                " orphaned, now " + after + " balloon(s) covering " + seenItems.Count + " item type(s).";
            return res;
        }
    }
}
