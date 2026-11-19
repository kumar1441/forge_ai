using System.IO;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for handle_locked_files (tool 248). Re-derives the open document's own OS
    /// read-only attribute via a SEPARATE File.GetAttributes call, sharing no code with the handler. Does NOT
    /// attempt an exclusive-open probe — confirmed live (2026-07-31) that SolidWorks holds its own active
    /// document open with an exclusive OS handle for the whole session, so that probe is a guaranteed false
    /// positive against `model`'s own path (see HandleLockedFiles.cs's class doc comment for the full finding).
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureHandleLockedFiles(IModelDoc2 model)
        {
            var mo = new JObject();
            string path = null; try { path = model.GetPathName(); } catch { }
            mo["path"] = path;
            if (string.IsNullOrWhiteSpace(path)) { mo["expectedStatus"] = null; return mo; }
            if (!File.Exists(path)) { mo["expectedStatus"] = "missing"; return mo; }

            bool readOnly = false;
            try { readOnly = (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0; } catch { }
            mo["expectedReadOnly"] = readOnly;
            mo["expectedStatus"] = readOnly ? "read-only" : "ok";
            return mo;
        }
    }
}
