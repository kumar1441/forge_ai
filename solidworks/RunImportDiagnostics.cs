using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class RunImportDiagnosticsResult
    {
        public bool Ran;
        public int DiagnosisReturn = int.MinValue;   // raw IPartDoc.ImportDiagnosis return, undecoded
        public int RebuildErrorsBefore = -1;
        public int RebuildErrorsAfter = -1;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// RunImportDiagnostics (tool #154) — scan an imported part for faulty faces/gaps and attempt an auto-heal.
    /// "run import diagnostics", "check for import errors", "heal the faulty faces on this import".
    ///
    /// PROBE build: `IPartDoc.ImportDiagnosis(CloseAllGaps, RemoveFaces, FixFaces, Options):Int32` found via
    /// reflection, untried — instrumented rather than assumed, per the "reflect + instrument first" rule. Distinct
    /// from `IPartDoc.ImportDiagnosisGapCloser` (a per-gap manual nudge tool, not a whole-part scan) and from
    /// `check_geometry_errors` (tool 156, a pure READ scan for tiny gaps/slivers — not yet built).
    /// </summary>
    public static class RunImportDiagnostics
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            bool diagVerb = Regex.IsMatch(c, @"\b(run|check|diagnose|heal|fix|repair)\b");
            bool importScope = Regex.IsMatch(c, @"\bimport\b") && Regex.IsMatch(c, @"\b(diagnos\w*|error|gap|face)\b");
            bool healFaces = Regex.IsMatch(c, @"\bfaulty\s+face") || Regex.IsMatch(c, @"\bclose\b.*\bgaps?\b");
            return diagVerb && (importScope || healFaces);
        }

        // same per-feature IFeature.GetErrorCode2 walk GetRebuildErrors.cs already proved live — not a fabricated
        // API, a reuse of a confirmed-working one.
        private static int CountRebuildErrors(IModelDoc2 model)
        {
            int err = 0, warn = 0;
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    CheckFeat(f, ref err, ref warn);
                    var s = f.GetFirstSubFeature() as Feature;
                    while (s != null) { CheckFeat(s, ref err, ref warn); s = s.GetNextSubFeature() as Feature; }
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch { return -1; }
            return err;
        }

        private static void CheckFeat(Feature f, ref int err, ref int warn)
        {
            int code = 0; bool isWarn = false;
            try { code = f.GetErrorCode2(out isWarn); } catch { return; }
            if (code == (int)swFeatureError_e.swFeatureErrorNone) return;
            if (isWarn) warn++; else err++;
        }

        public static async Task<RunImportDiagnosticsResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new RunImportDiagnosticsResult();
            if (model == null) { res.Error = "Open the imported part you want diagnosed."; return res; }
            int docType = 0; try { docType = model.GetType(); } catch { }
            if (docType != (int)swDocumentTypes_e.swDocPART)
            { res.Error = "run_import_diagnostics needs an open PART (not an assembly/drawing)."; return res; }

            res.RebuildErrorsBefore = CountRebuildErrors(model);
            await emit("Diagnoser", "scanning for faulty faces / gaps", "run", null);
            try
            {
                var pd = model as PartDoc;
                if (pd == null) { res.Error = "Couldn't get the PartDoc interface."; return res; }
                int ret = pd.ImportDiagnosis(true, true, true, 0);
                res.DiagnosisReturn = ret;
                res.Ran = true;
            }
            catch (Exception ex) { res.Error = ex.GetType().Name + ": " + ex.Message; await emit("Diagnoser", null, "fail", res.Error); return res; }

            try { model.ForceRebuild3(false); } catch { }
            res.RebuildErrorsAfter = CountRebuildErrors(model);

            res.Info = "ImportDiagnosis returned " + res.DiagnosisReturn + "; rebuild errors " + res.RebuildErrorsBefore + " -> " + res.RebuildErrorsAfter;
            await emit("Diagnoser", null, "done", res.Info);
            return res;
        }
    }
}
