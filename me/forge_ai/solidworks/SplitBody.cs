using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class SplitBodyResult
    {
        public bool Split;
        public int BodyCountBefore = -1;
        public int BodyCountAfter = -1;
        public string PlaneUsed;
        public string Error;
    }

    /// <summary>
    /// SplitBody (tool #220) — split an existing solid body along a reference plane into two independent bodies.
    /// "split this block in half", "split the part along the middle plane".
    ///
    /// PROBE build: `IFeatureManager.PreSplitBody`/`PostSplitBody` (found via reflection, untried) is the ONLY API
    /// surface for this — no dedicated "split" sketch-tool feature exists. This is a genuine test of the
    /// "modify-existing-solid" dead-class hypothesis in landmines.md (InsertCombineFeature/InsertMoveFace/InsertDome/
    /// InsertWrapFeature2/InsertMoveCopyBody2/InsertMultiFaceDraft/FeatureBossThicken/InsertMirrorFeature2-body-variant
    /// are ALL dead headless because they consume an EXISTING body rather than creating from a sketch/edge) — split
    /// is the same class (it consumes an existing solid, no new sketch profile involved), so this is instrumented
    /// rather than assumed dead, per the "reflect + instrument first" rule.
    ///
    /// Recipe: select a reference plane that fully bisects the body as the split tool, append-select the solid body
    /// (same `((Entity)body).Select4(true, null)` idiom AddHole.cs already proved live for body-scope selection),
    /// `PreSplitBody()` (returns the candidate result bodies), `PostSplitBody(candidates, false, null, null)` to
    /// commit. Fails CLOSED — a body count that doesn't grow is reported honest, not claimed.
    /// </summary>
    public static class SplitBody
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (!Regex.IsMatch(c, @"\bsplit\b")) return false;
            return Regex.IsMatch(c, @"\b(body|block|part|solid|half|in two)\b");
        }

        private static int CountSolidBodies(IModelDoc2 model)
        {
            try
            {
                var pd = model as PartDoc;
                var bodies = pd?.GetBodies2((int)swBodyType_e.swSolidBody, true) as object[];
                return bodies?.Length ?? 0;
            }
            catch { return 0; }
        }

        public static async Task<SplitBodyResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new SplitBodyResult();
            if (model == null) { res.Error = "Open the part you want split."; return res; }
            int docType = 0; try { docType = model.GetType(); } catch { }
            if (docType != (int)swDocumentTypes_e.swDocPART)
            { res.Error = "split_body needs an open PART (not an assembly/drawing)."; return res; }

            await emit("Scout", "counting solid bodies before the split", "run", null);
            int before = CountSolidBodies(model);
            res.BodyCountBefore = before;
            if (before < 1) { res.Error = "No solid body found to split."; await emit("Scout", null, "fail", res.Error); return res; }
            await emit("Scout", null, "done", before + " solid body");

            await emit("Splitter", "selecting a bisecting plane + the body", "run", null);
            try
            {
                model.ClearSelection2(true);
                // Right Plane bisects a centered block along its X axis — try it first, fall back to Front Plane
                // (bisects along Y) if Right Plane's own select fails (e.g. a non-centered or renamed part).
                string[] candidatePlanes = { "Right Plane", "Front Plane", "Top Plane" };
                bool selPlane = false;
                foreach (var pl in candidatePlanes)
                {
                    selPlane = model.Extension.SelectByID2(pl, "PLANE", 0, 0, 0, false, 0, null, 0);
                    if (selPlane) { res.PlaneUsed = pl; break; }
                }
                if (!selPlane) { res.Error = "Couldn't select any reference plane as the split tool."; await emit("Splitter", null, "fail", res.Error); return res; }

                var pd = model as PartDoc;
                var bodies = pd?.GetBodies2((int)swBodyType_e.swSolidBody, true) as object[];
                var body = bodies != null && bodies.Length > 0 ? bodies[0] as Body2 : null;
                if (body == null) { res.Error = "Couldn't resolve the solid body to split."; await emit("Splitter", null, "fail", res.Error); return res; }
                // Body2 has its OWN Select2(Append, SelectData), not IEntity's Select4 (unlike Face2/Edge — casting
                // to Entity throws E_NOINTERFACE on this build's Body2 COM object). Real SelectData, same
                // CreateSelectData() idiom the mate handlers already use, not a bare null.
                var bodySd = ((SelectionMgr)model.SelectionManager).CreateSelectData();
                bool selBody = body.Select2(true, bodySd);
                if (!selBody) { res.Error = "Couldn't append-select the solid body."; await emit("Splitter", null, "fail", res.Error); return res; }

                var fm = model.FeatureManager;
                object candidates = fm.PreSplitBody();
                if (candidates == null)
                {
                    res.Error = "PreSplitBody returned nothing — the plane (" + res.PlaneUsed + ") may not fully intersect the body.";
                    await emit("Splitter", null, "fail", res.Error);
                    return res;
                }
                var candArr = candidates as object[];
                await emit("Splitter", null, "run", (candArr?.Length ?? 0) + " candidate body(ies) from the split");

                // Variant 3: leave the original plane+body selection untouched (don't reselect the PreSplitBody
                // candidates), BodiesToMark=null (documented convention elsewhere in the API: null/missing == "all").
                var feat = fm.PostSplitBody(null, false, null, null);
                if (feat == null)
                {
                    res.Error = "PostSplitBody returned nothing — split did not commit.";
                    await emit("Splitter", null, "fail", res.Error);
                    return res;
                }
            }
            catch (Exception ex) { res.Error = ex.GetType().Name + ": " + ex.Message; await emit("Splitter", null, "fail", res.Error); return res; }

            try { model.ForceRebuild3(false); } catch { }
            int after = CountSolidBodies(model);
            res.BodyCountAfter = after;
            res.Split = after > before;
            await emit("Splitter", null, res.Split ? "done" : "fail", before + " -> " + after + " bodies");
            if (!res.Split && string.IsNullOrEmpty(res.Error))
                res.Error = "Split feature committed but the body count didn't grow (" + before + " -> " + after + ") — treating as a no-op.";
            return res;
        }
    }
}
