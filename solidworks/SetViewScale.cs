using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class SetViewScaleResult
    {
        public bool Verified;
        public bool AlreadyDone;
        public bool NeedsConfirm;
        public string Question;
        public string ViewLabel;         // resolved orientation label ("Front", "Isometric", ...) or internal name if unresolved
        public string ViewInternalName;  // the sheet's own "Drawing ViewN" name
        public double BeforeScale;
        public double RequestedScale;
        public double AfterScale;
        public int RebuildErrors;
        public List<string> Diag = new List<string>(); // per-view name|orientation|scale, for post-hoc debugging
        public string Info;
        public string Error;
    }

    /// <summary>
    /// SetViewScale — tool 107 (WRITE). "Set the isometric view to half scale" / "make the front view 1:2". Changes
    /// ONE drawing view's own scale (View.ScaleDecimal), independent of the sheet's overall scale.
    ///
    /// If no drawing is open yet, bootstraps one via InsertStandardViews.Run (tool 102, same reuse-not-duplicate
    /// pattern InsertStandardViews itself used for CreateDrawing) so this is reachable straight from an open part.
    /// Views are then resolved by ORIENTATION WORD (front/top/right/isometric/...) against View.GetOrientationName();
    /// when this run just bootstrapped the sheet, insertion order is known (Front/Top/Right/Isometric, matching
    /// InsertStandardViews.Views[]) and used as a instrumented fallback if GetOrientationName comes back empty —
    /// every view's raw name+orientation+scale is logged to Diag regardless so a future session can see which path
    /// actually fired instead of re-guessing. Zero views, an unresolved target on a PRE-EXISTING sheet, or an
    /// unparseable scale phrase are all genuine Rule #2 asks — never a guess.
    ///
    /// Sets View.UseSheetScale = 0 (custom, not inherited) before writing ScaleDecimal so the sheet's own scale
    /// can't silently override it. FAIL CLOSED (Rule #6): re-fetches the SAME view fresh from dd.GetViews() after
    /// ForceRebuild3 (never trusts a cached ref post-rebuild — the InsertDome/tool-157 stale-Face2 lesson applies
    /// to View handles too) and re-reads ScaleDecimal off that fresh handle.
    /// </summary>
    public static class SetViewScale
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            bool viewWord = Regex.IsMatch(c, @"\bview\b|\bviews\b|\b(front|top|right|left|back|bottom|isometric|iso)\b");
            if (!viewWord) return false;
            if (!Regex.IsMatch(c, @"\bscale\b")) return false;
            if (!Regex.IsMatch(c, @"\b(set|change|make|update|adjust)\b")) return false;
            return true;
        }

        private static readonly string[] BootstrapOrder = { "front", "top", "right", "isometric" };

        public static async Task<SetViewScaleResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new SetViewScaleResult();
            if (model == null) { res.Error = "Open a part, assembly, or drawing first."; return res; }

            double? requested = ParseScale(intent);
            if (requested == null)
            {
                res.NeedsConfirm = true;
                res.Question = "What scale should the view be set to? (e.g. \"1:2\", \"half scale\", \"2:1\", \"50%\")";
                return res;
            }
            res.RequestedScale = requested.Value;

            IModelDoc2 drawingDoc = null;
            bool justBootstrapped = false;
            bool activeIsDrawing = false;
            try { activeIsDrawing = (int)model.GetType() == (int)swDocumentTypes_e.swDocDRAWING; } catch { }

            if (activeIsDrawing)
            {
                drawingDoc = model;
            }
            else
            {
                await emit("Drafter", "no drawing open — creating one with standard views first", "run", null);
                var iv = await InsertStandardViews.Run(app, model, intent, emit);
                if (iv.NeedsConfirm) { res.NeedsConfirm = true; res.Question = iv.Question; return res; }
                if (iv.Error != null) { res.Error = "Couldn't set up a drawing to scale: " + iv.Error; return res; }
                drawingDoc = app.IActiveDoc2 as IModelDoc2;
                if (drawingDoc == null || (int)drawingDoc.GetType() != (int)swDocumentTypes_e.swDocDRAWING)
                { res.Error = "A drawing was reported created, but it isn't the active document — can't scale a view."; return res; }
                justBootstrapped = true;
            }

            var dd = drawingDoc as DrawingDoc;
            if (dd == null) { res.Error = "The target document isn't a drawing."; return res; }

            string targetWord = ParseTargetOrientation(intent);

            var views = EnumerateViews(dd, res.Diag);
            if (views.Count == 0) { res.Error = "This drawing sheet has no views to scale."; return res; }

            View target = null;
            string resolvedLabel = null;

            if (targetWord != null)
            {
                foreach (var (v, name, orientation) in views)
                {
                    string o = (orientation ?? "").TrimStart('*').ToLowerInvariant();
                    if (o == targetWord || o.Contains(targetWord)) { target = v; resolvedLabel = orientation ?? name; break; }
                }
                // fallback: only trust bootstrap insertion order when THIS run just created the sheet (known sequence)
                if (target == null && justBootstrapped)
                {
                    int idx = Array.IndexOf(BootstrapOrder, targetWord);
                    if (idx >= 0 && idx < views.Count) { target = views[idx].Item1; resolvedLabel = char.ToUpperInvariant(targetWord[0]) + targetWord.Substring(1); }
                }
            }
            else if (views.Count == 1)
            {
                target = views[0].Item1;
                resolvedLabel = views[0].Item3 ?? views[0].Item2;
            }

            if (target == null)
            {
                var names = new List<string>();
                foreach (var (_, name, orientation) in views) names.Add(orientation ?? name);
                res.NeedsConfirm = true;
                res.Question = "Which view should be rescaled — " + string.Join(", ", names) + "?";
                return res;
            }

            res.ViewLabel = resolvedLabel;
            try { res.ViewInternalName = target.Name; } catch { }
            try { res.BeforeScale = target.ScaleDecimal; } catch { }
            res.AlreadyDone = Math.Abs(res.BeforeScale - res.RequestedScale) < 1e-6;

            await emit("Scribe", "setting " + (res.ViewLabel ?? res.ViewInternalName) + " view scale to " + res.RequestedScale, "run", null);
            try { target.UseSheetScale = 0; } catch { }
            try { target.ScaleDecimal = res.RequestedScale; } catch (Exception ex) { res.Error = "Failed to set the view scale: " + ex.Message; return res; }

            try { drawingDoc.ForceRebuild3(false); } catch { }

            // ---- FAIL CLOSED: re-fetch a FRESH view handle, never trust the one held across the rebuild ----
            await emit("Sentinel", "verifying", "run", null);
            var afterViews = EnumerateViews(dd, res.Diag);
            View fresh = null;
            foreach (var (v, name, _) in afterViews) { if (name == res.ViewInternalName) { fresh = v; break; } }
            if (fresh == null) { res.Error = "The view disappeared after the rebuild — cannot verify the scale change."; return res; }

            try { res.AfterScale = fresh.ScaleDecimal; } catch { }
            try { res.RebuildErrors = drawingDoc.Extension.GetWhatsWrongCount(); } catch { }
            res.Verified = Math.Abs(res.AfterScale - res.RequestedScale) < 1e-6;

            if (!res.Verified)
            {
                res.Error = "Set the scale but the re-read value is " + res.AfterScale + ", not the requested " + res.RequestedScale + ".";
                await emit("Sentinel", null, "fail", res.Error);
                return res;
            }

            res.Info = "Set the " + (res.ViewLabel ?? res.ViewInternalName) + " view scale to " + res.RequestedScale +
                       " (was " + res.BeforeScale + "). Forge didn't save.";
            await emit("Sentinel", null, "done", res.Info);
            return res;
        }

        private static List<(View, string, string)> EnumerateViews(DrawingDoc dd, List<string> diag)
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
                    double scale = 0; try { scale = v.ScaleDecimal; } catch { }
                    diag.Add((name ?? "?") + "|" + (orientation ?? "") + "|" + scale);
                    result.Add((v, name, orientation));
                }
            }
            return result;
        }

        // "front"/"top"/"right"/"left"/"back"/"bottom"/"iso(metric)" — the only orientation words InsertStandardViews
        // itself creates, kept in sync with that handler's Views[] table.
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

        // "1:2", "1/2", "half", "double", "quarter", "full"/"1:1", "50%" — the common ways people phrase a view scale.
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
