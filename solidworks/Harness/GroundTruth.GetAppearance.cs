using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT appearance read — shares NO code with GetAppearance. Own MaterialPropertyValues read → RGB 0-255.
        public static JObject MeasureGetAppearance(IModelDoc2 model)
        {
            var res = new JObject();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART) { res["hasColor"] = false; return res; }
            double[] mv = null; try { mv = model.MaterialPropertyValues as double[]; } catch { }
            if (mv != null && mv.Length >= 3 && mv[0] >= 0)
            {
                res["hasColor"] = true;
                res["r"] = (int)Math.Round(mv[0] * 255);
                res["g"] = (int)Math.Round(mv[1] * 255);
                res["b"] = (int)Math.Round(mv[2] * 255);
            }
            else res["hasColor"] = false;
            return res;
        }
    }
}
