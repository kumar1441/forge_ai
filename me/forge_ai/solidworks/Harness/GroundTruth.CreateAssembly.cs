using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT check for create_assembly. A brand-new assembly has no path yet (Forge never saves), so like
        // create_drawing/create_part's GT, the best available independent signal is the session's own
        // active-document pointer, re-read fresh here rather than trusting the handler's own Title report.
        public static JObject MeasureCreateAssembly(ISldWorks app)
        {
            var res = new JObject();
            IModelDoc2 active = null;
            try { active = app.IActiveDoc2 as IModelDoc2; } catch { }
            if (active == null) { res["isAssembly"] = false; return res; }

            bool isAssembly = false;
            try { isAssembly = (int)active.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY; } catch { }
            res["isAssembly"] = isAssembly;
            if (!isAssembly) return res;

            try { res["title"] = active.GetTitle(); } catch { }
            try { res["hasPath"] = !string.IsNullOrEmpty(active.GetPathName()); } catch { }
            return res;
        }
    }
}
