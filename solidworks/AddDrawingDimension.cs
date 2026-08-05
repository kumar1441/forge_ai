using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class AddDrawingDimensionResult
    {
        public bool Verified;
        public bool AlreadyDone;
        public string DrawingPath;
        public string ViewName;
        public string DimensionName;
        public double ValueMm;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// AddDrawingDimension — tool 109. A manual linear dimension between two EXISTING model entities shown in a
    /// drawing view (distinct from tools 104/105, which sketch and select a FRESH line/circle — this one selects
    /// real geometry already there). Finds the base view's straight edges via IView.GetVisibleEntities2(null,
    /// swViewEntityType_Edge), classifies each by direction (start/end IVertex.GetPoint(), a completely model-
    /// space computation — no view/sheet transform needed since only relative parallelism/separation matters),
    /// and picks the most-separated PARALLEL pair (the two edges most likely to be a real dimension a person would
    /// draw — e.g. the plate's left/right or top/bottom edge). Selects both (Entity.Select4, second Append=true),
    /// then IModelDoc2.AddDimension2(X, Y, Z) — confirmed live via redist DLL reflection, the same select-then-
    /// call idiom as 104/105's CreateSectionViewAt5/CreateDetailViewAt3, and the same idiom
    /// the test fixture generator's MakeDimensionedHole already used for a PART sketch dimension (just without
    /// the InsertSketch bracket — dimensioning existing model edges on a drawing needs no active sketch at all).
    ///
    /// IDEMPOTENT (Rule #5): the created dimension is renamed to a fixed marker (MarkerName) via
    /// IDimension.Name — if a display dimension with that name already exists anywhere on the sheet, skip (never
    /// stacks a second manual dimension on rerun). FAIL CLOSED (Rule #6): re-walks the sheet's OWN display
    /// dimensions (GetFirstView/GetNextView + GetDisplayDimensions) for the marker name rather than trusting the
    /// AddDimension2 return object alone. Never saves.
    /// </summary>
    public static class AddDrawingDimension
    {
        private const string MarkerName = "ForgeAddedDim";

        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (Regex.IsMatch(c, @"\b(package|pdf|export|rebuild|batch|dxf|dwg|flat[- ]?pattern)\b")) return false;
            if (Regex.IsMatch(c, @"\b(dangling|broken|repair|reattach)\b")) return false;   // tools 110/111
            if (Regex.IsMatch(c, @"\bmodel\b")) return false;                               // import_model_dimensions (108) owns "model"
            if (Regex.IsMatch(c, @"\b(to|as|called)\b")) return false;                      // rename_dimension's clause
            bool verb = Regex.IsMatch(c, @"\b(insert|add|create|put|draw)\b");
            if (!verb) return false;
            bool dimWord = Regex.IsMatch(c, @"\bdim\b|\bdims\b|\bdimension\b|\bdimensions\b");
            if (!dimWord) return false;
            if (Regex.IsMatch(c, @"(?<![a-z0-9])\d+(\.\d+)?")) return false;                // a standalone value is set_dimension's territory
            return true;
        }

        public static async Task<AddDrawingDimensionResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new AddDrawingDimensionResult();
            if (model == null) { res.Error = "Open a drawing with at least one view first."; return res; }

            bool isDrawing = false;
            try { isDrawing = (int)model.GetType() == (int)swDocumentTypes_e.swDocDRAWING; } catch { }
            if (!isDrawing) { res.Error = "This isn't a drawing — open the .SLDDRW with the view you want to dimension."; return res; }

            var dd = model as IDrawingDoc;
            if (dd == null) { res.Error = "Couldn't access the drawing."; return res; }
            try { res.DrawingPath = model.GetPathName(); } catch { }

            await emit("Dimensioner", "checking for an existing manual dimension", "run", null);

            // ---- IDEMPOTENT (Rule #5): a marker-named display dimension already on the sheet means skip ----
            IView baseView = null;
            var v0 = dd.GetFirstView() as IView;
            while (v0 != null)
            {
                int t = -1; try { t = v0.Type; } catch { }
                if (t != (int)swDrawingViewTypes_e.swDrawingSheet && baseView == null) baseView = v0;
                if (HasMarker(v0)) { res.AlreadyDone = true; }
                v0 = v0.GetNextView() as IView;
            }

            if (res.AlreadyDone)
            {
                res.Verified = true;
                res.DimensionName = MarkerName;
                res.Info = "This sheet already has a manually-added dimension (" + MarkerName + ") — not stacking a duplicate.";
                await emit("Dimensioner", null, "done", res.Info);
                return res;
            }

            if (baseView == null) { res.Error = "No view on this sheet to dimension — insert a standard view first."; return res; }
            try { res.ViewName = baseView.Name; } catch { }

            bool activated = false;
            try { activated = dd.ActivateView(res.ViewName); } catch { }
            if (!activated) { res.Error = "Couldn't activate view '" + res.ViewName + "' to dimension it."; return res; }

            await emit("Dimensioner", "finding a parallel edge pair", "run", null);

            Edge e1 = null, e2 = null;
            try { FindBestParallelPair(baseView, out e1, out e2); } catch (Exception ex) { res.Error = "Couldn't read the view's edges: " + ex.Message; return res; }
            if (e1 == null || e2 == null) { res.Error = "Couldn't find two parallel straight edges on '" + res.ViewName + "' to dimension between."; return res; }

            try
            {
                model.ClearSelection2(true);
                ((Entity)e1).Select4(false, null);
                ((Entity)e2).Select4(true, null);
            }
            catch (Exception ex) { res.Error = "Couldn't select the edge pair: " + ex.Message; return res; }

            await emit("Dimensioner", "placing the dimension", "run", null);
            double[] outline = null;
            try { outline = baseView.GetOutline() as double[]; } catch { }
            double placeX = outline != null && outline.Length >= 4 ? outline[2] + 0.03 : 0.03;
            double placeY = outline != null && outline.Length >= 4 ? (outline[1] + outline[3]) / 2.0 : 0.03;

            DisplayDimension createdDim = null;
            try { createdDim = model.AddDimension2(placeX, placeY, 0) as DisplayDimension; }
            catch (Exception ex) { res.Error = "AddDimension2 failed: " + ex.Message; }

            if (createdDim == null)
            {
                try { model.ClearSelection2(true); } catch { }
                if (res.Error == null) res.Error = "SolidWorks didn't create a dimension between the selected edges.";
                await emit("Dimensioner", null, "fail", res.Error);
                return res;
            }

            try
            {
                var dim = createdDim.GetDimension2(0) as Dimension;
                if (dim != null)
                {
                    dim.Name = MarkerName;
                    try { res.ValueMm = dim.SystemValue * 1000.0; } catch { }
                }
            }
            catch { }
            try { model.ForceRebuild3(false); } catch { }

            // ---- FAIL CLOSED: re-walk the sheet's OWN views for the marker name, don't trust the return alone ----
            bool found = false;
            var fv = dd.GetFirstView() as IView;
            while (fv != null) { if (HasMarker(fv)) { found = true; break; } fv = fv.GetNextView() as IView; }

            res.Verified = found;
            res.DimensionName = MarkerName;
            if (!res.Verified)
            {
                res.Error = "A dimension object was returned, but the sheet's display dimensions don't show '" + MarkerName + "' after rebuild.";
                await emit("Dimensioner", null, "fail", res.Error);
                return res;
            }

            res.Info = "Added a dimension (" + MarkerName + (res.ValueMm > 0 ? ", " + Math.Round(res.ValueMm, 2) + "mm" : "") + ") between two edges on '" + res.ViewName + "'. Forge didn't save.";
            await emit("Dimensioner", null, "done", res.Info);
            return res;
        }

        private static bool HasMarker(IView v)
        {
            object[] dims = null;
            try { dims = v.GetDisplayDimensions() as object[]; } catch { return false; }
            if (dims == null) return false;
            foreach (var o in dims)
            {
                var d = o as DisplayDimension; if (d == null) continue;
                try
                {
                    var dim = d.GetDimension2(0) as Dimension;
                    if (dim != null && dim.Name == MarkerName) return true;
                }
                catch { }
            }
            return false;
        }

        // Walks the view's straight edges, classifies each by unit direction + midpoint (model space — no view/
        // sheet transform needed, only relative parallelism/separation matters), and returns the parallel pair
        // with the largest perpendicular separation (the most meaningful overall dimension, e.g. a plate's
        // left/right or top/bottom edge, not two edges of the same small feature).
        private static void FindBestParallelPair(IView view, out Edge best1, out Edge best2)
        {
            best1 = null; best2 = null;
            object[] ents = null;
            try { ents = view.GetVisibleEntities2(null, (int)swViewEntityType_e.swViewEntityType_Edge) as object[]; } catch { }
            if (ents == null || ents.Length < 2) return;

            var dirs = new List<double[]>();     // [dx,dy,dz, mx,my,mz]
            var edges = new List<Edge>();
            foreach (var o in ents)
            {
                var e = o as Edge; if (e == null) continue;
                var dir = EdgeDirAndMid(e);
                if (dir == null) continue;
                dirs.Add(dir);
                edges.Add(e);
            }

            double bestSep = -1;
            for (int i = 0; i < edges.Count; i++)
            {
                for (int j = i + 1; j < edges.Count; j++)
                {
                    var a = dirs[i]; var b = dirs[j];
                    // cross product magnitude ~0 => parallel
                    double cx = a[1] * b[2] - a[2] * b[1];
                    double cy = a[2] * b[0] - a[0] * b[2];
                    double cz = a[0] * b[1] - a[1] * b[0];
                    double crossMag = Math.Sqrt(cx * cx + cy * cy + cz * cz);
                    if (crossMag > 0.02) continue; // not parallel enough

                    double sx = b[3] - a[3], sy = b[4] - a[4], sz = b[5] - a[5];
                    double sep = Math.Sqrt(sx * sx + sy * sy + sz * sz);
                    if (sep < 0.001) continue; // same/coincident edge — not a meaningful pair
                    if (sep > bestSep) { bestSep = sep; best1 = edges[i]; best2 = edges[j]; }
                }
            }
        }

        private static double[] EdgeDirAndMid(Edge e)
        {
            Curve curve = null;
            try { curve = e.GetCurve() as Curve; } catch { }
            if (curve == null) return null;
            bool isLine = false;
            try { isLine = curve.IsLine(); } catch { }
            if (!isLine) return null;

            var sv = e.GetStartVertex() as Vertex;
            var ev = e.GetEndVertex() as Vertex;
            if (sv == null || ev == null) return null;
            var p0 = sv.GetPoint() as double[];
            var p1 = ev.GetPoint() as double[];
            if (p0 == null || p1 == null || p0.Length < 3 || p1.Length < 3) return null;

            double dx = p1[0] - p0[0], dy = p1[1] - p0[1], dz = p1[2] - p0[2];
            double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (len < 1e-9) return null;
            return new[] { dx / len, dy / len, dz / len, (p0[0] + p1[0]) / 2.0, (p0[1] + p1[1]) / 2.0, (p0[2] + p1[2]) / 2.0 };
        }
    }
}
