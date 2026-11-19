using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class CheckDraftingStandardsResult
    {
        public string SheetName;
        public int TotalDimensions;
        public int DanglingCount;
        public int NoToleranceCount;
        public List<string> EmptyTitleBlockFields = new List<string>();
        public List<string> NonStandardScaleViews = new List<string>();
        public bool ReleaseReady;
        public string Verdict;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// CheckDraftingStandards (tool #185 check_drafting_standards, READ) — pre-release drawing lint on the ACTIVE
    /// drawing: dangling dimensions, undimensioned/untoleranced dims, empty title-block fields, and views at a
    /// non-standard scale. Reports a release-ready / needs-fixes verdict with the itemized reasons.
    ///
    /// This codebase has no "company rules file" mechanism anywhere (no other tool reads one), so the checklist
    /// below is a sensible hardcoded default rather than a fabricated rules-file reader: dangling dims (same
    /// `IAnnotation.IsDangling()` primitive `ListDanglingDimensions.cs` proved, re-walked independently per this
    /// codebase's per-tool convention), a small standard title-block field set (Description/Material/Revision —
    /// the fields present on every SolidWorks-shipped title block, read via `ICustomPropertyManager.Get4` on the
    /// drawing document), and each view's scale (`IView.GetIScaleRatio()`/`GetScaleRatio()`) checked against the
    /// common engineering-drawing scale set (1:1, 1:2, 1:5, 1:10, 1:20, 1:50, 1:100, 2:1, 5:1, 10:1).
    /// </summary>
    public static class CheckDraftingStandards
    {
        private static readonly double[] StandardRatios = { 1.0, 0.5, 0.2, 0.1, 0.05, 0.02, 0.01, 2.0, 5.0, 10.0 };
        private static readonly string[] RequiredFields = { "Description", "Material", "Revision" };

        // Requires a check/audit/lint/validate/review/verify verb AND a "drafting/dimensioning standard(s)" or
        // "release-ready"/"pre-release" noun. Explicitly excludes set/switch/change/use/convert (SetDraftingStandard's
        // verb set) so the two never collide regardless of dispatch order.
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (Regex.IsMatch(c, @"\b(set|switch|change|use|convert)\b")) return false;
            bool verb = Regex.IsMatch(c, @"\b(check|audit|lint|validate|review|verify|scan)\b");
            bool noun = Regex.IsMatch(c, @"\b(drafting|dimensioning)\s+standards?\b")
                        || Regex.IsMatch(c, @"\brelease.?ready\b")
                        || Regex.IsMatch(c, @"\bpre.?release\b.*\bdrawing\b");
            return verb && noun;
        }

        public static async Task<CheckDraftingStandardsResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new CheckDraftingStandardsResult();
            var dd = model as DrawingDoc;
            if (dd == null) { res.Error = "Open the drawing you want to lint before release."; return res; }

            await emit("Lint", "rebuilding and scanning the drawing", "run", null);
            try { model.ForceRebuild3(false); } catch { }

            var sheet = dd.GetCurrentSheet() as ISheet;
            try { res.SheetName = sheet != null ? sheet.GetName() : null; } catch { }

            // ---- 1) dangling dimensions + missing tolerances, walked per-view (same shape ListDanglingDimensions.cs
            // proved for the dangling check; tolerance check is new — reads IDimension.GetToleranceType() per dim). ----
            object[] perSheet = null;
            try { perSheet = dd.GetViews() as object[]; } catch { }
            if (perSheet != null)
            {
                foreach (var so in perSheet)
                {
                    var group = so as object[];
                    if (group == null) continue;
                    for (int k = 1; k < group.Length; k++)
                    {
                        var v = group[k] as IView;
                        if (v == null) continue;
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
                            if (dangling) { res.DanglingCount++; continue; }

                            try
                            {
                                var d = ddim.GetDimension2(0) as Dimension;
                                int tol = (int)swTolType_e.swTolNONE;
                                if (d != null) tol = d.GetToleranceType();
                                if (tol == (int)swTolType_e.swTolNONE) res.NoToleranceCount++;
                            }
                            catch { }
                        }
                    }
                }
            }

            // ---- 2) empty title-block fields (standard SolidWorks-shipped field set, drawing-level custom props). ----
            CustomPropertyManager cpm = null;
            try { cpm = model.Extension.get_CustomPropertyManager(""); } catch { }
            foreach (var field in RequiredFields)
            {
                string val = null, resolved = null;
                try { cpm?.Get4(field, false, out val, out resolved); } catch { }
                if (string.IsNullOrWhiteSpace(resolved) && string.IsNullOrWhiteSpace(val)) res.EmptyTitleBlockFields.Add(field);
            }

            // ---- 3) non-standard view scales. ----
            if (perSheet != null)
            {
                foreach (var so in perSheet)
                {
                    var group = so as object[];
                    if (group == null) continue;
                    for (int k = 0; k < group.Length; k++)
                    {
                        var v = group[k] as IView;
                        if (v == null) continue;
                        double ratio = 0; try { ratio = v.get_IScaleRatio(); } catch { }
                        if (ratio <= 0) continue;
                        bool standard = StandardRatios.Any(sr => Math.Abs(sr - ratio) < 0.001);
                        if (!standard)
                        {
                            string vn = null; try { vn = v.Name; } catch { }
                            res.NonStandardScaleViews.Add((vn ?? "?") + " (" + ratio.ToString("0.###") + ":1)");
                        }
                    }
                }
            }

            res.ReleaseReady = res.DanglingCount == 0 && res.EmptyTitleBlockFields.Count == 0 && res.NonStandardScaleViews.Count == 0;
            res.Verdict = res.ReleaseReady ? "release-ready" : "needs-fixes";

            var reasons = new List<string>();
            if (res.DanglingCount > 0) reasons.Add(res.DanglingCount + " dangling dimension(s)");
            if (res.EmptyTitleBlockFields.Count > 0) reasons.Add("empty title-block field(s): " + string.Join(", ", res.EmptyTitleBlockFields));
            if (res.NonStandardScaleViews.Count > 0) reasons.Add("non-standard scale on: " + string.Join(", ", res.NonStandardScaleViews));
            if (res.NoToleranceCount > 0) reasons.Add(res.NoToleranceCount + " dimension(s) with no tolerance set (informational)");

            res.Info = res.ReleaseReady
                ? "Release-ready — " + res.TotalDimensions + " dimension(s) checked, title block complete, all view scales standard."
                : "Needs fixes: " + string.Join("; ", reasons.Take(3));

            await emit("Lint", null, "done", res.Verdict + " (" + res.TotalDimensions + " dims, " + res.DanglingCount + " dangling, " +
                res.EmptyTitleBlockFields.Count + " empty field(s), " + res.NonStandardScaleViews.Count + " odd-scale view(s))");
            return res;
        }
    }
}
