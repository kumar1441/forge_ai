using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for get_fixture_capacity — shares NO code with GetFixtureCapacity.cs. The handler
    /// groups bodies by (volume, area, sorted extents); this GT groups by sorted bounding-box EXTENTS ALONE (the
    /// same coarser-key trick MeasureGetCutList uses for get_cut_list), so the two must still agree on the
    /// dominant group's quantity or a grouping bug is exposed. For an assembly, independently re-selects the
    /// multibody component by the SAME max-body-count rule (a shared premise about which component IS the
    /// fixture, not a measurement) but re-derives the group counts through its own traversal + key. Read-only.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureGetFixtureCapacity(IModelDoc2 model)
        {
            var d = new JObject();
            if (model == null) { d["applicable"] = false; d["reason"] = "no active document"; return d; }
            int docType = 0; try { docType = (int)model.GetType(); } catch { }
            if (docType != (int)swDocumentTypes_e.swDocPART && docType != (int)swDocumentTypes_e.swDocASSEMBLY)
            { d["applicable"] = false; d["reason"] = "not a part or assembly"; return d; }
            d["applicable"] = true;

            List<Body2> bodies = new List<Body2>();
            string sourceName = null;

            if (docType == (int)swDocumentTypes_e.swDocPART)
            {
                object[] b = null; try { b = ((PartDoc)model).GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]; } catch { }
                bodies = (b ?? new object[0]).OfType<Body2>().ToList();
            }
            else
            {
                var asmDoc = model as AssemblyDoc;
                object[] comps = null; try { comps = asmDoc.GetComponents(true) as object[]; } catch { }
                Component2 best = null; List<Body2> bestBodies = null;
                foreach (var o in comps ?? new object[0])
                {
                    var c = o as Component2; if (c == null) continue;
                    bool sup = false; try { sup = c.IsSuppressed(); } catch { }
                    if (sup) continue;
                    object bi;
                    object[] cb = null; try { cb = c.GetBodies3((int)swBodyType_e.swSolidBody, out bi) as object[]; } catch { }
                    var cbList = (cb ?? new object[0]).OfType<Body2>().ToList();
                    if (cbList.Count > 1 && cbList.Count > (bestBodies?.Count ?? 1))
                    { best = c; bestBodies = cbList; }
                }
                if (best != null) { bodies = bestBodies; try { sourceName = best.Name2; } catch { } }
            }

            d["sourceComponent"] = sourceName;
            d["totalBodies"] = bodies.Count;

            var groups = new Dictionary<string, int>();
            foreach (var b in bodies)
            {
                double dx = 0, dy = 0, dz = 0;
                try
                {
                    var box = b.GetBodyBox() as double[];
                    if (box != null && box.Length >= 6)
                    {
                        dx = Math.Abs(box[3] - box[0]) * 1000; dy = Math.Abs(box[4] - box[1]) * 1000; dz = Math.Abs(box[5] - box[2]) * 1000;
                    }
                }
                catch { }
                var ext = new[] { dx, dy, dz }.OrderBy(v => v).Select(v => Math.Round(v, 1)).ToArray();
                string key = ext[0] + "|" + ext[1] + "|" + ext[2];
                groups[key] = groups.TryGetValue(key, out var n) ? n + 1 : 1;
            }

            d["uniqueGroups"] = groups.Count;
            d["maxQuantity"] = groups.Count == 0 ? 0 : groups.Values.Max();
            return d;
        }
    }
}
