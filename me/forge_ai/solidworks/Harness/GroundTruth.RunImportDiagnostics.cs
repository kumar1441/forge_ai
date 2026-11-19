using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT check for run_import_diagnostics — its OWN per-feature IFeature.GetErrorCode2 walk (a second,
        // separately-written loop, not a call into the handler's CountRebuildErrors) plus a solid-body recount, so a
        // handler that miscounts its own before/after can't agree with itself.
        public static JObject MeasureRunImportDiagnostics(IModelDoc2 model)
        {
            var res = new JObject();
            int err = 0, warn = 0;
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    int code = 0; bool isWarn = false;
                    try { code = f.GetErrorCode2(out isWarn); } catch { code = 0; }
                    if (code != (int)swFeatureError_e.swFeatureErrorNone) { if (isWarn) warn++; else err++; }
                    var s = f.GetFirstSubFeature() as Feature;
                    while (s != null)
                    {
                        int scode = 0; bool sWarn = false;
                        try { scode = s.GetErrorCode2(out sWarn); } catch { scode = 0; }
                        if (scode != (int)swFeatureError_e.swFeatureErrorNone) { if (sWarn) warn++; else err++; }
                        s = s.GetNextSubFeature() as Feature;
                    }
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch { }
            res["rebuildErrors"] = err;
            res["rebuildWarnings"] = warn;
            int bodyCount = 0;
            try
            {
                var pd = model as PartDoc;
                var bodies = pd?.GetBodies2((int)swBodyType_e.swSolidBody, true) as object[];
                bodyCount = bodies?.Length ?? 0;
            }
            catch { }
            res["solidBodyCount"] = bodyCount;
            return res;
        }
    }
}
