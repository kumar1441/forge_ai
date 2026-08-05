using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT read-only proof for the compare_versions handler. Compare.cs reads dimensions through
    /// VariantGenerator.ReadDimensions; THIS re-derives a fingerprint of the OPEN version with its OWN feature-tree
    /// walk (shares no code with Compare.cs). The harness diffs run0 vs run1 vs run2 of MeasureCompare — identical
    /// blobs are the proof that comparing two versions never mutated the live model (Robustness Rule #7: read-only).
    ///
    /// NOTE: this is a partial of GroundTruth so it lives with the other independent Measure* checks. Enabling it
    /// requires GroundTruth.cs to be declared `partial` (a one-word change) — see Compare.integration.md.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureCompare(ISldWorks app, IModelDoc2 model)
        {
            var root = new JObject();
            if (model == null) { root["error"] = "no model"; return root; }
            int dt = (int)model.GetType();
            root["docType"] = dt == (int)swDocumentTypes_e.swDocASSEMBLY ? "assembly" : (dt == (int)swDocumentTypes_e.swDocPART ? "part" : "other");

            // component count + name set (own enumeration — a rename/add/remove during a "read-only" run would move these)
            int comps = 0; var names = new JArray();
            var asm = model as AssemblyDoc;
            if (asm != null)
            {
                foreach (var o in (asm.GetComponents(false) as object[]) ?? new object[0])
                {
                    var c = o as Component2; if (c == null) continue;
                    bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                    comps++;
                    string nm = null; try { nm = c.Name2; } catch { } names.Add(nm);
                }
            }
            root["componentCount"] = comps;
            root["componentNames"] = names;

            // dimension fingerprint of the OPEN doc: count + summed |value| (metres), own tree walk (NOT ReadDimensions)
            int dimCount; double dimSum;
            DimFingerprint(model, out dimCount, out dimSum);
            root["dimCount"] = dimCount;
            root["dimSumM"] = Math.Round(dimSum, 9);
            return root;
        }

        // independent second implementation of the dimension read — the harness cross-checks read-only against it.
        private static void DimFingerprint(IModelDoc2 model, out int count, out double sumMeters)
        {
            int n = 0; double s = 0; var seen = new HashSet<string>();
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    var dd = f.GetFirstDisplayDimension() as DisplayDimension;
                    while (dd != null)
                    {
                        var dim = dd.GetDimension2(0) as Dimension;
                        if (dim != null)
                        {
                            string fn = null; try { fn = dim.FullName; } catch { }
                            if (fn != null && seen.Add(fn)) { n++; try { s += Math.Abs(dim.SystemValue); } catch { } }
                        }
                        dd = f.GetNextDisplayDimension(dd) as DisplayDimension;
                    }
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch { }
            count = n; sumMeters = s;
        }
    }
}
