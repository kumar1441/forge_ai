using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class DanglingDimRow
    {
        public string Sheet;
        public string ViewName;
        public string DimName;   // Dimension.FullName, e.g. "D1@Seed-Hole"
    }

    public class ListDanglingDimensionsResult
    {
        public bool IsDrawing;
        public string DrawingPath;
        public int TotalDimensions;
        public int DanglingCount;
        public List<DanglingDimRow> Dangling = new List<DanglingDimRow>();
        public string Info;
        public string Error;
    }

    /// <summary>
    /// ListDanglingDimensions — tool 110 (READ). "List the dangling dimensions" / "which dims are broken after the
    /// model change". Walks every view on the current drawing, checks each DisplayDimension's own
    /// IAnnotation.IsDangling() (the same primitive DrawingPkg.CountDangling already uses for its batch-repair
    /// count), and reports each dangling dim BY NAME (Dimension.FullName via GetDimension2(0)) rather than just a
    /// count — the read repair_dangling_dimension (tool 111, not yet built) will need to name its target.
    ///
    /// A drawing whose referencing model changed since the last rebuild won't show it until rebuilt — this handler
    /// calls ForceRebuild3 itself first (READ-only in effect: no geometry is touched, only the drawing's own
    /// dangling-annotation state is refreshed) so "0 dangling" is never a stale false negative.
    ///
    /// Matcher requires the literal "dangling"/"broken" word + a dimension noun, and excludes every write verb
    /// (repair/fix/reattach/rebuild/export/pdf/package/batch/import/pull/bring/create/insert/add/delete/remove/
    /// set/change/update) so it can never collide with DrawingPkg's batch rebuild-repair-export (demo #9),
    /// import_model_dimensions, or the not-yet-built repair_dangling_dimension. Placed BEFORE GetDrawingViews/
    /// DrawingPkg in dispatch (first-match-wins) since both would otherwise also fire on "dangling dims on this
    /// drawing" phrasing that happens to include a view/sheet/drawing noun.
    /// </summary>
    public static class ListDanglingDimensions
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (Regex.IsMatch(c, @"\b(repair|fix|reattach|rebuild|export|pdf|pdfs|package|batch|import|pull|bring|create|insert|add|delete|remove|set|change|update)\b")) return false;
            bool danglingWord = Regex.IsMatch(c, @"\b(dangling|broken)\b");
            if (!danglingWord) return false;
            return Regex.IsMatch(c, @"\bdim\b|\bdims\b|\bdimension\b|\bdimensions\b");
        }

        public static async Task<ListDanglingDimensionsResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new ListDanglingDimensionsResult();
            if (model == null) { res.Error = "Open a drawing first."; return res; }

            // Same stale-handle fix InsertView/ImportModelDimensions needed: the passed model can lag the true
            // active document (e.g. a prior chained command bootstrapped a drawing that's now active).
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

            if (!isDrawing)
            {
                res.IsDrawing = false;
                res.Error = "Open a drawing first — there are no dimensions to check on a part or assembly.";
                return res;
            }
            res.IsDrawing = true;
            try { res.DrawingPath = drawingDoc.GetPathName(); } catch { }

            var dd = drawingDoc as DrawingDoc;
            if (dd == null) { res.Error = "The active document isn't a drawing."; return res; }

            // A model edit upstream (e.g. a suppressed feature) only shows up as dangling AFTER a rebuild — never
            // trust a stale drawing state (READ-only in effect: refreshes annotation state, touches no geometry).
            await emit("Reader", "rebuilding the drawing and scanning for dangling dimensions", "run", null);
            try { drawingDoc.ForceRebuild3(false); } catch { }

            object[] perSheet = null;
            try { perSheet = dd.GetViews() as object[]; } catch (Exception ex) { res.Error = "Could not read the drawing's views: " + ex.Message; return res; }
            if (perSheet == null) { res.Error = "SolidWorks returned no view structure for this drawing."; return res; }

            foreach (var so in perSheet)
            {
                var group = so as object[];
                if (group == null || group.Length == 0) continue;
                string sheetName = null;
                try { sheetName = (group[0] as IView).Name; } catch { }
                if (sheetName == null) sheetName = "(unnamed sheet)";

                for (int k = 1; k < group.Length; k++)
                {
                    var v = group[k] as IView;
                    if (v == null) continue;
                    string viewName = null; try { viewName = v.Name; } catch { }

                    object[] dims = null;
                    try { dims = v.GetDisplayDimensions() as object[]; } catch { }
                    if (dims == null) continue;

                    foreach (var o in dims)
                    {
                        var ddim = o as DisplayDimension;
                        if (ddim == null) continue;
                        res.TotalDimensions++;

                        bool dangling = false;
                        try { var ann = ddim.GetAnnotation() as IAnnotation; if (ann != null) dangling = ann.IsDangling(); } catch { }
                        if (!dangling) continue;

                        string dimName = null;
                        try { var d = ddim.GetDimension2(0) as Dimension; if (d != null) dimName = d.FullName; } catch { }

                        res.DanglingCount++;
                        res.Dangling.Add(new DanglingDimRow { Sheet = sheetName, ViewName = viewName, DimName = dimName ?? "(unnamed)" });
                    }
                }
            }

            res.Info = res.DanglingCount == 0
                ? res.TotalDimensions + " dimension(s) on this drawing, none dangling — every one still resolves to real geometry."
                : res.DanglingCount + " of " + res.TotalDimensions + " dimension(s) are dangling: " +
                  string.Join(", ", res.Dangling.ConvertAll(r => r.DimName + " (" + r.ViewName + ")").ToArray());

            await emit("Reader", null, "done", res.Info);
            return res;
        }
    }
}
