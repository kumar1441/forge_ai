using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class FlatDxfResult
    {
        public int SheetMetalParts;   // parts that ARE sheet metal (a flat pattern can be produced)
        public int Exported;          // DXFs written AND verified on disk
        public int Skipped;           // parts skipped: not sheet metal
        public int Failed;            // sheet-metal parts we tried but couldn't export
        public string OutputDir;
        public List<string> Files = new List<string>();
        public string Info;
        public string Error;
        public List<string> Diag = new List<string>();
    }

    /// <summary>
    /// Demo #10 "Laser-ready in one line" — export every sheet-metal part in the open assembly (or the open
    /// part) as a laser-ready flat-pattern DXF into a folder. WRITES DXF files only; NEVER
    /// modifies or saves the source models. Non-sheet-metal parts are skipped cleanly (Rule #4 partial success),
    /// each export is per-item try/catch, and every DXF is VERIFIED on disk before it's counted (Rule #6 — the
    /// ExportToDWG2 return code alone is not trusted). Idempotent: a rerun overwrites the same files (Rule #5).
    /// Verified INDEPENDENTLY by GroundTruth.MeasureFlatDxf (own sheet-metal scan + on-disk DXF count).
    /// </summary>
    public static class FlatDxf
    {
        public static bool IsFlatDxfIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            cmd = cmd.ToLowerInvariant();
            bool dxf = Regex.IsMatch(cmd, @"\b(dxf|dwg|flat[- ]?pattern|laser|laser[- ]?ready|nest)\b");
            bool exp = Regex.IsMatch(cmd, @"\b(export|save|write|flatten|output|generate)\b") || cmd.Contains("flat pattern");
            return dxf && (exp || cmd.Contains("flat"));
        }

        // The output folder is a fixed convention off the model's own directory, so GroundTruth can re-derive it
        // independently without any shared code. Unsaved doc → a temp folder.
        public static string OutputDirFor(IModelDoc2 model)
        {
            string p = null; try { p = model.GetPathName(); } catch { }
            string baseDir = !string.IsNullOrEmpty(p)
                ? Path.GetDirectoryName(p)
                : Path.Combine(Path.GetTempPath(), "forge-flat-dxf");
            return Path.Combine(baseDir, "Forge-Flat-DXF");
        }

        public static async Task<FlatDxfResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new FlatDxfResult();
            res.OutputDir = OutputDirFor(model);
            try { Directory.CreateDirectory(res.OutputDir); }
            catch (Exception ex) { res.Error = "Can't create the output folder (" + res.OutputDir + "): " + ex.Message; return res; }

            // ---- gather the parts to consider: an assembly's UNIQUE parts (once each), or the single open part ----
            await emit("Flatten", "finding sheet-metal parts", "run", null);
            var parts = new Dictionary<string, IModelDoc2>(StringComparer.OrdinalIgnoreCase);
            var asm = model as AssemblyDoc;
            if (asm != null)
            {
                object[] comps = asm.GetComponents(false) as object[];
                foreach (var o in comps ?? new object[0])
                {
                    var c = o as Component2; if (c == null) continue;
                    bool sup = false; try { sup = c.IsSuppressed(); } catch { }
                    if (sup) continue;
                    string pp = null; try { pp = c.GetPathName(); } catch { }
                    if (string.IsNullOrEmpty(pp) || parts.ContainsKey(pp)) continue;
                    IModelDoc2 pd = null; try { pd = c.GetModelDoc2() as IModelDoc2; } catch { }
                    if (pd != null && (int)pd.GetType() == (int)swDocumentTypes_e.swDocPART) parts[pp] = pd;
                }
            }
            else if ((int)model.GetType() == (int)swDocumentTypes_e.swDocPART)
            {
                string pp = null; try { pp = model.GetPathName(); } catch { }
                if (!string.IsNullOrEmpty(pp)) parts[pp] = model;
            }
            else { res.Error = "Open a sheet-metal part, or an assembly that contains sheet-metal parts."; return res; }

            // classify (independent of export): which unique parts are sheet metal
            var sheet = new List<KeyValuePair<string, IModelDoc2>>();
            foreach (var kv in parts) { if (IsSheetMetalPart(kv.Value)) sheet.Add(kv); else res.Skipped++; }
            res.SheetMetalParts = sheet.Count;
            await emit("Flatten", null, "done", sheet.Count + " sheet-metal part(s) · " + res.Skipped + " other part(s) skipped");

            if (sheet.Count == 0)
            {
                res.Info = parts.Count == 0
                    ? "No parts found to export."
                    : "No sheet-metal parts here — " + res.Skipped + " part(s) have no flat pattern to export.";
                return res;
            }

            // suppress the DXF/DWG mapping dialog so an export never blocks waiting on a click (headless-safe); restore after
            bool prior = false; bool toggled = false;
            try { prior = app.GetUserPreferenceToggle((int)swUserPreferenceToggle_e.swDXFDontShowMap); app.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swDXFDontShowMap, true); toggled = true; }
            catch { }

            await emit("Flatten", "exporting flat-pattern DXFs", "run", null);
            int i = 0;
            foreach (var kv in sheet)
            {
                i++;
                string partName = Path.GetFileNameWithoutExtension(kv.Key);
                string dxfPath = Path.Combine(res.OutputDir, Sanitize(partName) + ".dxf");
                string diag = null;
                bool ok = false;
                try { ok = ExportPartFlatDxf(kv.Value, dxfPath, out diag); }
                catch (Exception ex) { diag = "ExportToDWG2 threw: " + ex.Message; }

                // VERIFY on disk — never trust the return code alone (Rule #6). Count only what actually landed.
                bool wrote = false; try { var fi = new FileInfo(dxfPath); wrote = fi.Exists && fi.Length > 0; } catch { }
                if (ok && wrote) { res.Exported++; res.Files.Add(dxfPath); }
                else { res.Failed++; res.Diag.Add(partName + ": " + (diag ?? (wrote ? "wrote but export reported failure" : "no file written"))); }

                // big-set progress so the panel never looks frozen (Rule #11)
                if (sheet.Count > 8 && (i % 5 == 0 || i == sheet.Count))
                    await emit("Flatten", null, "run", "exported " + res.Exported + "/" + sheet.Count + "…");
            }

            if (toggled) { try { app.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swDXFDontShowMap, prior); } catch { } }

            foreach (var d in res.Diag) await emit(null, null, "done", "▸ " + d);
            await emit("Flatten", null, "done", res.Exported + " DXF(s) written to " + res.OutputDir);

            // VERIFY pass (Rule #6): independently count the .dxf files that actually landed in the folder
            await emit("Sentinel", "verifying the DXF files landed", "run", null);
            int onDisk = 0; try { onDisk = Directory.GetFiles(res.OutputDir, "*.dxf").Length; } catch { }
            await emit("Sentinel", null, "done", onDisk + " DXF file(s) in the folder");

            res.Info = res.Failed == 0
                ? res.Exported + " sheet-metal part(s) exported as a laser-ready flat-pattern DXF -> " + res.OutputDir
                  + (res.Skipped > 0 ? "; " + res.Skipped + " non-sheet-metal part(s) skipped." : ".")
                : res.Exported + " exported, " + res.Failed + " failed"
                  + (res.Skipped > 0 ? ", " + res.Skipped + " skipped (not sheet metal)" : "")
                  + " -> " + res.OutputDir + ".";
            return res;
        }

        // ---- export ONE part's flat pattern to a DXF. Sheet-metal action + flat pattern; keeping bends
        //      (SheetMetalOptions = None, NOT RemoveBends) so bend lines ARE in the DXF. NOTE (2026-07-25,
        //      verified against the exported file): on this build everything lands on layer "0" — SW's native
        //      layering does NOT separate bend lines onto their own layer here. TODO: map bend lines to a
        //      dedicated layer (DXF/DWG layer mapping) + verify the layer count in the harness before claiming it.
        //      Reads the part and writes a DXF only — does NOT save/modify the source. Uses IPartDoc. ----
        private static bool ExportPartFlatDxf(IModelDoc2 partDoc, string dxfPath, out string diag)
        {
            diag = null;
            var part = partDoc as IPartDoc;
            if (part == null) { diag = "not a part document"; return false; }
            string src = null; try { src = partDoc.GetPathName(); } catch { }
            if (string.IsNullOrEmpty(src)) { diag = "part has no saved path (unsaved)"; return false; }

            // flat alignment: output origin + X/Y axes (identity — lay the flat on the XY plane)
            double[] align = new double[12];
            align[3] = 1;   // x-axis = (1,0,0)
            align[7] = 1;   // y-axis = (0,1,0)

            bool ok;
            try
            {
                ok = part.ExportToDWG2(
                    dxfPath,
                    src,
                    (int)swExportToDWG_e.swExportToDWG_ExportSheetMetal,
                    true,                                                          // ExportToSingleFile
                    align,                                                         // Alignment
                    false,                                                         // IsXDirFlipped
                    false,                                                         // IsYDirFlipped
                    (int)swExportFlatPatternViewOptions_e.swExportFlatPatternOption_None,  // keep bends → bend lines on own layer
                    null);                                                         // Views
            }
            catch (Exception ex) { diag = "ExportToDWG2 threw: " + ex.Message; return false; }
            if (!ok) diag = "ExportToDWG2 returned false";
            return ok;
        }

        // ---- sheet-metal test: the feature tree carries a SheetMetal / FlatPattern / SM-prefixed feature.
        //      Same signal ModelChecker.bas uses (M10). Feature-tree read only; no geometry mutation. ----
        private static bool IsSheetMetalPart(IModelDoc2 partDoc)
        {
            if (partDoc == null) return false;
            try { if ((int)partDoc.GetType() != (int)swDocumentTypes_e.swDocPART) return false; } catch { return false; }
            var f = partDoc.FirstFeature() as Feature;
            while (f != null)
            {
                string tn = ""; try { tn = f.GetTypeName2(); } catch { }
                if (!string.IsNullOrEmpty(tn) &&
                    (tn.IndexOf("SheetMetal", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     tn.IndexOf("FlatPattern", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     tn.IndexOf("SMBaseFlange", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     tn.StartsWith("SM", StringComparison.Ordinal)))
                    return true;
                f = f.GetNextFeature() as Feature;
            }
            return false;
        }

        private static string Sanitize(string name)
        {
            if (string.IsNullOrEmpty(name)) return "part";
            foreach (var ch in Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
            return name;
        }
    }
}
