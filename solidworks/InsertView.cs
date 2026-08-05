using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class InsertViewResult
    {
        public bool Verified;
        public bool AlreadyDone;
        public bool NeedsConfirm;
        public string Question;
        public string SourceModelPath;
        public string DrawingPath;
        public string ViewLabel;
        public string ViewInternalName;
        public int ViewCountBefore;
        public int ViewCountAfter;
        public double? RequestedScale;
        public double AppliedScale;
        public int RebuildErrors;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// InsertView — tool 103 (WRITE). "Insert a front view" / "add the left view at half scale". Places ONE
    /// named/custom view on the current drawing sheet — narrower than InsertStandardViews (tool 102, which always
    /// places all four front/top/right/isometric views at once). Matcher requires an insert-family verb + the
    /// literal word "view(s)" + EXACTLY ONE orientation word and no "standard/orthographic/projection" signal, so
    /// it never collides with InsertStandardViews's own standardWord-or-orthoHits>=2 threshold. Deliberately
    /// excludes "make" from its verb list (unlike InsertStandardViews) so "make the front view half scale" still
    /// lands on SetViewScale (tool 107), not here — SetViewScale's verb set (set/change/make/update/adjust) and
    /// this one's (insert/add/create/generate/put/give me) are disjoint by design.
    ///
    /// Same bootstrap pattern as InsertStandardViews/AddNote: reuses CreateDrawing.Run when no drawing is open yet
    /// (never duplicated), resolves the source part/assembly from the other open documents when a drawing already
    /// is open (Rule #2 ask on genuine ambiguity — zero or 2+ candidates).
    ///
    /// IDEMPOTENT (Rule #5): a view with the SAME orientation already on the sheet is left alone — reports
    /// AlreadyDone rather than stacking a duplicate. FAIL CLOSED (Rule #6): re-reads the sheet's own view list
    /// after the rebuild (never trusts the CreateDrawViewFromModelView3 return alone), confirms the count rose by
    /// exactly 1, the new view resolves back by orientation, and — when a scale was requested — that the freshly
    /// re-fetched view's own ScaleDecimal matches. Never saves — same as every other WRITE handler in this family.
    /// </summary>
    public static class InsertView
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (!Regex.IsMatch(c, @"\b(insert|add|create|generate|give\s+me|put)\b")) return false;
            if (!Regex.IsMatch(c, @"\bview\b|\bviews\b")) return false;
            if (Regex.IsMatch(c, @"\bstandard\b|\borthographic\b|\bprojection\b")) return false;
            if (Regex.IsMatch(c, @"\bsection\b|\bdetail\b")) return false; // reserved for insert_section_view/insert_detail_view (104/105)
            int orthoHits = 0;
            foreach (Match m in Regex.Matches(c, @"\b(front|top|right|left|back|bottom|iso|isometric)\b")) orthoHits++;
            return orthoHits == 1;
        }

        private struct Slot { public double X; public double Y; }
        private static readonly Dictionary<string, Slot> Slots = new Dictionary<string, Slot>
        {
            { "front",     new Slot { X = 0.11, Y = 0.20 } },
            { "top",       new Slot { X = 0.11, Y = 0.06 } },
            { "right",     new Slot { X = 0.25, Y = 0.20 } },
            { "isometric", new Slot { X = 0.25, Y = 0.06 } },
            { "left",      new Slot { X = 0.04, Y = 0.20 } },
            { "back",      new Slot { X = 0.32, Y = 0.20 } },
            { "bottom",    new Slot { X = 0.18, Y = 0.06 } },
        };

        public static async Task<InsertViewResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new InsertViewResult();
            if (model == null) { res.Error = "Open a part, assembly, or drawing first."; return res; }

            string targetWord = ParseTargetOrientation(intent);
            if (targetWord == null)
            {
                res.NeedsConfirm = true;
                res.Question = "Which view should be inserted — front, top, right, left, back, bottom, or isometric?";
                return res;
            }
            res.RequestedScale = ParseScale(intent);

            IModelDoc2 drawingDoc = null;
            string sourcePath = null;

            // The passed `model` handle can be STALE relative to the true active document — e.g. a prior
            // insert_view call already created a drawing and made it active, but `model` still points at the
            // original part (same stale-handle fix DeleteView needed for a chained/rerun command). Resolve the
            // TRUE active document first so a rerun recognizes the already-bootstrapped drawing instead of
            // bootstrapping a second one.
            bool modelIsDrawing = false;
            try { modelIsDrawing = (int)model.GetType() == (int)swDocumentTypes_e.swDocDRAWING; } catch { }
            IModelDoc2 activeDrawing = null;
            if (!modelIsDrawing)
            {
                try
                {
                    var active = app.IActiveDoc2 as IModelDoc2;
                    if (active != null && (int)active.GetType() == (int)swDocumentTypes_e.swDocDRAWING) activeDrawing = active;
                }
                catch { }
            }

            if (modelIsDrawing || activeDrawing != null)
            {
                drawingDoc = modelIsDrawing ? model : activeDrawing;
                try { sourcePath = FindOpenSourceModel(app, out res.Question); } catch { }
                if (sourcePath == null)
                {
                    res.NeedsConfirm = res.Question != null;
                    if (res.Question == null)
                        res.Error = "No part or assembly is open to reference — open the model this view should show first.";
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
                if (cd.Error != null) { res.Error = "Couldn't create a drawing to hold the view: " + cd.Error; return res; }
                drawingDoc = app.IActiveDoc2 as IModelDoc2;
                if (drawingDoc == null || (int)drawingDoc.GetType() != (int)swDocumentTypes_e.swDocDRAWING)
                { res.Error = "A drawing was reported created, but it isn't the active document — can't add a view."; return res; }
            }

            res.SourceModelPath = sourcePath;
            try { res.DrawingPath = drawingDoc.GetPathName(); } catch { }

            var dd = drawingDoc as DrawingDoc;
            if (dd == null) { res.Error = "The target document isn't a drawing."; return res; }

            var before = EnumerateViews(dd);
            res.ViewCountBefore = before.Count;

            // ---- IDEMPOTENT (Rule #5): a view with this exact orientation already on the sheet is left alone ----
            foreach (var (v, name, orientation) in before)
            {
                string o = (orientation ?? "").TrimStart('*').ToLowerInvariant();
                if (o == targetWord)
                {
                    res.AlreadyDone = true;
                    res.Verified = true;
                    res.ViewLabel = orientation ?? name;
                    res.ViewInternalName = name;
                    res.ViewCountAfter = res.ViewCountBefore;
                    res.Info = "A " + targetWord + " view is already on this sheet — not stacking a duplicate.";
                    await emit("Drafter", null, "done", res.Info);
                    return res;
                }
            }

            string swViewName = "*" + char.ToUpperInvariant(targetWord[0]) + targetWord.Substring(1);
            Slot slot;
            if (!Slots.TryGetValue(targetWord, out slot)) slot = new Slot { X = 0.15, Y = 0.15 };

            await emit("Drafter", "inserting the " + targetWord + " view", "run", null);
            View created = null;
            try { created = dd.CreateDrawViewFromModelView3(sourcePath, swViewName, slot.X, slot.Y, 0) as View; } catch { created = null; }
            if (created == null)
            {
                res.Error = "Couldn't insert the " + targetWord + " view — SolidWorks may not recognize this source's orientation.";
                await emit("Drafter", null, "fail", res.Error);
                return res;
            }

            if (res.RequestedScale.HasValue)
            {
                try { created.UseSheetScale = 0; created.ScaleDecimal = res.RequestedScale.Value; } catch { }
            }

            try { drawingDoc.ForceRebuild3(false); } catch { }

            // ---- FAIL CLOSED (Rule #6): re-read the sheet's own view list, never trust the created handle ----
            await emit("Sentinel", "verifying", "run", null);
            var after = EnumerateViews(dd);
            res.ViewCountAfter = after.Count;

            View fresh = null; string freshName = null, freshOrientation = null;
            foreach (var (v, name, orientation) in after)
            {
                string o = (orientation ?? "").TrimStart('*').ToLowerInvariant();
                if (o == targetWord) { fresh = v; freshName = name; freshOrientation = orientation; break; }
            }
            res.ViewLabel = freshOrientation ?? freshName;
            res.ViewInternalName = freshName;
            try { res.RebuildErrors = drawingDoc.Extension.GetWhatsWrongCount(); } catch { }
            if (fresh != null) { try { res.AppliedScale = fresh.ScaleDecimal; } catch { } }

            bool countOk = res.ViewCountAfter == res.ViewCountBefore + 1;
            bool viewFound = fresh != null;
            bool scaleOk = !res.RequestedScale.HasValue || Math.Abs(res.AppliedScale - res.RequestedScale.Value) < 1e-6;
            res.Verified = countOk && viewFound && scaleOk;

            if (!res.Verified)
            {
                res.Error = "Insert reported success but verification failed" +
                            (!countOk ? " (view count " + res.ViewCountBefore + " -> " + res.ViewCountAfter + ")" : "") +
                            (!viewFound ? " (couldn't find the new " + targetWord + " view by orientation)" : "") +
                            (!scaleOk ? " (scale is " + res.AppliedScale + ", requested " + res.RequestedScale.Value + ")" : "") + ".";
                await emit("Sentinel", null, "fail", res.Error);
                return res;
            }

            res.Info = "Inserted the " + targetWord + " view" + (res.RequestedScale.HasValue ? " at scale " + res.RequestedScale.Value : "") +
                       " (" + res.ViewCountBefore + " -> " + res.ViewCountAfter + " views). Forge didn't save.";
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

        // exactly one other open part/assembly document -> its path. Zero or more than one is a genuine
        // ambiguity (Rule #2: ask, never guess which model a drawing's view should reference). Independent copy of
        // InsertStandardViews's helper of the same name (kept inline, not shared, per this family's convention).
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
            question = "Which open model should this view show — " + string.Join(", ", candidates.ConvertAll(System.IO.Path.GetFileName)) + "?";
            return null;
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

        // "1:2", "1/2", "half", "double", "quarter", "full"/"1:1", "50%" — same phrasing SetViewScale parses,
        // kept as an independent copy so this handler's optional-scale path never depends on that handler's code.
        private static double? ParseScale(string intent)
        {
            string c = (intent ?? "").ToLowerInvariant();
            var ratio = Regex.Match(c, @"(\d+(?:\.\d+)?)\s*[:/]\s*(\d+(?:\.\d+)?)");
            if (ratio.Success)
            {
                double num = double.Parse(ratio.Groups[1].Value);
                double den = double.Parse(ratio.Groups[2].Value);
                if (den != 0) return num / den;
            }
            var pct = Regex.Match(c, @"(\d+(?:\.\d+)?)\s*%");
            if (pct.Success) return double.Parse(pct.Groups[1].Value) / 100.0;
            if (Regex.IsMatch(c, @"\bhalf\b")) return 0.5;
            if (Regex.IsMatch(c, @"\bquarter\b")) return 0.25;
            if (Regex.IsMatch(c, @"\bdouble\b")) return 2.0;
            if (Regex.IsMatch(c, @"\btriple\b")) return 3.0;
            if (Regex.IsMatch(c, @"\bfull\b")) return 1.0;
            return null;
        }
    }
}
