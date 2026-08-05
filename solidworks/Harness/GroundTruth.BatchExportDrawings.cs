using System;
using System.IO;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT check for batch_export_drawings. Does NOT trust the handler's own report of what it wrote —
        // re-lists "<folder>\forge-drawing-export" from disk (name + byte length per file) after the handler has
        // run, mirroring MeasureBatchConvertFiles's disk-listing shape for the neutral-format export.
        public static JObject MeasureBatchExportDrawings(IModelDoc2 model)
        {
            var res = new JObject();
            var files = new JArray();
            string target = null; try { target = model?.GetPathName(); } catch { }
            res["targetPath"] = target;
            if (string.IsNullOrWhiteSpace(target)) { res["outputFiles"] = files; return res; }
            try
            {
                string folder = Path.GetDirectoryName(target);
                res["folder"] = folder;
                string outDir = Path.Combine(folder, "forge-drawing-export");
                res["outputFolder"] = outDir;
                if (Directory.Exists(outDir))
                {
                    foreach (var p in Directory.GetFiles(outDir))
                    {
                        var fo = new JObject();
                        fo["name"] = Path.GetFileName(p);
                        long len = -1; try { len = new FileInfo(p).Length; } catch { }
                        fo["bytes"] = len;
                        files.Add(fo);
                    }
                }
            }
            catch { }
            res["outputFiles"] = files;
            res["outputFileCount"] = files.Count;
            return res;
        }
    }
}
