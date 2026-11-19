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
    public class ExportedDrawingRow
    {
        public string SourceName;
        public string SourcePath;
        public string OutputPath;
        public int EntityRecords = -1; // independent content check: real DXF entity records found in the export
        public bool Converted;         // SaveAs succeeded, file on disk, AND it contains real entity data (fail-closed)
        public string Error;
    }

    public class BatchExportDrawingsResult
    {
        public string SourceFolder;
        public string OutputFolder;
        public string TargetFormat;   // "DXF" | "DWG"
        public int Candidates;
        public int Converted;
        public int Failed;
        public List<ExportedDrawingRow> Files = new List<ExportedDrawingRow>();
        public string Info;
        public string Error;
    }

    /// <summary>
    /// BatchExportDrawings (tool #134) — folder-level .SLDDRW -> flat DXF/DWG export. The genuine gap left after
    /// tool 135 (batch_convert_files, native part -> STEP/IGES/Parasolid) and DrawingPkg (demo #9, drawing -> PDF
    /// with dangling-dim repair): neither one exports a drawing SHEET to a flat 2D format. DrawingPkg explicitly
    /// excludes dxf/dwg wording (that's sheet-metal flat-pattern territory in its own comment), and FlatDxf (tool
    /// 174) only exports sheet-metal flat patterns off a PART/ASSEMBLY, never a drawing view. So "batch export
    /// these drawings to dxf" had no live handler before this.
    ///
    ///   Scout   — resolve WHICH drawings. An explicit filename mentioned in the command (whole-token match, not a
    ///             word-bag scan) scopes to just that file; "all"/"every"/"batch" wording scopes to every .SLDDRW
    ///             sibling in the anchor drawing's folder; with neither, the currently-open drawing itself is the
    ///             sole candidate (a sane default action, not a hedge — Rule: doable tasks must ACT).
    ///   Export  — each candidate is opened silently (or reused if it's the anchor doc), exported via
    ///             IModelDocExtension.SaveAs to "<folder>\forge-drawing-export\<name>.<ext>" — the SAME generic
    ///             SaveAs call already proven live for PDF (DrawingPkg), STEP/IGES/Parasolid (BatchConvertFiles)
    ///             and native copies (SaveDocumentAs) — then closed WITHOUT saving; the source .SLDDRW is never
    ///             written.
    ///   Verify  — FAIL CLOSED (Rule #6): a SaveAs returning true is not trusted alone. The export is read back as
    ///             TEXT and its real DXF entity records are independently counted (occurrences of a defined-entity
    ///             tag — LINE/CIRCLE/ARC/LWPOLYLINE/TEXT/DIMENSION/INSERT — as its own line, the standard ASCII DXF
    ///             group-code layout) and required to clear a floor, catching a SaveAs that reports success over an
    ///             empty/stub file, the same content-verification shape BatchConvertFiles already uses for STEP.
    /// </summary>
    public static class BatchExportDrawings
    {
        private const int MinEntityRecords = 1; // even a blank sheet's border/title-block writes >=1 entity

        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            bool drw = Regex.IsMatch(c, @"\b(drawing|drawings|drw|slddrw|sheet|sheets)\b");
            bool fmt = Regex.IsMatch(c, @"\b(dxf|dwg)\b");
            bool op = Regex.IsMatch(c, @"\b(export|save|batch|convert|write|output)\b");
            return drw && fmt && op;
        }

        private static string ResolveExt(string cmd)
        {
            return Regex.IsMatch(cmd, @"\bdwg\b") ? ".dwg" : ".dxf";
        }

        // independent post-write content check: DXF's ASCII group-code layout writes each entity's TYPE as its own
        // line (e.g. a lone "LINE" line) inside the ENTITIES section — count those, the same "real geometry record"
        // proxy BatchConvertFiles uses for STEP's CARTESIAN_POINT.
        private static int CountDxfEntityRecords(string path)
        {
            try
            {
                var lines = File.ReadAllLines(path);
                return lines.Count(l =>
                {
                    string t = l.Trim();
                    return t == "LINE" || t == "CIRCLE" || t == "ARC" || t == "LWPOLYLINE" ||
                           t == "POLYLINE" || t == "TEXT" || t == "MTEXT" || t == "DIMENSION" || t == "INSERT";
                });
            }
            catch { return 0; }
        }

        public static async Task<BatchExportDrawingsResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new BatchExportDrawingsResult();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocDRAWING)
            { res.Error = "Open a drawing (.SLDDRW) — batch-export works on drawing files, not parts/assemblies."; return res; }

            string selfPath = null; try { selfPath = model.GetPathName(); } catch { }
            if (string.IsNullOrWhiteSpace(selfPath))
            { res.Error = "This drawing has never been saved — nothing to locate its folder from."; return res; }

            string folder = null; try { folder = Path.GetDirectoryName(selfPath); } catch { }
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            { res.Error = "Couldn't read the folder this drawing lives in."; return res; }
            res.SourceFolder = folder;

            string cmd = (intent ?? "").ToLowerInvariant();
            string ext = ResolveExt(cmd);
            res.TargetFormat = ext.TrimStart('.').ToUpperInvariant();

            string outDir = Path.Combine(folder, "forge-drawing-export");
            try { Directory.CreateDirectory(outDir); }
            catch (Exception ex) { res.Error = "Couldn't create the output folder (" + ex.GetType().Name + ")."; return res; }
            res.OutputFolder = outDir;

            await emit("Scout", "finding candidate drawings to export", "run", null);

            var allDrw = Directory.GetFiles(folder, "*.slddrw").Where(f => !Path.GetFileName(f).StartsWith("~$")).ToArray();
            var named = allDrw.Where(p =>
            {
                string bn = Path.GetFileNameWithoutExtension(p).ToLowerInvariant();
                string bnSpaced = bn.Replace("-", " ").Replace("_", " ");
                return cmd.Contains(bn) || cmd.Contains(bnSpaced);
            }).ToList();

            bool wholeFolder = Regex.IsMatch(cmd, @"\b(all|every|batch)\b");
            List<string> candidates;
            if (named.Count > 0) candidates = named;
            else if (wholeFolder) candidates = allDrw.ToList();
            else candidates = new List<string> { selfPath }; // default: just the open drawing — act, don't ask

            res.Candidates = candidates.Count;
            await emit("Scout", null, "done", res.Candidates + " candidate drawing(s) in " + Path.GetFileName(folder));

            await emit("Exporter", "exporting each drawing to " + res.TargetFormat, "run", null);
            foreach (var src in candidates)
            {
                var row = new ExportedDrawingRow { SourceName = Path.GetFileName(src), SourcePath = src };
                res.Files.Add(row);

                bool sameAsOpen = string.Equals(src, selfPath, StringComparison.OrdinalIgnoreCase);
                IModelDoc2 doc = sameAsOpen ? model : null;
                bool openedHere = false;
                try
                {
                    if (doc == null)
                    {
                        int oe = 0, ow = 0;
                        doc = app.OpenDoc6(src, (int)swDocumentTypes_e.swDocDRAWING, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref oe, ref ow) as IModelDoc2;
                        openedHere = doc != null;
                    }
                    if (doc == null) { row.Error = "Couldn't open the source drawing."; continue; }

                    string outPath = Path.Combine(outDir, Path.GetFileNameWithoutExtension(src) + ext);
                    row.OutputPath = outPath;
                    try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }
                    int e2 = 0, w2 = 0;
                    bool ok = doc.Extension.SaveAs(outPath, (int)swSaveAsVersion_e.swSaveAsCurrentVersion, (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, ref e2, ref w2);
                    if (!ok || !File.Exists(outPath))
                    { row.Error = "SaveAs returned " + ok + " (errs=" + e2 + ", warns=" + w2 + ")"; continue; }

                    row.EntityRecords = CountDxfEntityRecords(outPath);
                    if (row.EntityRecords < MinEntityRecords)
                    { row.Error = "Export landed but content check found only " + row.EntityRecords + " entity record(s) (expected >= " + MinEntityRecords + ") — treating as a stub/empty write."; continue; }

                    row.Converted = true;
                }
                catch (Exception ex) { row.Error = ex.GetType().Name + ": " + ex.Message; }
                finally { if (openedHere && doc != null) { try { app.CloseDoc(doc.GetTitle()); } catch { } } }
            }

            res.Converted = res.Files.Count(f => f.Converted);
            res.Failed = res.Files.Count - res.Converted;
            await emit("Exporter", null, res.Failed == 0 ? "done" : "fail",
                res.Converted + "/" + res.Candidates + " exported to " + res.TargetFormat + " (content-verified)");

            res.Info = res.Converted + " of " + res.Candidates + " drawing(s) exported to " + res.TargetFormat + " in " + outDir + ", each independently content-verified." +
                       (res.Failed > 0 ? " " + res.Failed + " failed: " + string.Join(", ", res.Files.Where(f => !f.Converted).Select(f => f.SourceName + " (" + f.Error + ")")) + "." : "") +
                       " Source drawing(s) untouched.";
            return res;
        }
    }
}
