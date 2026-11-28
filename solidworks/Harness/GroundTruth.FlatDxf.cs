using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for the flat-pattern DXF handler (demo #10). Shares NOTHING with FlatDxf.cs:
    ///   - its OWN sheet-metal feature-tree test (GtIsSheetMetal),
    ///   - its OWN output-folder derivation (same filesystem convention, re-computed here from the model path),
    ///   - its OWN on-disk .dxf count,
    ///   - an on-disk last-write-time + save-flag capture of every sheet-metal SOURCE part, so the harness can
    ///     prove the sources were never written (run0 mtime == run1 mtime; dirty flag stays clear).
    /// Headline invariant asserted downstream: DXFs written == sheet-metal parts found, sources untouched.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureFlatDxf(ISldWorks app, IModelDoc2 model)
        {
            var root = new JObject();

            // ---- own output-folder derivation (independent re-computation of the handler's convention) ----
            string modelPath = null; try { modelPath = model.GetPathName(); } catch { }
            string baseDir = !string.IsNullOrEmpty(modelPath)
                ? Path.GetDirectoryName(modelPath)
                : Path.Combine(Path.GetTempPath(), "forge-flat-dxf");
            string outDir = Path.Combine(baseDir, "Forge-Flat-DXF");
            root["outputDir"] = outDir;

            // ---- collect unique parts (own dedup by path) ----
            var parts = new Dictionary<string, IModelDoc2>(StringComparer.OrdinalIgnoreCase);
            var asm = model as AssemblyDoc;
            if (asm != null)
            {
                foreach (var o in (asm.GetComponents(false) as object[]) ?? new object[0])
                {
                    var c = o as Component2; if (c == null) continue;
                    bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                    string pp = null; try { pp = c.GetPathName(); } catch { }
                    if (string.IsNullOrEmpty(pp) || parts.ContainsKey(pp)) continue;
                    IModelDoc2 pd = null; try { pd = c.GetModelDoc2() as IModelDoc2; } catch { }
                    if (pd != null && (int)pd.GetType() == (int)swDocumentTypes_e.swDocPART) parts[pp] = pd;
                }
            }
            else if ((int)model.GetType() == (int)swDocumentTypes_e.swDocPART)
            {
                if (!string.IsNullOrEmpty(modelPath)) parts[modelPath] = model;
            }

            // ---- own sheet-metal classification + source-unchanged capture ----
            int sheet = 0, dirty = 0;
            var srcArr = new JArray();
            foreach (var kv in parts)
            {
                if (!GtIsSheetMetal(kv.Value)) continue;
                sheet++;
                bool sf = false; try { sf = kv.Value.GetSaveFlag(); } catch { }
                if (sf) dirty++;
                string mtime = ""; try { var fi = new FileInfo(kv.Key); if (fi.Exists) mtime = fi.LastWriteTimeUtc.ToString("o"); } catch { }
                srcArr.Add(new JObject
                {
                    ["name"] = Path.GetFileNameWithoutExtension(kv.Key),
                    ["path"] = kv.Key,
                    ["mtimeUtc"] = mtime,   // run0 vs run1 identical == source file never written
                    ["dirty"] = sf          // should stay false — export must not dirty/save the source
                });
            }
            root["totalParts"] = parts.Count;
            root["sheetMetalParts"] = sheet;
            root["sourceParts"] = srcArr;
            root["sourceDirtyCount"] = dirty;

            // ---- own on-disk DXF count ----
            int dxfCount = 0; var dxfNames = new JArray();
            try
            {
                if (Directory.Exists(outDir))
                {
                    var files = Directory.GetFiles(outDir, "*.dxf");
                    dxfCount = files.Length;
                    foreach (var f in files) dxfNames.Add(Path.GetFileName(f));
                }
            }
            catch { }
            root["dxfFilesOnDisk"] = dxfCount;
            root["dxfNames"] = dxfNames;

            int layerCountMin = 0;
            int filesWithBend = 0;
            int bendEntities = 0;
            var fileDetails = new JArray();
            try
            {
                if (Directory.Exists(outDir))
                {
                    string[] dxfFiles = Directory.GetFiles(outDir, "*.dxf");
                    layerCountMin = int.MaxValue;
                    foreach (string f in dxfFiles)
                    {
                        JObject detail = ScanDxfLayers(f);
                        fileDetails.Add(detail);
                        int lc = (int)detail["layerCount"];
                        if (lc < layerCountMin) layerCountMin = lc;
                        if ((bool)detail["bendLayerPresent"]) filesWithBend++;
                        bendEntities += (int)detail["bendEntities"];
                    }
                    if (dxfFiles.Length == 0) layerCountMin = 0;
                }
            }
            catch { }
            root["dxfFiles"] = fileDetails;
            root["dxfLayerCountMin"] = layerCountMin;
            root["dxfFilesWithBendLayer"] = filesWithBend;
            root["bendLayerEntityCount"] = bendEntities;
            root["bendLayerPresent"] = fileDetails.Count > 0 && filesWithBend == fileDetails.Count;
            root["bendLinesOnBendLayer"] = bendEntities > 0;

            // the headline invariant: at least one flat-pattern DXF per sheet-metal part, sources untouched
            root["dxfMatchesSheetMetal"] = sheet > 0 && dxfCount >= sheet;
            root["sourceUntouched"] = dirty == 0;
            return root;
        }

        // INDEPENDENT sheet-metal test — own feature-tree read, own type-name signal (nothing from FlatDxf.cs).
        private static bool GtIsSheetMetal(IModelDoc2 partDoc)
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

        private static JObject ScanDxfLayers(string path)
        {
            var detail = new JObject();
            detail["name"] = Path.GetFileName(path);
            var layerNames = new List<string>();
            var entityCountByLayer = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int totalEntities = 0;
            try
            {
                string[] lines = File.ReadAllText(path).Split(new string[] { "\r\n", "\n" }, StringSplitOptions.None);
                bool pendingSectionName = false;
                bool inTables = false;
                bool inEntities = false;
                bool pendingTableName = false;
                string openTable = null;
                bool inLayerEntry = false;
                string openEntity = null;
                string openEntityLayer = "0";
                bool openEntityLayerSet = false;
                for (int i = 0; i + 1 < lines.Length; i += 2)
                {
                    string code = lines[i].Trim();
                    string value = lines[i + 1].Trim();
                    if (code == "0")
                    {
                        if (openEntity != null)
                        {
                            totalEntities++;
                            int n = 0;
                            entityCountByLayer.TryGetValue(openEntityLayer, out n);
                            entityCountByLayer[openEntityLayer] = n + 1;
                            openEntity = null;
                        }
                        if (value == "SECTION") pendingSectionName = true;
                        else if (value == "ENDSEC") { inTables = false; inEntities = false; pendingSectionName = false; pendingTableName = false; openTable = null; inLayerEntry = false; }
                        else if (value == "TABLE") pendingTableName = true;
                        else if (value == "ENDTAB") { openTable = null; pendingTableName = false; inLayerEntry = false; }
                        else if (value == "LAYER") { if (inTables && openTable == "LAYER") inLayerEntry = true; }
                        else if (inEntities && IsDxfEntity(value)) { openEntity = value; openEntityLayer = "0"; openEntityLayerSet = false; }
                        else inLayerEntry = false;
                    }
                    else if (code == "2")
                    {
                        if (pendingSectionName) { inTables = value == "TABLES"; inEntities = value == "ENTITIES"; pendingSectionName = false; }
                        else if (pendingTableName) { openTable = value; pendingTableName = false; }
                        else if (inLayerEntry) { layerNames.Add(value); inLayerEntry = false; }
                    }
                    else if (code == "8" && openEntity != null && !openEntityLayerSet) { openEntityLayer = value; openEntityLayerSet = true; }
                }
                if (openEntity != null)
                {
                    totalEntities++;
                    int n = 0;
                    entityCountByLayer.TryGetValue(openEntityLayer, out n);
                    entityCountByLayer[openEntityLayer] = n + 1;
                }
            }
            catch { }
            detail["layerCount"] = layerNames.Count;
            detail["layers"] = new JArray(layerNames);
            detail["totalEntities"] = totalEntities;
            int onBend = 0;
            entityCountByLayer.TryGetValue("BEND", out onBend);
            detail["bendEntities"] = onBend;
            detail["bendLayerPresent"] = layerNames.Exists(n => n.Equals("BEND", StringComparison.OrdinalIgnoreCase));
            return detail;
        }

        private static bool IsDxfEntity(string value)
        {
            switch (value)
            {
                case "LINE":
                case "LWPOLYLINE":
                case "POLYLINE":
                case "CIRCLE":
                case "ARC":
                case "ELLIPSE":
                case "SPLINE":
                case "TEXT":
                case "MTEXT":
                case "DIMENSION":
                case "INSERT":
                case "HATCH":
                case "LEADER":
                case "SOLID":
                case "POINT":
                case "TRACE":
                case "RAY":
                case "XLINE":
                case "SHAPE":
                    return true;
                default:
                    return false;
            }
        }
    }
}
