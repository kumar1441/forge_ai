using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public static partial class GroundTruth
    {
        // INDEPENDENT constrained-status census for fully_define_sketch. This is the verdict maker: it walks the tree's
        // ProfileFeatures and asks each sketch for its RAW GetConstrainedStatus, then reports how many are NOT fully
        // defined. The harness compares this across run0 (baseline: some under-defined) and run1 (after the write: zero
        // under-defined) — so "did the sketches actually become fully defined" is decided by a second, handler-blind
        // status read, never by FullyDefineSketch's own return code (whose headless meaning is unproven on this build).
        public static JObject MeasureFullyDefineSketch(IModelDoc2 model)
        {
            var res = new JObject();
            var rows = new JArray();
            int full = 0, notFull = 0;
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { res["sketches"] = rows; res["count"] = 0; res["fullyDefined"] = 0; res["notFullyDefined"] = 0; return res; }
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    string tn = null; try { tn = f.GetTypeName2(); } catch { }
                    if (string.Equals(tn, "ProfileFeature", StringComparison.OrdinalIgnoreCase))
                    {
                        string nm = null; try { nm = f.Name; } catch { }
                        Sketch sk = null; try { sk = f.GetSpecificFeature2() as Sketch; } catch { }
                        if (sk != null)
                        {
                            int st = -1; try { st = sk.GetConstrainedStatus(); } catch { }
                            bool fd = st == (int)swConstrainedStatus_e.swFullyConstrained;
                            if (fd) full++; else notFull++;
                            rows.Add(new JObject { ["name"] = nm, ["status"] = st, ["fullyDefined"] = fd });
                        }
                    }
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch { }
            res["sketches"] = rows;
            res["count"] = rows.Count;
            res["fullyDefined"] = full;
            res["notFullyDefined"] = notFull;
            return res;
        }
    }
}
