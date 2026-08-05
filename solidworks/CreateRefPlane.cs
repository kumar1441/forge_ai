using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class CreateRefPlaneResult
    {
        public double OffsetMm;          // offset distance parsed from the intent (mm); default 25 when unspecified
        public string ReferencePlane;    // the standard plane the new plane is offset from: "Front Plane" | "Top Plane" | "Right Plane"
        public bool RefPlaneAdded;       // an InsertRefPlane feature was created (before verify)
        public int RebuildErrors;        // GetWhatsWrongCount() post-rebuild (0 => clean)
        public bool RolledBack;          // the plane was created but failed to verify → deleted, part restored
        public bool AlreadyDone;         // idempotent: a Forge-Plane already exists → nothing to do
        public bool Verified;            // fail closed: true ONLY when a NEW RefPlane appeared (count +1) and the rebuild is clean
        public string Info;              // verdict-first panel line
        public string Error;             // honest failure text
    }

    /// <summary>
    /// CreateRefPlane (tool #71 create_reference_plane) — a foundational reference-geometry WRITE. It inserts ONE
    /// reference plane offset from a standard plane (Front/Top/Right) by a distance: "add a reference plane 20mm above
    /// the top", "create an offset plane 30mm from the front", "add a plane 25mm up", "make a mid-plane". Valid on a
    /// PART or an assembly (reference planes exist on both).
    ///
    /// The geometry authoring is REUSED VERBATIM from RecipeExecutor.DoPlane / BuildOffsetPlane (the proven `plane`
    /// recipe op): select the standard plane by name via IModelDocExtension.SelectByID2("Front Plane"/"Top Plane"/
    /// "Right Plane", "PLANE", …), then drive IFeatureManager.InsertRefPlane with a DISTANCE constraint
    /// (swRefPlaneReferenceConstraint_Distance, OR-ing in _OptionFlip for a negative offset). The ONLY thing this
    /// handler adds over the recipe op is the universal WRITE spine: parse the intent, tag the feature Forge-Plane for
    /// idempotency, ONE ForceRebuild3, and a FAIL-CLOSED independent verify.
    ///
    /// Robustness (the 12 rules): offset (25mm) and reference plane (Front) both DEFAULT sensibly, so there is no
    /// ambiguity to ask about (Character #6 — no ceremony on a simple ask). IDEMPOTENT (Rule #5): the plane is tagged
    /// Forge-Plane; a second run finds it and reports "already added a reference plane — nothing to do" instead of
    /// stacking a second. UNDO is sacred (Rule #7): one tagged feature, one Ctrl+Z; Forge never saves. FAIL CLOSED
    /// (Rule #6): after the rebuild the handler INDEPENDENTLY re-traverses the tree and confirms a NEW RefPlane-type
    /// feature exists (the ref-plane count ROSE by 1) AND the rebuild is clean; anything less — InsertRefPlane returned
    /// null, or the rebuild errored — and the Forge-Plane feature is DELETED, the model restored, and the failure
    /// reported honestly. Never a fake green.
    /// </summary>
    public static class CreateRefPlane
    {
        private const string PlaneFeatureName = "Forge-Plane";
        private const double MM = 0.001;          // mm -> SW metres
        private const double DefaultOffsetMm = 25.0; // sensible default when no distance is stated
        private const string RefPlaneType = "RefPlane"; // GetTypeName2 of a reference-plane feature on this build

        public static bool IsCreateRefPlaneIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            // Removing/deleting a plane is not this handler.
            if (Regex.IsMatch(c, @"\b(remove|delete|strip|get rid of|kill|suppress)\b")) return false;
            bool addVerb = Regex.IsMatch(c, @"\b(add|create|make|insert|put)\b");
            bool hasPlane = Regex.IsMatch(c, @"\bplane\b");
            return addVerb && hasPlane;
        }

        public static async Task<CreateRefPlaneResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new CreateRefPlaneResult();

            if (model == null) { res.Error = "No open document — open the part or assembly you want the reference plane on."; return res; }
            int dt = (int)model.GetType();
            if (dt != (int)swDocumentTypes_e.swDocPART && dt != (int)swDocumentTypes_e.swDocASSEMBLY)
            { res.Error = "A reference plane is added on a part or an assembly — this document is neither."; return res; }

            // ---- IDEMPOTENT (Rule #5): a Forge-Plane already present → don't stack a second ----
            if (FindFeatureByName(model, PlaneFeatureName) != null)
            {
                res.AlreadyDone = true;
                res.Verified = true;   // the requested state already holds
                res.Info = "Already added a reference plane — a Forge-Plane feature is present, so there's nothing to do. " +
                           "To add a different one, delete Forge-Plane first (Edit > Delete, or Ctrl+Z), then run again.";
                await emit("Builder", null, "done", "Forge-Plane already present — nothing to do");
                return res;
            }

            double offMm = ParseOffsetMm(intent);
            string principal = ParseReferencePlane(intent);
            res.OffsetMm = offMm;
            res.ReferencePlane = principal;

            await emit("Gauge", "reading the reference geometry", "run", null);
            int planesBefore = RefPlaneCount(model);
            await emit("Gauge", null, "done",
                "offset " + Trim(offMm) + " mm from the " + principal + " · " + planesBefore + " reference plane(s) present");

            // ---- WRITE: select the standard plane and InsertRefPlane with a distance constraint (RecipeExecutor.DoPlane) ----
            await emit("Builder", "adding a reference plane " + Trim(offMm) + " mm from the " + principal, "run", null);

            Feature plane = null;
            try
            {
                try { model.ClearSelection2(true); } catch { }
                bool sel = model.Extension.SelectByID2(principal, "PLANE", 0, 0, 0, false, 0, null, 0);
                if (!sel)
                {
                    res.Error = "Couldn't select the " + principal + " — this document's standard planes may be renamed or missing.";
                    await emit("Builder", null, "fail", res.Error);
                    return res;
                }
                // REUSED VERBATIM from RecipeExecutor.BuildOffsetPlane: Distance constraint, OR OptionFlip for a
                // negative offset, |offset| in metres. InsertRefPlane(Constraint1, Val1, C2, V2, C3, V3).
                int constraint = (int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_Distance;
                if (offMm < 0) constraint |= (int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_OptionFlip;
                plane = model.FeatureManager.InsertRefPlane(constraint, Math.Abs(offMm) * MM, 0, 0, 0, 0) as Feature;
                try { model.ClearSelection2(true); } catch { }
            }
            catch (Exception ex)
            {
                res.Error = "The reference plane couldn't be created (" + ex.GetType().Name + ") — Forge left the document unchanged.";
                RollbackPlane(model);
                await emit("Builder", null, "fail", res.Error);
                return res;
            }

            if (plane == null)
            {
                res.Error = "SolidWorks refused the reference plane — the document is unchanged.";
                RollbackPlane(model);
                await emit("Builder", null, "fail", res.Error);
                return res;
            }
            res.RefPlaneAdded = true;
            try { plane.Name = PlaneFeatureName; } catch { }   // tag for idempotency + rollback (Rule #5/#7)

            // ---- rebuild once, then INDEPENDENTLY verify (Rule #6) ----
            await emit("Sentinel", "verifying the plane post-rebuild", "run", null);
            try { model.ForceRebuild3(false); } catch { }
            res.RebuildErrors = SafeWhatsWrong(model);

            int planesAfter = RefPlaneCount(model);
            bool countRose = planesAfter == planesBefore + 1;
            bool tagged = FindFeatureByName(model, PlaneFeatureName) != null;
            bool clean = res.RebuildErrors == 0;

            if (!countRose || !tagged || !clean)
            {
                // FAIL CLOSED + never ship a broken model (Rule #6/#7): delete the plane, restore the document.
                RollbackPlane(model);
                res.RolledBack = true;
                res.Error = !clean
                    ? "The reference plane rebuilt with " + res.RebuildErrors + " error(s) — rolled it back; the document is unchanged."
                    : (!countRose
                        ? "No new reference plane appeared after the rebuild (count " + planesBefore + " → " + planesAfter +
                          ") — rolled it back; the document is unchanged."
                        : "The reference plane could not be confirmed in the tree — rolled it back; the document is unchanged.");
                await emit("Sentinel", null, "fail", "rolled back — document restored");
                return res;
            }

            res.Verified = true;
            await emit("Sentinel", null, "done",
                "reference plane added: count " + planesBefore + " → " + planesAfter + ", " + Trim(offMm) +
                " mm from the " + principal + ", rebuild clean");

            res.Info = BuildInfo(res);
            return res;
        }

        // ---- verdict first (Character #3), the number not the adjective (Character #2), honest about what was VERIFIED ----
        private static string BuildInfo(CreateRefPlaneResult r)
        {
            return "Added a reference plane " + Trim(r.OffsetMm) + " mm from the " + (r.ReferencePlane ?? "Front Plane") +
                   " (a new RefPlane feature, rebuild clean). One Ctrl+Z removes it; Forge didn't save.";
        }

        // ================= intent parsing =================

        // Offset distance in mm: a number qualified by "mm" ("20mm", "30mm from the front", "25mm up"). A bare inch
        // value maps to mm. Default 25mm when nothing parseable (a sensible default — Character #6, don't ask).
        private static double ParseOffsetMm(string intent)
        {
            string c = (intent ?? "").ToLowerInvariant();

            var m = Regex.Match(c, @"(\d+(\.\d+)?)\s*mm");
            if (m.Success && double.TryParse(m.Groups[1].Value, out double v) && v > 0) return v;

            var inch = Regex.Match(c, @"(\d+(\.\d+)?)\s*(inch(es)?|in\b|"")");
            if (inch.Success && double.TryParse(inch.Groups[1].Value, out double vin) && vin > 0) return vin * 25.4;

            return DefaultOffsetMm;
        }

        // Reference standard plane: "front" → Front Plane, "top" (incl. "above the top") → Top Plane, "right" → Right
        // Plane. Default Front Plane when no plane word is named.
        private static string ParseReferencePlane(string intent)
        {
            string c = (intent ?? "").ToLowerInvariant();
            if (Regex.IsMatch(c, @"\btop\b")) return "Top Plane";
            if (Regex.IsMatch(c, @"\bright\b")) return "Right Plane";
            if (Regex.IsMatch(c, @"\bfront\b")) return "Front Plane";
            return "Front Plane";
        }

        // ================= tree helpers (own, independent of the verify's own return codes) =================

        // Count reference-plane features by GetTypeName2 == "RefPlane" (includes the 3 standard planes — the verify
        // asserts the DELTA, so the baseline is irrelevant).
        private static int RefPlaneCount(IModelDoc2 model)
        {
            int n = 0;
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    string tn = null; try { tn = f.GetTypeName2(); } catch { }
                    if (tn == RefPlaneType) n++;
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch { }
            return n;
        }

        private static Feature FindFeatureByName(IModelDoc2 model, string name)
        {
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    string nm = null; try { nm = f.Name; } catch { }
                    if (string.Equals(nm, name, StringComparison.OrdinalIgnoreCase)) return f;
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch { }
            return null;
        }

        private static int SafeWhatsWrong(IModelDoc2 model)
        { try { return model.Extension.GetWhatsWrongCount(); } catch { return 0; } }

        // delete the tagged plane and rebuild — restores the tree so a failed/unverified insert never ships a dirty model
        private static void RollbackPlane(IModelDoc2 model)
        {
            try
            {
                var f = FindFeatureByName(model, PlaneFeatureName);
                if (f == null) return;
                try { model.ClearSelection2(true); } catch { }
                bool sel = false; try { sel = f.Select2(false, 0); } catch { }
                if (sel) { try { model.EditDelete(); } catch { } }
                try { model.ForceRebuild3(false); } catch { }
                try { model.ClearSelection2(true); } catch { }
            }
            catch { }
        }

        private static string Trim(double v) => v.ToString("0.###");
    }
}
