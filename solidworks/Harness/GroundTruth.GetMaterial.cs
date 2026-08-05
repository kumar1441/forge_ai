using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT material read — shares NO code with GetMaterial. Own GetMaterialPropertyName2 read. props-block
        // is generated with material "6061 Alloy", a KNOWN truth.
        public static JObject MeasureGetMaterial(IModelDoc2 model)
        {
            var res = new JObject();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART) { res["material"] = null; return res; }
            string db = null, mat = null;
            try { mat = ((PartDoc)model).GetMaterialPropertyName2("", out db); } catch { }
            res["material"] = mat;
            res["hasMaterial"] = !string.IsNullOrWhiteSpace(mat);
            return res;
        }
    }
}
