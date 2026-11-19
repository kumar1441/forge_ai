using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class KnitSurfacesToSolidResult
    {
        public int SheetBodyCountBefore;
        public int SheetBodyCountAfter;
        public int SolidBodyCountBefore;
        public int SolidBodyCountAfter;
        public double VolumeMm3;
        public string FeatureName;
        public bool AlreadyDone;
        public bool Verified;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 181 — knit_surfaces_to_solid (WRITE). ⛔ PARKED 2026-07-31: `IFeatureManager.InsertSewRefSurface`
    /// is a SILENT NO-OP headless on this R2026x build — returns NULL across a 3-variant sweep (UseGapFilters
    /// on/off, MergeEntities on/off) even though all 3 sheet bodies select correctly every time (confirmed
    /// selCount=3/3) and are geometrically exact (the 2 caps were built from the tube's own boundary edges, so
    /// the shared edges are bit-identical — zero real gap). Joins the modify-EXISTING-geometry dead class
    /// (InsertCombineFeature/InsertMoveFace/InsertDome/PostSplitBody/AutoBalloon/InsertLibraryFeature) — knit
    /// consumes existing surface bodies rather than creating from a sketch/edge, fitting the already-mapped
    /// live/dead boundary. Full evidence in `docs/kb/landmines.md` and the test-config `_PARKED` entry. Handler
    /// kept DORMANT + fail-CLOSED (sweep evidence in the thrown Error). Revive only if `InsertSewRefSurface` is
    /// confirmed to work INTERACTIVELY here — do NOT re-attempt blind.
    ///
    /// Design (for the revive): stitches multiple surface (sheet) bodies that together fully enclose a volume
    /// into a single SOLID body — "knit these surfaces into a solid", "stitch the surface bodies together",
    /// "turn this shelled surface model into a solid part". `IsIntent` requires a knit/stitch/sew verb AND the
    /// "surface(s)" noun — deliberately disjoint vocabulary from `CombineBodies` (combine/merge/union/fuse/weld
    /// + body/bodies/solids, a SOLID-body boolean union, a completely different operation), so no ordering
    /// dependency either direction. Selects every sheet body first (`Body2.Select2`, not an `Entity` cast — the
    /// split_body landmine's lesson for selecting a BODY directly), then knits with `TryToFormSolid=true`.
    /// Verified by an INDEPENDENT re-count of solid vs. sheet bodies and a re-summed solid volume
    /// (`Body2.GetMassProperties`), never the raw Feature return alone. Undoable (one Ctrl+Z); Forge never saves.
    /// </summary>
    public static class KnitSurfacesToSolid
    {
        private const string KnitName = "Forge-Knit";

        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            return Regex.IsMatch(c, @"\b(knit|stitch|sew)\b") && Regex.IsMatch(c, @"\bsurfaces?\b");
        }

        public static async Task<KnitSurfacesToSolidResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new KnitSurfacesToSolidResult();
            var part = model as PartDoc;
            if (part == null) { res.Error = "Open a part with surface bodies to knit them into a solid."; return res; }

            await emit("Gauge", "reading surface bodies", "run", null);
            var sheets0 = SheetBodies(part);
            var solids0 = SolidBodiesOf(part);
            res.SheetBodyCountBefore = sheets0.Count;
            res.SolidBodyCountBefore = solids0.Count;
            await emit("Gauge", null, "done", sheets0.Count + " surface body(ies), " + solids0.Count + " solid body(ies)");

            if (sheets0.Count == 0)
            {
                if (solids0.Count > 0)
                {
                    res.AlreadyDone = true; res.Verified = true;
                    res.Info = "No surface bodies left to knit — this is already a solid.";
                    await emit("Sentinel", null, "done", "already a solid");
                    return res;
                }
                res.Error = "No surface bodies found in this part — nothing to knit.";
                await emit("Sentinel", null, "fail", res.Error);
                return res;
            }
            if (sheets0.Count == 1)
            {
                res.Error = "Only 1 surface body — need at least 2 overlapping/adjoining surfaces to knit into a solid.";
                await emit("Sentinel", null, "fail", res.Error);
                return res;
            }

            await emit("Scribe", "knitting " + sheets0.Count + " surface bodies into a solid", "run", null);

            // THROW-N sweep (this codebase's convention for an uncertain API — see CombineBodies.cs): try a few
            // variants before concluding InsertSewRefSurface is dead on this build. Kept as permanent evidence.
            Feature feat = null;
            var sweep = new List<string>();
            for (int variant = 0; variant < 3 && feat == null; variant++)
            {
                model.ClearSelection2(true);
                int selCount = 0;
                foreach (var b in sheets0) { try { if (b.Select2(true, null)) selCount++; } catch { } }
                try
                {
                    if (variant == 0) feat = model.FeatureManager.InsertSewRefSurface(false, true, true, 0.001, 0.001) as Feature;
                    else if (variant == 1) feat = model.FeatureManager.InsertSewRefSurface(true, true, true, 0.001, 0.001) as Feature;   // UseGapFilters=true
                    else feat = model.FeatureManager.InsertSewRefSurface(false, true, false, 0.001, 0.001) as Feature;   // MergeEntities=false
                }
                catch (Exception ex) { sweep.Add("v" + variant + "=ex:" + ex.GetType().Name); }
                sweep.Add("v" + variant + "=sel" + selCount + "/" + sheets0.Count + " retNull" + (feat == null));
                if (feat == null) model.ClearSelection2(true);
            }
            model.ClearSelection2(true);

            if (feat == null)
            {
                res.Error = "SolidWorks refused the knit (InsertSewRefSurface returned null on all variants) — the surfaces may not fully enclose a volume, a gap exceeds tolerance, or the API is dead headless on this build. sweep: " + string.Join("; ", sweep);
                await emit("Scribe", null, "fail", res.Error);
                return res;
            }
            try { feat.Name = KnitName; } catch { }
            try { model.ForceRebuild3(false); } catch { }
            res.FeatureName = SafeName(feat);

            // ---- Sentinel: INDEPENDENT re-count of sheet vs. solid bodies + re-summed solid volume, fail closed ----
            await emit("Sentinel", "verifying the knit result", "run", null);
            var sheetsAfter = SheetBodies(part);
            var solidsAfter = SolidBodiesOf(part);
            res.SheetBodyCountAfter = sheetsAfter.Count;
            res.SolidBodyCountAfter = solidsAfter.Count;
            res.VolumeMm3 = Math.Round(TotalSolidVolumeMm3(solidsAfter), 2);

            res.Verified = res.SolidBodyCountAfter >= 1 && res.VolumeMm3 > 0 && res.SolidBodyCountAfter > res.SolidBodyCountBefore;
            if (!res.Verified)
            {
                res.Error = "Knit didn't form a solid — sheet bodies " + res.SheetBodyCountBefore + "->" + res.SheetBodyCountAfter +
                    ", solid bodies " + res.SolidBodyCountBefore + "->" + res.SolidBodyCountAfter + ", volume " + res.VolumeMm3 + "mm3.";
                await emit("Sentinel", null, "fail", res.Error);
                return res;
            }

            res.Info = "Knit " + res.SheetBodyCountBefore + " surface bodies into a solid (" + res.FeatureName + "): volume " +
                res.VolumeMm3 + " mm3. One Ctrl+Z restores the surfaces; Forge didn't save.";
            await emit("Sentinel", null, "done", "solid formed, " + res.VolumeMm3 + " mm3");
            return res;
        }

        private static List<Body2> SheetBodies(PartDoc part)
        {
            var list = new List<Body2>();
            try
            {
                var b = part.GetBodies2((int)swBodyType_e.swSheetBody, false) as object[];
                foreach (var o in b ?? new object[0]) { var body = o as Body2; if (body != null) list.Add(body); }
            }
            catch { }
            return list;
        }

        private static List<Body2> SolidBodiesOf(PartDoc part)
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

        private static double TotalSolidVolumeMm3(List<Body2> solids)
        {
            double v = 0;
            foreach (var b in solids)
            {
                var mp = b.GetMassProperties(0) as double[];
                if (mp != null && mp.Length >= 4) v += mp[3] * 1e9;
            }
            return v;
        }

        private static string SafeName(Feature f) { try { return f.Name; } catch { return null; } }
    }
}
