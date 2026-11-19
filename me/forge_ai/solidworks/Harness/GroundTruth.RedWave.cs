using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground-truth measurement for demo #8 "fix the red wave" — shares NOTHING with RedWave.cs.
    /// Re-reads the two invariants the fix must move, from the SolidWorks API with its own code, so the harness
    /// cannot inherit the handler's blind spots:
    ///   overDefinedComponents — top-level components whose GetConstrainedStatus is over/no-solution/invalid,
    ///   rebuildErrors         — GetWhatsWrongCount() post-rebuild,
    ///   totalMateFeatures     — own Mates-folder tree walk (so run0→run1 delta proves exactly how many mates were removed).
    /// The harness grades the DELTA between run0 (baseline) and run1 (after the fix): over-defined should drop to 0,
    /// rebuild clean, and at most one mate removed for the single-root-cause variant.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureRedWave(ISldWorks app, IModelDoc2 model)
        {
            var mo = new JObject();
            var asm = model as AssemblyDoc;
            if (asm == null) { mo["error"] = "active doc is not an assembly"; return mo; }
            try { model.ForceRebuild3(false); } catch { }   // measure the SOLVED state

            int rebuildErrors = 0; try { rebuildErrors = model.Extension.GetWhatsWrongCount(); } catch { }

            int over = 0;
            foreach (var o in (asm.GetComponents(true) as object[]) ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                int st = (int)swConstrainedStatus_e.swUnderConstrained; try { st = c.GetConstrainedStatus(); } catch { }
                if (st == (int)swConstrainedStatus_e.swOverConstrained || st == (int)swConstrainedStatus_e.swNoSolution || st == (int)swConstrainedStatus_e.swInvalidSolution) over++;
            }

            // own Mates-folder walk (independent of the handler's inventory)
            int mateFeatures = 0;
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    string tn = ""; try { tn = f.GetTypeName2(); } catch { }
                    if (tn == "MateGroup")
                    {
                        var s = f.GetFirstSubFeature() as Feature;
                        while (s != null) { mateFeatures++; s = s.GetNextSubFeature() as Feature; }
                    }
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch { }

            mo["overDefinedComponents"] = over;
            mo["rebuildErrors"] = rebuildErrors;
            mo["totalMateFeatures"] = mateFeatures;
            return mo;
        }
    }
}
