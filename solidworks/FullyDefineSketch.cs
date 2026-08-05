using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class FullyDefineSketchRow
    {
        public string Name;
        public int BeforeStatus;   // raw GetConstrainedStatus BEFORE
        public int AfterStatus;    // raw GetConstrainedStatus AFTER
        public int ApiReturn;      // raw FullyDefineSketch return (INSTRUMENTED — value meaning unproven on this build)
        public bool WasUnderDefined;
        public bool NowFullyDefined;
    }

    public class FullyDefineSketchResult
    {
        public int SketchCount;
        public int UnderDefinedBefore;
        public int UnderDefinedAfter;
        public int Defined;           // how many sketches this run drove from under-defined → fully defined
        public bool AlreadyDone;      // idempotent: nothing was under-defined to begin with
        public bool NeedsConfirm;
        public string Question;
        public bool Verified;         // FAIL CLOSED: every targeted sketch is fully defined post-rebuild, count unchanged, rebuild clean
        public int FeatureCountBefore;
        public int FeatureCountAfter;
        public int RebuildErrors;
        public List<FullyDefineSketchRow> Rows = new List<FullyDefineSketchRow>();
        public string Diag;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// FullyDefineSketch (tool #150 fully_define_sketch) — the WRITE that acts on diagnose_sketch's finding: take every
    /// under-defined sketch and add the minimal relations + dimensions that pin it down, so an upstream edit can't make
    /// it drift. "fully define the sketches", "auto-dimension Sketch2", "constrain my under-defined sketch".
    ///
    /// API (DUMPED from the interop, not guessed): ISketchManager.FullyDefineSketch(
    ///     bool EntitiesToFullyDefine, bool UseRelations, int RelationsToApply, bool UseDimensions,
    ///     int HorizontalDimScheme, object HorizontalDatumDisp, int VerticalDimScheme, object VerticalDatumDisp,
    ///     int HorizontalDimPlacement, int VerticalDimPlacement) → int.
    /// It acts on the ACTIVE sketch, so each target must be entered for edit first (select the ProfileFeature → EditSketch),
    /// then exited. Its HEADLESS behaviour is UNPROVEN on this R2026x build — several complex headless APIs no-op silently
    /// (InsertMoveFace, IEquationMgr writes, Component2.Name2). So the raw return is INSTRUMENTED and recorded, but the
    /// verdict is decided ONLY by geometry: an INDEPENDENT GetConstrainedStatus re-read of every targeted sketch.
    ///
    /// Named crew:
    ///   Gauge — walk the tree's ProfileFeatures, read each sketch's constrained status; pick the under-defined ones
    ///           (optionally filtered to one named sketch). Nothing under-defined → idempotent "already fully defined".
    ///   Scribe — per sketch: EditSketch → FullyDefineSketch(all entities, all relations, baseline dims) → exit. One
    ///           ForceRebuild3 at the end. FullyDefineSketch dimensions to CURRENT geometry, so the solid never moves.
    ///   Sentinel — FAIL CLOSED (Rule #6): re-read every sketch's status independently; Verified only if every target is
    ///           now fully defined, the feature count is unchanged (this adds dims, not features/bodies), and the rebuild
    ///           is clean. UNDO is sacred (Rule #7): one Ctrl+Z removes the added dimensions; Forge never saves.
    /// </summary>
    public static class FullyDefineSketch
    {
        // NARROW + specific-first: needs a sketch noun AND an explicit "make it defined" WRITE verb. Diagnose (why/…)
        // and get_sketch_info (list/show) carry no such verb, so they keep their traffic. Excludes rename/delete/draw.
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (!Regex.IsMatch(c, @"\b(sketch|sketches)\b")) return false;
            if (Regex.IsMatch(c, @"\b(rename|delete|remove|suppress|draw|extrude|prefix|dimension of|value of)\b")) return false;
            // DiagnoseSketch owns diagnostic phrasing — "why are my sketches NOT fully defined" is a question,
            // not a write command, and must never be shadowed by this parked/dead handler (2026-07-29 regression sweep).
            if (Regex.IsMatch(c, @"\b(why|diagnose|diagnosis|explain|problem|problems|wrong)\b|\bnot\b.{0,15}\b(fully|constrain|defin)|n't\b.{0,15}\b(fully|constrain|defin)")) return false;
            return Regex.IsMatch(c, @"fully[\s-]?defin|fully[\s-]?constrain|auto[\s-]?(dimension|dim|constrain)|\bconstrain(ed|s)?\b|\bdefine\b|make\s+.*\bdefined\b");
        }

        public static async Task<FullyDefineSketchResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new FullyDefineSketchResult();
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { res.Error = "Fully-defining a sketch works on a single part — open the .SLDPRT."; return res; }

            await emit("Gauge", "reading every sketch's constrained status", "run", null);

            string wanted = ParseSketchName(intent);

            // ---- collect ProfileFeature sketches (independent tree walk), optionally filtered to one name ----
            var targets = new List<Tuple<Feature, Sketch, string>>();
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                string tn = null; try { tn = f.GetTypeName2(); } catch { }
                if (string.Equals(tn, "ProfileFeature", StringComparison.OrdinalIgnoreCase))
                {
                    string nm = null; try { nm = f.Name; } catch { }
                    if (wanted == null || Norm(nm).Contains(Norm(wanted)))
                    {
                        Sketch sk = null; try { sk = f.GetSpecificFeature2() as Sketch; } catch { }
                        if (sk != null) targets.Add(Tuple.Create(f, sk, nm));
                    }
                }
                f = f.GetNextFeature() as Feature;
            }

            res.SketchCount = targets.Count;
            if (res.SketchCount == 0)
            {
                res.Error = wanted != null
                    ? "No sketch here matches \"" + wanted + "\"."
                    : "This part has no sketches — an imported dumb solid has geometry but no sketch to define.";
                await emit("Gauge", null, "fail", res.Error);
                return res;
            }

            // ---- baseline status per sketch ----
            foreach (var t in targets)
            {
                int st = -1; try { st = t.Item2.GetConstrainedStatus(); } catch { }
                bool under = st != (int)swConstrainedStatus_e.swFullyConstrained;
                res.Rows.Add(new FullyDefineSketchRow { Name = t.Item3, BeforeStatus = st, WasUnderDefined = under });
            }
            res.UnderDefinedBefore = res.Rows.Count(r => r.WasUnderDefined);
            res.FeatureCountBefore = CountFeatures(model);

            // ---- IDEMPOTENT (Rule #5): nothing under-defined ----
            if (res.UnderDefinedBefore == 0)
            {
                res.AlreadyDone = true;
                res.Verified = true;
                res.UnderDefinedAfter = 0;
                res.FeatureCountAfter = res.FeatureCountBefore;
                foreach (var r in res.Rows) { r.AfterStatus = r.BeforeStatus; r.NowFullyDefined = true; }
                res.Info = "All " + res.SketchCount + " sketch" + (res.SketchCount == 1 ? " is" : "es are") + " already fully defined — nothing to do.";
                await emit("Scribe", null, "done", "already fully defined — no change");
                return res;
            }

            await emit("Gauge", null, "done", res.UnderDefinedBefore + " of " + res.SketchCount + " sketch(es) under-defined");
            await emit("Scribe", "adding relations + dimensions to each under-defined sketch", "run", null);

            var sm = model.SketchManager;
            // all relation types + baseline dimensions from the sketch origin (null datum). NOTE (2026-07-24): on this
            // R2026x build FullyDefineSketch RETURNS swAutodimStatusSuccess(0) but changes NOTHING headless — proven by
            // sweeping four call variants (relations+dims / dims-only / pre-selected / ordinate) against both sketches,
            // every one edit=True active=True ret=0 status=swUnderConstrained. So this handler fails CLOSED: it records
            // the raw return but the verdict is the independent status re-read. Same silent-no-op class as InsertMoveFace.
            int allRel = (int)swSketchFullyDefineRelationType_e.swSketchFullyDefineRelationType_Equal
                       | (int)swSketchFullyDefineRelationType_e.swSketchFullyDefineRelationType_Horizontal
                       | (int)swSketchFullyDefineRelationType_e.swSketchFullyDefineRelationType_Vertical
                       | (int)swSketchFullyDefineRelationType_e.swSketchFullyDefineRelationType_Tangent
                       | (int)swSketchFullyDefineRelationType_e.swSketchFullyDefineRelationType_Perpendicular
                       | (int)swSketchFullyDefineRelationType_e.swSketchFullyDefineRelationType_Colinear
                       | (int)swSketchFullyDefineRelationType_e.swSketchFullyDefineRelationType_Concentric
                       | (int)swSketchFullyDefineRelationType_e.swSketchFullyDefineRelationType_Parallel
                       | (int)swSketchFullyDefineRelationType_e.swSketchFullyDefineRelationType_Midpoint
                       | (int)swSketchFullyDefineRelationType_e.swSketchFullyDefineRelationType_Coincident;
            int hs = (int)swAutodimScheme_e.swAutodimSchemeBaseline;
            int hp = (int)swAutodimHorizontalPlacement_e.swAutodimHorizontalPlacementAbove;
            int vp = (int)swAutodimVerticalPlacement_e.swAutodimVerticalPlacementRight;

            foreach (var row in res.Rows.Where(r => r.WasUnderDefined))
            {
                var t = targets.First(x => x.Item3 == row.Name);
                int api = int.MinValue; bool activeOk = false, editOk = false;
                try
                {
                    model.ClearSelection2(true);
                    editOk = t.Item1.Select2(false, 0);
                    model.EditSketch();
                    activeOk = sm.ActiveSketch != null;
                    if (activeOk) api = sm.FullyDefineSketch(true, true, allRel, true, hs, null, hs, null, hp, vp);
                    try { model.InsertSketch2(true); } catch { try { sm.InsertSketch(true); } catch { } }
                    model.ClearSelection2(true);
                }
                catch (Exception ex) { row.ApiReturn = api; res.Diag = (res.Diag ?? "") + row.Name + ":EX(" + ex.GetType().Name + ") "; continue; }
                row.ApiReturn = api;
                res.Diag = (res.Diag ?? "") + row.Name + "(edit=" + editOk + ",active=" + activeOk + ",ret=" + api + ") ";
            }

            try { model.ForceRebuild3(false); } catch { }
            res.RebuildErrors = SafeWhatsWrong(model);
            res.FeatureCountAfter = CountFeatures(model);

            // ---- Sentinel: FAIL CLOSED — independent status re-read of every sketch ----
            await emit("Sentinel", "re-reading each sketch's constrained status", "run", null);
            foreach (var row in res.Rows)
            {
                var t = targets.First(x => x.Item3 == row.Name);
                int st = -1; try { st = t.Item2.GetConstrainedStatus(); } catch { }
                row.AfterStatus = st;
                row.NowFullyDefined = st == (int)swConstrainedStatus_e.swFullyConstrained;
            }
            res.UnderDefinedAfter = res.Rows.Count(r => !r.NowFullyDefined);
            res.Defined = res.Rows.Count(r => r.WasUnderDefined && r.NowFullyDefined);

            res.Diag = (res.Diag ?? "") + "api=[" + string.Join(",", res.Rows.Where(r => r.WasUnderDefined).Select(r => r.Name + ":" + r.ApiReturn + "→" + r.AfterStatus)) + "]";

            bool allDefined = res.UnderDefinedAfter == 0;
            bool countSame = res.FeatureCountAfter == res.FeatureCountBefore;
            bool clean = res.RebuildErrors == 0;
            res.Verified = allDefined && countSame && clean;

            if (!res.Verified)
            {
                res.Error = !allDefined
                        ? "FullyDefineSketch didn't fully constrain " + res.UnderDefinedAfter + " sketch(es) — the API no-opped or partially defined on this build. " + res.Diag
                    : !countSame
                        ? "The feature count changed during fully-define (should only add dimensions) — check the part. " + res.Diag
                        : "Fully-define left " + res.RebuildErrors + " rebuild error(s) — check the part. " + res.Diag;
                await emit("Sentinel", null, "fail", res.Error);
                return res;
            }

            await emit("Sentinel", null, "done", res.Defined + " sketch(es) now fully defined, feature count unchanged, rebuild clean");
            res.Info = "Fully defined " + res.Defined + " sketch(es) (" + res.UnderDefinedBefore + " were under-defined, now 0). " +
                       "Feature count unchanged, rebuild clean. One Ctrl+Z removes the added dimensions; Forge didn't save.";
            return res;
        }

        private static int CountFeatures(IModelDoc2 model)
        {
            int n = 0;
            try { var f = model.FirstFeature() as Feature; while (f != null) { n++; f = f.GetNextFeature() as Feature; } } catch { }
            return n;
        }

        private static string ParseSketchName(string intent)
        {
            var m = Regex.Match(intent ?? "", @"\b(sketch\s*\d+|[A-Za-z][\w\-]*sketch[\w\-]*)\b", RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            string v = m.Value.Trim();
            return Regex.IsMatch(v, @"^sketch(es)?$", RegexOptions.IgnoreCase) ? null : v;
        }

        private static string Norm(string s) { return Regex.Replace(s ?? "", @"[^0-9A-Za-z]", "").ToLowerInvariant(); }
        private static int SafeWhatsWrong(IModelDoc2 model) { try { return model.Extension.GetWhatsWrongCount(); } catch { return 0; } }
    }
}
