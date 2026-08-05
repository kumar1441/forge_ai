using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT dimension read — shares NO code with GetDimensions. Its own tree walk + its own value read
        // (Dimension.Value / IGetSystemValue), returning count + a name→mm map so the harness can assert both the
        // total AND a KNOWN dimension (e.g. the seeded block's 20mm thickness) reads the value it was generated with.
        public static JObject MeasureGetDimensions(IModelDoc2 model)
        {
            var res = new JObject();
            if (model == null) { res["error"] = "no doc"; return res; }
            int count = 0;
            var values = new JObject();
            var feat = model.FirstFeature() as Feature;
            while (feat != null)
            {
                var dd = feat.GetFirstDisplayDimension() as DisplayDimension;
                while (dd != null)
                {
                    Dimension d = null; try { d = dd.GetDimension2(0) as Dimension; } catch { }
                    if (d != null)
                    {
                        string dn = null; try { dn = d.FullName; } catch { }
                        if (string.IsNullOrEmpty(dn)) { try { dn = d.Name; } catch { } }
                        double v = double.NaN;
                        try { v = d.Value; } catch { }            // Dimension.Value is in the document's units (mm here)
                        if (double.IsNaN(v)) { try { v = d.SystemValue * 1000.0; } catch { } }
                        if (!string.IsNullOrEmpty(dn) && values[dn] == null) values[dn] = v;
                        count++;
                    }
                    dd = feat.GetNextDisplayDimension(dd) as DisplayDimension;
                }
                feat = feat.GetNextFeature() as Feature;
            }
            res["count"] = count;
            res["values"] = values;
            // convenience: the largest dimension value present (the seeded block's 80mm length) for a known-truth check
            double maxV = 0; foreach (var p in values.Properties()) { double x; if (double.TryParse(p.Value.ToString(), out x) && x > maxV) maxV = x; }
            res["maxMm"] = maxV;
            return res;
        }
    }
}
