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
    }
}
