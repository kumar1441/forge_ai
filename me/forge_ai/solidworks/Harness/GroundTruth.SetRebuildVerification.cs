using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT read of the verification-on-rebuild switch, from BOTH scopes separately. The handler collapses the
        // two into one answer; this keeps them apart so the harness can see WHERE the value actually lives on this build
        // and catch the case where a "successful" write landed in a scope nobody reads.
        public static JObject MeasureSetRebuildVerification(ISldWorks app, IModelDoc2 model)
        {
            var res = new JObject();
            const int pref = (int)swUserPreferenceToggle_e.swPerformanceVerifyOnRebuild;
            bool docVal = false, appVal = false;
            try { docVal = model.Extension.GetUserPreferenceToggle(pref, (int)swUserPreferenceOption_e.swDetailingNoOptionSpecified); } catch { }
            try { appVal = app.GetUserPreferenceToggle(pref); } catch { }
            res["docToggle"] = docVal;
            res["appToggle"] = appVal;
            res["either"] = docVal || appVal;
            return res;
        }
    }
}
