using System;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class CreateRefAxisResult
    {
        public int AxesBefore = -1;
        public int AxesAfter = -1;
        public bool Verified;      // fail closed: a NEW RefAxis appeared (count +1) and the rebuild is clean
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 72 — create_reference_axis (WRITE). Inserts ONE reference axis at the intersection of two standard planes
    /// (Front ∩ Right = the vertical axis) — the axis counterpart to create_reference_plane. Select the two planes by
    /// name (IModelDocExtension.SelectByID2), then IFeatureManager.InsertAxis2(true). Fail-closed (Rule #6): after the
    /// rebuild it INDEPENDENTLY re-counts RefAxis features and confirms the count ROSE by exactly 1 with a clean
    /// rebuild — InsertAxis2 returning non-null is NOT trusted on its own. Idempotent-ish, undoable, never saves.
    /// </summary>
    public static class CreateRefAxis
    {
        private const string RefAxisType = "RefAxis";

        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            return System.Text.RegularExpressions.Regex.IsMatch(c, @"\b(reference axis|ref axis|reference-axis|construction axis|datum axis)\b") ||
                   (System.Text.RegularExpressions.Regex.IsMatch(c, @"\b(create|add|make|insert|new)\b") && System.Text.RegularExpressions.Regex.IsMatch(c, @"\baxis\b"));
        }

        public static async Task<CreateRefAxisResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new CreateRefAxisResult();
            if (model == null) { res.Error = "Open a part or assembly to add a reference axis."; return res; }

            await emit("Gauge", "counting existing axes", "run", null);
            res.AxesBefore = AxisCount(model);
            await emit("Gauge", null, "done", res.AxesBefore + " reference ax" + (res.AxesBefore == 1 ? "is" : "es") + " present");

            // ---- WRITE: select Front + Right planes, insert the axis at their intersection ----
            await emit("Scribe", "inserting the axis (Front ∩ Right)", "run", null);
            try { model.ClearSelection2(true); } catch { }
            bool s1 = model.Extension.SelectByID2("Front Plane", "PLANE", 0, 0, 0, false, 0, null, 0);
            if (!s1) s1 = model.Extension.SelectByID2("Front", "PLANE", 0, 0, 0, false, 0, null, 0);
            bool s2 = model.Extension.SelectByID2("Right Plane", "PLANE", 0, 0, 0, true, 0, null, 0);
            if (!s2) s2 = model.Extension.SelectByID2("Right", "PLANE", 0, 0, 0, true, 0, null, 0);
            if (!s1 || !s2) { res.Error = "Couldn't select the Front/Right planes to build the axis on."; await emit("Scribe", null, "fail", res.Error); return res; }

            // InsertAxis2 lives on IModelDoc2 (not FeatureManager) on this build and returns a bool, not the feature.
            try { model.InsertAxis2(true); }
            catch (Exception ex) { res.Error = "Couldn't insert the axis (" + ex.GetType().Name + ") — unchanged."; await emit("Scribe", null, "fail", res.Error); return res; }
            try { model.ClearSelection2(true); } catch { }
            try { model.ForceRebuild3(false); } catch { }

            // ---- Sentinel: verify by INDEPENDENT re-count (fail closed) ----
            await emit("Sentinel", "verifying the new axis", "run", null);
            res.AxesAfter = AxisCount(model);
            int rebuildErr = 0; try { rebuildErr = model.Extension.GetWhatsWrongCount(); } catch { }
            res.Verified = res.AxesAfter == res.AxesBefore + 1 && rebuildErr == 0;
            if (!res.Verified)
            {
                res.Error = res.AxesAfter != res.AxesBefore + 1
                    ? "The axis count didn't rise by 1 (" + res.AxesBefore + " → " + res.AxesAfter + ") — the axis wasn't added."
                    : "The axis was added but introduced a rebuild error — check the model.";
                await emit("Sentinel", null, "fail", res.Error);
                return res;
            }

            await emit("Sentinel", null, "done", "axis added (" + res.AxesBefore + " → " + res.AxesAfter + "), rebuild clean");
            res.Info = "Added a reference axis at the Front∩Right intersection (" + res.AxesBefore + " → " + res.AxesAfter + " axes). One Ctrl+Z undoes it; Forge didn't save.";
            return res;
        }

        private static int AxisCount(IModelDoc2 model)
        {
            int n = 0;
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    string tn = null; try { tn = f.GetTypeName2(); } catch { }
                    if (tn == RefAxisType) n++;
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch { }
            return n;
        }
    }
}
