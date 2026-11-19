using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT check for close_document. Re-derives the SAME target path from intent text (via
        // OpenDocument.ExtractPath, shared with open_document's own GT rather than duplicated), then queries the
        // live SolidWorks session directly — never trusting the handler's own Closed report.
        public static JObject MeasureCloseDocument(ISldWorks app, string intent)
        {
            var res = new JObject();
            string path = OpenDocument.ExtractPath(intent);
            res["requestedPath"] = path;
            if (string.IsNullOrEmpty(path)) { res["stillOpen"] = false; return res; }

            IModelDoc2 doc = null;
            try { doc = app.GetOpenDocumentByName(path) as IModelDoc2; } catch { }
            res["stillOpen"] = doc != null;
            return res;
        }
    }
}
