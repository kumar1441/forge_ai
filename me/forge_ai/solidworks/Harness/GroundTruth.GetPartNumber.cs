using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT part-number read — shares NO code with GetPartNumber. Own property read across the same key set.
        // props-block has PartNo = FORGE-001, a KNOWN truth.
        public static JObject MeasureGetPartNumber(IModelDoc2 model)
        {
            var res = new JObject();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART) { res["found"] = false; return res; }
            string[] keys = { "PartNo", "PartNumber", "Part Number", "Part No", "Number", "PN", "DrawingNo", "Drawing Number" };
            string found = null;
            string activeCfg = ""; try { activeCfg = ((Configuration)model.GetActiveConfiguration()).Name; } catch { }
            foreach (var scope in new[] { "", activeCfg })
            {
                CustomPropertyManager cpm = null; try { cpm = model.Extension.get_CustomPropertyManager(scope); } catch { }
                if (cpm == null) continue;
                foreach (var k in keys)
                {
                    string val = null, resolved = null; try { cpm.Get4(k, false, out val, out resolved); } catch { }
                    string v = resolved ?? val;
                    if (!string.IsNullOrWhiteSpace(v)) { found = v.Trim(); break; }
                }
                if (found != null) break;
            }
            res["partNumber"] = found;
            res["found"] = found != null;
            return res;
        }
    }
}
