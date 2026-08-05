using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class DrawingViewRow
    {
        public string Sheet;
        public string Name;
        public int TypeCode;
        public string Type;              // the enum NAME, resolved at runtime — never a guessed string table
        public string BaseView;          // the view a section/detail was cut from, when there is one
        public double ScaleNumerator;
        public double ScaleDenominator;
        public string ReferencedModel;
        public bool ReferencedModelOnDisk;
        public int DimensionCount;
        public double[] Position;
    }

    public class GetDrawingViewsResult
    {
        public bool IsDrawing;
        public string DrawingPath;
        public int SheetCount;
        public int ViewCount;
        public List<string> Sheets = new List<string>();
        public List<DrawingViewRow> Views = new List<DrawingViewRow>();
        public List<string> MissingModels = new List<string>();   // referenced models that are NOT on disk
        public List<string> UnreadableViews = new List<string>(); // named, never folded into the healthy count
        public int TotalDimensions;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// GetDrawingViews (tool #258 get_drawing_views) — what is actually ON this drawing: every sheet, every view on it,
    /// each view's type, scale, the model it points at, and how many dimensions it carries. The question a shop asks
    /// before touching a released drawing, and the read that every drawing WRITE (insert/delete view, set scale,
    /// import dimensions) needs first in order to name its target.
    ///
    ///   Reader   — walks the sheet/view structure and reports it. Sheets are kept as sheets: the sheet entry that
    ///              SolidWorks returns alongside the real views is NOT counted as a view.
    ///   Sentinel — every referenced model is checked against the DISK. Per the docs/SOLIDWORKS-GOTCHAS.md landmine a missing reference
    ///              is invisible post-open (SolidWorks quietly resolves what it can), so File.Exists is the only honest
    ///              signal available here — a drawing whose model has moved still opens and still draws.
    ///
    /// READ-ONLY: no sheet is activated, no view is selected, nothing is rebuilt, nothing is saved. A view whose
    /// properties cannot be read is named in UnreadableViews (Rule: unmeasurable is never folded into "fine").
    /// </summary>
    public static class GetDrawingViews
    {
        // NARROW: needs a VIEW/SHEET noun and a READ verb, and bails on every drawing WRITE/EXPORT verb so it can never
        // take DrawingPkg's "export the drawings as pdfs" or FlatDxf's flat-pattern work. "sheet" is also the
        // sheet-metal noun, so sheet-metal vocabulary is excluded outright (GetSheetMetalProps owns that).
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (c.Contains("sheet metal") || c.Contains("sheetmetal")) return false;
            if (Regex.IsMatch(c, @"\b(bend|gauge|k-?factor|flat[- ]?pattern|dxf|dwg|laser|nest)\b")) return false;
            if (Regex.IsMatch(c, @"\b(export|pdf|pdfs|print|create|insert|add|delete|remove|rebuild|repair|fix|set|change|update|package)\b")) return false;
            bool noun = Regex.IsMatch(c, @"\b(view|views)\b") || Regex.IsMatch(c, @"\b(sheet|sheets)\b");
            bool drawingScope = Regex.IsMatch(c, @"\b(drawing|drawings|drw|slddrw|sheet|sheets)\b");
            bool read = Regex.IsMatch(c, @"\b(list|show|what|which|how many|tell|report|get|describe|inspect|on)\b");
            return noun && drawingScope && read;
        }

        public static async Task<GetDrawingViewsResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new GetDrawingViewsResult();
            if (model == null) { res.Error = "Open a drawing first."; return res; }

            var dd = model as DrawingDoc;
            if (dd == null)
            {
                res.IsDrawing = false;
                string what = "document";
                try { what = (int)model.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY ? "assembly" : "part"; } catch { }
                res.Error = "This is " + (what == "assembly" ? "an " : "a ") + what + ", not a drawing — there are no sheets or views to read. Open the .SLDDRW.";
                return res;
            }
            res.IsDrawing = true;
            try { res.DrawingPath = model.GetPathName(); } catch { }
            string dir = null;
            try { dir = System.IO.Path.GetDirectoryName(res.DrawingPath); } catch { }

            await emit("Reader", "reading the drawing's sheets and views", "run", null);

            // GetViews() returns one array PER SHEET; element 0 of each is the SHEET itself, the rest are its views.
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
                res.Sheets.Add(sheetName);

                for (int k = 1; k < group.Length; k++)
                {
                    var v = group[k] as IView;
                    if (v == null) { res.UnreadableViews.Add(sheetName + " / view " + k); continue; }
                    var row = new DrawingViewRow { Sheet = sheetName };
                    try { row.Name = v.Name; } catch { }
                    if (row.Name == null) { res.UnreadableViews.Add(sheetName + " / view " + k); continue; }
                    try
                    {
                        row.TypeCode = v.Type;
                        row.Type = Enum.GetName(typeof(swDrawingViewTypes_e), row.TypeCode) ?? ("type " + row.TypeCode);
                    }
                    catch { row.Type = "unreadable"; }
                    try { var sc = v.ScaleRatio as double[]; if (sc != null && sc.Length >= 2) { row.ScaleNumerator = sc[0]; row.ScaleDenominator = sc[1]; } } catch { }
                    try { row.ReferencedModel = v.GetReferencedModelName(); } catch { }
                    try { row.Position = v.Position as double[]; } catch { }
                    try { var dims = v.GetDisplayDimensions() as object[]; row.DimensionCount = dims == null ? 0 : dims.Length; } catch { }
                    try { var bv = v.GetBaseView() as IView; if (bv != null) row.BaseView = bv.Name; } catch { }

                    if (!string.IsNullOrEmpty(row.ReferencedModel))
                    {
                        string p = row.ReferencedModel;
                        try
                        {
                            if (!System.IO.Path.IsPathRooted(p) && dir != null) p = System.IO.Path.Combine(dir, p);
                            row.ReferencedModelOnDisk = System.IO.File.Exists(p);
                        }
                        catch { row.ReferencedModelOnDisk = false; }
                        if (!row.ReferencedModelOnDisk && !res.MissingModels.Contains(row.ReferencedModel))
                            res.MissingModels.Add(row.ReferencedModel);
                    }

                    res.TotalDimensions += row.DimensionCount;
                    res.Views.Add(row);
                }
            }

            res.SheetCount = res.Sheets.Count;
            res.ViewCount = res.Views.Count;

            await emit("Sentinel", "checking each referenced model against the disk", "run", null);

            if (res.ViewCount == 0)
            {
                res.Info = res.SheetCount + (res.SheetCount == 1 ? " sheet" : " sheets") + ", but no views on it — the drawing is empty.";
                await emit("Sentinel", null, "done", res.Info);
                return res;
            }

            var kinds = new Dictionary<string, int>();
            foreach (var v in res.Views) { string k = v.Type ?? "unknown"; kinds[k] = kinds.ContainsKey(k) ? kinds[k] + 1 : 1; }
            var parts = new List<string>();
            foreach (var kv in kinds) parts.Add(kv.Value + " " + kv.Key);

            res.Info = res.ViewCount + (res.ViewCount == 1 ? " view" : " views") + " on " +
                       res.SheetCount + (res.SheetCount == 1 ? " sheet" : " sheets") +
                       " (" + string.Join(", ", parts.ToArray()) + "), " + res.TotalDimensions + " dimensions.";
            if (res.MissingModels.Count > 0)
                res.Info += " " + res.MissingModels.Count + " referenced model" + (res.MissingModels.Count == 1 ? " is" : "s are") +
                            " NOT on disk: " + string.Join(", ", res.MissingModels.ToArray()) + " — those views are drawing stale geometry.";
            if (res.UnreadableViews.Count > 0)
                res.Info += " " + res.UnreadableViews.Count + " view(s) could not be read: " + string.Join(", ", res.UnreadableViews.ToArray()) + ".";

            await emit("Sentinel", null, "done", res.Info);
            return res;
        }
    }
}
