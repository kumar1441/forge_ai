using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT file-scope custom-property map — shares NO code with SetCustomProperty. Its own GetNames + Get4
        // read of the doc's "" scope, so the harness can assert a SPECIFIC property's value (name→resolved) after the
        // write, and prove idempotency (run2 value == run1 value).
        public static JObject MeasureSetCustomProperty(IModelDoc2 model)
        {
            var res = new JObject();
            var props = new JObject();
            if (model == null) { res["count"] = -1; res["props"] = props; return res; }
            CustomPropertyManager cpm = null;
            try { cpm = model.Extension.get_CustomPropertyManager(""); } catch { }
            if (cpm != null)
            {
                string[] names = null; try { names = cpm.GetNames() as string[]; } catch { }
                if (names != null) foreach (var n in names)
                {
                    if (string.IsNullOrEmpty(n)) continue;
                    string val = null, resolved = null; try { cpm.Get4(n, false, out val, out resolved); } catch { }
                    props[n] = string.IsNullOrEmpty(resolved) ? val : resolved;
                }
            }
            res["count"] = props.Count;
            res["props"] = props;
            return res;
        }
    }
}
