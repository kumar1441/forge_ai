using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for the move_component (translate ONE floating component by a vector) WRITE handler.
    /// Shares NO code with MoveComponent.cs.
    ///
    /// A single-component move is verified by a DELTA between a baseline read (run0, before the move) and the post-write
    /// read (run1): the ONE named target component's centroid must shift by ~the requested vector, while EVERY OTHER
    /// component's centroid is UNCHANGED (nothing else moves). Nothing may newly over-define and the rebuild must stay
    /// clean. A relative move is intentionally NOT idempotent — a rerun moves the target again — so run2 stability is not
    /// asserted for this test.
    ///
    /// The measurement is a per-component centroid keyed by NAME, so the harness can look up the handler's reported
    /// Component name in run1.components vs run0.components and assert its centroid shifted, and every other named
    /// component's centroid held. Centroids come from Component2.GetBox (assembly-space bbox center), a read owned wholly
    /// here.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureMoveComponent(ISldWorks app, IModelDoc2 model)
        {
            var d = new JObject();
            var asm = model as AssemblyDoc;
            if (asm == null) { d["applicable"] = false; d["error"] = "active doc is not an assembly"; return d; }
            d["applicable"] = true;

            try { model.ForceRebuild3(false); } catch { }   // measure the SOLVED state

            object[] top = asm.GetComponents(true) as object[];
            int topCount = 0, overDef = 0;
            var comps = new JArray();
            foreach (var o in top ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                topCount++;

                int st = (int)swConstrainedStatus_e.swUnderConstrained; try { st = c.GetConstrainedStatus(); } catch { }
                if (st == (int)swConstrainedStatus_e.swOverConstrained || st == (int)swConstrainedStatus_e.swNoSolution || st == (int)swConstrainedStatus_e.swInvalidSolution) overDef++;

                string name = null; try { name = c.Name2; } catch { }
                double[] b = null; try { b = c.GetBox(false, false) as double[]; } catch { }
                var entry = new JObject { ["name"] = name };
                if (b != null && b.Length >= 6)
                {
                    entry["cx"] = (b[0] + b[3]) / 2.0 * 1000.0;   // mm
                    entry["cy"] = (b[1] + b[4]) / 2.0 * 1000.0;
                    entry["cz"] = (b[2] + b[5]) / 2.0 * 1000.0;
                }
                comps.Add(entry);
            }

            int rebuildErrors = 0; try { rebuildErrors = model.Extension.GetWhatsWrongCount(); } catch { }

            d["topLevelComponents"] = topCount;
            d["components"] = comps;
            d["overDefinedComponents"] = overDef;
            d["rebuildErrors"] = rebuildErrors;

            // change fingerprint the grader diffs run0 -> run1 (one named component's centroid shifts; others hold)
            d["fingerprint"] = new JObject { ["topLevelComponents"] = topCount };
            return d;
        }
    }
}
