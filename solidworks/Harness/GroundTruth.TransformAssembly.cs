using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for the transform_assembly (move/rotate the WHOLE assembly as one rigid set) WRITE
    /// handler. Shares NO code with TransformAssembly.cs.
    ///
    /// A rigid whole-assembly move is verified by a DELTA between a baseline read (run0, before the move) and the
    /// post-write read (run1): the assembly's bounding-box CENTER must shift by ~the requested vector, while the
    /// bounding-box DIAGONAL is UNCHANGED (a rigid translation does not resize the model). Nothing may newly
    /// over-define and the rebuild must stay clean. run2 == run1 only for the idempotent "to the origin" form; a
    /// relative "move up 100" moves again on rerun (correct, not asserted for stability).
    ///
    /// The measurement path is DELIBERATELY DIFFERENT from the handler's: the handler averages per-component
    /// bbox-CENTROIDS (mean of component centers); this ground truth reads the WHOLE-ASSEMBLY bounding box via
    /// IModelDocExtension.GetBox (falling back to a union of component boxes if that API is unavailable on this
    /// build) — so agreement on the shifted center is a genuine cross-check, not a mirror of the handler's math.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureTransformAssembly(ISldWorks app, IModelDoc2 model)
        {
            var d = new JObject();
            var asm = model as AssemblyDoc;
            if (asm == null) { d["applicable"] = false; d["error"] = "active doc is not an assembly"; return d; }
            d["applicable"] = true;

            try { model.ForceRebuild3(false); } catch { }   // measure the SOLVED state

            object[] top = asm.GetComponents(true) as object[];
            int topCount = 0, overDef = 0;
            foreach (var o in top ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                topCount++;
                int st = (int)swConstrainedStatus_e.swUnderConstrained; try { st = c.GetConstrainedStatus(); } catch { }
                if (st == (int)swConstrainedStatus_e.swOverConstrained || st == (int)swConstrainedStatus_e.swNoSolution || st == (int)swConstrainedStatus_e.swInvalidSolution) overDef++;
            }

            // ---- whole-assembly bounding box as the UNION of every component's assembly-space box (Component2.GetBox) —
            //      a DIFFERENT path than the handler's per-component centroid AVERAGE. (IModelDocExtension.GetBox is
            //      absent on this 3DEXPERIENCE build, so the union IS the measurement, not a fallback.) ----
            double[] box = TxUnionComponentBoxes(asm);

            var centerMm = new JObject();
            double diagMm = 0;
            if (box != null && box.Length >= 6)
            {
                centerMm["x"] = (box[0] + box[3]) / 2.0 * 1000.0;
                centerMm["y"] = (box[1] + box[4]) / 2.0 * 1000.0;
                centerMm["z"] = (box[2] + box[5]) / 2.0 * 1000.0;
                double dx = box[3] - box[0], dy = box[4] - box[1], dz = box[5] - box[2];
                diagMm = Math.Sqrt(dx * dx + dy * dy + dz * dz) * 1000.0;
            }

            int rebuildErrors = 0; try { rebuildErrors = model.Extension.GetWhatsWrongCount(); } catch { }

            d["topLevelComponents"] = topCount;
            d["bboxCenterMm"] = centerMm;
            d["bboxDiagMm"] = diagMm;
            d["overDefinedComponents"] = overDef;
            d["rebuildErrors"] = rebuildErrors;

            // change fingerprint the grader diffs run0 -> run1 (center shifts by the vector; diagonal holds)
            d["fingerprint"] = new JObject
            {
                ["topLevelComponents"] = topCount,
                ["overDefinedComponents"] = overDef
            };
            return d;
        }

        // union of every top-level component's assembly-space bbox — the fallback whole-model box (own read).
        private static double[] TxUnionComponentBoxes(AssemblyDoc asm)
        {
            double[] acc = null;
            foreach (var o in (asm.GetComponents(true) as object[]) ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                double[] b = null; try { b = c.GetBox(false, false) as double[]; } catch { }
                if (b == null || b.Length < 6) continue;
                if (acc == null) acc = new[] { b[0], b[1], b[2], b[3], b[4], b[5] };
                else acc = new[]
                {
                    Math.Min(acc[0], b[0]), Math.Min(acc[1], b[1]), Math.Min(acc[2], b[2]),
                    Math.Max(acc[3], b[3]), Math.Max(acc[4], b[4]), Math.Max(acc[5], b[5])
                };
            }
            return acc;
        }
    }
}
