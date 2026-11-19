using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT check for copy_properties_between_files: re-reads the CURRENTLY ACTIVE document's own
        // custom-property list directly (CustomPropertyManager.GetNames()+Get4), not the handler's own Rows
        // report — a completely different call sequence than the handler's ReadResolved helper (same primitives,
        // read fresh here per this family's convention).
        public static JObject MeasureCopyPropertiesBetweenFiles(ISldWorks app)
        {
            var res = new JObject();
            IModelDoc2 active = null;
            try { active = app.IActiveDoc2 as IModelDoc2; } catch { }
            if (active == null) { res["hasDoc"] = false; return res; }
            res["hasDoc"] = true;

            var props = new JObject();
            try
            {
                var cpm = active.Extension.get_CustomPropertyManager("");
                if (cpm != null)
                {
                    var names = cpm.GetNames() as string[];
                    if (names != null)
                    {
                        foreach (var n in names)
                        {
                            string val = null, resolved = null;
                            try { cpm.Get4(n, false, out val, out resolved); } catch { }
                            props[n] = string.IsNullOrEmpty(resolved) ? val : resolved;
                        }
                    }
                }
            }
            catch { }
            res["properties"] = props;
            res["propertyCount"] = props.Count;
            return res;
        }
    }
}
