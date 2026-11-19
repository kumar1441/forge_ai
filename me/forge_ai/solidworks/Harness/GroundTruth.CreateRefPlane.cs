using System;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for the create_reference_plane handler. Shares NO code with CreateRefPlane.cs.
    ///
    /// create_reference_plane is a WRITE that adds ONE reference-plane feature (offset from a standard plane). So the
    /// harness compares a BASELINE read (run0, before any plane) against the post-write read (run1) and asserts the
    /// tree changed the way a real reference-plane insert must:
    ///
    ///   1. refPlaneCount ROSE by exactly 1 (a NEW RefPlane-type feature appeared)
    ///   2. hasForgePlane is TRUE on run1  (a feature literally named 'Forge-Plane' now exists)
    ///   3. rebuildErrors == 0             (the insert rebuilt clean)
    /// and the rerun is idempotent (run2 == run1 — no second plane stacked).
    ///
    /// This traversal is its OWN independent walk of the feature tree — it re-derives the ref-plane tally by
    /// GetTypeName2 == "RefPlane" from scratch, so agreement with the handler's DELTA is a genuine cross-check, not a
    /// mirror of the handler's own count. Valid on a PART or an assembly (reference planes exist on both).
    /// </summary>
    public static partial class GroundTruth
    {
        public static JObject MeasureCreateRefPlane(ISldWorks app, IModelDoc2 model)
        {
            var mo = new JObject();
            if (model == null) { mo["applicable"] = false; mo["reason"] = "no active document"; return mo; }
            mo["applicable"] = true;

            int total = 0, refPlaneCount = 0;
            bool hasForgePlane = false;
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    total++;
                    string tn = null; try { tn = f.GetTypeName2(); } catch { }
                    if (tn == "RefPlane") refPlaneCount++;         // reference-plane feature type (incl. the 3 standard planes)
                    string nm = null; try { nm = f.Name; } catch { }
                    if (string.Equals(nm, "Forge-Plane", StringComparison.OrdinalIgnoreCase)) hasForgePlane = true;
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch (Exception ex) { mo["error"] = ex.GetType().Name + ": " + ex.Message; }

            int rebuild = 0; try { rebuild = model.Extension.GetWhatsWrongCount(); } catch { }

            mo["totalFeatures"] = total;
            mo["refPlaneCount"] = refPlaneCount;     // the cross-check target (grader asserts run1 == run0 + 1)
            mo["hasForgePlane"] = hasForgePlane;     // does a feature named 'Forge-Plane' exist?
            mo["rebuildErrors"] = rebuild;

            // change fingerprint the grader diffs run0 -> run1 (ref-plane count +1); idempotent run2 == run1
            mo["fingerprint"] = new JObject { ["refPlaneCount"] = refPlaneCount, ["rebuildErrors"] = rebuild };
            return mo;
        }
    }
}
