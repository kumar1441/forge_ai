using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground-truth measurement for the headless harness.
    ///
    /// Deliberately shares NOTHING with the AutoMate pipeline's geometry interpretation or its
    /// self-reported verdicts — it re-reads post-rebuild geometry from the SolidWorks API with its
    /// own code so the harness cannot inherit the self-certification blind spots we killed all day.
    /// The backbone checks (translate snap-back, GetConstrainedStatus, mate inventory) don't touch
    /// mating logic at all; the seated-gap / through-stack checks are a second, independent
    /// implementation of the geometry.
    /// </summary>
    public static partial class GroundTruth
    {
        // Stage breadcrumb, set by the harness. No-op by default so nothing else has to know about it.
        public static Action<string> Trace = s => { };

        private class Cy { public double[] O; public double[] D; public double R; public Component2 Comp; }
        private class Pl { public double[] P; public double[] N; public double Area; public Component2 Comp; }

        // One ground-truth block by name, for TARGETED MODE. Only the keys a real-model fixture actually needs are
        // wired; anything else returns an explicit unknown marker rather than null, so a typo in test-config surfaces
        // as a failed assertion naming the bad key instead of a mysterious comparison against nothing.
        private static JToken MeasureByKey(string key, ISldWorks app, IModelDoc2 model, string intent)
        {
            switch (key)
            {
                case "listComponents":       return MeasureListComponents(model);
                case "duplicateComponents":  return MeasureFindDuplicateComponents(model);
                case "resolveDuplicatePaths": return MeasureResolveDuplicatePaths(model);
                case "interfere":            return MeasureInterfere(app, model);
                case "ghostRefs":            return MeasureDetectGhostReferences(model);
                case "floating":             return MeasureFindFloating(model);
                case "subAssemblies":        return MeasureListSubassemblies(model);
                case "dissolveSub":          return MeasureDissolveSubassembly(model);
                case "subFlex":              return MeasureSetSubassemblyFlexibility(model);
                case "explodeRepair":        return MeasureRepairExplodedView(model);
                case "designTable":          return MeasureManageDesignTable(model);
                case "fillSurface":          return MeasureFillSurface(model);
                case "describeGeometry":     return MeasureDescribeGeometry(model, intent);
                case "highlightEntities":    return MeasureHighlightEntities(model, intent);
                case "knitSurfaces":         return MeasureKnitSurfacesToSolid(model);
                case "arrangeAnnotations":   return MeasureArrangeDrawingAnnotations(model);
                case "widthMate":            return MeasureAddWidthMate(model);
                case "componentInfo":        return MeasureGetComponentInfo(model);
                case "componentConfig":      return MeasureGetComponentConfig(model);
                case "componentMass":        return MeasureGetComponentMass(model);
                case "listMates":            return MeasureListMates(model);
                case "suppressMate":         return MeasureSuppressMate(model);
                case "fileRefs":             return MeasureGetFileReferences(model);
                case "lightweight":          return MeasureSetComponentLightweight(model);
                case "renameComponent":      return MeasureRenameComponent(model);
                case "getProperties":        return MeasureGetProperties(model);
                case "copyPropertiesBetweenFiles": return MeasureCopyPropertiesBetweenFiles(app);
                case "createConfig":         return MeasureCreateConfiguration(model);
                case "activeDoc":            return MeasureGetActiveDocument(model);
                case "findDupes":            return MeasureFindDupes(app, model);
                case "redWave":              return MeasureRedWave(app, model);
                case "rebuildErrorList":     return MeasureGetRebuildErrors(model);
                case "fileHealth":           return MeasureDetectFileHealth(model);
                case "lockedFiles":          return MeasureHandleLockedFiles(model);
                case "inContextWrites":      return MeasureDetectInContextWrites(model);
                case "unknownFeatures":      return MeasureHandleUnknownFeatures(model);
                case "asmFeatures":          return MeasureHandleAssemblyFeatures(model);
                case "derivedChain":         return MeasureTraceDerivedParts(app, model);
                case "autosaveRecovery":     return MeasureRecoverAutosave(app, model);
                case "configExplosion":      return MeasureHandleConfigExplosion(model);
                case "simArtifacts":         return MeasureDetectSimulationArtifacts(model);
                case "quarantineFile":       return MeasureQuarantineFile(model, intent);
                case "rollbackBar":          return MeasureRollbackBar(model);
                case "scaleSanity":          return MeasureValidateScaleSanity(model);
                case "sharedSketches":       return MeasureDetectSharedSketches(model);
                case "localizedNames":       return MeasureResolveLocalizedNames(model);
                case "cutList":              return MeasureGetCutList(model);
                case "fixtureCapacity":      return MeasureGetFixtureCapacity(model);
                case "normalizeUnits":       return MeasureNormalizeUnits(model);
                case "rebuildDoc":           return MeasureRebuildDocument(model);
                case "compareBodies":        return MeasureCompareBodies(model);
                case "partSymmetry":         return MeasureCheckPartSymmetry(model);
                case "sketchPolygon":        return MeasureAddSketchPolygon(model);
                case "sketchSlot":           return MeasureAddSketchSlot(model);
                case "sketchEllipse":        return MeasureAddSketchEllipse(model);
                case "constructionGeom":     return MeasureAddConstructionGeometry(model);
                case "sketchArc":            return MeasureAddSketchArc(model);
                case "addSketchEntity":      return MeasureAddSketchEntity(app, model);
                case "createSketch":         return MeasureCreateSketch(app, model);
                case "create3DSketch":       return MeasureCreate3DSketch(app, model);
                case "importDxfToSketch":    return MeasureImportDxfToSketch(app, model);
                case "copySketchToPart":     return MeasureCopySketchToPart(app, model);
                case "addSketchDimension":   return MeasureAddSketchDimension(model);
                case "addSketchRelation":    return MeasureAddSketchRelation(model);
                case "sketchSpline":         return MeasureAddSketchSpline(model);
                case "sketchText":           return MeasureAddSketchText(model);
                case "sketchOffset":         return MeasureOffsetSketchEntities(model);
                case "sketchConvert":        return MeasureConvertEntities(model);
                case "sketchTrim":           return MeasureTrimExtendSketch(model);
                case "sketchMirror":         return MeasureMirrorSketchEntities(model);
                case "sketchPattern":        return MeasurePatternSketchEntities(model);
                case "revolveFeature":       return MeasureAddRevolve(model);
                case "sweepFeature":         return MeasureAddSweep(model);
                case "loftFeature":          return MeasureAddLoft(model);
                case "boundaryFeature":      return MeasureCreateBoundaryFeature(model);
                case "thickenFeature":       return MeasureCreateThicken(model);
                case "helixFeature":         return MeasureAddHelix(model);
                case "curveFeature":         return MeasureCreateCurve(model);
                case "ribFeature":           return MeasureCreateRib(model);
                case "extrudedSurface":      return MeasureCreateExtrudedSurface(model);
                case "sweptSurface":         return MeasureCreateSweptSurface(model);
                case "varFilletFeature":     return MeasureCreateVariableFillet(model);
                case "domeFeature":          return MeasureAddDome(model);
                case "combineBodies":        return MeasureCombineBodies(model);
                case "splitBody":            return MeasureSplitBody(model);
                case "saveBodies":           return MeasureSaveBodiesAsParts(app, model);
                case "runImportDiagnostics": return MeasureRunImportDiagnostics(model);
                case "checkGeometryErrors":  return MeasureCheckGeometryErrors(model);
                case "addCenterMarks":       return MeasureAddCenterMarks(model);
                case "replaceSheetFormat":   return MeasureReplaceSheetFormat(model);
                case "updateRevisionTable":  return MeasureUpdateRevisionTable(model);
                case "draftingStandards":    return MeasureCheckDraftingStandards(model);
                case "editLastFeature":      return MeasureEditLastFeature(model);
                case "batchCustomProp":      return MeasureBatchUpdateCustomProperties(model, ParseBatchPropName(intent));
                case "boltCircle":           return MeasureBoltCircle(app, model);
                case "faceGap":              return MeasureFaceGap(app, model);
                case "countComponents":      return MeasureCountNamedComponents(model, intent);
                case "countGearTeeth":       return MeasureCountGearTeeth(model, intent);
                case "batchConvertFiles":    return MeasureBatchConvertFiles(model);
                case "importFile":           return MeasureImportFile(app, intent);
                case "openDocument":         return MeasureOpenDocument(app, intent);
                case "saveDocumentAs":       return MeasureSaveDocumentAs(model, intent);
                case "saveDocument":         return MeasureSaveDocument(model);
                case "closeDocument":        return MeasureCloseDocument(app, intent);
                case "createDrawing":        return MeasureCreateDrawing(app);
                case "createPart":            return MeasureCreatePart(app);
                case "createAssembly":        return MeasureCreateAssembly(app);
                case "insertStandardViews":  { string p = null; try { p = model.GetPathName(); } catch { } return MeasureInsertStandardViews(app, p); }
                case "insertView":           return MeasureInsertView(app);
                case "setViewScale":         return MeasureSetViewScale(app);
                case "deleteView":           return MeasureDeleteView(app);
                case "addNote":              return MeasureAddNote(app);
                case "importModelDimensions": return MeasureImportModelDimensions(app);
                case "listDanglingDimensions": return MeasureListDanglingDimensions(app);
                case "packAndGo":             return MeasurePackAndGo(app, model, intent);
                case "captureViewport":       return MeasureCaptureViewport(model, intent);
                case "captureSection":        return MeasureCaptureSection(model, intent);
                case "insertNewPartInContext": return MeasureInsertNewPartInContext(model, intent);
                case "createLayoutSketch":    return MeasureCreateLayoutSketch(model);
                case "selectFace":            return MeasureSelectFace(model);
                case "bodies":                return MeasureGetBodies(model);
                case "faces":                 return MeasureGetFaces(model);
                default:
                    return new JObject { ["unknownGroundTruthKey"] = key };
            }
        }

        // ---- public entry: measure the assembly, return a JSON blob the harness asserts against ----
        // skipRebuild: perf-only (2026-07-29, test-loop throughput speedup #2). Harness.cs already does a
        // ForceRebuild3 immediately after opening a doc, before this is ever called for the run0 baseline — so
        // that FIRST call has nothing new to rebuild (no handler has touched the doc yet) and the doc-wide
        // rebuild here (30s+ on an imported dumb solid) is pure redundant cost. Every OTHER call site (run1/run2,
        // after a handler may have mutated the doc) omits this and keeps rebuilding, unchanged.
        public static string Measure(ISldWorks app, IModelDoc2 model, string intent = null, string[] only = null, bool skipRebuild = false)
        {
            if ((int)model.GetType() == (int)swDocumentTypes_e.swDocPART)
            {
                // TARGETED MODE for parts (2026-08-01, same rationale/shape as the assembly branch below — added
                // 2026-07-24 for a 55MB assembly, but never mirrored here). A heavy real PART (e.g. the seller's
                // 1560-face requirement-#9 dodecahedron) pays for ALL ~140 part-branch measurements below on every
                // one of run0/run1/run2 even when the case only asserts on one or two of them — the untargeted cost
                // of that full sweep grew with every measurement added since 07-24 and now times out on real
                // complex geometry. When gtOnly is present, compute ONLY those blocks and return early.
                if (only != null && only.Length > 0)
                {
                    var tj = new JObject();
                    tj["targetedMode"] = true;
                    tj["targetedKeys"] = new JArray(only);
                    foreach (var k in only) { Trace("  gt: " + k + " start"); tj[k] = MeasureByKey(k, app, model, intent); Trace("  gt: " + k + " done"); }
                    return tj.ToString();
                }

                // part-doc measurement is simplify-centric, but custom properties live on parts too — merge the
                // getProperties ground truth in so part-scoped read handlers (get_custom_properties) can be asserted.
                var pj = JObject.Parse(MeasureSimplify(model));
                pj["getProperties"] = MeasureGetProperties(model);
                pj["copyPropertiesBetweenFiles"] = MeasureCopyPropertiesBetweenFiles(app); // copy_properties_between_files (tool 142) — independent active-doc property re-read
                pj["setCustomProp"] = MeasureSetCustomProperty(model);
                pj["docUnits"] = MeasureSetDocumentUnits(model);
                pj["angUnits"] = MeasureSetAngularUnits(model);
                pj["decPlaces"] = MeasureSetDecimalPlaces(model);
                pj["draftStandard"] = MeasureSetDraftingStandard(model); // independent drafting/dimensioning-standard read
                pj["matDensity"] = MeasureGetMaterialDensity(model);
                pj["getDimensions"] = MeasureGetDimensions(model);
                pj["drivingDims"] = MeasureGetDrivingDimensions(model);  // raw rows + name-resolution check; PS derives the splits
                pj["featureInfo"] = MeasureGetFeatureInfo(model);
                pj["findFeatures"] = MeasureFindFeaturesByType(model);
                pj["featureDeps"] = MeasureListFeatureDependencies(model);
                pj["featureNames"] = MeasureFindFeatureByName(model);
                pj["batchRename"] = MeasureBatchRenameFeatures(model);
                pj["reorderFeature"] = MeasureReorderFeature(model);
                pj["sketchDiag"] = MeasureDiagnoseSketch(model);
                pj["fullyDefineSketch"] = MeasureFullyDefineSketch(model);
                pj["skipPatternInstance"] = MeasureSkipPatternInstance(model);
                pj["configFeatureSuppression"] = MeasureConfigFeatureSuppression(model);
                pj["configSpecificDimension"] = MeasureConfigSpecificDimension(model);
                pj["whereUsed"] = MeasureFindWhereUsed(model);   // raw folder roster; the harness derives the expected parents
                pj["renameFileWithReferences"] = MeasureRenameFileWithReferences(app, model); // rename_file_with_references (tool 128) — independent file-level dependency re-scan
                pj["moveFileWithReferences"] = MeasureMoveFileWithReferences(app, model); // move_file_with_references (tool 129) — independent file-level dependency re-scan, two-folder aware
                pj["batchConvertFiles"] = MeasureBatchConvertFiles(model); // raw forge-converted/ listing post-run
                pj["importFile"] = MeasureImportFile(app, intent); // import_file (tool 137) — independent app-level lookup, not model-scoped
                pj["openDocument"] = MeasureOpenDocument(app, intent); // open_document (tool 124) — independent app-level lookup, not model-scoped
                pj["saveDocumentAs"] = MeasureSaveDocumentAs(model, intent); // save_document_as (tool 126) — independent disk listing, not the handler's own report
                pj["captureViewport"] = MeasureCaptureViewport(model, intent); // capture_viewport (tool 234) — independent PNG re-read + IHDR re-decode, not the handler's own report
                pj["captureSection"] = MeasureCaptureSection(model, intent); // capture_section (tool 235) — independent before/after PNG byte-compare, not the handler's own report
                pj["selectFace"] = MeasureSelectFace(model); // select_face (tool 13) — independent SelectionMgr re-read + linked-list face re-derivation, not the handler's own report
                pj["selectEdge"] = MeasureSelectEdge(model); // select_edge (tool 14) — independent face-derived edge re-derivation (not the handler's whole-body edge array), not the handler's own report
                pj["selectPlane"] = MeasureSelectPlane(model, intent); // select_plane (tool 15) — independent GetFeatures(true) array re-derivation (not the handler's linked-list walk), not the handler's own report
                pj["getSelectedEntities"] = MeasureGetSelectedEntities(model, intent); // get_selected_entities (tool 16) — expected count/area derived from the intent text + independent face re-derivation, never a live-selection re-read
                pj["saveDocument"] = MeasureSaveDocument(model); // save_document (tool 125) — independent mtime read, not the handler's own report
                pj["closeDocument"] = MeasureCloseDocument(app, intent); // close_document (tool 127) — independent app-level lookup, not model-scoped
                pj["createDrawing"] = MeasureCreateDrawing(app); // create_drawing (tool 101) — independent active-doc read, not the handler's own report
                pj["createPart"] = MeasureCreatePart(app); // create_part (tool 228) — independent active-doc read, not the handler's own report
                pj["createAssembly"] = MeasureCreateAssembly(app); // create_assembly (tool 229) — independent active-doc read, not the handler's own report
                { string p = null; try { p = model.GetPathName(); } catch { } pj["insertStandardViews"] = MeasureInsertStandardViews(app, p); } // insert_standard_views (tool 102) — independent view-list re-read
                pj["insertView"] = MeasureInsertView(app); // insert_view (tool 103) — independent per-view name+orientation+scale re-read
                pj["setViewScale"] = MeasureSetViewScale(app); // set_view_scale (tool 107) — independent per-view name+orientation+scale re-read
                pj["deleteView"] = MeasureDeleteView(app); // delete_view (tool 106) — independent view-list re-read
                pj["addNote"] = MeasureAddNote(app); // add_note (tool 112) — independent annotation-list re-read
                pj["importModelDimensions"] = MeasureImportModelDimensions(app); // import_model_dimensions (tool 108) — independent per-view dimension-count re-read
                pj["listDanglingDimensions"] = MeasureListDanglingDimensions(app); // list_dangling_dimensions (tool 110) — independent dangling re-read
                pj["treeFolder"] = MeasureCreateTreeFolder(model); // raw top-level walk + each folder's children
                pj["rebuildVerify"] = MeasureSetRebuildVerification(app, model); // both scopes of the toggle, kept apart
                pj["sheetMetal"] = MeasureGetSheetMetalProps(model);
                pj["sheetMetalValid"] = MeasureValidateSheetMetal(model);
                pj["flatDxf"] = MeasureFlatDxf(app, model);   // flat-dxf runs on a PART fixture; only the assembly branch had it
                pj["activeDoc"] = MeasureGetActiveDocument(model);
                pj["measureAngle"] = MeasureMeasureAngle(model);
                pj["edges"] = MeasureGetEdges(model);
                pj["faces"] = MeasureGetFaces(model);
                pj["createConfig"] = MeasureCreateConfiguration(model);
                pj["copyConfig"] = MeasureCopyConfiguration(model);
                pj["renameDim"] = MeasureRenameDimension(model);
                pj["bodies"] = MeasureGetBodies(model);
                pj["material"] = MeasureGetMaterial(model);
                // partMaterials mirrors the assembly branch's shape (one entry per unique part) so the SAME harness
                // assertion works on a standalone PART doc too (test-loop hedge fix curveball-rubber-material, a
                // single-part LEAF SPRING model with no assembly/components to enumerate).
                {
                    var pm = new JArray();
                    var mj = MeasureGetMaterial(model);
                    string selfName = null; try { selfName = System.IO.Path.GetFileNameWithoutExtension(model.GetPathName()); } catch { }
                    var pmo = new JObject(); pmo["name"] = selfName; pmo["material"] = mj["material"]; pmo["kind"] = "part";
                    pm.Add(pmo);
                    pj["partMaterials"] = pm;
                }
                pj["sketches"] = MeasureGetSketches(model);
                pj["partNumber"] = MeasureGetPartNumber(model);
                pj["appearance"] = MeasureGetAppearance(model);
                pj["patterns"] = MeasureGetPatternInfo(model);
                pj["editFeatureParam"] = MeasureEditFeatureParameter(model);   // independent extrude-depth read (tool 73)
                pj["editLastFeature"] = MeasureEditLastFeature(model);         // edit_last_feature (tool 236) — part branch
                pj["refGeometry"] = MeasureGetRefGeometry(model);
                pj["refAxis"] = MeasureCreateRefAxis(model);
                pj["coordSys"] = MeasureCreateCoordSys(model);
                // compare runs part-vs-part too (demo #2's fixture is a PART pair); only the assembly branch had it,
                // so the read-only fingerprint (dimCount/dimSumM) was missing exactly where the case reads it.
                pj["compare"] = MeasureCompare(app, model);
                // the rebuild profiler's fixture is a PART with one expensive pattern (demo #7); its unchanged-model
                // proof (feature count + suppression state + an independent rebuild time) lives here too.
                pj["rebuildProfile"] = MeasureProfiler(app, model);
                pj["rebuildErrorList"] = MeasureGetRebuildErrors(model);   // get_rebuild_errors (tool 96) — part branch
                pj["fileHealth"] = MeasureDetectFileHealth(model);         // detect_file_health (tool 239) — part branch
                pj["lockedFiles"] = MeasureHandleLockedFiles(model);       // handle_locked_files (tool 248) — part branch
                pj["inContextWrites"] = MeasureDetectInContextWrites(model); // detect_in_context_writes (tool 242) — part branch
                pj["unknownFeatures"] = MeasureHandleUnknownFeatures(model); // handle_unknown_features (tool 243) — part branch
                pj["derivedChain"] = MeasureTraceDerivedParts(app, model);   // trace_derived_parts (tool 251) — part branch
                pj["autosaveRecovery"] = MeasureRecoverAutosave(app, model); // recover_autosave (tool 253) — part branch
                pj["configExplosion"] = MeasureHandleConfigExplosion(model); // handle_config_explosion (tool 255) — part branch
                pj["simArtifacts"] = MeasureDetectSimulationArtifacts(model); // detect_simulation_artifacts (tool 256) — part branch
                pj["quarantineFile"] = MeasureQuarantineFile(model, intent); // quarantine_file (tool 257) — part branch
                pj["rollbackBar"] = MeasureRollbackBar(model);             // handle_rollback_bar (tool 240) — part branch
                pj["scaleSanity"] = MeasureValidateScaleSanity(model);     // validate_scale_sanity (tool 254) — part branch
                pj["sharedSketches"] = MeasureDetectSharedSketches(model); // detect_shared_sketches (tool 252) — part branch
                pj["localizedNames"] = MeasureResolveLocalizedNames(model); // resolve_localized_names (tool 245) — part branch
                pj["cutList"] = MeasureGetCutList(model);                   // get_cut_list (tool 165) — part branch
                pj["fixtureCapacity"] = MeasureGetFixtureCapacity(model);   // get_fixture_capacity — part branch
                pj["rebuildDoc"] = MeasureRebuildDocument(model);          // rebuild_document (tool 95) — part branch
                pj["compareBodies"] = MeasureCompareBodies(model);         // compare_bodies (tool 170) — part branch
                pj["partSymmetry"] = MeasureCheckPartSymmetry(model);      // check_part_symmetry (tool 176) — part branch
                pj["sketchPolygon"] = MeasureAddSketchPolygon(model);      // add_sketch_polygon (tool 196) — part branch
                pj["sketchSlot"] = MeasureAddSketchSlot(model);            // add_sketch_slot (tool 195) — part branch
                pj["sketchEllipse"] = MeasureAddSketchEllipse(model);      // add_sketch_ellipse (tool 197) — part branch
                pj["constructionGeom"] = MeasureAddConstructionGeometry(model);  // add_construction_geometry (tool 204) — part branch
                pj["sketchArc"] = MeasureAddSketchArc(model);              // add_sketch_arc (tool 117) — part branch
                pj["addSketchEntity"] = MeasureAddSketchEntity(app, model); // add_sketch_entity (tool 82) — part branch
                pj["createSketch"] = MeasureCreateSketch(app, model);       // create_sketch (tool 81) — part branch
                pj["create3DSketch"] = MeasureCreate3DSketch(app, model);   // create_3d_sketch (tool 123) — part branch
                pj["importDxfToSketch"] = MeasureImportDxfToSketch(app, model); // import_dxf_to_sketch (tool 205) — part branch
                pj["copySketchToPart"] = MeasureCopySketchToPart(app, model);   // copy_sketch_to_part (tool 152) — part branch
                pj["insertLibraryFeature"] = MeasureInsertLibraryFeature(model); // insert_library_feature (tool 218) — independent feature-tree recount
                pj["addSketchDimension"] = MeasureAddSketchDimension(model); // add_sketch_dimension (tool 83) — part branch
                pj["addSketchRelation"] = MeasureAddSketchRelation(model);   // add_sketch_relation (tool 84) — part branch
                pj["sketchSpline"] = MeasureAddSketchSpline(model);        // add_sketch_spline (tool 116) — part branch
                pj["sketchText"] = MeasureAddSketchText(model);            // add_sketch_text (tool 198) — part branch
                pj["sketchOffset"] = MeasureOffsetSketchEntities(model);   // offset_sketch_entities (tool 199) — part branch
                pj["sketchConvert"] = MeasureConvertEntities(model);       // convert_entities (tool 200) — part branch
                pj["sketchTrim"] = MeasureTrimExtendSketch(model);         // trim_extend (tool 201) — part branch
                pj["sketchMirror"] = MeasureMirrorSketchEntities(model);   // mirror_sketch_entities (tool 202) — part branch
                pj["sketchPattern"] = MeasurePatternSketchEntities(model); // pattern_sketch_entities (tool 203) — part branch
                pj["revolveFeature"] = MeasureAddRevolve(model);           // add_revolve (tool 118) — part branch
                pj["sweepFeature"] = MeasureAddSweep(model);               // add_sweep (tool 119) — part branch
                pj["loftFeature"] = MeasureAddLoft(model);                 // add_loft (tool 120) — part branch
                pj["boundaryFeature"] = MeasureCreateBoundaryFeature(model); // create_boundary_feature (tool 210, PROBE) — part branch
                pj["thickenFeature"] = MeasureCreateThicken(model);         // create_thicken (tool 209, PROBE) — part branch
                pj["helixFeature"] = MeasureAddHelix(model);               // add_helix (tool 213) — part branch
                pj["curveFeature"] = MeasureCreateCurve(model);            // create_curve (tool 214) — part branch
                pj["ribFeature"] = MeasureCreateRib(model);                // create_rib (tool 207) — part branch
                pj["extrudedSurface"] = MeasureCreateExtrudedSurface(model); // create_extruded_surface (tool 222) — part branch
                pj["sweptSurface"] = MeasureCreateSweptSurface(model);       // create_swept_lofted_surface (tool 223) — part branch
                pj["varFilletFeature"] = MeasureCreateVariableFillet(model);// create_variable_fillet (tool 217) — part branch
                pj["domeFeature"] = MeasureAddDome(model);                 // add_dome (tool 121) — part branch
                pj["combineBodies"] = MeasureCombineBodies(model);         // combine_bodies (tool 219) — part branch
                pj["splitBody"] = MeasureSplitBody(model);                 // split_body (tool 220) — part branch
                pj["knitSurfaces"] = MeasureKnitSurfacesToSolid(model);    // knit_surfaces_to_solid (tool 181) — part branch
                pj["saveBodies"] = MeasureSaveBodiesAsParts(app, model);   // save_bodies_as_parts (tool 166) — part branch
                pj["runImportDiagnostics"] = MeasureRunImportDiagnostics(model); // run_import_diagnostics (tool 154) — part branch
                pj["checkGeometryErrors"] = MeasureCheckGeometryErrors(model);   // check_geometry_errors (tool 156) — part branch
                // bolt-circle verification (independent circular-edge PCD/hole-count/hole-diameter read) — the
                // assembly branch below already merges this; a standalone PART (e.g. a wheel rim with lug holes)
                // needs it too (test-loop wrong-answer fix rim-count-holes), and MeasureBoltCircle handles both doc
                // types on its own.
                pj["boltCircle"] = MeasureBoltCircle(app, model);
                pj["countGearTeeth"] = MeasureCountGearTeeth(model, intent);
                pj["designTable"] = MeasureManageDesignTable(model);   // manage_design_table (tool 194) — part branch
                pj["fillSurface"] = MeasureFillSurface(model);   // fill_surface (tool 226) — part branch
                pj["describeGeometry"] = MeasureDescribeGeometry(model, intent);   // describe_geometry (tool 237) — part branch
                pj["highlightEntities"] = MeasureHighlightEntities(model, intent);   // highlight_entities (tool 238) — part branch
                return pj.ToString();
            }

            if ((int)model.GetType() == (int)swDocumentTypes_e.swDocDRAWING)
            {
                // A DRAWING has no components, no bodies and no mates, so none of the assembly/part measurements apply.
                // Its own structural ground truth is the whole measurement.
                var dj = new JObject();
                dj["drawingViews"] = MeasureGetDrawingViews(model);
                dj["updateSheetReferences"] = MeasureUpdateSheetReferences(model); // update_sheet_references (tool 114) — independent per-view File.Exists re-check
                dj["insertBomTable"] = MeasureInsertBomTable(model); // insert_bom_table (tool 113) — independent IView.GetBomTable() re-check
                dj["insertSectionView"] = MeasureInsertSectionView(model); // insert_section_view (tool 104) — independent GetFirstView()/GetNextView() Type walk
                dj["insertDetailView"] = MeasureInsertDetailView(model); // insert_detail_view (tool 105) — independent GetFirstView()/GetNextView() Type walk
                dj["addDrawingDimension"] = MeasureAddDrawingDimension(model); // add_drawing_dimension (tool 109) — independent GetViews() total display-dimension count
                dj["batchExportDrawings"] = MeasureBatchExportDrawings(model); // batch_export_drawings (tool 134) — independent forge-drawing-export/ disk listing
                dj["addCenterMarks"] = MeasureAddCenterMarks(model); // add_center_marks (tool 162) — independent GetFirstView()/GetNextView() center-mark-count walk
                dj["replaceSheetFormat"] = MeasureReplaceSheetFormat(model); // replace_sheet_format (tool 163) — independent GetSheetNames()/get_Sheet() re-lookup
                dj["updateRevisionTable"] = MeasureUpdateRevisionTable(model); // update_revision_table (tool 184) — independent RevisionTable/TableAnnotation re-cast + row/label re-walk
                dj["draftingStandards"] = MeasureCheckDraftingStandards(model); // check_drafting_standards (tool 185) — independent GetViews()/CustomPropertyManager re-walk
                dj["cleanBomTable"] = MeasureCleanBomTable(model); // clean_bom_table (tool 161) — independent BomFeat->IBomFeature->table walk
                dj["repairBalloonReferences"] = MeasureRepairBalloonReferences(model); // repair_balloon_references (tool 160) — independent IView.IGetFirstNote()/INote walk
                dj["arrangeAnnotations"] = MeasureArrangeDrawingAnnotations(model); // arrange_drawing_annotations (tool 190) — independent per-dimension position + proximity re-check
                return dj.ToString();
            }

            var root = new JObject();
            var asm = model as AssemblyDoc;
            if (asm == null) { root["error"] = "active doc is not an assembly"; return root.ToString(); }
            var mu = (MathUtility)app.GetMathUtility();
            if (!skipRebuild)
            {
                Trace("  gt: rebuild start");
                try { model.ForceRebuild3(false); } catch { }   // measure the SOLVED state
                Trace("  gt: rebuild done");
            }

            // ---- TARGETED MODE (2026-07-24, OPT-IN). The full path below computes ~50 measurements — including a
            //      per-component geometry collection for the fastener analysis — for EVERY case, and Measure() runs on
            //      run0, run1 AND run2. On generated fixtures that is free. On the seller's 55MB / 92-component
            //      grinder it never finished: 17 minutes of wall clock and 1700s of SolidWorks CPU with no result,
            //      and gating just the five expensive named measurements was NOT enough.
            //      A case can now declare exactly which ground-truth blocks it asserts on (gtOnly in test-config) and
            //      only those are computed. When gtOnly is absent — every existing fixture — the code below runs
            //      completely unchanged, so this cannot regress anything that is green today.
            //      An unknown key is NOT silently ignored: it comes back as an explicit unknown marker so a typo in a
            //      config fails loudly instead of quietly asserting against a null. ----
            if (only != null && only.Length > 0)
            {
                root["targetedMode"] = true;
                root["targetedKeys"] = new JArray(only);
                foreach (var k in only) { Trace("  gt: " + k + " start"); root[k] = MeasureByKey(k, app, model, intent); Trace("  gt: " + k + " done"); }
                int tc = 0; try { tc = asm.GetComponentCount(false); } catch { }
                root["gateComponentCount"] = tc;
                return root.ToString();
            }

            var forge = ForgeTaggedComponents(model);          // both mates present (own tree read)
            int totalMates, forgeMates; CountMates(model, out totalMates, out forgeMates);

            // fresh geometry, our own collection
            object[] comps = asm.GetComponents(false) as object[];
            object[] topComps = asm.GetComponents(true) as object[];
            var byName = new Dictionary<string, Component2>();
            var fastCyls = new List<Cy>();
            var flangePlanes = new List<Pl>();
            var fastPlanesBy = new Dictionary<string, List<Pl>>();
            int totalComp = 0, fastCount = 0;
            foreach (var o in comps ?? new object[0])
            {
                var c = o as Component2;
                if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { }
                if (sup) continue;
                totalComp++;
                string nm = null; try { nm = c.Name2; } catch { }
                if (nm != null) byName[nm] = c;
                bool fast = IsFastenerName(nm);
                if (fast) { fastCount++; CollectCyls(mu, c, fastCyls); fastPlanesBy[nm] = CollectPerpPlanes(mu, c); }
                else CollectPerpPlanes(mu, c, flangePlanes);
            }

            var arr = new JArray();
            int overDef = 0;
            foreach (var name in forge)
            {
                var f = new JObject(); f["name"] = name;
                Component2 comp; if (!byName.TryGetValue(name, out comp)) { f["found"] = false; arr.Add(f); continue; }
                f["found"] = true;
                f["kind"] = IsNutName(name) ? "nut" : "bolt";

                // axis from the fastener's own longest cylinder
                Cy axCyl = null; foreach (var cy in fastCyls) if (cy.Comp != null && cy.Comp.Name2 == name) { axCyl = cy; break; }
                if (axCyl != null)
                {
                    double[] ax = axCyl.D, O = axCyl.O;
                    List<Pl> fp; fastPlanesBy.TryGetValue(name, out fp);
                    f["seatGapMm"] = SeatedGap(fp, flangePlanes, ax, O);            // min flush interface, our own measure
                    f["shankThroughStack"] = BoltThroughStack(fp, flangePlanes, ax, O);
                }

                int st = (int)swConstrainedStatus_e.swUnderConstrained; try { st = comp.GetConstrainedStatus(); } catch { }
                f["status"] = StatusName(st);
                if (st == (int)swConstrainedStatus_e.swOverConstrained || st == (int)swConstrainedStatus_e.swNoSolution || st == (int)swConstrainedStatus_e.swInvalidSolution) overDef++;

                f["translateHeldMm"] = TranslateSnapBack(mu, model, comp);          // the drag test, independent of mating logic
                arr.Add(f);
            }

            root["fasteners"] = arr;
            root["forgeMatedComps"] = forge.Count;
            root["totalMateFeatures"] = totalMates;
            root["forgeMateFeatures"] = forgeMates;
            root["overDefined"] = overDef;
            root["totalComponents"] = totalComp;
            root["topLevelComponents"] = topComps == null ? 0 : topComps.Length;
            root["fastenerCount"] = fastCount;

            // ---- general health (handler-agnostic) ----
            int odc = 0;
            foreach (var o in topComps ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                int st = (int)swConstrainedStatus_e.swUnderConstrained; try { st = c.GetConstrainedStatus(); } catch { }
                if (st == (int)swConstrainedStatus_e.swOverConstrained || st == (int)swConstrainedStatus_e.swNoSolution || st == (int)swConstrainedStatus_e.swInvalidSolution) odc++;
            }
            root["overDefinedComponents"] = odc;
            int rw = 0; try { rw = model.Extension.GetWhatsWrongCount(); } catch { }
            root["rebuildErrors"] = rw;
            root["rebuildErrorList"] = MeasureGetRebuildErrors(model);   // get_rebuild_errors (tool 96) independent walk
            root["fileHealth"] = MeasureDetectFileHealth(model);         // detect_file_health (tool 239) independent walk
            root["lockedFiles"] = MeasureHandleLockedFiles(model);       // handle_locked_files (tool 248) independent walk
            root["inContextWrites"] = MeasureDetectInContextWrites(model); // detect_in_context_writes (tool 242) independent walk
            root["unknownFeatures"] = MeasureHandleUnknownFeatures(model); // handle_unknown_features (tool 243) independent walk
            root["asmFeatures"] = MeasureHandleAssemblyFeatures(model);    // handle_assembly_features (tool 250) independent walk — this branch is assembly-only already
            root["derivedChain"] = MeasureTraceDerivedParts(app, model);   // trace_derived_parts (tool 251) independent walk
            root["autosaveRecovery"] = MeasureRecoverAutosave(app, model); // recover_autosave (tool 253) independent walk
            root["configExplosion"] = MeasureHandleConfigExplosion(model); // handle_config_explosion (tool 255) independent walk
            root["simArtifacts"] = MeasureDetectSimulationArtifacts(model); // detect_simulation_artifacts (tool 256) independent walk
            root["quarantineFile"] = MeasureQuarantineFile(model, intent); // quarantine_file (tool 257) independent re-parse
            root["rebuildDoc"] = MeasureRebuildDocument(model);          // rebuild_document (tool 95) clean/flagged + feature-count fingerprint

            // ---- per-component centroids (so the harness can diff run0 vs run1 and find the NEWLY added
            //      instance a mirror created, then verify THAT instance sits at the reflected position) ----
            var carr = new JArray();
            foreach (var o in topComps ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                string nm = null; try { nm = c.Name2; } catch { }
                double[] ctr = BoxCenter(c);
                var jo = new JObject(); jo["name"] = nm;
                if (ctr != null) { jo["cx"] = ctr[0] * 1000.0; jo["cy"] = ctr[1] * 1000.0; jo["cz"] = ctr[2] * 1000.0; }
                carr.Add(jo);
            }
            root["components"] = carr;

            // ---- mirror verification (independent reflected-twin check) ----
            root["mirror"] = MeasureMirror(mu, model, topComps);
            // ---- batch-simplify verification (does EVERY unique part now carry the Forge-Simplified config?) ----
            root["batchSimplify"] = MeasureBatchSimplify(asm);
            // ---- material verification: read each unique part's material back (independent of SetMaterial) ----
            root["partMaterials"] = MeasurePartMaterials(asm);
            // ---- batch custom-property verification (tool 139): every unique part's OWN property read-back ----
            root["batchCustomProp"] = MeasureBatchUpdateCustomProperties(model, ParseBatchPropName(intent));

            // ---- new-handler independent ground truth (each shares nothing with its handler; run0/run1/run2 deltas
            //      prove the read-only / write / idempotency invariants the harness asserts) ----
            root["mirrorSkip"] = MeasureMirrorSkip(app, model);      // per-kind top-level counts; excluded kinds must be UNCHANGED
            root["redWave"] = MeasureRedWave(app, model);            // over-defined / rebuild-flag / mate-count read
            root["upsize"] = MeasureUpsize(app, model);              // per-bolt active-config size read-back
            root["auditToolbox"] = MeasureAuditToolbox(model);       // fastener-kind classification (live/design-table/baked)
            root["listMates"] = MeasureListMates(model);             // independent mate inventory (total/suppressed/by-type)
            root["getProperties"] = MeasureGetProperties(model);     // independent custom-property count (total/sources)
            root["setCustomProp"] = MeasureSetCustomProperty(model); // independent file-scope name->value map
            root["docUnits"] = MeasureSetDocumentUnits(model);       // independent linear-unit read
            root["angUnits"] = MeasureSetAngularUnits(model);        // independent angular-unit read
            root["decPlaces"] = MeasureSetDecimalPlaces(model);      // independent linear decimal-places read
            root["draftStandard"] = MeasureSetDraftingStandard(model); // independent drafting/dimensioning-standard read
            root["componentInfo"] = MeasureGetComponentInfo(model);  // independent per-component flag counts
            root["componentTransform"] = MeasureGetComponentTransform(app, model); // independent component world positions
            root["renameComponent"] = MeasureRenameComponent(model);  // independent component-name inventory (total + names)
            root["refAxis"] = MeasureCreateRefAxis(model);           // independent reference-axis count
            { string fa, fb; ParseDistFrags(intent, out fa, out fb); root["measureDistance"] = MeasureMeasureDistance(app, model, fa, fb); }
            root["selectByFilter"] = MeasureSelectByFilter(model, FilterKind(intent));
            root["selectComponent"] = MeasureSelectComponent(model, intent); // select_component (tool 11) — independent tree-walk re-derivation, not the handler's own report
            root["selectPlane"] = MeasureSelectPlane(model, intent); // select_plane (tool 15) — independent GetFeatures(true) array re-derivation, not the handler's own report
            root["getSelectedEntities"] = MeasureGetSelectedEntities(model, intent); // get_selected_entities (tool 16) — expected count/area derived from the intent text, never a live-selection re-read
            root["suppressMate"] = MeasureSuppressMate(model);       // independent total/suppressed mate count
            root["addMate"] = MeasureAddMate(model);                 // independent total + concentric mate count (add_*_mate)
            root["widthMate"] = MeasureAddWidthMate(model);          // independent tab-centre read + width-mate census (add_width_mate)
            root["mateInfo"] = MeasureGetMateInfo(model, MateFrag(intent)); // independent single-mate type/entities
            root["activeDoc"] = MeasureGetActiveDocument(model);     // independent doc-context read
            root["createConfig"] = MeasureCreateConfiguration(model); // independent configuration inventory
            root["refGeometry"] = MeasureGetRefGeometry(model);      // independent reference-geometry count
            root["floating"] = MeasureFindFloating(model);           // independent floating-component count
            root["subAssemblies"] = MeasureListSubassemblies(model); // independent sub-assembly count
            root["dissolveSub"] = MeasureDissolveSubassembly(model); // independent top-level census + name list (dissolve_subassembly)
            root["subFlex"] = MeasureSetSubassemblyFlexibility(model); // independent per-sub-assembly Solving re-read (set_subassembly_flexibility)
            root["explodeRepair"] = MeasureRepairExplodedView(model); // independent explode-step coverage census (repair_exploded_view)
            root["knitSurfaces"] = MeasureKnitSurfacesToSolid(model); // independent sheet/solid body + volume census (knit_surfaces_to_solid)
            root["componentConfig"] = MeasureGetComponentConfig(model); // independent component-config spread
            root["componentConfigSwitch"] = MeasureChangeComponentConfig(model); // per-instance referenced-config census (scoping proof)
            root["replaceComponent"] = MeasureReplaceComponent(model); // per-instance part-FILE census (swap + scoping proof)
            root["batchReplaceComponents"] = MeasureBatchReplaceComponents(model); // per-instance kind+file census (multi-target swap proof)
            root["deleteComponent"] = MeasureDeleteComponent(model); // independent tree recount (total + bolt count)
            root["insertComponent"] = MeasureInsertComponent(model); // independent recount + per-file census (insert delta)
            root["insertNewPartInContext"] = MeasureInsertNewPartInContext(model, intent); // insert_new_part_in_context (tool 230) — independent component-count delta + on-disk file check
            root["createLayoutSketch"] = MeasureCreateLayoutSketch(model); // create_layout_sketch (tool 231) — independent recursive sketch census + tagged-feature check
            root["componentMass"] = MeasureGetComponentMass(model);  // independent per-part mass
            // ---- EXPENSIVE MEASUREMENTS, GATED (2026-07-24). Measure() runs on run0, run1 AND run2, so every entry
            //      below was paying its cost THREE TIMES per case whether or not the case asserted on it. On generated
            //      fixtures that is free; on a real 55MB / 92-component assembly the native interference detector
            //      alone is what made the run unfinishable (it is also run a 4th time by the handler itself).
            //      VERIFIED BEFORE GATING, not assumed: interfere / rebuildProfile / doctor / compare / drawingPkg are
            //      read by ZERO assertions in run-harness.ps1, and flatDxf's only consumer is a PART fixture served by
            //      the part branch above — so nothing that is green today depends on the assembly branch computing
            //      them. The gate is on INTENT, not on size: #4's `pc fan` has FOUR components and still timed out at
            //      300s, so a component-count threshold would have missed exactly the case that motivated this.
            //      Whatever is skipped is NAMED in skippedExpensive, so a future case that needs one fails loudly on a
            //      null rather than silently reading a zero. ----
            int compCountForGate = 0; try { compCountForGate = asm.GetComponentCount(false); } catch { }
            string gi = (intent ?? "").ToLowerInvariant();
            var skipped = new JArray();
            Func<string, string, Func<JObject>, JToken> gate = (key, wantPattern, compute) =>
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(gi, wantPattern)) return compute();
                skipped.Add(key);
                return JValue.CreateNull();
            };
            root["interfere"] = gate("interfere", @"\b(interfere|interferenc|clash|collision|collide|overlap|penetrat)\w*", () => MeasureInterfere(app, model));
            root["rebuildProfile"] = gate("rebuildProfile", @"\b(rebuild|profile|slow|performance|speed)\b", () => MeasureProfiler(app, model));
            // "check" is deliberately NOT in this pattern: it is far too common a verb (the interference case's own
            // intent starts with it) and would drag the doctor scan into unrelated runs.
            // \w* NOT \b on the tail: the doctor case's own intent is "diagnose this assembly", and `\bdiagnos\b`
            // does not match "diagnose" (the group's trailing \b needs a non-word char after "diagnos"). That typo
            // skipped the doctor ground truth on the ONE case that needs it — measured, not theorised.
            root["doctor"] = gate("doctor", @"\b(doctor|diagnos|health|broken|wrong)\w*", () => MeasureDoctor(app, model));
            root["compare"] = gate("compare", @"\b(compare|diff|revision|version)\b", () => MeasureCompare(app, model));
            root["impact"] = MeasureImpact(app, model);              // immutability fingerprint (feature/dim counts)
            // drawingPkg OPENS every .slddrw sitting next to the model — on a real customer folder that is the single
            // most expensive measurement here, and nothing but a drawing case ever reads it.
            root["drawingPkg"] = gate("drawingPkg", @"\b(drawing|drawings|drw|slddrw|sheet|pdf)\b", () => MeasureDrawingPkg(app, model));
            root["flatDxf"] = gate("flatDxf", @"\b(dxf|flat|sheet metal|sheetmetal|laser|nest)\b", () => MeasureFlatDxf(app, model));
            root["skippedExpensive"] = skipped;
            root["gateComponentCount"] = compCountForGate;
            root["listComponents"] = MeasureListComponents(model);   // assembly roster incl. SUPPRESSED instances
            root["duplicateComponents"] = MeasureFindDuplicateComponents(model); // components backed by the same file >1x

            root["ghostRefs"] = MeasureDetectGhostReferences(model); // mates still pointing at suppressed components
            root["lightweight"] = MeasureSetComponentLightweight(model); // per-component load state, cross-checked against the assembly-level count
            root["fileRefs"] = MeasureGetFileReferences(model);       // referenced files, from the component tree (not the dependency API)
            root["packAndGo"] = gate("packAndGo", @"\bpack\b", () => MeasurePackAndGo(app, model, intent)); // pack_and_go (tool 133) — independent disk listing + fresh reopen of the packaged copy
            root["repairMissingReferences"] = MeasureRepairMissingReferences(model); // repair_missing_references (tool 132) — independent per-component File.Exists re-check
            root["repairMate"] = MeasureRepairMate(model); // repair_mate (tool 62) — independent missing-component-then-mate-walk re-check
            root["resolveDuplicatePaths"] = MeasureResolveDuplicatePaths(model); // resolve_duplicate_paths (tool 241) — independent leaf-name-group + newest-file-wins re-check
            // ---- validate-properties ground truth (independent missing-material / missing+duplicate PN / no-weight counts + read-only fingerprint) ----
            root["validateProps"] = MeasureValidateProps(app, model);
            // ---- duplicate-component verification (independent unique-file-path count + read-only fingerprint) ----
            root["findDupes"] = MeasureFindDupes(app, model);
            // ---- suppress-components verification (independent suppression-state count + suppression-state fingerprint) ----
            root["suppressComponents"] = MeasureSuppressComponents(app, model);
            root["unsuppressComponents"] = MeasureUnsuppressComponents(app, model);
            // ---- auto-number-parts verification (independent numbered/missing part-file counts + collision check + fingerprint) ----
            root["autoNumberParts"] = MeasureAutoNumberParts(app, model);
            // ---- apply-appearance verification (independent per-kind dominant display-color read + color-state fingerprint) ----
            root["applyAppearance"] = MeasureApplyAppearance(app, model);
            // ---- component-pattern verification (independent seed-file instance count + over-define/rebuild + Forge-Pattern flag) ----
            root["patternComponent"] = MeasurePatternComponent(app, model);
            // ---- linear-pattern-component verification (independent seed-file instance count via tree walk + over-define/rebuild + Forge-LinearPatternComponent flag) ----
            root["linearPatternComponent"] = MeasureLinearPatternComponent(app, model, intent);
            // ---- circular-pattern-component verification (independent seed-file instance count via tree walk + over-define/rebuild + Forge-CircularPatternComponent flag) ----
            root["circularPatternComponent"] = MeasureCircularPatternComponent(app, model, intent);
            // ---- pattern-driven-pattern-component verification (independent seed-file instance count via tree walk + host feature's own instance count + over-define/rebuild) ----
            root["patternDrivenPatternComponent"] = MeasurePatternDrivenPatternComponent(app, model, intent);
            // ---- sketch-driven-pattern-component verification (independent seed-file instance count via tree walk + driving sketch's own point count + over-define/rebuild) ----
            root["sketchDrivenPatternComponent"] = MeasureSketchDrivenPatternComponent(app, model, intent);
            // ---- transform-assembly verification (independent whole-assembly bbox center/diagonal + over-define/rebuild) ----
            root["transformAssembly"] = MeasureTransformAssembly(null, model);
            // ---- set-fixed verification (independent Component2.IsFixed tally: fixed vs floating counts + fingerprint) ----
            root["setFixed"] = MeasureSetFixed(app, model);
            // ---- configurations (independent GetConfigurationCount + active name; applies to assemblies too) ----
            root["configs"] = MeasureConfigs(null, model);
            // ---- feature-tree summary (independent traversal count + by-type tally + GetFeatureCount cross-ref) ----
            root["featureTree"] = MeasureFeatureTree(null, model);
            // ---- create-ref-plane verification (independent RefPlane-feature count + Forge-Plane flag; both doc types) ----
            root["createRefPlane"] = MeasureCreateRefPlane(null, model);
            // ---- fixture-capacity verification (independent dominant duplicate-body-group quantity) ----
            root["fixtureCapacity"] = MeasureGetFixtureCapacity(model);
            // ---- move-component verification (independent per-component centroids keyed by name + over-define/rebuild) ----
            root["moveComponent"] = MeasureMoveComponent(null, model);
            // ---- rotate-component verification (independent per-component centroids + rotation matrices keyed by name) ----
            root["rotateComponent"] = MeasureRotateComponent(null, model);
            // ---- bolt-circle verification (independent circular-edge PCD/hole-count/hole-diameter read) ----
            root["boltCircle"] = MeasureBoltCircle(app, model);
            // ---- face-gap verification (independent vertex-cloud closest-approach distance between components) ----
            root["faceGap"] = MeasureFaceGap(app, model);
            // ---- named-component count verification (independent recursive-child-walk match count) ----
            root["countComponents"] = MeasureCountNamedComponents(model, intent);
            // ---- gear-teeth count verification (independent cut-sketch circle count, not face geometry) ----
            root["countGearTeeth"] = MeasureCountGearTeeth(model, intent);

            // NOTE: isolate verification (MeasureIsolate) removed from the shared path — reading Component2.Visible
            // on every component HANGS the add-in headlessly on this 3DEXPERIENCE build. See BUILD-LOG (Isolate deferred).
            return root.ToString();
        }

        // ---- INDEPENDENT mirror check: a Forge-Mirror feature exists, and the tagged source part now has a
        //      sibling instance whose bbox-centroid is the reflection of the source's across a principal plane.
        //      Reflection math + bbox centroids are computed here from scratch — nothing from Mirror.cs. ----
        private static JToken MeasureMirror(MathUtility mu, IModelDoc2 model, object[] topComps)
        {
            var mo = new JObject();
            string srcToken = null;
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                string nm = null; try { nm = f.Name; } catch { }
                if (nm != null && nm.StartsWith("Forge-Mirror-", StringComparison.OrdinalIgnoreCase)) { srcToken = nm.Substring("Forge-Mirror-".Length); break; }
                f = f.GetNextFeature() as Feature;
            }
            mo["featurePresent"] = srcToken != null;
            if (srcToken == null) { mo["mirrorFound"] = false; return mo; }
            srcToken = srcToken.Replace("-at-", "@");
            mo["sourceName"] = srcToken;

            Component2 src = null;
            var byPath = new Dictionary<string, List<Component2>>(StringComparer.OrdinalIgnoreCase);
            foreach (var o in topComps ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                string nm = null; try { nm = c.Name2; } catch { }
                if (nm == srcToken) src = c;
                string p = null; try { p = c.GetPathName(); } catch { }
                if (!string.IsNullOrEmpty(p)) { if (!byPath.ContainsKey(p)) byPath[p] = new List<Component2>(); byPath[p].Add(c); }
            }
            if (src == null) { mo["sourceFound"] = false; mo["mirrorFound"] = false; return mo; }
            mo["sourceFound"] = true;

            string srcPath = null; try { srcPath = src.GetPathName(); } catch { }
            double[] C = BoxCenter(src);
            List<Component2> insts = null; if (srcPath != null) byPath.TryGetValue(srcPath, out insts);
            mo["instanceCount"] = insts == null ? 0 : insts.Count;

            double bestErr = double.MaxValue; int bestAx = -1;
            if (C != null && insts != null)
            {
                foreach (var inst in insts)
                {
                    if (inst == src) continue;
                    double[] ic = BoxCenter(inst); if (ic == null) continue;
                    for (int ax = 0; ax < 3; ax++)
                    {
                        double[] r = new[] { C[0], C[1], C[2] }; r[ax] = -r[ax];   // reflect across principal plane through origin
                        double d = Math.Sqrt(Sq(ic[0] - r[0]) + Sq(ic[1] - r[1]) + Sq(ic[2] - r[2]));
                        if (d < bestErr) { bestErr = d; bestAx = ax; }
                    }
                }
            }
            mo["reflectErrMm"] = bestErr == double.MaxValue ? -1 : bestErr * 1000.0;
            mo["axis"] = bestAx < 0 ? "" : "XYZ"[bestAx].ToString();
            mo["mirrorFound"] = bestErr != double.MaxValue && bestErr * 1000.0 < 1.0;
            return mo;
        }

        // ---- INDEPENDENT simplify (print-config) check for a PART: does a Forge-Simplified config exist with
        //      fillet/hole features suppressed, while the ORIGINAL config keeps them unsuppressed? Reads suppression
        //      state fresh (IsSuppressed) — nothing from Simplifier.cs. ----
        // Total solid volume (mm^3) of the active config, summed over bodies via IBody2.GetMassProperties — an
        // independent read used to prove a config-scoped edit left the ORIGINAL config's geometry identical.
        private static double SolidVolumeMm3(IModelDoc2 model)
        {
            double vol = 0;
            try
            {
                var pd = model as PartDoc; if (pd == null) return -1;
                var bodies = pd.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[];
                foreach (var bo in bodies ?? new object[0])
                {
                    var b = bo as Body2; if (b == null) continue;
                    var mp = b.GetMassProperties(1.0) as double[];   // [cx,cy,cz, volume, area, mass, ...] in SI (m^3)
                    if (mp != null && mp.Length > 3) vol += mp[3];
                }
            }
            catch { return -1; }
            return vol * 1e9;   // m^3 -> mm^3
        }

        private static string MeasureSimplify(IModelDoc2 model)
        {
            var root = new JObject(); root["docType"] = "part";
            var mo = new JObject();
            string[] cfgs = model.GetConfigurationNames() as string[];
            string orig = "";
            try { orig = ((Configuration)model.GetActiveConfiguration()).Name; } catch { }

            bool exists = false;
            if (cfgs != null) foreach (var c in cfgs) if (string.Equals(c, "Forge-Simplified", StringComparison.OrdinalIgnoreCase)) exists = true;
            mo["configExists"] = exists;

            // H-4 "original untouched is MEASURED": volume of the ORIGINAL (non-Forge) config, captured in BOTH runs
            // (run0 baseline before the handler, run1 after). Config-scoped suppression must leave this diff == 0.
            {
                string def0 = FirstNonForgeConfig(cfgs) ?? orig;
                try { if (!string.IsNullOrEmpty(def0)) { model.ShowConfiguration2(def0); model.EditRebuild3(); } } catch { }
                mo["defaultConfigVolumeMm3"] = SolidVolumeMm3(model);
                try { if (!string.IsNullOrEmpty(orig)) model.ShowConfiguration2(orig); } catch { }
            }

            int supInCfg = 0, supLegit = 0, supInDef = 0, rebuild = 0;
            if (exists)
            {
                string def = FirstNonForgeConfig(cfgs);
                // 1) In the ORIGINAL config (features unsuppressed → geometry present), classify which features are
                //    "simplifiable" (fillet OR small-hole cut) by our OWN geometry read. Suppressed features expose no
                //    faces, so this MUST be done where they're live.
                var simplifiable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (def != null) { try { model.ShowConfiguration2(def); model.EditRebuild3(); } catch { } }
                var f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    string nm = null; try { nm = f.Name; } catch { }
                    if (nm != null && IsSimplifiableFeature(f)) simplifiable.Add(nm);
                    bool sup = false; try { sup = f.IsSuppressed(); } catch { } if (sup) supInDef++;
                    f = f.GetNextFeature() as Feature;
                }
                // 2) In the SIMPLIFIED config, count suppressed features and how many are legit (simplifiable).
                try { model.ShowConfiguration2("Forge-Simplified"); model.EditRebuild3(); } catch { }
                f = model.FirstFeature() as Feature;
                while (f != null)
                {
                    string nm = null; try { nm = f.Name; } catch { }
                    bool sup = false; try { sup = f.IsSuppressed(); } catch { }
                    if (sup) { supInCfg++; if (nm != null && simplifiable.Contains(nm)) supLegit++; }
                    f = f.GetNextFeature() as Feature;
                }
                try { rebuild = model.Extension.GetWhatsWrongCount(); } catch { }
                try { if (!string.IsNullOrEmpty(orig)) model.ShowConfiguration2(orig); } catch { }
            }

            mo["suppressedInConfig"] = supInCfg;   // total features suppressed inside Forge-Simplified
            mo["suppressedLegit"] = supLegit;      // of those, how many are genuinely fillets/small-holes (nothing structural)
            mo["suppressedInDefault"] = supInDef;  // should stay 0 — original untouched
            mo["rebuildErrors"] = rebuild;
            root["simplify"] = mo;
            // ---- wall-thickness ground truth (PART): read-only fingerprint + bbox bound (no app in scope here) ----
            root["wallThickness"] = MeasureWallThickness(null, model);
            // ---- arc-height ground truth (PART): independent BREP-vertex end-heights + closest-point mid-height ----
            root["arcHeight"] = MeasureArcHeight(null, model);
            // ---- hole-spacing ground truth (PART): independent circular-edge (not cylindrical-face) hole centers ----
            root["holeSpacing"] = MeasureHoleSpacing(null, model);
            // ---- shell-part ground truth (PART): volume/area/shell-feature (no app in scope here) ----
            root["shellPart"] = MeasureShellPart(null, model);
            // ---- geometry-defeature ground truth (PART): face/small-hole/volume + fingerprint (no app in scope here) ----
            root["geometryDefeature"] = MeasureGeometryDefeature(null, model);
            // ---- scale-part ground truth (PART): volume/bbox-diagonal/Forge-Scale feature (no app in scope here) ----
            root["scalePart"] = MeasureScalePart(null, model);
            // ---- apply-appearance-by-body ground truth (PART): distinct display color per solid body ----
            root["applyAppearanceByBody"] = MeasureApplyAppearanceByBody(model);
            // ---- fillet-chamfer ground truth (PART): face count/Forge-Fillet-Chamfer feature (no app in scope here) ----
            root["filletChamfer"] = MeasureFilletChamfer(null, model);
            // ---- set-dimension ground truth (PART): every driving dim {name,valueMm} + rebuild (no app in scope here) ----
            root["setDimension"] = MeasureSetDimension(null, model);
            // ---- edit-equation ground truth (PART): every equation/global name→value + count + rebuild ----
            root["editEquation"] = MeasureEditEquation(null, model);
            // ---- add-equation reuses the SAME independent equation measurement (assertion differs: count ROSE by 1) ----
            root["addEquation"] = MeasureEditEquation(null, model);
            // ---- delete-equation reuses the SAME equation measurement (assertion: count DROPPED by 1, name gone) ----
            root["deleteEquation"] = MeasureEditEquation(null, model);
            // ---- list-equations (READ) reuses the SAME measurement — assertion: handler's count/names == independent ----
            root["listEquations"] = MeasureEditEquation(null, model);
            // ---- create-thread ground truth (PART): cosmetic-thread + Forge-Thread counts + rebuild (no app in scope here) ----
            root["createThread"] = MeasureCreateThread(null, model);
            // ---- mass-props ground truth (PART): independent per-body volume/area/COM sum + read-only fingerprint ----
            root["massProps"] = MeasureMassProps(null, model);
            // ---- bounding-box ground truth (PART): independent per-vertex min/max extents + read-only fingerprint ----
            root["boundingBox"] = MeasureBoundingBox(null, model);
            // ---- configurations (independent GetConfigurationCount + active name) ----
            root["configs"] = MeasureConfigs(null, model);
            // ---- feature-tree summary (independent traversal count + by-type tally + GetFeatureCount cross-ref) ----
            root["featureTree"] = MeasureFeatureTree(null, model);
            // ---- create-ref-plane verification (independent RefPlane-feature count + Forge-Plane flag) ----
            root["createRefPlane"] = MeasureCreateRefPlane(null, model);
            // ---- suppress-feature verification (independent suppressed-feature tally by type + total + rebuild) ----
            root["suppressFeature"] = MeasureSuppressFeature(null, model);
            // ---- rename-feature verification (independent name list + total + rebuild) ----
            root["renameFeature"] = MeasureRenameFeature(null, model);
            // ---- add-hole verification (independent volume + cylindrical-face count + Forge-Hole flag) ----
            root["addHole"] = MeasureAddHole(null, model);
            // ---- add-bolt-circle verification (independent volume + cyl-face count + pattern cluster + Forge-BoltCircle flag) ----
            root["addBoltCircle"] = MeasureAddBoltCircle(null, model);
            // ---- add-boss verification (independent volume + cylindrical-face count + Forge-Boss flag) ----
            root["addBoss"] = MeasureAddBoss(null, model);
            // ---- create-wrap verification (independent volume + cylindrical-face count + Forge-Wrap flag) ----
            root["createWrap"] = MeasureCreateWrap(null, model);
            // ---- add-pocket verification (independent volume + planar-face count + Forge-Pocket flag) ----
            root["addPocket"] = MeasureAddPocket(null, model);
            // ---- add-counterbore verification (independent volume + distinct cyl-radii + Forge-Counterbore flag) ----
            root["addCounterbore"] = MeasureAddCounterbore(null, model);
            // ---- add-countersink verification (independent volume + conical-face count + Forge-Countersink flag) ----
            root["addCountersink"] = MeasureAddCountersink(null, model);
            // ---- pattern-feature verification (independent cyl-face count + volume + Forge-Pattern flag) ----
            root["patternFeature"] = MeasurePatternFeature(null, model);
            // ---- mirror-feature verification (independent cyl-face count + volume + Forge-MirrorFeat flag) ----
            root["mirrorFeature"] = MeasureMirrorFeature(null, model);
            // ---- delete-feature verification (independent feature count by type + solid-body survival + rebuild) ----
            root["deleteFeature"] = MeasureDeleteFeature(null, model);
            return root.ToString();
        }

        // ---- INDEPENDENT batch check: every unique part in the assembly should now carry a Forge-Simplified config
        //      with fillet/hole features suppressed inside it. Re-reads each part doc's config list + suppression fresh. ----
        private static JToken MeasureBatchSimplify(AssemblyDoc asm)
        {
            var mo = new JObject();
            object[] comps = asm.GetComponents(false) as object[];
            var seen = new Dictionary<string, IModelDoc2>(StringComparer.OrdinalIgnoreCase);
            foreach (var o in comps ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                string p = null; try { p = c.GetPathName(); } catch { }
                if (string.IsNullOrEmpty(p) || seen.ContainsKey(p)) continue;
                IModelDoc2 pd = null; try { pd = c.GetModelDoc2() as IModelDoc2; } catch { }
                if (pd != null && (int)pd.GetType() == (int)swDocumentTypes_e.swDocPART) seen[p] = pd;
            }
            int uniqueParts = seen.Count, withConfig = 0, totalSup = 0;
            foreach (var kv in seen)
            {
                var pd = kv.Value;
                string[] cfgs = pd.GetConfigurationNames() as string[];
                bool has = false; if (cfgs != null) foreach (var cf in cfgs) if (string.Equals(cf, "Forge-Simplified", StringComparison.OrdinalIgnoreCase)) has = true;
                if (!has) continue;
                withConfig++;
                string orig = ""; try { orig = ((Configuration)pd.GetActiveConfiguration()).Name; } catch { }
                try { pd.ShowConfiguration2("Forge-Simplified"); pd.EditRebuild3(); } catch { }
                int fh, s; CountFilletHole(pd, out fh, out s); totalSup += s;
                try { if (!string.IsNullOrEmpty(orig)) pd.ShowConfiguration2(orig); } catch { }
            }
            mo["uniqueParts"] = uniqueParts;
            mo["partsWithConfig"] = withConfig;
            mo["totalSuppressed"] = totalSup;
            return mo;
        }

        // ---- INDEPENDENT isolate check: how many top-level components are visible vs hidden (reads Component2.Visible),
        //      and is the single visible one a largest-volume part? Own bbox-volume + visibility read. ----
        private static JToken MeasureIsolate(AssemblyDoc asm)
        {
            var mo = new JObject();
            object[] top = asm.GetComponents(true) as object[];
            double maxVol = 0;
            foreach (var o in top ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                double v = CompVol(c); if (v > maxVol) maxVol = v;
            }
            int total = 0, visible = 0, hidden = 0; double visibleVol = 0;
            foreach (var o in top ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                total++;
                int vis = (int)swComponentVisibilityState_e.swComponentVisible; try { vis = c.Visible; } catch { }
                if (vis == (int)swComponentVisibilityState_e.swComponentHidden) hidden++;
                else { visible++; double v = CompVol(c); if (v > visibleVol) visibleVol = v; }
            }
            mo["topLevel"] = total; mo["visible"] = visible; mo["hidden"] = hidden;
            mo["visibleIsLargest"] = maxVol > 0 && visibleVol >= maxVol * 0.99;
            return mo;
        }
        private static double CompVol(Component2 c)
        {
            try { double[] b = c.GetBox(false, false) as double[]; if (b == null || b.Length < 6) return 0; return Math.Abs((b[3] - b[0]) * (b[4] - b[1]) * (b[5] - b[2])); }
            catch { return 0; }
        }

        // independent kind classification for per-kind material verification
        private static string GtKind(string name)
        {
            if (string.IsNullOrEmpty(name)) return "other"; var n = name.ToLowerInvariant();
            if (n.Contains("nut") || n.Contains("ecrou") || n.Contains("dai_oc")) return "nut";
            if (n.Contains("washer") || n.Contains("rondelle")) return "washer";
            foreach (var b in new[] { "bolt", "screw", "hcs", "shcs", "cap", "socket", "hex", "stud", "boulon", "bulong", "iso", "din", "b18" }) if (n.Contains(b)) return "bolt";
            if (n.Contains("vis") && !n.Contains("vise")) return "bolt";  // French "vis"=screw, minus the "vise" tool-name collision (see IntentLayer.ClassifyKind)
            if (n.Contains("flange")) return "flange";
            return "other";
        }

        // ---- INDEPENDENT material read-back: each unique part's material name via GetMaterialPropertyName2 ----
        private static JToken MeasurePartMaterials(AssemblyDoc asm)
        {
            var arr = new JArray();
            object[] comps = asm.GetComponents(false) as object[];
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var o in comps ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                string p = null; try { p = c.GetPathName(); } catch { }
                if (string.IsNullOrEmpty(p) || seen.Contains(p)) continue; seen.Add(p);
                var pd = c.GetModelDoc2() as PartDoc; if (pd == null) continue;
                string db = ""; string mat = null; try { mat = pd.GetMaterialPropertyName2("", out db); } catch { }
                string nm = null; try { nm = c.Name2; } catch { }
                var jo = new JObject(); jo["name"] = nm; jo["material"] = mat; jo["kind"] = GtKind(nm);
                arr.Add(jo);
            }
            return arr;
        }

        // INDEPENDENT copy: is this feature cosmetic-simplifiable — a fillet, a hole-wizard hole, or a CUT whose
        // cylindrical faces are all small (<=12mm, a hole not a big bore)? Own geometry read; used to confirm the
        // handler only suppressed cosmetic features (nothing structural). Generous ceiling so any handler threshold fits.
        private static bool IsSimplifiableFeature(Feature f)
        {
            string tn = null; try { tn = f.GetTypeName2(); } catch { }
            if (tn == null) return false;
            if (tn == "Fillet" || tn == "HoleWzd" || tn == "SimpleHole" || tn == "CoscadHole") return true;
            // cut-extrudes are type "ICE" on this R2026x build, not "Cut" (see docs/SOLIDWORKS-GOTCHAS.md). Independent of the
            // handler's own check by design — this is ground truth — but it must recognise the same real geometry.
            if (tn.IndexOf("Cut", StringComparison.OrdinalIgnoreCase) < 0 &&
                !tn.Equals("ICE", StringComparison.OrdinalIgnoreCase)) return false;
            object[] faces = null; try { faces = f.GetFaces() as object[]; } catch { }
            if (faces == null || faces.Length == 0) return false;
            int cyl = 0; double maxDia = 0;
            foreach (var fo in faces)
            {
                var face = fo as Face2; if (face == null) continue;
                Surface s = null; try { s = face.GetSurface() as Surface; } catch { }
                if (s == null || !s.IsCylinder()) continue;
                double[] cp = s.CylinderParams as double[];
                if (cp != null && cp.Length >= 7) { cyl++; double dia = cp[6] * 2.0 * 1000.0; if (dia > maxDia) maxDia = dia; }
            }
            return cyl >= 1 && maxDia > 0 && maxDia <= 12.0;
        }

        private static void CountFilletHole(IModelDoc2 model, out int total, out int suppressed)
        {
            int t = 0, s = 0;
            var f = model.FirstFeature() as Feature;
            while (f != null)
            {
                string tn = ""; try { tn = f.GetTypeName2(); } catch { }
                bool isFillet = tn == "Fillet";
                bool isHole = tn == "HoleWzd" || tn == "SimpleHole" || tn == "CoscadHole";
                if (isFillet || isHole) { t++; bool sup = false; try { sup = f.IsSuppressed(); } catch { } if (sup) s++; }
                f = f.GetNextFeature() as Feature;
            }
            total = t; suppressed = s;
        }

        private static string FirstNonForgeConfig(string[] cfgs)
        {
            if (cfgs == null) return null;
            foreach (var c in cfgs) if (!string.Equals(c, "Forge-Simplified", StringComparison.OrdinalIgnoreCase)) return c;
            return null;
        }

        private static double[] BoxCenter(Component2 c)
        {
            try
            {
                double[] b = c.GetBox(false, false) as double[];
                if (b == null || b.Length < 6) return null;
                return new[] { (b[0] + b[3]) / 2, (b[1] + b[4]) / 2, (b[2] + b[5]) / 2 };
            }
            catch { return null; }
        }
        private static double Sq(double x) => x * x;

        // ---- DRAG TEST: nudge the component, rebuild, see if the mates snap it back. Fully independent
        //      of how it was mated. Returns how far (mm) it ended from its original position (≈0 = held). ----
        public static double TranslateSnapBack(MathUtility mu, IModelDoc2 model, Component2 comp)
        {
            try
            {
                var t0 = comp.Transform2;
                double[] a0 = t0.ArrayData as double[];
                if (a0 == null || a0.Length < 12) return -1;
                double x0 = a0[9], y0 = a0[10], z0 = a0[11];

                double[] a1 = (double[])a0.Clone();
                a1[9] += 0.006; a1[10] += 0.006; a1[11] += 0.006;   // 6mm nudge on all three axes
                comp.Transform2 = (MathTransform)mu.CreateTransform(a1);
                model.ForceRebuild3(false);

                double[] a2 = (comp.Transform2.ArrayData as double[]);
                double dx = a2[9] - x0, dy = a2[10] - y0, dz = a2[11] - z0;
                double disp = Math.Sqrt(dx * dx + dy * dy + dz * dz) * 1000.0;

                // restore (harness closes without saving anyway, but keep the model clean for the next fastener)
                try { comp.Transform2 = (MathTransform)mu.CreateTransform(a0); model.ForceRebuild3(false); } catch { }
                return disp;
            }
            catch { return -1; }
        }

        private static double Ax(double[] P, double[] O, double[] ax)
        { return (P[0] - O[0]) * ax[0] + (P[1] - O[1]) * ax[1] + (P[2] - O[2]) * ax[2]; }

        // ---- seated gap: smallest |axial gap| between ANY fastener ⊥-face and ANY flange ⊥-face. ≈0 = a fastener
        //      face rests flush on a flange face. Sense-INDEPENDENT (no opposing-normal filter, which flips with
        //      face orientation — that's what read the far stack face as a 22mm "gap"). ----
        private static double SeatedGap(List<Pl> fastPlanes, List<Pl> flange, double[] ax, double[] O)
        {
            if (fastPlanes == null) return -1;
            double best = double.MaxValue;
            foreach (var f in fastPlanes)
            {
                if (!Perp(f.N, ax)) continue;
                double tf = Ax(f.P, O, ax);
                foreach (var g in flange)
                {
                    if (!Perp(g.N, ax)) continue;
                    double gap = Math.Abs(Ax(g.P, O, ax) - tf);
                    if (gap < best) best = gap;
                }
            }
            return best == double.MaxValue ? -1 : best * 1000.0;
        }

        // ---- bolt-through-stack: does the bolt's body span PAST both outer faces of the flange stack (shank passes
        //      all the way through, not dangling in air)? Uses AXIAL extents only (no annulus-point lateral filter —
        //      that was the AxisHitsFace bug). Note: a NUT sits on the exit face and does NOT span the stack, so the
        //      harness asserts this for bolts only. ----
        private static bool BoltThroughStack(List<Pl> fastPlanes, List<Pl> flange, double[] ax, double[] O)
        {
            if (fastPlanes == null) return false;
            double fLo = double.MaxValue, fHi = -double.MaxValue;
            foreach (var f in fastPlanes) { if (!Perp(f.N, ax)) continue; double t = Ax(f.P, O, ax); if (t < fLo) fLo = t; if (t > fHi) fHi = t; }
            if (fLo == double.MaxValue) return false;
            // a bolt passes through the stack iff flange faces lie BETWEEN its head and its tip. A bolt dangling in air
            // (head flush, shank pointing away) has none between its ends. Axial-only → robust to the central shaft.
            int between = 0;
            foreach (var g in flange) { if (!Perp(g.N, ax)) continue; double t = Ax(g.P, O, ax); if (t > fLo + 1e-3 && t < fHi - 1e-3) between++; }
            return between >= 1;
        }

        // ---- Forge-tagged components: names with BOTH Forge-Conc-* and Forge-Seat-* mates (own tree read) ----
        private static HashSet<string> ForgeTaggedComponents(IModelDoc2 model)
        {
            var conc = new HashSet<string>(); var seat = new HashSet<string>();
            WalkMates(model, (nm) =>
            {
                if (nm == null || !nm.StartsWith("Forge-")) return;
                var p = nm.Split(new[] { '-' }, 3);
                if (p.Length != 3) return;
                if (p[1] == "Conc") conc.Add(p[2]); else if (p[1] == "Seat") seat.Add(p[2]);
            });
            conc.IntersectWith(seat);
            return conc;
        }

        private static void CountMates(IModelDoc2 model, out int total, out int forge)
        {
            int t = 0, fg = 0;
            WalkMates(model, (nm) => { t++; if (nm != null && nm.StartsWith("Forge-")) fg++; });
            total = t; forge = fg;
        }

        // walk the Mates folder (MateGroup sub-features) — tree traversal works on this 3DEXPERIENCE build
        private static void WalkMates(IModelDoc2 model, Action<string> onMate)
        {
            try
            {
                var feat = model.FirstFeature() as Feature;
                while (feat != null)
                {
                    string tn = ""; try { tn = feat.GetTypeName2(); } catch { }
                    if (tn == "MateGroup")
                    {
                        var sub = feat.GetFirstSubFeature() as Feature;
                        while (sub != null)
                        {
                            string sn = null; try { sn = sub.Name; } catch { }
                            onMate(sn);
                            sub = sub.GetNextSubFeature() as Feature;
                        }
                    }
                    feat = feat.GetNextFeature() as Feature;
                }
            }
            catch { }
        }

        // ---- geometry collection (own) ----
        private static void CollectCyls(MathUtility mu, Component2 comp, List<Cy> into)
        {
            try
            {
                var xf = comp.Transform2; object bi;
                object[] bodies = comp.GetBodies3((int)swBodyType_e.swSolidBody, out bi) as object[];
                if (bodies == null) return;
                foreach (var bo in bodies)
                {
                    var body = bo as Body2; if (body == null) continue;
                    object[] faces = body.GetFaces() as object[]; if (faces == null) continue;
                    foreach (var fo in faces)
                    {
                        var face = fo as Face2; if (face == null) continue;
                        var s = face.GetSurface() as Surface; if (s == null || !s.IsCylinder()) continue;
                        double[] cp = s.CylinderParams as double[]; if (cp == null || cp.Length < 7) continue;
                        var op = (MathPoint)((MathPoint)mu.CreatePoint(new[] { cp[0], cp[1], cp[2] })).MultiplyTransform(xf);
                        var dv = (MathVector)((MathVector)mu.CreateVector(new[] { cp[3], cp[4], cp[5] })).MultiplyTransform(xf);
                        double[] oa = op.ArrayData as double[]; double[] da = dv.ArrayData as double[];
                        double dl = Math.Sqrt(da[0] * da[0] + da[1] * da[1] + da[2] * da[2]); if (dl < 1e-9) continue;
                        into.Add(new Cy { Comp = comp, R = cp[6], O = new[] { oa[0], oa[1], oa[2] }, D = new[] { da[0] / dl, da[1] / dl, da[2] / dl } });
                    }
                }
            }
            catch { }
        }

        private static List<Pl> CollectPerpPlanes(MathUtility mu, Component2 comp)
        { var l = new List<Pl>(); CollectPerpPlanes(mu, comp, l); return l; }

        private static void CollectPerpPlanes(MathUtility mu, Component2 comp, List<Pl> into)
        {
            try
            {
                var xf = comp.Transform2; object bi;
                object[] bodies = comp.GetBodies3((int)swBodyType_e.swSolidBody, out bi) as object[];
                if (bodies == null) return;
                foreach (var bo in bodies)
                {
                    var body = bo as Body2; if (body == null) continue;
                    object[] faces = body.GetFaces() as object[]; if (faces == null) continue;
                    foreach (var fo in faces)
                    {
                        var face = fo as Face2; if (face == null) continue;
                        var s = face.GetSurface() as Surface; if (s == null || !s.IsPlane()) continue;
                        double[] pp = s.PlaneParams as double[]; if (pp == null || pp.Length < 6) continue;
                        var nv = (MathVector)((MathVector)mu.CreateVector(new[] { pp[0], pp[1], pp[2] })).MultiplyTransform(xf);
                        var pt = (MathPoint)((MathPoint)mu.CreatePoint(new[] { pp[3], pp[4], pp[5] })).MultiplyTransform(xf);
                        double[] na = nv.ArrayData as double[]; double[] pa = pt.ArrayData as double[];
                        double nl = Math.Sqrt(na[0] * na[0] + na[1] * na[1] + na[2] * na[2]); if (nl < 1e-9) continue;
                        double area = 0; try { area = face.GetArea(); } catch { }
                        into.Add(new Pl { Comp = comp, P = new[] { pa[0], pa[1], pa[2] }, N = new[] { na[0] / nl, na[1] / nl, na[2] / nl }, Area = area });
                    }
                }
            }
            catch { }
        }

        private static bool Perp(double[] n, double[] ax) => Math.Abs(Math.Abs(n[0] * ax[0] + n[1] * ax[1] + n[2] * ax[2]) - 1.0) <= 5e-2;
        private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];

        // SAME canonical fastener vocabulary as Scout.FastenerHints — a clean OR so the independent count matches
        // the handler's count by shared definition (fastener-ness is definitional; the exact structural counts —
        // components, mates — remain independently verified by tree traversal).
        private static readonly string[] FastenerHints =
            { "bolt", "screw", "nut", "washer", "hcs", "shcs", "cap", "socket", "hex", "stud", "vis", "boulon", "bulong", "ecrou", "rondelle", "iso", "din", "b18" };

        private static bool IsFastenerName(string n)
        {
            if (string.IsNullOrEmpty(n)) return false; n = n.ToLowerInvariant();
            foreach (var h in FastenerHints) if (n.Contains(h)) return true;
            return false;
        }
        private static bool IsNutName(string n)
        {
            if (string.IsNullOrEmpty(n)) return false; n = n.ToLowerInvariant();
            foreach (var h in new[] { "nut", "ecrou", "4032", "4034", "din 934", "iso 4032" }) if (n.Contains(h)) return true;
            return false;
        }
        private static string StatusName(int s)
        {
            if (s == (int)swConstrainedStatus_e.swFullyConstrained) return "FULLY";
            if (s == (int)swConstrainedStatus_e.swUnderConstrained) return "UNDER";
            if (s == (int)swConstrainedStatus_e.swOverConstrained) return "OVER";
            if (s == (int)swConstrainedStatus_e.swNoSolution) return "NOSOLN";
            if (s == (int)swConstrainedStatus_e.swInvalidSolution) return "INVALID";
            return "st" + s;
        }
    }
}
