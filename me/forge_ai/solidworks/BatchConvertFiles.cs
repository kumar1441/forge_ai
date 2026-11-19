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
    public class ConvertedFileRow
    {
        public string SourceName;
        public string SourcePath;
        public string OutputPath;
        public double SourceVolumeM3 = -1;
        public int ExportEntityCount = -1; // independent content check: real geometry records found in the export
        public bool Converted;     // SaveAs succeeded, file on disk, AND it contains real geometry data (fail-closed)
        public string Error;
    }

    public class BatchConvertFilesResult
    {
        public string SourceFolder;
        public string OutputFolder;
        public string TargetFormat;     // "STEP" | "IGES" | "PARASOLID"
        public int Candidates;
        public int Converted;
        public int Failed;
        public List<ConvertedFileRow> Files = new List<ConvertedFileRow>();
        public string Info;
        public string Error;
        public string Question;
    }

    /// <summary>
    /// BatchConvertFiles (tool #135) — folder-level native-SW-part -> neutral CAD format (STEP/IGES/Parasolid)
    /// conversion. "convert pattern-block to STEP", "batch convert every part in this folder to step".
    ///
    ///   Scout    — resolve WHICH source parts. An explicit filename mentioned in the command (matched as a whole,
    ///              hyphens/underscores folded to spaces — NOT a word-bag scan, which would let a generic word like
    ///              "block" cross-match unrelated folder-mates such as giant-block/global-block) wins and scopes to
    ///              just that file. With no name match, "all"/"every"/"batch" wording scopes to the WHOLE folder;
    ///              otherwise this asks rather than guessing which files the user meant (Rule #2).
    ///   Convert  — each candidate is opened silently (or reused if it's the doc already open), exported via
    ///              IModelDocExtension.SaveAs to "<folder>\forge-converted\<name>.<ext>", then closed WITHOUT saving
    ///              — the source document and file are never written to.
    ///   Verify   — FAIL CLOSED (Rule #6): a SaveAs that returns true is not trusted on its own. Reopening the just-
    ///              written export to re-measure it geometrically was tried first and is NOT viable on this build —
    ///              OpenDoc6(swDocPART) on a fresh self-exported STEP always returns swFileRequiresRepairError
    ///              (0x200000, no Silent-mode workaround exists for it), and OpenDoc7 + DocumentSpecification
    ///              (whose AutoRepair/CriticalDataRepair flags exist for exactly that error) instead fails with
    ///              swConnectedIsOffline regardless of ReadOnly — OpenDoc7 appears to route through a 3DEXPERIENCE
    ///              PLM check even for a fully local file on this build. Proven empirically (3 separate rebuild+
    ///              harness cycles, not theoretical) — see docs/kb/landmines.md. So instead the export is verified
    ///              by CONTENT: the file is read back as text and its real geometry records are independently
    ///              counted (STEP: CARTESIAN_POINT occurrences) and required to clear a floor — catching the actual
    ///              failure mode this guards against (SaveAs reporting success over an empty/stub file) without
    ///              needing a second SolidWorks document open.
    ///
    /// Distinct from import_file (#137, neutral format -> NEW SolidWorks part) by direction: this only ever reads a
    /// native SW document and writes a neutral export; it never creates a .SLDPRT.
    /// </summary>
    public static class BatchConvertFiles
    {
        private const int MinEntityCount = 4; // even a trivial box has 8 vertices; a stub/empty write has 0

        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (!Regex.IsMatch(c, @"\b(convert|export|batch[- ]?convert)\b")) return false;
            return Regex.IsMatch(c, @"\b(step|stp|iges|igs|parasolid|x_t)\b");
        }

        private static string ResolveExt(string cmd)
        {
            if (Regex.IsMatch(cmd, @"\b(iges|igs)\b")) return ".IGES";
            if (Regex.IsMatch(cmd, @"\b(parasolid|x_t)\b")) return ".x_t";
            return ".STEP"; // covers "step"/"stp", and is the default with no format named
        }

        // independent post-write content check (see the class doc for why this replaces a SW reopen). STEP counts
        // CARTESIAN_POINT records (every vertex writes one, so real B-rep geometry always has several); IGES counts
        // Directory Entry lines (column 73 == 'D'); Parasolid just requires the real magic header plus a non-trivial
        // size, since its binary/compact text layout doesn't have an easy per-line entity marker.
        private static int CountGeometryRecords(string path, string ext)
        {
            try
            {
                if (string.Equals(ext, ".STEP", StringComparison.OrdinalIgnoreCase))
                    return Regex.Matches(File.ReadAllText(path), "CARTESIAN_POINT").Count;
                if (string.Equals(ext, ".IGES", StringComparison.OrdinalIgnoreCase))
                    return File.ReadAllLines(path).Count(l => l.Length >= 73 && l[72] == 'D');
                if (string.Equals(ext, ".x_t", StringComparison.OrdinalIgnoreCase))
                {
                    string head = null;
                    using (var sr = new StreamReader(path)) head = sr.ReadLine();
                    var fi = new FileInfo(path);
                    return (head != null && head.Contains("PARASOLID") && fi.Length > 200) ? MinEntityCount : 0;
                }
            }
            catch { }
            return 0;
        }

        public static async Task<BatchConvertFilesResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new BatchConvertFilesResult();
            if (model == null) { res.Error = "Open the part (or a part in the folder you want converted)."; return res; }

            string selfPath = null; try { selfPath = model.GetPathName(); } catch { }
            if (string.IsNullOrWhiteSpace(selfPath))
            { res.Error = "This document has never been saved — nothing to locate its folder from."; return res; }

            string folder = null; try { folder = Path.GetDirectoryName(selfPath); } catch { }
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            { res.Error = "Couldn't read the folder this document lives in."; return res; }
            res.SourceFolder = folder;

            string cmd = (intent ?? "").ToLowerInvariant();
            res.TargetFormat = ResolveExt(cmd).TrimStart('.').ToUpperInvariant();

            string outDir = Path.Combine(folder, "forge-converted");
            try { Directory.CreateDirectory(outDir); }
            catch (Exception ex) { res.Error = "Couldn't create the output folder (" + ex.GetType().Name + ")."; return res; }
            res.OutputFolder = outDir;

            await emit("Scout", "finding candidate parts to convert", "run", null);

            var allParts = Directory.GetFiles(folder, "*.SLDPRT");
            var named = allParts.Where(p =>
            {
                string bn = Path.GetFileNameWithoutExtension(p).ToLowerInvariant();
                string bnSpaced = bn.Replace("-", " ").Replace("_", " ");
                return cmd.Contains(bn) || cmd.Contains(bnSpaced);
            }).ToList();

            bool wholeFolder = Regex.IsMatch(cmd, @"\b(all|every|batch)\b");
            List<string> candidates;
            if (named.Count > 0) candidates = named;
            else if (wholeFolder) candidates = allParts.ToList();
            else
            {
                res.Question = "Which part(s) should I convert? Name one, or say \"convert every part in this folder\".";
                return res;
            }

            res.Candidates = candidates.Count;
            await emit("Scout", null, "done", res.Candidates + " candidate part(s) in " + Path.GetFileName(folder));

            string ext = ResolveExt(cmd);
            await emit("Converter", "exporting each part to " + res.TargetFormat, "run", null);
            foreach (var src in candidates)
            {
                var row = new ConvertedFileRow { SourceName = Path.GetFileName(src), SourcePath = src };
                res.Files.Add(row);

                bool sameAsOpen = string.Equals(src, selfPath, StringComparison.OrdinalIgnoreCase);
                IModelDoc2 doc = sameAsOpen ? model : null;
                bool openedHere = false;
                try
                {
                    if (doc == null)
                    {
                        int oe = 0, ow = 0;
                        doc = app.OpenDoc6(src, (int)swDocumentTypes_e.swDocPART, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref oe, ref ow) as IModelDoc2;
                        openedHere = doc != null;
                    }
                    if (doc == null) { row.Error = "Couldn't open the source part."; continue; }

                    try { row.SourceVolumeM3 = doc.Extension.CreateMassProperty().Volume; } catch { }

                    string outPath = Path.Combine(outDir, Path.GetFileNameWithoutExtension(src) + ext);
                    row.OutputPath = outPath;
                    int e2 = 0, w2 = 0;
                    bool ok = doc.Extension.SaveAs(outPath, (int)swSaveAsVersion_e.swSaveAsCurrentVersion, (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, ref e2, ref w2);
                    if (!ok || !File.Exists(outPath))
                    { row.Error = "SaveAs returned " + ok + " (errs=" + e2 + ", warns=" + w2 + ")"; continue; }

                    row.ExportEntityCount = CountGeometryRecords(outPath, ext);
                    if (row.ExportEntityCount < MinEntityCount)
                    { row.Error = "Export landed but content check found only " + row.ExportEntityCount + " geometry record(s) (expected >= " + MinEntityCount + ") — treating as a stub/empty write."; continue; }

                    row.Converted = true;
                }
                catch (Exception ex) { row.Error = ex.GetType().Name + ": " + ex.Message; }
                finally { if (openedHere && doc != null) { try { app.CloseDoc(doc.GetTitle()); } catch { } } }
            }

            res.Converted = res.Files.Count(f => f.Converted);
            res.Failed = res.Files.Count - res.Converted;
            await emit("Converter", null, res.Failed == 0 ? "done" : "fail",
                res.Converted + "/" + res.Candidates + " converted to " + res.TargetFormat + " (content-verified)");

            res.Info = res.Converted + " of " + res.Candidates + " part(s) converted to " + res.TargetFormat + " in " + outDir + ", each independently content-verified." +
                       (res.Failed > 0 ? " " + res.Failed + " failed: " + string.Join(", ", res.Files.Where(f => !f.Converted).Select(f => f.SourceName + " (" + f.Error + ")")) + "." : "") +
                       " Source files untouched.";
            return res;
        }
    }
}
