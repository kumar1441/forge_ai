using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class InsertStandardViewsResult
    {
        public bool Verified;
        public bool AlreadyDone;
        public string SourceModelPath;
        public string DrawingPath;
        public int ViewsInserted;
        public List<string> Inserted = new List<string>();
        public List<string> Failed = new List<string>();
        public string Info;
        public string Error;
        public string Question;
        public bool NeedsConfirm;
    }

    /// <summary>
    /// InsertStandardViews — tool 102. Adds front/top/right/isometric views of a part or assembly onto a drawing
    /// sheet in one call, via IDrawingDoc.CreateDrawViewFromModelView3(ModelName, ViewName, LocX, LocY, LocZ)
    /// (ModelName = source's full path, ViewName = a standard name like "*Front"/"*Top"/"*Right"/"*Isometric",
    /// Loc = sheet position in meters — reflected off the redist DLL while building create_drawing, tool 101).
    ///
    /// Narrower and more specific than create_drawing's bare "create a drawing" (requires an explicit view/
    /// standard-views signal), so it's checked FIRST wherever both could match ("create a drawing with standard
    /// views" must land here, not stop at an empty sheet). If no drawing is open yet, reuses CreateDrawing.Run
    /// (not duplicated) to make one referencing whichever part/assembly is currently active, then adds the views
    /// to it. If a drawing IS already open, resolves the source part/assembly from the OTHER currently-open
    /// documents — if there's exactly one, use it; zero or more than one is a genuine ambiguity (Rule #2 — ask,
    /// never guess which model the views are of).
    ///
    /// IDEMPOTENT (Rule #5): a sheet that already has views is left alone — reports already-done instead of
    /// stacking a second set of duplicate views on every rerun. FAIL CLOSED (Rule #6): after inserting, re-reads
    /// the sheet's own view list (not just trusting each CreateDrawViewFromModelView3 return) so "verified" means
    /// the views are actually there. Never saves — same as every other WRITE handler.
    /// </summary>
    public static class InsertStandardViews
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (!Regex.IsMatch(c, @"\b(insert|add|create|make|generate|give\s+me|put)\b")) return false;
            bool viewWord = Regex.IsMatch(c, @"\b(view|views)\b");
            if (!viewWord) return false;
            bool standardWord = Regex.IsMatch(c, @"\bstandard\b|\borthographic\b|\bprojection\b");
            int orthoHits = 0;
            foreach (Match m in Regex.Matches(c, @"\b(front|top|right|left|back|bottom|iso|isometric)\b")) orthoHits++;
            return standardWord || orthoHits >= 2;
        }

        private struct ViewSpec { public string Name; public double X; public double Y; }
        private static readonly ViewSpec[] Views =
        {
            new ViewSpec { Name = "*Front",      X = 0.11, Y = 0.20 },
            new ViewSpec { Name = "*Top",         X = 0.11, Y = 0.06 },
            new ViewSpec { Name = "*Right",       X = 0.25, Y = 0.20 },
            new ViewSpec { Name = "*Isometric",   X = 0.25, Y = 0.06 },
        };

        public static async Task<InsertStandardViewsResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new InsertStandardViewsResult();
            if (model == null) { res.Error = "Open a part, assembly, or drawing first."; return res; }

            IModelDoc2 drawingDoc = null;
            string sourcePath = null;

            bool activeIsDrawing = false;
            try { activeIsDrawing = (int)model.GetType() == (int)swDocumentTypes_e.swDocDRAWING; } catch { }

            if (activeIsDrawing)
            {
                drawingDoc = model;
                try { sourcePath = FindOpenSourceModel(app, out res.Question); } catch { }
                if (sourcePath == null)
                {
                    res.NeedsConfirm = res.Question != null;
                    if (res.Question == null)
                        res.Error = "No part or assembly is open to reference — open the model these views should show first.";
                    return res;
                }
            }
            else
            {
                try { sourcePath = model.GetPathName(); } catch { }
                if (string.IsNullOrEmpty(sourcePath))
                {
                    res.Error = "This model has never been saved — it has no file path for the drawing to reference. Save it first.";
                    return res;
                }
                await emit("Drafter", "no drawing open — creating one first", "run", null);
                var cd = await CreateDrawing.Run(app, model, intent, emit);
                if (cd.Error != null) { res.Error = "Couldn't create a drawing to hold the views: " + cd.Error; return res; }
                drawingDoc = app.IActiveDoc2 as IModelDoc2;
                if (drawingDoc == null || (int)drawingDoc.GetType() != (int)swDocumentTypes_e.swDocDRAWING)
                { res.Error = "A drawing was reported created, but it isn't the active document — can't add views."; return res; }
            }

            res.SourceModelPath = sourcePath;
            try { res.DrawingPath = drawingDoc.GetPathName(); } catch { }

            var dd = drawingDoc as DrawingDoc;
            if (dd == null) { res.Error = "The target document isn't a drawing."; return res; }

            // ---- IDEMPOTENT (Rule #5): a sheet that already carries views is left alone ----
            int existingViews = CountViews(dd);
            if (existingViews > 0)
            {
                res.AlreadyDone = true;
                res.Verified = true;
                res.Info = "This sheet already has " + existingViews + " view(s) — not stacking a duplicate set. Delete them first if you want a fresh layout.";
                await emit("Drafter", null, "done", "already has views — skipped");
                return res;
            }

            await emit("Drafter", "placing front/top/right/isometric views", "run", null);
            foreach (var v in Views)
            {
                View created = null;
                try { created = dd.CreateDrawViewFromModelView3(sourcePath, v.Name, v.X, v.Y, 0) as View; }
                catch { created = null; }
                if (created != null) res.Inserted.Add(v.Name);
                else res.Failed.Add(v.Name);
            }

            try { drawingDoc.ForceRebuild3(false); } catch { }

            // ---- FAIL CLOSED (Rule #6): re-read the sheet's own view list, don't trust the per-call return alone ----
            res.ViewsInserted = CountViews(dd);
            res.Verified = res.ViewsInserted >= Views.Length && res.Failed.Count == 0;

            if (!res.Verified)
            {
                res.Error = "Only " + res.ViewsInserted + " of " + Views.Length + " standard views landed on the sheet" +
                            (res.Failed.Count > 0 ? " (failed: " + string.Join(", ", res.Failed) + ")" : "") +
                            " — SolidWorks may not recognize this source's standard orientations.";
                await emit("Drafter", null, "fail", res.Error);
                return res;
            }

            res.Info = "Added " + res.ViewsInserted + " standard views (front/top/right/isometric) of " +
                       System.IO.Path.GetFileName(sourcePath) + " to the drawing. Forge didn't save.";
            await emit("Drafter", null, "done", res.Info);
            return res;
        }

        // number of real views on the sheet (GetViews()[sheet][0] is the sheet itself, not a view — same shape as
        // GetDrawingViews.cs's enumeration, kept independent/inline here rather than shared so this handler's own
        // idempotency check never depends on another handler's code changing under it).
        private static int CountViews(DrawingDoc dd)
        {
            int count = 0;
            object[] perSheet = null;
            try { perSheet = dd.GetViews() as object[]; } catch { return 0; }
            if (perSheet == null) return 0;
            foreach (var so in perSheet)
            {
                var group = so as object[];
                if (group == null) continue;
                count += Math.Max(0, group.Length - 1);
            }
            return count;
        }

        // exactly one other open part/assembly document -> its path. Zero or more than one is a genuine
        // ambiguity (Rule #2: ask, never guess which model a drawing's views should reference).
        private static string FindOpenSourceModel(ISldWorks app, out string question)
        {
            question = null;
            var candidates = new List<string>();
            object[] docs = null;
            try { docs = app.GetDocuments() as object[]; } catch { }
            if (docs != null)
            {
                foreach (var o in docs)
                {
                    var d = o as IModelDoc2; if (d == null) continue;
                    int t = -1; try { t = (int)d.GetType(); } catch { }
                    if (t != (int)swDocumentTypes_e.swDocPART && t != (int)swDocumentTypes_e.swDocASSEMBLY) continue;
                    string p = null; try { p = d.GetPathName(); } catch { }
                    if (!string.IsNullOrEmpty(p) && !candidates.Contains(p)) candidates.Add(p);
                }
            }
            if (candidates.Count == 1) return candidates[0];
            if (candidates.Count == 0) return null;
            question = "Which open model should these views show — " + string.Join(", ", candidates.ConvertAll(System.IO.Path.GetFileName)) + "?";
            return null;
        }
    }
}
