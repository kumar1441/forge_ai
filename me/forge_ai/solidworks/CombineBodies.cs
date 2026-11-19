using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class CombineBodiesResult
    {
        public bool Success;
        public bool AlreadyDone;
        public int BodyCountBefore;
        public int BodyCountAfter;   // combine-add unions all bodies -> fewer bodies
        public double VolumeMm3;     // union volume (known truth on multibody-block: 17600)
        public string FeatureName;
        public string FeatureType;
        public string Diag;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 219 — combine_bodies (WRITE, boolean ADD). ⛔ PARKED 2026-07-25: IFeatureManager.InsertCombineFeature is a
    /// SILENT NO-OP headless on this R2026x build — returns NULL, no feature, bodies unchanged. PROVEN by a throw-4
    /// sweep on the multibody-block: Body2[] tools / boxed object[] tools / selection-only null tools / ONLY the
    /// interfering pair {C,D} — ALL FOUR retNull=true, bodies stayed 4, volume 24000. So it is not a marshaling or a
    /// "needs-interference" issue; the op commits nothing. This sharpens the build's boundary: the LIVE in-model ops
    /// all CREATE geometry from a sketch/edge (extrude/cut/revolve/sweep/loft/helix/fillet/chamfer/pattern), the DEAD
    /// ones all MODIFY/consume existing SOLID geometry (InsertMoveFace, InsertDome, InsertCombineFeature,
    /// FullyDefineSketch). The temp-body boolean (Body2.Operations2 SWBODYINTERSECT, used by compare_bodies) is LIVE —
    /// it's the IN-MODEL combine FEATURE that's dead. Handler kept DORMANT + fail-CLOSED (sweep evidence in Diag/Error).
    /// Revive only if InsertCombineFeature is confirmed to work INTERACTIVELY here — do NOT re-attempt blind.
    ///
    /// Design (for the revive): unions ALL solid bodies via InsertCombineFeature(SWBODYADD, main, tools); judge by
    /// GEOMETRY (union volume = sum minus once-counted overlap = 17600 mm3 on multibody-block, bodies drop below 4);
    /// names "Forge-Combine" for idempotency; never saves.
    /// </summary>
    public static class CombineBodies
    {
        private const string CombineName = "Forge-Combine";

        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            return Regex.IsMatch(c, @"\b(combine|merge|union|fuse|weld)\b")
                   && Regex.IsMatch(c, @"\b(bod(y|ies)|solids?)\b");
        }

        public static async Task<CombineBodiesResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new CombineBodiesResult();
            var part = model as PartDoc;
            if (part == null) { res.Error = "Open a multibody part (.SLDPRT) to combine bodies."; return res; }

            var existing = FindFeature(model, CombineName);
            var bodies0 = SolidBodies(part);
            if (existing != null || bodies0.Count < 2)
            {
                res.AlreadyDone = true; res.Success = existing != null;
                res.BodyCountAfter = bodies0.Count;
                res.FeatureName = existing == null ? null : SafeName(existing);
                res.Info = existing != null
                    ? "Bodies are already combined (" + res.FeatureName + ") — nothing to do."
                    : "Only " + bodies0.Count + " solid body — nothing to combine.";
                if (existing == null) res.Error = res.Info;
                return res;
            }

            await emit("Builder", "unioning " + bodies0.Count + " solid bodies", "run", null);

            res.BodyCountBefore = bodies0.Count;
            double volBefore = TotalSolidVolumeMm3(part);
            Feature feat = null;
            var sweep = new List<string>();
            int add = (int)swBodyOperationType_e.SWBODYADD;
            // THROW-N: InsertCombineFeature may want the tool array marshaled differently, or the bodies pre-selected.
            // Instrument each variant's null return before concluding the op is dead.
            for (int variant = 0; variant < 4 && feat == null; variant++)
            {
                var fresh = SolidBodies(part);
                if (fresh.Count < 2) break;
                var main = fresh[0];
                model.ClearSelection2(true);
                try
                {
                    if (variant == 0)   // typed Body2[] tool array, all bodies selected
                    {
                        foreach (var b in fresh) { try { ((Entity)b).Select4(true, null); } catch { } }
                        var tools = new Body2[fresh.Count - 1];
                        for (int k = 1; k < fresh.Count; k++) tools[k - 1] = fresh[k];
                        feat = model.FeatureManager.InsertCombineFeature(add, main, tools) as Feature;
                    }
                    else if (variant == 1)   // boxed object[] tool array
                    {
                        foreach (var b in fresh) { try { ((Entity)b).Select4(true, null); } catch { } }
                        var tools = new object[fresh.Count - 1];
                        for (int k = 1; k < fresh.Count; k++) tools[k - 1] = fresh[k];
                        feat = model.FeatureManager.InsertCombineFeature(add, main, tools) as Feature;
                    }
                    else if (variant == 2)   // selection-driven: select all, null tool array
                    {
                        foreach (var b in fresh) { try { ((Entity)b).Select4(true, null); } catch { } }
                        feat = model.FeatureManager.InsertCombineFeature(add, main, null) as Feature;
                    }
                    else   // ONLY the interfering pair (last two by creation order = C,D on multibody-block)
                    {
                        var m2 = fresh[fresh.Count - 2]; var t2 = fresh[fresh.Count - 1];
                        try { ((Entity)m2).Select4(true, null); } catch { }
                        try { ((Entity)t2).Select4(true, null); } catch { }
                        feat = model.FeatureManager.InsertCombineFeature(add, m2, new Body2[] { t2 }) as Feature;
                    }
                }
                catch (Exception ex) { sweep.Add("v" + variant + "=ex:" + ex.Message); }
                sweep.Add("v" + variant + "=retNull" + (feat == null));
                if (feat != null) { model.ClearSelection2(true); model.ForceRebuild3(false); }
                else model.ClearSelection2(true);
            }

            if (feat == null)
            {
                res.Diag = "InsertCombineFeature no-op | sweep: " + string.Join("; ", sweep);
                res.Error = "SolidWorks refused the combine (InsertCombineFeature returned null on all variants) — may be dead headless on this build. sweep: " + string.Join("; ", sweep);
                return res;
            }
            res.Diag = "sweep: " + string.Join("; ", sweep) + " | ";
            try { feat.Name = CombineName; } catch { }

            res.FeatureName = SafeName(feat);
            res.FeatureType = SafeType(feat);
            res.BodyCountAfter = SolidBodies(part).Count;
            res.VolumeMm3 = Math.Round(TotalSolidVolumeMm3(part), 2);
            int rw = 0; try { rw = model.Extension.GetWhatsWrongCount(); } catch { }
            // union conserves volume minus the (once-counted) overlap => must be < the pre-combine sum and > 0.
            bool volDropped = res.VolumeMm3 > 0 && res.VolumeMm3 < volBefore - 0.5;
            res.Success = res.BodyCountAfter < res.BodyCountBefore && volDropped && rw == 0 && FindFeature(model, CombineName) != null;
            res.Diag += "combine name=" + res.FeatureName + " type=" + res.FeatureType + " bodies " + res.BodyCountBefore + "->" + res.BodyCountAfter + " volBefore=" + Math.Round(volBefore, 2) + " volAfter=" + res.VolumeMm3 + " rebuildErr=" + rw;

            await emit("Builder", null, "done", res.Success ? "bodies combined" : ("bodies " + res.BodyCountBefore + "->" + res.BodyCountAfter));

            res.Info = res.Success
                ? "Combined " + res.BodyCountBefore + " bodies into " + res.BodyCountAfter + " (" + res.FeatureName + "): union volume " + res.VolumeMm3 + " mm3. Undo restores them; nothing was saved."
                : "Combine did not verify (bodies " + res.BodyCountBefore + "->" + res.BodyCountAfter + ", volume " + res.VolumeMm3 + "mm3, rebuildErr=" + rw + ").";
            return res;
        }

        private static List<Body2> SolidBodies(PartDoc part)
        {
            var list = new List<Body2>();
            try
            {
                var b = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[];
                foreach (var o in b ?? new object[0]) { var body = o as Body2; if (body != null) list.Add(body); }
            }
            catch { }
            return list;
        }

        private static double TotalSolidVolumeMm3(PartDoc part)
        {
            double v = 0;
            foreach (var b in SolidBodies(part))
            {
                var mp = b.GetMassProperties(0) as double[];
                if (mp != null && mp.Length >= 4) v += mp[3] * 1e9;
            }
            return v;
        }

        private static Feature FindFeature(IModelDoc2 model, string prefix)
        {
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                string nm = SafeName(f);
                if (nm != null && nm.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return f;
                f = f.GetNextFeature() as Feature;
            }
            return null;
        }

        private static string SafeName(Feature f) { try { return f.Name; } catch { return null; } }
        private static string SafeType(Feature f) { try { return f.GetTypeName2(); } catch { return null; } }
    }
}
