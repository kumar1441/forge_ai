using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT check for open_document. Deliberately does NOT trust the handler's own Opened/Title report —
        // re-derives the target path from the SAME intent text via OpenDocument.ExtractPath (not from the handler's
        // result object), then queries the SolidWorks session directly for that document (GetOpenDocumentByName) and
        // reads its load-mode state (IsOpenedViewOnly / IsOpenedReadOnly / GetLightWeightComponentCount) straight
        // from the API, independent of whatever OpenDoc6's own return value claimed.
        public static JObject MeasureOpenDocument(ISldWorks app, string intent)
        {
            var res = new JObject();
            string path = OpenDocument.ExtractPath(intent);
            res["requestedPath"] = path;
            if (string.IsNullOrEmpty(path)) { res["found"] = false; return res; }

            IModelDoc2 doc = null;
            try { doc = app.GetOpenDocumentByName(path) as IModelDoc2; } catch { }
            res["found"] = doc != null;
            if (doc == null) return res;

            try { res["title"] = doc.GetTitle(); } catch { }
            try { res["docType"] = (int)doc.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY ? "assembly" : "part"; } catch { }
            try { res["isViewOnly"] = doc.IsOpenedViewOnly(); } catch { }
            try { res["isReadOnly"] = doc.IsOpenedReadOnly(); } catch { }
            try
            {
                var asm = doc as AssemblyDoc;
                if (asm != null) res["lightweightCount"] = asm.GetLightWeightComponentCount();
            }
            catch { }
            return res;
        }
    }
}
