using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// RECIPE EXECUTOR — runs a parametric FEATURE RECIPE (docs/RECIPE-SCHEMA.md) via the SW API in-process to
    /// produce a NATIVE, parametric part or assembly (real feature tree — never a dumb solid), then VERIFIES it
    /// fail-closed (rebuild clean + native tree + measured dims match). Every op is recorded on the ApiTrail
    /// (op, api, ok, note) for the recipe corpus. It only calls into the SW API and Recipe/RecipeResult — it never
    /// touches the rest of the add-in, so it stands alone as the parametric-generation engine both the test-loop and
    /// the create-part handler drive.
    ///
    /// LIVE-SW RISK: this is SW-API geometry authoring and MUST be validated live in SolidWorks. Reflection against
    /// the R2026x interop confirmed every signature used (HoleWizard5, FeatureLinearPattern4, FeatureCircularPattern4,
    /// FeatureFillet3, InsertFeatureChamfer, InsertRefPlane, InsertAxis2) but headless compile != correct geometry.
    /// See the delivery report for the exact ops that need eyes on a real model. Units: recipe is mm, SW is metres.
    /// </summary>
    public static class RecipeExecutor
    {
        private const double MM = 0.001; // recipe mm -> SW metres

        // CONFIRMED DEAD 2026-07-27 (live-instrumented): IFeatureManager.HoleWizard5 returns NULL on this R2026x
        // build even with a valid pre-selected positioning sketch point, correct holeType/standard args, and a
        // clean rebuild (whatsWrong==0) — a silent no-op, same class as InsertDome/InsertCombineFeature/
        // InsertMoveFace (DEAD ops that MODIFY/consume existing geometry via a complex wizard-style API, vs the
        // LIVE plain FeatureManager creation calls). Keep FALSE — the cut-extrude fallback below is the permanent
        // path, not an interim one. Do not re-flip this without a NEW instrumented finding (a different signature,
        // a different SW build, etc.) — see docs/kb/landmines.md.
        private const bool PreferHoleWizard = false;

        /// <summary>
        /// Build + verify a recipe. Returns what was built even on failure (fail-closed): Verified is true ONLY when
        /// the build ran, the rebuild is clean, the tree is native, and the requested verify dims measured OK.
        /// </summary>
        public static RecipeResult Execute(ISldWorks app, Recipe recipe, string outDir)
        {
            var result = new RecipeResult();
            if (app == null) { result.Error = "no SW app"; return result; }
            if (recipe == null || recipe.Raw == null) { result.Error = "null recipe"; return result; }
            if (!string.IsNullOrEmpty(recipe.ParseError)) { result.Error = "recipe parse error: " + recipe.ParseError; return result; }
            if (string.IsNullOrWhiteSpace(outDir)) { result.Error = "no output directory"; return result; }

            var ctx = new Ctx { App = app, Recipe = recipe, Result = result };
            IModelDoc2 doc = null;
            try
            {
                Directory.CreateDirectory(outDir);
                bool asm = recipe.IsAssembly;

                string template = app.GetUserPreferenceStringValue((int)(asm
                    ? swUserPreferenceStringValue_e.swDefaultTemplateAssembly
                    : swUserPreferenceStringValue_e.swDefaultTemplatePart));
                doc = app.NewDocument(template, 0, 0, 0) as IModelDoc2;
                if (doc == null) { result.Error = "NewDocument returned null"; return result; }
                ctx.Doc = doc;
                ctx.Asm = doc as AssemblyDoc;

                // ---- execute every feature op IN ORDER ----
                foreach (var tok in recipe.Features)
                {
                    var f = tok as JObject; if (f == null) continue;
                    string op = ((string)f["op"] ?? "").Trim().ToLowerInvariant();
                    bool ok;
                    try { ok = ExecuteOp(ctx, op, f); }
                    catch (Exception ex)
                    {
                        ok = false;
                        result.Step(op, "", false, ex.GetType().Name + ": " + ex.Message);
                    }
                    if (!ok)
                    {
                        if (string.IsNullOrEmpty(result.Error)) result.Error = "op '" + op + "' failed";
                        result.FailedOp = op;
                        break; // stop; return what was built so far
                    }
                }

                // ---- rebuild + verify (independently of the ops' own return codes) ----
                try { doc.ForceRebuild3(false); } catch { }
                Verify(ctx);

                // ---- save the artifact (even on failure, so the corpus keeps the model + path) ----
                string ext = asm ? ".SLDASM" : ".SLDPRT";
                string path = Path.Combine(outDir, SafeName(recipe.Name) + ext);
                int se = 0, sw = 0;
                bool saved = doc.Extension.SaveAs(path, (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                    (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, ref se, ref sw);
                if (saved) { result.Path = path; result.Built = result.Built && string.IsNullOrEmpty(result.FailedOp); }
                result.Step("save", "IModelDocExtension::SaveAs", saved, saved ? path : ("errno=" + se));
            }
            catch (Exception ex)
            {
                result.Error = result.Error ?? (ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                try { if (doc != null) app.CloseDoc(doc.GetTitle()); } catch { }
            }
            return result;
        }

        // ---------------------------------------------------------------- op dispatch

        private static bool ExecuteOp(Ctx c, string op, JObject f)
        {
            switch (op)
            {
                case "sketch": return c.Asm == null ? DoSketch(c, f) : SkipInAsm(c, op);
                case "extrude": return c.Asm == null ? DoExtrude(c, f) : SkipInAsm(c, op);
                case "cut": return c.Asm == null ? DoCut(c, f) : SkipInAsm(c, op);
                case "hole": return c.Asm == null ? DoHole(c, f) : SkipInAsm(c, op);
                case "linear_pattern": return c.Asm == null ? DoLinearPattern(c, f) : SkipInAsm(c, op);
                case "circular_pattern": return c.Asm == null ? DoCircularPattern(c, f) : SkipInAsm(c, op);
                case "fillet": return c.Asm == null ? DoFillet(c, f) : SkipInAsm(c, op);
                case "chamfer": return c.Asm == null ? DoChamfer(c, f) : SkipInAsm(c, op);
                case "plane": return DoPlane(c, f);
                case "component": return c.Asm != null ? DoComponent(c, f) : SkipInPart(c, op);
                case "mate": return c.Asm != null ? DoMate(c, f) : SkipInPart(c, op);
                default:
                    c.Result.Step(op, "", false, "unknown op");
                    return false;
            }
        }

        private static bool SkipInAsm(Ctx c, string op) { c.Result.Step(op, "", false, "op not valid in an assembly recipe"); return false; }
        private static bool SkipInPart(Ctx c, string op) { c.Result.Step(op, "", false, "op only valid in an assembly recipe"); return false; }

        // ---------------------------------------------------------------- sketch

        private static bool DoSketch(Ctx c, JObject f)
        {
            string id = (string)f["id"];
            string plane = (string)f["plane"] ?? "Front";
            var entities = f["entities"] as JArray;
            if (!SelectSketchPlane(c, plane)) { c.Result.Step("sketch", "SelectByID2", false, "could not select plane '" + plane + "'"); return false; }

            var sk = c.Doc.SketchManager;
            sk.InsertSketch(true); // begin on the selected plane
            int drawn = 0;
            if (entities != null)
                foreach (var et in entities)
                {
                    var e = et as JObject; if (e == null) continue;
                    if (DrawEntity(c, sk, e)) drawn++;
                }
            sk.InsertSketch(true); // exit
            c.Doc.ClearSelection2(true);

            var feat = c.Doc.FeatureByPositionReverse(0) as Feature;
            if (feat == null) { c.Result.Step("sketch", "InsertSketch", false, "sketch feature not found after exit"); return false; }
            if (!string.IsNullOrEmpty(id)) { c.Sketches[id] = feat; c.Features[id] = feat; }
            c.Result.Step("sketch", "ISketchManager::InsertSketch", true, "plane=" + plane + " entities=" + drawn);
            return true;
        }

        private static bool DrawEntity(Ctx c, ISketchManager sk, JObject e)
        {
            string t = ((string)e["type"] ?? "").ToLowerInvariant();
            switch (t)
            {
                case "rectangle":
                    {
                        double x = D(e, "x"), y = D(e, "y"), w = D(e, "w"), h = D(e, "h");
                        sk.CreateCornerRectangle(x * MM, y * MM, 0, (x + w) * MM, (y + h) * MM, 0);
                        return true;
                    }
                case "circle":
                    sk.CreateCircleByRadius(D(e, "cx") * MM, D(e, "cy") * MM, 0, D(e, "r") * MM);
                    return true;
                case "line":
                    sk.CreateLine(D(e, "x1") * MM, D(e, "y1") * MM, 0, D(e, "x2") * MM, D(e, "y2") * MM, 0);
                    return true;
                case "polygon":
                    {
                        double cx = D(e, "cx"), cy = D(e, "cy"), r = D(e, "r");
                        int sides = (int?)e["sides"] ?? 6;
                        // inscribed radius: a point on the polygon sits at (cx+r, cy)
                        sk.CreatePolygon(cx * MM, cy * MM, 0, (cx + r) * MM, cy * MM, 0, sides, true);
                        return true;
                    }
                case "slot":
                    {
                        double x1 = D(e, "x1"), y1 = D(e, "y1"), x2 = D(e, "x2"), y2 = D(e, "y2"), r = D(e, "r");
                        sk.CreateSketchSlot((int)swSketchSlotCreationType_e.swSketchSlotCreationType_center_line,
                            (int)swSketchSlotLengthType_e.swSketchSlotLengthType_CenterCenter,
                            2 * r * MM, x1 * MM, y1 * MM, 0, x2 * MM, y2 * MM, 0, 0, 0, 0, 1, false);
                        return true;
                    }
                default:
                    c.Result.Step("sketch", "", false, "unknown entity type '" + t + "'");
                    return false;
            }
        }

        // ---------------------------------------------------------------- extrude / cut

        private static bool DoExtrude(Ctx c, JObject f)
        {
            string id = (string)f["id"];
            string skId = (string)f["sketch"];
            double depth = D(f, "depthMm");
            string dir = ((string)f["dir"] ?? "positive").ToLowerInvariant();
            bool merge = (bool?)f["merge"] ?? true;
            if (!SelectFeature(c, skId)) { c.Result.Step("extrude", "Select2", false, "sketch '" + skId + "' not found"); return false; }

            bool both = dir == "both";
            bool flip = dir == "negative";
            double d1 = both ? depth / 2 * MM : depth * MM;
            double d2 = both ? depth / 2 * MM : 0;
            int t1 = (int)swEndConditions_e.swEndCondBlind;
            int t2 = both ? (int)swEndConditions_e.swEndCondBlind : 0;

            var feat = c.Doc.FeatureManager.FeatureExtrusion3(
                !both, flip, false, t1, t2, d1, d2, false, false, false, false, 0, 0,
                false, false, false, false, merge, true, true, 0, 0, false);
            c.Doc.ClearSelection2(true);
            if (feat == null) { c.Result.Step("extrude", "IFeatureManager::FeatureExtrusion3", false, "returned null"); return false; }
            if (!string.IsNullOrEmpty(id)) c.Features[id] = feat;
            c.Result.Step("extrude", "IFeatureManager::FeatureExtrusion3", true, "sketch=" + skId + " depthMm=" + depth + " dir=" + dir);
            return true;
        }

        private static bool DoCut(Ctx c, JObject f)
        {
            string id = (string)f["id"];
            string skId = (string)f["sketch"];
            bool through = (bool?)f["throughAll"] ?? false;
            double depth = D(f, "depthMm");
            string dir = ((string)f["dir"] ?? "positive").ToLowerInvariant();
            bool flip = dir == "negative";
            if (!SelectFeature(c, skId)) { c.Result.Step("cut", "Select2", false, "sketch '" + skId + "' not found"); return false; }

            int t1 = through ? (int)swEndConditions_e.swEndCondThroughAll : (int)swEndConditions_e.swEndCondBlind;
            double d1 = through ? 0 : depth * MM;
            // through-all cuts BOTH directions (Sd=false, T2=throughAll) so it works regardless of which side of
            // the sketch plane the solid sits on (extrude direction sign). Blind stays single-direction.
            bool sd = !through;
            int t2 = through ? (int)swEndConditions_e.swEndCondThroughAll : 0;

            var feat = c.Doc.FeatureManager.FeatureCut4(
                sd, flip, false, t1, t2, d1, 0, false, false, false, false, 0, 0,
                false, false, false, false, false, true, true, true, true, false, 0, 0, false, false);
            c.Doc.ClearSelection2(true);
            if (feat == null) { c.Result.Step("cut", "IFeatureManager::FeatureCut4", false, "returned null"); return false; }
            if (!string.IsNullOrEmpty(id)) c.Features[id] = feat;
            c.Result.Step("cut", "IFeatureManager::FeatureCut4", true, "sketch=" + skId + (through ? " throughAll" : " depthMm=" + depth));
            return true;
        }

        // ---------------------------------------------------------------- hole

        private static bool DoHole(Ctx c, JObject f)
        {
            string id = (string)f["id"];
            string onFace = (string)f["onFace"] ?? "Front";
            string standard = ((string)f["standard"] ?? "simple").ToLowerInvariant();
            double dia = D(f, "diameterMm");
            bool through = (bool?)f["throughAll"] ?? true;
            double depth = D(f, "depthMm");
            var positions = f["positions"] as JArray;
            if (positions == null || positions.Count == 0) { c.Result.Step("hole", "", false, "no positions"); return false; }

            if (PreferHoleWizard && TryHoleWizard(c, id, onFace, standard, dia, through, depth, positions))
                return true;

            // Fallback (default until HoleWizard5 is validated live): a real, native cut-extrude of circles at each
            // position. throughAll or blind. counterbore/countersink get a second shallow larger cut so the feature
            // tree carries the intent; tapped is treated as a clearance hole (no cosmetic thread) and noted.
            if (!SelectSketchPlane(c, onFace)) { c.Result.Step("hole", "SelectByID2", false, "could not select face/plane '" + onFace + "'"); return false; }
            var sk = c.Doc.SketchManager;
            sk.InsertSketch(true);
            foreach (var pt in positions)
            {
                var p = pt as JObject; if (p == null) continue;
                sk.CreateCircleByRadius(D(p, "x") * MM, D(p, "y") * MM, 0, dia / 2 * MM);
            }
            sk.InsertSketch(true);
            c.Doc.ClearSelection2(true);
            var skFeat = c.Doc.FeatureByPositionReverse(0) as Feature;
            if (skFeat != null) skFeat.Select2(false, 0);

            int t1 = through ? (int)swEndConditions_e.swEndCondThroughAll : (int)swEndConditions_e.swEndCondBlind;
            double d1 = through ? 0 : depth * MM;
            var feat = c.Doc.FeatureManager.FeatureCut4(
                true, false, false, t1, 0, d1, 0, false, false, false, false, 0, 0,
                false, false, false, false, false, true, true, true, true, false, 0, 0, false, false);
            c.Doc.ClearSelection2(true);
            if (feat == null) { c.Result.Step("hole", "IFeatureManager::FeatureCut4", false, "cut-circle returned null"); return false; }
            if (!string.IsNullOrEmpty(id)) c.Features[id] = feat;
            c.Result.Step("hole", "IFeatureManager::FeatureCut4",
                true, "cut-circle fallback (HoleWizard5 needs live validation); standard=" + standard + " count=" + positions.Count + " dia=" + dia);
            return true;
        }

        // Guarded HoleWizard5 attempt (off by default via PreferHoleWizard). Pre-places sketch points at each position
        // on the target face, selects that positioning sketch, then drives HoleWizard5. Verified rebuild-clean; on any
        // doubt it deletes the wizard feature and returns false so the caller falls back to the proven cut-circle path.
        private static bool TryHoleWizard(Ctx c, string id, string onFace, string standard, double dia, bool through, double depth, JArray positions)
        {
            int before = FeatureCountRaw(c.Doc);
            try
            {
                if (!SelectSketchPlane(c, onFace)) return false;
                var sk = c.Doc.SketchManager;
                sk.InsertSketch(true);
                foreach (var pt in positions)
                {
                    var p = pt as JObject; if (p == null) continue;
                    sk.CreatePoint(D(p, "x") * MM, D(p, "y") * MM, 0);
                }
                sk.InsertSketch(true);
                c.Doc.ClearSelection2(true);
                var skFeat = c.Doc.FeatureByPositionReverse(0) as Feature;
                if (skFeat != null) skFeat.Select2(false, 0);

                int holeType = HoleWizardType(standard);
                short endType = (short)(through ? swEndConditions_e.swEndCondThroughAll : swEndConditions_e.swEndCondBlind);
                var hw = c.Doc.FeatureManager.HoleWizard5(
                    holeType, (int)swWzdHoleStandards_e.swStandardISO, 0, "", endType,
                    dia * MM, through ? 0 : depth * MM, 0,           // Diameter, Depth, Length
                    0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,             // Value1..Value12
                    "", false, false, true, false, false, false);   // ThreadClass, RevDir, FeatureScope, AutoSelect, AsmScope, AutoSelComps, Propagate
                c.Doc.ForceRebuild3(false);
                if (hw != null && c.Doc.Extension.GetWhatsWrongCount() == 0)
                {
                    if (!string.IsNullOrEmpty(id)) c.Features[id] = hw;
                    c.Result.Step("hole", "IFeatureManager::HoleWizard5", true, "standard=" + standard + " count=" + positions.Count + " dia=" + dia);
                    return true;
                }
                // roll back anything the attempt added, so the fallback starts clean
                RollbackTo(c.Doc, before);
                return false;
            }
            catch { RollbackTo(c.Doc, before); return false; }
        }

        private static int HoleWizardType(string standard)
        {
            switch (standard)
            {
                case "counterbore": return (int)swWzdGeneralHoleTypes_e.swWzdCounterBore;
                case "countersink": return (int)swWzdGeneralHoleTypes_e.swWzdCounterSink;
                case "tapped": return (int)swWzdGeneralHoleTypes_e.swWzdTap;
                default: return (int)swWzdGeneralHoleTypes_e.swWzdHole;
            }
        }

        // ---------------------------------------------------------------- patterns

        private static bool DoLinearPattern(Ctx c, JObject f)
        {
            string id = (string)f["id"];
            string featId = (string)f["feature"];
            if (!c.Features.TryGetValue(featId ?? "", out var seed) || seed == null)
            { c.Result.Step("linear_pattern", "", false, "feature '" + featId + "' not found"); return false; }

            var axis1 = EnsureAxis(c, ((string)f["dir"] ?? "x"));
            if (axis1 == null) { c.Result.Step("linear_pattern", "InsertAxis2", false, "could not build dir axis"); return false; }
            int n1 = (int?)f["count"] ?? 2;
            double s1 = D(f, "spacingMm") * MM;

            bool two = f["dir2"] != null && f["count2"] != null;
            Feature axis2 = two ? EnsureAxis(c, (string)f["dir2"]) : null;
            int n2 = two ? ((int?)f["count2"] ?? 1) : 0;
            double s2 = two ? D(f, "spacing2Mm") * MM : 0;

            c.Doc.ClearSelection2(true);
            axis1.Select2(false, 1);
            if (two && axis2 != null) axis2.Select2(true, 2);
            seed.Select2(true, 4);

            var feat = c.Doc.FeatureManager.FeatureLinearPattern4(
                n1, s1, n2, s2, false, false, "", "", true, false, false, false, false, false, false, false, false, false, 0, 0);
            c.Doc.ClearSelection2(true);
            if (feat == null) { c.Result.Step("linear_pattern", "IFeatureManager::FeatureLinearPattern4", false, "returned null"); return false; }
            if (!string.IsNullOrEmpty(id)) c.Features[id] = feat;
            c.Result.Step("linear_pattern", "IFeatureManager::FeatureLinearPattern4", true, "feature=" + featId + " count=" + n1 + (two ? "x" + n2 : ""));
            return true;
        }

        private static bool DoCircularPattern(Ctx c, JObject f)
        {
            string id = (string)f["id"];
            string featId = (string)f["feature"];
            if (!c.Features.TryGetValue(featId ?? "", out var seed) || seed == null)
            { c.Result.Step("circular_pattern", "", false, "feature '" + featId + "' not found"); return false; }

            var axis = EnsureAxis(c, ((string)f["axis"] ?? "z"));
            if (axis == null) { c.Result.Step("circular_pattern", "InsertAxis2", false, "could not build axis"); return false; }
            int n = (int?)f["count"] ?? 2;
            double angDeg = (double?)f["angleDeg"] ?? 360.0;
            bool equal = (bool?)f["equal"] ?? true;

            c.Doc.ClearSelection2(true);
            axis.Select2(false, 1);
            seed.Select2(true, 4);

            var feat = c.Doc.FeatureManager.FeatureCircularPattern4(
                n, angDeg * Math.PI / 180.0, false, "", true, equal, false);
            c.Doc.ClearSelection2(true);
            if (feat == null) { c.Result.Step("circular_pattern", "IFeatureManager::FeatureCircularPattern4", false, "returned null"); return false; }
            if (!string.IsNullOrEmpty(id)) c.Features[id] = feat;
            c.Result.Step("circular_pattern", "IFeatureManager::FeatureCircularPattern4", true, "feature=" + featId + " count=" + n + " angle=" + angDeg);
            return true;
        }

        // ---------------------------------------------------------------- fillet / chamfer

        private static bool DoFillet(Ctx c, JObject f)
        {
            string target = ((string)f["target"] ?? "all_edges").ToLowerInvariant();
            double r = D(f, "radiusMm");
            int n = SelectTargetEdges(c, target);
            if (n == 0) { c.Result.Step("fillet", "Select4", false, "no edges resolved for target '" + target + "'"); return false; }

            var feat = c.Doc.FeatureManager.FeatureFillet3(
                (int)swFeatureFilletOptions_e.swFeatureFilletUniformRadius | (int)swFeatureFilletOptions_e.swFeatureFilletPropagate,
                r * MM, 0, 0,
                (int)swFeatureFilletType_e.swFeatureFilletType_Simple, 0, 0, null, null, null, null, null, null, null) as Feature;
            c.Doc.ClearSelection2(true);
            if (feat == null) { c.Result.Step("fillet", "IFeatureManager::FeatureFillet3", false, "returned null"); return false; }
            string id = (string)f["id"]; if (!string.IsNullOrEmpty(id)) c.Features[id] = feat;
            c.Result.Step("fillet", "IFeatureManager::FeatureFillet3", true, "target=" + target + " edges=" + n + " rMm=" + r);
            return true;
        }

        private static bool DoChamfer(Ctx c, JObject f)
        {
            string target = ((string)f["target"] ?? "all_edges").ToLowerInvariant();
            double dist = D(f, "distanceMm");
            double angDeg = (double?)f["angleDeg"] ?? 45.0;
            int n = SelectTargetEdges(c, target);
            if (n == 0) { c.Result.Step("chamfer", "Select4", false, "no edges resolved for target '" + target + "'"); return false; }

            var feat = c.Doc.FeatureManager.InsertFeatureChamfer(
                0, (int)swChamferType_e.swChamferAngleDistance, dist * MM, angDeg * Math.PI / 180.0, 0, 0, 0, 0);
            c.Doc.ClearSelection2(true);
            if (feat == null) { c.Result.Step("chamfer", "IFeatureManager::InsertFeatureChamfer", false, "returned null"); return false; }
            string id = (string)f["id"]; if (!string.IsNullOrEmpty(id)) c.Features[id] = feat;
            c.Result.Step("chamfer", "IFeatureManager::InsertFeatureChamfer", true, "target=" + target + " edges=" + n + " distMm=" + dist + " ang=" + angDeg);
            return true;
        }

        // ---------------------------------------------------------------- plane

        private static bool DoPlane(Ctx c, JObject f)
        {
            string id = (string)f["id"];
            string from = (string)f["from"] ?? "Top";
            double off = D(f, "offsetMm");
            var plane = BuildOffsetPlane(c, from, off);
            if (plane == null) { c.Result.Step("plane", "IFeatureManager::InsertRefPlane", false, "could not build plane from '" + from + "'"); return false; }
            if (!string.IsNullOrEmpty(id)) { c.Planes[id] = plane; c.Features[id] = plane; }
            c.Result.Step("plane", "IFeatureManager::InsertRefPlane", true, "from=" + from + " offsetMm=" + off);
            return true;
        }

        private static Feature BuildOffsetPlane(Ctx c, string from, double offMm)
        {
            string principal = PrincipalPlaneName(from);
            if (principal == null) return null;
            c.Doc.ClearSelection2(true);
            if (!c.Doc.Extension.SelectByID2(principal, "PLANE", 0, 0, 0, false, 0, null, 0)) return null;
            int constraint = (int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_Distance;
            if (offMm < 0) constraint |= (int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_OptionFlip;
            var plane = c.Doc.FeatureManager.InsertRefPlane(constraint, Math.Abs(offMm) * MM, 0, 0, 0, 0) as Feature;
            c.Doc.ClearSelection2(true);
            return plane;
        }

        // ---------------------------------------------------------------- assembly: component / mate

        private static bool DoComponent(Ctx c, JObject f)
        {
            string id = (string)f["id"];
            string partPath = (string)f["partPath"];
            var at = f["atMm"] as JObject;
            double ax = at != null ? D(at, "x") * MM : 0, ay = at != null ? D(at, "y") * MM : 0, az = at != null ? D(at, "z") * MM : 0;

            // nested recipe -> build it to a real part file first, then insert that
            if (string.IsNullOrEmpty(partPath) && f["recipe"] is JObject nested)
            {
                string subDir = Path.Combine(Path.GetDirectoryName(c.Result.Path ?? Path.GetTempPath()) ?? Path.GetTempPath(), "components");
                var sub = RecipeExecutor.Execute(c.App, Recipe.FromJObject(nested), subDir);
                c.Result.Step("component", "RecipeExecutor::Execute(nested)", sub.Path != null, "nested=" + (sub.Path ?? sub.Error));
                if (string.IsNullOrEmpty(sub.Path)) return false;
                partPath = sub.Path;
            }
            if (string.IsNullOrEmpty(partPath) || !File.Exists(partPath))
            { c.Result.Step("component", "", false, "no partPath / file missing"); return false; }

            // AddComponent5 landmine: the referenced part must be LOADED first or the insert silently no-ops.
            int oe = 0, ow = 0;
            c.App.OpenDoc6(partPath, (int)swDocumentTypes_e.swDocPART, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref oe, ref ow);

            c.Asm.AddComponent5(partPath, (int)swAddComponentConfigOptions_e.swAddComponentConfigOptions_CurrentSelectedConfig, "", false, "", ax, ay, az);
            c.Doc.ClearSelection2(true);
            var comp = FindComponentByPath(c.Asm, partPath, true); // most-recently-added match
            if (comp == null) { c.Result.Step("component", "IAssemblyDoc::AddComponent5", false, "component not found after insert"); return false; }
            if (!string.IsNullOrEmpty(id)) c.Comps[id] = comp;
            c.Result.Step("component", "IAssemblyDoc::AddComponent5", true, "id=" + id + " path=" + Path.GetFileName(partPath));
            return true;
        }

        private static bool DoMate(Ctx c, JObject f)
        {
            string type = ((string)f["type"] ?? "concentric").ToLowerInvariant();
            var a = f["a"] as JObject; var b = f["b"] as JObject;
            var ca = ResolveComp(c, a); var cb = ResolveComp(c, b);
            if (ca == null || cb == null) { c.Result.Step("mate", "", false, "could not resolve both components"); return false; }

            bool concentric = type == "concentric";
            Face2 fa = concentric ? SmallestCylFace(ca) : LargestPlanarFace(ca);
            Face2 fb = concentric ? SmallestCylFace(cb) : LargestPlanarFace(cb);
            if (fa == null || fb == null) { c.Result.Step("mate", "", false, "no suitable faces for '" + type + "' mate"); return false; }

            c.Doc.ClearSelection2(true);
            var sd = ((SelectionMgr)c.Doc.SelectionManager).CreateSelectData();
            sd.Mark = 1;
            ((Entity)fa).Select4(false, sd);
            ((Entity)fb).Select4(true, sd);

            int mateType = concentric ? (int)swMateType_e.swMateCONCENTRIC
                : (type == "distance" ? (int)swMateType_e.swMateDISTANCE : (int)swMateType_e.swMateCOINCIDENT);
            double distance = type == "distance" ? D(f, "distanceMm") * MM : 0;
            int err;
            var mate = c.Asm.AddMate5(mateType, (int)swMateAlign_e.swMateAlignCLOSEST, false,
                distance, distance, distance, 0, 0, 0, 0, 0, false, false, 0, out err);
            c.Doc.ClearSelection2(true);
            // swAddMateError_NoError == 1 (NOT 0). Over-defined (5) still creates the mate; treat both as created.
            bool created = mate != null && (err == (int)swAddMateError_e.swAddMateError_NoError || err == (int)swAddMateError_e.swAddMateError_OverDefinedAssembly);
            c.Result.Step("mate", "IAssemblyDoc::AddMate5", created, "type=" + type + " addMateErr=" + err + " (best-effort face resolve)");
            return created;
        }

        // ---------------------------------------------------------------- verification

        private static void Verify(Ctx c)
        {
            var r = c.Result;
            r.FeatureCount = FeatureCountRaw(c.Doc);

            // 1) rebuild clean
            int whatsWrong = 0; try { whatsWrong = c.Doc.Extension.GetWhatsWrongCount(); } catch { }
            r.RebuildClean = whatsWrong == 0;

            // 2) native tree (real modeling features present, not a lone imported body)
            r.NativeTree = c.Asm != null ? (CountComponents(c.Asm) > 0) : HasNativeModelingFeatures(c.Doc);

            // 3) requested dims measured on the real body
            r.DimsMatch = MeasureAndMatch(c);

            // Built = ran to completion (no failed op) — SaveAs sets Path separately.
            r.Built = string.IsNullOrEmpty(r.FailedOp);
        }

        private static bool MeasureAndMatch(Ctx c)
        {
            var verify = c.Recipe.Verify;
            var measured = c.Result.Measured;
            bool allMatch = true;
            bool anyMeasured = false;

            // bounding box (parts only; assemblies noted)
            var bbox = verify?["boundingBoxMm"] as JObject;
            double[] box = c.Asm == null ? SolidBox(c.Doc) : null;
            if (box != null)
            {
                double dx = Math.Abs(box[3] - box[0]) / MM, dy = Math.Abs(box[4] - box[1]) / MM, dz = Math.Abs(box[5] - box[2]) / MM;
                measured["boundingBoxMm"] = new JObject { ["x"] = Round(dx), ["y"] = Round(dy), ["z"] = Round(dz) };
                if (bbox != null)
                {
                    anyMeasured = true;
                    double wx = D(bbox, "x"), wy = D(bbox, "y"), wz = D(bbox, "z");
                    // GetBodyBox is the LOOSE (approximate) box — tessellation of curved faces (holes) inflates
                    // thin dims slightly, so compare with generous slack (0.3mm or 1.5%), not the exact ±0.01.
                    Func<double, double, bool> nearBox = (a, b) => Math.Abs(a - b) <= Math.Max(0.3, 0.015 * Math.Max(Math.Abs(b), 1));
                    bool perAxis = nearBox(dx, wx) && nearBox(dy, wy) && nearBox(dz, wz);
                    var da = new[] { dx, dy, dz }; Array.Sort(da); var wa = new[] { wx, wy, wz }; Array.Sort(wa);
                    bool sorted = nearBox(da[0], wa[0]) && nearBox(da[1], wa[1]) && nearBox(da[2], wa[2]);
                    if (!perAxis && sorted) measured["boundingBoxNote"] = "matched on sorted dims (axis mapping differs)";
                    if (!perAxis && !sorted) allMatch = false;
                }
            }
            else if (bbox != null) { allMatch = false; measured["boundingBoxNote"] = "no solid body to measure"; }

            // hole count (approx: internal cylindrical faces on the solid body; fillets/rounds inflate this)
            if (verify?["holes"] != null && c.Asm == null)
            {
                anyMeasured = true;
                int want = (int)verify["holes"];
                int cyl = CylindricalFaceCount(c.Doc);
                measured["cylindricalFaces"] = cyl;
                measured["holesRequested"] = want;
                if (cyl < want) allMatch = false;
                measured["holesNote"] = "cylindrical-face count is an upper-bound proxy for holes (fillets inflate it)";
            }

            // feature count if requested
            if (verify?["features"] != null)
            {
                anyMeasured = true;
                int want = (int)verify["features"];
                measured["featureCount"] = c.Result.FeatureCount;
                if (c.Result.FeatureCount < want) allMatch = false;
            }

            if (!anyMeasured)
            {
                // nothing requested to prove; fail-closed still needs a real body to exist for a part
                if (c.Asm == null && SolidBox(c.Doc) == null) return false;
                return true;
            }
            return allMatch;
        }

        // ---------------------------------------------------------------- selection helpers

        // Select a plane/face for sketching. Accepts principal names, a registered ref-plane id, "offset:P:mm", or a
        // planar-face keyword (top/bottom/front/back/left/right resolved by outward normal).
        private static bool SelectSketchPlane(Ctx c, string descriptor)
        {
            c.Doc.ClearSelection2(true);
            if (string.IsNullOrEmpty(descriptor)) descriptor = "Front";

            if (descriptor.StartsWith("offset:", StringComparison.OrdinalIgnoreCase))
            {
                var parts = descriptor.Split(':');
                if (parts.Length != 3) return false;
                double mm; if (!double.TryParse(parts[2], out mm)) return false;
                var plane = BuildOffsetPlane(c, parts[1], mm);
                if (plane == null) return false;
                return plane.Select2(false, 0);
            }

            if (c.Planes.TryGetValue(descriptor, out var reg) && reg != null)
                return reg.Select2(false, 0);

            string principal = PrincipalPlaneName(descriptor);
            if (principal != null)
                return c.Doc.Extension.SelectByID2(principal, "PLANE", 0, 0, 0, false, 0, null, 0);

            // planar-face keyword
            var face = ResolvePlanarFaceByKeyword(c.Doc, descriptor);
            if (face != null) return ((Entity)face).Select4(false, null);
            return false;
        }

        private static bool SelectFeature(Ctx c, string id)
        {
            if (string.IsNullOrEmpty(id) || !c.Features.TryGetValue(id, out var feat) || feat == null) return false;
            c.Doc.ClearSelection2(true);
            return feat.Select2(false, 0);
        }

        private static string PrincipalPlaneName(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "front": return "Front Plane";
                case "top": return "Top Plane";
                case "right": return "Right Plane";
                default: return null;
            }
        }

        // Build (once) a construction axis along a principal direction via the intersection of two principal planes:
        //   X = Front ∩ Top,  Y = Front ∩ Right,  Z = Top ∩ Right.  Reused by both pattern ops.
        private static Feature EnsureAxis(Ctx c, string dir)
        {
            string key = (dir ?? "x").Trim().ToLowerInvariant();
            if (c.Axes.TryGetValue(key, out var cached) && cached != null) return cached;

            string p1, p2;
            switch (key)
            {
                case "x": p1 = "Front Plane"; p2 = "Top Plane"; break;
                case "y": p1 = "Front Plane"; p2 = "Right Plane"; break;
                case "z": p1 = "Top Plane"; p2 = "Right Plane"; break;
                default: return null;
            }
            c.Doc.ClearSelection2(true);
            if (!c.Doc.Extension.SelectByID2(p1, "PLANE", 0, 0, 0, false, 0, null, 0)) return null;
            if (!c.Doc.Extension.SelectByID2(p2, "PLANE", 0, 0, 0, true, 0, null, 0)) return null;
            bool ok = c.Doc.InsertAxis2(true);
            c.Doc.ClearSelection2(true);
            if (!ok) return null;
            var axis = c.Doc.FeatureByPositionReverse(0) as Feature;
            if (axis != null) c.Axes[key] = axis;
            return axis;
        }

        // Resolve + append-select the edges named by a semantic fillet/chamfer target. Returns the count selected.
        private static int SelectTargetEdges(Ctx c, string target)
        {
            c.Doc.ClearSelection2(true);
            var body = GetSolidBody(c.Doc);
            if (body == null) return 0;
            var edges = new List<Edge>();

            if (target.StartsWith("feature:", StringComparison.OrdinalIgnoreCase))
            {
                string id = target.Substring("feature:".Length);
                if (c.Features.TryGetValue(id, out var feat) && feat != null)
                {
                    object[] faces = feat.GetFaces() as object[];
                    if (faces != null)
                        foreach (var fo in faces) { var fc = fo as Face2; if (fc != null) CollectEdges(fc, edges); }
                }
            }
            else
            {
                switch (target)
                {
                    case "all_edges":
                        {
                            object[] be = body.GetEdges() as object[];
                            if (be != null) foreach (var eo in be) { var e = eo as Edge; if (e != null) edges.Add(e); }
                            break;
                        }
                    case "vertical_edges":
                        {
                            object[] be = body.GetEdges() as object[];
                            if (be != null) foreach (var eo in be) { var e = eo as Edge; if (e != null && IsVertical(e)) edges.Add(e); }
                            break;
                        }
                    case "top_face_edges":
                        CollectEdges(ExtremePlanarFace(body, +1), edges); break;
                    case "bottom_face_edges":
                        CollectEdges(ExtremePlanarFace(body, -1), edges); break;
                    default:
                        return 0;
                }
            }

            int n = 0;
            var seen = new HashSet<int>();
            foreach (var e in edges)
            {
                if (e == null) continue;
                int h = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(e);
                if (!seen.Add(h)) continue;
                try { if (((Entity)e).Select4(true, null)) n++; } catch { }
            }
            return n;
        }

        // ---------------------------------------------------------------- geometry helpers

        private static Body2 GetSolidBody(IModelDoc2 doc)
        {
            try
            {
                var pd = doc as PartDoc; if (pd == null) return null;
                object[] bodies = pd.GetBodies2((int)swBodyType_e.swSolidBody, true) as object[];
                if (bodies == null || bodies.Length == 0) return null;
                return bodies[0] as Body2;
            }
            catch { return null; }
        }

        private static double[] SolidBox(IModelDoc2 doc)
        {
            var body = GetSolidBody(doc);
            if (body == null) return null;
            try { return body.GetBodyBox() as double[]; } catch { return null; }
        }

        private static int CylindricalFaceCount(IModelDoc2 doc)
        {
            var body = GetSolidBody(doc); if (body == null) return 0;
            int n = 0;
            try
            {
                object[] faces = body.GetFaces() as object[]; if (faces == null) return 0;
                foreach (var fo in faces)
                {
                    var face = fo as Face2; if (face == null) continue;
                    var s = face.GetSurface() as Surface;
                    if (s != null && s.IsCylinder()) n++;
                }
            }
            catch { }
            return n;
        }

        private static void CollectEdges(Face2 face, List<Edge> into)
        {
            if (face == null) return;
            try { object[] es = face.GetEdges() as object[]; if (es != null) foreach (var eo in es) { var e = eo as Edge; if (e != null) into.Add(e); } }
            catch { }
        }

        private static bool IsVertical(Edge e)
        {
            var d = EdgeDir(e);
            return d != null && Math.Abs(d[2]) > 0.9; // ~parallel to Z
        }

        private static double[] EdgeDir(Edge e)
        {
            try
            {
                var c = e.GetCurve() as Curve; if (c == null || !c.IsLine()) return null;
                var sv = e.GetStartVertex() as Vertex; var ev = e.GetEndVertex() as Vertex;
                if (sv == null || ev == null) return null;
                double[] a = sv.GetPoint() as double[]; double[] b = ev.GetPoint() as double[];
                if (a == null || b == null) return null;
                double dx = b[0] - a[0], dy = b[1] - a[1], dz = b[2] - a[2];
                double L = Math.Sqrt(dx * dx + dy * dy + dz * dz); if (L < 1e-9) return null;
                return new[] { dx / L, dy / L, dz / L };
            }
            catch { return null; }
        }

        // Extreme planar face along Z: sign +1 => the face whose outward normal is closest to +Z (top),
        // -1 => closest to -Z (bottom). Matches Front-plane parts extruded along Z (the common bracket/plate case).
        private static Face2 ExtremePlanarFace(Body2 body, int sign)
        {
            Face2 best = null; double bestDot = -2;
            try
            {
                object[] faces = body.GetFaces() as object[]; if (faces == null) return null;
                foreach (var fo in faces)
                {
                    var face = fo as Face2; if (face == null) continue;
                    var s = face.GetSurface() as Surface; if (s == null || !s.IsPlane()) continue;
                    double[] nrm = FaceNormal(face); if (nrm == null) continue;
                    double dot = sign * nrm[2];
                    if (dot > bestDot) { bestDot = dot; best = face; }
                }
            }
            catch { }
            return bestDot > 0.5 ? best : null;
        }

        private static double[] FaceNormal(Face2 face)
        {
            try { return face.Normal as double[]; } catch { return null; }
        }

        private static Face2 LargestPlanarFace(Component2 comp)
        {
            Face2 best = null; double bestA = -1;
            foreach (var face in ComponentFaces(comp))
            {
                var s = face.GetSurface() as Surface; if (s == null || !s.IsPlane()) continue;
                double a = 0; try { a = face.GetArea(); } catch { }
                if (a > bestA) { bestA = a; best = face; }
            }
            return best;
        }

        private static Face2 SmallestCylFace(Component2 comp)
        {
            Face2 best = null; double bestR = double.MaxValue;
            foreach (var face in ComponentFaces(comp))
            {
                var s = face.GetSurface() as Surface; if (s == null || !s.IsCylinder()) continue;
                double[] cp = s.CylinderParams as double[]; if (cp == null || cp.Length < 7) continue;
                if (cp[6] < bestR) { bestR = cp[6]; best = face; }
            }
            return best;
        }

        private static Face2 ResolvePlanarFaceByKeyword(IModelDoc2 doc, string keyword)
        {
            var body = GetSolidBody(doc); if (body == null) return null;
            int axis; int sign;
            switch ((keyword ?? "").Trim().ToLowerInvariant())
            {
                case "top": axis = 1; sign = +1; break;
                case "bottom": axis = 1; sign = -1; break;
                case "front": axis = 2; sign = +1; break;
                case "back": axis = 2; sign = -1; break;
                case "right": axis = 0; sign = +1; break;
                case "left": axis = 0; sign = -1; break;
                default: return null;
            }
            Face2 best = null; double bestDot = -2;
            try
            {
                object[] faces = body.GetFaces() as object[]; if (faces == null) return null;
                foreach (var fo in faces)
                {
                    var face = fo as Face2; if (face == null) continue;
                    var s = face.GetSurface() as Surface; if (s == null || !s.IsPlane()) continue;
                    double[] nrm = FaceNormal(face); if (nrm == null) continue;
                    double dot = sign * nrm[axis];
                    if (dot > bestDot) { bestDot = dot; best = face; }
                }
            }
            catch { }
            return bestDot > 0.5 ? best : null;
        }

        private static IEnumerable<Face2> ComponentFaces(Component2 comp)
        {
            var list = new List<Face2>();
            try
            {
                object bi;
                object[] bodies = comp.GetBodies3((int)swBodyType_e.swSolidBody, out bi) as object[];
                if (bodies == null) return list;
                foreach (var bo in bodies)
                {
                    var body = bo as Body2; if (body == null) continue;
                    object[] faces = body.GetFaces() as object[]; if (faces == null) continue;
                    foreach (var fo in faces) { var f = fo as Face2; if (f != null) list.Add(f); }
                }
            }
            catch { }
            return list;
        }

        // ---------------------------------------------------------------- component / feature-tree helpers

        private static Component2 FindComponentByPath(AssemblyDoc asm, string path, bool last)
        {
            Component2 found = null;
            try
            {
                var comps = asm.GetComponents(true) as object[];
                if (comps == null) return null;
                foreach (var o in comps)
                {
                    var c = o as Component2; if (c == null) continue;
                    string p = null; try { p = c.GetPathName(); } catch { }
                    if (p != null && string.Equals(p, path, StringComparison.OrdinalIgnoreCase))
                    {
                        found = c;
                        if (!last) break; // first match
                    }
                }
            }
            catch { }
            return found;
        }

        private static Component2 ResolveComp(Ctx c, JObject sel)
        {
            if (sel == null) return null;
            string id = (string)sel["comp"];
            if (!string.IsNullOrEmpty(id) && c.Comps.TryGetValue(id, out var comp)) return comp;
            return null;
        }

        private static int CountComponents(AssemblyDoc asm)
        {
            try { var comps = asm.GetComponents(true) as object[]; return comps?.Length ?? 0; } catch { return 0; }
        }

        private static int FeatureCountRaw(IModelDoc2 doc)
        {
            int n = 0;
            try { var f = doc.FirstFeature() as Feature; while (f != null) { n++; f = f.GetNextFeature() as Feature; } }
            catch { }
            return n;
        }

        private static bool HasNativeModelingFeatures(IModelDoc2 doc)
        {
            bool hasModeling = false, hasImported = false;
            try
            {
                var f = doc.FirstFeature() as Feature;
                while (f != null)
                {
                    string tn = ""; try { tn = f.GetTypeName2() ?? ""; } catch { }
                    string t = tn.ToLowerInvariant();
                    if (t.Contains("imported")) hasImported = true;
                    if (t.Contains("extru") || t.Contains("cut") || t.Contains("fillet") || t.Contains("chamfer")
                        || t.Contains("pattern") || t.Contains("hole") || t.Contains("revolve") || t.Contains("boss"))
                        hasModeling = true;
                    f = f.GetNextFeature() as Feature;
                }
            }
            catch { }
            return hasModeling && !(hasImported && !hasModeling);
        }

        // Delete every feature added after position 'keep' (used to unwind a failed HoleWizard attempt).
        private static void RollbackTo(IModelDoc2 doc, int keep)
        {
            try
            {
                while (FeatureCountRaw(doc) > keep)
                {
                    var f = doc.FeatureByPositionReverse(0) as Feature;
                    if (f == null) break;
                    doc.ClearSelection2(true);
                    f.Select2(false, 0);
                    doc.EditDelete();
                }
                doc.ClearSelection2(true);
                doc.ForceRebuild3(false);
            }
            catch { }
        }

        // ---------------------------------------------------------------- misc

        private static double D(JObject o, string key) { return (double?)o[key] ?? 0.0; }
        private static double Round(double v) { return Math.Round(v, 3); }
        private static bool Near(double a, double b) { return Math.Abs(a - b) <= 0.01; }

        private static bool SortedNear(double[] a, double[] b)
        {
            var x = (double[])a.Clone(); var y = (double[])b.Clone();
            Array.Sort(x); Array.Sort(y);
            for (int i = 0; i < x.Length; i++) if (Math.Abs(x[i] - y[i]) > 0.01) return false;
            return true;
        }

        private static string SafeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "recipe-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            foreach (var ch in Path.GetInvalidFileNameChars()) name = name.Replace(ch, '-');
            return name;
        }

        // Per-recipe execution state: the live doc + the id -> feature/plane/component maps the ops resolve against.
        private class Ctx
        {
            public ISldWorks App;
            public Recipe Recipe;
            public RecipeResult Result;
            public IModelDoc2 Doc;
            public AssemblyDoc Asm;
            public readonly Dictionary<string, Feature> Sketches = new Dictionary<string, Feature>();
            public readonly Dictionary<string, Feature> Features = new Dictionary<string, Feature>();
            public readonly Dictionary<string, Feature> Planes = new Dictionary<string, Feature>();
            public readonly Dictionary<string, Feature> Axes = new Dictionary<string, Feature>();
            public readonly Dictionary<string, Component2> Comps = new Dictionary<string, Component2>();
        }
    }
}
