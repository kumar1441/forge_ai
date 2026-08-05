# SolidWorks API — Gotchas

> Notes from the maintainer. Every entry here cost real debugging time on a
> **3DEXPERIENCE SOLIDWORKS Design R2026x** build. Read this before writing any
> SolidWorks geometry / mate / read code — it is the reference companion to the add-in
> in `solidworks/`.

## Build & register the add-in

From the `solidworks/` folder:

```powershell
dotnet build Forge.SolidWorks.csproj -c Release
```

If the build can't find `SolidWorks.Interop.*`, fix the `<HintPath>` lines in
`Forge.SolidWorks.csproj` to point at your install's
`...\SOLIDWORKS Corp\SOLIDWORKS\api\redist\` folder.

Register as an Administrator, from `bin\x64\Release\`:

```powershell
& "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe" /codebase Forge.SolidWorks.dll
```

You should see `Types registered successfully`. `/codebase` tells .NET where the DLL
lives. Uninstall later with the same command plus `/unregister`.

## The rule that predicts everything

On this build the live in-model feature ops all **create geometry from a sketch or edge**
(extrude, cut/ICE, revolve, sweep, loft, helix, fillet, chamfer, linear pattern, sketch
mirror). The dead ones all **modify or consume existing solid geometry** (move face,
dome, combine bodies, wrap, move/copy body, multi-face draft, thicken, split-body commit,
library feature, sew/knit to solid). Before building any body-modifying write, instrument
the raw return AND an independent geometry re-read — don't theorise from a single null.

Exceptions exist and matter:

- `InsertNetBlend2` (Boundary Boss/Base) returns NULL even on a successful commit — on a
  null return, fall back to `FeatureByPositionReverse(0)` and accept the result if its
  type isn't `ProfileFeature`.
- `HoleWizard5` no-ops even though it "creates from a sketch point" — likely a multi-step
  wizard state machine, not a one-shot call. Use the plain cut-extrude circle path for
  holes.
- A handful of real multi-body parts make otherwise-live ops misbehave: `InsertScale`
  and `FeatureCut4` can return NULL (or commit a geometrically no-op cut) specifically on
  parts with many thin, irregular bodies. Treat that as a capability gap, not a code bug.

## Reads that work (verified)

- Dimensions: traverse the feature tree via `GetFirstDisplayDimension` /
  `GetNextDisplayDimension` → `GetSystemValue3()`. `Dimension.FullName` carries the
  owning document as its last `@`-segment (`"D1@Boss-Extrude1@rev-plate-v1.Part"`) —
  strip the last segment before matching dimensions across documents.
- Rebuild errors: `IModelDocExtension.GetWhatsWrong(out features, out codes, out warnings)`
  returns three parallel arrays (feature names, `swFeatureError_e` ints, warning bools);
  `IFeature.GetErrorCode2(out isWarning)` works per-feature and is a genuinely independent
  count. Both enumerated the same flagged features as `GetWhatsWrongCount()`.
- `FeatureStatistics` works on a PART doc but returns NULL on an ASSEMBLY.
  `FeatureTypes` are INTEGER type codes — resolve real names via the feature's own
  `GetTypeName2()`.
- Density: `IMassProperty.Density` (returns 2700 for 6061 aluminium). Do NOT derive it as
  `Mass ÷ Volume` — `Mass` does not apply material density on this build (the ratio
  returns ~1). `UseSystemUnits = true` for SI.
- `IBody2.GetMassProperties(0)` on a SOLID body: `[3]=volume`, `[4]=surface area`.
  On a SHEET (non-solid) body the layout shifts: `[3]=area`, `[4]=total boundary-edge
  length`. Verify the raw array on a known-dimension fixture per body type.
- Body booleans work on TEMP bodies: `Body2.Copy()` + `Body2.Operations2(SWBODYINTERSECT)`
  computes overlap volume without touching the model (`err=1067` = computed-zero "no
  overlap", a valid answer, not a failure). Copy both operands first.
- `IBody2.ApplyTransform(MathTransform)` accepts an IMPROPER (reflection) transform built
  via `IMathUtility.CreateTransform(double[16])` — usable for mirror/symmetry checks on
  temp copies.
- Mate inventory: `IComponent2.GetMates()` returns null and `AssemblyDoc.GetMates` throws,
  but feature-tree traversal of the Mates folder works
  (`FirstFeature`/`GetNextFeature` + `GetFirstSubFeature`/`GetNextSubFeature`).
- Drawing dimensions: `IView.GetDisplayDimensions()` gives name, value, position, type,
  tolerance. On this build `IsDangling` is a METHOD on `IAnnotation`
  (`DisplayDimension.GetAnnotation().IsDangling()`), not a property on
  `IDisplayDimension`.

## Dead reads — do not call them

- `Component2.GetBox` returns NULL — use `Transform2` for position (translation slots
  `[9,10,11]` of `ArrayData`), or transform a `MathPoint` by `Transform2`. For size, read
  the underlying part's body box and transform it.
- `IModelDoc2`/`IComponent2` "missing reference" state: after a missing file, the document
  looks healthy by design — SolidWorks quietly suppresses/auto-resolves. The ONLY honest
  signal is `OpenDoc6`'s open-time error/warning returns (`openErrors`), or a disk
  `File.Exists` check. Never hunt post-open constraint flags.
- Per-config dimension VALUES: `GetSystemValue2(config)`/`GetSystemValue3(swAllConfiguration)`
  return 0 for every INACTIVE config of a still-shared dimension. To read a value per
  config, activate each config (`ShowConfiguration2` → rebuild → read `SystemValue`), then
  restore the original active config. (Per-config SUPPRESSION via
  `IsSuppressed2(swSpecifyConfiguration,{name})` does read inactive configs correctly —
  this quirk is specific to dimension value getters.)

## Return codes & enums — never hardcode from memory

- `swAddMateError_NoError = 1`, NOT 0. Checking `== 0` after `AddMate5` discards good
  mates. `code=5` = over-defined — the mate IS still created; capture the returned object
  and delete it. `code=0` = generic reject, nothing created.
- `swSuppressionChangeOk = 2` (0=bad component, 1=bad state, 2=ok, 3=failed) — "ok" is 2.
- `swDimensionDrivenState_e` is inverted from the usual assumption:
  `Unknown=0, Driven=1, Driving=2`.
- `FeatureFillet3` returns NULL unless `Options` includes `swFeatureFilletUniformRadius`
  (`swFeatureFilletUniformRadius | swFeatureFilletPropagate`). Also: filleting ALL 12
  edges of a box fails at the 8 corners — a real simple-fillet limit; fillet a subset.
- `SketchTrim` returns FALSE even on a successful trim — judge success by geometry (the
  non-construction length actually dropped), never the bool.
- COM array setters want a real typed array (SAFEARRAY of I4): `new int[]{2}`, NOT a
  boxed `new object[]{2}`. A boxed VARIANT array is silently ignored even though
  `ModifyDefinition` returns True. Verify with an immediate read-back before concluding an
  API is dead.
- To dump any enum rather than trust memory:
  `[Reflection.Assembly]::LoadFrom('solidworks/bin/Release/SolidWorks.Interop.swconst.dll')`
  then `[Enum]::GetNames($t)` / `[int][Enum]::Parse(...)`.

## Writes that silently no-op on this build

All of these return without throwing and commit nothing. When you must implement one,
attempt it, then fail closed with an honest reason after an independent geometry check —
never claim success on the API's word.

- `IEquationMgr.Add` / `Add2` / `Add3` — all return -1, every insert index, every string
  format (`"n" = v`, `"n"=v`, bare), every config scope. Reads work; writes are dead.
  Author driving globals by hand via Tools > Equations.
- `InsertMoveFace` / `InsertMoveFace2` / `InsertMoveFace3` — all return null; volume
  unchanged.
- `FullyDefineSketch` — returns `swAutodimStatusSuccess(0)` but never leaves
  `swUnderConstrained(2)`; the sketch-edit machinery commits nothing.
- `ConvertEntities` — projecting a solid-body edge into an active sketch adds nothing.
- `InsertDome` — void, pre-selected face selects fine, yet no Dome feature, volume
  unchanged. (Re-resolve geometry refs FRESH after any rebuild inside a sweep — a stale
  `Face2` makes `Select4` return false and masquerades as a rejection.)
- `InsertCombineFeature` — returns null, bodies unchanged, all tool-argument variants.
- `InsertWrapFeature2` — returns null even with `SelectData.Mark` disambiguation.
- `InsertMoveCopyBody2` — returns null, body count/volume unchanged.
- `InsertMultiFaceDraft` — returns null, volume unchanged, 0 rebuild errors.
- `FeatureBossThicken` — returns null; the `FeatureByPositionReverse(0)` fallback finds
  nothing new.
- `PostSplitBody` — returns null across all marking variants (candidates passed through,
  re-selected, or null = "mark all"). NOTE: `PreSplitBody` itself is LIVE and returns the
  candidate bodies. Also: select a BODY via `Body2.Select2(true, SelectData)` directly —
  `((Entity)body).Select4` throws `InvalidCastException`/`E_NOINTERFACE` because `Body2`
  does not implement `IEntity` on this build (unlike `Face2`/`Edge`).
- `InsertLibraryFeature(string)` — void, relies on current selection; feature count
  unchanged. (The design-library File Locations preference is also empty on an unattended
  launch.)
- `InsertDwgOrDxfFile` / `InsertDwgOrDxfFile2` — null on a plane selection, an active
  sketch, or with `IImportDxfDwgData.ImportMethod` set to `swImportDxfDwg_ImportToPartSketch`.
- `AutoBalloon5` / `AutoBalloon` — return null (honest) after successful activation and a
  valid options object.
- `IView.AutoInsertCenterMarks` — returns `true` while adding nothing (a LIE); verify by
  counting marks before/after.
- `Component2.Name2` setter — silently no-ops. Rename the tree Feature whose
  `GetSpecificFeature2()` IS the component instead (`feature.Name` sticks).
- `Component2.SetSuppression2(swComponentLightweight)` — returns `swSuppressionChangeOk(2)`
  but nothing changes. The working route is the UI's own: `Select4` the components then
  `IAssemblyDoc.MakeLightWeight()`.
- `IComponent2.IsSuppressed()` returns TRUE for a LIGHTWEIGHT component — it is NOT a
  suppression test. The only honest one is `GetSuppression2() == swComponentSuppressed(0)`.
- `AddMate5` with `swMateWIDTH` — dead headless for every selection-mark/order scheme,
  proven by a control coincident mate on the same two faces succeeding. Every other mate
  type works via the same call path.
- `IFeatureManager.InsertScale` — live on single-body parts (volume/bbox land on
  factor³/factor) but returns NULL on multi-body parts; after a NULL on a multi-body part
  the bodies can be left MUTATED in a way `CreateMassProperty()` (whole-doc) cannot see —
  measure per-body (`IBody2.GetMassProperties`) instead.
- `FeatureCut4` (Through All) — needs the owning body explicitly selected for feature
  scope on multi-body parts, and even then can commit a no-op cut.
- `EditDeleteFace(DeleteAndPatch)` — consistently declines on every attempt (selection
  succeeds, the op silently refuses).
- Reopening a SolidWorks-self-exported neutral file (STEP/IGES/Parasolid) headless is
  dead: `OpenDoc6` returns `errs=2097152` (`swFileRequiresRepairError`) and `OpenDoc7`
  fails with `spec.Error=16777216` (`swConnectedIsOffline`) on a fully local file.
  `swDocIMPORTED_PART(6)` is a classification, NOT a valid open-time type.
  **`ISldWorks.LoadFile4(path, arg, importData, ref errs)` IS live** — the direct
  translator entry point. Gotcha: never name a materialized import with the bare source
  basename (SolidWorks identifies docs by TITLE and it collides with the open anchor);
  always suffix it (`<name>-imported.SLDPRT`).
- FeatureWorks (`IFeatureWorksApp`, dumb-solid recognition) is not COM-registered on a
  "Design" SKU — `REGDB_E_CLASSNOTREG`, an install/license gap, not an API bug.
- `IExplodeStep.SetComponents(Object)` HANGS headlessly (no exception, watchdog-only
  timeout). Adding an orphan to an existing step: add a BRAND NEW step via the ordinary
  selection-driven `IAddExplodeStep` path instead. `AutoExplode()` re-run is a one-shot
  false-success (returns true, creates zero steps).
- `InsertSewRefSurface` (Knit) — returns null across every variant even with zero-gap
  surfaces and confirmed selection.
- `InsertCosmeticWeldBead2` — a failed call tears down the COM proxies for selections made
  just before it; any retry reusing those selections throws
  `COMException 0x80010108 RPC_E_DISCONNECTED`. Re-select FRESH immediately before each
  independent attempt.

## Preference & session gotchas

- The DOCUMENT scope is a silent no-op for `swPerformanceVerifyOnRebuild`;
  `ISldWorks.SetUserPreferenceToggle` (app scope) is the live route. Never OR the two
  scopes into one verdict — a "set OFF" read falls through to the app scope where the ON
  actually lives. Write the proven scope first, and count a route only if what it wrote
  MOVED. (Unit/decimal setters via `SetUserPreferenceInteger` are a different, working
  family — read each preference back.)
- An application-scoped SolidWorks preference does NOT survive a full SLDWORKS restart.
  Arrange a precondition and assert on it in the same session.
- COM disconnect `0x80010108` — re-acquire references after rebuilds, release COM objects
  per iteration, and prefer batch operations with ONE `ForceRebuild3` at the end over
  rebuilding mid-loop.
- Application-scoped dialog/rebuild freezing: headless automation can hang on SW's modal
  "What's Wrong" prompt unless `swShowErrorsEveryRebuild` is turned off first. A
  component-suppress ripple into a cross-subassembly reference can hang the same way (no
  confirmed preference constant — dump/confirm interactively before hardcoding one).

## Model & geometry reading landmines

- `GetClosestPointOn` operates in the face's PART-LOCAL frame. Transform assembly-space
  points into the component's frame (`Component2.Transform2.Inverse()`) first or the
  distances are garbage.
- `Face2.GetClosestPointOn` respects the TRIMMED face boundary — a box-centroid sample on
  a non-rectangular face can snap to a boundary edge. Prefer an interior-point scheme
  (box centre + inset candidates) over a single centroid sample.
- `GetTessTriangles` can return spurious vertices tens of metres outside the body's own
  `GetBodyBox`, and some bodies return nothing usable at all. Prefer topology-anchored
  reads (`GetClosestPointOn`, `GetBox`, `GetVertices`) over display-mesh reads; when
  tessellation is used, sanity-filter samples against the body's bbox.
- Feature TYPE NAMES on this build are not the ones you'd guess: a cut-extrude reports
  `"ICE"`, not `"Cut"`. Dump `GetTypeName2()` before matching on type. The
  `FirstFeature`/`GetNextFeature` walk also lists ELEVEN empty container folders
  (`CommentsFolder`, `EqnFolder`, `MaterialFolder`, ...) — skip every `*Folder` type.
  Confirmed working: `ProfileFeature` (sketch), `Extrusion` (boss), `ICE` (cut), `LPattern`.
- A feature FOLDER is a flat start/END-TAG RANGE, not a parent: members appear between
  `Name|FtrFolder` and `Name___EndTag___|FtrFolder` in the flat walk, and
  `GetFirstSubFeature()` on the folder returns NOTHING. Authoritative membership is
  `folder.GetSpecificFeature2() as IFeatureFolder → GetFeatures()`. Grouping non-adjacent
  features pulls in everything between them; a consumed sketch is inlined as a flat
  sibling and appears inside the range without being a member.

## Mating best practices (bolts & nuts)

- `swAddMateError_NoError = 1`, not 0 (see return-code section). Over-defined mates are
  still created — capture and delete them.
- Reference chain for a bolt+nut stack: bolt shank ↔ flange HOLE concentric (never
  bolt↔nut — that leaves no radial anchor), bolt bearing ↔ stack face seat, nut bore ↔
  bolt shank concentric, nut bearing ↔ OPPOSITE stack face seat.
- Per-fastener ATOMIC rollback: concentric + seat succeed together or roll back together;
  a lone concentric lets parts slide/sink. Roll back per fastener, never the whole run.
- Concentric mates flip freely — decide alignment from geometry (head end faces the seat
  face), verify post-rebuild with `dot(shankDir, seatFaceNormal) < -0.9` plus the
  shank-midpoint inside the stack, and self-correct with a max-1 flip retry (delete BOTH
  mates, re-add flipped, rebuild BEFORE re-measuring). For cylinder↔cylinder the aligned/
  anti flag is a no-op — the SEAT coincident's alignment flips the bolt end-for-end.
- Identify the head end by RADIAL EXTENT (hex-head max radius > shank radius), never by
  face normals. Bearing = the annular face around the shank; `rout = sqrt(area/π + rin²)`.
- Symmetric nuts: measure the MATED face (nearest the seat), not "the bearing face".
- Pattern instances: mate the SEEDS only; instances are pattern-driven. Mating instances
  individually over-defines.
- Never filter/measure a floating component's CURRENT position pre-solve — reference the
  paired hole's geometry; gap measurements are only meaningful post-rebuild.
- A bolt hole's axis passes through VOID — the seating face is an annulus around it. Test
  distance-from-axis within `(hole_r, ~2.5×hole_r)`, never "axis intersects the face".
- Track every created mate object regardless of return code so rollback can reach them.

## Natural-language parsing pitfalls

- Scale factors: a regex that only matches `bigger/larger/increase/enlarge/grow` will
  silently read "scaled up by 20%" as an ABSOLUTE 0.2 (shrink to a fifth) instead of a
  RELATIVE 1.2. Cover relative phrasing explicitly and regression-test it.
- "Set `<dim>` to `<number>` ... in config X" is a per-config dimension edit — a config-
  switch action must exclude `\bto\s+\d` or it steals the intent (a config switch targets
  a NAME, never a numeric value).
- Dimension names in commands must be matched against the LIVE list (fuzzy), never
  assumed to exist; zero or multiple matches is an ambiguity question, never a guess.
- Equation string format for setting a global: `'"name" = value'` (quotes around the
  name, spaces around `=`). Set via `eqMgr.Equation(i)` + one `ForceRebuild3`.
