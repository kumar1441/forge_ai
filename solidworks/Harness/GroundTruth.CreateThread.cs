using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground-truth for the create_thread handler. Shares NO code with CreateThread.cs.
    ///
    /// Adding a cosmetic thread is a WRITE that inserts a COSMETIC-THREAD feature per hole, so — like the other write
    /// handlers — the harness compares a BASELINE read (run0, before any thread) against the post-write read (run1)
    /// and asserts the tree actually gained the threads a real tap must:
    ///
    ///   1. cosmeticThreadCount ROSE by exactly the handler's ThreadsAdded (features whose GetTypeName2 == "CosmeticThread")
    ///   2. forgeThreadCount > 0 on run1 (features named 'Forge-Thread-...' now exist)
    ///   3. rebuildErrors == 0 (the write left the model clean)
    ///   4. run2 == run1 (idempotent — a rerun stacks no second thread)
    ///
    /// Every number here is re-derived from scratch by a DIFFERENT path than the handler's report: this counts
    /// cosmetic-thread features by GetTypeName2 across the WHOLE feature tree (the handler tracks the Feature objects it
    /// created), so agreement on the delta is a genuine cross-check, not a mirror of the handler's own bookkeeping.
    /// hasSolid lets the grader demand an honest handler refusal on a body-less part.
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureCreateThread(ISldWorks app, IModelDoc2 model)
        {
            var mo = new JObject();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { mo["applicable"] = false; mo["reason"] = "active doc is not a part"; return mo; }
            mo["applicable"] = true;

            var part = model as PartDoc;
            int bodyCount = 0, cylFaces = 0;
            if (part != null)
            {
                object[] bodies = null;
                try { bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]; } catch { }
                foreach (var bo in bodies ?? new object[0])
                {
                    var body = bo as Body2; if (body == null) continue;
                    bodyCount++;
                    object[] faces = null; try { faces = body.GetFaces() as object[]; } catch { }
                    foreach (var fo in faces ?? new object[0])
                    {
                        var face = fo as Face2; if (face == null) continue;
                        Surface s = null; try { s = face.GetSurface() as Surface; } catch { }
                        if (s == null) continue;
                        bool isCyl = false; try { isCyl = s.IsCylinder(); } catch { }
                        if (isCyl) cylFaces++;
                    }
                }
            }

            // independent feature-tree walk: cosmetic-thread features (by type) + Forge-Thread features (by name)
            int cosmeticThreads = 0, forgeThreads = 0;
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                string tn = null; try { tn = f.GetTypeName2(); } catch { }
                if (string.Equals(tn, "CosmeticThread", StringComparison.OrdinalIgnoreCase)) cosmeticThreads++;
                string nm = null; try { nm = f.Name; } catch { }
                if (nm != null && nm.StartsWith("Forge-Thread", StringComparison.OrdinalIgnoreCase)) forgeThreads++;
                f = f.GetNextFeature() as Feature;
            }

            int rebuild = 0; try { rebuild = model.Extension.GetWhatsWrongCount(); } catch { }

            mo["bodyCount"] = bodyCount;
            mo["cylindricalFaceCount"] = cylFaces;
            mo["cosmeticThreadCount"] = cosmeticThreads;   // features whose GetTypeName2 == "CosmeticThread"
            mo["forgeThreadCount"] = forgeThreads;         // features named 'Forge-Thread-...'
            mo["rebuildErrors"] = rebuild;
            mo["hasSolid"] = bodyCount > 0;                // no solid => the handler MUST refuse honestly, not fake threads

            // change fingerprint the grader diffs run0 -> run1 (cosmetic-thread count UP, rebuild clean) and run1 == run2
            var fp = new JObject();
            fp["cosmeticThreadCount"] = cosmeticThreads;
            fp["rebuildErrors"] = rebuild;
            mo["fingerprint"] = fp;
            return mo;
        }
    }
}
