using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class PatternFeatureResult
    {
        public string SeedFeature;           // the feature that was patterned, e.g. "Seed-Hole"
        public bool Circular;                // circular pattern requested (else linear)
        public int Count;                    // total instances (seed + copies)
        public double SpacingMm;             // linear pitch (mm); ignored for circular
        public double AngleDeg;              // circular total angle (deg); ignored for linear
        public int CylFacesBefore = -1;      // internal cylindrical (bore) faces before, independently measured
        public int CylFacesAfter = -1;       // ... after the pattern + rebuild
        public double VolumeBeforeMm3 = -1;  // solid volume before (mm^3)
        public double VolumeAfterMm3 = -1;   // ... after (a hole pattern removes more material)
        public int RebuildErrors;            // GetWhatsWrongCount() post-rebuild (0 => clean)
        public bool Flipped;                 // the first direction missed → flipped once
        public bool RolledBack;              // created but failed to verify → deleted, part restored
        public bool Verified;                // fail closed: true ONLY when the pattern feature appeared + rebuild clean + geometry changed
        public bool AlreadyDone;             // idempotent: a Forge-Pattern already exists → nothing to do
        public string Info;
        public string Error;
    }

    /// <summary>
    /// PatternFeature (tool #210 "pattern a feature") — a REAL geometry WRITE on a single PART: it replicates an
    /// existing SEED feature into a LINEAR array (default) or a CIRCULAR array — the classic "array these 6 times"
    /// CAD move: "pattern the hole 4 times spaced 20mm", "make a linear pattern of the cut, 3 instances", "circular
    /// pattern the boss 6 times around".
    ///
    /// The FeatureLinearPattern4 / FeatureCircularPattern4 mechanics are REUSED from RecipeExecutor (validated on this
    /// R2026x interop). The handler adds the universal WRITE spine plus the two things a recipe never has to solve:
    ///   • SEED RESOLUTION (Rule #2, #8 — ground it in the live tree, never guess): the seed defaults to the
    ///     most-recent patternable feature (a cut/hole/boss/fillet the user just made — session-continuity), preferring
    ///     a Forge-tagged one; the base body feature is never patterned. Zero candidates → an honest refusal, not a
    ///     fake pattern.
    ///   • DIRECTION (linear): the body's longest linear edge is the direction reference (no extra axis feature — one
    ///     clean Ctrl+Z). A first direction that lands instances off the body (rebuild errors / no new bores) is
    ///     self-corrected with a max-1 FLIP (Rule #6).
    ///
    /// Robustness: PART only (Rule #2). Count/spacing default sensibly (3 × 20mm). IDEMPOTENT (Rule #5): the pattern is
    /// tagged "Forge-Pattern"; a rerun finds it and reports "already patterned — nothing to do". UNDO is sacred
    /// (Rule #7). FAIL CLOSED (Rule #6): after the rebuild the handler INDEPENDENTLY confirms the pattern feature
    /// exists, the rebuild is clean, and the geometry actually changed the way a real array must (more bore faces for a
    /// hole seed, or a volume change) — anything less and the Forge-Pattern is DELETED, the part restored, the failure
    /// reported honestly.
    /// </summary>
    public static class PatternFeature
    {
        private const string PatternName = "Forge-Pattern";
        private const string AxisName = "Forge-PatAxis";
        private const double MM = 0.001;
        private const int DefaultCount = 3;
        private const double DefaultSpacingMm = 20.0;
        private const double DefaultAngleDeg = 360.0;

        // feature types that make solid geometry and are sensible pattern seeds (the base body is excluded separately)
        private static readonly HashSet<string> Patternable = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        // NOTE: on this R2026x build a cut-extrude's GetTypeName2 is "ICE" (already included) — kept so a seed with a
        // non-hint name still resolves by type, not only by the name hint.
        { "Cut", "CutExtrusion", "Extrusion", "Boss", "BossExtrusion", "Fillet", "Chamfer", "HoleWzd", "HoleSeries",
          "CBORE", "CSK", "Rib", "Dome", "Draft", "ICE", "MacroFeature" };

        public static bool IsPatternFeatureIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (Regex.IsMatch(c, @"\b(remove|delete|un-?pattern|dissolve)\b")) return false;
            // a COMPONENT pattern (assembly) is a different handler — "pattern the bolts/components" is not this one
            if (Regex.IsMatch(c, @"\b(component|components|bolts?|nuts?|screws?|fasteners?|parts?)\b")) return false;
            bool verb = Regex.IsMatch(c, @"\b(pattern|array|replicate|repeat|duplicate)\b");
            bool feat = Regex.IsMatch(c, @"\b(feature|hole|cut|pocket|slot|boss|fillet|chamfer|rib|it|this|that|the)\b");
            return verb && feat;
        }

        public static async Task<PatternFeatureResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new PatternFeatureResult();

            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            { res.Error = "Patterning a feature works on a single part — open the .SLDPRT, not an assembly (component patterns are a different command)."; return res; }
            var part = model as PartDoc;
            if (part == null) { res.Error = "This document has no part geometry to pattern."; return res; }

            // ---- IDEMPOTENT (Rule #5) ----
            if (FindFeatureByName(model, PatternName) != null)
            {
                res.AlreadyDone = true;
                res.Verified = true;
                res.Info = "Already patterned — a Forge-Pattern feature is present, so there's nothing to do. To pattern " +
                           "differently, delete Forge-Pattern first (Ctrl+Z), then run again.";
                await emit("Patterner", null, "done", "Forge-Pattern already present — nothing to do");
                return res;
            }

            res.Circular = Regex.IsMatch((intent ?? "").ToLowerInvariant(), @"\b(circular|radial|around|round)\b");
            res.Count = ParseCount(intent);
            res.SpacingMm = ParseSpacingMm(intent);
            res.AngleDeg = ParseAngleDeg(intent);

            await emit("Gauge", "finding the feature to pattern", "run", null);
            Feature seed = ResolveSeed(model);
            if (seed == null)
            { res.Error = "Nothing to pattern — this part has only its base body, no hole/cut/boss/fillet to replicate. Add one first, or name the feature."; return res; }
            res.SeedFeature = SafeName(seed);

            res.CylFacesBefore = CountCylFaces(part);
            res.VolumeBeforeMm3 = GetVolumeMm3(model);
            await emit("Gauge", null, "done",
                "seed '" + res.SeedFeature + "' · " + (res.Circular ? res.Count + "× circular " + Trim(res.AngleDeg) + "°"
                    : res.Count + "× linear @ " + Trim(res.SpacingMm) + " mm") + " · " + res.CylFacesBefore + " bore face(s)");

            if (res.Count < 2)
            { res.Error = "A pattern needs at least 2 instances — say how many, e.g. 'pattern it 4 times'."; return res; }

            await emit("Patterner", (res.Circular ? "circular-" : "linear-") + "patterning '" + res.SeedFeature + "' ×" + res.Count, "run", null);

            // ---- first attempt ----
            string err = res.Circular
                ? TryCircular(model, seed, res)
                : TryLinear(model, seed, res, flip: false);
            if (err != null) { res.Error = err; RollbackPattern(model); return res; }

            try { model.ForceRebuild3(false); } catch { }
            res.RebuildErrors = SafeWhatsWrong(model);
            res.CylFacesAfter = CountCylFaces(part);
            res.VolumeAfterMm3 = GetVolumeMm3(model);
            bool geomChanged = GeometryChanged(res);

            // linear first direction missed (instances off the body → rebuild errors / no new bores) → flip once
            if (!res.Circular && (res.RebuildErrors != 0 || !geomChanged))
            {
                await emit("Patterner", "first direction missed — flipping the pattern the other way", "run", null);
                RollbackPattern(model);
                res.Flipped = true;
                err = TryLinear(model, seed, res, flip: true);
                if (err != null) { res.Error = err; RollbackPattern(model); return res; }
                try { model.ForceRebuild3(false); } catch { }
                res.RebuildErrors = SafeWhatsWrong(model);
                res.CylFacesAfter = CountCylFaces(part);
                res.VolumeAfterMm3 = GetVolumeMm3(model);
                geomChanged = GeometryChanged(res);
            }

            // ---- INDEPENDENTLY verify (Rule #6): pattern present + rebuild clean + geometry actually changed ----
            await emit("Sentinel", "verifying the pattern post-rebuild", "run", null);
            bool clean = res.RebuildErrors == 0;
            bool tagged = FindFeatureByName(model, PatternName) != null;

            if (!tagged || !clean || !geomChanged)
            {
                RollbackPattern(model);
                res.RolledBack = true;
                res.CylFacesAfter = CountCylFaces(part);
                res.VolumeAfterMm3 = GetVolumeMm3(model);
                res.Error = !clean
                    ? "The pattern rebuilt with " + res.RebuildErrors + " error(s) (instances likely fell off the part) — rolled it back; the part is unchanged."
                    : (!tagged
                        ? "The pattern could not be confirmed in the tree — rolled it back; the part is unchanged."
                        : "The pattern changed no geometry (the copies landed on top of the seed or off the body) — rolled it back; the part is unchanged.");
                await emit("Sentinel", null, "fail", "rolled back — part restored");
                return res;
            }

            res.Verified = true;
            await emit("Sentinel", null, "done",
                "patterned '" + res.SeedFeature + "' ×" + res.Count + (res.Circular ? " circular" : " linear") +
                ", bore faces " + res.CylFacesBefore + " → " + res.CylFacesAfter + ", rebuild clean");

            res.Info = BuildInfo(res);
            return res;
        }

        private static bool GeometryChanged(PatternFeatureResult r)
        {
            bool bores = r.CylFacesAfter > r.CylFacesBefore;   // a hole/round seed adds bore faces
            bool vol = r.VolumeBeforeMm3 > 0 && r.VolumeAfterMm3 > 0 &&
                       Math.Abs(r.VolumeAfterMm3 - r.VolumeBeforeMm3) > r.VolumeBeforeMm3 * 0.0005; // a cut removes / a boss adds
            return bores || vol;
        }

        private static string BuildInfo(PatternFeatureResult r)
        {
            string how = r.Circular ? (r.Count + "× circular around " + Trim(r.AngleDeg) + "°")
                                    : (r.Count + "× linear at " + Trim(r.SpacingMm) + " mm pitch" + (r.Flipped ? " (flipped into the part)" : ""));
            string geom = r.CylFacesAfter > r.CylFacesBefore
                ? "bore faces " + r.CylFacesBefore + " → " + r.CylFacesAfter
                : "volume " + r.VolumeBeforeMm3.ToString("N0") + " → " + r.VolumeAfterMm3.ToString("N0") + " mm³";
            return "Patterned '" + r.SeedFeature + "' — " + how + " (" + geom + ", rebuild clean). One Ctrl+Z removes it; Forge didn't save.";
        }

        // ================= the two pattern paths (RecipeExecutor mechanics) =================

        private static string TryLinear(IModelDoc2 model, Feature seed, PatternFeatureResult res, bool flip)
        {
            try
            {
                Edge dirEdge = LongestLinearEdge(model);
                if (dirEdge == null) return "No straight edge on the part to give the pattern a direction — can't build a linear pattern here.";

                try { model.ClearSelection2(true); } catch { }
                var sm = model.SelectionManager as SelectionMgr;
                var sd = sm.CreateSelectData(); sd.Mark = 1;
                ((Entity)dirEdge).Select4(false, sd);       // direction reference → mark 1
                if (!seed.Select2(true, 4)) return "Couldn't select the seed feature '" + SafeName(seed) + "' to pattern.";

                var feat = model.FeatureManager.FeatureLinearPattern4(
                    res.Count, res.SpacingMm * MM, 1, 0, flip, false, "", "", true,
                    false, false, false, false, false, false, false, false, false, 0, 0) as Feature;
                try { model.ClearSelection2(true); } catch { }
                if (feat == null) return "SolidWorks refused the linear pattern — the seed or direction may be invalid.";
                try { feat.Name = PatternName; } catch { }
                return null;
            }
            catch (Exception ex) { return "The linear pattern couldn't be created (" + ex.GetType().Name + ") — the part is unchanged."; }
        }

        private static string TryCircular(IModelDoc2 model, Feature seed, PatternFeatureResult res)
        {
            Feature axis = null;
            try
            {
                axis = EnsureZAxis(model);
                if (axis == null) return "Couldn't build a rotation axis for the circular pattern.";
                try { model.ClearSelection2(true); } catch { }
                if (!axis.Select2(false, 1)) return "Couldn't select the rotation axis.";   // axis → mark 1
                if (!seed.Select2(true, 4)) return "Couldn't select the seed feature '" + SafeName(seed) + "' to pattern.";

                var feat = model.FeatureManager.FeatureCircularPattern4(
                    res.Count, res.AngleDeg * Math.PI / 180.0, false, "", true, true, false) as Feature;
                try { model.ClearSelection2(true); } catch { }
                if (feat == null) return "SolidWorks refused the circular pattern — the seed or axis may be invalid.";
                try { feat.Name = PatternName; } catch { }
                return null;
            }
            catch (Exception ex) { return "The circular pattern couldn't be created (" + ex.GetType().Name + ") — the part is unchanged."; }
        }

        // Z axis (Top ∩ Right), named for cleanup; created only for the circular path.
        private static Feature EnsureZAxis(IModelDoc2 model)
        {
            var existing = FindFeatureByName(model, AxisName);
            if (existing != null) return existing;
            try { model.ClearSelection2(true); } catch { }
            if (!model.Extension.SelectByID2("Top Plane", "PLANE", 0, 0, 0, false, 0, null, 0)) return null;
            if (!model.Extension.SelectByID2("Right Plane", "PLANE", 0, 0, 0, true, 0, null, 0)) return null;
            bool ok = model.InsertAxis2(true);
            try { model.ClearSelection2(true); } catch { }
            if (!ok) return null;
            var axis = model.FeatureByPositionReverse(0) as Feature;
            if (axis != null) { try { axis.Name = AxisName; } catch { } }
            return axis;
        }

        // ================= seed resolution (grounded in the live tree) =================

        // Prefer a Forge-tagged feature (the thing the user just added); else the LAST patternable non-base feature.
        private static Feature ResolveSeed(IModelDoc2 model)
        {
            Feature forgeTag = null, lastCandidate = null, baseSolid = null;
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    string tn = SafeType(f);
                    string nm = SafeName(f);
                    bool solidish = tn != null && Patternable.Contains(tn);
                    bool nameHint = nm != null && Regex.IsMatch(nm, @"(hole|cut|pocket|slot|boss|fillet|chamfer|rib|seed)", RegexOptions.IgnoreCase);
                    if (solidish || nameHint)
                    {
                        if (baseSolid == null) baseSolid = f;         // first solid-making feature = the base body
                        else lastCandidate = f;                        // any later one is a real seed candidate
                        if (nm != null && nm.StartsWith("Forge-", StringComparison.OrdinalIgnoreCase) &&
                            !nm.Equals(PatternName, StringComparison.OrdinalIgnoreCase) && !nm.Equals(AxisName, StringComparison.OrdinalIgnoreCase))
                            forgeTag = f;
                    }
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch { }
            return forgeTag ?? lastCandidate;   // never the base body alone
        }

        // ================= geometry helpers =================

        // the longest straight edge of the solid (its direction gives a linear pattern its axis; for a long block that
        // is the length edge — the natural array direction). Independent of any pattern math.
        private static Edge LongestLinearEdge(IModelDoc2 model)
        {
            Edge best = null; double bestLen = 0;
            try
            {
                var part = model as PartDoc;
                foreach (var bo in SolidBodies(part) ?? new object[0])
                {
                    var body = bo as Body2; if (body == null) continue;
                    object[] edges = null; try { edges = body.GetEdges() as object[]; } catch { }
                    foreach (var eo in edges ?? new object[0])
                    {
                        var e = eo as Edge; if (e == null) continue;
                        var curve = e.GetCurve() as Curve; if (curve == null) continue;
                        bool line = false; try { line = curve.IsLine(); } catch { }
                        if (!line) continue;
                        double[] cp = null; try { cp = e.GetCurveParams2() as double[]; } catch { }
                        if (cp == null || cp.Length < 8) continue;
                        double dx = cp[3] - cp[0], dy = cp[4] - cp[1], dz = cp[5] - cp[2];
                        double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                        if (len > bestLen) { bestLen = len; best = e; }
                    }
                }
            }
            catch { }
            return best;
        }

        private static int CountCylFaces(PartDoc part)
        {
            int n = 0;
            foreach (var bo in SolidBodies(part) ?? new object[0])
            {
                var body = bo as Body2; if (body == null) continue;
                object[] faces = null; try { faces = body.GetFaces() as object[]; } catch { }
                foreach (var fo in faces ?? new object[0])
                {
                    var face = fo as Face2; if (face == null) continue;
                    Surface s = null; try { s = face.GetSurface() as Surface; } catch { }
                    bool cyl = false; try { cyl = s != null && s.IsCylinder(); } catch { }
                    if (cyl) n++;
                }
            }
            return n;
        }

        private static double GetVolumeMm3(IModelDoc2 model)
        {
            try { var mp = model.Extension.CreateMassProperty(); return mp == null ? -1 : mp.Volume * 1e9; }
            catch { return -1; }
        }

        private static object[] SolidBodies(PartDoc part)
        { try { return part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]; } catch { return null; } }

        private static int SafeWhatsWrong(IModelDoc2 model)
        { try { return model.Extension.GetWhatsWrongCount(); } catch { return 0; } }

        // delete the pattern (and the circular-path axis, if any) and rebuild — restores the part
        private static void RollbackPattern(IModelDoc2 model)
        {
            try
            {
                DeleteNamed(model, PatternName);
                DeleteNamed(model, AxisName);
                try { model.ForceRebuild3(false); } catch { }
                try { model.ClearSelection2(true); } catch { }
            }
            catch { }
        }

        private static void DeleteNamed(IModelDoc2 model, string name)
        {
            var f = FindFeatureByName(model, name);
            if (f == null) return;
            try { model.ClearSelection2(true); } catch { }
            bool sel = false; try { sel = f.Select2(false, 0); } catch { }
            if (sel) { try { model.EditDelete(); } catch { } }
        }

        private static Feature FindFeatureByName(IModelDoc2 model, string name)
        {
            try
            {
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    if (string.Equals(SafeName(f), name, StringComparison.OrdinalIgnoreCase)) return f;
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch { }
            return null;
        }

        private static string SafeName(Feature f) { try { return f?.Name; } catch { return null; } }
        private static string SafeType(Feature f) { try { return f?.GetTypeName2(); } catch { return null; } }

        // ================= intent parsing =================

        private static int ParseCount(string intent)
        {
            string c = (intent ?? "").ToLowerInvariant();
            var m = Regex.Match(c, @"(\d+)\s*(?:x|times|instances|copies|up|places)");
            if (m.Success && int.TryParse(m.Groups[1].Value, out int v) && v > 0) return v;
            m = Regex.Match(c, @"(?:x|×)\s*(\d+)");
            if (m.Success && int.TryParse(m.Groups[1].Value, out int v2) && v2 > 0) return v2;
            // a bare small number ("pattern the hole 4")
            m = Regex.Match(c, @"\b([2-9]|1\d|2\d)\b");
            if (m.Success && int.TryParse(m.Groups[1].Value, out int v3) && v3 >= 2) return v3;
            return DefaultCount;
        }

        private static double ParseSpacingMm(string intent)
        {
            string c = (intent ?? "").ToLowerInvariant();
            var m = Regex.Match(c, @"(\d+(\.\d+)?)\s*mm\s*(?:apart|pitch|spacing|spaced|between)?");
            if (m.Success && double.TryParse(m.Groups[1].Value, out double v) && v > 0) return v;
            m = Regex.Match(c, @"(?:spaced|spacing|pitch|apart|every)\s*(\d+(\.\d+)?)\s*mm");
            if (m.Success && double.TryParse(m.Groups[1].Value, out double v2) && v2 > 0) return v2;
            return DefaultSpacingMm;
        }

        private static double ParseAngleDeg(string intent)
        {
            string c = (intent ?? "").ToLowerInvariant();
            var m = Regex.Match(c, @"(\d+(\.\d+)?)\s*(?:deg|degrees|°)");
            if (m.Success && double.TryParse(m.Groups[1].Value, out double v) && v > 0) return v;
            return DefaultAngleDeg;
        }

        private static string Trim(double v) => v.ToString("0.###");
    }
}
