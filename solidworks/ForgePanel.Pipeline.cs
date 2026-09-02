using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// The SHARED handler pipeline. EVERY command flows through the same stages, so all handlers — and any
    /// handler added later — inherit them without being re-wired one by one:
    ///
    ///   parse (never trust the literal words)  ->  confirm-or-ask on ambiguity (never guess)  ->
    ///   preview before destructive writes  ->  execute  ->  verify (fail closed)  ->
    ///   log (telemetry + correction capture + thumbs + crash recovery), once.
    ///
    /// A handler plugs in as an HSpec + a thin Execute adapter over its EXISTING Run() — the handler's internals
    /// are untouched, so the green handlers can't regress. To add a future handler: add one HSpec entry and it
    /// gets every stage above for free. The old per-handler regex blocks in ForgePanel remain only as the
    /// OFFLINE fallback (used when the cloud parser is unreachable).
    /// </summary>
    public partial class ForgePanel
    {
        private class HOutcome
        {
            public bool Verified;        // fail closed: only true when the handler CONFIRMED the result (geometry / read-back / clean rebuild)
            public int Items;
            public string Info;
            public string Error;
            public bool AskedConfirm;    // handler-level ambiguity (e.g. unknown material/target) -> ask, don't execute
            public string Question;
            public JObject Card;         // custom panel card (e.g. "mated"); null => a plain answer card
        }

        private class HSpec
        {
            public string Name;                 // telemetry / handler id
            public string[] Actions;            // parsed actions this handler owns
            public bool AssemblyOnly = true;
            public bool Destructive;            // if broad-scope, gets a preview-then-confirm before running
            public Func<IModelDoc2, IntentPlan, IntentOperation, string, string> Preview;  // one-line plan (or null)
            public Func<IModelDoc2, IntentPlan, IntentOperation, string, Func<string, string, string, string, Task>, Task<HOutcome>> Execute;
        }

        private class Pending { public HSpec Spec; public IntentPlan Plan; public IntentOperation Op; public string Intent; public List<IntentOperation> Remaining; }
        private Pending _pendingConfirm;
        private List<HSpec> _specs;

        private ISldWorks App => SwAddin.SwApp;

        private List<HSpec> Specs()
        {
            if (_specs != null) return _specs;
            _specs = new List<HSpec>
            {
                // Mate — the hero "say it -> done". Reversible via Ctrl+Z, so it stays direct (no confirm gate).
                new HSpec {
                    Name = "mate", Actions = new[] { "mate" }, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var mr = await AutoMate.Run(App, doc, emit);
                        if (mr.Error != null) return new HOutcome { Error = mr.Error };
                        return new HOutcome {
                            Verified = mr.RebuildClean && mr.Proud == 0, Items = mr.Mated,
                            Card = new JObject { ["type"] = "mated", ["count"] = mr.Mated, ["seated"] = mr.Seated,
                                                 ["proud"] = mr.Proud, ["failed"] = mr.Failed, ["clean"] = mr.RebuildClean } };
                    }
                },
                // Material — resolves targets + library mapping and does its OWN confirm-or-ask (unknown material/target).
                new HSpec {
                    Name = "set_material", Actions = new[] { "set_material" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var mtr = await Materializer.RunIntent(App, doc, plan, emit);
                        if (mtr.NeedsConfirm) return new HOutcome { AskedConfirm = true, Question = mtr.Question };
                        if (mtr.Error != null) return new HOutcome { Error = mtr.Error };
                        return new HOutcome { Verified = true, Items = mtr.Applied, Info = mtr.Info };
                    }
                },
                // Mirror — reflect components. "Mirror everything EXCEPT hardware/motors/purchased" routes to
                // MirrorSkip (structure-only, with the "N of M excluding K" preview the WOW hinges on). Plain
                // "mirror everything" stays on the existing Mirror handler. Broad -> preview first (Rule #3).
                new HSpec {
                    Name = "mirror", Actions = new[] { "mirror" }, Destructive = true,
                    Preview = (doc, plan, op, intent) => {
                        if (MirrorSkip.IsSkipIntent(op, intent)) return MirrorSkip.PreviewLine(doc);   // "mirroring 112 of 150, excluding 38"
                        return IsBroad(op, doc)
                            ? "This will mirror every top-level component across the principal plane on a " + CompCount(doc) + "-component assembly"
                            : null;
                    },
                    Execute = async (doc, plan, op, intent, emit) => {
                        if (MirrorSkip.IsSkipIntent(op, intent)) {
                            var ms = await MirrorSkip.Run(App, doc, intent, emit);
                            if (ms.Error != null) return new HOutcome { Error = ms.Error };
                            // FAIL CLOSED (Rule #6): verified only when the mirror was created, every included part gained a
                            // reflected twin, at least one twin is geometrically confirmed, and the rebuild is clean.
                            return new HOutcome {
                                Verified = ms.AlreadyDone || (ms.Created && ms.Added == ms.Included && ms.Matched > 0 && ms.RebuildErrors == 0),
                                Items = ms.Included, Info = ms.Info };
                        }
                        var mir = await Mirror.Run(App, doc, intent, emit);
                        if (mir.Error != null) return new HOutcome { Error = mir.Error };
                        return new HOutcome { Verified = mir.Created || mir.AlreadyDone, Items = mir.Created ? 1 : 0,
                                              Info = mir.AlreadyDone ? "Already mirrored." : mir.Info };
                    }
                },
                // Explode — view-only spread, fully reversible; no confirm.
                new HSpec {
                    Name = "explode", Actions = new[] { "explode" }, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var xr = await Exploder.Run(App, doc, "explode the assembly", emit);
                        if (xr.Error != null) return new HOutcome { Error = xr.Error };
                        return new HOutcome { Verified = true, Items = xr.Moved, Info = xr.Info };
                    }
                },
                // Collapse — reverse the explode; view-only.
                new HSpec {
                    Name = "collapse", Actions = new[] { "collapse" }, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var xr = await Exploder.Run(App, doc, "put it back together", emit);
                        if (xr.Error != null) return new HOutcome { Error = xr.Error };
                        return new HOutcome { Verified = true, Items = xr.Moved, Info = xr.Info };
                    }
                },
                // Scan — read-only assembly doctor.
                new HSpec {
                    Name = "scan", Actions = new[] { "scan" }, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var scr = await Scout.Run(App, doc, emit);
                        if (scr.Error != null) return new HOutcome { Error = scr.Error };
                        return new HOutcome { Verified = true, Items = scr.Total, Info = scr.Info };
                    }
                },
                // Diagnose — read-only "assembly doctor": broken refs, dangling dims, missing materials, dup part
                // numbers, rebuild errors/hogs, circular refs. One report, verdict first. Never writes. Distinct
                // action from scan (quick inventory) — this is the deep audit.
                new HSpec {
                    Name = "diagnose", Actions = new[] { "diagnose" }, AssemblyOnly = true, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var dr = await Doctor.Run(App, doc, intent, emit);
                        if (dr.Error != null) return new HOutcome { Error = dr.Error };
                        return new HOutcome { Verified = true, Items = dr.Components, Info = dr.Info };
                    }
                },
                // Validate properties (tool #143) — read-only release-readiness check: missing materials, missing/duplicate
                // part numbers, no-computable-weight parts. One report, verdict first. Never writes. NARROWER than diagnose
                // (the broad doctor) — this is the BOM/release properties subset only.
                new HSpec {
                    Name = "validate_props", Actions = new[] { "validate_props" }, AssemblyOnly = true, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var vr = await ValidateProps.Run(App, doc, intent, emit);
                        if (vr.Error != null) return new HOutcome { Error = vr.Error };
                        return new HOutcome { Verified = true, Items = vr.UniqueParts, Info = vr.Info };
                    }
                },
                // AutoNumberParts ("assign part numbers to parts missing one" — tools #138/#139) — WRITE fix that pairs
                // with validate_props. Assigns a sequential part number (PN-0001..) to each unique part with NO part-
                // number property. A property write (PartNo custom property) — undoable, no geometry, Forge never saves —
                // but it modifies the model, so Destructive=true → preview before a broad run. PreviewLine returns null
                // for ≤3 parts so they execute directly.
                new HSpec {
                    Name = "auto_number_parts", Actions = new[] { "auto_number_parts" }, AssemblyOnly = true, Destructive = true,
                    Preview = (doc, plan, op, intent) => AutoNumberParts.PreviewLine(doc, intent),
                    Execute = async (doc, plan, op, intent, emit) => {
                        var ar = await AutoNumberParts.Run(App, doc, intent, emit);
                        if (ar.Error != null) return new HOutcome { Error = ar.Error };
                        // FAIL CLOSED (Rule #6): verified only when nothing failed AND either something was newly confirmed
                        // numbered, or everything was already numbered (idempotent no-op).
                        bool verified = ar.Failed == 0 && (ar.Assigned > 0 || (ar.UniqueParts > 0 && ar.MissingBefore == 0));
                        return new HOutcome { Verified = verified, Items = ar.Assigned, Info = ar.Info };
                    }
                },
                // FindDupes (tool #136) — read-only duplicate-component finder. Groups components by referenced part file
                // (reuse count) and flags any part-number carried by >1 distinct file (a possible duplicate-modeled part).
                // Never writes → no confirm gate.
                new HSpec {
                    Name = "find_duplicates", Actions = new[] { "find_duplicates" }, AssemblyOnly = true, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var fr = await FindDupes.Run(App, doc, intent, emit);
                        if (fr.Error != null) return new HOutcome { Error = fr.Error };
                        return new HOutcome { Verified = true, Items = fr.UniqueParts, Info = fr.Info };
                    }
                },
                // Interfere — read-only signal-vs-noise interference report. Runs SW's native detector, filters the
                // fastener thread/seat clearances out, reports only the REAL part-on-part clashes. Never writes.
                new HSpec {
                    Name = "interference", Actions = new[] { "interference" }, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var ir = await Interfere.Run(App, doc, intent, emit);
                        if (ir.Error != null) return new HOutcome { Error = ir.Error };
                        return new HOutcome { Verified = true, Items = ir.Real, Info = ir.Info };
                    }
                },
                // Change-impact — READ-ONLY dependency map ("what breaks if I change this?"). Part OR assembly.
                // Grounded in the live feature graph; never edits. Asks one question if the target is unresolvable.
                new HSpec {
                    Name = "change_impact", Actions = new[] { "change_impact" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var ir = await Impact.Run(App, doc, intent, emit);
                        if (ir.Question != null) return new HOutcome { AskedConfirm = true, Question = ir.Question };
                        if (ir.Error != null) return new HOutcome { Error = ir.Error };
                        // remember for a follow-up ("show me the pattern") so it resolves + shows, never re-asks (Demo 3)
                        _lastImpactTarget = ir.Target; _lastImpactPrimary = ir.PrimaryDependent; _lastImpactDependents = ir.Names;
                        return new HOutcome { Verified = ir.Available, Items = ir.Total, Info = ir.Info };
                    }
                },
                // Compare — READ-ONLY version diff. Part or assembly. Asks when the 2nd version can't be resolved.
                new HSpec {
                    Name = "compare_versions", Actions = new[] { "compare_versions" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var cr = await Compare.Run(App, doc, intent, _attachedFile, emit);
                        if (cr.NeedsConfirm) return new HOutcome { AskedConfirm = true, Question = cr.Question };
                        if (cr.Error != null) return new HOutcome { Error = cr.Error };
                        return new HOutcome { Verified = true, Items = cr.ChangedParts, Info = cr.Info };
                    }
                },
                // Profile — read-only rebuild-time profiler. Reports the slowest feature; suppress-test is reverted.
                new HSpec {
                    Name = "rebuild_profile", Actions = new[] { "rebuild_profile" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var pr = await Profiler.Run(App, doc, intent, emit);
                        if (pr.Error != null) return new HOutcome { Error = pr.Error };
                        return new HOutcome { Verified = pr.Reverted, Items = pr.FeatureCount, Info = pr.Info };
                    }
                },
                // Wall thickness — READ-ONLY sampled min-wall scan on a PART (FEA prep / injection molding / thin-wall
                // machining). Part-or-assembly at the pipeline (AssemblyOnly=false lets PARTs through); the handler refuses
                // an assembly honestly. Never writes → no confirm gate. Verified only when a real minimum was produced.
                new HSpec {
                    Name = "wall_thickness", Actions = new[] { "wall_thickness" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var wr = await WallThickness.Run(App, doc, intent, emit);
                        if (wr.Error != null) return new HOutcome { Error = wr.Error };
                        // FAIL CLOSED (Rule #6 / #4): "verified" only when a positive reading was actually measured; an
                        // unmeasurable part (no solid, freeform-only) returns Error above and never reports a fake number.
                        bool wOk = wr.CenterRequested ? wr.CenterThicknessMm > 0 : wr.MinThicknessMm > 0;
                        return new HOutcome { Verified = wOk, Items = wr.BelowCount, Info = wr.Info };
                    }
                },
                // Count through-holes — READ-ONLY through-vs-blind hole classification on a PART (test-loop
                // wrong-answer grinder-count-through-holes: no action in the cloud's vocabulary, fell to
                // list_features). Never writes → no confirm gate.
                new HSpec {
                    Name = "count_through_holes", Actions = new[] { "count_through_holes" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var thr = await CountThroughHoles.Run(App, doc, intent, emit);
                        if (thr.Error != null) return new HOutcome { Error = thr.Error };
                        return new HOutcome { Verified = thr.Verified, Items = thr.ThroughCount, Info = thr.Info };
                    }
                },
                // Mesh openings — READ-ONLY opening count across one row of a woven/welded wire mesh ASSEMBLY
                // (test-loop wrong-answer count-mesh-cells: no action in the cloud's vocabulary, fell to a generic
                // scan). Never writes → no confirm gate.
                new HSpec {
                    Name = "mesh_openings", Actions = new[] { "mesh_openings" }, AssemblyOnly = true, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var mor = await MeshOpenings.Run(App, doc, intent, emit);
                        if (mor.Error != null) return new HOutcome { Error = mor.Error };
                        return new HOutcome { Verified = mor.Verified, Items = mor.OpeningsPerRow, Info = mor.Info };
                    }
                },
                // Hole spacing — READ-ONLY center-to-center distance between same-size holes on a PART (test-loop
                // wrong-answer measure-mounting-hole-distance: no action in the cloud's vocabulary, fell to
                // get_bounding_box). Never writes → no confirm gate.
                new HSpec {
                    Name = "hole_spacing", Actions = new[] { "hole_spacing" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var hsr = await HoleSpacing.Run(App, doc, intent, emit);
                        if (hsr.Error != null) return new HOutcome { Error = hsr.Error };
                        return new HOutcome { Verified = hsr.Verified, Items = hsr.HoleCount, Info = hsr.Info };
                    }
                },
                // Arc height / camber — READ-ONLY sampled camber measurement on a PART (test-loop wrong-answer
                // measure-arc-height: no action in the cloud's vocabulary at all, fell to get_bounding_box). Never
                // writes → no confirm gate. Verified only when a real arc height was actually measured.
                new HSpec {
                    Name = "arc_height", Actions = new[] { "arc_height" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var ar = await ArcHeight.Run(App, doc, intent, emit);
                        if (ar.Error != null) return new HOutcome { Error = ar.Error };
                        return new HOutcome { Verified = ar.ArcHeightMm >= 0, Items = 0, Info = ar.Info };
                    }
                },
                // GetMassProps — READ: mass / volume / surface area / centre of mass of the active part or assembly. Never
                // writes → no confirm gate. Verified only when a solid produced a positive mass; a body-less model returns
                // Error and never reports a fake number.
                new HSpec {
                    Name = "get_mass_properties", Actions = new[] { "get_mass_properties" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var mr = await GetMassProps.Run(App, doc, intent, emit);
                        if (mr.Error != null) return new HOutcome { Error = mr.Error };
                        return new HOutcome { Verified = mr.Verified, Items = 0, Info = mr.Info };
                    }
                },
                // GetBoundingBox — READ: overall L×W×H + diagonal of the active part or assembly. Never writes.
                new HSpec {
                    Name = "get_bounding_box", Actions = new[] { "get_bounding_box" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var br = await GetBoundingBox.Run(App, doc, intent, emit);
                        if (br.Error != null) return new HOutcome { Error = br.Error };
                        return new HOutcome { Verified = br.Verified, Items = 0, Info = br.Info };
                    }
                },
                // CaptureViewport — READ/export: renders the current view to a PNG (the LLM's eyes). Never writes
                // to the model or saves it; the PNG is a Forge scratch temp file.
                new HSpec {
                    Name = "capture_viewport", Actions = new[] { "capture_viewport" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var cv = await CaptureViewport.Run(App, doc, intent, emit);
                        if (cv.Error != null) return new HOutcome { Error = cv.Error };
                        return new HOutcome { Verified = cv.Success, Items = 0, Info = cv.Info };
                    }
                },
                // CaptureSection — READ/export: screenshot with a live section cut, to verify internal geometry
                // (wall thickness, hole depths). Never saves; the cut stays visible so the caller can inspect it.
                new HSpec {
                    Name = "capture_section", Actions = new[] { "capture_section" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var cs = await CaptureSection.Run(App, doc, intent, emit);
                        if (cs.Error != null) return new HOutcome { Error = cs.Error };
                        return new HOutcome { Verified = cs.Success, Items = 0, Info = cs.Info };
                    }
                },
                // SelectFace — WRITE-of-state: selects a planar face by criteria (top/bottom/left/right/largest),
                // ready for a follow-up command. Never modifies geometry. Part-doc only.
                new HSpec {
                    Name = "select_face", Actions = new[] { "select_face" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var sf = await SelectFace.Run(App, doc, intent, emit);
                        if (sf.Error != null) return new HOutcome { Error = sf.Error };
                        return new HOutcome { Verified = sf.Success, Items = 0, Info = sf.Info };
                    }
                },
                // SelectComponent — WRITE-of-state: selects ONE assembly component by (near-)exact name,
                // ready for a follow-up command. Never modifies geometry. Assembly-doc only.
                new HSpec {
                    Name = "select_component", Actions = new[] { "select_component" }, AssemblyOnly = true, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var sc = await SelectComponent.Run(App, doc, intent, emit);
                        if (sc.NeedsConfirm) return new HOutcome { AskedConfirm = true, Question = sc.Question };
                        if (sc.Error != null) return new HOutcome { Error = sc.Error };
                        return new HOutcome { Verified = sc.Success, Items = 0, Info = sc.Info };
                    }
                },
                // SelectEdge — WRITE-of-state: selects one edge by criteria (longest/shortest, optionally
                // linear/circular), ready for a follow-up command. Never modifies geometry. Part-doc only.
                new HSpec {
                    Name = "select_edge", Actions = new[] { "select_edge" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var se = await SelectEdge.Run(App, doc, intent, emit);
                        if (se.Error != null) return new HOutcome { Error = se.Error };
                        return new HOutcome { Verified = se.Success, Items = 0, Info = se.Info };
                    }
                },
                // SelectPlane — WRITE-of-state: selects one reference plane (standard or custom named), ready
                // for a follow-up command. Never modifies geometry. Works on part or assembly docs.
                new HSpec {
                    Name = "select_plane", Actions = new[] { "select_plane" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var sp = await SelectPlane.Run(App, doc, intent, emit);
                        if (sp.NeedsConfirm) return new HOutcome { AskedConfirm = true, Question = sp.Question };
                        if (sp.Error != null) return new HOutcome { Error = sp.Error };
                        return new HOutcome { Verified = sp.Success, Items = 0, Info = sp.Info };
                    }
                },
                // GetSelectedEntities — READ: reports what is currently selected, honestly (including "nothing
                // selected"). Never writes/modifies geometry (an optional embedded "select the X face" sub-command
                // may select, same as select_face would).
                new HSpec {
                    Name = "get_selected_entities", Actions = new[] { "get_selected_entities" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var gse = await GetSelectedEntities.Run(App, doc, intent, emit);
                        if (gse.Error != null) return new HOutcome { Error = gse.Error };
                        return new HOutcome { Verified = gse.Success, Items = gse.Count, Info = gse.Info };
                    }
                },
                // ClearSelection — WRITE-of-state: clears whatever is currently selected. Never modifies geometry.
                new HSpec {
                    Name = "clear_selection", Actions = new[] { "clear_selection" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var cs = await ClearSelection.Run(App, doc, intent, emit);
                        if (cs.Error != null) return new HOutcome { Error = cs.Error };
                        return new HOutcome { Verified = cs.Success, Items = 0, Info = cs.Info };
                    }
                },
                // MeasureBoltCircle — READ: bolt-circle diameter (PCD), hole count, hole diameter of a flange's bolt
                // pattern. Never writes. No ASME B16.5 lookup table — reports measured geometry, not a class verdict.
                new HSpec {
                    Name = "measure_bolt_circle", Actions = new[] { "measure_bolt_circle" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var bc = await MeasureBoltCircle.Run(App, doc, intent, emit);
                        if (bc.Error != null) return new HOutcome { Error = bc.Error };
                        return new HOutcome { Verified = bc.Verified, Items = bc.HoleCount, Info = bc.Info };
                    }
                },
                // CountNamedComponents — READ: named-part-type count question ("how many servos", "count the
                // rollers") the cloud has no specific action for. Never writes.
                new HSpec {
                    Name = "count_named_components", Actions = new[] { "count_named_components" }, AssemblyOnly = true, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var cn = await CountNamedComponents.Run(App, doc, intent, emit);
                        if (cn.Error != null) return new HOutcome { Error = cn.Error };
                        return new HOutcome { Verified = cn.Verified, Items = cn.Count, Info = cn.Info };
                    }
                },
                // CountGearTeeth — READ: "how many teeth on this gear", "tooth count on both bevel gears" — no
                // handler counted gear teeth at all (missing-capability gap). Geometry-only (works on dumb/imported
                // solids); never writes.
                new HSpec {
                    Name = "count_gear_teeth", Actions = new[] { "count_gear_teeth" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var gt = await CountGearTeeth.Run(App, doc, intent, emit);
                        if (gt.Error != null) return new HOutcome { Error = gt.Error };
                        return new HOutcome { Verified = gt.Verified, Items = gt.Counts.Count, Info = gt.Info };
                    }
                },
                // MeasureFaceGap — READ (tool 28, check_clearance): closest anti-parallel planar-face gap between two
                // non-fastener components — "how far apart are the mating faces". Never writes.
                new HSpec {
                    Name = "check_clearance", Actions = new[] { "check_clearance" }, AssemblyOnly = true, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var fg = await MeasureFaceGap.Run(App, doc, intent, emit);
                        if (fg.Error != null) return new HOutcome { Error = fg.Error };
                        return new HOutcome { Verified = fg.Verified, Items = 0, Info = fg.Info };
                    }
                },
                // GetConfigs — READ: list the configurations of the active part or assembly. Never writes.
                new HSpec {
                    Name = "list_configurations", Actions = new[] { "list_configurations" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var cr = await GetConfigs.Run(App, doc, intent, emit);
                        if (cr.Error != null) return new HOutcome { Error = cr.Error };
                        return new HOutcome { Verified = cr.Verified, Items = cr.Count, Info = cr.Info };
                    }
                },
                // GetFeatureTree — READ: summarize the active model's feature tree (count + by-type breakdown). Never writes.
                new HSpec {
                    Name = "list_features", Actions = new[] { "list_features" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var fr = await GetFeatureTree.Run(App, doc, intent, emit);
                        if (fr.Error != null) return new HOutcome { Error = fr.Error };
                        return new HOutcome { Verified = fr.Verified, Items = fr.TotalFeatures, Info = fr.Info };
                    }
                },
                // ListEquations — READ: report every equation / global variable on a PART. Never writes.
                new HSpec {
                    Name = "list_equations", Actions = new[] { "list_equations" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var lr = await ListEquations.Run(App, doc, intent, emit);
                        if (lr.Error != null) return new HOutcome { Error = lr.Error };
                        return new HOutcome { Verified = true, Items = lr.Count, Info = lr.Info };
                    }
                },
                // SuppressFeature — WRITE: suppress/unsuppress USER-NAMED features (by type or name) in a PART's ACTIVE
                // config (FEA/print prep). Destructive → preview. Verified only when every target reads back IsSuppressed()
                // in the requested state and the rebuild is clean; a suppress that breaks a dependent rebuild is reverted.
                new HSpec {
                    Name = "suppress_feature", Actions = new[] { "suppress_feature", "unsuppress_feature" }, AssemblyOnly = false, Destructive = true,
                    Preview = (doc, plan, op, intent) => SuppressFeature.PreviewLine(doc, intent),
                    Execute = async (doc, plan, op, intent, emit) => {
                        var sr = await SuppressFeature.Run(App, doc, intent, emit);
                        if (sr.NeedsConfirm) return new HOutcome { AskedConfirm = true, Question = sr.Question };
                        if (sr.Error != null) return new HOutcome { Error = sr.Error };
                        return new HOutcome { Verified = sr.Verified, Items = sr.Changed, Info = sr.Info };
                    }
                },
                // AddHole — WRITE: drill a through-hole at the centre of the largest/top flat face of a PART. Destructive
                // (adds geometry) → preview. Verified only when volume dropped, a bore face appeared, and rebuild is clean.
                new HSpec {
                    Name = "add_hole", Actions = new[] { "add_hole" }, AssemblyOnly = false, Destructive = true,
                    Preview = (doc, plan, op, intent) => {
                        bool part = (int)doc.GetType() == (int)swDocumentTypes_e.swDocPART;
                        return part ? "This will drill a through-hole at the centre of the largest (or top) flat face (adds a Forge-Hole feature; one Ctrl+Z removes it, Forge won't save)" : null;
                    },
                    Execute = async (doc, plan, op, intent, emit) => {
                        var hr = await AddHole.Run(App, doc, intent, emit);
                        if (hr.Error != null) return new HOutcome { Error = hr.Error };
                        return new HOutcome { Verified = hr.Verified, Items = hr.AlreadyDone ? 0 : 1, Info = hr.Info };
                    }
                },
                // AddBoltCircle — WRITE: drill N holes equally spaced on a circle of a PART ("5 bolt holes on a 4.5
                // inch circle"). Destructive → preview. Verified only when volume dropped, N new bore faces appeared,
                // and rebuild is clean; a placement that doesn't fit any face is an honest Error, never a guess.
                new HSpec {
                    Name = "add_bolt_circle", Actions = new[] { "add_bolt_circle" }, AssemblyOnly = false, Destructive = true,
                    Preview = (doc, plan, op, intent) => {
                        bool part = (int)doc.GetType() == (int)swDocumentTypes_e.swDocPART;
                        return part ? "This will drill several through-holes equally spaced on a bolt circle, centred on the largest (or top) flat face (adds a Forge-BoltCircle feature; one Ctrl+Z removes it, Forge won't save)" : null;
                    },
                    Execute = async (doc, plan, op, intent, emit) => {
                        var br = await AddBoltCircle.Run(App, doc, intent, emit);
                        if (br.Error != null) return new HOutcome { Error = br.Error };
                        return new HOutcome { Verified = br.Verified, Items = br.AlreadyDone ? 0 : br.Count, Info = br.Info };
                    }
                },
                // AddBoss — WRITE: add a round boss/pad (positive extrude, ADDS material) at the centre of the largest/top
                // face of a PART. Destructive → preview. Verified only when volume ROSE by ~the boss volume, a new boss-wall
                // face appeared, and rebuild is clean; an extrude that adds no material (wrong side) is rolled back.
                new HSpec {
                    Name = "add_boss", Actions = new[] { "add_boss" }, AssemblyOnly = false, Destructive = true,
                    Preview = (doc, plan, op, intent) => {
                        bool part = (int)doc.GetType() == (int)swDocumentTypes_e.swDocPART;
                        return part ? "This will add a round boss at the centre of the largest (or top) flat face (adds a Forge-Boss feature; one Ctrl+Z removes it, Forge won't save)" : null;
                    },
                    Execute = async (doc, plan, op, intent, emit) => {
                        var br = await AddBoss.Run(App, doc, intent, emit);
                        if (br.Error != null) return new HOutcome { Error = br.Error };
                        return new HOutcome { Verified = br.Verified, Items = br.AlreadyDone ? 0 : 1, Info = br.Info };
                    }
                },
                // CreateWrap — WRITE: emboss/deboss a sketch circle onto the largest planar face of a PART via
                // IFeatureManager.InsertWrapFeature2 (tool 211). Destructive -> preview. Verified only when volume
                // changed the expected way (up for emboss, down for deboss) by ~circle-area*thickness, a new
                // cylindrical wrap-wall face appeared, and rebuild is clean; a wrap that didn't take is rolled back.
                new HSpec {
                    Name = "create_wrap", Actions = new[] { "create_wrap" }, AssemblyOnly = false, Destructive = true,
                    Preview = (doc, plan, op, intent) => {
                        bool part = (int)doc.GetType() == (int)swDocumentTypes_e.swDocPART;
                        return part ? "This will emboss/deboss a circle onto the largest flat face (adds a Forge-Wrap feature; one Ctrl+Z removes it, Forge won't save)" : null;
                    },
                    Execute = async (doc, plan, op, intent, emit) => {
                        var wr = await CreateWrap.Run(App, doc, intent, emit);
                        if (wr.Error != null) return new HOutcome { Error = wr.Error };
                        return new HOutcome { Verified = wr.Verified, Items = wr.AlreadyDone ? 0 : 1, Info = wr.Info };
                    }
                },
                // AddPocket — WRITE: mill a rectangular pocket/slot (negative extrude, REMOVES material) at the centre of the
                // largest/top face of a PART. Destructive → preview. Verified only when volume DROPPED by ~w·l·depth, new
                // planar recess faces appeared, and rebuild is clean; a cut that removes nothing (wrong side) flips once then
                // is rolled back honestly.
                new HSpec {
                    Name = "add_pocket", Actions = new[] { "add_pocket" }, AssemblyOnly = false, Destructive = true,
                    Preview = (doc, plan, op, intent) => {
                        bool part = (int)doc.GetType() == (int)swDocumentTypes_e.swDocPART;
                        return part ? "This will mill a rectangular pocket at the centre of the largest (or top) flat face (adds a Forge-Pocket feature; one Ctrl+Z removes it, Forge won't save)" : null;
                    },
                    Execute = async (doc, plan, op, intent, emit) => {
                        var pr = await AddPocket.Run(App, doc, intent, emit);
                        if (pr.Error != null) return new HOutcome { Error = pr.Error };
                        return new HOutcome { Verified = pr.Verified, Items = pr.AlreadyDone ? 0 : 1, Info = pr.Info };
                    }
                },
                // PatternFeature — WRITE: replicate a seed feature into a linear/circular array on a PART. Destructive →
                // preview. Verified only when the pattern feature appeared, the rebuild is clean, and geometry actually
                // changed (bore faces up for a hole seed, or a volume change); a first linear direction that misses is
                // flipped once, and a pattern that changes nothing / breaks the rebuild is rolled back honestly.
                new HSpec {
                    Name = "pattern_feature", Actions = new[] { "pattern_feature" }, AssemblyOnly = false, Destructive = true,
                    Preview = (doc, plan, op, intent) => {
                        bool part = (int)doc.GetType() == (int)swDocumentTypes_e.swDocPART;
                        return part ? "This will replicate the most-recent feature (hole/cut/boss) into an array (adds a Forge-Pattern feature; one Ctrl+Z removes it, Forge won't save)" : null;
                    },
                    Execute = async (doc, plan, op, intent, emit) => {
                        var pr = await PatternFeature.Run(App, doc, intent, emit);
                        if (pr.Error != null) return new HOutcome { Error = pr.Error };
                        return new HOutcome { Verified = pr.Verified, Items = pr.AlreadyDone ? 0 : 1, Info = pr.Info };
                    }
                },
                // MirrorFeature — WRITE: reflect a seed feature across a standard plane on a PART (symmetric twin).
                // Destructive → preview. Verified only when the mirror feature appeared, the rebuild is clean, and
                // geometry changed (a new bore for a hole seed, or a volume change); tries both InsertMirrorFeature2 mark
                // schemes, and a mirror that changes nothing / breaks the rebuild is rolled back honestly.
                new HSpec {
                    Name = "mirror_feature", Actions = new[] { "mirror_feature" }, AssemblyOnly = false, Destructive = true,
                    Preview = (doc, plan, op, intent) => {
                        bool part = (int)doc.GetType() == (int)swDocumentTypes_e.swDocPART;
                        return part ? "This will mirror the most-recent feature (hole/cut/boss) across a standard plane (adds a Forge-MirrorFeat feature; one Ctrl+Z removes it, Forge won't save)" : null;
                    },
                    Execute = async (doc, plan, op, intent, emit) => {
                        var mr = await MirrorFeature.Run(App, doc, intent, emit);
                        if (mr.Error != null) return new HOutcome { Error = mr.Error };
                        return new HOutcome { Verified = mr.Verified, Items = mr.AlreadyDone ? 0 : 1, Info = mr.Info };
                    }
                },
                // AddCounterbore — WRITE: mill a stepped counterbore (head recess + through clearance) on a PART.
                // Destructive → preview. Verified only when volume dropped, two coaxial bores of different radii appeared,
                // and rebuild is clean; composes the proven AddHole/AddPocket cut machinery, rolls back on any failure.
                new HSpec {
                    Name = "add_counterbore", Actions = new[] { "add_counterbore" }, AssemblyOnly = false, Destructive = true,
                    Preview = (doc, plan, op, intent) => {
                        bool part = (int)doc.GetType() == (int)swDocumentTypes_e.swDocPART;
                        return part ? "This will mill a counterbored hole (a wide recess over a through clearance hole) at the centre of the largest (or top) flat face (adds Forge-Counterbore features; Ctrl+Z per cut, Forge won't save)" : null;
                    },
                    Execute = async (doc, plan, op, intent, emit) => {
                        var cr = await AddCounterbore.Run(App, doc, intent, emit);
                        if (cr.Error != null) return new HOutcome { Error = cr.Error };
                        return new HOutcome { Verified = cr.Verified, Items = cr.AlreadyDone ? 0 : 1, Info = cr.Info };
                    }
                },
                // AddCountersink — WRITE: machine a conical countersink (flat-head recess + through clearance) on a PART.
                // Destructive → preview. Verified only when a new conical face appeared, the volume dropped, and the
                // rebuild is clean; composes the proven AddHole through-cut + FilletChamfer chamfer, rolls back on failure.
                new HSpec {
                    Name = "add_countersink", Actions = new[] { "add_countersink" }, AssemblyOnly = false, Destructive = true,
                    Preview = (doc, plan, op, intent) => {
                        bool part = (int)doc.GetType() == (int)swDocumentTypes_e.swDocPART;
                        return part ? "This will machine a countersunk hole (a conical recess over a through clearance hole) at the centre of the largest (or top) flat face (adds Forge-Countersink features; Ctrl+Z per step, Forge won't save)" : null;
                    },
                    Execute = async (doc, plan, op, intent, emit) => {
                        var cr = await AddCountersink.Run(App, doc, intent, emit);
                        if (cr.Error != null) return new HOutcome { Error = cr.Error };
                        return new HOutcome { Verified = cr.Verified, Items = cr.AlreadyDone ? 0 : 1, Info = cr.Info };
                    }
                },
                // DeleteEquation — parametric WRITE: remove an equation/global on a PART. Reversible. Verified only when
                // the count dropped by 1, the name is gone, and the rebuild is clean (a delete that orphans a driven dim
                // is reported, not silently passed). Asks when the target is ambiguous.
                new HSpec {
                    Name = "delete_equation", Actions = new[] { "delete_equation" }, AssemblyOnly = false, Destructive = false,
                    Preview = (doc, plan, op, intent) => null,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var dr = await DeleteEquation.Run(App, doc, intent, emit);
                        if (dr.NeedsConfirm) return new HOutcome { AskedConfirm = true, Question = dr.Question };
                        if (dr.Error != null) return new HOutcome { Error = dr.Error };
                        return new HOutcome { Verified = dr.Verified, Items = dr.NotFound ? 0 : 1, Info = dr.Info };
                    }
                },
                // AddEquation — parametric WRITE: create a global variable on a PART. Reversible (one Ctrl+Z). Verified
                // only when the equation count rose by 1, the new global reads the value, and the rebuild is clean.
                new HSpec {
                    Name = "add_equation", Actions = new[] { "add_equation" }, AssemblyOnly = false, Destructive = false,
                    Preview = (doc, plan, op, intent) => null,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var ar = await AddEquation.Run(App, doc, intent, emit);
                        if (ar.Error != null) return new HOutcome { Error = ar.Error };
                        return new HOutcome { Verified = ar.Verified, Items = ar.AlreadyExists ? 0 : 1, Info = ar.Info };
                    }
                },
                // EditEquation — parametric WRITE: change an equation / global-variable value on a PART. Destructive
                // (it drives geometry via linked dimensions), but reversible (one Ctrl+Z). Verified only when the named
                // equation re-reads at the new value and the rebuild is clean; asks when the target equation is ambiguous.
                new HSpec {
                    Name = "edit_equation", Actions = new[] { "edit_equation" }, AssemblyOnly = false, Destructive = false,
                    Preview = (doc, plan, op, intent) => null,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var er = await EditEquation.Run(App, doc, intent, emit);
                        if (er.NeedsConfirm) return new HOutcome { AskedConfirm = true, Question = er.Question };
                        if (er.Error != null) return new HOutcome { Error = er.Error };
                        return new HOutcome { Verified = er.Verified, Items = er.AlreadyDone ? 0 : 1, Info = er.Info };
                    }
                },
                // RenameFeature — metadata WRITE: give a feature a meaningful name (no geometry change). Not destructive
                // (renaming can't corrupt geometry), so no preview. Verified only when a feature named NewName exists, the
                // old name is gone, the tree count is unchanged, and the rebuild is clean.
                new HSpec {
                    Name = "rename_feature", Actions = new[] { "rename_feature" }, AssemblyOnly = false, Destructive = false,
                    Preview = (doc, plan, op, intent) => null,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var rr = await RenameFeature.Run(App, doc, intent, emit);
                        if (rr.Error != null) return new HOutcome { Error = rr.Error };
                        if (rr.NeedsConfirm) return new HOutcome { AskedConfirm = true, Question = rr.Question };
                        return new HOutcome { Verified = rr.Verified, Items = rr.AlreadyDone ? 0 : 1, Info = rr.Info };
                    }
                },
                // DeleteFeature — WRITE: permanently delete features (by type/name) from a PART's tree — the permanent
                // counterpart to suppress_feature. Uses DeleteSelection2 (no confirmation dialog; headless-safe) and never
                // deletes the base body / reference geometry. Verified only when the matched features are GONE, a solid
                // survives, and the rebuild is clean; a delete that breaks a dependent rebuild is reported honestly.
                new HSpec {
                    Name = "delete_feature", Actions = new[] { "delete_feature" }, AssemblyOnly = false, Destructive = true,
                    Preview = (doc, plan, op, intent) => DeleteFeature.PreviewLine(doc, intent),
                    Execute = async (doc, plan, op, intent, emit) => {
                        var dr = await DeleteFeature.Run(App, doc, intent, emit);
                        if (dr.NeedsConfirm) return new HOutcome { AskedConfirm = true, Question = dr.Question };
                        if (dr.Error != null) return new HOutcome { Error = dr.Error };
                        return new HOutcome { Verified = dr.Verified, Items = dr.Deleted, Info = dr.Info };
                    }
                },
                // CreateRefPlane — WRITE: insert a reference plane offset from a standard plane (reference geometry).
                // Part-or-assembly. Destructive → preview. Verified only when a new RefPlane feature exists + rebuild clean.
                new HSpec {
                    Name = "create_reference_plane", Actions = new[] { "create_reference_plane" }, AssemblyOnly = false, Destructive = true,
                    Preview = (doc, plan, op, intent) => "This will add an offset reference plane (adds a Forge-Plane feature; one Ctrl+Z removes it, Forge won't save)",
                    Execute = async (doc, plan, op, intent, emit) => {
                        var pr = await CreateRefPlane.Run(App, doc, intent, emit);
                        if (pr.Error != null) return new HOutcome { Error = pr.Error };
                        return new HOutcome { Verified = pr.Verified, Items = pr.AlreadyDone ? 0 : 1, Info = pr.Info };
                    }
                },
                // SetFixed — WRITE: fix (lock) or float (free) components in an assembly. A state change (no geometry);
                // Destructive=true so a broad "fix everything" previews first. Verified only when every target independently
                // reads back Component2.IsFixed in the requested state.
                new HSpec {
                    Name = "set_fixed", Actions = new[] { "set_fixed", "fix_component", "float_component" }, AssemblyOnly = true, Destructive = true,
                    Preview = (doc, plan, op, intent) => {
                        string i = (intent ?? "").ToLowerInvariant();
                        bool fl = System.Text.RegularExpressions.Regex.IsMatch(i, @"\b(float|unfix|un-fix|free|release)\b");
                        return "This will " + (fl ? "float (free)" : "fix (lock)") + " the targeted components — one Ctrl+Z undoes it, Forge won't save";
                    },
                    Execute = async (doc, plan, op, intent, emit) => {
                        var sr = await SetFixed.Run(App, doc, intent, emit);
                        if (sr.NeedsConfirm) return new HOutcome { AskedConfirm = true, Question = sr.Question };
                        if (sr.Error != null) return new HOutcome { Error = sr.Error };
                        return new HOutcome { Verified = sr.Verified, Items = sr.Changed, Info = sr.Info };
                    }
                },
                // Fix red wave — find the root-cause over-defining / dangling mate and remove ONLY it. Destructive
                // (deletes a mate) so it previews first; the delete itself is one-Ctrl+Z undoable and Forge never saves.
                new HSpec {
                    Name = "fix_red_wave", Actions = new[] { "fix_red_wave" }, Destructive = true,
                    Preview = (doc, plan, op, intent) => {
                        int wrong = 0; try { wrong = doc.Extension.GetWhatsWrongCount(); } catch { }
                        return wrong > 0
                            ? "This assembly has " + wrong + " mate-error flag(s); Forge will find the root-cause mate(s) and remove ONLY those, leaving your other mates intact"
                            : "No mate errors detected — Forge will re-check and, if it's clean, change nothing";
                    },
                    Execute = async (doc, plan, op, intent, emit) => {
                        var rw = await RedWave.Run(App, doc, intent, emit);
                        if (rw.Error != null) return new HOutcome { Error = rw.Error };
                        return new HOutcome {
                            Verified = rw.RebuildClean, Items = rw.Removed,
                            Card = new JObject { ["type"] = "red_wave", ["removed"] = rw.Removed, ["cleared"] = rw.Cleared,
                                                 ["errorsBefore"] = rw.ErrorsBefore, ["errorsAfter"] = rw.ErrorsAfter,
                                                 ["overBefore"] = rw.OverDefinedBefore, ["overAfter"] = rw.OverDefinedAfter,
                                                 ["mate"] = rw.RemovedMateType, ["clean"] = rw.RebuildClean, ["info"] = rw.Info } };
                    }
                },
                // Isolate — visibility only, reversible ("show all").
                new HSpec {
                    Name = "isolate", Actions = new[] { "isolate" }, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var ir = await Isolator.Run(App, doc, intent, emit);
                        if (ir.Error != null) return new HOutcome { Error = ir.Error };
                        return new HOutcome { Verified = ir.Error == null, Items = ir.Hidden, Info = ir.Info };
                    }
                },
                // SuppressComponents ("strip an assembly" / "suppress the fasteners") — WRITE handler that suppresses a resolved
                // set of components. Suppression is a reversible STATE change (one Ctrl+Z per part; Forge never saves), but it
                // modifies the model, so Destructive=true → preview before a broad strip. PreviewLine returns null for small/
                // ambiguous sets so they execute directly and let the handler ask.
                new HSpec {
                    Name = "suppress_components", Actions = new[] { "suppress_components" }, AssemblyOnly = true, Destructive = true,
                    Preview = (doc, plan, op, intent) => SuppressComponents.PreviewLine(doc, intent),
                    Execute = async (doc, plan, op, intent, emit) => {
                        var sr = await SuppressComponents.Run(App, doc, intent, emit);
                        if (sr.Error != null) return new HOutcome { Error = sr.Error };
                        // FAIL CLOSED (Rule #6): verified only when nothing failed AND either something was newly confirmed
                        // suppressed, or the whole matched set was already suppressed (idempotent no-op).
                        bool verified = sr.Failed == 0 && (sr.Suppressed > 0 || (sr.Matched > 0 && sr.AlreadySuppressed == sr.Matched));
                        return new HOutcome { Verified = verified, Items = sr.Suppressed, Info = sr.Info };
                    }
                },
                // UnsuppressComponents — the completeness pair of suppress_components: re-activate suppressed components.
                // Reversible state write; Destructive=false (bringing parts BACK can't corrupt geometry, and it has no
                // ambiguous-destruction risk). Verified only when nothing failed and either something was confirmed active
                // or there was nothing suppressed to restore (idempotent no-op).
                new HSpec {
                    Name = "unsuppress_components", Actions = new[] { "unsuppress_components" }, AssemblyOnly = true, Destructive = false,
                    Preview = (doc, plan, op, intent) => null,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var ur = await UnsuppressComponents.Run(App, doc, intent, emit);
                        if (ur.Error != null) return new HOutcome { Error = ur.Error };
                        bool verified = ur.Failed == 0 && (ur.Unsuppressed > 0 || ur.NothingToDo);
                        return new HOutcome { Verified = verified, Items = ur.Unsuppressed, Info = ur.Info };
                    }
                },
                // DeleteComponent (tool 30, WRITE) — live-dispatch gap, same class as rename_component/get_faces
                // above: harness-GREEN (Harness.cs IntentDispatch) but never registered as an HSpec here, so every
                // "delete the <component>" phrasing fell through to the legacy ForgeApi.Act() cloud path, which asks
                // instead of acting (test-loop hedged finding "delete-tamper": "delete the tamper component" got zero
                // attempt). Genuinely destructive (removes parts + their mates) → keeps the preview/"go" gate.
                new HSpec {
                    Name = "delete_component", Actions = new[] { "delete_component" }, AssemblyOnly = true, Destructive = true,
                    Preview = (doc, plan, op, intent) => DeleteComponent.PreviewLine(doc, intent),
                    Execute = async (doc, plan, op, intent, emit) => {
                        var dr = await DeleteComponent.Run(App, doc, intent, emit);
                        if (dr.Error != null) return new HOutcome { Error = dr.Error };
                        return new HOutcome { Verified = dr.Verified, Items = dr.Deleted, Info = dr.Info };
                    }
                },
                // ApplyAppearance ("color the bolts red" / "color by material") — VISUAL WRITE handler that sets a
                // component's display color. No geometry is edited (one Ctrl+Z per component; Forge never saves), but it
                // modifies the model, so Destructive=true → preview before a broad recolor. AssemblyOnly=false: a lone
                // part can be colored too. PreviewLine returns null for small/ambiguous sets so they execute directly.
                new HSpec {
                    Name = "apply_appearance", Actions = new[] { "apply_appearance" }, AssemblyOnly = false, Destructive = true,
                    Preview = (doc, plan, op, intent) => ApplyAppearance.PreviewLine(doc, intent),
                    Execute = async (doc, plan, op, intent, emit) => {
                        var ar = await ApplyAppearance.Run(App, doc, intent, emit);
                        if (ar.Error != null) return new HOutcome { Error = ar.Error };
                        // FAIL CLOSED (Rule #6): verified only when nothing failed AND either something was newly confirmed
                        // at the requested RGB, or the whole matched set was already that color (idempotent no-op).
                        bool verified = ar.Failed == 0 && (ar.Colored > 0 || (ar.Matched > 0 && ar.AlreadyColored == ar.Matched));
                        return new HOutcome { Verified = verified, Items = ar.Colored, Info = ar.Info };
                    }
                },
                // PatternComponent — reproduce ONE seed fastener into every empty hole on its bolt circle via a real component
                // pattern feature (Forge-Pattern). Adds geometry → preview first; one Ctrl+Z undoes it, Forge never saves.
                new HSpec {
                    Name = "pattern_component", Actions = new[] { "pattern_component" }, AssemblyOnly = true, Destructive = true,
                    Preview = (doc, plan, op, intent) => "This will pattern the seed fastener around the bolt circle to fill every empty hole (a new component pattern; one Ctrl+Z undoes it)",
                    Execute = async (doc, plan, op, intent, emit) => {
                        var pr = await PatternComponent.Run(App, doc, intent, emit);
                        if (pr.Error != null) return new HOutcome { Error = pr.Error };
                        // FAIL CLOSED (Rule #6): verified only when the independent recount matched, nothing over-defined, rebuild
                        // clean, and the feature wasn't rolled back — OR the idempotent "already patterned / holes full" no-op.
                        bool verified = !pr.RolledBack && pr.OverDefined == 0 && pr.RebuildErrors == 0
                                        && (pr.AlreadyPatterned || pr.InstancesAdded == pr.ExpectedInstances);
                        return new HOutcome { Verified = verified, Items = pr.InstancesAdded, Info = pr.Info };
                    }
                },
                // LinearPatternComponent (tool 41) — patterns ONE named component along a straight-line direction,
                // count + spacing given directly by the user (generic — never derived from a hole ring, unlike
                // pattern_component). Adds new component instances → preview first; Forge never saves.
                new HSpec {
                    Name = "linear_pattern_components", Actions = new[] { "linear_pattern_components" }, AssemblyOnly = true, Destructive = true,
                    Preview = (doc, plan, op, intent) => "This will insert copies of the named component along a straight line at the requested count and spacing",
                    Execute = async (doc, plan, op, intent, emit) => {
                        var lr = await LinearPatternComponent.Run(App, doc, intent, emit);
                        if (lr.NeedsConfirm) return new HOutcome { AskedConfirm = true, Question = lr.Question };
                        if (lr.Error != null) return new HOutcome { Error = lr.Error };
                        bool verified = !lr.RolledBack && lr.OverDefined == 0 && lr.RebuildErrors == 0
                                        && (lr.AlreadyPatterned || lr.InstancesAdded == lr.ExpectedInstances);
                        return new HOutcome { Verified = verified, Items = lr.InstancesAdded, Info = lr.Info };
                    }
                },
                // CircularPatternComponent (tool 42) — patterns ONE named component around an axis, count +
                // angle span given directly by the user (generic — never derived from a hole ring, unlike
                // pattern_component). Adds new component instances → preview first; Forge never saves.
                new HSpec {
                    Name = "circular_pattern_components", Actions = new[] { "circular_pattern_components" }, AssemblyOnly = true, Destructive = true,
                    Preview = (doc, plan, op, intent) => "This will insert copies of the named component around the largest cylindrical axis found elsewhere in the assembly, at the requested count and angle",
                    Execute = async (doc, plan, op, intent, emit) => {
                        var cr = await CircularPatternComponent.Run(App, doc, intent, emit);
                        if (cr.NeedsConfirm) return new HOutcome { AskedConfirm = true, Question = cr.Question };
                        if (cr.Error != null) return new HOutcome { Error = cr.Error };
                        bool verified = !cr.RolledBack && cr.OverDefined == 0 && cr.RebuildErrors == 0
                                        && (cr.AlreadyPatterned || cr.InstancesAdded == cr.ExpectedInstances);
                        return new HOutcome { Verified = verified, Items = cr.InstancesAdded, Info = cr.Info };
                    }
                },
                // PatternDrivenPatternComponent (tool 44) — patterns ONE named component to FOLLOW an existing
                // feature pattern (LPattern/CirPattern) already on another component, count/spacing read from that
                // feature rather than given by the user. Adds new component instances → preview first; Forge never saves.
                new HSpec {
                    Name = "pattern_driven_pattern", Actions = new[] { "pattern_driven_pattern" }, AssemblyOnly = true, Destructive = true,
                    Preview = (doc, plan, op, intent) => "This will insert copies of the named component following an existing feature pattern already on another part in the assembly",
                    Execute = async (doc, plan, op, intent, emit) => {
                        var pdr = await PatternDrivenPatternComponent.Run(App, doc, intent, emit);
                        if (pdr.NeedsConfirm) return new HOutcome { AskedConfirm = true, Question = pdr.Question };
                        if (pdr.Error != null) return new HOutcome { Error = pdr.Error };
                        bool verified = !pdr.RolledBack && pdr.OverDefined == 0 && pdr.RebuildErrors == 0
                                        && (pdr.AlreadyPatterned || pdr.InstancesAdded == pdr.ExpectedInstances);
                        return new HOutcome { Verified = verified, Items = pdr.InstancesAdded, Info = pdr.Info };
                    }
                },
                // SketchDrivenPatternComponent (tool 45) — places copies of ONE named component at every point of
                // an existing assembly-level sketch. Adds new component instances → preview first; Forge never saves.
                new HSpec {
                    Name = "sketch_driven_pattern", Actions = new[] { "sketch_driven_pattern" }, AssemblyOnly = true, Destructive = true,
                    Preview = (doc, plan, op, intent) => "This will insert copies of the named component at every point of an existing sketch in the assembly",
                    Execute = async (doc, plan, op, intent, emit) => {
                        var sdr = await SketchDrivenPatternComponent.Run(App, doc, intent, emit);
                        if (sdr.NeedsConfirm) return new HOutcome { AskedConfirm = true, Question = sdr.Question };
                        if (sdr.Error != null) return new HOutcome { Error = sdr.Error };
                        bool verified = !sdr.RolledBack && sdr.OverDefined == 0 && sdr.RebuildErrors == 0
                                        && (sdr.AlreadyPatterned || sdr.InstancesAdded == sdr.ExpectedInstances);
                        return new HOutcome { Verified = verified, Items = sdr.InstancesAdded, Info = sdr.Info };
                    }
                },
                // Resize — changes fastener size definitions; always preview.
                new HSpec {
                    Name = "resize", Actions = new[] { "resize" }, Destructive = true,
                    Preview = (doc, plan, op, intent) => "This will swap fastener size configurations across the assembly",
                    Execute = async (doc, plan, op, intent, emit) => {
                        var rr = await Resizer.Run(App, doc, intent, emit);
                        if (rr.Error != null) return new HOutcome { Error = rr.Error };
                        return new HOutcome { Verified = rr.Error == null && rr.Switched > 0, Items = rr.Switched, Info = rr.Info };
                    }
                },
                // AuditToolbox (tool 249) — READ-ONLY fastener-integrity audit: classifies each fastener as
                // live-toolbox / design-table / baked-fixed so upsize knows which parts can even be config-switched.
                new HSpec {
                    Name = "audit_toolbox", Actions = new[] { "audit_toolbox" }, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var ar = await AuditToolbox.Run(App, doc, intent, emit);
                        if (ar.Error != null) return new HOutcome { Error = ar.Error };
                        return new HOutcome { Verified = true, Items = ar.Fasteners, Info = ar.Info };
                    }
                },
                // Upsize — swap fastener size configs (M6->M8) AND catch the now-undersized clearance holes (offer
                // only). Distinct action from resize so the hole-catch offer is never lost. Definition-changing → preview.
                new HSpec {
                    Name = "upsize", Actions = new[] { "upsize" }, Destructive = true,
                    Preview = (doc, plan, op, intent) => "This will swap every matching fastener up a size (configuration swap) across the assembly",
                    Execute = async (doc, plan, op, intent, emit) => {
                        var ur = await Upsize.Run(App, doc, intent, emit);
                        if (ur.Error != null) return new HOutcome { Error = ur.Error };
                        // FAIL CLOSED: verified only when a read-back confirmed swap happened (or everything was already the new size).
                        bool verified = ur.RebuildClean && (ur.Switched > 0 || (ur.AlreadyNew == ur.Found && ur.Found > 0));
                        return new HOutcome { Verified = verified, Items = ur.Switched, Info = ur.Info };
                    }
                },
                // Simplify — new suppressed config; part OR assembly (assembly => batch every part). Always preview when broad.
                new HSpec {
                    Name = "simplify", Actions = new[] { "simplify" }, AssemblyOnly = false, Destructive = true,
                    Preview = (doc, plan, op, intent) => {
                        bool asm = (int)doc.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY;
                        return asm ? "This will add a print-prep (suppressed-cosmetics) config to every part in the assembly" : null;
                    },
                    Execute = async (doc, plan, op, intent, emit) => {
                        bool asm = (int)doc.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY;
                        if (asm) {
                            var br = await Batcher.Run(App, doc, intent, emit);
                            if (br.Error != null) return new HOutcome { Error = br.Error };
                            return new HOutcome { Verified = br.Error == null, Items = br.TotalSuppressed, Info = br.Info };
                        }
                        var sr = await Simplifier.Run(App, doc, intent, emit);
                        if (sr.Error != null) return new HOutcome { Error = sr.Error };
                        return new HOutcome { Verified = sr.Error == null, Items = sr.Suppressed, Info = sr.Info };
                    }
                },
                // Shell — WRITE: hollow a PART to a wall thickness (casting / 3D-print prep) by adding a Forge-Shell feature.
                // Part-or-assembly at the pipeline (AssemblyOnly=false lets PARTs through); the handler refuses an assembly
                // honestly. Destructive (adds geometry) → preview first. Verified only when the solid volume actually DROPPED
                // and the rebuild is clean; a self-intersecting shell is rolled back and reported, never a fake green.
                new HSpec {
                    Name = "shell_part", Actions = new[] { "shell_part" }, AssemblyOnly = false, Destructive = true,
                    Preview = (doc, plan, op, intent) => {
                        bool part = (int)doc.GetType() == (int)swDocumentTypes_e.swDocPART;
                        return part ? "This will hollow the part into a thin-walled shell (adds a Forge-Shell feature; one Ctrl+Z removes it, Forge won't save)" : null;
                    },
                    Execute = async (doc, plan, op, intent, emit) => {
                        var sr = await ShellPart.Run(App, doc, intent, emit);
                        if (sr.NeedsConfirm) return new HOutcome { AskedConfirm = true, Question = sr.Question };
                        if (sr.Error != null) return new HOutcome { Error = sr.Error };
                        // FAIL CLOSED (Rule #6): Verified is set inside Run only when volume DROPPED and the rebuild is clean
                        // (or the part was already shelled — idempotent). A rolled-back shell returns Error above.
                        return new HOutcome { Verified = sr.Verified, Items = sr.AlreadyDone ? 0 : 1, Info = sr.Info };
                    }
                },
                // Scale — WRITE: scale a PART's geometry by a uniform factor (resizing / inch→mm unit fix) via a Forge-Scale
                // feature. Part-or-assembly at the pipeline (AssemblyOnly=false lets PARTs through); the handler refuses an
                // assembly honestly. Destructive (changes every dimension) → preview first. Verified only when volume landed
                // on ×factor^3 AND the bbox diagonal on ×factor AND the rebuild is clean; a bad scale is rolled back.
                new HSpec {
                    Name = "scale_part", Actions = new[] { "scale_part" }, AssemblyOnly = false, Destructive = true,
                    Preview = (doc, plan, op, intent) => {
                        bool part = (int)doc.GetType() == (int)swDocumentTypes_e.swDocPART;
                        return part ? "This will scale the whole part's geometry by the given factor (adds a parametric Forge-Scale feature; one Ctrl+Z removes it, Forge won't save)" : null;
                    },
                    Execute = async (doc, plan, op, intent, emit) => {
                        var sr = await ScalePart.Run(App, doc, intent, emit);
                        if (sr.NeedsConfirm) return new HOutcome { AskedConfirm = true, Question = sr.Question };
                        if (sr.Error != null) return new HOutcome { Error = sr.Error };
                        return new HOutcome { Verified = sr.Verified, Items = sr.AlreadyDone ? 0 : 1, Info = sr.Info };
                    }
                },
                // SetDimension — WRITE: set ONE named model dimension by plain English ("change the bore to 25", "set D1 to
                // 100"). PART (the handler also reaches assembly dims via the same traversal). Destructive (moves a dimension)
                // → preview. Verified only when the fresh read-back equals the requested value AND the rebuild is clean; a
                // change that breaks the rebuild — or a driven/locked dim that won't take — is rolled back honestly.
                new HSpec {
                    Name = "set_dimension", Actions = new[] { "set_dimension" }, AssemblyOnly = false, Destructive = true,
                    Preview = (doc, plan, op, intent) => SetDimension.Preview(doc, intent),
                    Execute = async (doc, plan, op, intent, emit) => {
                        var dr = await SetDimension.Run(App, doc, intent, emit);
                        if (dr.NeedsConfirm) return new HOutcome { AskedConfirm = true, Question = dr.Question };
                        if (dr.Error != null) return new HOutcome { Error = dr.Error };
                        return new HOutcome { Verified = dr.Verified, Items = dr.AlreadyDone ? 0 : 1, Info = dr.Info };
                    }
                },
                // TransformAssembly — WRITE: move/rotate the ENTIRE assembly as one rigid set (every top-level component gets
                // the SAME transform, so inter-component mates stay satisfied). Assembly-only, Destructive → preview a broad
                // move (PreviewLine returns null for ≤3 comps). Verified only when the geometry-measured center shift matches
                // the request, the move stayed rigid, nothing over-defined, and the rebuild is clean.
                new HSpec {
                    Name = "transform_assembly", Actions = new[] { "transform_assembly" }, AssemblyOnly = true, Destructive = true,
                    Preview = (doc, plan, op, intent) => TransformAssembly.PreviewLine(doc, intent),
                    Execute = async (doc, plan, op, intent, emit) => {
                        var tr = await TransformAssembly.Run(App, doc, intent, emit);
                        if (tr.NeedsConfirm) return new HOutcome { AskedConfirm = true, Question = tr.Question };
                        if (tr.Error != null) return new HOutcome { Error = tr.Error };
                        return new HOutcome { Verified = tr.Verified, Items = tr.ComponentsMoved, Info = tr.Info };
                    }
                },
                // MoveComponent — WRITE: translate ONE floating/unmated component by a vector (only the target moves,
                // everything else stays put). Distinct from transform_assembly (moves EVERY component). Assembly-only,
                // Destructive. Verified only when the target's measured shift matches, NO other component moved, rebuild clean.
                new HSpec {
                    Name = "move_component", Actions = new[] { "move_component" }, AssemblyOnly = true, Destructive = true,
                    Preview = (doc, plan, op, intent) => MoveComponent.PreviewLine(doc, intent),
                    Execute = async (doc, plan, op, intent, emit) => {
                        var mr = await MoveComponent.Run(App, doc, intent, emit);
                        if (mr.NeedsConfirm) return new HOutcome { AskedConfirm = true, Question = mr.Question };
                        if (mr.Error != null) return new HOutcome { Error = mr.Error };
                        return new HOutcome { Verified = mr.Verified, Items = 1, Info = mr.Info };
                    }
                },
                // RotateComponent — WRITE: rotate ONE floating component about its own centroid by an angle (only the target
                // turns, everything else stays put). Assembly-only, Destructive. Verified only when the target's measured
                // orientation change matches the angle, its centre held, NO other component moved/turned, and rebuild clean.
                new HSpec {
                    Name = "rotate_component", Actions = new[] { "rotate_component" }, AssemblyOnly = true, Destructive = true,
                    Preview = (doc, plan, op, intent) => RotateComponent.PreviewLine(doc, intent),
                    Execute = async (doc, plan, op, intent, emit) => {
                        var rr = await RotateComponent.Run(App, doc, intent, emit);
                        if (rr.NeedsConfirm) return new HOutcome { AskedConfirm = true, Question = rr.Question };
                        if (rr.Error != null) return new HOutcome { Error = rr.Error };
                        return new HOutcome { Verified = rr.Verified, Items = 1, Info = rr.Info };
                    }
                },
                // FilletChamfer — WRITE: fillet or chamfer criterion-selected edges of a PART (DFM / finishing) by adding a
                // Forge-Fillet / Forge-Chamfer feature. Part-or-assembly at the pipeline (AssemblyOnly=false lets PARTs
                // through); the handler refuses an assembly honestly. Destructive (adds geometry) → preview first. Verified
                // only when the solid FACE COUNT rose and the rebuild is clean; a feature that won't rebuild is rolled back.
                new HSpec {
                    Name = "fillet_chamfer", Actions = new[] { "fillet_chamfer" }, AssemblyOnly = false, Destructive = true,
                    Preview = (doc, plan, op, intent) => {
                        bool part = (int)doc.GetType() == (int)swDocumentTypes_e.swDocPART;
                        return part ? "This will fillet/chamfer the selected sharp edges (adds a Forge-Fillet/Forge-Chamfer feature; one Ctrl+Z removes it, Forge won't save)" : null;
                    },
                    Execute = async (doc, plan, op, intent, emit) => {
                        var fr = await FilletChamfer.Run(App, doc, intent, emit);
                        if (fr.NeedsConfirm) return new HOutcome { AskedConfirm = true, Question = fr.Question };
                        if (fr.Error != null) return new HOutcome { Error = fr.Error };
                        return new HOutcome { Verified = fr.Verified, Items = fr.AlreadyDone ? 0 : fr.EdgesFilleted, Info = fr.Info };
                    }
                },
                // CreateThread — WRITE: add cosmetic threads (with size callouts) to a PART's internal holes. Part-only at the
                // handler (AssemblyOnly=false lets PARTs through; the handler refuses an assembly). Destructive → preview.
                // Verified only when the independent cosmetic-thread recount rose by exactly ThreadsAdded and rebuild is clean.
                new HSpec {
                    Name = "create_thread", Actions = new[] { "create_thread" }, AssemblyOnly = false, Destructive = true,
                    Preview = (doc, plan, op, intent) => {
                        bool part = (int)doc.GetType() == (int)swDocumentTypes_e.swDocPART;
                        return part ? "This will add cosmetic threads (with size callouts) to the internal holes (one Ctrl+Z per thread removes them; Forge won't save)" : null;
                    },
                    Execute = async (doc, plan, op, intent, emit) => {
                        var tr = await CreateThread.Run(App, doc, intent, emit);
                        if (tr.NeedsConfirm) return new HOutcome { AskedConfirm = true, Question = tr.Question };
                        if (tr.Error != null) return new HOutcome { Error = tr.Error };
                        return new HOutcome { Verified = tr.Verified, Items = tr.ThreadsAdded, Info = tr.Info };
                    }
                },
                // GeometryDefeature — WRITE: strip small holes/fillets from a PART's GEOMETRY (works on IMPORTED DUMB SOLIDS where
                // feature-suppression Simplify is a no-op) for 3D-print / FEA prep. Part-or-assembly at the pipeline
                // (AssemblyOnly=false lets PARTs through); the handler refuses an assembly honestly. Destructive → preview first.
                // Verified only when the face count actually fell AND volume rose (holes filled) with a clean rebuild — or the
                // fallback wrote a real defeatured copy; a heal that didn't take is rolled back and reported, never a fake green.
                new HSpec {
                    Name = "geometry_defeature", Actions = new[] { "geometry_defeature" }, AssemblyOnly = false, Destructive = true,
                    Preview = (doc, plan, op, intent) => {
                        bool part = (int)doc.GetType() == (int)swDocumentTypes_e.swDocPART;
                        return part ? "This will remove the small holes and fillets from the part's geometry (delete-and-patch; works on imported dumb solids). One Ctrl+Z restores it, Forge won't save" : null;
                    },
                    Execute = async (doc, plan, op, intent, emit) => {
                        var gr = await GeometryDefeature.Run(App, doc, intent, emit);
                        if (gr.Error != null) return new HOutcome { Error = gr.Error };
                        // FAIL CLOSED (Rule #6): Verified is set inside Run only when the in-place removal was geometry-confirmed
                        // (faces down + volume up + clean rebuild), OR the part was already simplified (idempotent), OR the fallback
                        // copy was written. Items = faces removed (0 on the "already simplified" and fallback paths).
                        return new HOutcome { Verified = gr.Verified, Items = gr.FacesRemoved, Info = gr.Info };
                    }
                },
                // Batch — explicit "do it to every part"; always preview.
                new HSpec {
                    Name = "batch", Actions = new[] { "batch" }, Destructive = true,
                    Preview = (doc, plan, op, intent) => "This will run print-prep on every unique part in the assembly",
                    Execute = async (doc, plan, op, intent, emit) => {
                        var br = await Batcher.Run(App, doc, intent, emit);
                        if (br.Error != null) return new HOutcome { Error = br.Error };
                        return new HOutcome { Verified = br.Error == null, Items = br.TotalSuppressed, Info = br.Info };
                    }
                },
                // Drawing package — rebuild every sibling drawing, repair dangling dims, export PDFs. Writes only
                // NEW PDF files (source .SLDDRW never saved/overwritten) into a default Forge-PDF folder next to the
                // model, so it ACTS on a natural command — no hedge about the output folder, no "reply go" gate
                // (Demo 9). Accepts a DRAWING doc as the active doc (it's what this handler is for). Non-destructive
                // to the model → never hits the ambiguity/preview gates that made it stop and ask.
                new HSpec {
                    Name = "drawing_package", Actions = new[] { "drawing_package" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var dr = await DrawingPkg.Run(App, doc, intent, emit);
                        if (dr.Error != null) return new HOutcome { Error = dr.Error };
                        return new HOutcome { Verified = dr.PdfsWritten > 0 && dr.PdfsWritten >= dr.Processed, Items = dr.PdfsWritten, Info = dr.Info };
                    }
                },
                // FlatDxf — export every sheet-metal part's flat pattern as a shop-ready DXF (bend lines on their own
                // layer). WRITES only NEW DXF files (never the source models). Part OR assembly. Detects sheet-metal
                // ITSELF and skips non-sheet-metal cleanly, so it ACTS on a natural command — no "confirm this is
                // sheet metal" hedge (Demo 10). Non-destructive to the model → skips the ambiguity/preview gates.
                new HSpec {
                    Name = "flat_dxf", Actions = new[] { "flat_dxf" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var fr = await FlatDxf.Run(App, doc, intent, emit);
                        if (fr.Error != null) return new HOutcome { Error = fr.Error };
                        // fail closed: verified only when every sheet-metal part produced a DXF and none failed
                        return new HOutcome {
                            Verified = fr.Error == null && fr.Failed == 0 && fr.Exported == fr.SheetMetalParts && fr.Exported > 0,
                            Items = fr.Exported, Info = fr.Info };
                    }
                },
                // CreateDrawing (tool 101, WRITE) — live-dispatch gap fix (2026-07-29): this was harness-GREEN
                // (Harness.cs IntentDispatch) since it shipped but never registered as an HSpec here, so a real
                // "create a drawing" typed into the panel could never reach it — same class of gap as
                // delete_component/rename_component/get_faces before it. Never saves (Forge never saves by
                // default); non-destructive to the currently-open document (it opens a brand-new doc alongside it).
                new HSpec {
                    Name = "create_drawing", Actions = new[] { "create_drawing" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var cd = await CreateDrawing.Run(App, doc, intent, emit);
                        if (cd.Error != null) return new HOutcome { Error = cd.Error };
                        return new HOutcome { Verified = cd.Created, Items = cd.Created ? 1 : 0, Info = "Created " + (cd.Title ?? "new drawing") + " from " + System.IO.Path.GetFileName(cd.TemplatePath ?? "") };
                    }
                },
                // CreatePart (tool 228, WRITE): brand-new blank part document, same shape as CreateDrawing above.
                // Non-destructive (opens a new doc alongside whatever's already open); never requires a doc to be open.
                new HSpec {
                    Name = "create_part", Actions = new[] { "create_part" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var cp = await CreatePart.Run(App, doc, intent, emit);
                        if (cp.Error != null) return new HOutcome { Error = cp.Error };
                        return new HOutcome { Verified = cp.Created, Items = cp.Created ? 1 : 0, Info = "Created " + (cp.Title ?? "new part") + " from " + System.IO.Path.GetFileName(cp.TemplatePath ?? "") };
                    }
                },
                // CreatePlate (WRITE): a real SOLID from scratch — a rectangular plate/block (blank part + centred
                // rect + extrude). The "create a plate/block/cube" primitive CreatePart alone can't produce (a blank
                // part has no solid). Same non-destructive new-doc convention as create_part above. Verified only when
                // the plate was created AND the post-rebuild check is clean.
                new HSpec {
                    Name = "create_plate", Actions = new[] { "create_plate" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var pl = await CreatePlate.Run(App, doc, intent, emit);
                        if (pl.Error != null) return new HOutcome { Error = pl.Error };
                        return new HOutcome { Verified = pl.Created && pl.RebuildClean, Items = pl.Created ? 1 : 0, Info = pl.Info };
                    }
                },
                // CreateSphere (WRITE): a real SOLID sphere from scratch (blank part + half-circle revolve). Same
                // new-doc convention. Verified only via the INDEPENDENT volume check ≈ (4/3)πr³ + clean rebuild
                // (res.Verified — fail closed, never a fake green on an unmeasured solid).
                new HSpec {
                    Name = "create_sphere", Actions = new[] { "create_sphere" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var sp = await CreateSphere.Run(App, doc, intent, emit);
                        if (sp.Error != null) return new HOutcome { Error = sp.Error };
                        return new HOutcome { Verified = sp.Verified, Items = sp.Created ? 1 : 0, Info = sp.Info };
                    }
                },
                // CreateCylinder (WRITE): a real SOLID cylinder from scratch (blank part + circle extrude). Same
                // new-doc convention. Verified only via the INDEPENDENT volume check ≈ πr²h + clean rebuild.
                new HSpec {
                    Name = "create_cylinder", Actions = new[] { "create_cylinder" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var cy = await CreateCylinder.Run(App, doc, intent, emit);
                        if (cy.Error != null) return new HOutcome { Error = cy.Error };
                        return new HOutcome { Verified = cy.Verified, Items = cy.Created ? 1 : 0, Info = cy.Info };
                    }
                },
                // CreateAssembly (tool 229, WRITE): brand-new blank assembly document, same shape as CreatePart above.
                new HSpec {
                    Name = "create_assembly", Actions = new[] { "create_assembly" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var ca = await CreateAssembly.Run(App, doc, intent, emit);
                        if (ca.Error != null) return new HOutcome { Error = ca.Error };
                        return new HOutcome { Verified = ca.Created, Items = ca.Created ? 1 : 0, Info = "Created " + (ca.Title ?? "new assembly") + " from " + System.IO.Path.GetFileName(ca.TemplatePath ?? "") };
                    }
                },
                // InsertNewPartInContext (tool 230, WRITE): top-down in-context part creation, additive only
                // (never removes/replaces an existing component) — same Destructive=false convention as
                // InsertComponent above.
                new HSpec {
                    Name = "insert_new_part_in_context", Actions = new[] { "insert_new_part_in_context" }, AssemblyOnly = true, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var ip = await InsertNewPartInContext.Run(App, doc, intent, emit);
                        if (ip.Error != null) return new HOutcome { Error = ip.Error };
                        return new HOutcome { Verified = ip.Success, Items = ip.Success ? 1 : 0, Info = ip.Info };
                    }
                },
                // InsertStandardViews (tool 102, WRITE): reuses CreateDrawing when no drawing is open yet, then adds
                // front/top/right/isometric views. Built with full production wiring from the start (HSpec +
                // RunViaPipeline intercept + LocalActionFor, see docs/kb/robustness.md "Live-dispatch gap").
                new HSpec {
                    Name = "insert_standard_views", Actions = new[] { "insert_standard_views" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var iv = await InsertStandardViews.Run(App, doc, intent, emit);
                        if (iv.NeedsConfirm) return new HOutcome { AskedConfirm = true, Question = iv.Question };
                        if (iv.Error != null) return new HOutcome { Error = iv.Error };
                        return new HOutcome { Verified = iv.Verified, Items = iv.ViewsInserted, Info = iv.Info };
                    }
                },
                // InsertView (tool 103, WRITE): reuses CreateDrawing to bootstrap a drawing when none is open yet,
                // then places ONE named/custom view (narrower than InsertStandardViews' always-4), optionally at a
                // requested scale. Built with full production wiring from the start (HSpec + RunViaPipeline
                // intercept + LocalActionFor, see docs/kb/robustness.md "Live-dispatch gap").
                new HSpec {
                    Name = "insert_view", Actions = new[] { "insert_view" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var iv = await InsertView.Run(App, doc, intent, emit);
                        if (iv.NeedsConfirm) return new HOutcome { AskedConfirm = true, Question = iv.Question };
                        if (iv.Error != null) return new HOutcome { Error = iv.Error };
                        return new HOutcome { Verified = iv.Verified, Items = iv.ViewCountAfter - iv.ViewCountBefore, Info = iv.Info };
                    }
                },
                // SetViewScale (tool 107, WRITE): reuses InsertStandardViews to bootstrap a drawing when none is
                // open yet, then sets ONE view's own scale. Built with full production wiring from the start
                // (HSpec + RunViaPipeline intercept + LocalActionFor, see docs/kb/robustness.md "Live-dispatch gap").
                new HSpec {
                    Name = "set_view_scale", Actions = new[] { "set_view_scale" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var sv = await SetViewScale.Run(App, doc, intent, emit);
                        if (sv.NeedsConfirm) return new HOutcome { AskedConfirm = true, Question = sv.Question };
                        if (sv.Error != null) return new HOutcome { Error = sv.Error };
                        return new HOutcome { Verified = sv.Verified, Items = 1, Info = sv.Info };
                    }
                },
                // DeleteView (tool 106, WRITE): permanently removes one view from the current drawing sheet.
                // Destructive=true (an irreversible tree edit, same class as delete_feature/delete_mate) — the
                // handler's own idempotent "already gone" path means a repeat ask never fires for a stale target.
                new HSpec {
                    Name = "delete_view", Actions = new[] { "delete_view" }, AssemblyOnly = false, Destructive = true,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var dv = await DeleteView.Run(App, doc, intent, emit);
                        if (dv.NeedsConfirm) return new HOutcome { AskedConfirm = true, Question = dv.Question };
                        if (dv.Error != null) return new HOutcome { Error = dv.Error };
                        return new HOutcome { Verified = dv.Verified, Items = 1, Info = dv.Info };
                    }
                },
                // AddNote (tool 112, WRITE): places free text on the current drawing sheet, bootstrapping a bare
                // drawing (reusing CreateDrawing) when none is open yet.
                new HSpec {
                    Name = "add_note", Actions = new[] { "add_note" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var an = await AddNote.Run(App, doc, intent, emit);
                        if (an.NeedsConfirm) return new HOutcome { AskedConfirm = true, Question = an.Question };
                        if (an.Error != null) return new HOutcome { Error = an.Error };
                        return new HOutcome { Verified = an.Verified, Items = 1, Info = an.Info };
                    }
                },
                // ImportModelDimensions (tool 108, WRITE): reuses InsertStandardViews to bootstrap a drawing with
                // views when none is open yet, then pulls the model's own dimensions onto the sheet.
                new HSpec {
                    Name = "import_model_dimensions", Actions = new[] { "import_model_dimensions" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var imd = await ImportModelDimensions.Run(App, doc, intent, emit);
                        if (imd.NeedsConfirm) return new HOutcome { AskedConfirm = true, Question = imd.Question };
                        if (imd.Error != null) return new HOutcome { Error = imd.Error };
                        return new HOutcome { Verified = imd.Verified, Items = imd.DimensionCountAfter - imd.DimensionCountBefore, Info = imd.Info };
                    }
                },
                // Doc-lifecycle cluster (tools 124-127, 135) — live-dispatch gap fix (2026-07-29): all four shipped
                // harness-GREEN but were never registered as HSpecs here, same class of gap as create_drawing above.
                // Each already has its own internal Rule #2 confirm-ask (NeedsConfirm/Question), so Destructive=false
                // here matches CreateDrawing/CountGearTeeth/InsertStandardViews — the handler is its own gate.
                new HSpec {
                    Name = "open_document", Actions = new[] { "open_document" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var or_ = await OpenDocument.Run(App, doc, intent, null, emit);
                        if (or_.NeedsConfirm) return new HOutcome { AskedConfirm = true, Question = or_.Question };
                        if (or_.Error != null) return new HOutcome { Error = or_.Error };
                        return new HOutcome { Verified = or_.Opened, Items = or_.Opened ? 1 : 0, Info = "Opened " + (or_.Title ?? or_.Path) + " (" + or_.LoadMode + ")" };
                    }
                },
                new HSpec {
                    Name = "save_document", Actions = new[] { "save_document" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var sr = await SaveDocument.Run(App, doc, intent, emit);
                        if (sr.Error != null) return new HOutcome { Error = sr.Error };
                        return new HOutcome { Verified = sr.Saved, Items = sr.Saved ? 1 : 0, Info = "Saved " + System.IO.Path.GetFileName(sr.Path ?? "") + " (errs=" + sr.Errors + ", warns=" + sr.Warnings + ")" };
                    }
                },
                new HSpec {
                    Name = "save_document_as", Actions = new[] { "save_document_as" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var sar = await SaveDocumentAs.Run(App, doc, intent, emit);
                        if (sar.NeedsConfirm) return new HOutcome { AskedConfirm = true, Question = sar.Question };
                        if (sar.Error != null) return new HOutcome { Error = sar.Error };
                        return new HOutcome { Verified = sar.Saved && sar.ReopenVerified, Items = sar.Saved ? 1 : 0, Info = "Saved a copy to " + sar.OutputPath };
                    }
                },
                new HSpec {
                    Name = "close_document", Actions = new[] { "close_document" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var cr = await CloseDocument.Run(App, doc, intent, emit);
                        if (cr.NeedsConfirm) return new HOutcome { AskedConfirm = true, Question = cr.Question };
                        if (cr.Error != null) return new HOutcome { Error = cr.Error };
                        return new HOutcome { Verified = cr.Closed, Items = cr.Closed ? 1 : 0, Info = "Closed " + System.IO.Path.GetFileName(cr.Path ?? "") + (cr.Saved ? " (saved first)" : "") };
                    }
                },
                new HSpec {
                    Name = "batch_convert_files", Actions = new[] { "batch_convert_files" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var bcr = await BatchConvertFiles.Run(App, doc, intent, emit);
                        if (bcr.Question != null) return new HOutcome { AskedConfirm = true, Question = bcr.Question };
                        if (bcr.Error != null) return new HOutcome { Error = bcr.Error };
                        return new HOutcome { Verified = bcr.Converted > 0 && bcr.Failed == 0, Items = bcr.Converted, Info = bcr.Info };
                    }
                },
                new HSpec {
                    Name = "import_file", Actions = new[] { "import_file" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var ifr = await ImportFile.Run(App, doc, intent, null, emit);
                        if (ifr.Question != null) return new HOutcome { AskedConfirm = true, Question = ifr.Question };
                        if (ifr.Error != null) return new HOutcome { Error = ifr.Error };
                        return new HOutcome { Verified = ifr.Imported, Items = ifr.Imported ? 1 : 0, Info = "Imported " + System.IO.Path.GetFileName(ifr.NewPartPath ?? "") + " (" + ifr.SourceFormat + ")" };
                    }
                },
                new HSpec {
                    Name = "batch_export_drawings", Actions = new[] { "batch_export_drawings" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var bed = await BatchExportDrawings.Run(App, doc, intent, emit);
                        if (bed.Error != null) return new HOutcome { Error = bed.Error };
                        return new HOutcome { Verified = bed.Converted > 0 && bed.Failed == 0, Items = bed.Converted, Info = bed.Info };
                    }
                },
                // Mate family (tools 53/55/56/58/59/60/61, live-dispatch gap fix 2026-07-29) — all nine share the
                // same MateName/CompA/CompB/Verified/Info/Error shape; none has a NeedsConfirm (each fails closed
                // with a concrete Error instead of asking). AssemblyOnly=true — mates only exist on assemblies.
                new HSpec {
                    Name = "add_concentric_mate", Actions = new[] { "add_concentric_mate" }, AssemblyOnly = true, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var mr = await AddConcentricMate.Run(App, doc, intent, emit);
                        if (mr.Error != null) return new HOutcome { Error = mr.Error };
                        return new HOutcome { Verified = mr.Verified, Items = mr.MatesAfter, Info = mr.Info };
                    }
                },
                new HSpec {
                    Name = "add_coincident_mate", Actions = new[] { "add_coincident_mate" }, AssemblyOnly = true, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var mr = await AddCoincidentMate.Run(App, doc, intent, emit);
                        if (mr.Error != null) return new HOutcome { Error = mr.Error };
                        return new HOutcome { Verified = mr.Verified, Items = mr.MatesAfter, Info = mr.Info };
                    }
                },
                new HSpec {
                    Name = "add_parallel_mate", Actions = new[] { "add_parallel_mate" }, AssemblyOnly = true, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var mr = await AddParallelMate.Run(App, doc, intent, emit);
                        if (mr.Error != null) return new HOutcome { Error = mr.Error };
                        return new HOutcome { Verified = mr.Verified, Items = mr.MatesAfter, Info = mr.Info };
                    }
                },
                new HSpec {
                    Name = "add_distance_mate", Actions = new[] { "add_distance_mate" }, AssemblyOnly = true, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var mr = await AddDistanceMate.Run(App, doc, intent, emit);
                        if (mr.Error != null) return new HOutcome { Error = mr.Error };
                        return new HOutcome { Verified = mr.Verified, Items = mr.MatesAfter, Info = mr.Info };
                    }
                },
                new HSpec {
                    Name = "add_angle_mate", Actions = new[] { "add_angle_mate" }, AssemblyOnly = true, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var mr = await AddAngleMate.Run(App, doc, intent, emit);
                        if (mr.Error != null) return new HOutcome { Error = mr.Error };
                        return new HOutcome { Verified = mr.Verified, Items = mr.MatesAfter, Info = mr.Info };
                    }
                },
                new HSpec {
                    Name = "add_width_mate", Actions = new[] { "add_width_mate" }, AssemblyOnly = true, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var mr = await AddWidthMate.Run(App, doc, intent, emit);
                        if (mr.Error != null) return new HOutcome { Error = mr.Error };
                        return new HOutcome { Verified = mr.Verified, Items = mr.MatesAfter, Info = mr.Info };
                    }
                },
                new HSpec {
                    Name = "edit_mate_value", Actions = new[] { "edit_mate_value" }, AssemblyOnly = true, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var mr = await EditMateValue.Run(App, doc, intent, emit);
                        if (mr.Error != null) return new HOutcome { Error = mr.Error };
                        return new HOutcome { Verified = mr.Verified, Items = 1, Info = mr.Info };
                    }
                },
                new HSpec {
                    Name = "delete_mate", Actions = new[] { "delete_mate" }, AssemblyOnly = true, Destructive = true,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var mr = await DeleteMate.Run(App, doc, intent, emit);
                        if (mr.Error != null) return new HOutcome { Error = mr.Error };
                        return new HOutcome { Verified = mr.Verified, Items = 1, Info = mr.Info };
                    }
                },
                new HSpec {
                    Name = "suppress_mate", Actions = new[] { "suppress_mate", "unsuppress_mate" }, AssemblyOnly = true, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var mr = await SuppressMate.Run(App, doc, intent, emit);
                        if (mr.Error != null) return new HOutcome { Error = mr.Error };
                        return new HOutcome { Verified = mr.Verified, Items = 1, Info = mr.Info };
                    }
                },
                // Pattern-edit family (tools 48/49/52, live-dispatch gap fix 2026-07-29) — all three PART-only
                // (AssemblyOnly=false), reversible (undoable, never saves), share the ILinearPatternFeatureData/
                // ModifyDefinition recipe. SkipPatternInstance alone can NeedsConfirm (instance number out of range).
                new HSpec {
                    Name = "edit_pattern_count", Actions = new[] { "edit_pattern_count" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var epc = await EditPatternCount.Run(App, doc, intent, emit);
                        if (epc.Error != null) return new HOutcome { Error = epc.Error };
                        return new HOutcome { Verified = epc.Verified, Items = epc.InstancesAfter, Info = epc.Info };
                    }
                },
                new HSpec {
                    Name = "edit_pattern_spacing", Actions = new[] { "edit_pattern_spacing" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var eps = await EditPatternSpacing.Run(App, doc, intent, emit);
                        if (eps.Error != null) return new HOutcome { Error = eps.Error };
                        return new HOutcome { Verified = eps.Verified, Items = 1, Info = eps.Info };
                    }
                },
                new HSpec {
                    Name = "skip_pattern_instance", Actions = new[] { "skip_pattern_instance" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var spi = await SkipPatternInstance.Run(App, doc, intent, emit);
                        if (spi.NeedsConfirm) return new HOutcome { AskedConfirm = true, Question = spi.Question };
                        if (spi.Error != null) return new HOutcome { Error = spi.Error };
                        return new HOutcome { Verified = spi.Verified, Items = spi.SkippedAfter, Info = spi.Info };
                    }
                },
                // Configuration + custom-property family (tools 39/64/67/68/69/70/71/72/85/90, live-dispatch gap fix
                // 2026-07-29) — configs and custom properties both work on part or assembly (AssemblyOnly=false)
                // except change_component_config/get_component_config which need named component instances
                // (AssemblyOnly=true). delete_configuration/delete_custom_property are the only destructive pair.
                new HSpec {
                    Name = "set_config_specific_dimension", Actions = new[] { "set_config_specific_dimension" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var csd = await ConfigSpecificDimension.Run(App, doc, intent, emit);
                        if (csd.NeedsConfirm) return new HOutcome { AskedConfirm = true, Question = csd.Question };
                        if (csd.Error != null) return new HOutcome { Error = csd.Error };
                        return new HOutcome { Verified = csd.Verified, Items = csd.MatchedDims, Info = csd.Info };
                    }
                },
                new HSpec {
                    Name = "set_config_feature_suppression", Actions = new[] { "set_config_feature_suppression" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var cfs = await ConfigFeatureSuppression.Run(App, doc, intent, emit);
                        if (cfs.NeedsConfirm) return new HOutcome { AskedConfirm = true, Question = cfs.Question };
                        if (cfs.Error != null) return new HOutcome { Error = cfs.Error };
                        return new HOutcome { Verified = cfs.Verified, Items = 1, Info = cfs.Info };
                    }
                },
                new HSpec {
                    Name = "change_component_config", Actions = new[] { "change_component_config" }, AssemblyOnly = true, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var ccc = await ChangeComponentConfig.Run(App, doc, intent, emit);
                        if (ccc.NeedsConfirm) return new HOutcome { AskedConfirm = true, Question = ccc.Question };
                        if (ccc.Error != null) return new HOutcome { Error = ccc.Error };
                        return new HOutcome { Verified = ccc.Verified, Items = ccc.Changed, Info = ccc.Info };
                    }
                },
                new HSpec {
                    Name = "set_active_configuration", Actions = new[] { "set_active_configuration" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var sac = await SetActiveConfiguration.Run(App, doc, intent, emit);
                        if (sac.Error != null) return new HOutcome { Error = sac.Error };
                        return new HOutcome { Verified = sac.Verified, Items = 1, Info = sac.Info };
                    }
                },
                new HSpec {
                    Name = "create_configuration", Actions = new[] { "create_configuration" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var crc = await CreateConfiguration.Run(App, doc, intent, emit);
                        if (crc.Error != null) return new HOutcome { Error = crc.Error };
                        return new HOutcome { Verified = crc.Verified, Items = crc.ConfigsAfter, Info = crc.Info };
                    }
                },
                new HSpec {
                    Name = "delete_configuration", Actions = new[] { "delete_configuration" }, AssemblyOnly = false, Destructive = true,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var dlc = await DeleteConfiguration.Run(App, doc, intent, emit);
                        if (dlc.Error != null) return new HOutcome { Error = dlc.Error };
                        return new HOutcome { Verified = dlc.Verified, Items = dlc.ConfigsAfter, Info = dlc.Info };
                    }
                },
                new HSpec {
                    Name = "rename_configuration", Actions = new[] { "rename_configuration" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var rnc = await RenameConfiguration.Run(App, doc, intent, emit);
                        if (rnc.Error != null) return new HOutcome { Error = rnc.Error };
                        return new HOutcome { Verified = rnc.Verified, Items = 1, Info = rnc.Info };
                    }
                },
                new HSpec {
                    Name = "copy_configuration", Actions = new[] { "copy_configuration" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var cpc = await CopyConfiguration.Run(App, doc, intent, emit);
                        if (cpc.Error != null) return new HOutcome { Error = cpc.Error };
                        return new HOutcome { Verified = cpc.Verified, Items = cpc.CountAfter, Info = cpc.Info };
                    }
                },
                new HSpec {
                    Name = "set_custom_property", Actions = new[] { "set_custom_property" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var scp = await SetCustomProperty.Run(App, doc, intent, emit);
                        if (scp.Error != null) return new HOutcome { Error = scp.Error };
                        return new HOutcome { Verified = scp.Verified, Items = 1, Info = scp.Info };
                    }
                },
                // CopyPropertiesBetweenFiles (tool 142, WRITE): template propagation — reads every custom property
                // off a SOURCE file (path parsed from the command) and writes each onto the currently-open target.
                new HSpec {
                    Name = "copy_properties_between_files", Actions = new[] { "copy_properties_between_files" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var cpb = await CopyPropertiesBetweenFiles.Run(App, doc, intent, null, emit);
                        if (cpb.NeedsConfirm) return new HOutcome { AskedConfirm = true, Question = cpb.Question };
                        if (cpb.Error != null) return new HOutcome { Error = cpb.Error };
                        return new HOutcome { Verified = cpb.Verified, Items = cpb.Copied, Info = cpb.Info };
                    }
                },
                // CopySketchToPart (tool 152, WRITE): reads a sketch's LINE geometry off a SOURCE file (path parsed
                // from the command) and recreates it as a new sketch on the currently-open target.
                new HSpec {
                    Name = "copy_sketch_to_part", Actions = new[] { "copy_sketch_to_part" }, AssemblyOnly = false, Destructive = true,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var cst = await CopySketchToPart.Run(App, doc, intent, null, emit);
                        if (cst.Error != null) return new HOutcome { Error = cst.Error };
                        return new HOutcome { Verified = cst.Applied, Items = cst.LinesCopied, Info = cst.Info };
                    }
                },
                // InsertLibraryFeature (tool 218, WRITE, PROBE): places a real shipped .sldlfp Design Library
                // feature on the part's first planar face. Destructive=true (it's an irreversible geometry write).
                new HSpec {
                    Name = "insert_library_feature", Actions = new[] { "insert_library_feature" }, AssemblyOnly = false, Destructive = true,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var ilf = await InsertLibraryFeature.Run(App, doc, intent, emit);
                        if (ilf.Error != null) return new HOutcome { Error = ilf.Error };
                        return new HOutcome { Verified = ilf.Verified, Items = ilf.Verified ? 1 : 0, Info = ilf.Info };
                    }
                },
                new HSpec {
                    Name = "delete_custom_property", Actions = new[] { "delete_custom_property" }, AssemblyOnly = false, Destructive = true,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var dcp = await DeleteCustomProperty.Run(App, doc, intent, emit);
                        if (dcp.Error != null) return new HOutcome { Error = dcp.Error };
                        return new HOutcome { Verified = dcp.Verified, Items = dcp.WasPresent ? 1 : 0, Info = dcp.Info };
                    }
                },
                new HSpec {
                    Name = "get_custom_property", Actions = new[] { "get_custom_property" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var gcp = await GetCustomProperty.Run(App, doc, intent, emit);
                        if (gcp.Error != null) return new HOutcome { Error = gcp.Error };
                        return new HOutcome { Verified = gcp.Found, Items = gcp.Found ? 1 : 0, Info = gcp.Info };
                    }
                },
                new HSpec {
                    Name = "get_component_config", Actions = new[] { "get_component_config" }, AssemblyOnly = true, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var gcc = await GetComponentConfig.Run(App, doc, intent, emit);
                        if (gcc.Error != null) return new HOutcome { Error = gcc.Error };
                        return new HOutcome { Verified = true, Items = gcc.Total, Info = gcc.Info };
                    }
                },
                // Document-settings family (tools 76/77/78/79/80/232 + get_document_units, live-dispatch gap fix
                // 2026-07-29) — units/decimal-places/drafting-standard/dimension-rename, each fenced by its own
                // required vocabulary word (Harness.cs IntentDispatch already proves this exact order is collision-
                // free: normalize -> angular -> decimal -> drafting-standard -> set-units -> get-units).
                new HSpec {
                    Name = "rename_dimension", Actions = new[] { "rename_dimension" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var rnd = await RenameDimension.Run(App, doc, intent, emit);
                        if (rnd.Error != null) return new HOutcome { Error = rnd.Error };
                        return new HOutcome { Verified = rnd.Renamed, Items = rnd.Renamed ? 1 : 0, Info = rnd.Info };
                    }
                },
                new HSpec {
                    Name = "normalize_units", Actions = new[] { "normalize_units" }, AssemblyOnly = true, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var nu = await NormalizeUnits.Run(App, doc, intent, emit);
                        if (nu.Error != null) return new HOutcome { Error = nu.Error };
                        return new HOutcome { Verified = true, Items = nu.MismatchCount, Info = nu.Info };
                    }
                },
                new HSpec {
                    Name = "set_angular_units", Actions = new[] { "set_angular_units" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var sau = await SetAngularUnits.Run(App, doc, intent, emit);
                        if (sau.Error != null) return new HOutcome { Error = sau.Error };
                        return new HOutcome { Verified = sau.Verified, Items = 1, Info = sau.Info };
                    }
                },
                new HSpec {
                    Name = "set_decimal_places", Actions = new[] { "set_decimal_places" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var sdp = await SetDecimalPlaces.Run(App, doc, intent, emit);
                        if (sdp.Error != null) return new HOutcome { Error = sdp.Error };
                        return new HOutcome { Verified = sdp.Verified, Items = sdp.After, Info = sdp.Info };
                    }
                },
                new HSpec {
                    Name = "set_document_properties", Actions = new[] { "set_document_properties" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var sds = await SetDraftingStandard.Run(App, doc, intent, emit);
                        if (sds.Error != null) return new HOutcome { Error = sds.Error };
                        return new HOutcome { Verified = sds.Verified, Items = 1, Info = sds.Info };
                    }
                },
                new HSpec {
                    Name = "set_document_units", Actions = new[] { "set_document_units" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var sdu = await SetDocumentUnits.Run(App, doc, intent, emit);
                        if (sdu.Error != null) return new HOutcome { Error = sdu.Error };
                        return new HOutcome { Verified = sdu.Verified, Items = 1, Info = sdu.Info };
                    }
                },
                new HSpec {
                    Name = "get_document_units", Actions = new[] { "get_document_units" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var gdu = await GetDocumentUnits.Run(App, doc, intent, emit);
                        if (gdu.Error != null) return new HOutcome { Error = gdu.Error };
                        return new HOutcome { Verified = true, Items = 1, Info = gdu.Info };
                    }
                },
                // List/count family (tools 44/45/etc, live-dispatch gap fix 2026-07-29) — all READ, disjoint
                // required vocabulary (tree/subassembly/reference-geometry/dependency/dimension nouns never
                // overlap). get_dimension_value and list_dimensions share one class (same case block in
                // Harness.cs) — one HSpec, both action names.
                new HSpec {
                    Name = "list_dimensions", Actions = new[] { "list_dimensions", "get_dimension_value" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var gd = await GetDimensions.Run(App, doc, intent, emit);
                        if (gd.Error != null) return new HOutcome { Error = gd.Error };
                        return new HOutcome { Verified = true, Items = gd.Count, Info = gd.Info };
                    }
                },
                new HSpec {
                    Name = "list_components", Actions = new[] { "list_components" }, AssemblyOnly = true, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var lc = await ListComponents.Run(App, doc, intent, emit);
                        if (lc.Error != null) return new HOutcome { Error = lc.Error };
                        return new HOutcome { Verified = true, Items = lc.Total, Info = lc.Info };
                    }
                },
                new HSpec {
                    Name = "list_feature_dependencies", Actions = new[] { "list_feature_dependencies" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var lfd = await ListFeatureDependencies.Run(App, doc, intent, emit);
                        if (lfd.Error != null) return new HOutcome { Error = lfd.Error };
                        return new HOutcome { Verified = lfd.Resolved, Items = lfd.ChildCount, Info = lfd.Info };
                    }
                },
                new HSpec {
                    Name = "list_subassemblies", Actions = new[] { "list_subassemblies" }, AssemblyOnly = true, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var ls = await ListSubassemblies.Run(App, doc, intent, emit);
                        if (ls.Error != null) return new HOutcome { Error = ls.Error };
                        return new HOutcome { Verified = true, Items = ls.SubAssemblies, Info = ls.Info };
                    }
                },
                new HSpec {
                    Name = "list_reference_geometry", Actions = new[] { "list_reference_geometry" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var rg = await GetRefGeometry.Run(App, doc, intent, emit);
                        if (rg.Error != null) return new HOutcome { Error = rg.Error };
                        return new HOutcome { Verified = true, Items = rg.Planes + rg.Axes + rg.Points + rg.CoordSystems, Info = rg.Info };
                    }
                },
                // Component-tree WRITE family (tools 17/31/etc, live-dispatch gap fix 2026-07-29). insert_component
                // and replace_component both require a named FILE reference (quoted path or .sldprt/.sldasm token)
                // so they never collide with feature-add/config-swap handlers. combine_bodies is PARKED (dead API:
                // InsertCombineFeature is a silent no-op headless on this build) — wiring it still helps: a user
                // who asks now gets the handler's honest, specific dead-API refusal instead of falling through to
                // the cloud's generic misroute/hedge.
                new HSpec {
                    Name = "insert_component", Actions = new[] { "insert_component" }, AssemblyOnly = true, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var ic = await InsertComponent.Run(App, doc, intent, emit);
                        if (ic.NeedsConfirm) return new HOutcome { AskedConfirm = true, Question = ic.Question };
                        if (ic.Error != null) return new HOutcome { Error = ic.Error };
                        return new HOutcome { Verified = ic.Verified, Items = ic.Inserted, Info = ic.Info };
                    }
                },
                new HSpec {
                    Name = "replace_component", Actions = new[] { "replace_component" }, AssemblyOnly = true, Destructive = true,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var rc = await ReplaceComponent.Run(App, doc, intent, emit);
                        if (rc.NeedsConfirm) return new HOutcome { AskedConfirm = true, Question = rc.Question };
                        if (rc.Error != null) return new HOutcome { Error = rc.Error };
                        return new HOutcome { Verified = rc.Verified, Items = rc.Replaced, Info = rc.Info };
                    }
                },
                new HSpec {
                    Name = "batch_replace_components", Actions = new[] { "batch_replace_components" }, AssemblyOnly = true, Destructive = true,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var brc = await BatchReplaceComponents.Run(App, doc, intent, emit);
                        if (brc.NeedsConfirm) return new HOutcome { AskedConfirm = true, Question = brc.Question };
                        if (brc.Error != null) return new HOutcome { Error = brc.Error };
                        return new HOutcome { Verified = brc.Verified, Items = brc.TotalReplaced, Info = brc.Info };
                    }
                },
                new HSpec {
                    Name = "combine_bodies", Actions = new[] { "combine_bodies" }, AssemblyOnly = false, Destructive = true,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var cb = await CombineBodies.Run(App, doc, intent, emit);
                        if (cb.Error != null) return new HOutcome { Error = cb.Error };
                        return new HOutcome { Verified = cb.Success, Items = cb.BodyCountAfter, Info = cb.Info };
                    }
                },
                new HSpec {
                    Name = "split_body", Actions = new[] { "split_body" }, AssemblyOnly = false, Destructive = true,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var sb = await SplitBody.Run(App, doc, intent, emit);
                        if (sb.Error != null) return new HOutcome { Error = sb.Error };
                        return new HOutcome { Verified = sb.Split, Items = sb.BodyCountAfter, Info = sb.BodyCountBefore + " -> " + sb.BodyCountAfter + " bodies (" + sb.PlaneUsed + ")" };
                    }
                },
                new HSpec {
                    Name = "delete_replace_face", Actions = new[] { "delete_replace_face" }, AssemblyOnly = false, Destructive = true,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var drf = await DeleteReplaceFace.Run(App, doc, intent, emit);
                        if (drf.Error != null) return new HOutcome { Error = drf.Error };
                        return new HOutcome { Verified = drf.Verified, Items = drf.FaceCountAfter, Info = drf.Info };
                    }
                },
                new HSpec {
                    Name = "run_dfm_checks", Actions = new[] { "run_dfm_checks" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var dfm = await RunDfmChecks.Run(App, doc, intent, emit);
                        if (dfm.Error != null) return new HOutcome { Error = dfm.Error };
                        return new HOutcome { Verified = dfm.Verified, Items = dfm.HolesChecked, Info = dfm.Info };
                    }
                },
                new HSpec {
                    Name = "export_bom", Actions = new[] { "export_bom" }, AssemblyOnly = true, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var eb = await ExportBom.Run(App, doc, intent, emit);
                        if (eb.Error != null) return new HOutcome { Error = eb.Error };
                        return new HOutcome { Verified = eb.Verified, Items = eb.RowCount, Info = eb.Info };
                    }
                },
                new HSpec {
                    Name = "run_import_diagnostics", Actions = new[] { "run_import_diagnostics" }, AssemblyOnly = false, Destructive = true,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var rid = await RunImportDiagnostics.Run(App, doc, intent, emit);
                        if (rid.Error != null) return new HOutcome { Error = rid.Error };
                        return new HOutcome { Verified = rid.Ran, Items = rid.DiagnosisReturn, Info = rid.Info };
                    }
                },
                new HSpec {
                    Name = "save_bodies_as_parts", Actions = new[] { "save_bodies_as_parts" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var sbp = await SaveBodiesAsParts.Run(App, doc, intent, emit);
                        if (sbp.Error != null) return new HOutcome { Error = sbp.Error };
                        return new HOutcome { Verified = sbp.Verified, Items = sbp.Created + sbp.AlreadyThere, Info = sbp.Info };
                    }
                },
                new HSpec {
                    Name = "check_geometry_errors", Actions = new[] { "check_geometry_errors" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var cge = await CheckGeometryErrors.Run(App, doc, intent, emit);
                        if (cge.Error != null) return new HOutcome { Error = cge.Error };
                        return new HOutcome { Verified = cge.Checked, Items = cge.TotalGaps, Info = cge.Info };
                    }
                },
                new HSpec {
                    Name = "add_center_marks", Actions = new[] { "add_center_marks" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var acm = await AddCenterMarks.Run(App, doc, intent, emit);
                        if (acm.Error != null) return new HOutcome { Error = acm.Error };
                        return new HOutcome { Verified = acm.Verified, Items = acm.CenterMarksAfter, Info = acm.Info };
                    }
                },
                new HSpec {
                    Name = "replace_sheet_format", Actions = new[] { "replace_sheet_format" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var rsf = await ReplaceSheetFormat.Run(App, doc, intent, emit);
                        if (rsf.Error != null) return new HOutcome { Error = rsf.Error };
                        return new HOutcome { Verified = rsf.Verified, Info = rsf.FormatBefore + " -> " + rsf.FormatAfter };
                    }
                },
                new HSpec {
                    Name = "update_revision_table", Actions = new[] { "update_revision_table" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var urt = await UpdateRevisionTable.Run(App, doc, intent, emit);
                        if (urt.Error != null) return new HOutcome { Error = urt.Error };
                        return new HOutcome { Verified = urt.Verified, Items = urt.RowsAfter, Info = urt.Info };
                    }
                },
                new HSpec {
                    Name = "check_drafting_standards", Actions = new[] { "check_drafting_standards" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var cds = await CheckDraftingStandards.Run(App, doc, intent, emit);
                        if (cds.Error != null) return new HOutcome { Error = cds.Error };
                        return new HOutcome { Verified = true, Items = cds.TotalDimensions, Info = cds.Info };
                    }
                },
                // Assembly-diagnostic READ family + dissolve_subassembly WRITE (live-dispatch gap fix 2026-07-29) —
                // each fenced by its own distinctive noun (floating/over-defined/duplicate/symmetry/dissolve), no
                // ordering constraints among them. check_part_symmetry is the only PART-level one.
                new HSpec {
                    Name = "find_floating_components", Actions = new[] { "find_floating_components" }, AssemblyOnly = true, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var ff = await FindFloating.Run(App, doc, intent, emit);
                        if (ff.Error != null) return new HOutcome { Error = ff.Error };
                        return new HOutcome { Verified = true, Items = ff.Floating, Info = ff.Info };
                    }
                },
                new HSpec {
                    Name = "find_over_defined_components", Actions = new[] { "find_over_defined_components" }, AssemblyOnly = true, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var fo = await FindOverDefined.Run(App, doc, intent, emit);
                        if (fo.Error != null) return new HOutcome { Error = fo.Error };
                        return new HOutcome { Verified = true, Items = fo.OverDefined, Info = fo.Info };
                    }
                },
                new HSpec {
                    Name = "find_duplicate_components", Actions = new[] { "find_duplicate_components" }, AssemblyOnly = true, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var fd = await FindDuplicateComponents.Run(App, doc, intent, emit);
                        if (fd.Error != null) return new HOutcome { Error = fd.Error };
                        return new HOutcome { Verified = fd.Success, Items = fd.DuplicateGroups, Info = fd.Info };
                    }
                },
                new HSpec {
                    Name = "resolve_duplicate_paths", Actions = new[] { "resolve_duplicate_paths" }, AssemblyOnly = true, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var rd = await ResolveDuplicatePaths.Run(App, doc, intent, emit);
                        if (rd.Error != null) return new HOutcome { Error = rd.Error };
                        return new HOutcome { Verified = rd.Success, Items = rd.GroupCount, Info = rd.Info };
                    }
                },
                new HSpec {
                    Name = "check_part_symmetry", Actions = new[] { "check_part_symmetry" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var cps = await CheckPartSymmetry.Run(App, doc, intent, emit);
                        if (cps.Error != null) return new HOutcome { Error = cps.Error };
                        return new HOutcome { Verified = true, Items = cps.SymmetryPlanes, Info = cps.Info };
                    }
                },
                new HSpec {
                    Name = "dissolve_subassembly", Actions = new[] { "dissolve_subassembly" }, AssemblyOnly = true, Destructive = true,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var ds = await DissolveSubassembly.Run(App, doc, intent, emit);
                        if (ds.Error != null) return new HOutcome { Error = ds.Error };
                        return new HOutcome { Verified = ds.Verified, Items = ds.ChildrenPromoted, Info = ds.Info };
                    }
                },
                new HSpec {
                    Name = "set_subassembly_flexibility", Actions = new[] { "set_subassembly_flexibility" }, AssemblyOnly = true, Destructive = true,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var sf = await SetSubassemblyFlexibility.Run(App, doc, intent, emit);
                        if (sf.Error != null) return new HOutcome { Error = sf.Error };
                        return new HOutcome { Verified = sf.Verified, Items = sf.StateAfter, Info = sf.Info };
                    }
                },
                new HSpec {
                    Name = "repair_exploded_view", Actions = new[] { "repair_exploded_view" }, AssemblyOnly = true, Destructive = true,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var rv = await RepairExplodedView.Run(App, doc, intent, emit);
                        if (rv.Error != null) return new HOutcome { Error = rv.Error };
                        return new HOutcome { Verified = rv.Verified, Items = rv.RepairedNames.Count, Info = rv.Info };
                    }
                },
                new HSpec {
                    // Part-only (AssemblyOnly=false lets PARTs through) — the handler refuses an assembly honestly
                    // (design tables live on parts/assemblies in general, but this build's fixture and the only
                    // proven live path are part-scoped).
                    Name = "manage_design_table", Actions = new[] { "manage_design_table" }, AssemblyOnly = false, Destructive = true,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var mv = await ManageDesignTable.Run(App, doc, intent, emit);
                        if (mv.Error != null) return new HOutcome { Error = mv.Error };
                        return new HOutcome { Verified = mv.Verified, Items = mv.Rows.Count, Info = mv.Info };
                    }
                },
                new HSpec {
                    // Part-only (AssemblyOnly=false lets PARTs through) — only parts have sheet bodies to patch.
                    Name = "fill_surface", Actions = new[] { "fill_surface" }, AssemblyOnly = false, Destructive = true,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var fv = await FillSurface.Run(App, doc, intent, emit);
                        if (fv.Error != null) return new HOutcome { Error = fv.Error };
                        return new HOutcome { Verified = fv.Verified, Items = fv.RimsFilled, Info = fv.Info };
                    }
                },
                // DescribeGeometry — READ: semantic readout of the currently SELECTED face (shape family,
                // area/diameter/height/orientation/concavity). Never modifies geometry (an optional embedded
                // "describe the X face"/"describe the hole" sub-command may select, same as select_face would).
                new HSpec {
                    Name = "describe_geometry", Actions = new[] { "describe_geometry" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var dg = await DescribeGeometry.Run(App, doc, intent, emit);
                        if (dg.Error != null) return new HOutcome { Error = dg.Error };
                        return new HOutcome { Verified = dg.Success, Items = 0, Info = dg.Info };
                    }
                },
                // HighlightEntities — WRITE-of-state: selects+zooms the target face (top/bottom/left/right/
                // largest/hole) so the user sees exactly what a follow-up command is about to touch. Never a
                // permanent color/appearance write (that's apply_appearance) — one ClearSelection2 undoes it.
                new HSpec {
                    Name = "highlight_entities", Actions = new[] { "highlight_entities" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var he = await HighlightEntities.Run(App, doc, intent, emit);
                        if (he.Error != null) return new HOutcome { Error = he.Error };
                        return new HOutcome { Verified = he.Success, Items = 0, Info = he.Info };
                    }
                },
                // HandleLockedFiles — READ pre-flight: is the open document's own file read-only/locked/
                // permission-denied. v1 scope is the open doc's own path only (not a batch/dependency scan).
                new HSpec {
                    Name = "handle_locked_files", Actions = new[] { "handle_locked_files" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var lf = await HandleLockedFiles.Run(App, doc, intent, emit);
                        if (lf.Error != null) return new HOutcome { Error = lf.Error };
                        return new HOutcome { Verified = lf.Success, Items = lf.Blocked, Info = lf.Info };
                    }
                },
                // DetectInContextWrites — READ, attributes external-file risk to the specific feature that
                // carries it (in-context / derived-part / "Insert Part" style features), not just a document-
                // level dependency list.
                new HSpec {
                    Name = "detect_in_context_writes", Actions = new[] { "detect_in_context_writes" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var icw = await DetectInContextWrites.Run(App, doc, intent, emit);
                        if (icw.Error != null) return new HOutcome { Error = icw.Error };
                        return new HOutcome { Verified = icw.Success, Items = icw.InContextFeatureCount, Info = icw.Info };
                    }
                },
                // HandleUnknownFeatures — READ, detects third-party/plugin (macro-feature-typed) entries in the
                // tree; never modifies them, only reports so other operations can route around them.
                new HSpec {
                    Name = "handle_unknown_features", Actions = new[] { "handle_unknown_features" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var huf = await HandleUnknownFeatures.Run(App, doc, intent, emit);
                        if (huf.Error != null) return new HOutcome { Error = huf.Error };
                        return new HOutcome { Verified = huf.Success, Items = huf.UnknownFeatureCount, Info = huf.Info };
                    }
                },
                // HandleAssemblyFeatures — READ, assembly-only: cuts/holes/fillets/chamfers/drafts created
                // directly at the ASSEMBLY level (not inside any component part) — they don't travel with a
                // mirrored/patterned/exported component, so flag them before that operation runs.
                new HSpec {
                    Name = "handle_assembly_features", Actions = new[] { "handle_assembly_features" }, AssemblyOnly = true, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var haf = await HandleAssemblyFeatures.Run(App, doc, intent, emit);
                        if (haf.Error != null) return new HOutcome { Error = haf.Error };
                        return new HOutcome { Verified = haf.Success, Items = haf.AssemblyFeatureCount, Info = haf.Info };
                    }
                },
                // TraceDerivedParts — READ, walks the external-file-reference chain recursively across
                // documents to map full derived-part lineage (not just the one-hop DetectInContextWrites answer).
                new HSpec {
                    Name = "trace_derived_parts", Actions = new[] { "trace_derived_parts" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var tdp = await TraceDerivedParts.Run(App, doc, intent, emit);
                        if (tdp.Error != null) return new HOutcome { Error = tdp.Error };
                        return new HOutcome { Verified = tdp.Success, Items = tdp.ChainDepth, Info = tdp.Info };
                    }
                },
                // RecoverAutosave — READ, post-crash: locate the newest autosave/backup copy in SolidWorks' OWN
                // configured recovery folders, diff it (byte hash only, never opened in SW) against the last
                // saved file, and OFFER recovery. Never auto-overwrites anything.
                new HSpec {
                    Name = "recover_autosave", Actions = new[] { "recover_autosave" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var ra = await RecoverAutosave.Run(App, doc, intent, emit);
                        if (ra.Error != null) return new HOutcome { Error = ra.Error };
                        return new HOutcome { Verified = ra.Success, Items = ra.Checked, Info = ra.Info };
                    }
                },
                // HandleConfigExplosion — READ guard: refuses an "activate/rebuild ALL/EVERY configuration" bulk
                // ask on a huge-config-count file (Toolbox master / design-table monster), steering toward a
                // single named-config switch instead. Never performs any config operation itself.
                new HSpec {
                    Name = "handle_config_explosion", Actions = new[] { "handle_config_explosion" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var hce = await HandleConfigExplosion.Run(App, doc, intent, emit);
                        if (hce.Error != null) return new HOutcome { Error = hce.Error };
                        return new HOutcome { Verified = hce.Success, Items = hce.ConfigCount, Info = hce.Info };
                    }
                },
                // DetectSimulationArtifacts — READ guard: classifies weld-bead/belt-chain feature-tree entries
                // so a follow-up geometry op (mirror/pattern/defeature) can route around them. Never modifies.
                new HSpec {
                    Name = "detect_simulation_artifacts", Actions = new[] { "detect_simulation_artifacts" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var dsa = await DetectSimulationArtifacts.Run(App, doc, intent, emit);
                        if (dsa.Error != null) return new HOutcome { Error = dsa.Error };
                        return new HOutcome { Verified = dsa.Success, Items = dsa.ArtifactCount, Info = dsa.Info };
                    }
                },
                // QuarantineFile — classifies an EXTERNALLY-reported attempt history (pass/fail outcomes in the
                // request text) as a poltergeist (inconsistent) or not; writes a marker sidecar only when
                // quarantining, never auto-retries.
                new HSpec {
                    Name = "quarantine_file", Actions = new[] { "quarantine_file" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var qf = await QuarantineFile.Run(App, doc, intent, emit);
                        if (qf.Error != null) return new HOutcome { Error = qf.Error };
                        return new HOutcome { Verified = qf.Success, Items = qf.PassCount + qf.FailCount, Info = qf.Info };
                    }
                },
                new HSpec {
                    // Part-or-assembly at the pipeline (AssemblyOnly=false lets PARTs through); the handler refuses
                    // an assembly honestly (only parts have sheet bodies to knit).
                    Name = "knit_surfaces_to_solid", Actions = new[] { "knit_surfaces_to_solid" }, AssemblyOnly = false, Destructive = true,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var kv = await KnitSurfacesToSolid.Run(App, doc, intent, emit);
                        if (kv.Error != null) return new HOutcome { Error = kv.Error };
                        return new HOutcome { Verified = kv.Verified, Items = kv.SolidBodyCountAfter, Info = kv.Info };
                    }
                },
                new HSpec {
                    // Drawing-only at the pipeline (AssemblyOnly=false lets DRAWINGs through); the handler refuses
                    // a non-drawing honestly.
                    Name = "arrange_drawing_annotations", Actions = new[] { "arrange_drawing_annotations" }, AssemblyOnly = false, Destructive = true,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var av = await ArrangeDrawingAnnotations.Run(App, doc, intent, emit);
                        if (av.Error != null) return new HOutcome { Error = av.Error };
                        return new HOutcome { Verified = av.Verified, Items = av.RepositionedCount, Info = av.Info };
                    }
                },
                // Feature-search READ family (live-dispatch gap fix 2026-07-29) — each fenced by its own required
                // vocabulary (feature-info phrase/param+feature-word, type keyword, called/named, where-used
                // phrase), so no ordering constraints among them.
                new HSpec {
                    Name = "get_feature_info", Actions = new[] { "get_feature_info" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var gfi = await GetFeatureInfo.Run(App, doc, intent, emit);
                        if (gfi.Error != null) return new HOutcome { Error = gfi.Error };
                        return new HOutcome { Verified = true, Items = gfi.Count, Info = gfi.Info };
                    }
                },
                new HSpec {
                    Name = "find_features_by_type", Actions = new[] { "find_features_by_type" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var fft = await FindFeaturesByType.Run(App, doc, intent, emit);
                        if (fft.Error != null) return new HOutcome { Error = fft.Error };
                        return new HOutcome { Verified = true, Items = fft.Matched, Info = fft.Info };
                    }
                },
                new HSpec {
                    Name = "find_feature_by_name", Actions = new[] { "find_feature_by_name" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var ffn = await FindFeatureByName.Run(App, doc, intent, emit);
                        if (ffn.Error != null) return new HOutcome { Error = ffn.Error };
                        return new HOutcome { Verified = ffn.Match != null, Items = ffn.Candidates, Info = ffn.Info };
                    }
                },
                new HSpec {
                    Name = "find_where_used", Actions = new[] { "find_where_used" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var fwu = await FindWhereUsed.Run(App, doc, intent, emit);
                        if (fwu.Error != null) return new HOutcome { Error = fwu.Error };
                        return new HOutcome { Verified = true, Items = fwu.ParentAssemblies + fwu.ParentDrawings, Info = fwu.Info };
                    }
                },
                // Getter READ family (live-dispatch gap fix 2026-07-29) — 17 simple property/info readers, each
                // fenced by its own required vocabulary. get_custom_properties (GetProperties) has no IsIntent of
                // its own (cloud-classification only, same category as the already-live suppress_feature/set_fixed)
                // so it gets an HSpec but no local intercept/LocalActionFor line — this alone still fixes it since
                // the missing piece was Specs() registration. get_cut_list must be checked BEFORE the existing
                // GetBodies intercept further down this function (it's the more specific "unique/grouped bodies"
                // reading) — this insertion point already runs earlier in RunViaPipeline, so that's satisfied.
                new HSpec {
                    Name = "get_custom_properties", Actions = new[] { "get_custom_properties" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var gp = await GetProperties.Run(App, doc, intent, emit);
                        if (gp.Error != null) return new HOutcome { Error = gp.Error };
                        return new HOutcome { Verified = true, Items = gp.Total, Info = gp.Info };
                    }
                },
                new HSpec {
                    Name = "get_component_transform", Actions = new[] { "get_component_transform" }, AssemblyOnly = true, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var gct = await GetComponentTransform.Run(App, doc, intent, emit);
                        if (gct.Error != null) return new HOutcome { Error = gct.Error };
                        return new HOutcome { Verified = true, Items = gct.Count, Info = gct.Info };
                    }
                },
                new HSpec {
                    Name = "get_mate_info", Actions = new[] { "get_mate_info" }, AssemblyOnly = true, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var gmi = await GetMateInfo.Run(App, doc, intent, emit);
                        if (gmi.Error != null) return new HOutcome { Error = gmi.Error };
                        return new HOutcome { Verified = true, Items = gmi.EntityCount, Info = gmi.Info };
                    }
                },
                new HSpec {
                    Name = "get_active_document", Actions = new[] { "get_active_document" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var gad = await GetActiveDocument.Run(App, doc, intent, emit);
                        if (gad.Error != null) return new HOutcome { Error = gad.Error };
                        return new HOutcome { Verified = true, Items = 1, Info = gad.Info };
                    }
                },
                new HSpec {
                    Name = "get_face_normal", Actions = new[] { "get_face_normal" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var gf = await GetFaces.Run(App, doc, intent, emit);
                        if (gf.Error != null) return new HOutcome { Error = gf.Error };
                        return new HOutcome { Verified = true, Items = gf.Total, Info = gf.Info };
                    }
                },
                new HSpec {
                    Name = "get_material_density", Actions = new[] { "get_material_density" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var gmd = await GetMaterialDensity.Run(App, doc, intent, emit);
                        if (gmd.Error != null) return new HOutcome { Error = gmd.Error };
                        return new HOutcome { Verified = true, Items = 1, Info = gmd.Info };
                    }
                },
                new HSpec {
                    Name = "get_sheet_metal_properties", Actions = new[] { "get_sheet_metal_properties" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var gsmp = await GetSheetMetalProps.Run(App, doc, intent, emit);
                        if (gsmp.Error != null) return new HOutcome { Error = gsmp.Error };
                        return new HOutcome { Verified = true, Items = gsmp.BendCount, Info = gsmp.Info };
                    }
                },
                new HSpec {
                    Name = "set_sheet_metal_thickness", Actions = new[] { "set_sheet_metal_thickness" }, AssemblyOnly = false, Destructive = true,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var ssmt = await SetSheetMetalThickness.Run(App, doc, intent, emit);
                        if (ssmt.Error != null) return new HOutcome { Error = ssmt.Error };
                        return new HOutcome { Verified = ssmt.Verified, Items = 1, Info = ssmt.Info };
                    }
                },
                new HSpec {
                    Name = "get_part_number", Actions = new[] { "get_part_number" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var gpn = await GetPartNumber.Run(App, doc, intent, emit);
                        if (gpn.Error != null) return new HOutcome { Error = gpn.Error };
                        return new HOutcome { Verified = gpn.Found, Items = gpn.Found ? 1 : 0, Info = gpn.Info };
                    }
                },
                new HSpec {
                    Name = "get_appearance", Actions = new[] { "get_appearance" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var gap = await GetAppearance.Run(App, doc, intent, emit);
                        if (gap.Error != null) return new HOutcome { Error = gap.Error };
                        return new HOutcome { Verified = gap.HasColor, Items = gap.HasColor ? 1 : 0, Info = gap.Info };
                    }
                },
                new HSpec {
                    Name = "get_rebuild_errors", Actions = new[] { "get_rebuild_errors" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var gre = await GetRebuildErrors.Run(App, doc, intent, emit);
                        if (gre.Error != null) return new HOutcome { Error = gre.Error };
                        return new HOutcome { Verified = true, Items = gre.Total, Info = gre.Info };
                    }
                },
                new HSpec {
                    Name = "handle_rollback_bar", Actions = new[] { "handle_rollback_bar" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var hrb = await HandleRollbackBar.Run(App, doc, intent, emit);
                        if (hrb.Error != null) return new HOutcome { Error = hrb.Error };
                        return new HOutcome { Verified = true, Items = hrb.RolledBackCount, Info = hrb.Info };
                    }
                },
                new HSpec {
                    Name = "get_component_mass", Actions = new[] { "get_component_mass" }, AssemblyOnly = true, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var gcm = await GetComponentMass.Run(App, doc, intent, emit);
                        if (gcm.Error != null) return new HOutcome { Error = gcm.Error };
                        return new HOutcome { Verified = true, Items = gcm.UniqueParts, Info = gcm.Info };
                    }
                },
                new HSpec {
                    Name = "get_sketch_info", Actions = new[] { "get_sketch_info" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var gs = await GetSketches.Run(App, doc, intent, emit);
                        if (gs.Error != null) return new HOutcome { Error = gs.Error };
                        return new HOutcome { Verified = true, Items = gs.SketchCount, Info = gs.Info };
                    }
                },
                // Sketch-diagnostic WRITE/READ trio (live-dispatch gap fix 2026-07-29) — must be registered so
                // their RunViaPipeline intercepts (placed BEFORE get_sketch_info's, per Harness.cs's proven order)
                // can resolve them.
                new HSpec {
                    Name = "fully_define_sketch", Actions = new[] { "fully_define_sketch" }, AssemblyOnly = false, Destructive = true,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var fds = await FullyDefineSketch.Run(App, doc, intent, emit);
                        if (fds.NeedsConfirm) return new HOutcome { AskedConfirm = true, Question = fds.Question };
                        if (fds.Error != null) return new HOutcome { Error = fds.Error };
                        return new HOutcome { Verified = fds.Verified, Items = fds.Defined, Info = fds.Info };
                    }
                },
                new HSpec {
                    Name = "diagnose_sketch", Actions = new[] { "diagnose_sketch" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var ds = await DiagnoseSketch.Run(App, doc, intent, emit);
                        if (ds.Error != null) return new HOutcome { Error = ds.Error };
                        return new HOutcome { Verified = true, Items = ds.SketchCount, Info = ds.Info };
                    }
                },
                new HSpec {
                    Name = "detect_shared_sketches", Actions = new[] { "detect_shared_sketches" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var dss = await DetectSharedSketches.Run(App, doc, intent, emit);
                        if (dss.Error != null) return new HOutcome { Error = dss.Error };
                        return new HOutcome { Verified = true, Items = dss.SharedCount, Info = dss.Info };
                    }
                },
                new HSpec {
                    Name = "get_pattern_info", Actions = new[] { "get_pattern_info" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var gpi = await GetPatternInfo.Run(App, doc, intent, emit);
                        if (gpi.Error != null) return new HOutcome { Error = gpi.Error };
                        return new HOutcome { Verified = true, Items = gpi.PatternCount, Info = gpi.Info };
                    }
                },
                new HSpec {
                    Name = "get_cut_list", Actions = new[] { "get_cut_list" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var gcl = await GetCutList.Run(App, doc, intent, emit);
                        if (gcl.Error != null) return new HOutcome { Error = gcl.Error };
                        return new HOutcome { Verified = true, Items = gcl.UniqueGroups, Info = gcl.Info };
                    }
                },
                new HSpec {
                    Name = "get_file_references", Actions = new[] { "get_file_references" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var gfr = await GetFileReferences.Run(App, doc, intent, emit);
                        if (gfr.Error != null) return new HOutcome { Error = gfr.Error };
                        return new HOutcome { Verified = true, Items = gfr.UniqueFiles, Info = gfr.Info };
                    }
                },
                // RepairMate (tool 62, WRITE): for the active assembly, find mates whose referenced component's file
                // no longer resolves on disk and re-attach them to a fuzzy-resolved, DIFFERENTLY-NAMED replacement
                // in the assembly's own folder (prefix match after stripping copy/moved/old/(n) suffixes), via the
                // same proven ReplaceComponents swap 31/128/129/132 use. Distinct from repair_missing_references
                // (132, exact-filename search only, component-level report) — this operates at MATE granularity and
                // trusts a differently-named replacement. Fail-closed: verified every previously-broken mate is
                // independently re-walked and confirmed its referenced component now resolves.
                new HSpec {
                    Name = "repair_mate", Actions = new[] { "repair_mate" }, AssemblyOnly = true, Destructive = true,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var rm = await RepairMate.Run(App, doc, intent, emit);
                        if (rm.Error != null) return new HOutcome { Error = rm.Error };
                        return new HOutcome { Verified = rm.Verified, Items = rm.Repaired, Info = rm.Info };
                    }
                },
                // RepairMissingReferences (tool 132, WRITE): for every component whose stored file reference no
                // longer resolves on disk, search (an explicit folder from the request, else the assembly's own
                // folder + immediate subfolders) for a same-named file and re-point it via the same proven
                // ReplaceComponents swap 128/129 use. Fail-closed: verified every found-missing component is
                // independently confirmed re-pointed at its resolved path.
                new HSpec {
                    Name = "repair_missing_references", Actions = new[] { "repair_missing_references" }, AssemblyOnly = true, Destructive = true,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var rmr = await RepairMissingReferences.Run(App, doc, intent, emit);
                        if (rmr.Error != null) return new HOutcome { Error = rmr.Error };
                        return new HOutcome { Verified = rmr.Verified, Items = rmr.Repaired, Info = rmr.Info };
                    }
                },
                // UpdateSheetReferences (tool 114, WRITE): sibling of repair_missing_references but for a DRAWING's
                // views instead of an assembly's components — for every view whose referenced model no longer
                // resolves on disk, search (explicit folder from the request, else the drawing's own folder +
                // immediate subfolders) for a same-named file and re-point it via IDrawingDoc.ReplaceViewModel.
                // AssemblyOnly=false (same convention as create_drawing/get_drawing_views): the handler itself is
                // the doc-type gate (drawing required), not the pipeline.
                new HSpec {
                    Name = "update_sheet_references", Actions = new[] { "update_sheet_references" }, AssemblyOnly = false, Destructive = true,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var usr = await UpdateSheetReferences.Run(App, doc, intent, emit);
                        if (usr.Error != null) return new HOutcome { Error = usr.Error };
                        return new HOutcome { Verified = usr.Verified, Items = usr.Repaired, Info = usr.Info };
                    }
                },
                // InsertBomTable (tool 113, WRITE): for the active drawing's assembly view, attach a native
                // linked BOM table (IView.InsertBomTable2) — zero prior art in this codebase, template resolved
                // via the swFileLocationsBOMTemplates preference + install-relative fallback (same shape as
                // create_drawing's own template resolution). AssemblyOnly=false — the handler itself gates on
                // needing a drawing document, not the pipeline.
                new HSpec {
                    Name = "insert_bom_table", Actions = new[] { "insert_bom_table" }, AssemblyOnly = false, Destructive = true,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var ibt = await InsertBomTable.Run(App, doc, intent, emit);
                        if (ibt.Error != null) return new HOutcome { Error = ibt.Error };
                        return new HOutcome { Verified = ibt.Verified, Items = ibt.ComponentTypes, Info = ibt.Info };
                    }
                },
                // CleanBomTable (tool 161, WRITE): for the active drawing's existing BOM table, removes orphaned
                // rows (zero live components backing them) and duplicate Part-Number rows, then resorts (QTY
                // descending if a quantity column exists, else Item Number ascending) via BomFeat -> IBomFeature
                // -> IGetTableAnnotations(1) -> IBomTableAnnotation.Sort — a different API surface than the dead
                // IView.GetBomTable() accessor. AssemblyOnly=false — the handler itself gates on needing a
                // drawing with an existing BOM table, not the pipeline.
                new HSpec {
                    Name = "clean_bom_table", Actions = new[] { "clean_bom_table" }, AssemblyOnly = false, Destructive = true,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var cbt = await CleanBomTable.Run(App, doc, intent, emit);
                        if (cbt.Error != null) return new HOutcome { Error = cbt.Error };
                        return new HOutcome { Verified = cbt.Verified, Items = cbt.RemovedOrphaned + cbt.RemovedDuplicate, Info = cbt.Info };
                    }
                },
                // RepairBalloonReferences (tool 160, WRITE): for the active drawing's assembly view, removes
                // balloons whose leader has detached from the model then restores coverage via AutoBalloon5
                // (which skips components that already have a live balloon). AssemblyOnly=false — the handler
                // itself gates on needing a drawing with an assembly view, not the pipeline.
                new HSpec {
                    Name = "repair_balloon_references", Actions = new[] { "repair_balloon_references" }, AssemblyOnly = false, Destructive = true,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var rbr = await RepairBalloonReferences.Run(App, doc, intent, emit);
                        if (rbr.Error != null) return new HOutcome { Error = rbr.Error };
                        return new HOutcome { Verified = rbr.Verified, Items = rbr.BalloonsAfter, Info = rbr.Info };
                    }
                },
                // InsertSectionView (tool 104, WRITE): cuts a section view from an existing base view on the
                // active drawing's sheet — sketches a vertical cut line through the base view's outline midpoint,
                // selects it, then IDrawingDoc.CreateSectionViewAt5. AssemblyOnly=false — the handler itself gates
                // on needing a drawing document, not the pipeline.
                new HSpec {
                    Name = "insert_section_view", Actions = new[] { "insert_section_view" }, AssemblyOnly = false, Destructive = true,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var isv = await InsertSectionView.Run(App, doc, intent, emit);
                        if (isv.Error != null) return new HOutcome { Error = isv.Error };
                        return new HOutcome { Verified = isv.Verified, Items = isv.Verified ? 1 : 0, Info = isv.Info };
                    }
                },
                // InsertDetailView (tool 105, WRITE): circles a region on an existing base view and blows it up
                // into a separate scaled detail view — sketches+selects a circle, then IDrawingDoc.
                // CreateDetailViewAt3. AssemblyOnly=false — the handler itself gates on needing a drawing document.
                new HSpec {
                    Name = "insert_detail_view", Actions = new[] { "insert_detail_view" }, AssemblyOnly = false, Destructive = true,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var idv = await InsertDetailView.Run(App, doc, intent, emit);
                        if (idv.Error != null) return new HOutcome { Error = idv.Error };
                        return new HOutcome { Verified = idv.Verified, Items = idv.Verified ? 1 : 0, Info = idv.Info };
                    }
                },
                // AddDrawingDimension (tool 109, WRITE): a manual linear dimension between two EXISTING model
                // edges shown in a drawing view (picks the most-separated parallel edge pair, e.g. a plate's
                // left/right edge), via IModelDoc2.AddDimension2. AssemblyOnly=false — the handler itself gates
                // on needing a drawing document.
                new HSpec {
                    Name = "add_drawing_dimension", Actions = new[] { "add_drawing_dimension" }, AssemblyOnly = false, Destructive = true,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var add = await AddDrawingDimension.Run(App, doc, intent, emit);
                        if (add.Error != null) return new HOutcome { Error = add.Error };
                        return new HOutcome { Verified = add.Verified, Items = add.Verified ? 1 : 0, Info = add.Info };
                    }
                },
                // PackAndGo (tool 133, WRITE): bundles the active document plus every dependency into one folder
                // via the native pack-and-go API (rewrites references so the copy is self-contained), never
                // touches the source. Fail-closed: verified by reopening the packaged copy fresh.
                new HSpec {
                    Name = "pack_and_go", Actions = new[] { "pack_and_go" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var pag = await PackAndGo.Run(App, doc, intent, emit);
                        if (pag.Error != null) return new HOutcome { Error = pag.Error };
                        return new HOutcome { Verified = pag.SizesMatch && pag.SourceUnchanged && pag.Copied == pag.TotalToCopy, Items = pag.Copied, Info = pag.Copied + "/" + pag.TotalToCopy + " files packed to " + pag.DestFolder };
                    }
                },
                // RenameFileWithReferences (tool 128, WRITE): renames the active PART's own file on disk (true SaveAs,
                // no Copy flag, so the document's own identity moves) and relinks every parent assembly in the same
                // folder via the proven ReplaceComponents path. Fail-closed: verified new file present, old file gone,
                // every found parent independently confirmed pointing at the new path.
                new HSpec {
                    Name = "rename_file_with_references", Actions = new[] { "rename_file_with_references" }, AssemblyOnly = false, Destructive = true,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var rfr = await RenameFileWithReferences.Run(App, doc, intent, emit);
                        if (rfr.Error != null) return new HOutcome { Error = rfr.Error };
                        return new HOutcome { Verified = rfr.Verified, Items = rfr.ParentsRelinked, Info = rfr.Info };
                    }
                },
                // MoveFileWithReferences (tool 129, WRITE): moves the active PART's own file to a DIFFERENT folder
                // (true SaveAs, no Copy flag) and relinks every parent assembly that referenced it in its OLD folder
                // via the same proven ReplaceComponents path as tool 128. Fail-closed: verified new file present,
                // old file gone, every found parent independently confirmed pointing at the new path.
                new HSpec {
                    Name = "move_file_with_references", Actions = new[] { "move_file_with_references" }, AssemblyOnly = false, Destructive = true,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var mfr = await MoveFileWithReferences.Run(App, doc, intent, emit);
                        if (mfr.Error != null) return new HOutcome { Error = mfr.Error };
                        return new HOutcome { Verified = mfr.Verified, Items = mfr.ParentsRelinked, Info = mfr.Info };
                    }
                },
                new HSpec {
                    Name = "get_drawing_views", Actions = new[] { "get_drawing_views" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var gdv = await GetDrawingViews.Run(App, doc, intent, emit);
                        if (gdv.Error != null) return new HOutcome { Error = gdv.Error };
                        return new HOutcome { Verified = true, Items = gdv.ViewCount, Info = gdv.Info };
                    }
                },
                // ListDanglingDimensions (tool 110, READ): walks every view's display dimensions and reports which
                // are dangling, by name. Rebuilds the drawing itself first so a stale sheet never reads "0 dangling".
                new HSpec {
                    Name = "list_dangling_dimensions", Actions = new[] { "list_dangling_dimensions" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var ldd = await ListDanglingDimensions.Run(App, doc, intent, emit);
                        if (ldd.Error != null) return new HOutcome { Error = ldd.Error };
                        return new HOutcome { Verified = true, Items = ldd.DanglingCount, Info = ldd.Info };
                    }
                },
                new HSpec {
                    Name = "get_driving_dimensions", Actions = new[] { "get_driving_dimensions" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var gdd = await GetDrivingDimensions.Run(App, doc, intent, emit);
                        if (gdd.Error != null) return new HOutcome { Error = gdd.Error };
                        return new HOutcome { Verified = true, Items = gdd.DrivingCount, Info = gdd.Info };
                    }
                },
                // Sketch-to-solid feature-create WRITE family (live-dispatch gap fix 2026-07-29) — each fenced by
                // its own distinctive keyword (dome/helix/loft/revolve/sweep/thicken+surface). add_dome and
                // create_thicken are PARKED (dead APIs: InsertDome/thicken-surface are silent no-ops headless on
                // this build) but wiring them still gets a user an honest refusal instead of a cloud misroute;
                // add_helix/loft/revolve/sweep are LIVE.
                new HSpec {
                    Name = "add_revolve", Actions = new[] { "add_revolve" }, AssemblyOnly = false, Destructive = true,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var ar = await AddRevolve.Run(App, doc, intent, emit);
                        if (ar.Error != null) return new HOutcome { Error = ar.Error };
                        return new HOutcome { Verified = ar.Success, Items = ar.BodyCountAfter, Info = ar.Info };
                    }
                },
                new HSpec {
                    Name = "add_sweep", Actions = new[] { "add_sweep" }, AssemblyOnly = false, Destructive = true,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var asw = await AddSweep.Run(App, doc, intent, emit);
                        if (asw.Error != null) return new HOutcome { Error = asw.Error };
                        return new HOutcome { Verified = asw.Success, Items = asw.BodyCountAfter, Info = asw.Info };
                    }
                },
                new HSpec {
                    Name = "add_loft", Actions = new[] { "add_loft" }, AssemblyOnly = false, Destructive = true,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var al = await AddLoft.Run(App, doc, intent, emit);
                        if (al.Error != null) return new HOutcome { Error = al.Error };
                        return new HOutcome { Verified = al.Success, Items = al.BodyCountAfter, Info = al.Info };
                    }
                },
                new HSpec {
                    Name = "add_helix", Actions = new[] { "add_helix" }, AssemblyOnly = false, Destructive = true,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var ah = await AddHelix.Run(App, doc, intent, emit);
                        if (ah.Error != null) return new HOutcome { Error = ah.Error };
                        return new HOutcome { Verified = ah.Success, Items = 1, Info = ah.Info };
                    }
                },
                new HSpec {
                    Name = "add_dome", Actions = new[] { "add_dome" }, AssemblyOnly = false, Destructive = true,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var ad = await AddDome.Run(App, doc, intent, emit);
                        if (ad.Error != null) return new HOutcome { Error = ad.Error };
                        return new HOutcome { Verified = ad.Success, Items = 1, Info = ad.Info };
                    }
                },
                new HSpec {
                    Name = "create_thicken", Actions = new[] { "create_thicken" }, AssemblyOnly = false, Destructive = true,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var ct = await CreateThicken.Run(App, doc, intent, emit);
                        if (ct.Error != null) return new HOutcome { Error = ct.Error };
                        return new HOutcome { Verified = ct.Success, Items = 1, Info = ct.Info };
                    }
                },
                // Sketch-entity ADD WRITE family (live-dispatch gap fix 2026-07-29) — each fenced by its own
                // distinctive noun; add_sketch_arc explicitly excludes slot/construction/centerline so ordering
                // among these doesn't matter.
                new HSpec {
                    Name = "add_construction_geometry", Actions = new[] { "add_construction_geometry" }, AssemblyOnly = false, Destructive = true,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var acg = await AddConstructionGeometry.Run(App, doc, intent, emit);
                        if (acg.Error != null) return new HOutcome { Error = acg.Error };
                        return new HOutcome { Verified = acg.Success, Items = acg.ConstructionSegments, Info = acg.Info };
                    }
                },
                new HSpec {
                    Name = "add_sketch_arc", Actions = new[] { "add_sketch_arc" }, AssemblyOnly = false, Destructive = true,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var asa = await AddSketchArc.Run(App, doc, intent, emit);
                        if (asa.Error != null) return new HOutcome { Error = asa.Error };
                        return new HOutcome { Verified = asa.Success, Items = asa.ArcSegments, Info = asa.Info };
                    }
                },
                new HSpec {
                    Name = "add_sketch_entity", Actions = new[] { "add_sketch_entity" }, AssemblyOnly = false, Destructive = true,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var ase2 = await AddSketchEntity.Run(App, doc, intent, emit);
                        if (ase2.Error != null) return new HOutcome { Error = ase2.Error };
                        return new HOutcome { Verified = ase2.Applied, Items = ase2.Kind == "point" ? ase2.PointsAfter : ase2.SegmentsAfter, Info = ase2.Info };
                    }
                },
                new HSpec {
                    Name = "add_sketch_dimension", Actions = new[] { "add_sketch_dimension" }, AssemblyOnly = false, Destructive = true,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var asd = await AddSketchDimension.Run(App, doc, intent, emit);
                        if (asd.Error != null) return new HOutcome { Error = asd.Error };
                        return new HOutcome { Verified = asd.Applied, Items = (int)Math.Round(asd.AfterMm), Info = asd.Info };
                    }
                },
                new HSpec {
                    Name = "create_sketch", Actions = new[] { "create_sketch" }, AssemblyOnly = false, Destructive = true,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var cs = await CreateSketch.Run(App, doc, intent, emit);
                        if (cs.Error != null) return new HOutcome { Error = cs.Error };
                        return new HOutcome { Verified = cs.Applied, Info = cs.Info };
                    }
                },
                // CreateLayoutSketch (tool 231, WRITE): assembly-only master skeleton sketch, same shape as
                // create_sketch above but scoped to AssemblyOnly and gated on explicit layout/skeleton wording.
                new HSpec {
                    Name = "create_layout_sketch", Actions = new[] { "create_layout_sketch" }, AssemblyOnly = true, Destructive = true,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var cls = await CreateLayoutSketch.Run(App, doc, intent, emit);
                        if (cls.Error != null) return new HOutcome { Error = cls.Error };
                        return new HOutcome { Verified = cls.Applied, Info = cls.Info };
                    }
                },
                new HSpec {
                    Name = "create_3d_sketch", Actions = new[] { "create_3d_sketch" }, AssemblyOnly = false, Destructive = true,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var c3d = await Create3DSketch.Run(App, doc, intent, emit);
                        if (c3d.Error != null) return new HOutcome { Error = c3d.Error };
                        return new HOutcome { Verified = c3d.Applied, Info = c3d.Info };
                    }
                },
                new HSpec {
                    Name = "import_dxf_to_sketch", Actions = new[] { "import_dxf_to_sketch" }, AssemblyOnly = false, Destructive = true,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var idx = await ImportDxfToSketch.Run(App, doc, intent, emit);
                        if (idx.Error != null) return new HOutcome { Error = idx.Error };
                        return new HOutcome { Verified = idx.Applied, Items = idx.Segments, Info = idx.Info };
                    }
                },
                new HSpec {
                    Name = "add_sketch_ellipse", Actions = new[] { "add_sketch_ellipse" }, AssemblyOnly = false, Destructive = true,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var ase = await AddSketchEllipse.Run(App, doc, intent, emit);
                        if (ase.Error != null) return new HOutcome { Error = ase.Error };
                        return new HOutcome { Verified = ase.Success, Items = ase.EllipseSegments, Info = ase.Info };
                    }
                },
                new HSpec {
                    Name = "add_sketch_polygon", Actions = new[] { "add_sketch_polygon" }, AssemblyOnly = false, Destructive = true,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var asp = await AddSketchPolygon.Run(App, doc, intent, emit);
                        if (asp.Error != null) return new HOutcome { Error = asp.Error };
                        return new HOutcome { Verified = asp.Success, Items = asp.LineSegments, Info = asp.Info };
                    }
                },
                new HSpec {
                    Name = "add_sketch_slot", Actions = new[] { "add_sketch_slot" }, AssemblyOnly = false, Destructive = true,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var ass = await AddSketchSlot.Run(App, doc, intent, emit);
                        if (ass.Error != null) return new HOutcome { Error = ass.Error };
                        return new HOutcome { Verified = ass.Success, Items = ass.LineSegments, Info = ass.Info };
                    }
                },
                new HSpec {
                    Name = "add_sketch_spline", Actions = new[] { "add_sketch_spline" }, AssemblyOnly = false, Destructive = true,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var asl = await AddSketchSpline.Run(App, doc, intent, emit);
                        if (asl.Error != null) return new HOutcome { Error = asl.Error };
                        return new HOutcome { Verified = asl.Success, Items = asl.SplineSegments, Info = asl.Info };
                    }
                },
                new HSpec {
                    Name = "add_sketch_text", Actions = new[] { "add_sketch_text" }, AssemblyOnly = false, Destructive = true,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var ast = await AddSketchText.Run(App, doc, intent, emit);
                        if (ast.Error != null) return new HOutcome { Error = ast.Error };
                        return new HOutcome { Verified = ast.Success, Items = ast.TextSegments, Info = ast.Info };
                    }
                },
                // Measure READ pair + sketch-tool WRITE family (live-dispatch gap fix 2026-07-29) — measure_angle/
                // measure_distance each exclude the mate-add verbs so they never collide with add_angle_mate/
                // add_distance_mate; offset/convert/trim-extend/mirror/pattern are disjoint by verb.
                new HSpec {
                    Name = "measure_distance", Actions = new[] { "measure_distance" }, AssemblyOnly = true, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var md = await MeasureDistance.Run(App, doc, intent, emit);
                        if (md.Error != null) return new HOutcome { Error = md.Error };
                        return new HOutcome { Verified = md.DistanceMm >= 0, Items = 1, Info = md.Info };
                    }
                },
                new HSpec {
                    Name = "measure_angle", Actions = new[] { "measure_angle" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var ma = await MeasureAngle.Run(App, doc, intent, emit);
                        if (ma.Error != null) return new HOutcome { Error = ma.Error };
                        return new HOutcome { Verified = ma.AngleDeg >= 0, Items = 1, Info = ma.Info };
                    }
                },
                new HSpec {
                    Name = "offset_sketch_entities", Actions = new[] { "offset_sketch_entities" }, AssemblyOnly = false, Destructive = true,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var ose = await OffsetSketchEntities.Run(App, doc, intent, emit);
                        if (ose.Error != null) return new HOutcome { Error = ose.Error };
                        return new HOutcome { Verified = ose.Success, Items = ose.Segments, Info = ose.Info };
                    }
                },
                new HSpec {
                    Name = "convert_entities", Actions = new[] { "convert_entities" }, AssemblyOnly = false, Destructive = true,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var ce = await ConvertEntities.Run(App, doc, intent, emit);
                        if (ce.Error != null) return new HOutcome { Error = ce.Error };
                        return new HOutcome { Verified = ce.Success, Items = ce.Segments, Info = ce.Info };
                    }
                },
                new HSpec {
                    Name = "trim_extend", Actions = new[] { "trim_extend" }, AssemblyOnly = false, Destructive = true,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var te = await TrimExtendSketch.Run(App, doc, intent, emit);
                        if (te.Error != null) return new HOutcome { Error = te.Error };
                        return new HOutcome { Verified = te.Success, Items = te.Segments, Info = te.Info };
                    }
                },
                new HSpec {
                    Name = "mirror_sketch_entities", Actions = new[] { "mirror_sketch_entities" }, AssemblyOnly = false, Destructive = true,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var mse = await MirrorSketchEntities.Run(App, doc, intent, emit);
                        if (mse.Error != null) return new HOutcome { Error = mse.Error };
                        return new HOutcome { Verified = mse.Success, Items = mse.Segments, Info = mse.Info };
                    }
                },
                new HSpec {
                    Name = "pattern_sketch_entities", Actions = new[] { "pattern_sketch_entities" }, AssemblyOnly = false, Destructive = true,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var pse = await PatternSketchEntities.Run(App, doc, intent, emit);
                        if (pse.Error != null) return new HOutcome { Error = pse.Error };
                        return new HOutcome { Verified = pse.Success, Items = pse.Segments, Info = pse.Info };
                    }
                },
                // Reference-geometry + surface/rib feature-create WRITE family (live-dispatch gap fix 2026-07-29)
                // — each fenced by its own distinctive noun. create_variable_fillet is PARKED (dead API:
                // swFeatureFilletType_VariableRadius is a silent no-op headless on this build) but wired anyway for
                // an honest refusal; the rest are LIVE.
                new HSpec {
                    Name = "create_boundary_feature", Actions = new[] { "create_boundary_feature" }, AssemblyOnly = false, Destructive = true,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var cbf = await CreateBoundaryFeature.Run(App, doc, intent, emit);
                        if (cbf.Error != null) return new HOutcome { Error = cbf.Error };
                        return new HOutcome { Verified = cbf.Success, Items = cbf.BodyCountAfter, Info = cbf.Info };
                    }
                },
                new HSpec {
                    Name = "create_coordinate_system", Actions = new[] { "create_coordinate_system" }, AssemblyOnly = false, Destructive = true,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var ccs = await CreateCoordSys.Run(App, doc, intent, emit);
                        if (ccs.Error != null) return new HOutcome { Error = ccs.Error };
                        return new HOutcome { Verified = ccs.Verified, Items = ccs.CoordSystemsAfter, Info = ccs.Info };
                    }
                },
                new HSpec {
                    Name = "create_curve", Actions = new[] { "create_curve" }, AssemblyOnly = false, Destructive = true,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var cc = await CreateCurve.Run(App, doc, intent, emit);
                        if (cc.Error != null) return new HOutcome { Error = cc.Error };
                        return new HOutcome { Verified = cc.Success, Items = cc.Points, Info = cc.Info };
                    }
                },
                new HSpec {
                    Name = "create_extruded_surface", Actions = new[] { "create_extruded_surface" }, AssemblyOnly = false, Destructive = true,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var ces = await CreateExtrudedSurface.Run(App, doc, intent, emit);
                        if (ces.Error != null) return new HOutcome { Error = ces.Error };
                        return new HOutcome { Verified = ces.Success, Items = ces.SurfaceBodiesAfter, Info = ces.Info };
                    }
                },
                new HSpec {
                    Name = "create_reference_axis", Actions = new[] { "create_reference_axis" }, AssemblyOnly = false, Destructive = true,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var cra = await CreateRefAxis.Run(App, doc, intent, emit);
                        if (cra.Error != null) return new HOutcome { Error = cra.Error };
                        return new HOutcome { Verified = cra.Verified, Items = cra.AxesAfter, Info = cra.Info };
                    }
                },
                new HSpec {
                    Name = "create_rib", Actions = new[] { "create_rib" }, AssemblyOnly = false, Destructive = true,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var cr = await CreateRib.Run(App, doc, intent, emit);
                        if (cr.Error != null) return new HOutcome { Error = cr.Error };
                        return new HOutcome { Verified = cr.Success, Items = 1, Info = cr.Info };
                    }
                },
                new HSpec {
                    Name = "create_swept_lofted_surface", Actions = new[] { "create_swept_lofted_surface" }, AssemblyOnly = false, Destructive = true,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var css = await CreateSweptSurface.Run(App, doc, intent, emit);
                        if (css.Error != null) return new HOutcome { Error = css.Error };
                        return new HOutcome { Verified = css.Success, Items = css.SurfaceBodiesAfter, Info = css.Info };
                    }
                },
                new HSpec {
                    Name = "create_tree_folder", Actions = new[] { "create_tree_folder" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var ctf = await CreateTreeFolder.Run(App, doc, intent, emit);
                        if (ctf.NeedsConfirm) return new HOutcome { AskedConfirm = true, Question = ctf.Question };
                        if (ctf.Error != null) return new HOutcome { Error = ctf.Error };
                        return new HOutcome { Verified = ctf.FolderPresent, Items = 1, Info = ctf.Info };
                    }
                },
                new HSpec {
                    Name = "create_variable_fillet", Actions = new[] { "create_variable_fillet" }, AssemblyOnly = false, Destructive = true,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var cvf = await CreateVariableFillet.Run(App, doc, intent, emit);
                        if (cvf.Error != null) return new HOutcome { Error = cvf.Error };
                        return new HOutcome { Verified = cvf.Success, Items = 1, Info = cvf.Info };
                    }
                },
                // Final live-dispatch-gap sweep (2026-07-29) — batch WRITEs, diagnostic READs, and feature-edit
                // WRITEs, each fenced by its own required vocabulary. edit_feature_parameter is checked before the
                // generic set_dimension fallback (both this cluster and that one already satisfy that ordering by
                // position in this function). reorder_feature has no IsIntent of its own (cloud-classification
                // only, same category as get_custom_properties/suppress_feature) so it gets an HSpec but no local
                // intercept/LocalActionFor line.
                new HSpec {
                    Name = "select_components_by_filter", Actions = new[] { "select_components_by_filter" }, AssemblyOnly = true, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var sbf = await SelectByFilter.Run(App, doc, intent, emit);
                        if (sbf.Error != null) return new HOutcome { Error = sbf.Error };
                        return new HOutcome { Verified = true, Items = sbf.Matched, Info = sbf.Info };
                    }
                },
                new HSpec {
                    Name = "batch_update_materials", Actions = new[] { "batch_update_materials" }, AssemblyOnly = true, Destructive = true,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var bum = await BatchUpdateMaterials.Run(App, doc, intent, emit);
                        if (bum.Error != null) return new HOutcome { Error = bum.Error };
                        return new HOutcome { Verified = bum.Applied > 0 || bum.Skipped > 0, Items = bum.Applied, Info = bum.Info };
                    }
                },
                new HSpec {
                    Name = "batch_update_custom_properties", Actions = new[] { "batch_update_custom_properties" }, AssemblyOnly = true, Destructive = true,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var buc = await BatchUpdateCustomProperties.Run(App, doc, intent, emit);
                        if (buc.Error != null) return new HOutcome { Error = buc.Error };
                        return new HOutcome { Verified = buc.Applied > 0 || buc.Skipped > 0, Items = buc.Applied, Info = buc.Info };
                    }
                },
                new HSpec {
                    Name = "detect_ghost_references", Actions = new[] { "detect_ghost_references" }, AssemblyOnly = true, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var dgr = await DetectGhostReferences.Run(App, doc, intent, emit);
                        if (dgr.Error != null) return new HOutcome { Error = dgr.Error };
                        return new HOutcome { Verified = true, Items = dgr.GhostMates, Info = dgr.Info };
                    }
                },
                new HSpec {
                    Name = "validate_sheet_metal", Actions = new[] { "validate_sheet_metal" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var vsm = await ValidateSheetMetal.Run(App, doc, intent, emit);
                        if (vsm.Error != null) return new HOutcome { Error = vsm.Error };
                        return new HOutcome { Verified = true, Items = vsm.Violations, Info = vsm.Info };
                    }
                },
                new HSpec {
                    Name = "rebuild_document", Actions = new[] { "rebuild_document" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var rd = await RebuildDocument.Run(App, doc, intent, emit);
                        if (rd.Error != null) return new HOutcome { Error = rd.Error };
                        return new HOutcome { Verified = rd.Success, Items = rd.RebuildErrors, Info = rd.Info };
                    }
                },
                new HSpec {
                    Name = "compare_bodies", Actions = new[] { "compare_bodies" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var comp = await CompareBodies.Run(App, doc, intent, emit);
                        if (comp.Error != null) return new HOutcome { Error = comp.Error };
                        return new HOutcome { Verified = true, Items = comp.DuplicateGroups, Info = comp.Info };
                    }
                },
                new HSpec {
                    Name = "validate_scale_sanity", Actions = new[] { "validate_scale_sanity" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var vss = await ValidateScaleSanity.Run(App, doc, intent, emit);
                        if (vss.Error != null) return new HOutcome { Error = vss.Error };
                        return new HOutcome { Verified = true, Items = 1, Info = vss.Info };
                    }
                },
                new HSpec {
                    Name = "resolve_localized_names", Actions = new[] { "resolve_localized_names" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var rln = await ResolveLocalizedNames.Run(App, doc, intent, emit);
                        if (rln.Error != null) return new HOutcome { Error = rln.Error };
                        return new HOutcome { Verified = true, Items = rln.LocalizedCount, Info = rln.Info };
                    }
                },
                new HSpec {
                    Name = "batch_rename_features", Actions = new[] { "batch_rename_features" }, AssemblyOnly = false, Destructive = true,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var brf = await BatchRenameFeatures.Run(App, doc, intent, emit);
                        if (brf.NeedsConfirm) return new HOutcome { AskedConfirm = true, Question = brf.Question };
                        if (brf.Error != null) return new HOutcome { Error = brf.Error };
                        return new HOutcome { Verified = brf.Verified, Items = brf.Renamed, Info = brf.Info };
                    }
                },
                new HSpec {
                    Name = "reorder_feature", Actions = new[] { "reorder_feature" }, AssemblyOnly = false, Destructive = true,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var rf = await ReorderFeature.Run(App, doc, intent, emit);
                        if (rf.NeedsConfirm) return new HOutcome { AskedConfirm = true, Question = rf.Question };
                        if (rf.Error != null) return new HOutcome { Error = rf.Error };
                        return new HOutcome { Verified = rf.Verified, Items = 1, Info = rf.Info };
                    }
                },
                new HSpec {
                    Name = "set_component_lightweight", Actions = new[] { "set_component_lightweight", "set_component_resolved" }, AssemblyOnly = true, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var scl = await SetComponentLightweight.Run(App, doc, intent, emit);
                        if (scl.NeedsConfirm) return new HOutcome { AskedConfirm = true, Question = scl.Question };
                        if (scl.Error != null) return new HOutcome { Error = scl.Error };
                        return new HOutcome { Verified = scl.Verified, Items = scl.Changed, Info = scl.Info };
                    }
                },
                new HSpec {
                    Name = "set_rebuild_verification", Actions = new[] { "set_rebuild_verification" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var srv = await SetRebuildVerification.Run(App, doc, intent, emit);
                        if (srv.Error != null) return new HOutcome { Error = srv.Error };
                        return new HOutcome { Verified = srv.Verified, Items = 1, Info = srv.Info };
                    }
                },
                new HSpec {
                    Name = "edit_feature_parameter", Actions = new[] { "edit_feature_parameter" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var efp = await EditFeatureParameter.Run(App, doc, intent, emit);
                        if (efp.Error != null) return new HOutcome { Error = efp.Error };
                        return new HOutcome { Verified = efp.Verified, Items = 1, Info = efp.Info };
                    }
                },
                new HSpec {
                    Name = "edit_last_feature", Actions = new[] { "edit_last_feature" }, AssemblyOnly = false, Destructive = false,
                    Execute = async (doc, plan, op, intent, emit) => {
                        var elf = await EditLastFeature.Run(App, doc, intent, emit);
                        if (elf.Error != null) return new HOutcome { Error = elf.Error };
                        return new HOutcome { Verified = elf.Verified, Items = 1, Info = elf.Info };
                    }
                },
            };
            return _specs;
        }

        // ---- AUTHORITATIVE demo routing (Demos 9 & 10 panel FAILs): a natural "export this flat pattern as a DXF"
        //      or "rebuild the drawings and export to PDF" MUST reach FlatDxf / drawing_package and ACT — never the
        //      generic answer path (the "explain how to do it manually" anti-pattern) or a part-only guard. These two
        //      intents are unambiguous and specific-first (FlatDxf before drawing_package, both exclude each other),
        //      so we route them BEFORE the cloud parse — a cloud misclassification (a wrong-but-valid spec action
        //      slips past the local net) or a network hiccup can no longer break a recorded demo. Returns true iff
        //      handled. Runs AFTER pending-confirm + impact-followup, so those still resolve first. ----
        private async Task<bool> TryDemoRouteFirst(string intent)
        {
            var doc = App?.ActiveDoc as IModelDoc2;
            if (doc == null) return false;
            string i = (intent ?? "").Trim().ToLowerInvariant();
            if (i.Length == 0 || IsAffirmative(i)) return false;   // a bare "go" is a confirm, not a fresh command

            string action = null;
            if (FlatDxf.IsFlatDxfIntent(i)) action = "flat_dxf";                 // demo #10 (more specific than drawing_package)
            else if (DrawingPkg.IsDrawingPkgIntent(i)) action = "drawing_package"; // demo #9
            else if (Interfere.IsInterfereIntent(i)) action = "interference";    // demo #5 — MUST route here: its COM is
                                                                                 // apartment-bound and crashes SW if the
                                                                                 // cloud-parse await moves it off the UI thread
            // On a PART, a bare "flatten"/"unfold"/"flat pattern" (no dxf word) unambiguously means the flat pattern —
            // there's no sub-assembly to dissolve — so route it to FlatDxf too, closing the anti-demo hole where it
            // would otherwise fall to the generic "here's how you'd do it manually" essay. Part-scoped so an assembly
            // "flatten the sub-assembly" still reaches DissolveSubassembly.
            else if ((int)doc.GetType() == (int)swDocumentTypes_e.swDocPART
                     && System.Text.RegularExpressions.Regex.IsMatch(i, @"\b(flatten|unfold|flat[- ]?pattern)\b")
                     && !System.Text.RegularExpressions.Regex.IsMatch(i, @"\bsub.?assembl(y|ies)\b"))
                action = "flat_dxf";
            if (action == null) return false;

            var spec = Specs().Find(s => Array.IndexOf(s.Actions, action) >= 0);
            if (spec == null) return false;

            var plan = new IntentPlan { Confidence = 1.0 };
            var op = new IntentOperation { Action = action };
            plan.Operations.Add(op);
            try { PanelCapture.Log("parse", new { intent, action, confidence = 1.0, routed = spec.Name, source = "demo-route-first" }); } catch { }
            await ExecuteSpec(spec, doc, plan, op, intent);
            return true;
        }

        // ---- the runner: returns true if the pipeline handled the command; false => let the offline regex path try. ----
        private async Task<bool> RunViaPipeline(string intent)
        {
            var doc = App?.ActiveDoc as IModelDoc2;
            if (doc == null) return false;   // no doc: let the legacy path show its guidance

            // test-loop wrong-route fix (flange-14-pressure-300psi): a pressure/burst-rating question has no
            // geometric answer — state the honest limit before the cloud parse can misroute it to an unrelated
            // property/health check. Same guard as Harness.cs IntentDispatch, kept in sync.
            if (HonestLimits.IsPressureRatingQuestion(intent))
            {
                Send(new { type = "answer", answer = HonestLimits.PressureRatingLimitMessage });
                return true;
            }

            // test-loop wrong-route fix (flange-17-why-leaking): same guard as Harness.cs IntentDispatch, kept in sync.
            if (HonestLimits.IsLeakDiagnosisQuestion(intent))
            {
                Send(new { type = "answer", answer = HonestLimits.LeakDiagnosisLimitMessage });
                return true;
            }

            // test-loop wrong-answer fix (flange-13-weld-onto-tube): "weld the flange to the tube end" has no action
            // in the cloud's vocabulary, so it guessed the closest one — mate — and silently bolted a fastener
            // instead. Same guard shape as IsPressureRatingQuestion/IsLeakDiagnosisQuestion above, kept in sync
            // with Harness.cs IntentDispatch.
            if (HonestLimits.IsWeldRequest(intent))
            {
                Send(new { type = "answer", answer = HonestLimits.WeldLimitMessage });
                return true;
            }

            // test-loop hedged cluster fix (create-enclosure, add-motor-mount, design-ball, compose-into-cable-
            // assembly, combine-with-linear-rail, chain-walk-cycle): same mechanism as the HonestLimits guards
            // above, kept in sync with Harness.cs IntentDispatch.
            if (HonestLimits.IsGenerativeSynthesisRequest(intent))
            {
                Send(new { type = "answer", answer = HonestLimits.GenerativeSynthesisLimitMessage });
                return true;
            }

            // test-loop unclear fix (hull-vague-improve): a purely subjective aesthetic ask ("more sleek", "logo pop
            // more", "looks off") — same mechanism as the HonestLimits guards above, kept in sync with Harness.cs
            // IntentDispatch. This scenario's own expected behavior IS to clarify, not act.
            if (HonestLimits.IsVagueAestheticRequest(intent))
            {
                Send(new { type = "answer", answer = HonestLimits.VagueAestheticClarifyMessage });
                return true;
            }

            // test-loop hedged fix (replace-battery): "need a bigger battery, about 1.5 times the volume of the
            // current one" on an assembly parses LOW confidence (0.25) and 0 ops from the cloud - no scale_part op
            // ever reaches the scale_part HSpec, so it falls to the zero-op fallback's clarifying question instead
            // of attempting the scale. A digit-"times"-"volume" phrasing is narrow and unambiguous enough to route
            // authoritatively before the cloud parse, same shape as get_faces/get_edges below - deliberately NOT
            // the full ScalePart.IsScaleIntent (that's broad enough to also match already-live percent/x/height
            // scale phrasings that already route fine through the cloud; widening this override to all of them
            // risks changing behavior nothing asked to change). ScalePart.Run itself now resolves a NAMED
            // sub-component ("battery") to its own PartDoc when given an assembly, and reads "N times the volume"
            // as a volume ratio (cube-root to a linear factor), not a literal linear scale.
            if (System.Text.RegularExpressions.Regex.IsMatch(intent ?? "", @"\d+(\.\d+)?\s*times\b.{0,25}\bvolume\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                var scaleSpec = Specs().Find(s => Array.IndexOf(s.Actions, "scale_part") >= 0);
                if (scaleSpec != null)
                {
                    var scalePlan = new IntentPlan { Confidence = 1.0 };
                    var scaleOp = new IntentOperation { Action = "scale_part" };
                    scalePlan.Operations.Add(scaleOp);
                    try { PanelCapture.Log("parse", new { intent, action = "scale_part", confidence = 1.0, routed = scaleSpec.Name, source = "volume-ratio-route-first" }); } catch { }
                    await ExecuteSpec(scaleSpec, doc, scalePlan, scaleOp, intent);
                    return true;
                }
            }

            // test-loop wrong-route fix (count-faces): "how many faces on this surface?" has no get_faces action
            // in the cloud parser's vocabulary yet, so it always lands on the closest known read (list_features)
            // instead of counting faces. GetFaces.cs itself is already proven GREEN by the harness (getFaces
            // fixture, tool 23) — it was just never reachable from the live dispatch. Route it AUTHORITATIVELY
            // before the cloud parse, same shape as the HonestLimits guards above.
            if (GetFaces.IsIntent(intent))
            {
                OpCounter.Increment();
                ForgeData.RunBegin("get_faces", ForgeData.Mask(intent, IntentLayer.ComponentNames(doc)), 0);
                Func<string, string, string, string, Task> emitF = async (agent, gloss, state, result) =>
                { Send(new { type = "step", agent, gloss, state, result }); await Task.Delay(60); };
                var fr = await GetFaces.Run(App, doc, intent, emitF);
                if (fr.Error != null)
                { LogRun("get_faces", intent, doc, "error", false, 0, errorCode: "get_faces"); Send(new { type = "error", message = fr.Error }); return true; }
                LogRun("get_faces", intent, doc, "executed", true, fr.Total);
                Send(new { type = "answer", answer = fr.Info, runId = _currentRunId, handler = "get_faces" });
                return true;
            }

            // Same live-dispatch gap as get_faces above: GetEdges.cs (tool 24, get_edge_length) and GetBodies.cs
            // (list_bodies) are harness-GREEN but were never registered as an HSpec or a legacy fallback either —
            // route both AUTHORITATIVELY before the cloud parse for the same reason.
            if (GetEdges.IsIntent(intent))
            {
                OpCounter.Increment();
                ForgeData.RunBegin("get_edge_length", ForgeData.Mask(intent, IntentLayer.ComponentNames(doc)), 0);
                Func<string, string, string, string, Task> emitE = async (agent, gloss, state, result) =>
                { Send(new { type = "step", agent, gloss, state, result }); await Task.Delay(60); };
                var er = await GetEdges.Run(App, doc, intent, emitE);
                if (er.Error != null)
                { LogRun("get_edge_length", intent, doc, "error", false, 0, errorCode: "get_edge_length"); Send(new { type = "error", message = er.Error }); return true; }
                LogRun("get_edge_length", intent, doc, "executed", true, er.EdgeCount);
                Send(new { type = "answer", answer = er.Info, runId = _currentRunId, handler = "get_edge_length" });
                return true;
            }

            // test-loop wrong-route fix (hull-diagnostic-check): DetectFileHealth.cs (tool 239, detect_file_health) is
            // harness-GREEN but had no live route either — same gap as get_faces/get_edges above. "run error check
            // on the boat hull model, looking for bad geometry" on a PART doc had nowhere honest to land: the
            // cloud's closest known action is "diagnose" (the ASSEMBLY doctor), which then fails closed with "Open
            // the assembly (.SLDASM)" on a part — a wrong-route, not a real refusal. DetectFileHealth works on
            // EITHER a part or an assembly (rebuild errors/warnings, unknown feature types, freeze bar), so route
            // it authoritatively before the cloud parse can misroute to the assembly-only doctor. IsIntent is a
            // narrow health/pre-flight vocabulary (file health/corrupt/safe-to-touch), so it can't shadow Doctor's
            // own "diagnose"/"what's wrong"/"audit" wording.
            if (DetectFileHealth.IsIntent(intent))
            {
                OpCounter.Increment();
                ForgeData.RunBegin("detect_file_health", ForgeData.Mask(intent, IntentLayer.ComponentNames(doc)), 0);
                Func<string, string, string, string, Task> emitFH = async (agent, gloss, state, result) =>
                { Send(new { type = "step", agent, gloss, state, result }); await Task.Delay(60); };
                var fhr = await DetectFileHealth.Run(App, doc, intent, emitFH);
                if (fhr.Error != null)
                { LogRun("detect_file_health", intent, doc, "error", false, 0, errorCode: "detect_file_health"); Send(new { type = "error", message = fhr.Error }); return true; }
                LogRun("detect_file_health", intent, doc, "executed", true, fhr.RebuildProblems);
                Send(new { type = "answer", answer = fhr.Info, runId = _currentRunId, handler = "detect_file_health" });
                return true;
            }

            // test-loop wrong-answer fix (rim-count-holes/grinder-count-through-holes): measure_bolt_circle already
            // had an HSpec AND an intent matcher, but the matcher only ran as a POST-cloud-failure fallback
            // (TryLocalFallback/LocalActionFor) — so when the cloud parser confidently (successfully) picked a
            // valid but generic action instead (list_features/get_bounding_box) for a "count the holes"-shaped
            // question, nothing ever corrected it. Route AUTHORITATIVELY before the cloud parse, same shape as
            // get_faces/get_edges/get_bodies/get_material above — an honest "no repeating pattern found" from the
            // bolt-circle handler beats a silently-wrong generic feature/face dump every time.
            // MUST run BEFORE GetBodies below: test-loop wrong-route fix (count-mounting-holes-3kw) — "how many
            // mounting holes are on the 3 kW motor BODY?" incidentally contains "body", so GetBodies.IsIntent (word
            // body/bodies + a count word, no hole exclusion) was winning the race and returning a plain solid-body
            // count instead of the hole count actually asked for. Specific-first.
            if (MeasureBoltCircle.IsBoltCircleIntent(intent))
            {
                var bcSpec = Specs().Find(s => s.Name == "measure_bolt_circle");
                var bcPlan = new IntentPlan { Confidence = 1.0 };
                var bcOp = new IntentOperation { Action = "measure_bolt_circle" };
                bcPlan.Operations.Add(bcOp);
                await ExecuteSpec(bcSpec, doc, bcPlan, bcOp, intent);
                return true;
            }

            // test-loop hedged fix (rim-add-bolt-holes): "put 5 bolt holes equally spaced on a 4.5 inch circle" has
            // no add_bolt_circle action in the cloud's vocabulary — it parses the closest known one (add_hole),
            // which only ever drills ONE hole at a face's centre. Route AUTHORITATIVELY before the cloud parse,
            // same shape as measure_bolt_circle above, kept in sync with Harness.cs IntentDispatch. Doesn't collide
            // with measure_bolt_circle's own matcher above — see AddBoltCircle.IsAddBoltCircleIntent.
            if (AddBoltCircle.IsAddBoltCircleIntent(intent))
            {
                var abcSpec = Specs().Find(s => s.Name == "add_bolt_circle");
                var abcPlan = new IntentPlan { Confidence = 1.0 };
                var abcOp = new IntentOperation { Action = "add_bolt_circle" };
                abcPlan.Operations.Add(abcOp);
                await ExecuteSpec(abcSpec, doc, abcPlan, abcOp, intent);
                return true;
            }

            // test-loop wrong-answer fix (count-servos/count-rollers): a named-part-type count question ("how many
            // servos", "count the rollers") has no action in the cloud's vocabulary, so it lands on a generic
            // fallback (list_bodies/list_features) that answers a broader question instead. Same authoritative
            // pre-cloud override shape as measure_bolt_circle above. Also MUST run before GetBodies: its own
            // IsIntent already excludes body/bodies vocabulary so it can't shadow list_bodies itself, but placed
            // here to stay next to MeasureBoltCircle (same specific-first shape).

            // test-loop wrong-answer fix (measure-thickness-center): a plain "how thick is the metal in the middle of
            // that curve?" has no action in the cloud parser's vocabulary at all (only the narrower "wall thickness"/
            // "min thickness" phrasing does), so it declined outright instead of routing to wall_thickness — same
            // live-dispatch-gap shape as measure_bolt_circle/count_named_components above. WallThickness.Run itself
            // already reports a location-scoped answer near the intent's own "center of the curve" wording when
            // WallThickness.IsGenericThicknessQuestion catches a broader phrasing than the narrow offline matcher.
            if (WallThickness.IsStandaloneThicknessQuestion(intent))
            {
                var wtSpec = Specs().Find(s => s.Name == "wall_thickness");
                var wtPlan = new IntentPlan { Confidence = 1.0 };
                var wtOp = new IntentOperation { Action = "wall_thickness" };
                wtPlan.Operations.Add(wtOp);
                await ExecuteSpec(wtSpec, doc, wtPlan, wtOp, intent);
                return true;
            }

            // test-loop wrong-answer fix (grinder-count-through-holes): "how many holes go all the way through the
            // part" has no action in the cloud parser's vocabulary at all, so it fell to list_features. A
            // genuinely new geometric-analysis capability, not a routing fix.
            if (CountThroughHoles.IsIntent(intent))
            {
                var thSpec = Specs().Find(s => s.Name == "count_through_holes");
                var thPlan = new IntentPlan { Confidence = 1.0 };
                var thOp = new IntentOperation { Action = "count_through_holes" };
                thPlan.Operations.Add(thOp);
                await ExecuteSpec(thSpec, doc, thPlan, thOp, intent);
                return true;
            }

            // test-loop wrong-answer fix (count-mesh-cells): "count of openings across one row of the mesh" has no
            // action in the cloud parser's vocabulary at all, so it fell to a generic assembly scan. A genuinely
            // new geometric-analysis capability, not a routing fix.
            if (MeshOpenings.IsMeshOpeningsIntent(intent))
            {
                var moSpec = Specs().Find(s => s.Name == "mesh_openings");
                var moPlan = new IntentPlan { Confidence = 1.0 };
                var moOp = new IntentOperation { Action = "mesh_openings" };
                moPlan.Operations.Add(moOp);
                await ExecuteSpec(moSpec, doc, moPlan, moOp, intent);
                return true;
            }

            // test-loop wrong-answer fix (measure-mounting-hole-distance): "how far apart are the bolt holes?" has
            // no action in the cloud parser's vocabulary at all (only a generic overall-size fallback), so it
            // declined outright instead of routing to a real hole-to-hole spacing measurement. Distinct from
            // MeasureBoltCircle (that assumes a CIRCULAR pattern) — this handles a linear row or a bare pair too.
            if (HoleSpacing.IsHoleSpacingIntent(intent))
            {
                var hsSpec = Specs().Find(s => s.Name == "hole_spacing");
                var hsPlan = new IntentPlan { Confidence = 1.0 };
                var hsOp = new IntentOperation { Action = "hole_spacing" };
                hsPlan.Operations.Add(hsOp);
                await ExecuteSpec(hsSpec, doc, hsPlan, hsOp, intent);
                return true;
            }

            // test-loop wrong-answer fix (measure-arc-height): "arc height of this spring?" has no action in the
            // cloud parser's vocabulary at all (only a generic overall-size fallback), so it declined outright
            // instead of routing to a real camber measurement. Same authoritative-override shape as WallThickness
            // above — a genuinely new capability, not a routing fix, but wired the same way.
            if (ArcHeight.IsArcHeightIntent(intent))
            {
                var ahSpec = Specs().Find(s => s.Name == "arc_height");
                var ahPlan = new IntentPlan { Confidence = 1.0 };
                var ahOp = new IntentOperation { Action = "arc_height" };
                ahPlan.Operations.Add(ahOp);
                await ExecuteSpec(ahSpec, doc, ahPlan, ahOp, intent);
                return true;
            }

            if (CountNamedComponents.IsIntent(intent))
            {
                var cnSpec = Specs().Find(s => s.Name == "count_named_components");
                var cnPlan = new IntentPlan { Confidence = 1.0 };
                var cnOp = new IntentOperation { Action = "count_named_components" };
                cnPlan.Operations.Add(cnOp);
                await ExecuteSpec(cnSpec, doc, cnPlan, cnOp, intent);
                return true;
            }

            if (CountGearTeeth.IsIntent(intent))
            {
                var gtSpec = Specs().Find(s => s.Name == "count_gear_teeth");
                var gtPlan = new IntentPlan { Confidence = 1.0 };
                var gtOp = new IntentOperation { Action = "count_gear_teeth" };
                gtPlan.Operations.Add(gtOp);
                await ExecuteSpec(gtSpec, doc, gtPlan, gtOp, intent);
                return true;
            }

            // CreateDrawing (tool 101) — live-dispatch gap fix (2026-07-29), same shape as CountGearTeeth above.
            // Doc-lifecycle cluster (tools 124-127, 135) — live-dispatch gap fix (2026-07-29), same shape as
            // CountGearTeeth/CreateDrawing above. CloseDocument BEFORE OpenDocument: "close X" must never fall to
            // the open-first fallback CloseDocument itself uses internally when the target isn't open yet.
            if (CloseDocument.IsIntent(intent))
            {
                var cldSpec = Specs().Find(s => s.Name == "close_document");
                var cldPlan = new IntentPlan { Confidence = 1.0 };
                var cldOp = new IntentOperation { Action = "close_document" };
                cldPlan.Operations.Add(cldOp);
                await ExecuteSpec(cldSpec, doc, cldPlan, cldOp, intent);
                return true;
            }
            // SaveBodiesAsParts (tool 166) requires BOTH a body noun AND a part/file noun together, so it's the
            // more specific match against "save the bodies as parts" and must run BEFORE SaveDocumentAs (whose
            // broad "save...as" regex would otherwise also fire) and BEFORE SplitBody below (whose "split"+body/
            // part noun regex would otherwise also fire on "split the bodies into separate part files").
            if (SaveBodiesAsParts.IsIntent(intent))
            {
                var sbpSpec = Specs().Find(s => s.Name == "save_bodies_as_parts");
                var sbpPlan = new IntentPlan { Confidence = 1.0 };
                var sbpOp = new IntentOperation { Action = "save_bodies_as_parts" };
                sbpPlan.Operations.Add(sbpOp);
                await ExecuteSpec(sbpSpec, doc, sbpPlan, sbpOp, intent);
                return true;
            }
            if (SaveDocumentAs.IsIntent(intent))
            {
                var sdaSpec = Specs().Find(s => s.Name == "save_document_as");
                var sdaPlan = new IntentPlan { Confidence = 1.0 };
                var sdaOp = new IntentOperation { Action = "save_document_as" };
                sdaPlan.Operations.Add(sdaOp);
                await ExecuteSpec(sdaSpec, doc, sdaPlan, sdaOp, intent);
                return true;
            }
            if (SaveDocument.IsIntent(intent))
            {
                var sdSpec = Specs().Find(s => s.Name == "save_document");
                var sdPlan = new IntentPlan { Confidence = 1.0 };
                var sdOp = new IntentOperation { Action = "save_document" };
                sdPlan.Operations.Add(sdOp);
                await ExecuteSpec(sdSpec, doc, sdPlan, sdOp, intent);
                return true;
            }
            if (OpenDocument.IsIntent(intent))
            {
                var odSpec = Specs().Find(s => s.Name == "open_document");
                var odPlan = new IntentPlan { Confidence = 1.0 };
                var odOp = new IntentOperation { Action = "open_document" };
                odPlan.Operations.Add(odOp);
                await ExecuteSpec(odSpec, doc, odPlan, odOp, intent);
                return true;
            }
            if (BatchConvertFiles.IsIntent(intent))
            {
                var bcfSpec = Specs().Find(s => s.Name == "batch_convert_files");
                var bcfPlan = new IntentPlan { Confidence = 1.0 };
                var bcfOp = new IntentOperation { Action = "batch_convert_files" };
                bcfPlan.Operations.Add(bcfOp);
                await ExecuteSpec(bcfSpec, doc, bcfPlan, bcfOp, intent);
                return true;
            }
            if (ImportFile.IsIntent(intent))
            {
                var ifSpec = Specs().Find(s => s.Name == "import_file");
                var ifPlan = new IntentPlan { Confidence = 1.0 };
                var ifOp = new IntentOperation { Action = "import_file" };
                ifPlan.Operations.Add(ifOp);
                await ExecuteSpec(ifSpec, doc, ifPlan, ifOp, intent);
                return true;
            }
            // BatchExportDrawings BEFORE FlatDxf's own intercept (same ordering reasoning as the offline dispatch
            // in Harness.cs: FlatDxf's dxf/dwg+export matcher never checks for a drawing noun and would otherwise
            // misroute a drawing-scoped DXF request into its sheet-metal flat-pattern scan).
            if (BatchExportDrawings.IsIntent(intent))
            {
                var bedSpec = Specs().Find(s => s.Name == "batch_export_drawings");
                var bedPlan = new IntentPlan { Confidence = 1.0 };
                var bedOp = new IntentOperation { Action = "batch_export_drawings" };
                bedPlan.Operations.Add(bedOp);
                await ExecuteSpec(bedSpec, doc, bedPlan, bedOp, intent);
                return true;
            }

            // Mate family (tools 53/55/56/58/59/60/61) — live-dispatch gap fix (2026-07-29), same shape as the
            // doc-lifecycle cluster above. EditMateValue/DeleteMate/SuppressMate BEFORE the add_*_mate checks so a
            // "make the Concentric2 mate 10mm" edit never falls through to a fresh add attempt.
            if (EditMateValue.IsIntent(intent))
            {
                var emvSpec = Specs().Find(s => s.Name == "edit_mate_value");
                var emvPlan = new IntentPlan { Confidence = 1.0 };
                var emvOp = new IntentOperation { Action = "edit_mate_value" };
                emvPlan.Operations.Add(emvOp);
                await ExecuteSpec(emvSpec, doc, emvPlan, emvOp, intent);
                return true;
            }
            if (DeleteMate.IsIntent(intent))
            {
                var dmSpec = Specs().Find(s => s.Name == "delete_mate");
                var dmPlan = new IntentPlan { Confidence = 1.0 };
                var dmOp = new IntentOperation { Action = "delete_mate" };
                dmPlan.Operations.Add(dmOp);
                await ExecuteSpec(dmSpec, doc, dmPlan, dmOp, intent);
                return true;
            }
            if (SuppressMate.IsIntent(intent))
            {
                var smSpec = Specs().Find(s => s.Name == "suppress_mate");
                var smPlan = new IntentPlan { Confidence = 1.0 };
                var smOp = new IntentOperation { Action = "suppress_mate" };
                smPlan.Operations.Add(smOp);
                await ExecuteSpec(smSpec, doc, smPlan, smOp, intent);
                return true;
            }
            if (AddConcentricMate.IsIntent(intent))
            {
                var acmSpec = Specs().Find(s => s.Name == "add_concentric_mate");
                var acmPlan = new IntentPlan { Confidence = 1.0 };
                var acmOp = new IntentOperation { Action = "add_concentric_mate" };
                acmPlan.Operations.Add(acmOp);
                await ExecuteSpec(acmSpec, doc, acmPlan, acmOp, intent);
                return true;
            }
            if (AddCoincidentMate.IsIntent(intent))
            {
                var acoSpec = Specs().Find(s => s.Name == "add_coincident_mate");
                var acoPlan = new IntentPlan { Confidence = 1.0 };
                var acoOp = new IntentOperation { Action = "add_coincident_mate" };
                acoPlan.Operations.Add(acoOp);
                await ExecuteSpec(acoSpec, doc, acoPlan, acoOp, intent);
                return true;
            }
            if (AddParallelMate.IsIntent(intent))
            {
                var aplSpec = Specs().Find(s => s.Name == "add_parallel_mate");
                var aplPlan = new IntentPlan { Confidence = 1.0 };
                var aplOp = new IntentOperation { Action = "add_parallel_mate" };
                aplPlan.Operations.Add(aplOp);
                await ExecuteSpec(aplSpec, doc, aplPlan, aplOp, intent);
                return true;
            }
            if (AddDistanceMate.IsIntent(intent))
            {
                var adsSpec = Specs().Find(s => s.Name == "add_distance_mate");
                var adsPlan = new IntentPlan { Confidence = 1.0 };
                var adsOp = new IntentOperation { Action = "add_distance_mate" };
                adsPlan.Operations.Add(adsOp);
                await ExecuteSpec(adsSpec, doc, adsPlan, adsOp, intent);
                return true;
            }
            if (AddAngleMate.IsIntent(intent))
            {
                var aagSpec = Specs().Find(s => s.Name == "add_angle_mate");
                var aagPlan = new IntentPlan { Confidence = 1.0 };
                var aagOp = new IntentOperation { Action = "add_angle_mate" };
                aagPlan.Operations.Add(aagOp);
                await ExecuteSpec(aagSpec, doc, aagPlan, aagOp, intent);
                return true;
            }
            if (AddWidthMate.IsIntent(intent))
            {
                var awdSpec = Specs().Find(s => s.Name == "add_width_mate");
                var awdPlan = new IntentPlan { Confidence = 1.0 };
                var awdOp = new IntentOperation { Action = "add_width_mate" };
                awdPlan.Operations.Add(awdOp);
                await ExecuteSpec(awdSpec, doc, awdPlan, awdOp, intent);
                return true;
            }

            // pattern_sketch_entities BEFORE the pattern-edit family below: "pattern the sketch to 3 copies" could
            // in theory match EditPatternCount's broad verb+digit+noun check too, but pattern_sketch_entities
            // requires the literal "sketch" noun (a SKETCH-entity pattern, not a feature's instance count), so the
            // more specific one has to win first.
            if (PatternSketchEntities.IsIntent(intent))
            {
                var pseSpec = Specs().Find(s => s.Name == "pattern_sketch_entities");
                var psePlan = new IntentPlan { Confidence = 1.0 };
                var pseOp = new IntentOperation { Action = "pattern_sketch_entities" };
                psePlan.Operations.Add(pseOp);
                await ExecuteSpec(pseSpec, doc, psePlan, pseOp, intent);
                return true;
            }

            // Pattern-edit family (tools 48/49/52) — live-dispatch gap fix (2026-07-29), same shape as the mate
            // family above. SkipPatternInstance BEFORE EditPatternCount: both can match a bare number + "pattern",
            // but only skip_pattern_instance's IsIntent requires the literal "skip" verb (edit_pattern_count needs
            // a change verb instead), so checking skip first costs nothing and guards against any future overlap.
            if (SkipPatternInstance.IsIntent(intent))
            {
                var spiSpec = Specs().Find(s => s.Name == "skip_pattern_instance");
                var spiPlan = new IntentPlan { Confidence = 1.0 };
                var spiOp = new IntentOperation { Action = "skip_pattern_instance" };
                spiPlan.Operations.Add(spiOp);
                await ExecuteSpec(spiSpec, doc, spiPlan, spiOp, intent);
                return true;
            }
            // LinearPatternComponent (tool 41, CREATE) — must be checked BEFORE EditPatternSpacing: that handler's
            // broad "pattern"+"apart/spacing/pitch"+digit matcher would otherwise swallow a create phrase like
            // "...component 3 times, 40mm apart" — this one's narrower "linear(ly) pattern"+"component"+count
            // requirement is the more specific match (specific-first), mirroring Harness.cs's IntentDispatch order.
            if (LinearPatternComponent.IsIntent(intent))
            {
                var lpcSpec = Specs().Find(s => s.Name == "linear_pattern_components");
                var lpcPlan = new IntentPlan { Confidence = 1.0 };
                var lpcOp = new IntentOperation { Action = "linear_pattern_components" };
                lpcPlan.Operations.Add(lpcOp);
                await ExecuteSpec(lpcSpec, doc, lpcPlan, lpcOp, intent);
                return true;
            }
            // CircularPatternComponent (tool 42, CREATE) — checked here alongside its linear sibling; its
            // "circular(ly) pattern"+"component"+count matcher is disjoint from EditPatternSpacing/Count's
            // vocabulary (no spacing/apart/pitch words), but grouping the pair keeps the pattern family together.
            if (CircularPatternComponent.IsIntent(intent))
            {
                var cpcSpec = Specs().Find(s => s.Name == "circular_pattern_components");
                var cpcPlan = new IntentPlan { Confidence = 1.0 };
                var cpcOp = new IntentOperation { Action = "circular_pattern_components" };
                cpcPlan.Operations.Add(cpcOp);
                await ExecuteSpec(cpcSpec, doc, cpcPlan, cpcOp, intent);
                return true;
            }
            // PatternDrivenPatternComponent (tool 44, CREATE) — its "existing/feature/hole pattern"+follow/match/
            // driven wording is disjoint from its 41/42 siblings' count/spacing/angle-in-text requirement, so order
            // among the three doesn't matter; grouped here to keep the pattern-component family together.
            if (PatternDrivenPatternComponent.IsIntent(intent))
            {
                var pdpSpec = Specs().Find(s => s.Name == "pattern_driven_pattern");
                var pdpPlan = new IntentPlan { Confidence = 1.0 };
                var pdpOp = new IntentOperation { Action = "pattern_driven_pattern" };
                pdpPlan.Operations.Add(pdpOp);
                await ExecuteSpec(pdpSpec, doc, pdpPlan, pdpOp, intent);
                return true;
            }
            // SketchDrivenPatternComponent (tool 45, CREATE) — its "sketch"+"point(s)"+"component" wording is
            // disjoint from every other pattern-family matcher, so order among them doesn't matter; grouped here
            // to keep the family together.
            if (SketchDrivenPatternComponent.IsIntent(intent))
            {
                var sdpSpec = Specs().Find(s => s.Name == "sketch_driven_pattern");
                var sdpPlan = new IntentPlan { Confidence = 1.0 };
                var sdpOp = new IntentOperation { Action = "sketch_driven_pattern" };
                sdpPlan.Operations.Add(sdpOp);
                await ExecuteSpec(sdpSpec, doc, sdpPlan, sdpOp, intent);
                return true;
            }
            if (EditPatternSpacing.IsIntent(intent))
            {
                var epsSpec = Specs().Find(s => s.Name == "edit_pattern_spacing");
                var epsPlan = new IntentPlan { Confidence = 1.0 };
                var epsOp = new IntentOperation { Action = "edit_pattern_spacing" };
                epsPlan.Operations.Add(epsOp);
                await ExecuteSpec(epsSpec, doc, epsPlan, epsOp, intent);
                return true;
            }
            if (EditPatternCount.IsIntent(intent))
            {
                var epcSpec = Specs().Find(s => s.Name == "edit_pattern_count");
                var epcPlan = new IntentPlan { Confidence = 1.0 };
                var epcOp = new IntentOperation { Action = "edit_pattern_count" };
                epcPlan.Operations.Add(epcOp);
                await ExecuteSpec(epcSpec, doc, epcPlan, epcOp, intent);
                return true;
            }

            // Configuration + custom-property family (tools 39/64/67/68/69/70/71/72/85/90) — live-dispatch gap fix
            // (2026-07-29), same shape as the mate/pattern families above. set_config_specific_dimension BEFORE the
            // plain set_dimension fallback below: "set the depth to 30 in the Variant-1 configuration" has a verb +
            // number that set_dimension's matcher would also claim (it has no config-word exclusion), so the
            // config-word-requiring, more-specific matcher must win by running first — same ordering Harness.cs
            // already uses (see test-config.json config-specific-dimension-block's note). set_config_feature_
            // suppression and change_component_config are each guarded by their own required nouns (feature-word /
            // component-word) so they never collide with set_active_configuration's doc-level switch.
            if (ConfigSpecificDimension.IsIntent(intent))
            {
                var csdSpec = Specs().Find(s => s.Name == "set_config_specific_dimension");
                var csdPlan = new IntentPlan { Confidence = 1.0 };
                var csdOp = new IntentOperation { Action = "set_config_specific_dimension" };
                csdPlan.Operations.Add(csdOp);
                await ExecuteSpec(csdSpec, doc, csdPlan, csdOp, intent);
                return true;
            }
            if (ConfigFeatureSuppression.IsIntent(intent))
            {
                var cfsSpec = Specs().Find(s => s.Name == "set_config_feature_suppression");
                var cfsPlan = new IntentPlan { Confidence = 1.0 };
                var cfsOp = new IntentOperation { Action = "set_config_feature_suppression" };
                cfsPlan.Operations.Add(cfsOp);
                await ExecuteSpec(cfsSpec, doc, cfsPlan, cfsOp, intent);
                return true;
            }
            if (ChangeComponentConfig.IsIntent(intent))
            {
                var cccSpec = Specs().Find(s => s.Name == "change_component_config");
                var cccPlan = new IntentPlan { Confidence = 1.0 };
                var cccOp = new IntentOperation { Action = "change_component_config" };
                cccPlan.Operations.Add(cccOp);
                await ExecuteSpec(cccSpec, doc, cccPlan, cccOp, intent);
                return true;
            }
            if (SetActiveConfiguration.IsIntent(intent))
            {
                var sacSpec = Specs().Find(s => s.Name == "set_active_configuration");
                var sacPlan = new IntentPlan { Confidence = 1.0 };
                var sacOp = new IntentOperation { Action = "set_active_configuration" };
                sacPlan.Operations.Add(sacOp);
                await ExecuteSpec(sacSpec, doc, sacPlan, sacOp, intent);
                return true;
            }
            if (CreateConfiguration.IsIntent(intent))
            {
                var crcSpec = Specs().Find(s => s.Name == "create_configuration");
                var crcPlan = new IntentPlan { Confidence = 1.0 };
                var crcOp = new IntentOperation { Action = "create_configuration" };
                crcPlan.Operations.Add(crcOp);
                await ExecuteSpec(crcSpec, doc, crcPlan, crcOp, intent);
                return true;
            }
            if (DeleteConfiguration.IsIntent(intent))
            {
                var dlcSpec = Specs().Find(s => s.Name == "delete_configuration");
                var dlcPlan = new IntentPlan { Confidence = 1.0 };
                var dlcOp = new IntentOperation { Action = "delete_configuration" };
                dlcPlan.Operations.Add(dlcOp);
                await ExecuteSpec(dlcSpec, doc, dlcPlan, dlcOp, intent);
                return true;
            }
            if (RenameConfiguration.IsIntent(intent))
            {
                var rncSpec = Specs().Find(s => s.Name == "rename_configuration");
                var rncPlan = new IntentPlan { Confidence = 1.0 };
                var rncOp = new IntentOperation { Action = "rename_configuration" };
                rncPlan.Operations.Add(rncOp);
                await ExecuteSpec(rncSpec, doc, rncPlan, rncOp, intent);
                return true;
            }
            if (CopyConfiguration.IsIntent(intent))
            {
                var cpcSpec = Specs().Find(s => s.Name == "copy_configuration");
                var cpcPlan = new IntentPlan { Confidence = 1.0 };
                var cpcOp = new IntentOperation { Action = "copy_configuration" };
                cpcPlan.Operations.Add(cpcOp);
                await ExecuteSpec(cpcSpec, doc, cpcPlan, cpcOp, intent);
                return true;
            }
            // CopyPropertiesBetweenFiles (tool 142) requires "copy" + "propert(y|ies)" + "from" — no other property
            // handler's verb list includes "copy" or "from", so it never collides.
            if (CopyPropertiesBetweenFiles.IsIntent(intent))
            {
                var cpbSpec = Specs().Find(s => s.Name == "copy_properties_between_files");
                var cpbPlan = new IntentPlan { Confidence = 1.0 };
                var cpbOp = new IntentOperation { Action = "copy_properties_between_files" };
                cpbPlan.Operations.Add(cpbOp);
                await ExecuteSpec(cpbSpec, doc, cpbPlan, cpbOp, intent);
                return true;
            }
            // CopySketchToPart (tool 152) requires "copy" + "sketch" + "from" and excludes the property noun, so
            // it never collides with CopyPropertiesBetweenFiles right above.
            if (CopySketchToPart.IsIntent(intent))
            {
                var cstSpec = Specs().Find(s => s.Name == "copy_sketch_to_part");
                var cstPlan = new IntentPlan { Confidence = 1.0 };
                var cstOp = new IntentOperation { Action = "copy_sketch_to_part" };
                cstPlan.Operations.Add(cstOp);
                await ExecuteSpec(cstSpec, doc, cstPlan, cstOp, intent);
                return true;
            }
            // InsertLibraryFeature (tool 218) requires the explicit "library feature" phrase — disjoint from
            // every other insert/copy vocabulary in this build.
            if (InsertLibraryFeature.IsIntent(intent))
            {
                var ilfSpec = Specs().Find(s => s.Name == "insert_library_feature");
                var ilfPlan = new IntentPlan { Confidence = 1.0 };
                var ilfOp = new IntentOperation { Action = "insert_library_feature" };
                ilfPlan.Operations.Add(ilfOp);
                await ExecuteSpec(ilfSpec, doc, ilfPlan, ilfOp, intent);
                return true;
            }
            if (SetCustomProperty.IsIntent(intent))
            {
                var scpSpec = Specs().Find(s => s.Name == "set_custom_property");
                var scpPlan = new IntentPlan { Confidence = 1.0 };
                var scpOp = new IntentOperation { Action = "set_custom_property" };
                scpPlan.Operations.Add(scpOp);
                await ExecuteSpec(scpSpec, doc, scpPlan, scpOp, intent);
                return true;
            }
            if (DeleteCustomProperty.IsIntent(intent))
            {
                var dcpSpec = Specs().Find(s => s.Name == "delete_custom_property");
                var dcpPlan = new IntentPlan { Confidence = 1.0 };
                var dcpOp = new IntentOperation { Action = "delete_custom_property" };
                dcpPlan.Operations.Add(dcpOp);
                await ExecuteSpec(dcpSpec, doc, dcpPlan, dcpOp, intent);
                return true;
            }
            if (GetCustomProperty.IsIntent(intent))
            {
                var gcpSpec = Specs().Find(s => s.Name == "get_custom_property");
                var gcpPlan = new IntentPlan { Confidence = 1.0 };
                var gcpOp = new IntentOperation { Action = "get_custom_property" };
                gcpPlan.Operations.Add(gcpOp);
                await ExecuteSpec(gcpSpec, doc, gcpPlan, gcpOp, intent);
                return true;
            }
            if (GetComponentConfig.IsIntent(intent))
            {
                var gccSpec = Specs().Find(s => s.Name == "get_component_config");
                var gccPlan = new IntentPlan { Confidence = 1.0 };
                var gccOp = new IntentOperation { Action = "get_component_config" };
                gccPlan.Operations.Add(gccOp);
                await ExecuteSpec(gccSpec, doc, gccPlan, gccOp, intent);
                return true;
            }

            // Document-settings family — live-dispatch gap fix (2026-07-29), same order Harness.cs IntentDispatch
            // already proves collision-free: rename_dimension -> normalize_units -> angular -> decimal -> drafting
            // standard -> set-units -> get-units (each fenced by its own required vocabulary word).
            if (RenameDimension.IsIntent(intent))
            {
                var rndSpec = Specs().Find(s => s.Name == "rename_dimension");
                var rndPlan = new IntentPlan { Confidence = 1.0 };
                var rndOp = new IntentOperation { Action = "rename_dimension" };
                rndPlan.Operations.Add(rndOp);
                await ExecuteSpec(rndSpec, doc, rndPlan, rndOp, intent);
                return true;
            }
            if (NormalizeUnits.IsIntent(intent))
            {
                var nuSpec = Specs().Find(s => s.Name == "normalize_units");
                var nuPlan = new IntentPlan { Confidence = 1.0 };
                var nuOp = new IntentOperation { Action = "normalize_units" };
                nuPlan.Operations.Add(nuOp);
                await ExecuteSpec(nuSpec, doc, nuPlan, nuOp, intent);
                return true;
            }
            if (SetAngularUnits.IsIntent(intent))
            {
                var sauSpec = Specs().Find(s => s.Name == "set_angular_units");
                var sauPlan = new IntentPlan { Confidence = 1.0 };
                var sauOp = new IntentOperation { Action = "set_angular_units" };
                sauPlan.Operations.Add(sauOp);
                await ExecuteSpec(sauSpec, doc, sauPlan, sauOp, intent);
                return true;
            }
            if (SetDecimalPlaces.IsIntent(intent))
            {
                var sdpSpec = Specs().Find(s => s.Name == "set_decimal_places");
                var sdpPlan = new IntentPlan { Confidence = 1.0 };
                var sdpOp = new IntentOperation { Action = "set_decimal_places" };
                sdpPlan.Operations.Add(sdpOp);
                await ExecuteSpec(sdpSpec, doc, sdpPlan, sdpOp, intent);
                return true;
            }
            if (SetDraftingStandard.IsIntent(intent))
            {
                var sdsSpec = Specs().Find(s => s.Name == "set_document_properties");
                var sdsPlan = new IntentPlan { Confidence = 1.0 };
                var sdsOp = new IntentOperation { Action = "set_document_properties" };
                sdsPlan.Operations.Add(sdsOp);
                await ExecuteSpec(sdsSpec, doc, sdsPlan, sdsOp, intent);
                return true;
            }
            if (SetDocumentUnits.IsIntent(intent))
            {
                var sduSpec = Specs().Find(s => s.Name == "set_document_units");
                var sduPlan = new IntentPlan { Confidence = 1.0 };
                var sduOp = new IntentOperation { Action = "set_document_units" };
                sduPlan.Operations.Add(sduOp);
                await ExecuteSpec(sduSpec, doc, sduPlan, sduOp, intent);
                return true;
            }
            if (GetDocumentUnits.IsIntent(intent))
            {
                var gduSpec = Specs().Find(s => s.Name == "get_document_units");
                var gduPlan = new IntentPlan { Confidence = 1.0 };
                var gduOp = new IntentOperation { Action = "get_document_units" };
                gduPlan.Operations.Add(gduOp);
                await ExecuteSpec(gduSpec, doc, gduPlan, gduOp, intent);
                return true;
            }

            // List/count family — live-dispatch gap fix (2026-07-29), each fenced by its own required noun
            // (tree/subassembly/reference-geometry/dependency/dimension), so order among them doesn't matter.
            if (GetDimensions.IsIntent(intent))
            {
                var gdSpec = Specs().Find(s => s.Name == "list_dimensions");
                var gdPlan = new IntentPlan { Confidence = 1.0 };
                var gdOp = new IntentOperation { Action = "list_dimensions" };
                gdPlan.Operations.Add(gdOp);
                await ExecuteSpec(gdSpec, doc, gdPlan, gdOp, intent);
                return true;
            }
            if (ListComponents.IsIntent(intent))
            {
                var lcSpec = Specs().Find(s => s.Name == "list_components");
                var lcPlan = new IntentPlan { Confidence = 1.0 };
                var lcOp = new IntentOperation { Action = "list_components" };
                lcPlan.Operations.Add(lcOp);
                await ExecuteSpec(lcSpec, doc, lcPlan, lcOp, intent);
                return true;
            }
            if (ListFeatureDependencies.IsIntent(intent))
            {
                var lfdSpec = Specs().Find(s => s.Name == "list_feature_dependencies");
                var lfdPlan = new IntentPlan { Confidence = 1.0 };
                var lfdOp = new IntentOperation { Action = "list_feature_dependencies" };
                lfdPlan.Operations.Add(lfdOp);
                await ExecuteSpec(lfdSpec, doc, lfdPlan, lfdOp, intent);
                return true;
            }
            if (ListSubassemblies.IsIntent(intent))
            {
                var lsSpec = Specs().Find(s => s.Name == "list_subassemblies");
                var lsPlan = new IntentPlan { Confidence = 1.0 };
                var lsOp = new IntentOperation { Action = "list_subassemblies" };
                lsPlan.Operations.Add(lsOp);
                await ExecuteSpec(lsSpec, doc, lsPlan, lsOp, intent);
                return true;
            }
            if (GetRefGeometry.IsIntent(intent))
            {
                var rgSpec = Specs().Find(s => s.Name == "list_reference_geometry");
                var rgPlan = new IntentPlan { Confidence = 1.0 };
                var rgOp = new IntentOperation { Action = "list_reference_geometry" };
                rgPlan.Operations.Add(rgOp);
                await ExecuteSpec(rgSpec, doc, rgPlan, rgOp, intent);
                return true;
            }

            // Component-tree WRITE family — live-dispatch gap fix (2026-07-29). Both insert_component and
            // replace_component require an explicit FILE reference (quoted path or .sldprt/.sldasm token), which
            // no other handler's matcher demands, so ordering among them and against everything else is safe.
            if (InsertComponent.IsIntent(intent))
            {
                var icSpec = Specs().Find(s => s.Name == "insert_component");
                var icPlan = new IntentPlan { Confidence = 1.0 };
                var icOp = new IntentOperation { Action = "insert_component" };
                icPlan.Operations.Add(icOp);
                await ExecuteSpec(icSpec, doc, icPlan, icOp, intent);
                return true;
            }
            // BatchReplaceComponents (tool 164) BEFORE ReplaceComponent — same shadowing lesson as Harness.cs:
            // a multi-target "replace X with A and Y with B" command also satisfies ReplaceComponent's looser
            // single-target matcher, so the more specific (2+ file refs) check must run first.
            if (BatchReplaceComponents.IsIntent(intent))
            {
                var brcSpec = Specs().Find(s => s.Name == "batch_replace_components");
                var brcPlan = new IntentPlan { Confidence = 1.0 };
                var brcOp = new IntentOperation { Action = "batch_replace_components" };
                brcPlan.Operations.Add(brcOp);
                await ExecuteSpec(brcSpec, doc, brcPlan, brcOp, intent);
                return true;
            }
            if (ReplaceComponent.IsIntent(intent))
            {
                var rcSpec = Specs().Find(s => s.Name == "replace_component");
                var rcPlan = new IntentPlan { Confidence = 1.0 };
                var rcOp = new IntentOperation { Action = "replace_component" };
                rcPlan.Operations.Add(rcOp);
                await ExecuteSpec(rcSpec, doc, rcPlan, rcOp, intent);
                return true;
            }
            if (SplitBody.IsIntent(intent))
            {
                var sbSpec = Specs().Find(s => s.Name == "split_body");
                var sbPlan = new IntentPlan { Confidence = 1.0 };
                var sbOp = new IntentOperation { Action = "split_body" };
                sbPlan.Operations.Add(sbOp);
                await ExecuteSpec(sbSpec, doc, sbPlan, sbOp, intent);
                return true;
            }
            if (RunDfmChecks.IsIntent(intent))
            {
                var dfmSpec = Specs().Find(s => s.Name == "run_dfm_checks");
                var dfmPlan = new IntentPlan { Confidence = 1.0 };
                var dfmOp = new IntentOperation { Action = "run_dfm_checks" };
                dfmPlan.Operations.Add(dfmOp);
                await ExecuteSpec(dfmSpec, doc, dfmPlan, dfmOp, intent);
                return true;
            }
            if (ExportBom.IsIntent(intent))
            {
                var ebSpec = Specs().Find(s => s.Name == "export_bom");
                var ebPlan = new IntentPlan { Confidence = 1.0 };
                var ebOp = new IntentOperation { Action = "export_bom" };
                ebPlan.Operations.Add(ebOp);
                await ExecuteSpec(ebSpec, doc, ebPlan, ebOp, intent);
                return true;
            }
            if (DeleteReplaceFace.IsIntent(intent))
            {
                var drfSpec = Specs().Find(s => s.Name == "delete_replace_face");
                var drfPlan = new IntentPlan { Confidence = 1.0 };
                var drfOp = new IntentOperation { Action = "delete_replace_face" };
                drfPlan.Operations.Add(drfOp);
                await ExecuteSpec(drfSpec, doc, drfPlan, drfOp, intent);
                return true;
            }
            if (RunImportDiagnostics.IsIntent(intent))
            {
                var ridSpec = Specs().Find(s => s.Name == "run_import_diagnostics");
                var ridPlan = new IntentPlan { Confidence = 1.0 };
                var ridOp = new IntentOperation { Action = "run_import_diagnostics" };
                ridPlan.Operations.Add(ridOp);
                await ExecuteSpec(ridSpec, doc, ridPlan, ridOp, intent);
                return true;
            }
            if (CheckGeometryErrors.IsIntent(intent))
            {
                var cgeSpec = Specs().Find(s => s.Name == "check_geometry_errors");
                var cgePlan = new IntentPlan { Confidence = 1.0 };
                var cgeOp = new IntentOperation { Action = "check_geometry_errors" };
                cgePlan.Operations.Add(cgeOp);
                await ExecuteSpec(cgeSpec, doc, cgePlan, cgeOp, intent);
                return true;
            }
            if (AddCenterMarks.IsIntent(intent))
            {
                var acmSpec = Specs().Find(s => s.Name == "add_center_marks");
                var acmPlan = new IntentPlan { Confidence = 1.0 };
                var acmOp = new IntentOperation { Action = "add_center_marks" };
                acmPlan.Operations.Add(acmOp);
                await ExecuteSpec(acmSpec, doc, acmPlan, acmOp, intent);
                return true;
            }
            if (ReplaceSheetFormat.IsIntent(intent))
            {
                var rsfSpec = Specs().Find(s => s.Name == "replace_sheet_format");
                var rsfPlan = new IntentPlan { Confidence = 1.0 };
                var rsfOp = new IntentOperation { Action = "replace_sheet_format" };
                rsfPlan.Operations.Add(rsfOp);
                await ExecuteSpec(rsfSpec, doc, rsfPlan, rsfOp, intent);
                return true;
            }
            if (UpdateRevisionTable.IsIntent(intent))
            {
                var urtSpec = Specs().Find(s => s.Name == "update_revision_table");
                var urtPlan = new IntentPlan { Confidence = 1.0 };
                var urtOp = new IntentOperation { Action = "update_revision_table" };
                urtPlan.Operations.Add(urtOp);
                await ExecuteSpec(urtSpec, doc, urtPlan, urtOp, intent);
                return true;
            }
            if (CheckDraftingStandards.IsIntent(intent))
            {
                var cdsSpec = Specs().Find(s => s.Name == "check_drafting_standards");
                var cdsPlan = new IntentPlan { Confidence = 1.0 };
                var cdsOp = new IntentOperation { Action = "check_drafting_standards" };
                cdsPlan.Operations.Add(cdsOp);
                await ExecuteSpec(cdsSpec, doc, cdsPlan, cdsOp, intent);
                return true;
            }
            if (CombineBodies.IsIntent(intent))
            {
                var cbSpec = Specs().Find(s => s.Name == "combine_bodies");
                var cbPlan = new IntentPlan { Confidence = 1.0 };
                var cbOp = new IntentOperation { Action = "combine_bodies" };
                cbPlan.Operations.Add(cbOp);
                await ExecuteSpec(cbSpec, doc, cbPlan, cbOp, intent);
                return true;
            }

            // Assembly-diagnostic READ family + dissolve_subassembly WRITE — live-dispatch gap fix (2026-07-29),
            // each fenced by its own distinctive noun, no ordering constraints among them.
            if (FindFloating.IsIntent(intent))
            {
                var ffSpec = Specs().Find(s => s.Name == "find_floating_components");
                var ffPlan = new IntentPlan { Confidence = 1.0 };
                var ffOp = new IntentOperation { Action = "find_floating_components" };
                ffPlan.Operations.Add(ffOp);
                await ExecuteSpec(ffSpec, doc, ffPlan, ffOp, intent);
                return true;
            }
            if (FindOverDefined.IsIntent(intent))
            {
                var foSpec = Specs().Find(s => s.Name == "find_over_defined_components");
                var foPlan = new IntentPlan { Confidence = 1.0 };
                var foOp = new IntentOperation { Action = "find_over_defined_components" };
                foPlan.Operations.Add(foOp);
                await ExecuteSpec(foSpec, doc, foPlan, foOp, intent);
                return true;
            }
            if (ResolveDuplicatePaths.IsIntent(intent))
            {
                var rdSpec = Specs().Find(s => s.Name == "resolve_duplicate_paths");
                var rdPlan = new IntentPlan { Confidence = 1.0 };
                var rdOp = new IntentOperation { Action = "resolve_duplicate_paths" };
                rdPlan.Operations.Add(rdOp);
                await ExecuteSpec(rdSpec, doc, rdPlan, rdOp, intent);
                return true;
            }
            if (FindDuplicateComponents.IsIntent(intent))
            {
                var fdSpec = Specs().Find(s => s.Name == "find_duplicate_components");
                var fdPlan = new IntentPlan { Confidence = 1.0 };
                var fdOp = new IntentOperation { Action = "find_duplicate_components" };
                fdPlan.Operations.Add(fdOp);
                await ExecuteSpec(fdSpec, doc, fdPlan, fdOp, intent);
                return true;
            }
            if (CheckPartSymmetry.IsIntent(intent))
            {
                var cpsSpec = Specs().Find(s => s.Name == "check_part_symmetry");
                var cpsPlan = new IntentPlan { Confidence = 1.0 };
                var cpsOp = new IntentOperation { Action = "check_part_symmetry" };
                cpsPlan.Operations.Add(cpsOp);
                await ExecuteSpec(cpsSpec, doc, cpsPlan, cpsOp, intent);
                return true;
            }
            if (DissolveSubassembly.IsIntent(intent))
            {
                var dsSpec = Specs().Find(s => s.Name == "dissolve_subassembly");
                var dsPlan = new IntentPlan { Confidence = 1.0 };
                var dsOp = new IntentOperation { Action = "dissolve_subassembly" };
                dsPlan.Operations.Add(dsOp);
                await ExecuteSpec(dsSpec, doc, dsPlan, dsOp, intent);
                return true;
            }
            if (SetSubassemblyFlexibility.IsIntent(intent))
            {
                var sfSpec = Specs().Find(s => s.Name == "set_subassembly_flexibility");
                var sfPlan = new IntentPlan { Confidence = 1.0 };
                var sfOp = new IntentOperation { Action = "set_subassembly_flexibility" };
                sfPlan.Operations.Add(sfOp);
                await ExecuteSpec(sfSpec, doc, sfPlan, sfOp, intent);
                return true;
            }
            // RepairExplodedView (tool 193) checked here, BEFORE the direct Exploder intercept further down in
            // ForgePanel.cs's OnCommand — repair/reattach/fix vocabulary is more specific than Exploder's bare
            // \bexploded\b (which also carries its own negative-guard exclusion as defense-in-depth).
            if (RepairExplodedView.IsIntent(intent))
            {
                var rvSpec = Specs().Find(s => s.Name == "repair_exploded_view");
                var rvPlan = new IntentPlan { Confidence = 1.0 };
                var rvOp = new IntentOperation { Action = "repair_exploded_view" };
                rvPlan.Operations.Add(rvOp);
                await ExecuteSpec(rvSpec, doc, rvPlan, rvOp, intent);
                return true;
            }
            // ManageDesignTable (tool 194) — unique "design table" vocabulary, no ordering dependency.
            if (ManageDesignTable.IsIntent(intent))
            {
                var mdSpec = Specs().Find(s => s.Name == "manage_design_table");
                var mdPlan = new IntentPlan { Confidence = 1.0 };
                var mdOp = new IntentOperation { Action = "manage_design_table" };
                mdPlan.Operations.Add(mdOp);
                await ExecuteSpec(mdSpec, doc, mdPlan, mdOp, intent);
                return true;
            }
            // FillSurface (tool 226) — fill/patch + surface(s) vocabulary, disjoint from knit/stitch/sew+surfaces
            // (KnitSurfacesToSolid) and thicken+surface (CreateThicken), no ordering dependency.
            if (FillSurface.IsIntent(intent))
            {
                var fsSpec = Specs().Find(s => s.Name == "fill_surface");
                var fsPlan = new IntentPlan { Confidence = 1.0 };
                var fsOp = new IntentOperation { Action = "fill_surface" };
                fsPlan.Operations.Add(fsOp);
                await ExecuteSpec(fsSpec, doc, fsPlan, fsOp, intent);
                return true;
            }
            // DescribeGeometry (tool 237) — describe/explain + face/geometry/shape/surface vocabulary, disjoint
            // from GetSelectedEntities (what/show/list/get/tell+selected) and GetFeatureInfo (depth/radius-of-a-
            // named-feature), no ordering dependency.
            if (DescribeGeometry.IsIntent(intent))
            {
                var dgSpec = Specs().Find(s => s.Name == "describe_geometry");
                var dgPlan = new IntentPlan { Confidence = 1.0 };
                var dgOp = new IntentOperation { Action = "describe_geometry" };
                dgPlan.Operations.Add(dgOp);
                await ExecuteSpec(dgSpec, doc, dgPlan, dgOp, intent);
                return true;
            }
            // HighlightEntities (tool 238) — highlight/flash/light up + face/hole/bore vocabulary, disjoint from
            // DescribeGeometry (describe/explain) and SelectFace (select + face, no highlight/flash verb).
            if (HighlightEntities.IsIntent(intent))
            {
                var heSpec = Specs().Find(s => s.Name == "highlight_entities");
                var hePlan = new IntentPlan { Confidence = 1.0 };
                var heOp = new IntentOperation { Action = "highlight_entities" };
                hePlan.Operations.Add(heOp);
                await ExecuteSpec(heSpec, doc, hePlan, heOp, intent);
                return true;
            }
            // HandleLockedFiles (tool 248) — locked/read-only/checked-out/permission-denied + file/document
            // vocabulary, disjoint from DetectFileHealth (health/preflight/corruption); GetFileReferences
            // explicitly excludes this vocabulary so it never shadows either direction.
            if (HandleLockedFiles.IsIntent(intent))
            {
                var lfSpec = Specs().Find(s => s.Name == "handle_locked_files");
                var lfPlan = new IntentPlan { Confidence = 1.0 };
                var lfOp = new IntentOperation { Action = "handle_locked_files" };
                lfPlan.Operations.Add(lfOp);
                await ExecuteSpec(lfSpec, doc, lfPlan, lfOp, intent);
                return true;
            }
            // DetectInContextWrites (tool 242) — in-context/external-reference/ripple/propagate vocabulary;
            // GetFileReferences explicitly excludes this vocabulary so it never shadows either direction.
            if (DetectInContextWrites.IsIntent(intent))
            {
                var icwSpec = Specs().Find(s => s.Name == "detect_in_context_writes");
                var icwPlan = new IntentPlan { Confidence = 1.0 };
                var icwOp = new IntentOperation { Action = "detect_in_context_writes" };
                icwPlan.Operations.Add(icwOp);
                await ExecuteSpec(icwSpec, doc, icwPlan, icwOp, intent);
                return true;
            }
            // HandleUnknownFeatures (tool 243) — unknown/third-party/plugin/macro-feature vocabulary, disjoint
            // from DetectInContextWrites (in-context/external-reference/ripple).
            if (HandleUnknownFeatures.IsIntent(intent))
            {
                var hufSpec = Specs().Find(s => s.Name == "handle_unknown_features");
                var hufPlan = new IntentPlan { Confidence = 1.0 };
                var hufOp = new IntentOperation { Action = "handle_unknown_features" };
                hufPlan.Operations.Add(hufOp);
                await ExecuteSpec(hufSpec, doc, hufPlan, hufOp, intent);
                return true;
            }
            // HandleAssemblyFeatures (tool 250) — assembly-level cut/hole/fillet/chamfer/draft vocabulary,
            // disjoint from HandleUnknownFeatures (unknown/third-party type) and DetectInContextWrites
            // (external-file-reference risk) — this is about a feature's SCOPE, not its type or refs.
            if (HandleAssemblyFeatures.IsIntent(intent))
            {
                var hafSpec = Specs().Find(s => s.Name == "handle_assembly_features");
                var hafPlan = new IntentPlan { Confidence = 1.0 };
                var hafOp = new IntentOperation { Action = "handle_assembly_features" };
                hafPlan.Operations.Add(hafOp);
                await ExecuteSpec(hafSpec, doc, hafPlan, hafOp, intent);
                return true;
            }
            // TraceDerivedParts (tool 251) — derived/lineage/derivation vocabulary, or trace+chain/parts;
            // disjoint from DetectInContextWrites (one-hop, in-context/ripple wording).
            if (TraceDerivedParts.IsIntent(intent))
            {
                var tdpSpec = Specs().Find(s => s.Name == "trace_derived_parts");
                var tdpPlan = new IntentPlan { Confidence = 1.0 };
                var tdpOp = new IntentOperation { Action = "trace_derived_parts" };
                tdpPlan.Operations.Add(tdpOp);
                await ExecuteSpec(tdpSpec, doc, tdpPlan, tdpOp, intent);
                return true;
            }
            // RecoverAutosave (tool 253) — recover/restore/salvage + autosave/backup/crash vocabulary; disjoint
            // from HandleLockedFiles (lock/read-only) and DetectFileHealth (health/preflight/corruption).
            if (RecoverAutosave.IsIntent(intent))
            {
                var raSpec = Specs().Find(s => s.Name == "recover_autosave");
                var raPlan = new IntentPlan { Confidence = 1.0 };
                var raOp = new IntentOperation { Action = "recover_autosave" };
                raPlan.Operations.Add(raOp);
                await ExecuteSpec(raSpec, doc, raPlan, raOp, intent);
                return true;
            }
            // HandleConfigExplosion (tool 255) — "activate/rebuild ALL/EVERY config(s)" bulk phrasing or explicit
            // config-explosion vocabulary; SetActiveConfiguration/RebuildDocument both exclude the bulk phrasing.
            if (HandleConfigExplosion.IsIntent(intent))
            {
                var hceSpec = Specs().Find(s => s.Name == "handle_config_explosion");
                var hcePlan = new IntentPlan { Confidence = 1.0 };
                var hceOp = new IntentOperation { Action = "handle_config_explosion" };
                hcePlan.Operations.Add(hceOp);
                await ExecuteSpec(hceSpec, doc, hcePlan, hceOp, intent);
                return true;
            }
            // DetectSimulationArtifacts (tool 256) — weld-bead/belt-chain/sim-artifact vocabulary; disjoint from
            // HandleUnknownFeatures (generic third-party "MacroFeature" type, not a native feature type).
            if (DetectSimulationArtifacts.IsIntent(intent))
            {
                var dsaSpec = Specs().Find(s => s.Name == "detect_simulation_artifacts");
                var dsaPlan = new IntentPlan { Confidence = 1.0 };
                var dsaOp = new IntentOperation { Action = "detect_simulation_artifacts" };
                dsaPlan.Operations.Add(dsaOp);
                await ExecuteSpec(dsaSpec, doc, dsaPlan, dsaOp, intent);
                return true;
            }
            // QuarantineFile (tool 257) — quarantine/isolate+file/poltergeist vocabulary; Isolator.IsIsolateIntent
            // got a matching exclusion so it never shadows this in either direction.
            if (QuarantineFile.IsIntent(intent))
            {
                var qfSpec = Specs().Find(s => s.Name == "quarantine_file");
                var qfPlan = new IntentPlan { Confidence = 1.0 };
                var qfOp = new IntentOperation { Action = "quarantine_file" };
                qfPlan.Operations.Add(qfOp);
                await ExecuteSpec(qfSpec, doc, qfPlan, qfOp, intent);
                return true;
            }
            // KnitSurfacesToSolid (tool 181) — knit/stitch/sew + "surface(s)" is disjoint vocabulary from
            // CombineBodies' combine/merge/union/fuse/weld + body/bodies/solids, no ordering dependency needed.
            if (KnitSurfacesToSolid.IsIntent(intent))
            {
                var ksSpec = Specs().Find(s => s.Name == "knit_surfaces_to_solid");
                var ksPlan = new IntentPlan { Confidence = 1.0 };
                var ksOp = new IntentOperation { Action = "knit_surfaces_to_solid" };
                ksPlan.Operations.Add(ksOp);
                await ExecuteSpec(ksSpec, doc, ksPlan, ksOp, intent);
                return true;
            }
            // ArrangeDrawingAnnotations (tool 190) — excludes bom/balloon/revision-table nouns and check/audit/
            // lint/validate/scan verbs, disjoint from CleanBomTable/RepairBalloonReferences/CheckDraftingStandards.
            if (ArrangeDrawingAnnotations.IsIntent(intent))
            {
                var avSpec = Specs().Find(s => s.Name == "arrange_drawing_annotations");
                var avPlan = new IntentPlan { Confidence = 1.0 };
                var avOp = new IntentOperation { Action = "arrange_drawing_annotations" };
                avPlan.Operations.Add(avOp);
                await ExecuteSpec(avSpec, doc, avPlan, avOp, intent);
                return true;
            }

            // Feature-search READ family — live-dispatch gap fix (2026-07-29), each fenced by its own required
            // vocabulary, no ordering constraints among them.
            if (GetFeatureInfo.IsIntent(intent))
            {
                var gfiSpec = Specs().Find(s => s.Name == "get_feature_info");
                var gfiPlan = new IntentPlan { Confidence = 1.0 };
                var gfiOp = new IntentOperation { Action = "get_feature_info" };
                gfiPlan.Operations.Add(gfiOp);
                await ExecuteSpec(gfiSpec, doc, gfiPlan, gfiOp, intent);
                return true;
            }
            if (FindFeaturesByType.IsIntent(intent))
            {
                var fftSpec = Specs().Find(s => s.Name == "find_features_by_type");
                var fftPlan = new IntentPlan { Confidence = 1.0 };
                var fftOp = new IntentOperation { Action = "find_features_by_type" };
                fftPlan.Operations.Add(fftOp);
                await ExecuteSpec(fftSpec, doc, fftPlan, fftOp, intent);
                return true;
            }
            if (FindFeatureByName.IsIntent(intent))
            {
                var ffnSpec = Specs().Find(s => s.Name == "find_feature_by_name");
                var ffnPlan = new IntentPlan { Confidence = 1.0 };
                var ffnOp = new IntentOperation { Action = "find_feature_by_name" };
                ffnPlan.Operations.Add(ffnOp);
                await ExecuteSpec(ffnSpec, doc, ffnPlan, ffnOp, intent);
                return true;
            }
            if (FindWhereUsed.IsIntent(intent))
            {
                var fwuSpec = Specs().Find(s => s.Name == "find_where_used");
                var fwuPlan = new IntentPlan { Confidence = 1.0 };
                var fwuOp = new IntentOperation { Action = "find_where_used" };
                fwuPlan.Operations.Add(fwuOp);
                await ExecuteSpec(fwuSpec, doc, fwuPlan, fwuOp, intent);
                return true;
            }

            // Getter READ family — live-dispatch gap fix (2026-07-29). get_custom_properties has no IsIntent of
            // its own (cloud-classification only) so it has no intercept here — its HSpec registration alone
            // fixes it. get_cut_list runs here, before the existing GetBodies intercept further down.
            if (GetComponentTransform.IsIntent(intent))
            {
                var gctSpec = Specs().Find(s => s.Name == "get_component_transform");
                var gctPlan = new IntentPlan { Confidence = 1.0 };
                var gctOp = new IntentOperation { Action = "get_component_transform" };
                gctPlan.Operations.Add(gctOp);
                await ExecuteSpec(gctSpec, doc, gctPlan, gctOp, intent);
                return true;
            }
            if (GetMateInfo.IsIntent(intent))
            {
                var gmiSpec = Specs().Find(s => s.Name == "get_mate_info");
                var gmiPlan = new IntentPlan { Confidence = 1.0 };
                var gmiOp = new IntentOperation { Action = "get_mate_info" };
                gmiPlan.Operations.Add(gmiOp);
                await ExecuteSpec(gmiSpec, doc, gmiPlan, gmiOp, intent);
                return true;
            }
            if (GetActiveDocument.IsIntent(intent))
            {
                var gadSpec = Specs().Find(s => s.Name == "get_active_document");
                var gadPlan = new IntentPlan { Confidence = 1.0 };
                var gadOp = new IntentOperation { Action = "get_active_document" };
                gadPlan.Operations.Add(gadOp);
                await ExecuteSpec(gadSpec, doc, gadPlan, gadOp, intent);
                return true;
            }
            if (GetFaces.IsIntent(intent))
            {
                var gfSpec = Specs().Find(s => s.Name == "get_face_normal");
                var gfPlan = new IntentPlan { Confidence = 1.0 };
                var gfOp = new IntentOperation { Action = "get_face_normal" };
                gfPlan.Operations.Add(gfOp);
                await ExecuteSpec(gfSpec, doc, gfPlan, gfOp, intent);
                return true;
            }
            if (GetMaterialDensity.IsIntent(intent))
            {
                var gmdSpec = Specs().Find(s => s.Name == "get_material_density");
                var gmdPlan = new IntentPlan { Confidence = 1.0 };
                var gmdOp = new IntentOperation { Action = "get_material_density" };
                gmdPlan.Operations.Add(gmdOp);
                await ExecuteSpec(gmdSpec, doc, gmdPlan, gmdOp, intent);
                return true;
            }
            if (SetSheetMetalThickness.IsIntent(intent))
            {
                var ssmtSpec = Specs().Find(s => s.Name == "set_sheet_metal_thickness");
                var ssmtPlan = new IntentPlan { Confidence = 1.0 };
                var ssmtOp = new IntentOperation { Action = "set_sheet_metal_thickness" };
                ssmtPlan.Operations.Add(ssmtOp);
                await ExecuteSpec(ssmtSpec, doc, ssmtPlan, ssmtOp, intent);
                return true;
            }
            if (GetSheetMetalProps.IsIntent(intent))
            {
                var gsmpSpec = Specs().Find(s => s.Name == "get_sheet_metal_properties");
                var gsmpPlan = new IntentPlan { Confidence = 1.0 };
                var gsmpOp = new IntentOperation { Action = "get_sheet_metal_properties" };
                gsmpPlan.Operations.Add(gsmpOp);
                await ExecuteSpec(gsmpSpec, doc, gsmpPlan, gsmpOp, intent);
                return true;
            }
            if (GetPartNumber.IsIntent(intent))
            {
                var gpnSpec = Specs().Find(s => s.Name == "get_part_number");
                var gpnPlan = new IntentPlan { Confidence = 1.0 };
                var gpnOp = new IntentOperation { Action = "get_part_number" };
                gpnPlan.Operations.Add(gpnOp);
                await ExecuteSpec(gpnSpec, doc, gpnPlan, gpnOp, intent);
                return true;
            }
            if (GetAppearance.IsIntent(intent))
            {
                var gapSpec = Specs().Find(s => s.Name == "get_appearance");
                var gapPlan = new IntentPlan { Confidence = 1.0 };
                var gapOp = new IntentOperation { Action = "get_appearance" };
                gapPlan.Operations.Add(gapOp);
                await ExecuteSpec(gapSpec, doc, gapPlan, gapOp, intent);
                return true;
            }
            if (GetRebuildErrors.IsIntent(intent))
            {
                var greSpec = Specs().Find(s => s.Name == "get_rebuild_errors");
                var grePlan = new IntentPlan { Confidence = 1.0 };
                var greOp = new IntentOperation { Action = "get_rebuild_errors" };
                grePlan.Operations.Add(greOp);
                await ExecuteSpec(greSpec, doc, grePlan, greOp, intent);
                return true;
            }
            if (HandleRollbackBar.IsIntent(intent))
            {
                var hrbSpec = Specs().Find(s => s.Name == "handle_rollback_bar");
                var hrbPlan = new IntentPlan { Confidence = 1.0 };
                var hrbOp = new IntentOperation { Action = "handle_rollback_bar" };
                hrbPlan.Operations.Add(hrbOp);
                await ExecuteSpec(hrbSpec, doc, hrbPlan, hrbOp, intent);
                return true;
            }
            if (GetComponentMass.IsIntent(intent))
            {
                var gcmSpec = Specs().Find(s => s.Name == "get_component_mass");
                var gcmPlan = new IntentPlan { Confidence = 1.0 };
                var gcmOp = new IntentOperation { Action = "get_component_mass" };
                gcmPlan.Operations.Add(gcmOp);
                await ExecuteSpec(gcmSpec, doc, gcmPlan, gcmOp, intent);
                return true;
            }
            // Sketch-diagnostic WRITE/READ trio — live-dispatch gap fix (2026-07-29). Harness.cs IntentDispatch's own
            // proven order is FullyDefineSketch -> DiagnoseSketch -> DetectSharedSketches -> GetSketches (each more
            // specific than the plain sketch-list READ below), so all three run BEFORE GetSketches here too.
            if (FullyDefineSketch.IsIntent(intent))
            {
                var fdsSpec = Specs().Find(s => s.Name == "fully_define_sketch");
                var fdsPlan = new IntentPlan { Confidence = 1.0 };
                var fdsOp = new IntentOperation { Action = "fully_define_sketch" };
                fdsPlan.Operations.Add(fdsOp);
                await ExecuteSpec(fdsSpec, doc, fdsPlan, fdsOp, intent);
                return true;
            }
            if (DiagnoseSketch.IsIntent(intent))
            {
                var dsSpec = Specs().Find(s => s.Name == "diagnose_sketch");
                var dsPlan = new IntentPlan { Confidence = 1.0 };
                var dsOp = new IntentOperation { Action = "diagnose_sketch" };
                dsPlan.Operations.Add(dsOp);
                await ExecuteSpec(dsSpec, doc, dsPlan, dsOp, intent);
                return true;
            }
            if (DetectSharedSketches.IsIntent(intent))
            {
                var dssSpec = Specs().Find(s => s.Name == "detect_shared_sketches");
                var dssPlan = new IntentPlan { Confidence = 1.0 };
                var dssOp = new IntentOperation { Action = "detect_shared_sketches" };
                dssPlan.Operations.Add(dssOp);
                await ExecuteSpec(dssSpec, doc, dssPlan, dssOp, intent);
                return true;
            }
            if (GetSketches.IsIntent(intent))
            {
                var gsSpec = Specs().Find(s => s.Name == "get_sketch_info");
                var gsPlan = new IntentPlan { Confidence = 1.0 };
                var gsOp = new IntentOperation { Action = "get_sketch_info" };
                gsPlan.Operations.Add(gsOp);
                await ExecuteSpec(gsSpec, doc, gsPlan, gsOp, intent);
                return true;
            }
            if (GetPatternInfo.IsIntent(intent))
            {
                var gpiSpec = Specs().Find(s => s.Name == "get_pattern_info");
                var gpiPlan = new IntentPlan { Confidence = 1.0 };
                var gpiOp = new IntentOperation { Action = "get_pattern_info" };
                gpiPlan.Operations.Add(gpiOp);
                await ExecuteSpec(gpiSpec, doc, gpiPlan, gpiOp, intent);
                return true;
            }
            if (GetCutList.IsIntent(intent))
            {
                var gclSpec = Specs().Find(s => s.Name == "get_cut_list");
                var gclPlan = new IntentPlan { Confidence = 1.0 };
                var gclOp = new IntentOperation { Action = "get_cut_list" };
                gclPlan.Operations.Add(gclOp);
                await ExecuteSpec(gclSpec, doc, gclPlan, gclOp, intent);
                return true;
            }
            // PackAndGo (tool 133) BEFORE GetFileReferences: same specific-first ordering as Harness.cs.
            if (PackAndGo.IsIntent(intent))
            {
                var pagSpec = Specs().Find(s => s.Name == "pack_and_go");
                var pagPlan = new IntentPlan { Confidence = 1.0 };
                var pagOp = new IntentOperation { Action = "pack_and_go" };
                pagPlan.Operations.Add(pagOp);
                await ExecuteSpec(pagSpec, doc, pagPlan, pagOp, intent);
                return true;
            }
            // RenameFileWithReferences BEFORE RenameComponent's own intercept below: both can share "rename ... to",
            // but this one requires the "file" noun (RenameComponent now excludes it) — same ordering as Harness.cs.
            if (RenameFileWithReferences.IsIntent(intent))
            {
                var rfrSpec = Specs().Find(s => s.Name == "rename_file_with_references");
                var rfrPlan = new IntentPlan { Confidence = 1.0 };
                var rfrOp = new IntentOperation { Action = "rename_file_with_references" };
                rfrPlan.Operations.Add(rfrOp);
                await ExecuteSpec(rfrSpec, doc, rfrPlan, rfrOp, intent);
                return true;
            }
            // MoveFileWithReferences: same "file"-noun-gated ordering as RenameFileWithReferences above, checked
            // before any component/assembly/feature "move" intercept.
            if (MoveFileWithReferences.IsIntent(intent))
            {
                var mfrSpec = Specs().Find(s => s.Name == "move_file_with_references");
                var mfrPlan = new IntentPlan { Confidence = 1.0 };
                var mfrOp = new IntentOperation { Action = "move_file_with_references" };
                mfrPlan.Operations.Add(mfrOp);
                await ExecuteSpec(mfrSpec, doc, mfrPlan, mfrOp, intent);
                return true;
            }
            // UpdateSheetReferences BEFORE RepairMissingReferences: its drawing-scope noun requirement (sheet/
            // drawing/view) makes it the strictly more specific of the two, same ordering as Harness.cs Dispatch().
            if (UpdateSheetReferences.IsIntent(intent))
            {
                var usrSpec = Specs().Find(s => s.Name == "update_sheet_references");
                var usrPlan = new IntentPlan { Confidence = 1.0 };
                var usrOp = new IntentOperation { Action = "update_sheet_references" };
                usrPlan.Operations.Add(usrOp);
                await ExecuteSpec(usrSpec, doc, usrPlan, usrOp, intent);
                return true;
            }
            // InsertBomTable: "bom"/"bill of materials" is a vocabulary no other matcher claims, so no ordering
            // dependency needed — checked here anyway alongside its file/drawing-family siblings.
            if (InsertBomTable.IsIntent(intent))
            {
                var ibtSpec = Specs().Find(s => s.Name == "insert_bom_table");
                var ibtPlan = new IntentPlan { Confidence = 1.0 };
                var ibtOp = new IntentOperation { Action = "insert_bom_table" };
                ibtPlan.Operations.Add(ibtOp);
                await ExecuteSpec(ibtSpec, doc, ibtPlan, ibtOp, intent);
                return true;
            }
            // CleanBomTable: same "bom" noun as InsertBomTable but a clean-ish verb and an explicit insert-verb
            // exclusion, so the two matchers are disjoint by vocabulary alone — no ordering dependency needed.
            if (CleanBomTable.IsIntent(intent))
            {
                var cbtSpec = Specs().Find(s => s.Name == "clean_bom_table");
                var cbtPlan = new IntentPlan { Confidence = 1.0 };
                var cbtOp = new IntentOperation { Action = "clean_bom_table" };
                cbtPlan.Operations.Add(cbtOp);
                await ExecuteSpec(cbtSpec, doc, cbtPlan, cbtOp, intent);
                return true;
            }
            // RepairBalloonReferences: "balloon(s)" is a vocabulary no other matcher claims, so no ordering
            // dependency needed.
            if (RepairBalloonReferences.IsIntent(intent))
            {
                var rbrSpec = Specs().Find(s => s.Name == "repair_balloon_references");
                var rbrPlan = new IntentPlan { Confidence = 1.0 };
                var rbrOp = new IntentOperation { Action = "repair_balloon_references" };
                rbrPlan.Operations.Add(rbrOp);
                await ExecuteSpec(rbrSpec, doc, rbrPlan, rbrOp, intent);
                return true;
            }
            // InsertSectionView: the explicit "section" word (and excluded "detail") is a vocabulary no other
            // drawing-view matcher claims, so no ordering dependency needed.
            if (InsertSectionView.IsIntent(intent))
            {
                var isvSpec = Specs().Find(s => s.Name == "insert_section_view");
                var isvPlan = new IntentPlan { Confidence = 1.0 };
                var isvOp = new IntentOperation { Action = "insert_section_view" };
                isvPlan.Operations.Add(isvOp);
                await ExecuteSpec(isvSpec, doc, isvPlan, isvOp, intent);
                return true;
            }
            // InsertDetailView: the explicit "detail" word is a vocabulary no other drawing-view matcher claims,
            // so no ordering dependency needed.
            if (InsertDetailView.IsIntent(intent))
            {
                var idvSpec = Specs().Find(s => s.Name == "insert_detail_view");
                var idvPlan = new IntentPlan { Confidence = 1.0 };
                var idvOp = new IntentOperation { Action = "insert_detail_view" };
                idvPlan.Operations.Add(idvOp);
                await ExecuteSpec(idvSpec, doc, idvPlan, idvOp, intent);
                return true;
            }
            // AddDrawingDimension: excludes "model"/"dangling"/"broken"/"repair"/"reattach" and any standalone
            // numeric value, so it's disjoint from import_model_dimensions/list_dangling_dimensions/set_dimension.
            if (AddDrawingDimension.IsIntent(intent))
            {
                var addSpec = Specs().Find(s => s.Name == "add_drawing_dimension");
                var addPlan = new IntentPlan { Confidence = 1.0 };
                var addOp = new IntentOperation { Action = "add_drawing_dimension" };
                addPlan.Operations.Add(addOp);
                await ExecuteSpec(addSpec, doc, addPlan, addOp, intent);
                return true;
            }
            // RepairMate BEFORE RepairMissingReferences: requires the explicit "mate(s)" noun, which 132's noun set
            // (reference/component/file) never includes, so "repair the broken mate" is unambiguous either order —
            // checked first anyway per the specific-first convention.
            if (RepairMate.IsIntent(intent))
            {
                var rmSpec = Specs().Find(s => s.Name == "repair_mate");
                var rmPlan = new IntentPlan { Confidence = 1.0 };
                var rmOp = new IntentOperation { Action = "repair_mate" };
                rmPlan.Operations.Add(rmOp);
                await ExecuteSpec(rmSpec, doc, rmPlan, rmOp, intent);
                return true;
            }
            // RepairMissingReferences BEFORE GetFileReferences: a repair/fix/resolve verb + missing/broken wording
            // is more specific than GetFileReferences' plain query verbs, checked first anyway per convention.
            if (RepairMissingReferences.IsIntent(intent))
            {
                var rmrSpec = Specs().Find(s => s.Name == "repair_missing_references");
                var rmrPlan = new IntentPlan { Confidence = 1.0 };
                var rmrOp = new IntentOperation { Action = "repair_missing_references" };
                rmrPlan.Operations.Add(rmrOp);
                await ExecuteSpec(rmrSpec, doc, rmrPlan, rmrOp, intent);
                return true;
            }
            if (GetFileReferences.IsIntent(intent))
            {
                var gfrSpec = Specs().Find(s => s.Name == "get_file_references");
                var gfrPlan = new IntentPlan { Confidence = 1.0 };
                var gfrOp = new IntentOperation { Action = "get_file_references" };
                gfrPlan.Operations.Add(gfrOp);
                await ExecuteSpec(gfrSpec, doc, gfrPlan, gfrOp, intent);
                return true;
            }
            // ListDanglingDimensions (tool 110) BEFORE GetDrawingViews/DrawingPkg: both would also fire on
            // "dangling dims on this drawing" phrasing, so the narrower dangling-specific matcher goes first.
            if (ListDanglingDimensions.IsIntent(intent))
            {
                var lddSpec = Specs().Find(s => s.Name == "list_dangling_dimensions");
                var lddPlan = new IntentPlan { Confidence = 1.0 };
                var lddOp = new IntentOperation { Action = "list_dangling_dimensions" };
                lddPlan.Operations.Add(lddOp);
                await ExecuteSpec(lddSpec, doc, lddPlan, lddOp, intent);
                return true;
            }
            if (GetDrawingViews.IsIntent(intent))
            {
                var gdvSpec = Specs().Find(s => s.Name == "get_drawing_views");
                var gdvPlan = new IntentPlan { Confidence = 1.0 };
                var gdvOp = new IntentOperation { Action = "get_drawing_views" };
                gdvPlan.Operations.Add(gdvOp);
                await ExecuteSpec(gdvSpec, doc, gdvPlan, gdvOp, intent);
                return true;
            }
            if (GetDrivingDimensions.IsIntent(intent))
            {
                var gddSpec = Specs().Find(s => s.Name == "get_driving_dimensions");
                var gddPlan = new IntentPlan { Confidence = 1.0 };
                var gddOp = new IntentOperation { Action = "get_driving_dimensions" };
                gddPlan.Operations.Add(gddOp);
                await ExecuteSpec(gddSpec, doc, gddPlan, gddOp, intent);
                return true;
            }

            // Sketch-to-solid feature-create WRITE family — live-dispatch gap fix (2026-07-29), each fenced by its
            // own distinctive keyword, no ordering constraints among them.
            if (AddRevolve.IsIntent(intent))
            {
                var arSpec = Specs().Find(s => s.Name == "add_revolve");
                var arPlan = new IntentPlan { Confidence = 1.0 };
                var arOp = new IntentOperation { Action = "add_revolve" };
                arPlan.Operations.Add(arOp);
                await ExecuteSpec(arSpec, doc, arPlan, arOp, intent);
                return true;
            }
            if (AddSweep.IsIntent(intent))
            {
                var aswSpec = Specs().Find(s => s.Name == "add_sweep");
                var aswPlan = new IntentPlan { Confidence = 1.0 };
                var aswOp = new IntentOperation { Action = "add_sweep" };
                aswPlan.Operations.Add(aswOp);
                await ExecuteSpec(aswSpec, doc, aswPlan, aswOp, intent);
                return true;
            }
            if (AddLoft.IsIntent(intent))
            {
                var alSpec = Specs().Find(s => s.Name == "add_loft");
                var alPlan = new IntentPlan { Confidence = 1.0 };
                var alOp = new IntentOperation { Action = "add_loft" };
                alPlan.Operations.Add(alOp);
                await ExecuteSpec(alSpec, doc, alPlan, alOp, intent);
                return true;
            }
            if (AddHelix.IsIntent(intent))
            {
                var ahSpec = Specs().Find(s => s.Name == "add_helix");
                var ahPlan = new IntentPlan { Confidence = 1.0 };
                var ahOp = new IntentOperation { Action = "add_helix" };
                ahPlan.Operations.Add(ahOp);
                await ExecuteSpec(ahSpec, doc, ahPlan, ahOp, intent);
                return true;
            }
            if (AddDome.IsIntent(intent))
            {
                var adSpec = Specs().Find(s => s.Name == "add_dome");
                var adPlan = new IntentPlan { Confidence = 1.0 };
                var adOp = new IntentOperation { Action = "add_dome" };
                adPlan.Operations.Add(adOp);
                await ExecuteSpec(adSpec, doc, adPlan, adOp, intent);
                return true;
            }
            if (CreateThicken.IsIntent(intent))
            {
                var ctSpec = Specs().Find(s => s.Name == "create_thicken");
                var ctPlan = new IntentPlan { Confidence = 1.0 };
                var ctOp = new IntentOperation { Action = "create_thicken" };
                ctPlan.Operations.Add(ctOp);
                await ExecuteSpec(ctSpec, doc, ctPlan, ctOp, intent);
                return true;
            }

            // Sketch-entity ADD WRITE family — live-dispatch gap fix (2026-07-29), each fenced by its own
            // distinctive noun, no ordering constraints among them.
            if (AddConstructionGeometry.IsIntent(intent))
            {
                var acgSpec = Specs().Find(s => s.Name == "add_construction_geometry");
                var acgPlan = new IntentPlan { Confidence = 1.0 };
                var acgOp = new IntentOperation { Action = "add_construction_geometry" };
                acgPlan.Operations.Add(acgOp);
                await ExecuteSpec(acgSpec, doc, acgPlan, acgOp, intent);
                return true;
            }
            if (AddSketchArc.IsIntent(intent))
            {
                var asaSpec = Specs().Find(s => s.Name == "add_sketch_arc");
                var asaPlan = new IntentPlan { Confidence = 1.0 };
                var asaOp = new IntentOperation { Action = "add_sketch_arc" };
                asaPlan.Operations.Add(asaOp);
                await ExecuteSpec(asaSpec, doc, asaPlan, asaOp, intent);
                return true;
            }
            if (AddSketchEntity.IsIntent(intent))
            {
                var aseeSpec = Specs().Find(s => s.Name == "add_sketch_entity");
                var aseePlan = new IntentPlan { Confidence = 1.0 };
                var aseeOp = new IntentOperation { Action = "add_sketch_entity" };
                aseePlan.Operations.Add(aseeOp);
                await ExecuteSpec(aseeSpec, doc, aseePlan, aseeOp, intent);
                return true;
            }
            if (AddSketchDimension.IsIntent(intent))
            {
                var asdSpec = Specs().Find(s => s.Name == "add_sketch_dimension");
                var asdPlan = new IntentPlan { Confidence = 1.0 };
                var asdOp = new IntentOperation { Action = "add_sketch_dimension" };
                asdPlan.Operations.Add(asdOp);
                await ExecuteSpec(asdSpec, doc, asdPlan, asdOp, intent);
                return true;
            }
            // AddSketchRelation (tool 84) is deliberately NOT wired into live dispatch: ISketchRelationManager.
            // AddRelation CRASHED SolidWorks headless on this build (2026-07-30, see BUILD-LOG) — a live user
            // command matching AddSketchRelation.IsIntent must NOT be able to reach it. Harness.cs still carries
            // the dormant dispatch (harness-only, inert without a test-config case) for future re-investigation.
            if (Create3DSketch.IsIntent(intent))
            {
                var c3dSpec = Specs().Find(s => s.Name == "create_3d_sketch");
                var c3dPlan = new IntentPlan { Confidence = 1.0 };
                var c3dOp = new IntentOperation { Action = "create_3d_sketch" };
                c3dPlan.Operations.Add(c3dOp);
                await ExecuteSpec(c3dSpec, doc, c3dPlan, c3dOp, intent);
                return true;
            }
            if (ImportDxfToSketch.IsIntent(intent))
            {
                var idxSpec = Specs().Find(s => s.Name == "import_dxf_to_sketch");
                var idxPlan = new IntentPlan { Confidence = 1.0 };
                var idxOp = new IntentOperation { Action = "import_dxf_to_sketch" };
                idxPlan.Operations.Add(idxOp);
                await ExecuteSpec(idxSpec, doc, idxPlan, idxOp, intent);
                return true;
            }
            if (CreateLayoutSketch.IsIntent(intent))
            {
                var clsSpec = Specs().Find(s => s.Name == "create_layout_sketch");
                var clsPlan = new IntentPlan { Confidence = 1.0 };
                var clsOp = new IntentOperation { Action = "create_layout_sketch" };
                clsPlan.Operations.Add(clsOp);
                await ExecuteSpec(clsSpec, doc, clsPlan, clsOp, intent);
                return true;
            }
            if (CreateSketch.IsIntent(intent))
            {
                var csSpec = Specs().Find(s => s.Name == "create_sketch");
                var csPlan = new IntentPlan { Confidence = 1.0 };
                var csOp = new IntentOperation { Action = "create_sketch" };
                csPlan.Operations.Add(csOp);
                await ExecuteSpec(csSpec, doc, csPlan, csOp, intent);
                return true;
            }
            if (AddSketchEllipse.IsIntent(intent))
            {
                var aseSpec = Specs().Find(s => s.Name == "add_sketch_ellipse");
                var asePlan = new IntentPlan { Confidence = 1.0 };
                var aseOp = new IntentOperation { Action = "add_sketch_ellipse" };
                asePlan.Operations.Add(aseOp);
                await ExecuteSpec(aseSpec, doc, asePlan, aseOp, intent);
                return true;
            }
            if (AddSketchPolygon.IsIntent(intent))
            {
                var aspSpec = Specs().Find(s => s.Name == "add_sketch_polygon");
                var aspPlan = new IntentPlan { Confidence = 1.0 };
                var aspOp = new IntentOperation { Action = "add_sketch_polygon" };
                aspPlan.Operations.Add(aspOp);
                await ExecuteSpec(aspSpec, doc, aspPlan, aspOp, intent);
                return true;
            }
            if (AddSketchSlot.IsIntent(intent))
            {
                var assSpec = Specs().Find(s => s.Name == "add_sketch_slot");
                var assPlan = new IntentPlan { Confidence = 1.0 };
                var assOp = new IntentOperation { Action = "add_sketch_slot" };
                assPlan.Operations.Add(assOp);
                await ExecuteSpec(assSpec, doc, assPlan, assOp, intent);
                return true;
            }
            if (AddSketchSpline.IsIntent(intent))
            {
                var aslSpec = Specs().Find(s => s.Name == "add_sketch_spline");
                var aslPlan = new IntentPlan { Confidence = 1.0 };
                var aslOp = new IntentOperation { Action = "add_sketch_spline" };
                aslPlan.Operations.Add(aslOp);
                await ExecuteSpec(aslSpec, doc, aslPlan, aslOp, intent);
                return true;
            }
            if (AddSketchText.IsIntent(intent))
            {
                var astSpec = Specs().Find(s => s.Name == "add_sketch_text");
                var astPlan = new IntentPlan { Confidence = 1.0 };
                var astOp = new IntentOperation { Action = "add_sketch_text" };
                astPlan.Operations.Add(astOp);
                await ExecuteSpec(astSpec, doc, astPlan, astOp, intent);
                return true;
            }

            // Measure READ pair + sketch-tool WRITE family — live-dispatch gap fix (2026-07-29). measure_distance/
            // measure_angle each exclude the mate-add verbs so they never collide with add_distance_mate/
            // add_angle_mate. offset/convert/trim-extend/mirror are disjoint by verb (pattern_sketch_entities is
            // wired earlier, before the pattern-edit family, for the ordering reason noted there).
            if (MeasureDistance.IsIntent(intent))
            {
                var mdSpec = Specs().Find(s => s.Name == "measure_distance");
                var mdPlan = new IntentPlan { Confidence = 1.0 };
                var mdOp = new IntentOperation { Action = "measure_distance" };
                mdPlan.Operations.Add(mdOp);
                await ExecuteSpec(mdSpec, doc, mdPlan, mdOp, intent);
                return true;
            }
            if (MeasureAngle.IsIntent(intent))
            {
                var maSpec = Specs().Find(s => s.Name == "measure_angle");
                var maPlan = new IntentPlan { Confidence = 1.0 };
                var maOp = new IntentOperation { Action = "measure_angle" };
                maPlan.Operations.Add(maOp);
                await ExecuteSpec(maSpec, doc, maPlan, maOp, intent);
                return true;
            }
            if (OffsetSketchEntities.IsIntent(intent))
            {
                var oseSpec = Specs().Find(s => s.Name == "offset_sketch_entities");
                var osePlan = new IntentPlan { Confidence = 1.0 };
                var oseOp = new IntentOperation { Action = "offset_sketch_entities" };
                osePlan.Operations.Add(oseOp);
                await ExecuteSpec(oseSpec, doc, osePlan, oseOp, intent);
                return true;
            }
            if (ConvertEntities.IsIntent(intent))
            {
                var ceSpec = Specs().Find(s => s.Name == "convert_entities");
                var cePlan = new IntentPlan { Confidence = 1.0 };
                var ceOp = new IntentOperation { Action = "convert_entities" };
                cePlan.Operations.Add(ceOp);
                await ExecuteSpec(ceSpec, doc, cePlan, ceOp, intent);
                return true;
            }
            if (TrimExtendSketch.IsIntent(intent))
            {
                var teSpec = Specs().Find(s => s.Name == "trim_extend");
                var tePlan = new IntentPlan { Confidence = 1.0 };
                var teOp = new IntentOperation { Action = "trim_extend" };
                tePlan.Operations.Add(teOp);
                await ExecuteSpec(teSpec, doc, tePlan, teOp, intent);
                return true;
            }
            if (MirrorSketchEntities.IsIntent(intent))
            {
                var mseSpec = Specs().Find(s => s.Name == "mirror_sketch_entities");
                var msePlan = new IntentPlan { Confidence = 1.0 };
                var mseOp = new IntentOperation { Action = "mirror_sketch_entities" };
                msePlan.Operations.Add(mseOp);
                await ExecuteSpec(mseSpec, doc, msePlan, mseOp, intent);
                return true;
            }

            // Reference-geometry + surface/rib feature-create WRITE family — live-dispatch gap fix (2026-07-29),
            // each fenced by its own distinctive noun, no ordering constraints among them.
            if (CreateBoundaryFeature.IsIntent(intent))
            {
                var cbfSpec = Specs().Find(s => s.Name == "create_boundary_feature");
                var cbfPlan = new IntentPlan { Confidence = 1.0 };
                var cbfOp = new IntentOperation { Action = "create_boundary_feature" };
                cbfPlan.Operations.Add(cbfOp);
                await ExecuteSpec(cbfSpec, doc, cbfPlan, cbfOp, intent);
                return true;
            }
            if (CreateCoordSys.IsIntent(intent))
            {
                var ccsSpec = Specs().Find(s => s.Name == "create_coordinate_system");
                var ccsPlan = new IntentPlan { Confidence = 1.0 };
                var ccsOp = new IntentOperation { Action = "create_coordinate_system" };
                ccsPlan.Operations.Add(ccsOp);
                await ExecuteSpec(ccsSpec, doc, ccsPlan, ccsOp, intent);
                return true;
            }
            if (CreateCurve.IsIntent(intent))
            {
                var ccSpec = Specs().Find(s => s.Name == "create_curve");
                var ccPlan = new IntentPlan { Confidence = 1.0 };
                var ccOp = new IntentOperation { Action = "create_curve" };
                ccPlan.Operations.Add(ccOp);
                await ExecuteSpec(ccSpec, doc, ccPlan, ccOp, intent);
                return true;
            }
            if (CreateExtrudedSurface.IsIntent(intent))
            {
                var cesSpec = Specs().Find(s => s.Name == "create_extruded_surface");
                var cesPlan = new IntentPlan { Confidence = 1.0 };
                var cesOp = new IntentOperation { Action = "create_extruded_surface" };
                cesPlan.Operations.Add(cesOp);
                await ExecuteSpec(cesSpec, doc, cesPlan, cesOp, intent);
                return true;
            }
            if (CaptureViewport.IsIntent(intent))
            {
                var cvSpec = Specs().Find(s => s.Name == "capture_viewport");
                var cvPlan = new IntentPlan { Confidence = 1.0 };
                var cvOp = new IntentOperation { Action = "capture_viewport" };
                cvPlan.Operations.Add(cvOp);
                await ExecuteSpec(cvSpec, doc, cvPlan, cvOp, intent);
                return true;
            }
            if (CaptureSection.IsIntent(intent))
            {
                var csSpec = Specs().Find(s => s.Name == "capture_section");
                var csPlan = new IntentPlan { Confidence = 1.0 };
                var csOp = new IntentOperation { Action = "capture_section" };
                csPlan.Operations.Add(csOp);
                await ExecuteSpec(csSpec, doc, csPlan, csOp, intent);
                return true;
            }
            if (GetSelectedEntities.IsIntent(intent))
            {
                // BEFORE SelectFace/Component/Edge/Plane below — see Harness.cs's IntentDispatch comment for why
                // (compound test phrasing also contains "select"+"face", the READ verb here is the tie-breaker).
                var gseSpec = Specs().Find(s => s.Name == "get_selected_entities");
                var gsePlan = new IntentPlan { Confidence = 1.0 };
                var gseOp = new IntentOperation { Action = "get_selected_entities" };
                gsePlan.Operations.Add(gseOp);
                await ExecuteSpec(gseSpec, doc, gsePlan, gseOp, intent);
                return true;
            }
            if (ClearSelection.IsIntent(intent))
            {
                var csSpec = Specs().Find(s => s.Name == "clear_selection");
                var csPlan = new IntentPlan { Confidence = 1.0 };
                var csOp = new IntentOperation { Action = "clear_selection" };
                csPlan.Operations.Add(csOp);
                await ExecuteSpec(csSpec, doc, csPlan, csOp, intent);
                return true;
            }
            if (SelectFace.IsIntent(intent))
            {
                var sfSpec = Specs().Find(s => s.Name == "select_face");
                var sfPlan = new IntentPlan { Confidence = 1.0 };
                var sfOp = new IntentOperation { Action = "select_face" };
                sfPlan.Operations.Add(sfOp);
                await ExecuteSpec(sfSpec, doc, sfPlan, sfOp, intent);
                return true;
            }
            if (SelectComponent.IsIntent(intent))
            {
                var scSpec = Specs().Find(s => s.Name == "select_component");
                var scPlan = new IntentPlan { Confidence = 1.0 };
                var scOp = new IntentOperation { Action = "select_component" };
                scPlan.Operations.Add(scOp);
                await ExecuteSpec(scSpec, doc, scPlan, scOp, intent);
                return true;
            }
            if (SelectEdge.IsIntent(intent))
            {
                var seSpec = Specs().Find(s => s.Name == "select_edge");
                var sePlan = new IntentPlan { Confidence = 1.0 };
                var seOp = new IntentOperation { Action = "select_edge" };
                sePlan.Operations.Add(seOp);
                await ExecuteSpec(seSpec, doc, sePlan, seOp, intent);
                return true;
            }
            if (SelectPlane.IsIntent(intent))
            {
                var spSpec = Specs().Find(s => s.Name == "select_plane");
                var spPlan = new IntentPlan { Confidence = 1.0 };
                var spOp = new IntentOperation { Action = "select_plane" };
                spPlan.Operations.Add(spOp);
                await ExecuteSpec(spSpec, doc, spPlan, spOp, intent);
                return true;
            }
            if (CreateRefAxis.IsIntent(intent))
            {
                var craSpec = Specs().Find(s => s.Name == "create_reference_axis");
                var craPlan = new IntentPlan { Confidence = 1.0 };
                var craOp = new IntentOperation { Action = "create_reference_axis" };
                craPlan.Operations.Add(craOp);
                await ExecuteSpec(craSpec, doc, craPlan, craOp, intent);
                return true;
            }
            if (CreateRib.IsIntent(intent))
            {
                var crSpec = Specs().Find(s => s.Name == "create_rib");
                var crPlan = new IntentPlan { Confidence = 1.0 };
                var crOp = new IntentOperation { Action = "create_rib" };
                crPlan.Operations.Add(crOp);
                await ExecuteSpec(crSpec, doc, crPlan, crOp, intent);
                return true;
            }
            if (CreateSweptSurface.IsIntent(intent))
            {
                var cssSpec = Specs().Find(s => s.Name == "create_swept_lofted_surface");
                var cssPlan = new IntentPlan { Confidence = 1.0 };
                var cssOp = new IntentOperation { Action = "create_swept_lofted_surface" };
                cssPlan.Operations.Add(cssOp);
                await ExecuteSpec(cssSpec, doc, cssPlan, cssOp, intent);
                return true;
            }
            if (CreateTreeFolder.IsIntent(intent))
            {
                var ctfSpec = Specs().Find(s => s.Name == "create_tree_folder");
                var ctfPlan = new IntentPlan { Confidence = 1.0 };
                var ctfOp = new IntentOperation { Action = "create_tree_folder" };
                ctfPlan.Operations.Add(ctfOp);
                await ExecuteSpec(ctfSpec, doc, ctfPlan, ctfOp, intent);
                return true;
            }
            if (CreateVariableFillet.IsIntent(intent))
            {
                var cvfSpec = Specs().Find(s => s.Name == "create_variable_fillet");
                var cvfPlan = new IntentPlan { Confidence = 1.0 };
                var cvfOp = new IntentOperation { Action = "create_variable_fillet" };
                cvfPlan.Operations.Add(cvfOp);
                await ExecuteSpec(cvfSpec, doc, cvfPlan, cvfOp, intent);
                return true;
            }

            // Final live-dispatch-gap sweep — live-dispatch gap fix (2026-07-29). edit_feature_parameter runs
            // before the generic set_dimension fallback (LocalActionFor, further down); reorder_feature has no
            // IsIntent of its own so it has no intercept here (HSpec registration alone fixes it).
            if (SelectByFilter.IsIntent(intent))
            {
                var sbfSpec = Specs().Find(s => s.Name == "select_components_by_filter");
                var sbfPlan = new IntentPlan { Confidence = 1.0 };
                var sbfOp = new IntentOperation { Action = "select_components_by_filter" };
                sbfPlan.Operations.Add(sbfOp);
                await ExecuteSpec(sbfSpec, doc, sbfPlan, sbfOp, intent);
                return true;
            }
            if (BatchUpdateMaterials.IsIntent(intent))
            {
                var bumSpec = Specs().Find(s => s.Name == "batch_update_materials");
                var bumPlan = new IntentPlan { Confidence = 1.0 };
                var bumOp = new IntentOperation { Action = "batch_update_materials" };
                bumPlan.Operations.Add(bumOp);
                await ExecuteSpec(bumSpec, doc, bumPlan, bumOp, intent);
                return true;
            }
            if (BatchUpdateCustomProperties.IsIntent(intent))
            {
                var bucSpec = Specs().Find(s => s.Name == "batch_update_custom_properties");
                var bucPlan = new IntentPlan { Confidence = 1.0 };
                var bucOp = new IntentOperation { Action = "batch_update_custom_properties" };
                bucPlan.Operations.Add(bucOp);
                await ExecuteSpec(bucSpec, doc, bucPlan, bucOp, intent);
                return true;
            }
            if (DetectGhostReferences.IsIntent(intent))
            {
                var dgrSpec = Specs().Find(s => s.Name == "detect_ghost_references");
                var dgrPlan = new IntentPlan { Confidence = 1.0 };
                var dgrOp = new IntentOperation { Action = "detect_ghost_references" };
                dgrPlan.Operations.Add(dgrOp);
                await ExecuteSpec(dgrSpec, doc, dgrPlan, dgrOp, intent);
                return true;
            }
            if (ValidateSheetMetal.IsIntent(intent))
            {
                var vsmSpec = Specs().Find(s => s.Name == "validate_sheet_metal");
                var vsmPlan = new IntentPlan { Confidence = 1.0 };
                var vsmOp = new IntentOperation { Action = "validate_sheet_metal" };
                vsmPlan.Operations.Add(vsmOp);
                await ExecuteSpec(vsmSpec, doc, vsmPlan, vsmOp, intent);
                return true;
            }
            if (RebuildDocument.IsIntent(intent))
            {
                var rdSpec = Specs().Find(s => s.Name == "rebuild_document");
                var rdPlan = new IntentPlan { Confidence = 1.0 };
                var rdOp = new IntentOperation { Action = "rebuild_document" };
                rdPlan.Operations.Add(rdOp);
                await ExecuteSpec(rdSpec, doc, rdPlan, rdOp, intent);
                return true;
            }
            if (CompareBodies.IsIntent(intent))
            {
                var compSpec = Specs().Find(s => s.Name == "compare_bodies");
                var compPlan = new IntentPlan { Confidence = 1.0 };
                var compOp = new IntentOperation { Action = "compare_bodies" };
                compPlan.Operations.Add(compOp);
                await ExecuteSpec(compSpec, doc, compPlan, compOp, intent);
                return true;
            }
            if (ValidateScaleSanity.IsIntent(intent))
            {
                var vssSpec = Specs().Find(s => s.Name == "validate_scale_sanity");
                var vssPlan = new IntentPlan { Confidence = 1.0 };
                var vssOp = new IntentOperation { Action = "validate_scale_sanity" };
                vssPlan.Operations.Add(vssOp);
                await ExecuteSpec(vssSpec, doc, vssPlan, vssOp, intent);
                return true;
            }
            if (ResolveLocalizedNames.IsIntent(intent))
            {
                var rlnSpec = Specs().Find(s => s.Name == "resolve_localized_names");
                var rlnPlan = new IntentPlan { Confidence = 1.0 };
                var rlnOp = new IntentOperation { Action = "resolve_localized_names" };
                rlnPlan.Operations.Add(rlnOp);
                await ExecuteSpec(rlnSpec, doc, rlnPlan, rlnOp, intent);
                return true;
            }
            if (BatchRenameFeatures.IsIntent(intent))
            {
                var brfSpec = Specs().Find(s => s.Name == "batch_rename_features");
                var brfPlan = new IntentPlan { Confidence = 1.0 };
                var brfOp = new IntentOperation { Action = "batch_rename_features" };
                brfPlan.Operations.Add(brfOp);
                await ExecuteSpec(brfSpec, doc, brfPlan, brfOp, intent);
                return true;
            }
            if (SetComponentLightweight.IsIntent(intent))
            {
                var sclSpec = Specs().Find(s => s.Name == "set_component_lightweight");
                var sclPlan = new IntentPlan { Confidence = 1.0 };
                var sclOp = new IntentOperation { Action = "set_component_lightweight" };
                sclPlan.Operations.Add(sclOp);
                await ExecuteSpec(sclSpec, doc, sclPlan, sclOp, intent);
                return true;
            }
            if (SetRebuildVerification.IsIntent(intent))
            {
                var srvSpec = Specs().Find(s => s.Name == "set_rebuild_verification");
                var srvPlan = new IntentPlan { Confidence = 1.0 };
                var srvOp = new IntentOperation { Action = "set_rebuild_verification" };
                srvPlan.Operations.Add(srvOp);
                await ExecuteSpec(srvSpec, doc, srvPlan, srvOp, intent);
                return true;
            }
            if (EditFeatureParameter.IsIntent(intent))
            {
                var efpSpec = Specs().Find(s => s.Name == "edit_feature_parameter");
                var efpPlan = new IntentPlan { Confidence = 1.0 };
                var efpOp = new IntentOperation { Action = "edit_feature_parameter" };
                efpPlan.Operations.Add(efpOp);
                await ExecuteSpec(efpSpec, doc, efpPlan, efpOp, intent);
                return true;
            }
            if (EditLastFeature.IsIntent(intent))
            {
                var elfSpec = Specs().Find(s => s.Name == "edit_last_feature");
                var elfPlan = new IntentPlan { Confidence = 1.0 };
                var elfOp = new IntentOperation { Action = "edit_last_feature" };
                elfPlan.Operations.Add(elfOp);
                await ExecuteSpec(elfSpec, doc, elfPlan, elfOp, intent);
                return true;
            }

            // InsertStandardViews (tool 102) BEFORE CreateDrawing: more specific (requires the explicit view
            // signal), so "create a drawing with standard views" lands here, not on an empty sheet.
            if (InsertStandardViews.IsIntent(intent))
            {
                var isvSpec = Specs().Find(s => s.Name == "insert_standard_views");
                var isvPlan = new IntentPlan { Confidence = 1.0 };
                var isvOp = new IntentOperation { Action = "insert_standard_views" };
                isvPlan.Operations.Add(isvOp);
                await ExecuteSpec(isvSpec, doc, isvPlan, isvOp, intent);
                return true;
            }

            // InsertView (tool 103) AFTER InsertStandardViews: requires EXACTLY ONE orientation word and no
            // standard/orthographic/projection signal, so the two never collide (see Harness.cs IntentDispatch
            // comment for why). Its verb list excludes "make", so it never collides with SetViewScale below either.
            if (InsertView.IsIntent(intent))
            {
                var ivSpec = Specs().Find(s => s.Name == "insert_view");
                var ivPlan = new IntentPlan { Confidence = 1.0 };
                var ivOp = new IntentOperation { Action = "insert_view" };
                ivPlan.Operations.Add(ivOp);
                await ExecuteSpec(ivSpec, doc, ivPlan, ivOp, intent);
                return true;
            }

            // SetViewScale (tool 107) BEFORE CreateDrawing/InsertStandardViews's fallthrough: requires an explicit
            // "scale" word so it never collides with either (see Harness.cs IntentDispatch comment for why).
            if (SetViewScale.IsIntent(intent))
            {
                var svSpec = Specs().Find(s => s.Name == "set_view_scale");
                var svPlan = new IntentPlan { Confidence = 1.0 };
                var svOp = new IntentOperation { Action = "set_view_scale" };
                svPlan.Operations.Add(svOp);
                await ExecuteSpec(svSpec, doc, svPlan, svOp, intent);
                return true;
            }

            // DeleteView (tool 106) requires an explicit delete/remove verb — never collides with InsertStandardViews
            // or SetViewScale's verb lists.
            if (DeleteView.IsIntent(intent))
            {
                var dvSpec = Specs().Find(s => s.Name == "delete_view");
                var dvPlan = new IntentPlan { Confidence = 1.0 };
                var dvOp = new IntentOperation { Action = "delete_view" };
                dvPlan.Operations.Add(dvOp);
                await ExecuteSpec(dvSpec, doc, dvPlan, dvOp, intent);
                return true;
            }

            // AddNote (tool 112) requires the explicit "note" noun — no other drawing handler's matcher uses it.
            if (AddNote.IsIntent(intent))
            {
                var anSpec = Specs().Find(s => s.Name == "add_note");
                var anPlan = new IntentPlan { Confidence = 1.0 };
                var anOp = new IntentOperation { Action = "add_note" };
                anPlan.Operations.Add(anOp);
                await ExecuteSpec(anSpec, doc, anPlan, anOp, intent);
                return true;
            }

            // ImportModelDimensions (tool 108) requires the explicit "model" word alongside a dimension noun and
            // an import-flavored verb (import/pull/bring) — deliberately excludes show/display/list so it never
            // collides with GetDimensions (a part-scoped READ that owns those verbs).
            if (ImportModelDimensions.IsIntent(intent))
            {
                var imdSpec = Specs().Find(s => s.Name == "import_model_dimensions");
                var imdPlan = new IntentPlan { Confidence = 1.0 };
                var imdOp = new IntentOperation { Action = "import_model_dimensions" };
                imdPlan.Operations.Add(imdOp);
                await ExecuteSpec(imdSpec, doc, imdPlan, imdOp, intent);
                return true;
            }

            if (CreateDrawing.IsIntent(intent))
            {
                var cdSpec = Specs().Find(s => s.Name == "create_drawing");
                var cdPlan = new IntentPlan { Confidence = 1.0 };
                var cdOp = new IntentOperation { Action = "create_drawing" };
                cdPlan.Operations.Add(cdOp);
                await ExecuteSpec(cdSpec, doc, cdPlan, cdOp, intent);
                return true;
            }

            if (InsertNewPartInContext.IsIntent(intent))
            {
                var ipSpec = Specs().Find(s => s.Name == "insert_new_part_in_context");
                var ipPlan = new IntentPlan { Confidence = 1.0 };
                var ipOp = new IntentOperation { Action = "insert_new_part_in_context" };
                ipPlan.Operations.Add(ipOp);
                await ExecuteSpec(ipSpec, doc, ipPlan, ipOp, intent);
                return true;
            }

            // BASIC-SOLID intercepts (WRITE, from scratch) — the same live-dispatch-gap class as create_part below:
            // the cloud parser has no create_sphere/create_cylinder/create_plate action, so a bare "create a sphere"
            // / "make a cylinder" / "create a rectangular block with a hole" would parse 0 ops and never reach the
            // HSpecs. These run BEFORE the cloud parse so the basic solids route even when the cloud is unreachable.
            // Specific-first: each fenced by its own noun, before the generic create_part. The plate cue uses the
            // widened local net (IsOfflinePlateIntent) so plain block/cube/rectangular phrasing still lands.
            if (CreateSphere.IsIntent(intent))
            {
                var spSpec = Specs().Find(s => s.Name == "create_sphere");
                var spPlan = new IntentPlan { Confidence = 1.0 };
                var spOp = new IntentOperation { Action = "create_sphere" };
                spPlan.Operations.Add(spOp);
                await ExecuteSpec(spSpec, doc, spPlan, spOp, intent);
                return true;
            }

            if (CreateCylinder.IsIntent(intent))
            {
                var cySpec = Specs().Find(s => s.Name == "create_cylinder");
                var cyPlan = new IntentPlan { Confidence = 1.0 };
                var cyOp = new IntentOperation { Action = "create_cylinder" };
                cyPlan.Operations.Add(cyOp);
                await ExecuteSpec(cySpec, doc, cyPlan, cyOp, intent);
                return true;
            }

            if (IsOfflinePlateIntent(intent))
            {
                var plSpec = Specs().Find(s => s.Name == "create_plate");
                var plPlan = new IntentPlan { Confidence = 1.0 };
                var plOp = new IntentOperation { Action = "create_plate" };
                plPlan.Operations.Add(plOp);
                await ExecuteSpec(plSpec, doc, plPlan, plOp, intent);
                return true;
            }

            if (CreatePart.IsIntent(intent))
            {
                var cpSpec = Specs().Find(s => s.Name == "create_part");
                var cpPlan = new IntentPlan { Confidence = 1.0 };
                var cpOp = new IntentOperation { Action = "create_part" };
                cpPlan.Operations.Add(cpOp);
                await ExecuteSpec(cpSpec, doc, cpPlan, cpOp, intent);
                return true;
            }

            if (CreateAssembly.IsIntent(intent))
            {
                var caSpec = Specs().Find(s => s.Name == "create_assembly");
                var caPlan = new IntentPlan { Confidence = 1.0 };
                var caOp = new IntentOperation { Action = "create_assembly" };
                caPlan.Operations.Add(caOp);
                await ExecuteSpec(caSpec, doc, caPlan, caOp, intent);
                return true;
            }

            if (GetBodies.IsIntent(intent))
            {
                OpCounter.Increment();
                ForgeData.RunBegin("list_bodies", ForgeData.Mask(intent, IntentLayer.ComponentNames(doc)), 0);
                Func<string, string, string, string, Task> emitB = async (agent, gloss, state, result) =>
                { Send(new { type = "step", agent, gloss, state, result }); await Task.Delay(60); };
                var br = await GetBodies.Run(App, doc, intent, emitB);
                if (br.Error != null)
                { LogRun("list_bodies", intent, doc, "error", false, 0, errorCode: "list_bodies"); Send(new { type = "error", message = br.Error }); return true; }
                LogRun("list_bodies", intent, doc, "executed", true, br.SolidBodies + br.SurfaceBodies);
                Send(new { type = "answer", answer = br.Info, runId = _currentRunId, handler = "list_bodies" });
                return true;
            }

            // test-loop wrong-answer fix (count-clamping-positions): "what's the max parts this fixture can take"
            // has no capacity action in the cloud's vocabulary, so it fell through to a generic scan reporting the
            // current component count instead of the fixture's actual (geometry-derived) capacity. Same direct,
            // no-HSpec pattern as list_bodies/get_edge_length/get_faces above.
            if (GetFixtureCapacity.IsIntent(intent))
            {
                OpCounter.Increment();
                ForgeData.RunBegin("get_fixture_capacity", ForgeData.Mask(intent, IntentLayer.ComponentNames(doc)), 0);
                Func<string, string, string, string, Task> emitFc = async (agent, gloss, state, result) =>
                { Send(new { type = "step", agent, gloss, state, result }); await Task.Delay(60); };
                var fcr = await GetFixtureCapacity.Run(App, doc, intent, emitFc);
                if (fcr.Error != null)
                { LogRun("get_fixture_capacity", intent, doc, "error", false, 0, errorCode: "get_fixture_capacity"); Send(new { type = "error", message = fcr.Error }); return true; }
                LogRun("get_fixture_capacity", intent, doc, "executed", true, fcr.MaxQuantity);
                Send(new { type = "answer", answer = fcr.Info, runId = _currentRunId, handler = "get_fixture_capacity" });
                return true;
            }

            // Same live-dispatch gap: GetMaterial.cs (get_material, the READ counterpart to set_material) has no
            // registered spec either. Its own IsIntent already excludes set/change/apply/density phrasing so it
            // can't shadow Materializer.IsMaterialIntent (the set_material legacy fallback) or get_material_density.
            if (GetMaterial.IsIntent(intent))
            {
                OpCounter.Increment();
                ForgeData.RunBegin("get_material", ForgeData.Mask(intent, IntentLayer.ComponentNames(doc)), 0);
                Func<string, string, string, string, Task> emitM2 = async (agent, gloss, state, result) =>
                { Send(new { type = "step", agent, gloss, state, result }); await Task.Delay(60); };
                var gmr = await GetMaterial.Run(App, doc, intent, emitM2);
                if (gmr.Error != null)
                { LogRun("get_material", intent, doc, "error", false, 0, errorCode: "get_material"); Send(new { type = "error", message = gmr.Error }); return true; }
                LogRun("get_material", intent, doc, "executed", true, gmr.HasMaterial ? 1 : 0);
                Send(new { type = "answer", answer = gmr.Info, runId = _currentRunId, handler = "get_material" });
                return true;
            }

            // Same live-dispatch gap: ListMates.cs (list_mates) has no registered spec either. Assembly-only, but
            // Run() self-guards (returns Error on a part) so no extra doc-type check is needed here. Its own
            // IsListMatesIntent already excludes fix/repair/add/delete phrasing so it can't shadow fix_red_wave.
            if (ListMates.IsListMatesIntent(intent))
            {
                OpCounter.Increment();
                ForgeData.RunBegin("list_mates", ForgeData.Mask(intent, IntentLayer.ComponentNames(doc)), 0);
                Func<string, string, string, string, Task> emitLM = async (agent, gloss, state, result) =>
                { Send(new { type = "step", agent, gloss, state, result }); await Task.Delay(60); };
                var lmr = await ListMates.Run(App, doc, intent, emitLM);
                if (lmr.Error != null)
                { LogRun("list_mates", intent, doc, "error", false, 0, errorCode: "list_mates"); Send(new { type = "error", message = lmr.Error }); return true; }
                LogRun("list_mates", intent, doc, "executed", true, lmr.Total);
                Send(new { type = "answer", answer = lmr.Info, runId = _currentRunId, handler = "list_mates" });
                return true;
            }

            // Same live-dispatch gap: GetComponentInfo.cs (get_component_info) has no registered spec either.
            // Assembly-only, but Run() self-guards. NOTE: its IsIntent also matches "list the components" phrasing
            // that conceptually belongs to the (still-unwired) list_components action — checked here FIRST only
            // because list_components isn't live yet; when list_components is wired, put it BEFORE this check.
            if (GetComponentInfo.IsIntent(intent))
            {
                OpCounter.Increment();
                ForgeData.RunBegin("get_component_info", ForgeData.Mask(intent, IntentLayer.ComponentNames(doc)), 0);
                Func<string, string, string, string, Task> emitCI = async (agent, gloss, state, result) =>
                { Send(new { type = "step", agent, gloss, state, result }); await Task.Delay(60); };
                var cir = await GetComponentInfo.Run(App, doc, intent, emitCI);
                if (cir.Error != null)
                { LogRun("get_component_info", intent, doc, "error", false, 0, errorCode: "get_component_info"); Send(new { type = "error", message = cir.Error }); return true; }
                LogRun("get_component_info", intent, doc, "executed", true, cir.Total);
                Send(new { type = "answer", answer = cir.Info, runId = _currentRunId, handler = "get_component_info" });
                return true;
            }

            // Same live-dispatch gap, this time a WRITE: RenameComponent.cs (rename_component) has no registered
            // spec either — every rename-component phrasing fell through to the legacy ForgeApi.Act() cloud path,
            // which asks a clarifying question instead of acting (test-loop hedged finding "rename-dynamic-component":
            // "rename the component X to Y" got zero attempt, just ambiguities). The handler already resolves its
            // own target from the live tree and asks ONE question via res.Error on 0/many matches (Rule #2) — same
            // shape as set_material's already-live Destructive=false write, so no preview gate needed here either.
            // IsIntent requires "rename"+"to" and excludes feature/dim/config nouns, so it can't shadow those.
            if (RenameComponent.IsIntent(intent))
            {
                OpCounter.Increment();
                ForgeData.RunBegin("rename_component", ForgeData.Mask(intent, IntentLayer.ComponentNames(doc)), 0);
                Func<string, string, string, string, Task> emitRC = async (agent, gloss, state, result) =>
                { Send(new { type = "step", agent, gloss, state, result }); await Task.Delay(60); };
                var rcr = await RenameComponent.Run(App, doc, intent, emitRC);
                if (rcr.Error != null)
                { LogRun("rename_component", intent, doc, "error", false, 0, errorCode: "rename_component"); Send(new { type = "error", message = rcr.Error }); return true; }
                LogRun("rename_component", intent, doc, "executed", rcr.Renamed, rcr.TotalComponents);
                Send(new { type = "answer", answer = rcr.Info, runId = _currentRunId, handler = "rename_component" });
                return true;
            }

            // test-loop hedge fix (change-green-button-color, the regression corpus): a PART document has no
            // sub-components for the cloud's ambiguity check to search against, so a descriptor of the part ITSELF
            // ("the green button's color") — not a sub-target — makes the cloud bail with 0 ops + "this is a part
            // file, not an assembly" instead of just coloring the open part. ApplyAppearance.RunPart already does
            // exactly that (colors the whole part, ignoring any named sub-target — a single part has nothing else
            // it could mean). Authoritative override, PART-only: assemblies keep going through the normal
            // cloud/HSpec path unchanged. Kept in sync with Harness.cs IntentDispatch.
            if ((doc as AssemblyDoc) == null && !GetAppearance.IsIntent(intent) && ApplyAppearance.IsAppearanceIntent(intent))
            {
                OpCounter.Increment();
                ForgeData.RunBegin("apply_appearance", ForgeData.Mask(intent, IntentLayer.ComponentNames(doc)), 0);
                Func<string, string, string, string, Task> emitAA = async (agent, gloss, state, result) =>
                { Send(new { type = "step", agent, gloss, state, result }); await Task.Delay(60); };
                var aar = await ApplyAppearance.Run(App, doc, intent, emitAA);
                if (aar.Error != null)
                { LogRun("apply_appearance", intent, doc, "error", false, 0, errorCode: "apply_appearance"); Send(new { type = "error", message = aar.Error }); return true; }
                LogRun("apply_appearance", intent, doc, "executed", aar.Colored > 0 || aar.AlreadyColored > 0, aar.Matched);
                Send(new { type = "answer", answer = aar.Info, runId = _currentRunId, handler = "apply_appearance" });
                return true;
            }

            Send(new { type = "status", message = "Understanding…" });
            var plan = await IntentLayer.Parse(intent, doc);
            if (plan == null || plan.Error != null || plan.Operations == null || plan.Operations.Count == 0)
                return await TryLocalFallback(intent, doc);   // cloud down/unparseable -> local net for the demo handlers, else false

            var op = plan.Operations[0];
            string action = (op.Action ?? "").Trim().ToLowerInvariant();
            var spec = Specs().Find(s => Array.IndexOf(s.Actions, action) >= 0);
            // Observability (panel-testing.md): the intent internals — acted-vs-asked is derivable from what we Send next.
            try { PanelCapture.Log("parse", new { intent, action, confidence = plan.Confidence,
                ambiguities = plan.Ambiguities, routed = spec?.Name }); } catch { }
            if (spec == null) return await TryLocalFallback(intent, doc);  // parser returned a non-pipeline/wrong action -> local net first, else legacy

            bool isAsm = (int)doc.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY;
            if (spec.AssemblyOnly && !isAsm)
            { Send(new { type = "error", message = "Open the assembly (.SLDASM) for that." }); return true; }

            // HONEST REFUSAL over silent substitution (test-loop false-success cluster, the regression corpus 2026-07-27: add-belt-clip, add-hex-flat, design-handle, hull-generate-keel, hull-add-window-cutout —
            // all routed by the cloud, LOW confidence (0.35-0.65), to a GENERIC primitive (add_boss/add_pocket/
            // add_hole: one plain round boss or rectangular pocket at a face centre). The handler then genuinely
            // BUILDS and Sentinel-verifies that generic primitive (real volume change, real new face — not a lie),
            // but the result is not what was asked for ("a belt clip" is not a bare 12mm cylinder) — reporting that
            // as success is the dishonest part, not the geometry. When the cloud is unsure (low confidence) AND the
            // user named a SPECIFIC real-world feature these primitives structurally cannot produce, decline before
            // ever cutting metal instead of quietly swapping in a look-alike shape.
            if (HonestLimits.IsGenericPrimitiveMismatch(spec.Name, intent, plan.Confidence))
            {
                Send(new { type = "error", message = HonestLimits.GenericPrimitiveMismatchMessage });
                return true;
            }

            // ACT, DON'T HEDGE (Rule #2). The parser flags soft "ambiguities" too eagerly — "this is usually used on
            // an assembly", "a part has no mates", etc. Those are NOT reasons to stop: a read-only / reversible handler
            // grounds itself in the live model and asks its OWN one question only when the TARGET genuinely can't be
            // resolved. So we honour the parser's ambiguity gate ONLY before a destructive write (never guess before
            // changing the model); for everything else we run the handler and let it decide whether to ask.
            // DESTRUCTIVE WRITE: the PREVIEW is the safety gate (Rule #3) AND the wow ("mirroring 112 of 150,
            // excluding 38"). Show it and wait for "go" — do NOT hedge on the parser's soft, already-resolved
            // ambiguities (Rule #2): "you said motor but there's no motor, I'll do hardware only" is a note, not a
            // question. Only when there is NO preview to show do we honor a genuine ambiguity by asking.
            var chainRest = plan.Operations.Count > 1 ? plan.Operations.Skip(1).ToList() : null;
            if (spec.Destructive && !IsAffirmative(intent))
            {
                string line = spec.Preview != null ? spec.Preview(doc, plan, op, intent) : null;
                if (line != null)
                {
                    _pendingConfirm = new Pending { Spec = spec, Plan = plan, Op = op, Intent = intent, Remaining = chainRest };
                    Send(new { type = "clarify", message = line + " — reply \"go\" to proceed, or say what to change." });
                    return true;
                }
                if (plan.Ambiguities != null && plan.Ambiguities.Count > 0)
                {
                    LogRun(spec.Name, intent, doc, "asked", false, 0, plan);
                    _pendingIntent = intent;   // carry the thread so a short reply resolves
                    Send(new { type = "clarify", message = plan.Ambiguities[0] });
                    return true;
                }
            }

            var firstOutcome = await ExecuteSpec(spec, doc, plan, op, intent);
            // CHAIN CONTINUATION (test-loop false-success flange-12: "mate the flanges, bump up the bolt size, check for
            // interference" parsed 3 ops but only op[0] ever ran — the other two were silently dropped, never even
            // mentioned. A multi-op plan means the user named every step in ONE message, so once the first step lands
            // cleanly we run the rest too. Unlike op[0], continuation legs skip the destructive preview gate (Rule #3
            // exists to surface scope the user didn't specify — "mirroring 112 of 150" — but here the user already
            // named the exact action; re-asking "go" for something they just said restates their own words back at
            // them, the hedge Rule #3 isn't meant to protect against). Each leg still runs the full verify/log/report
            // stage, and one leg failing doesn't abort the rest (Rule #4: partial success beats total failure).
            if (chainRest != null && firstOutcome != null && firstOutcome.Error == null && !firstOutcome.AskedConfirm)
                await RunChainRest(doc, plan, chainRest, intent);
            return true;
        }

        // ---- shared execute + verify + log stage (used by the pipeline AND by a confirmed preview). Returns the
        //      outcome so a multi-op chain can decide whether to run its next leg. ----
        private async Task<HOutcome> ExecuteSpec(HSpec spec, IModelDoc2 doc, IntentPlan plan, IntentOperation op, string intent)
        {
            OpCounter.Increment();
            ForgeData.RunBegin(spec.Name, ForgeData.Mask(intent, IntentLayer.ComponentNames(doc)), 0);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Func<string, string, string, string, Task> emit = async (a, g, s, r) =>
            { Send(new { type = "step", agent = a, gloss = g, state = s, result = r }); await Task.Delay(60); };

            HOutcome o;
            try { o = await spec.Execute(doc, plan, op, intent, emit); }
            catch (Exception ex)
            {
                LogRun(spec.Name, intent, doc, "error", false, 0, plan, ex.GetType().Name, sw.ElapsedMilliseconds);
                Telemetry.Log("edit_run", success: false, swVersion: SwVer(), opsUsed: OpCounter.Count, errorCode: ex.GetType().Name, questionType: "edit", questionSummary: spec.Name);
                Send(new { type = "error", message = ex.Message });
                return new HOutcome { Error = ex.Message };
            }

            // confirm-or-ask surfaced by the handler itself (e.g. material: unknown target) — Rule #2.
            if (o.AskedConfirm)
            {
                LogRun(spec.Name, intent, doc, "asked", false, 0, plan, durationMs: sw.ElapsedMilliseconds);
                Telemetry.Log("edit_run", success: false, swVersion: SwVer(), opsUsed: OpCounter.Count, questionType: "edit", questionSummary: spec.Name + " (ask)");
                Send(new { type = "answer", answer = o.Question });
                return o;
            }
            if (o.Error != null)
            {
                LogRun(spec.Name, intent, doc, "error", false, 0, plan, spec.Name, sw.ElapsedMilliseconds);
                Telemetry.Log("edit_run", success: false, swVersion: SwVer(), opsUsed: OpCounter.Count, errorCode: spec.Name, questionType: "edit", questionSummary: spec.Name);
                Send(new { type = "error", message = o.Error });
                return o;
            }

            // FAIL CLOSED: we log/report what was VERIFIED, not merely attempted.
            LogRun(spec.Name, intent, doc, "executed", o.Verified, o.Items, plan, durationMs: sw.ElapsedMilliseconds);
            Telemetry.Log(IsReadHandler(spec.Name) ? "read_run" : "edit_run", success: o.Verified, featuresCount: o.Items,
                durationMs: sw.ElapsedMilliseconds, swVersion: SwVer(), opsUsed: OpCounter.Count, questionType: "edit", questionSummary: spec.Name);

            if (o.Card != null) { o.Card["runId"] = _currentRunId; o.Card["handler"] = spec.Name; Send(o.Card); }
            else Send(new { type = "answer", answer = o.Info, runId = _currentRunId, handler = spec.Name });
            return o;
        }

        // ---- CHAIN CONTINUATION: run every remaining op from a multi-op plan in order, skipping the destructive
        //      preview gate (the user already named each step in the one message that started the chain — see the
        //      comment in RunViaPipeline). An op whose action has no handler, or needs an assembly it doesn't have,
        //      is reported honestly and skipped — never silently dropped (Rule #4: partial success beats total
        //      failure, but every part of the ask must at least be ACKNOWLEDGED). One leg's failure doesn't stop
        //      the rest. ----
        private async Task RunChainRest(IModelDoc2 doc, IntentPlan plan, List<IntentOperation> ops, string intent)
        {
            // test-loop false-success fix (suppress-rotate-unsuppress, the regression corpus):
            // "suppress the axis, rotate the assembly 45, then unsuppress the axis" ran all 3 legs, but leg 2
            // (rotate) only ASKED a clarifying question ("which direction?") — ExecuteSpec sent that question as
            // its own "answer" message, but nothing here looked at the outcome, so the loop just moved on to leg 3
            // (unsuppress), which fully succeeded and became the LAST message in the transcript. A judge reading
            // the tail of the conversation sees a clean finish and calls it false-success, because nothing ever
            // said the middle step didn't happen. Track every leg's outcome and, if any leg didn't fully verify,
            // send ONE final honest summary AFTER the loop — same shape as Rule #4's "9 done, 2 skipped, 1 needs
            // your eyes". A chain where every leg verifies stays exactly as quiet as before (no extra noise).
            var incomplete = new List<string>();
            foreach (var nextOp in ops)
            {
                var liveDoc = App?.ActiveDoc as IModelDoc2;
                if (liveDoc == null) { incomplete.Add("chain stopped — model closed"); break; }
                string action = (nextOp.Action ?? "").Trim().ToLowerInvariant();
                if (action.Length == 0) continue;

                // test-loop wrong-answer fix (chain-thickness-arc-check): the cloud decides EVERY chain step's action
                // in one upfront call, with no per-step text handed back — only a full plan of Actions (IntentOperation
                // has no raw-text field). "...then measure thickness at the center" has no location-scoped-thickness
                // action in the cloud's CHAIN vocabulary (only the top-level single-shot path gets the
                // WallThickness.IsGenericThicknessQuestion override above), so it substituted a generic fallback
                // (get_mass_properties) — an irrelevant answer to "how thick", not what was asked. The chain's ONE
                // shared `intent` string (the whole original sentence) IS available to every leg, so re-run the same
                // authoritative check here: a generic-fallback leg whose FULL intent text is a genuine thickness
                // question gets corrected to wall_thickness before dispatch, same fix as the single-shot path.
                if ((action == "get_mass_properties" || action == "get_bounding_box") && WallThickness.IsGenericThicknessQuestion(intent))
                    action = nextOp.Action = "wall_thickness";

                var nextSpec = Specs().Find(s => Array.IndexOf(s.Actions, action) >= 0);
                if (nextSpec == null)
                {
                    Send(new { type = "answer", answer = "(also asked: \"" + action + "\" — Forge doesn't have a handler for that yet, skipped)" });
                    incomplete.Add("\"" + action + "\": no handler, skipped");
                    continue;
                }
                bool isAsm = (int)liveDoc.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY;
                if (nextSpec.AssemblyOnly && !isAsm)
                {
                    Send(new { type = "answer", answer = "(also asked: \"" + action + "\" — needs the assembly open, skipped)" });
                    incomplete.Add("\"" + action + "\": needs an assembly, skipped");
                    continue;
                }
                var outcome = await ExecuteSpec(nextSpec, liveDoc, plan, nextOp, intent);
                if (outcome == null) incomplete.Add(nextSpec.Name + ": no result");
                else if (outcome.AskedConfirm) incomplete.Add(nextSpec.Name + " still needs your input (" + outcome.Question + ")");
                else if (outcome.Error != null) incomplete.Add(nextSpec.Name + " failed (" + outcome.Error + ")");
                else if (!outcome.Verified) incomplete.Add(nextSpec.Name + " ran but couldn't verify the change");
            }
            if (incomplete.Count > 0)
                Send(new { type = "answer", answer = "Chain not fully done — " + string.Join("; ", incomplete) + "." });
        }

        // ---- a pending preview is awaiting confirmation; returns true if this message resolved it. ----
        private async Task<bool> ResolvePendingConfirm(string intent)
        {
            if (_pendingConfirm == null) return false;
            var pc = _pendingConfirm; _pendingConfirm = null;
            if (!IsAffirmative(intent)) return false;   // not a "go" -> treat as a fresh command
            var doc = App?.ActiveDoc as IModelDoc2;
            if (doc == null) { Send(new { type = "error", message = "Open the model again to run that." }); return true; }
            var outcome = await ExecuteSpec(pc.Spec, doc, pc.Plan, pc.Op, pc.Intent);
            // this "go" only confirmed op[0] of a multi-op chain — the rest is still queued (see RunViaPipeline).
            if (pc.Remaining != null && pc.Remaining.Count > 0 && outcome != null && outcome.Error == null && !outcome.AskedConfirm)
                await RunChainRest(doc, pc.Plan, pc.Remaining, pc.Intent);
            return true;
        }

        // ---- LOCAL FALLBACK (panel<->harness fidelity): when the cloud parser is unreachable OR returns an action the
        //      pipeline doesn't own (a rare haiku misclassification), a natural DEMO command must still ACT — never drop
        //      to the hedge-prone legacy path. This was the "passed the harness, failed the panel" gap: the headless
        //      harness routes these read-only / reversible demos through an OFFLINE matcher, but the live panel had no
        //      offline route for them and leaned entirely on the cloud. So here we recognise the SAME demo intents the
        //      harness does, synthesise a minimal plan, and run them through the SAME tested ExecuteSpec stage
        //      (preview -> execute -> verify -> log). Narrow + specific-first (the shadowing lesson): only the demo
        //      handlers with no other offline route live here (ForgePanel.Generate already covers mate/mirror/explode/
        //      material/scan/isolate/resize/batch/simplify); everything else returns false so the existing offline regex
        //      blocks / legacy path still handle it. Returns true iff this handled the command. ----
        private async Task<bool> TryLocalFallback(string intent, IModelDoc2 doc)
        {
            string action = LocalActionFor(intent);
            if (action == null) return false;
            var spec = Specs().Find(s => Array.IndexOf(s.Actions, action) >= 0);
            if (spec == null) return false;

            bool isAsm = (int)doc.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY;
            if (spec.AssemblyOnly && !isAsm)
            { Send(new { type = "error", message = "Open the assembly (.SLDASM) for that." }); return true; }

            var plan = new IntentPlan { Confidence = 1.0 };
            var op = new IntentOperation { Action = action };
            plan.Operations.Add(op);
            try { PanelCapture.Log("parse", new { intent, action, confidence = 1.0, routed = spec.Name, source = "local-fallback" }); } catch { }

            // PREVIEW before a destructive write (Rule #3) — identical to the pipeline path so the demo behaves the same.
            if (spec.Destructive && !IsAffirmative(intent))
            {
                string line = spec.Preview != null ? spec.Preview(doc, plan, op, intent) : null;
                if (line != null)
                {
                    _pendingConfirm = new Pending { Spec = spec, Plan = plan, Op = op, Intent = intent };
                    Send(new { type = "clarify", message = line + " — reply \"go\" to proceed, or say what to change." });
                    return true;
                }
            }
            await ExecuteSpec(spec, doc, plan, op, intent);
            return true;
        }

        // The demo read-only / reversible intents recognised locally (mirrors the harness OfflineDispatch matchers).
        // Specific-first; ONLY handlers that have no other offline route in the panel. Returns the pipeline action, or
        // null for no local match (so non-demo commands still fall through to the existing offline blocks / legacy path).
        private static string LocalActionFor(string intent)
        {
            string i = (intent ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(i)) return null;
            if (Compare.IsCompareIntent(i)) return "compare_versions";                                   // demo #2
            if (Regex.IsMatch(i, @"\b(what breaks|what depends|if i change|what happens if|impact of|depends? on)\b")) return "change_impact";  // demo #3
            if (RedWave.IsFixIntent(i)) return "fix_red_wave";                                            // demo #8
            if (Interfere.IsInterfereIntent(i)) return "interference";                                    // demo #5
            if (Profiler.IsProfileIntent(i)) return "rebuild_profile";                                    // demo #7
            if (BatchExportDrawings.IsIntent(i)) return "batch_export_drawings";                          // before FlatDxf — its dxf/dwg matcher never checks for a drawing noun
            if (FlatDxf.IsFlatDxfIntent(i)) return "flat_dxf";                                            // demo #10 (more specific than drawing_package)
            if (DrawingPkg.IsDrawingPkgIntent(i)) return "drawing_package";                               // demo #9
            if (Regex.Matches(i, @"\bm(\d+)\b").Count >= 2 && Regex.IsMatch(i, @"\b(upsize|replace|swap|change|convert|bump|to)\b")) return "upsize";  // demo #6
            // test-loop hedged finding (flange-02-count-holes): "number of bolt holes"/"hole pattern" questions had
            // an HSpec (measure_bolt_circle) but no local matcher route here, so a cloud miss fell through to the
            // legacy clarify path instead of the already-built handler. Kept in sync with Harness.cs IntentDispatch.
            if (MeasureBoltCircle.IsBoltCircleIntent(i)) return "measure_bolt_circle";
            if (CountNamedComponents.IsIntent(i)) return "count_named_components";
            if (CountGearTeeth.IsIntent(i)) return "count_gear_teeth";
            if (InsertStandardViews.IsIntent(i)) return "insert_standard_views";   // before CreateDrawing — more specific
            if (InsertView.IsIntent(i)) return "insert_view";
            if (SetViewScale.IsIntent(i)) return "set_view_scale";
            if (DeleteView.IsIntent(i)) return "delete_view";
            if (AddNote.IsIntent(i)) return "add_note";
            if (InsertSectionView.IsIntent(i)) return "insert_section_view";
            if (InsertDetailView.IsIntent(i)) return "insert_detail_view";
            if (AddDrawingDimension.IsIntent(i)) return "add_drawing_dimension";
            if (ImportModelDimensions.IsIntent(i)) return "import_model_dimensions";
            if (CreateDrawing.IsIntent(i)) return "create_drawing";
            if (InsertNewPartInContext.IsIntent(i)) return "insert_new_part_in_context";
            // BASIC-SOLID family (WRITE, from scratch — create_part alone makes a BLANK part, no solid). Specific-first
            // (plate/block/cube/sphere/cylinder BEFORE the generic create_part): each is fenced by its own noun, and
            // plate/block/cube additionally needs the WIDER offline cue (Ravi 2026-09-02) so a plain "create a
            // rectangular block" / "... block with a hole of 10mm" — no AxBxC mm size, no literal "plate" word —
            // still lands on the from-scratch plate solid offline. The ported CreatePlate.IsIntent stays untouched.
            if (CreateSphere.IsIntent(i)) return "create_sphere";
            if (CreateCylinder.IsIntent(i)) return "create_cylinder";
            if (IsOfflinePlateIntent(i)) return "create_plate";
            // Reference-plane cue ("create/make a plane") — NOT a plate/block solid. Action value matches the
            // create_reference_plane HSpec's Actions[] exactly (CreateRefPlane.IsCreateRefPlaneIntent already
            // excludes remove/delete phrasing).
            if (CreateRefPlane.IsCreateRefPlaneIntent(i)) return "create_reference_plane";
            if (CreatePart.IsIntent(i)) return "create_part";
            if (CreateAssembly.IsIntent(i)) return "create_assembly";
            if (CloseDocument.IsIntent(i)) return "close_document";               // before OpenDocument — see RunViaPipeline note
            if (SaveBodiesAsParts.IsIntent(i)) return "save_bodies_as_parts";      // before save_document_as / split_body — see RunViaPipeline note
            if (SaveDocumentAs.IsIntent(i)) return "save_document_as";
            if (SaveDocument.IsIntent(i)) return "save_document";
            if (OpenDocument.IsIntent(i)) return "open_document";
            if (BatchConvertFiles.IsIntent(i)) return "batch_convert_files";
            if (ImportFile.IsIntent(i)) return "import_file";
            if (EditMateValue.IsIntent(i)) return "edit_mate_value";       // before delete/suppress/add_*_mate below
            if (DeleteMate.IsIntent(i)) return "delete_mate";
            if (SuppressMate.IsIntent(i)) return "suppress_mate";
            if (AddConcentricMate.IsIntent(i)) return "add_concentric_mate";
            if (AddCoincidentMate.IsIntent(i)) return "add_coincident_mate";
            if (AddParallelMate.IsIntent(i)) return "add_parallel_mate";
            if (AddDistanceMate.IsIntent(i)) return "add_distance_mate";
            if (AddAngleMate.IsIntent(i)) return "add_angle_mate";
            if (AddWidthMate.IsIntent(i)) return "add_width_mate";
            if (PatternSketchEntities.IsIntent(i)) return "pattern_sketch_entities";   // before edit_pattern_count (more specific: requires "sketch")
            if (SkipPatternInstance.IsIntent(i)) return "skip_pattern_instance";   // before edit_pattern_count/spacing
            if (LinearPatternComponent.IsIntent(i)) return "linear_pattern_components";   // before edit_pattern_spacing (see RunViaPipeline note)
            if (CircularPatternComponent.IsIntent(i)) return "circular_pattern_components";
            if (PatternDrivenPatternComponent.IsIntent(i)) return "pattern_driven_pattern";
            if (SketchDrivenPatternComponent.IsIntent(i)) return "sketch_driven_pattern";
            if (EditPatternSpacing.IsIntent(i)) return "edit_pattern_spacing";
            if (EditPatternCount.IsIntent(i)) return "edit_pattern_count";
            // test-loop hedged finding (delete-tamper): "delete the tamper component" parsed 0 ops from the cloud
            // (no dedicated delete_component action in its vocabulary), so this WRITE never had a route — same
            // live-dispatch-gap shape as measure_bolt_circle/count_named_components above.
            if (DeleteComponent.IsIntent(i)) return "delete_component";
            // test-loop wrong-route finding (hull-volume): "check the volume on this hull design" has no offline
            // route here at all (only the harness's IntentDispatch wires GetMassProps), so the live panel leaned
            // entirely on the cloud, which misrouted it. Kept in sync with Harness.cs IntentDispatch ordering
            // (after GetBodies-style body-count matchers, which this file doesn't route locally anyway).
            if (GetMassProps.IsMassPropsIntent(i)) return "get_mass_properties";
            // Configuration + custom-property family — live-dispatch gap fix (2026-07-29). set_config_specific_
            // dimension MUST precede set_dimension below: both match verb+number, but only the config one requires
            // the "config"/"configuration" word, so it has to win first (see the RunViaPipeline block's note).
            if (ConfigSpecificDimension.IsIntent(i)) return "set_config_specific_dimension";
            if (ConfigFeatureSuppression.IsIntent(i)) return "set_config_feature_suppression";
            if (ChangeComponentConfig.IsIntent(i)) return "change_component_config";
            if (SetActiveConfiguration.IsIntent(i)) return "set_active_configuration";
            if (CreateConfiguration.IsIntent(i)) return "create_configuration";
            if (DeleteConfiguration.IsIntent(i)) return "delete_configuration";
            if (RenameConfiguration.IsIntent(i)) return "rename_configuration";
            if (CopyConfiguration.IsIntent(i)) return "copy_configuration";
            if (CopyPropertiesBetweenFiles.IsIntent(i)) return "copy_properties_between_files";
            if (CopySketchToPart.IsIntent(i)) return "copy_sketch_to_part";
            if (InsertLibraryFeature.IsIntent(i)) return "insert_library_feature";
            if (SetCustomProperty.IsIntent(i)) return "set_custom_property";
            if (DeleteCustomProperty.IsIntent(i)) return "delete_custom_property";
            if (GetCustomProperty.IsIntent(i)) return "get_custom_property";
            if (GetComponentConfig.IsIntent(i)) return "get_component_config";
            if (RenameDimension.IsIntent(i)) return "rename_dimension";
            if (NormalizeUnits.IsIntent(i)) return "normalize_units";
            if (SetAngularUnits.IsIntent(i)) return "set_angular_units";
            if (SetDecimalPlaces.IsIntent(i)) return "set_decimal_places";
            if (SetDraftingStandard.IsIntent(i)) return "set_document_properties";
            if (SetDocumentUnits.IsIntent(i)) return "set_document_units";
            if (GetDocumentUnits.IsIntent(i)) return "get_document_units";
            if (GetDimensions.IsIntent(i)) return "list_dimensions";
            if (ListComponents.IsIntent(i)) return "list_components";
            if (ListFeatureDependencies.IsIntent(i)) return "list_feature_dependencies";
            if (ListSubassemblies.IsIntent(i)) return "list_subassemblies";
            if (GetRefGeometry.IsIntent(i)) return "list_reference_geometry";
            if (InsertComponent.IsIntent(i)) return "insert_component";
            if (BatchReplaceComponents.IsIntent(i)) return "batch_replace_components";
            if (ReplaceComponent.IsIntent(i)) return "replace_component";
            if (CombineBodies.IsIntent(i)) return "combine_bodies";
            if (SplitBody.IsIntent(i)) return "split_body";
            if (DeleteReplaceFace.IsIntent(i)) return "delete_replace_face";
            if (RunDfmChecks.IsIntent(i)) return "run_dfm_checks";
            if (ExportBom.IsIntent(i)) return "export_bom";
            if (RunImportDiagnostics.IsIntent(i)) return "run_import_diagnostics";
            if (CheckGeometryErrors.IsIntent(i)) return "check_geometry_errors";
            if (AddCenterMarks.IsIntent(i)) return "add_center_marks";
            if (ReplaceSheetFormat.IsIntent(i)) return "replace_sheet_format";
            if (UpdateRevisionTable.IsIntent(i)) return "update_revision_table";
            if (CheckDraftingStandards.IsIntent(i)) return "check_drafting_standards";
            if (FindFloating.IsIntent(i)) return "find_floating_components";
            if (FindOverDefined.IsIntent(i)) return "find_over_defined_components";
            if (ResolveDuplicatePaths.IsIntent(i)) return "resolve_duplicate_paths";
            if (FindDuplicateComponents.IsIntent(i)) return "find_duplicate_components";
            if (CheckPartSymmetry.IsIntent(i)) return "check_part_symmetry";
            if (DissolveSubassembly.IsIntent(i)) return "dissolve_subassembly";
            if (SetSubassemblyFlexibility.IsIntent(i)) return "set_subassembly_flexibility";
            if (RepairExplodedView.IsIntent(i)) return "repair_exploded_view";
            if (ManageDesignTable.IsIntent(i)) return "manage_design_table";
            if (FillSurface.IsIntent(i)) return "fill_surface";
            if (DescribeGeometry.IsIntent(i)) return "describe_geometry";
            if (HighlightEntities.IsIntent(i)) return "highlight_entities";
            if (HandleLockedFiles.IsIntent(i)) return "handle_locked_files";
            if (DetectInContextWrites.IsIntent(i)) return "detect_in_context_writes";
            if (HandleUnknownFeatures.IsIntent(i)) return "handle_unknown_features";
            if (HandleAssemblyFeatures.IsIntent(i)) return "handle_assembly_features";
            if (TraceDerivedParts.IsIntent(i)) return "trace_derived_parts";
            if (RecoverAutosave.IsIntent(i)) return "recover_autosave";
            if (HandleConfigExplosion.IsIntent(i)) return "handle_config_explosion";
            if (DetectSimulationArtifacts.IsIntent(i)) return "detect_simulation_artifacts";
            if (QuarantineFile.IsIntent(i)) return "quarantine_file";
            if (KnitSurfacesToSolid.IsIntent(i)) return "knit_surfaces_to_solid";
            if (ArrangeDrawingAnnotations.IsIntent(i)) return "arrange_drawing_annotations";
            if (GetFeatureInfo.IsIntent(i)) return "get_feature_info";
            if (FindFeaturesByType.IsIntent(i)) return "find_features_by_type";
            if (FindFeatureByName.IsIntent(i)) return "find_feature_by_name";
            if (FindWhereUsed.IsIntent(i)) return "find_where_used";
            if (GetComponentTransform.IsIntent(i)) return "get_component_transform";
            if (GetMateInfo.IsIntent(i)) return "get_mate_info";
            if (GetActiveDocument.IsIntent(i)) return "get_active_document";
            if (GetFaces.IsIntent(i)) return "get_face_normal";
            if (GetMaterialDensity.IsIntent(i)) return "get_material_density";
            if (SetSheetMetalThickness.IsIntent(i)) return "set_sheet_metal_thickness";
            if (GetSheetMetalProps.IsIntent(i)) return "get_sheet_metal_properties";
            if (GetPartNumber.IsIntent(i)) return "get_part_number";
            if (GetAppearance.IsIntent(i)) return "get_appearance";
            if (GetRebuildErrors.IsIntent(i)) return "get_rebuild_errors";
            if (HandleRollbackBar.IsIntent(i)) return "handle_rollback_bar";
            if (GetComponentMass.IsIntent(i)) return "get_component_mass";
            if (FullyDefineSketch.IsIntent(i)) return "fully_define_sketch";      // before diagnose_sketch/get_sketch_info
            if (DiagnoseSketch.IsIntent(i)) return "diagnose_sketch";
            if (DetectSharedSketches.IsIntent(i)) return "detect_shared_sketches";
            if (GetSketches.IsIntent(i)) return "get_sketch_info";
            if (GetPatternInfo.IsIntent(i)) return "get_pattern_info";
            if (GetCutList.IsIntent(i)) return "get_cut_list";
            if (PackAndGo.IsIntent(i)) return "pack_and_go";
            if (RenameFileWithReferences.IsIntent(i)) return "rename_file_with_references";
            if (MoveFileWithReferences.IsIntent(i)) return "move_file_with_references";
            if (UpdateSheetReferences.IsIntent(i)) return "update_sheet_references";
            if (InsertBomTable.IsIntent(i)) return "insert_bom_table";
            if (CleanBomTable.IsIntent(i)) return "clean_bom_table";
            if (RepairBalloonReferences.IsIntent(i)) return "repair_balloon_references";
            if (RepairMate.IsIntent(i)) return "repair_mate";
            if (RepairMissingReferences.IsIntent(i)) return "repair_missing_references";
            if (GetFileReferences.IsIntent(i)) return "get_file_references";
            if (ListDanglingDimensions.IsIntent(i)) return "list_dangling_dimensions";
            if (GetDrawingViews.IsIntent(i)) return "get_drawing_views";
            if (GetDrivingDimensions.IsIntent(i)) return "get_driving_dimensions";
            if (AddRevolve.IsIntent(i)) return "add_revolve";
            if (AddSweep.IsIntent(i)) return "add_sweep";
            if (AddLoft.IsIntent(i)) return "add_loft";
            if (AddHelix.IsIntent(i)) return "add_helix";
            if (AddDome.IsIntent(i)) return "add_dome";
            if (CreateThicken.IsIntent(i)) return "create_thicken";
            if (AddConstructionGeometry.IsIntent(i)) return "add_construction_geometry";
            if (AddSketchArc.IsIntent(i)) return "add_sketch_arc";
            if (AddSketchEntity.IsIntent(i)) return "add_sketch_entity";
            if (AddSketchDimension.IsIntent(i)) return "add_sketch_dimension";
            // AddSketchRelation (tool 84) intentionally excluded here — see the RunViaPipeline note above (crashes
            // SolidWorks headless; not safe to expose to a live user command).
            if (Create3DSketch.IsIntent(i)) return "create_3d_sketch";
            if (ImportDxfToSketch.IsIntent(i)) return "import_dxf_to_sketch";
            if (CreateLayoutSketch.IsIntent(i)) return "create_layout_sketch";
            if (CreateSketch.IsIntent(i)) return "create_sketch";
            if (AddSketchEllipse.IsIntent(i)) return "add_sketch_ellipse";
            if (AddSketchPolygon.IsIntent(i)) return "add_sketch_polygon";
            if (AddSketchSlot.IsIntent(i)) return "add_sketch_slot";
            if (AddSketchSpline.IsIntent(i)) return "add_sketch_spline";
            if (AddSketchText.IsIntent(i)) return "add_sketch_text";
            if (MeasureDistance.IsIntent(i)) return "measure_distance";
            if (MeasureAngle.IsIntent(i)) return "measure_angle";
            if (OffsetSketchEntities.IsIntent(i)) return "offset_sketch_entities";
            if (ConvertEntities.IsIntent(i)) return "convert_entities";
            if (TrimExtendSketch.IsIntent(i)) return "trim_extend";
            if (MirrorSketchEntities.IsIntent(i)) return "mirror_sketch_entities";
            if (CreateBoundaryFeature.IsIntent(i)) return "create_boundary_feature";
            if (CreateCoordSys.IsIntent(i)) return "create_coordinate_system";
            if (CreateCurve.IsIntent(i)) return "create_curve";
            if (CreateExtrudedSurface.IsIntent(i)) return "create_extruded_surface";
            if (CaptureViewport.IsIntent(i)) return "capture_viewport";
            if (CaptureSection.IsIntent(i)) return "capture_section";
            if (GetSelectedEntities.IsIntent(i)) return "get_selected_entities";
            if (ClearSelection.IsIntent(i)) return "clear_selection";
            if (SelectFace.IsIntent(i)) return "select_face";
            if (SelectComponent.IsIntent(i)) return "select_component";
            if (SelectEdge.IsIntent(i)) return "select_edge";
            if (SelectPlane.IsIntent(i)) return "select_plane";
            if (CreateRefAxis.IsIntent(i)) return "create_reference_axis";
            if (CreateRib.IsIntent(i)) return "create_rib";
            if (CreateSweptSurface.IsIntent(i)) return "create_swept_lofted_surface";
            if (CreateTreeFolder.IsIntent(i)) return "create_tree_folder";
            if (CreateVariableFillet.IsIntent(i)) return "create_variable_fillet";
            if (SelectByFilter.IsIntent(i)) return "select_components_by_filter";
            if (BatchUpdateMaterials.IsIntent(i)) return "batch_update_materials";
            if (BatchUpdateCustomProperties.IsIntent(i)) return "batch_update_custom_properties";
            if (DetectGhostReferences.IsIntent(i)) return "detect_ghost_references";
            if (ValidateSheetMetal.IsIntent(i)) return "validate_sheet_metal";
            if (RebuildDocument.IsIntent(i)) return "rebuild_document";
            if (CompareBodies.IsIntent(i)) return "compare_bodies";
            if (ValidateScaleSanity.IsIntent(i)) return "validate_scale_sanity";
            if (ResolveLocalizedNames.IsIntent(i)) return "resolve_localized_names";
            if (BatchRenameFeatures.IsIntent(i)) return "batch_rename_features";
            if (SetComponentLightweight.IsIntent(i)) return "set_component_lightweight";
            if (SetRebuildVerification.IsIntent(i)) return "set_rebuild_verification";
            if (EditFeatureParameter.IsIntent(i)) return "edit_feature_parameter";
            if (EditLastFeature.IsIntent(i)) return "edit_last_feature";
            // test-loop no-change finding (change-outer-ring-diameter): the cloud returned a ZERO-op parse (conf=0.15,
            // "make the outer ring OD 150mm") with no local route to retry, so nothing ran at all — same live-
            // dispatch-gap shape as measure_bolt_circle/count_named_components/delete_component above. SetDimension's
            // own matcher (verb + a number) is broad, so it sits near the END of this chain — after every more
            // specific local route — and only ever engages once the cloud has ALREADY failed to parse anything.
            if (SetDimension.IsSetDimensionIntent(i)) return "set_dimension";
            if (Doctor.IsDoctorIntent(i)) return "diagnose";                                              // demo #12 (broad — last)
            return null;
        }

        // Offline plate/block/cube cue — WIDER than the ported CreatePlate.IsIntent, which demands an AxBxC mm size
        // or the literal word "plate" and would therefore strand a plain "create a rectangular block" or "... block
        // with a hole of 10mm" offline (nothing runs today). Local-only: the ported matcher keeps its strict shape
        // when it self-fires; this net only ADDS the verb + plate-family-noun (plate/panel/slab/blank/block/cube/
        // rectangular) route, letting CreatePlate fill its sensible 100×60×8 defaults. Guarded so document/object
        // nouns it must NOT claim stay on their own routes — "create a new blank part" (a part document, not a
        // solid) still belongs to create_part / insert_new_part_in_context.
        private static bool IsOfflinePlateIntent(string i)
        {
            if (string.IsNullOrEmpty(i)) return false;
            if (CreatePlate.IsIntent(i)) return true;
            if (Regex.IsMatch(i, @"\b(part|component|assembly|drawing|mate|sketch|pattern|configur|reference)\b")) return false;
            bool noun = Regex.IsMatch(i, @"\b(plate|panel|slab|blank|block|cube|rectangular)\b");
            bool verb = Regex.IsMatch(i, @"\b(create|make|build|new|start)\b");
            return verb && noun;
        }

        private static bool IsAffirmative(string s)
        {
            s = (s ?? "").Trim().ToLowerInvariant();
            return Regex.IsMatch(s, @"^(go|go ahead|yes|yep|yeah|ok|okay|sure|proceed|do it|confirm|continue|please do)\b");
        }

        // broad = scope explicitly says all/every/entire, OR the assembly is big enough that Rule #11 wants a heads-up.
        private static bool IsBroad(IntentOperation op, IModelDoc2 doc)
        {
            string scope = op?.Parameters?["scope"]?.ToString() ?? "";
            if (Regex.IsMatch(scope, @"\b(all|every|everything|entire|whole|complete|full)\b", RegexOptions.IgnoreCase)) return true;
            return CompCount(doc) > 25;
        }
        private static int CompCount(IModelDoc2 doc)
        {
            try { var a = doc as AssemblyDoc; if (a != null) return a.GetComponentCount(false); } catch { }
            return 0;
        }
        // Count sibling .slddrw files next to the open doc — the drawing-package preview needs a count without
        // pulling in DrawingPkg internals.
        private static int DrawingPkgCount(IModelDoc2 doc)
        {
            try
            {
                string p = doc.GetPathName();
                if (string.IsNullOrEmpty(p)) return 0;
                string dir = System.IO.Path.GetDirectoryName(p);
                if (string.IsNullOrEmpty(dir) || !System.IO.Directory.Exists(dir)) return 0;
                return System.IO.Directory.GetFiles(dir, "*.slddrw").Length;
            }
            catch { return 0; }
        }
        // Read-only handlers log as read_run (like scan): they never write the model.
        private static bool IsReadHandler(string name)
        {
            return name == "scan" || name == "interference" || name == "rebuild_profile"
                || name == "diagnose" || name == "compare_versions" || name == "change_impact"
                || name == "validate_props" || name == "find_duplicates" || name == "wall_thickness" || name == "arc_height" || name == "hole_spacing" || name == "mesh_openings" || name == "count_through_holes"
                || name == "get_mass_properties" || name == "get_bounding_box" || name == "list_configurations" || name == "list_features"
                || name == "list_equations";
        }

        // ---- GRACEFUL DEGRADATION (Labtec bug #1): when the cloud router answers a free-form / unrecognized ask
        //      with a canned dead-end ("didn't map that to one of my tools"), an empty clarify/answer, or the SAME
        //      dead-end twice in a row, the panel must NOT relay that back and forth — the user gets ONE concrete
        //      suggestion with a real example command, then the thread is dropped so it can't repeat. A genuine
        //      router question (which dimension / which component) never matches these patterns and relays normally.
        private static readonly Regex[] CloudDeadEndPatterns =
        {
            new Regex(@"\b(map(ped|ping)?s?)\b.{0,40}\b(tool|command|action|operation)s?\b", RegexOptions.IgnoreCase),
            new Regex(@"\b(tool|command|action|operation)s?\b.{0,40}\b(map(ped|ping)?s?)\b", RegexOptions.IgnoreCase),
            new Regex(@"\b(no|not|n't|never|unable|can'?t|cannot|couldn't|didn't|isn't|doesn't)\b.{0,25}\b(map(ped|ping)?s?)\b", RegexOptions.IgnoreCase),
            new Regex(@"\b(map(ped|ping)?s?)\b.{0,25}\b(no|not|n't|never|unable|can'?t|cannot|couldn't|didn't|isn't|doesn't)\b", RegexOptions.IgnoreCase),
            new Regex(@"\bnot (one|any) of (my|our|the|these|those)\b", RegexOptions.IgnoreCase),
            new Regex(@"\b(no|no\s*named|no\s*known|don't have|doesn't have|no handler|lack(s)?)\b.{0,25}\b(tool|handler|toolkit|capabilit|command|action|operation)s?\b", RegexOptions.IgnoreCase),
            new Regex(@"\b(outside|beyond) (my|our|the)\b.{0,25}\b(capabilit|reach|tool|skill|expertise)\b", RegexOptions.IgnoreCase),
            new Regex(@"\b(can'?t|cannot|unable to|don't know how|doesn't know how|not sure how|no way)\b.{0,30}\b(do|perform|act|map|interpret|figure)\b", RegexOptions.IgnoreCase),
            new Regex(@"\bunclear (how|what|which) (to|i)\b", RegexOptions.IgnoreCase)
        };

        private bool IsCloudDeadEnd(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return true;   // an empty clarify/answer = the router had nothing to say
            foreach (var re in CloudDeadEndPatterns)
                if (re.IsMatch(text)) return true;
            return false;
        }

        // The single, concrete, never-repeated fallback for an unrecognized/unmapped request. The message names a
        // real example command for the CURRENT doc type so the user always has an action to take next.
        private void SendUnmappedSuggestion()
        {
            _pendingIntent = null;        // drop the thread — never re-fold a request the router couldn't map
            _lastRelayedClarify = null;   // and forget any prior relay so a fresh, real question can still be asked
            var doc = SwAddin.SwApp?.ActiveDoc as IModelDoc2;
            bool isAsm = false;
            if (doc != null) { try { isAsm = (int)doc.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY; } catch { } }
            Send(new { type = "answer", answer = isAsm
                ? "I couldn't map that to a real Forge action, so nothing was changed. Try one of these: \"mate the flange to the tube\", \"scan this assembly\", or \"what breaks if I change the top plate?\"."
                : "I couldn't map that to a real Forge action, so nothing was changed. Try one of these: \"set the boss height to 80mm\", \"drill an 8mm hole\", or \"get the mass properties\"." });
        }
    }
}
