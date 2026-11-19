using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public class InContextFeatureRow
    {
        public string FeatureName;
        public string FeatureType;
        public List<string> ExternalFiles = new List<string>();
    }

    public class DetectInContextWritesResult
    {
        public bool Success;
        public int FeaturesScanned;
        public int InContextFeatureCount;   // features that carry an external file reference
        public List<string> AffectedFiles = new List<string>();   // distinct OTHER files an edit here could ripple into
        public List<InContextFeatureRow> Rows = new List<InContextFeatureRow>();
        public string Info;
        public string Error;
    }

    /// <summary>
    /// DetectInContextWrites (tool 242, READ) — "warn when an operation will write to OTHER files than the
    /// target." The scariest silent side-effect in SolidWorks: a feature built IN-CONTEXT (e.g. Insert > Part,
    /// or a sketch/feature that consumed another component's geometry while editing in an assembly) carries a
    /// live link back to that other file — editing the dimension that drives it can silently ripple into a
    /// document the user never opened.
    ///
    /// `IFeature.ListExternalFileReferencesCount()` / `IFeature.ListExternalFileReferences(...)` is the one API
    /// SolidWorks exposes for this per-feature (confirmed via reflection sweep of the interop: IModelDoc2 only
    /// has a DOCUMENT-level external-reference list, e.g. for pack-and-go/GetFileReferences (tool 130) — it
    /// does not say WHICH feature is responsible, so it can't answer "will editing THIS thing touch another
    /// file"). Walking every top-level feature and calling the per-feature API is the only way to attribute the
    /// risk to a specific operation, which is what this tool promises ("warn when an OPERATION will write...").
    ///
    /// Top-level walk only (FirstFeature/GetNextFeature) — same scope line CreateLayoutSketch's handler draws
    /// against its GT's recursive walk; GT here re-derives via FeatureManager.GetFeatures(false) (ALL features,
    /// including sub-features, a different traversal primitive) for a genuinely independent cross-check.
    ///
    /// READ-ONLY: nothing is opened, changed, rebuilt or saved. Detect + report, never auto-fix.
    /// </summary>
    public static class DetectInContextWrites
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            bool contextWord = Regex.IsMatch(c, @"\bin[\s-]?context\b|\bexternal(ly)?[\s-]?referenc(e|ed|ing)\b|\bin[\s-]?place\b");
            bool spreadWord = Regex.IsMatch(c, @"\b(other|another|other'?s|multiple)\s+(file|files|part|parts|document|documents)\b|\bripple\b|\bpropagat(e|es|ing)\b|\bside[\s-]?effect");
            bool askVerb = Regex.IsMatch(c, @"\b(warn|check|detect|will\s+(this|it|editing)|does\s+(this|editing)|touch|affect|modify|change|break)\b");
            // "will editing this part also change/touch other files" style asks, OR explicit in-context/external-ref vocabulary alone.
            return contextWord || (spreadWord && askVerb);
        }

        public static async Task<DetectInContextWritesResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new DetectInContextWritesResult();
            if (model == null) { res.Error = "Open a document to check for in-context/external-file writes."; return res; }

            await emit("Sentinel", "walking features for external file references", "run", null);

            var affected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var rows = new List<InContextFeatureRow>();
            int scanned = 0;

            Feature feat = null;
            try { feat = model.FirstFeature() as Feature; } catch { }
            while (feat != null)
            {
                scanned++;
                int cnt = 0;
                try { cnt = feat.ListExternalFileReferencesCount(); } catch { }
                if (cnt > 0)
                {
                    object modelPathObj = null, compPathObj = null, featObj = null, dataTypeObj = null, statusObj = null, refEntObj = null, featComObj = null;
                    try { feat.ListExternalFileReferences(out modelPathObj, out compPathObj, out featObj, out dataTypeObj, out statusObj, out refEntObj, out featComObj); } catch { }

                    string fname = null, ftype = null;
                    try { fname = feat.Name; } catch { }
                    try { ftype = feat.GetTypeName2(); } catch { }
                    var row = new InContextFeatureRow { FeatureName = fname, FeatureType = ftype };

                    var paths = modelPathObj as object[];
                    if (paths != null)
                        foreach (var p in paths)
                        {
                            var s = p as string;
                            if (!string.IsNullOrWhiteSpace(s)) { affected.Add(s); row.ExternalFiles.Add(s); }
                        }
                    rows.Add(row);
                }
                Feature next = null; try { next = feat.GetNextFeature() as Feature; } catch { }
                feat = next;
            }

            res.FeaturesScanned = scanned;
            res.InContextFeatureCount = rows.Count;
            res.AffectedFiles = affected.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
            res.Rows = rows;
            res.Success = true;

            res.Info = rows.Count == 0
                ? "No in-context / external-file-referencing features found — editing this document stays self-contained."
                : rows.Count + " feature(s) carry an external file reference, touching " + res.AffectedFiles.Count +
                  " other file(s): " + string.Join(", ", res.AffectedFiles.Select(Path.GetFileName)) +
                  " — an edit here may propagate there. Never modify without opening and reviewing those files too.";
            await emit("Sentinel", null, "done", res.Info);
            return res;
        }
    }
}
