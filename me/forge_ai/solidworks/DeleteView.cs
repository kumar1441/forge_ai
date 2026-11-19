using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class DeleteViewResult
    {
        public bool Verified;
        public bool AlreadyDone;
        public bool NeedsConfirm;
        public string Question;
        public string ViewLabel;
        public string ViewInternalName;
        public int ViewCountBefore;
        public int ViewCountAfter;
        public int RebuildErrors;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// DeleteView — tool 106 (WRITE). "Delete the isometric view" / "remove the top view". Permanently removes ONE
    /// view from the CURRENT drawing sheet. Unlike InsertStandardViews/SetViewScale, this does NOT bootstrap a
    /// drawing — deleting a view that was never asked to exist is nonsensical, so it hard-requires an already-open
    /// drawing with views.
    ///
    /// No IDrawingDoc/IView "DeleteView" method exists on this interop (reflected, confirmed absent) — views are
    /// removed the same way DeleteFeature.cs removes features: SELECT the target (IModelDocExtension.SelectByID2
    /// with the "DRAWINGVIEW" selection type, keyed by the view's own internal Name — "Drawing View1" etc, NOT the
    /// orientation) then IModelDocExtension.DeleteSelection2 with the NO-PROMPT option bits (the "delete with
    /// dependents?" modal HANGS headless — same landmine DeleteFeature.cs already worked around).
    ///
    /// Resolves the target view by ORIENTATION WORD (front/top/right/left/back/bottom/isometric) against
    /// View.GetOrientationName() — the same resolution SetViewScale uses, kept as an independent inline copy here
    /// (not shared) so this handler's own idempotency logic never depends on another handler's code changing under
    /// it. IDEMPOTENT (Rule #5): if the named view is already gone, that's the delete's goal state already
    /// achieved — reports AlreadyDone, never an error and never a re-ask (mirrors DeleteFeature's "zero matches on
    /// a RECOGNIZED type is a clean no-op"). Genuinely ambiguous only when NO orientation word was given and more
    /// than one view remains — that's a real Rule #2 ask. FAIL CLOSED (Rule #6): re-enumerates the sheet's own view
    /// list post-delete; verified only when the count dropped by exactly 1 AND the specific view's internal name is
    /// gone.
    /// </summary>
    public static class DeleteView
    {
        private const int DELETE_NOPROMPT =
            (int)swDeleteSelectionOptions_e.swDelete_Children | (int)swDeleteSelectionOptions_e.swDelete_Absorbed;

        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            bool viewWord = Regex.IsMatch(c, @"\bview\b|\bviews\b|\b(front|top|right|left|back|bottom|isometric|iso)\b");
            if (!viewWord) return false;
            return Regex.IsMatch(c, @"\b(delete|remove|get rid of|erase)\b");
        }

        public static async Task<DeleteViewResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new DeleteViewResult();
            if (model == null) { res.Error = "Open a part, assembly, or drawing first."; return res; }

            // The passed `model` handle can be STALE relative to the true active document — e.g. a chained command
            // ("create a drawing with standard views, then delete the isometric view") switches the active doc to a
            // brand-new drawing that `model` never points at. Fall back to the app's own active document when the
            // handed-in model isn't a drawing, exactly like InsertStandardViews does when resolving its source model.
            IModelDoc2 drawingDoc = model;
            bool isDrawing = false;
            try { isDrawing = (int)drawingDoc.GetType() == (int)swDocumentTypes_e.swDocDRAWING; } catch { }
            if (!isDrawing)
            {
                IModelDoc2 active = null;
                try { active = app.IActiveDoc2 as IModelDoc2; } catch { }
                bool activeIsDrawing = false;
                try { activeIsDrawing = active != null && (int)active.GetType() == (int)swDocumentTypes_e.swDocDRAWING; } catch { }
                if (activeIsDrawing) { drawingDoc = active; isDrawing = true; }
            }
            if (!isDrawing) { res.Error = "Open a drawing with views first — nothing to delete here."; return res; }

            var dd = drawingDoc as DrawingDoc;
            if (dd == null) { res.Error = "The active document isn't a drawing."; return res; }

            var views = EnumerateViews(dd);
            res.ViewCountBefore = views.Count;
            if (views.Count == 0) { res.AlreadyDone = true; res.Verified = true; res.Info = "No views on this sheet — nothing to delete."; return res; }

            string targetWord = ParseTargetOrientation(intent);
            View target = null;
            string resolvedLabel = null;

            if (targetWord != null)
            {
                foreach (var (v, name, orientation) in views)
                {
                    string o = (orientation ?? "").TrimStart('*').ToLowerInvariant();
                    if (o == targetWord || o.Contains(targetWord)) { target = v; resolvedLabel = orientation ?? name; break; }
                }
                if (target == null)
                {
                    res.AlreadyDone = true;
                    res.Verified = true;
                    res.Info = "No " + targetWord + " view found — already removed, or never existed.";
                    return res;
                }
            }
            else if (views.Count == 1)
            {
                target = views[0].Item1;
                resolvedLabel = views[0].Item3 ?? views[0].Item2;
            }
            else
            {
                var names = new List<string>();
                foreach (var (_, name, orientation) in views) names.Add(orientation ?? name);
                res.NeedsConfirm = true;
                res.Question = "Which view should be deleted — " + string.Join(", ", names) + "?";
                return res;
            }

            res.ViewLabel = resolvedLabel;
            try { res.ViewInternalName = target.Name; } catch { }

            await emit("Gauge", "selecting the " + (res.ViewLabel ?? res.ViewInternalName) + " view", "run", null);
            bool selected = false;
            try { selected = drawingDoc.Extension.SelectByID2(res.ViewInternalName, "DRAWINGVIEW", 0, 0, 0, false, 0, null, 0); } catch { }
            if (!selected) { res.Error = "Couldn't select the " + (res.ViewLabel ?? res.ViewInternalName) + " view to delete."; return res; }

            await emit("Reaper", "deleting the view", "run", null);
            try { drawingDoc.Extension.DeleteSelection2(DELETE_NOPROMPT); } catch (Exception ex) { res.Error = "Delete failed: " + ex.Message; return res; }
            try { drawingDoc.ForceRebuild3(false); } catch { }

            // ---- FAIL CLOSED: re-enumerate the sheet's own view list, don't trust the select/delete return alone ----
            await emit("Sentinel", "verifying", "run", null);
            var after = EnumerateViews(dd);
            res.ViewCountAfter = after.Count;
            bool stillPresent = false;
            foreach (var (_, name, _) in after) { if (name == res.ViewInternalName) { stillPresent = true; break; } }
            try { res.RebuildErrors = drawingDoc.Extension.GetWhatsWrongCount(); } catch { }
            res.Verified = !stillPresent && res.ViewCountAfter == res.ViewCountBefore - 1;

            if (!res.Verified)
            {
                res.Error = "Expected " + (res.ViewCountBefore - 1) + " views after deleting " + (res.ViewLabel ?? res.ViewInternalName) +
                            ", but " + res.ViewCountAfter + " remain" + (stillPresent ? " (the target view is still there)" : "") + ".";
                await emit("Sentinel", null, "fail", res.Error);
                return res;
            }

            res.Info = "Deleted the " + (res.ViewLabel ?? res.ViewInternalName) + " view (" + res.ViewCountBefore + " -> " + res.ViewCountAfter + " views). Forge didn't save.";
            await emit("Sentinel", null, "done", res.Info);
            return res;
        }

        private static List<(View, string, string)> EnumerateViews(DrawingDoc dd)
        {
            var result = new List<(View, string, string)>();
            object[] perSheet = null;
            try { perSheet = dd.GetViews() as object[]; } catch { return result; }
            if (perSheet == null) return result;
            foreach (var so in perSheet)
            {
                var group = so as object[];
                if (group == null) continue;
                for (int k = 1; k < group.Length; k++)
                {
                    var v = group[k] as View;
                    if (v == null) continue;
                    string name = null; try { name = v.Name; } catch { }
                    string orientation = null; try { orientation = v.GetOrientationName(); } catch { }
                    result.Add((v, name, orientation));
                }
            }
            return result;
        }

        private static string ParseTargetOrientation(string intent)
        {
            string c = (intent ?? "").ToLowerInvariant();
            if (Regex.IsMatch(c, @"\bisometric\b|\biso\b")) return "isometric";
            if (Regex.IsMatch(c, @"\bfront\b")) return "front";
            if (Regex.IsMatch(c, @"\btop\b")) return "top";
            if (Regex.IsMatch(c, @"\bright\b")) return "right";
            if (Regex.IsMatch(c, @"\bleft\b")) return "left";
            if (Regex.IsMatch(c, @"\bback\b")) return "back";
            if (Regex.IsMatch(c, @"\bbottom\b")) return "bottom";
            return null;
        }
    }
}
