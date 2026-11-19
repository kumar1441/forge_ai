using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class ImportDxfToSketchResult
    {
        public string FilePath;
        public string PlaneName;
        public string SketchName;
        public int Segments;
        public bool Applied;          // geometry-derived: a new sketch FEATURE with segments must exist
        public bool AlreadyDone;
        public string Diag;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// ImportDxfToSketch (tool 205, "import_dxf_to_sketch") — bring a customer's 2D DXF/DWG profile in as sketch
    /// entities: "import this dxf into a sketch", "insert the dwg profile on the front plane". The reverse
    /// direction of FlatDxf (tool 174, which EXPORTS a flat pattern out); this one READS an external file IN, so
    /// it requires the import/insert verb and excludes FlatDxf's export/flatten verbs so the two never shadow.
    ///
    /// API is `IFeatureManager.InsertDwgOrDxfFile(string FileName) : Feature` — a NEW surface for this codebase
    /// (reflected live off the installed interop DLL first: confirmed real, distinct from
    /// `IModelDocExtension.InsertDwgOrDxfFile` which returns bool and targets drawing-sheet import). Selecting a
    /// plane first (no sketch pre-opened) mirrors the Insert > DXF/DWG menu workflow for a part: SolidWorks creates
    /// its own new sketch feature on the selected plane from the file's entities. The DXF/DWG mapping dialog is
    /// suppressed via the same `swDXFDontShowMap` toggle FlatDxf already uses headless (restored after in a
    /// finally), so this never blocks waiting on a click.
    ///
    /// Success is judged by GEOMETRY: a new ProfileFeature must appear with at least one non-construction segment
    /// (an empty/failed import leaves nothing new, same discard-on-exit landmine as CreateSketch). Tagged
    /// "Forge-DxfImport" for idempotency; never saves — one Ctrl+Z removes it.
    /// </summary>
    public static class ImportDxfToSketch
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            bool file = Regex.IsMatch(c, @"\b(dxf|dwg)\b");
            if (!file) return false;
            if (Regex.IsMatch(c, @"\b(export|save|write|flatten|output|generate)\b") || c.Contains("flat pattern")) return false;   // FlatDxf (174) owns those
            bool verb = Regex.IsMatch(c, @"\b(import|insert|bring in|load|open)\b");
            return verb;
        }

        public static async Task<ImportDxfToSketchResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new ImportDxfToSketchResult();
            var part = model as PartDoc;
            if (part == null) { res.Error = "Open a part (.SLDPRT) to import a DXF/DWG profile into."; return res; }

            string path = ParseFilePath(intent);
            if (string.IsNullOrEmpty(path)) { res.Error = "No DXF/DWG file path found in the command."; return res; }
            if (!File.Exists(path)) { res.Error = "DXF/DWG file not found: " + path; return res; }
            res.FilePath = path;

            string plane = ParsePlane(intent);
            res.PlaneName = plane;

            var existing = FindTaggedFeature(model);
            if (existing != null)
            {
                res.AlreadyDone = true; res.Applied = true; res.SketchName = SafeName(existing);
                res.Segments = SegmentCount(existing);
                res.Info = "A DXF import (" + res.SketchName + ") is already here — nothing to do.";
                await emit("Draftsman", null, "done", res.SketchName + " already present — nothing to do");
                return res;
            }

            await emit("Draftsman", "importing " + Path.GetFileName(path) + " onto " + plane, "run", null);

            bool priorToggle = false; bool toggled = false;
            try { priorToggle = app.GetUserPreferenceToggle((int)swUserPreferenceToggle_e.swDXFDontShowMap); app.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swDXFDontShowMap, true); toggled = true; }
            catch { }

            var beforeNames = new HashSet<string>(SketchFeatureNames(model));
            Feature ret = null;
            bool retWasNull = false;
            string importDiag = null;
            try
            {
                SelectPlane(model, plane);       // NO active sketch pre-opened — mirrors the Insert > DXF/DWG menu
                var fm = model.FeatureManager;    // workflow for a part: SW is expected to create its own new sketch
                object importData = null;
                try
                {
                    importData = app.GetImportFileData(path);
                    var idd = importData as IImportDxfDwgData;
                    if (idd != null) { try { idd.ImportMethod[""] = (int)swImportDxfDwg_ImportMethod_e.swImportDxfDwg_ImportToPartSketch; } catch { } }
                }
                catch (Exception ex) { importDiag = "GetImportFileData threw: " + ex.Message; }

                ret = (importData != null
                    ? fm.InsertDwgOrDxfFile2(path, importData)
                    : fm.InsertDwgOrDxfFile(path)) as Feature;
                retWasNull = ret == null;
                model.ClearSelection2(true);
                model.ForceRebuild3(false);
            }
            catch (Exception ex) { res.Error = "Importing the DXF/DWG file failed: " + ex.Message; return res; }
            finally { if (toggled) { try { app.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swDXFDontShowMap, priorToggle); } catch { } } }

            var created = ret ?? NewSketchFeature(model, beforeNames);
            int segs = created != null ? SegmentCount(created) : 0;
            if (created == null || segs == 0)
            {
                res.Diag = "retWasNull=" + retWasNull + " createdFound=" + (created != null) + " importDiag=" + importDiag;
                res.Error = "The DXF/DWG import produced no sketch geometry.";
                await emit("Draftsman", null, "fail", res.Error);
                return res;
            }

            try { created.Name = "Forge-DxfImport"; } catch { }
            res.SketchName = SafeName(created);
            res.Segments = segs;
            res.Applied = true;
            res.Diag = "plane=" + plane + " name=" + res.SketchName + " segments=" + segs;

            await emit("Draftsman", null, "done", res.SketchName + " imported (" + segs + " segment(s))");

            res.Info = "Imported " + Path.GetFileName(path) + " as a new sketch (" + res.SketchName + ", " + segs + " segment(s)) on " + plane + ". One Ctrl+Z removes it; Forge didn't save.";
            return res;
        }

        // ---------- parsing ----------
        private static string ParseFilePath(string intent)
        {
            if (string.IsNullOrEmpty(intent)) return null;
            var m = Regex.Match(intent, @"[A-Za-z]:\\[^\s""']+?\.(?:dxf|dwg)", RegexOptions.IgnoreCase);
            return m.Success ? m.Value : null;
        }

        private static string ParsePlane(string intent)
        {
            string c = (intent ?? "").ToLowerInvariant();
            if (Regex.IsMatch(c, @"\btop\s*plane\b")) return "Top Plane";
            if (Regex.IsMatch(c, @"\bright\s*plane\b")) return "Right Plane";
            return "Front Plane";
        }

        // ---------- geometry helpers ----------
        private static void SelectPlane(IModelDoc2 model, string plane)
        { try { model.Extension.SelectByID2(plane, "PLANE", 0, 0, 0, false, 0, null, 0); } catch { } }

        private static int SegmentCount(Feature f)
        {
            int segs = 0;
            try
            {
                var sk = f.GetSpecificFeature2() as Sketch;
                if (sk != null)
                    foreach (var o in (sk.GetSketchSegments() as object[]) ?? new object[0])
                    {
                        var seg = o as SketchSegment; if (seg == null) continue;
                        bool constr = false; try { constr = seg.ConstructionGeometry; } catch { }
                        if (!constr) segs++;
                    }
            }
            catch { }
            return segs;
        }

        private static IEnumerable<string> SketchFeatureNames(IModelDoc2 model)
        {
            var list = new List<string>();
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                string tn = ""; try { tn = f.GetTypeName2(); } catch { }
                if (tn == "ProfileFeature") list.Add(SafeName(f));
                f = f.GetNextFeature() as Feature;
            }
            return list;
        }

        private static Feature NewSketchFeature(IModelDoc2 model, HashSet<string> before)
        {
            Feature found = null;
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                string tn = ""; try { tn = f.GetTypeName2(); } catch { }
                if (tn == "ProfileFeature" && !before.Contains(SafeName(f))) found = f;
                f = f.GetNextFeature() as Feature;
            }
            return found;
        }

        private static Feature FindTaggedFeature(IModelDoc2 model)
        {
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                string nm = SafeName(f);
                if (nm != null && nm.Equals("Forge-DxfImport", StringComparison.OrdinalIgnoreCase)) return f;
                f = f.GetNextFeature() as Feature;
            }
            return null;
        }

        private static string SafeName(Feature f) { try { return f.Name; } catch { return null; } }
    }
}
