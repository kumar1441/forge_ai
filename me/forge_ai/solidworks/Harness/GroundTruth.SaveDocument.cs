using System;
using System.IO;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT check for save_document — reads the file's mtime straight from disk (not from the handler's
        // own MtimeChanged report) and requires it to be recent, i.e. a real write actually landed.
        public static JObject MeasureSaveDocument(IModelDoc2 model)
        {
            var res = new JObject();
            string path = null; try { path = model?.GetPathName(); } catch { }
            res["path"] = path;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) { res["found"] = false; return res; }
            res["found"] = true;
            DateTime mtime = DateTime.MinValue;
            try { mtime = File.GetLastWriteTimeUtc(path); } catch { }
            res["mtimeUtc"] = mtime.ToString("o");
            res["recentlyWritten"] = mtime != DateTime.MinValue && (DateTime.UtcNow - mtime) < TimeSpan.FromMinutes(2);
            return res;
        }
    }
}
