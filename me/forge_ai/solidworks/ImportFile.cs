using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class ImportFileResult
    {
        public bool Imported;
        public string SourcePath;
        public string SourceFormat;   // "STEP" | "IGES" | "PARASOLID"
        public string NewPartPath;
        public string NewPartTitle;
        public int Errors;
        public int Warnings;
        public List<string> ErrorFlags = new List<string>();
        public List<string> WarningFlags = new List<string>();
        public double Volume = -1;
        public string Error;
        public string Question;
    }

    /// <summary>
    /// ImportFile (tool #137) — neutral CAD format (STEP/IGES/Parasolid) -> NEW SolidWorks part, with import
    /// diagnostics. "import this STEP file", "bring in the IGES from the supplier".
    ///
    /// Distinct from batch_convert_files (#135, the reverse direction: native SW -> neutral) by direction, and from
    /// open_document (#124, native .SLDPRT/.SLDASM only — see its class doc) by format: this is the ONLY tool that
    /// has to solve opening a neutral file headless.
    ///
    /// docs/kb/landmines.md documents that REOPENING a SolidWorks-self-exported neutral file via OpenDoc6/OpenDoc7
    /// is dead on this build (swFileRequiresRepairError / swConnectedIsOffline respectively). This tool uses a
    /// GENUINELY DIFFERENT API — ISldWorks.LoadFile4 (the direct translator entry point, not the document-open
    /// pipeline) — instrumented BEFORE assuming it shares that fate, per the "reflect + instrument first" rule.
    /// If LoadFile4 also comes back null/empty here, this parks alongside that landmine; do not force it.
    ///
    /// On success, LoadFile4 hands back an in-memory imported document. "Into new part" means that has to be
    /// materialized as a real .SLDPRT on disk — SaveAs to "&lt;source-folder&gt;\forge-imported\&lt;name&gt;.SLDPRT",
    /// the same content-addressed output-folder convention BatchConvertFiles (135) uses for its exports. Unlike
    /// BatchConvertFiles the new document is left OPEN as the active doc afterward (same convention as CreatePart)
    /// — importing is "give me a working part", not a fire-and-forget export.
    /// </summary>
    public static class ImportFile
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (!Regex.IsMatch(c, @"\b(import|bring in)\b")) return false;
            return Regex.IsMatch(c, @"\.(step|stp|iges|igs|x_t|x_b)\b")
                || Regex.IsMatch(c, @"\b(step|iges|parasolid)\b");
        }

        // shared with GroundTruth.MeasureImportFile, which re-derives the SAME source path independently.
        public static string ExtractPath(string intent)
        {
            if (string.IsNullOrEmpty(intent)) return null;
            var q = Regex.Match(intent, "[\"']([^\"']+\\.(?:step|stp|iges|igs|x_t|x_b))[\"']", RegexOptions.IgnoreCase);
            if (q.Success) return q.Groups[1].Value.Trim();
            var m = Regex.Match(intent, @"([a-zA-Z]:\\.+?\.(?:step|stp|iges|igs|x_t|x_b)|\\\\[^\s].+?\.(?:step|stp|iges|igs|x_t|x_b))", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value.Trim();
            return null;
        }

        private static string ResolveFormat(string path)
        {
            string ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            if (ext == "stp") return "STEP";
            if (ext == "igs") return "IGES";
            if (ext == "x_t" || ext == "x_b") return "PARASOLID";
            return ext.ToUpperInvariant();
        }

        private static readonly (int bit, string name)[] ErrorBits =
        {
            ((int)swFileLoadError_e.swGenericError, "swGenericError"),
            ((int)swFileLoadError_e.swFileNotFoundError, "swFileNotFoundError"),
            ((int)swFileLoadError_e.swIdMatchError, "swIdMatchError"),
            ((int)swFileLoadError_e.swInvalidFileTypeError, "swInvalidFileTypeError"),
            ((int)swFileLoadError_e.swFutureVersion, "swFutureVersion"),
            ((int)swFileLoadError_e.swLowResourcesError, "swLowResourcesError"),
            ((int)swFileLoadError_e.swNoDisplayData, "swNoDisplayData"),
            ((int)swFileLoadError_e.swFileRequiresRepairError, "swFileRequiresRepairError"),
            ((int)swFileLoadError_e.swFileCriticalDataRepairError, "swFileCriticalDataRepairError"),
            ((int)swFileLoadError_e.swApplicationBusy, "swApplicationBusy"),
            ((int)swFileLoadError_e.swConnectedIsOffline, "swConnectedIsOffline"),
        };

        private static readonly (int bit, string name)[] WarningBits =
        {
            ((int)swFileLoadWarning_e.swFileLoadWarning_IdMismatch, "IdMismatch"),
            ((int)swFileLoadWarning_e.swFileLoadWarning_NeedsRegen, "NeedsRegen"),
            ((int)swFileLoadWarning_e.swFileLoadWarning_ModelOutOfDate, "ModelOutOfDate"),
            ((int)swFileLoadWarning_e.swFileLoadWarning_AutomaticRepair, "AutomaticRepair"),
            ((int)swFileLoadWarning_e.swFileLoadWarning_CriticalDataRepair, "CriticalDataRepair"),
            ((int)swFileLoadWarning_e.swFileLoadWarning_MissingExternalReferences, "MissingExternalReferences"),
        };

        private static List<string> Decode(int flags, (int bit, string name)[] table)
        {
            var names = new List<string>();
            foreach (var (bit, name) in table) if (bit != 0 && (flags & bit) == bit) names.Add(name);
            return names;
        }

        public static async Task<ImportFileResult> Run(ISldWorks app, IModelDoc2 model, string intent, string attachedFile, Func<string, string, string, string, Task> emit)
        {
            var res = new ImportFileResult();
            await emit("Scout", "finding the neutral file to import", "run", null);

            string path = ExtractPath(intent);
            if (path == null && !string.IsNullOrEmpty(attachedFile)) path = attachedFile;
            if (path == null)
            {
                await emit("Scout", null, "done", "no file resolved");
                res.Question = "Which STEP/IGES/Parasolid file should I import? Attach it or give me the full path.";
                return res;
            }
            if (!File.Exists(path))
            {
                await emit("Scout", null, "done", "not found");
                res.Error = "Couldn't find \"" + path + "\".";
                return res;
            }
            res.SourcePath = path;
            res.SourceFormat = ResolveFormat(path);
            await emit("Scout", null, "done", Path.GetFileName(path) + " (" + res.SourceFormat + ")");

            await emit("Importer", "importing via LoadFile4", "run", null);
            IModelDoc2 imported = null;
            try
            {
                int errs = 0;
                imported = app.LoadFile4(path, "", null, ref errs) as IModelDoc2;
                res.Errors = errs;
                res.ErrorFlags = Decode(errs, ErrorBits);
            }
            catch (Exception ex) { res.Error = ex.GetType().Name + ": " + ex.Message; }

            if (imported == null)
            {
                await emit("Importer", null, "fail", "LoadFile4 returned nothing (errs=" + res.Errors + (res.ErrorFlags.Count > 0 ? ", " + string.Join("|", res.ErrorFlags) : "") + ")");
                if (string.IsNullOrEmpty(res.Error))
                    res.Error = "Import failed — LoadFile4 returned no document (errs=" + res.Errors + (res.ErrorFlags.Count > 0 ? ": " + string.Join(", ", res.ErrorFlags) : "") + ").";
                return res;
            }
            await emit("Importer", null, "done", "imported into an in-memory document");

            await emit("Materializer", "saving as a new part", "run", null);
            try
            {
                string folder = Path.GetDirectoryName(path);
                string outDir = Path.Combine(folder, "forge-imported");
                Directory.CreateDirectory(outDir);
                // "-imported" suffix (not just the bare source name) — SolidWorks identifies open documents by
                // TITLE, and a same-session collision with an already-open source file of the same base name
                // (e.g. a self-exported pattern-block.STEP next to the still-open pattern-block.SLDPRT anchor)
                // corrupts GetOpenDocumentByName lookups for BOTH documents, not just this one.
                string outPath = Path.Combine(outDir, Path.GetFileNameWithoutExtension(path) + "-imported.SLDPRT");

                int e2 = 0, w2 = 0;
                bool ok = imported.Extension.SaveAs(outPath, (int)swSaveAsVersion_e.swSaveAsCurrentVersion, (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, ref e2, ref w2);
                res.Warnings = w2;
                res.WarningFlags = Decode(w2, WarningBits);
                if (!ok || !File.Exists(outPath))
                {
                    res.Error = "Imported the geometry but couldn't save it as a new part (SaveAs returned " + ok + ", errs=" + e2 + ", warns=" + w2 + ").";
                    await emit("Materializer", null, "fail", res.Error);
                    return res;
                }

                try { res.Volume = imported.Extension.CreateMassProperty().Volume; } catch { }
                if (res.Volume <= 0)
                {
                    res.Error = "Saved a new part, but it reports zero/no volume — treating as an empty/failed import.";
                    await emit("Materializer", null, "fail", res.Error);
                    return res;
                }

                res.NewPartPath = outPath;
                try { res.NewPartTitle = imported.GetTitle(); } catch { }
                res.Imported = true;
                await emit("Materializer", null, "done", Path.GetFileName(outPath) + " (" + res.Volume.ToString("0.######") + " m^3)");
            }
            catch (Exception ex) { res.Error = ex.GetType().Name + ": " + ex.Message; }

            return res;
        }
    }
}
