using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public class ArrangeDrawingAnnotationsResult
    {
        public int TotalDimensions;
        public int OverlapsBefore;
        public int OverlapsAfter;
        public int RepositionedCount;
        public bool AlreadyDone;
        public bool Verified;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 190 — arrange_drawing_annotations (WRITE). Declutters overlapping dimension annotations on a
    /// drawing — "arrange the drawing annotations", "declutter these dimensions", "space out the overlapping
    /// dims". No direct SolidWorks "auto-declutter" API exists on this build (reflected: no hit-test/bbox method
    /// on `IAnnotation`), so this is a from-scratch layout algorithm: walk every dimension's position
    /// (`IAnnotation.GetPosition`), treat any two within 1mm of each other as "overlapping" (a proximity proxy —
    /// the closest thing to a real text-bbox hit-test available), and nudge the later one down by a fixed 8mm
    /// step, repeating until it clears every already-placed annotation, via the confirmed-live
    /// `IAnnotation.SetPosition(X,Y,Z)`. `IsIntent` requires an arrange/declutter/space/reposition/spread/tidy/
    /// organize verb AND the annotation/dimension/label noun — excludes "bom"/"balloon"/"table" so it stays
    /// disjoint from `CleanBomTable`/`RepairBalloonReferences`/`UpdateRevisionTable`, and excludes check/audit/
    /// lint/validate/scan verbs so it stays disjoint from `CheckDraftingStandards` (a READ-only lint, this is a
    /// WRITE fix). Overlap distance is X/Y ONLY — confirmed live that `SetPosition`'s Z argument is a no-op on
    /// this build (a dimension's Z stays pinned to whatever its owning view established; only X/Y actually move),
    /// and annotation position is fundamentally a 2D sheet-space concept anyway. Verified by an INDEPENDENT
    /// re-read of every position, re-counting overlaps from scratch — never the handler's own running tally.
    /// Undoable (one Ctrl+Z); Forge never saves.
    /// </summary>
    public static class ArrangeDrawingAnnotations
    {
        private const double OverlapThreshM = 0.001;   // 1mm — proximity proxy for "visually overlapping"
        private const double NudgeM = 0.008;            // 8mm per step, clearly visible on a real drawing

        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (Regex.IsMatch(c, @"\b(bom|balloon|revision table)\b")) return false;
            if (Regex.IsMatch(c, @"\b(check|audit|lint|validate|scan|verify)\b")) return false;
            bool verb = Regex.IsMatch(c, @"\b(arrange|declutter|de-clutter|space out|reposition|spread out|tidy|organize|organise)\b");
            bool noun = Regex.IsMatch(c, @"\b(annotation|dimension|label)s?\b");
            return verb && noun;
        }

        public static async Task<ArrangeDrawingAnnotationsResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new ArrangeDrawingAnnotationsResult();
            var dd = model as DrawingDoc;
            if (dd == null) { res.Error = "Open the drawing whose annotations need arranging."; return res; }

            await emit("Gauge", "reading dimension positions", "run", null);
            var anns = CollectDimensionAnnotations(dd);
            res.TotalDimensions = anns.Count;
            var before = ReadPositions(anns);
            res.OverlapsBefore = CountOverlaps(before);
            await emit("Gauge", null, "done", res.TotalDimensions + " dimension(s), " + res.OverlapsBefore + " overlapping pair(s)");

            if (res.OverlapsBefore == 0)
            {
                res.AlreadyDone = true; res.Verified = true;
                res.Info = "No overlapping annotations found — nothing to arrange.";
                await emit("Sentinel", null, "done", "already clean");
                return res;
            }

            await emit("Scribe", "spacing out " + res.OverlapsBefore + " overlapping pair(s)", "run", null);
            var placed = new List<double[]>();
            for (int i = 0; i < anns.Count; i++)
            {
                var p = before[i];
                if (p == null) continue;
                bool moved = false;
                int guard = 0;
                while (TooClose(p, placed) && guard++ < 50) { p = new[] { p[0], p[1] - NudgeM, p[2] }; moved = true; }
                if (moved)
                {
                    bool ok = false; try { ok = anns[i].SetPosition(p[0], p[1], p[2]); } catch { }
                    if (ok) res.RepositionedCount++;
                }
                placed.Add(p);
            }
            try { model.ForceRebuild3(false); } catch { }

            // ---- Sentinel: INDEPENDENT re-read of every position, re-count overlaps from scratch ----
            await emit("Sentinel", "verifying the layout", "run", null);
            var annsAfter = CollectDimensionAnnotations(dd);
            var after = ReadPositions(annsAfter);
            res.OverlapsAfter = CountOverlaps(after);
            res.Verified = res.OverlapsAfter == 0 && res.RepositionedCount > 0;

            if (!res.Verified)
            {
                res.Error = "Arrange didn't fully resolve overlaps — " + res.OverlapsBefore + " -> " + res.OverlapsAfter + " overlapping pair(s), " + res.RepositionedCount + " repositioned.";
                await emit("Sentinel", null, "fail", res.Error);
                return res;
            }

            res.Info = "Repositioned " + res.RepositionedCount + " overlapping annotation(s) — 0 overlapping pairs remain. One Ctrl+Z restores the layout; Forge didn't save.";
            await emit("Sentinel", null, "done", res.RepositionedCount + " repositioned, 0 overlaps left");
            return res;
        }

        private static List<IAnnotation> CollectDimensionAnnotations(DrawingDoc dd)
        {
            var list = new List<IAnnotation>();
            var view = dd.GetFirstView() as IView;
            while (view != null)
            {
                object[] dims = null;
                try { dims = view.GetDisplayDimensions() as object[]; } catch { }
                if (dims != null)
                    foreach (var o in dims)
                    {
                        var ddim = o as DisplayDimension; if (ddim == null) continue;
                        var ann = ddim.GetAnnotation() as IAnnotation;
                        if (ann != null) list.Add(ann);
                    }
                view = view.GetNextView() as IView;
            }
            return list;
        }

        private static List<double[]> ReadPositions(List<IAnnotation> anns)
        {
            var list = new List<double[]>();
            foreach (var a in anns) { double[] p = null; try { p = a.GetPosition() as double[]; } catch { } list.Add(p); }
            return list;
        }

        private static int CountOverlaps(List<double[]> positions)
        {
            int n = 0;
            for (int i = 0; i < positions.Count; i++)
            {
                var pi = positions[i]; if (pi == null || pi.Length < 2) continue;
                for (int j = i + 1; j < positions.Count; j++)
                {
                    var pj = positions[j]; if (pj == null || pj.Length < 2) continue;
                    if (Dist(pi, pj) < OverlapThreshM) n++;
                }
            }
            return n;
        }

        private static bool TooClose(double[] p, List<double[]> placed)
        {
            foreach (var q in placed) if (Dist(p, q) < OverlapThreshM) return true;
            return false;
        }

        // X/Y ONLY — see the class doc comment for why Z is excluded (SetPosition's Z argument is a confirmed
        // no-op on this build, and annotation position is fundamentally 2D sheet space anyway).
        private static double Dist(double[] a, double[] b)
        {
            double dx = a[0] - b[0], dy = a[1] - b[1];
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }
}
