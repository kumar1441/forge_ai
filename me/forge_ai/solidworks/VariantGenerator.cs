using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class DimInfo
    {
        public string Name;        // SolidWorks dimension full name, e.g. "D1@Sketch1"
        public double ValueMm;     // current value in millimetres
        public string Feature;     // owning feature name (for display)
        public string Type;        // "diameter" | "linear" | "angular" | "chamfer" | "other"
        public bool Selected;      // the engineer pinned/selected this dim (or its feature) in SolidWorks
    }

    public class FeatureDep
    {
        public string Name;
        public string Type;
        public List<string> Affects = new List<string>(); // downstream features this one impacts
    }

    public class EditChange
    {
        public string Name;   // dimension full name
        public string Label;  // friendly, e.g. "bore"
        public string Unit;
        public double Value;  // absolute target value
    }

    public class EditResult
    {
        public List<string> Changed = new List<string>(); // human descriptions of what changed
        public bool Healthy = true;                        // false if the edit broke a feature
        public bool Reverted;                              // true if we undid the change to stay valid
        public string Issue;                               // broken feature names
        public string Error;
        public string Path;                                // sandbox path of the edited COPY (original never touched)
    }

    public class VariantOutcome
    {
        public string Label;        // e.g. "20mm"
        public string Path;         // saved .SLDPRT path (or "")
        public string DrawingPath;  // saved .SLDDRW path (or "")
        public bool Success;
        public string Error;
        public bool Healthy = true; // false if the variant rebuilt with errors (e.g. a chamfer broke)
        public string Issue;        // names of the broken features, for "review" flagging
    }

    public class VariantSummary
    {
        public string DimensionName;
        public string Unit;
        public List<VariantOutcome> Variants = new List<VariantOutcome>();
    }

    /// <summary>
    /// Garrett's variant loop: read a part's dimensions, then stamp out one .SLDPRT
    /// per requested value of a single dimension. The base part is never modified on disk
    /// (each variant is written with the "save as copy" flag, leaving the original active).
    /// </summary>
    public static class VariantGenerator
    {
        // Read every display dimension in the active part by traversing the feature tree.
        public static List<DimInfo> ReadDimensions(IModelDoc2 model)
        {
            var dims = new List<DimInfo>();
            var seen = new HashSet<string>();

            Feature feat = (Feature)model.FirstFeature();
            while (feat != null)
            {
                DisplayDimension dispDim = (DisplayDimension)feat.GetFirstDisplayDimension();
                while (dispDim != null)
                {
                    Dimension dim = (Dimension)dispDim.GetDimension2(0);
                    if (dim != null && !seen.Contains(dim.FullName))
                    {
                        seen.Add(dim.FullName);
                        dims.Add(new DimInfo
                        {
                            Name = dim.FullName,
                            ValueMm = dim.SystemValue * 1000.0, // SystemValue is metres
                            Feature = feat.Name,
                            Type = DimTypeName(dim)
                        });
                    }
                    dispDim = (DisplayDimension)feat.GetNextDisplayDimension(dispDim);
                }
                feat = (Feature)feat.GetNextFeature();
            }
            return dims;
        }

        private class DimOrig { public Dimension Dim; public double Orig; }

        // Dependency-aware in-place edit on the ACTIVE part. Applies the change, rebuilds, and checks
        // what broke. If something breaks and force==false, it REVERTS so the model is never left
        // broken (and reports what would have broken). force==true keeps the change regardless.
        public static EditResult ApplyEdit(ISldWorks swApp, IModelDoc2 model, List<EditChange> changes, bool force)
        {
            var r = new EditResult();
            bool prevShowErrors = swApp.GetUserPreferenceToggle((int)swUserPreferenceToggle_e.swShowErrorsEveryRebuild);
            var originals = new List<DimOrig>();
            try
            {
                swApp.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swShowErrorsEveryRebuild, false);
                foreach (var c in changes)
                {
                    Dimension dim = (Dimension)model.Parameter(c.Name);
                    if (dim == null) { r.Error = "Dimension '" + c.Name + "' not found."; continue; }

                    // Sanity guard: never feed SolidWorks a value that could hang/break a rebuild.
                    double meters = ToMeters(c.Value, c.Unit);
                    if (!(meters > 0) || meters > 100.0) // 100 m ceiling
                    { r.Error = "Value out of a safe range (" + c.Value + " " + (c.Unit ?? "mm") + ") — not applied."; continue; }

                    originals.Add(new DimOrig { Dim = dim, Orig = dim.SystemValue });
                    dim.SystemValue = meters;
                    r.Changed.Add(c.Label + " = " + c.Value + (c.Unit ?? "mm"));
                }
                // EditRebuild3 rebuilds ONLY the changed feature + its downstream — seconds on a heavy model.
                model.EditRebuild3();

                bool broken = model.Extension.GetWhatsWrongCount() > 0;
                if (broken) { r.Healthy = false; r.Issue = DescribeWhatsWrong(model); }

                if (!broken || force)
                {
                    // GUARDRAIL: save the EDITED state as a COPY into the sandbox. SaveAs with the Copy
                    // flag never changes the open document, and the original file is never Save()'d.
                    string src = model.GetPathName();
                    if (!string.IsNullOrEmpty(src))
                    {
                        string outPath = System.IO.Path.Combine(SafePaths.NewRunFolder(),
                            System.IO.Path.GetFileNameWithoutExtension(src) + "_edited.SLDPRT");
                        SafePaths.AssertInSandbox(outPath);
                        int e = 0, w = 0;
                        model.Extension.SaveAs(outPath,
                            (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                            (int)swSaveAsOptions_e.swSaveAsOptions_Silent | (int)swSaveAsOptions_e.swSaveAsOptions_Copy,
                            null, ref e, ref w);
                        r.Path = outPath;
                    }
                }
                else
                {
                    r.Reverted = true; // broken and not forced — nothing saved
                }
            }
            catch (Exception ex) { r.Error = ex.Message; }
            finally
            {
                // ALWAYS restore the live document so the user's OPEN part is left exactly as it was.
                foreach (var o in originals) { try { o.Dim.SystemValue = o.Orig; } catch { } }
                try { model.EditRebuild3(); } catch { }
                swApp.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swShowErrorsEveryRebuild, prevShowErrors);
            }
            return r;
        }

        // The part's dependency graph: each feature -> the downstream features it affects. This is
        // what powers change-impact ("if I change X, what breaks") and dependency-aware editing.
        public static List<FeatureDep> ReadDependencies(IModelDoc2 model)
        {
            var deps = new List<FeatureDep>();
            Feature feat = (Feature)model.FirstFeature();
            while (feat != null)
            {
                var fd = new FeatureDep { Name = feat.Name, Type = feat.GetTypeName2() };
                object kids = feat.GetChildren();
                var arr = kids as object[];
                if (arr != null)
                    foreach (var k in arr)
                    {
                        var kf = k as Feature;
                        if (kf != null) fd.Affects.Add(kf.Name);
                    }
                // Keep only features that drive something — that's the dependency graph; folders and
                // leaf features add noise the AI doesn't need.
                if (fd.Affects.Count > 0) deps.Add(fd);
                feat = (Feature)feat.GetNextFeature();
            }
            return deps;
        }

        // Dimension kind, so the AI can map "bore"/"diameter" to a real diameter dim, not a depth.
        // The interop's IDimension.GetType() shadows Object.GetType() and returns swDimensionType_e.
        private static string DimTypeName(Dimension dim)
        {
            int t;
            try { t = dim.GetType(); }
            catch { return "other"; }

            switch (t)
            {
                case 5: case 6: case 14: case 15: return "diameter"; // radial/diameter/diametric
                case 2: case 11: case 12: return "linear";           // linear distances (e.g. cut depth)
                case 3: return "angular";
                case 10: return "chamfer";
                default: return "other";
            }
        }

        // unitToMeters: convert a target value (in the plan's unit) to metres for SystemValue.
        private static double ToMeters(double value, string unit)
        {
            switch ((unit ?? "mm").Trim().ToLowerInvariant())
            {
                case "in":
                case "inch":
                case "inches": return value * 0.0254;
                case "cm": return value / 100.0;
                case "m": return value;
                default: return value / 1000.0; // mm
            }
        }

        // Each variant applies ALL changes at index i: variant i sets every dimension to its i-th value.
        // Drawings are only generated when the engineer asked for them (drawings == true).
        public static VariantSummary Generate(ISldWorks swApp, IModelDoc2 model, List<DimChange> changes, int count, bool drawings, bool drawOriginal)
        {
            var summary = new VariantSummary
            {
                DimensionName = string.Join(" + ", changes.ConvertAll(c => c.Label)),
                Unit = "mm"
            };

            string basePath = model.GetPathName();
            if (string.IsNullOrEmpty(basePath))
                throw new Exception("Save the part once before generating variants.");

            // Resolve every dimension up front and capture its original value (to restore later).
            var targets = new List<DimTarget>();
            foreach (var ch in changes)
            {
                Dimension d = (Dimension)model.Parameter(ch.Name);
                if (d == null)
                    throw new Exception("Dimension '" + ch.Name + "' (" + ch.Label + ") not found in the active part.");
                targets.Add(new DimTarget { Change = ch, Dim = d, Original = d.SystemValue });
            }

            // GUARDRAIL: all variant/drawing output goes into the per-user sandbox, NEVER next to the
            // original. The base part is only read; it is never Save()'d (SaveAs uses the Copy flag).
            string dir = SafePaths.NewRunFolder();
            SafePaths.AssertInSandbox(dir);
            string baseName = Path.GetFileNameWithoutExtension(basePath);

            // Drawing creation needs the part LOADED/visible to project views, so we only run the
            // invisible "quiet" mode for parts-only runs. With drawings, docs are visible (the
            // validated recipe), at the cost of a little screen activity.
            bool quiet = !(drawings || drawOriginal);
            bool prevShowErrors = false;
            try
            {
                if (quiet)
                {
                    swApp.CommandInProgress = true;
                    swApp.DocumentVisible(false, (int)swDocumentTypes_e.swDocDRAWING);
                    swApp.DocumentVisible(false, (int)swDocumentTypes_e.swDocPART);
                }

                // Suppress the "What's Wrong during rebuild" modal so a broken feature can't freeze
                // the panel. We still detect those errors silently via GetWhatsWrongCount.
                prevShowErrors = swApp.GetUserPreferenceToggle((int)swUserPreferenceToggle_e.swShowErrorsEveryRebuild);
                swApp.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swShowErrorsEveryRebuild, false);

                // Draw the original/base part first (with its current dims), without closing it —
                // it's the user's open document.
                if (drawOriginal)
                {
                    var baseOutcome = new VariantOutcome { Label = "original", Success = true, Path = basePath };
                    try { baseOutcome.DrawingPath = DrawingGenerator.Generate(swApp, basePath, basePath, false); }
                    catch (Exception ex) { baseOutcome.Success = false; baseOutcome.Error = "Original drawing failed: " + ex.Message; }
                    summary.Variants.Add(baseOutcome);
                }

                for (int i = 0; i < count; i++)
                {
                    var labelParts = new List<string>();
                    foreach (var t in targets)
                    {
                        double val = i < t.Change.Values.Length ? t.Change.Values[i] : t.Change.Values[t.Change.Values.Length - 1];
                        labelParts.Add(t.Change.Label + " " + FormatLabel(val, t.Change.Unit));
                    }
                    string panelLabel = string.Join(", ", labelParts);
                    var outcome = new VariantOutcome { Label = panelLabel };

                    try
                    {
                        foreach (var t in targets)
                        {
                            double val = i < t.Change.Values.Length ? t.Change.Values[i] : t.Change.Values[t.Change.Values.Length - 1];
                            t.Dim.SystemValue = ToMeters(val, t.Change.Unit);
                        }
                        model.ForceRebuild3(false);

                        // Did any change break a downstream feature? Flag for review, don't hide it.
                        if (model.Extension.GetWhatsWrongCount() > 0)
                        {
                            outcome.Healthy = false;
                            outcome.Issue = DescribeWhatsWrong(model);
                        }

                        string newPath = Path.Combine(dir, baseName + "_v" + (i + 1) + ".SLDPRT");
                        int err = 0, warn = 0;
                        int options = (int)swSaveAsOptions_e.swSaveAsOptions_Silent
                                    | (int)swSaveAsOptions_e.swSaveAsOptions_Copy;

                        bool ok = model.Extension.SaveAs(
                            newPath,
                            (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                            options, null, ref err, ref warn);

                        outcome.Success = ok;
                        outcome.Path = ok ? newPath : "";
                        if (!ok) { outcome.Error = "SaveAs failed (err " + err + ")"; }
                        else if (drawings)
                        {
                            try { outcome.DrawingPath = DrawingGenerator.Generate(swApp, basePath, newPath); }
                            catch (Exception drawEx)
                            {
                                outcome.DrawingPath = "";
                                outcome.Error = "Part saved. Drawing failed: " + drawEx.Message;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        outcome.Success = false;
                        outcome.Error = ex.Message;
                    }
                    summary.Variants.Add(outcome);
                }
            }
            finally
            {
                // Restore every changed dimension so the base part is left untouched.
                foreach (var t in targets) t.Dim.SystemValue = t.Original;
                model.ForceRebuild3(false);

                swApp.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swShowErrorsEveryRebuild, prevShowErrors);
                if (quiet)
                {
                    swApp.DocumentVisible(true, (int)swDocumentTypes_e.swDocPART);
                    swApp.DocumentVisible(true, (int)swDocumentTypes_e.swDocDRAWING);
                    swApp.CommandInProgress = false;
                }
            }

            return summary;
        }

        private class DimTarget
        {
            public DimChange Change;
            public Dimension Dim;
            public double Original;
        }

        // Names of the features that errored/warned after a rebuild, e.g. "Chamfer2".
        private static string DescribeWhatsWrong(IModelDoc2 model)
        {
            try
            {
                object feats, errs, warns;
                model.Extension.GetWhatsWrong(out feats, out errs, out warns);
                var names = feats as object[];
                if (names == null || names.Length == 0) return "rebuild error";
                var list = new List<string>();
                foreach (var n in names)
                    if (n != null) list.Add(n.ToString());
                return list.Count > 0 ? string.Join(", ", list) : "rebuild error";
            }
            catch
            {
                return "rebuild error";
            }
        }

        private static string FormatLabel(double v, string unit)
        {
            string num = (v == Math.Floor(v)) ? ((long)v).ToString() : v.ToString("0.###");
            return num + (unit ?? "mm");
        }

        private static string Sanitize(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s)
                sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
            return sb.ToString();
        }
    }
}
