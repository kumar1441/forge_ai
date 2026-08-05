using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for the rotate_component (rotate ONE floating component about an axis by an angle) WRITE
    /// handler. Shares NO code with RotateComponent.cs.
    ///
    /// A single-component rotate is verified by a DELTA between a baseline read (run0, before the rotate) and the
    /// post-write read (run1): the ONE named target component's ORIENTATION must change by ~the requested angle while its
    /// centroid barely moves (the rotation is about its OWN centre, a fixed point), and EVERY OTHER component's centroid
    /// AND orientation is UNCHANGED (nothing else turns or shifts). Nothing may newly over-define and the rebuild must stay
    /// clean. A rotate is intentionally NOT idempotent — a rerun rotates the target again — so run2 stability is not
    /// asserted for this test.
    ///
    /// Per component this records the bbox-center centroid (Component2.GetBox, assembly-space, mm) AND the 9 rotation-matrix
    /// elements r0..r8 of Component2.Transform2 (IMathTransform.ArrayData: [0..8] = 3x3 rotation, [9..11] = translation,
    /// [12] = scale). The harness looks up the handler's reported Component name in run1.components vs run0.components and
    /// computes the RELATIVE rotation angle between the two rotation matrices via the Frobenius inner product:
    ///   angle = acos((trace(R0^T · R1) - 1)/2) = acos((Σ_k r0[k]·r1[k] - 1)/2)   (layout-independent),
    /// asserting it ≈ the requested angle; that the target centroid barely moved; and that every OTHER component's rotation
    /// matrix and centroid are unchanged. All reads here are owned wholly by this measure.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureRotateComponent(ISldWorks app, IModelDoc2 model)
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
                var entry = new JObject { ["name"] = name };

                // centroid: bbox center, assembly-space, mm
                double[] b = null; try { b = c.GetBox(false, false) as double[]; } catch { }
                if (b != null && b.Length >= 6)
                {
                    entry["cx"] = (b[0] + b[3]) / 2.0 * 1000.0;
                    entry["cy"] = (b[1] + b[4]) / 2.0 * 1000.0;
                    entry["cz"] = (b[2] + b[5]) / 2.0 * 1000.0;
                }

                // orientation: the 9 rotation-matrix elements of Transform2 (ArrayData [0..8])
                double[] a = null; try { a = c.Transform2.ArrayData as double[]; } catch { }
                if (a != null && a.Length >= 9)
                {
                    for (int k = 0; k < 9; k++) entry["r" + k] = a[k];
                }
                comps.Add(entry);
            }

            int rebuildErrors = 0; try { rebuildErrors = model.Extension.GetWhatsWrongCount(); } catch { }

            d["topLevelComponents"] = topCount;
            d["components"] = comps;
            d["overDefinedComponents"] = overDef;
            d["rebuildErrors"] = rebuildErrors;

            // change fingerprint the grader diffs run0 -> run1 (one named component's orientation turns; centre + others hold)
            d["fingerprint"] = new JObject { ["topLevelComponents"] = topCount };
            return d;
        }
    }
}
