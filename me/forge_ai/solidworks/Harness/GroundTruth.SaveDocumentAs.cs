using System.IO;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT check for save_document_as. Deliberately does NOT trust the handler's own OutputPath —
        // re-derives the expected output path from the SAME intent text + the active model's OWN path
        // (SaveDocumentAs.ResolveOutputPath, not the handler's result object), then lists the file straight
        // from disk (name + byte length), independent of whatever SaveAs's own return value claimed.
        public static JObject MeasureSaveDocumentAs(IModelDoc2 model, string intent)
        {
            var res = new JObject();
            string srcPath = null; try { srcPath = model?.GetPathName(); } catch { }
            res["sourcePath"] = srcPath;
            if (string.IsNullOrEmpty(srcPath)) { res["found"] = false; return res; }

            string expected = SaveDocumentAs.ResolveOutputPath(intent, srcPath);
            res["expectedPath"] = expected;
            if (string.IsNullOrEmpty(expected)) { res["found"] = false; return res; }

            bool exists = File.Exists(expected);
            res["found"] = exists;
            if (exists)
            {
                long len = -1; try { len = new FileInfo(expected).Length; } catch { }
                res["bytes"] = len;
            }
            return res;
        }
    }
}
