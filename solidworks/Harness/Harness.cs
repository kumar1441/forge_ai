using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    /// <summary>
    /// In-process test harness. Triggered by a request file the external orchestrator (run-harness.ps1)
    /// drops; a poll timer in ForgePanel claims it and calls RunFromRequest. Everything here runs INSIDE
    /// the add-in (the only place COM works on this 3DEXPERIENCE build): open the assembly, run the handler
    /// pipeline, then INDEPENDENTLY measure ground truth (GroundTruth.cs — shares nothing with the pipeline),
    /// run the handler a second time for idempotency, screenshot, write result JSON, close WITHOUT saving.
    ///
    /// Request JSON:  { "assemblyPath", "handlerIntent", "resultPath", "screenshotPath" }
    /// </summary>
    public static class Harness
    {
        // Delete orphaned "~$" SolidWorks lock files in a model's folder. A crash (WHEA, add-in AV, kill) leaves
        // these 6-byte lock files behind; on the NEXT open SolidWorks thinks the file is open elsewhere and FREEZES
        // or opens read-only (which then fails writes/exports). This is what repeatedly bit the demo recording and
        // would kill the 24/7 test-loop within an hour (it crashes+reopens constantly). SAFE: only delete a lock whose
        // real file is NOT currently open in this SolidWorks — so a legitimately-open document keeps its lock.
        private static void CleanOrphanLocks(ISldWorks app, string modelPath)
        {
            try
            {
                string dir = Path.GetDirectoryName(modelPath);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

                var open = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    var docs = app.GetDocuments() as object[];
                    if (docs != null)
                        foreach (var o in docs)
                        {
                            string p = null; try { p = (o as IModelDoc2)?.GetPathName(); } catch { }
                            if (!string.IsNullOrEmpty(p)) open.Add(Path.GetFileName(p));
                        }
                }
                catch { }

                foreach (var lockFile in Directory.GetFiles(dir, "~$*"))
                {
                    string real = Path.GetFileName(lockFile);
                    if (real.StartsWith("~$")) real = real.Substring(2);
                    if (open.Contains(real)) continue;   // SW actually has it open — leave the lock alone
                    try { File.Delete(lockFile); } catch { }
                }
            }
            catch { }
        }

        public static async Task RunFromRequest(ISldWorks app, string requestPath)
        {
            string resultPath = null;
            var res = new JObject();
            res["startedUtc"] = DateTime.UtcNow.ToString("o");
            IModelDoc2 model = null;
            string title = null;
            try
            {
                var req = JObject.Parse(File.ReadAllText(requestPath));

                // ---- INSPECT: open an assembly and report its FAILURE STATE only. Used by fixture generators that
                //      break a model with a FILE operation (rename/move a referenced part) and must prove SolidWorks
                //      actually reports the breakage before the fixture is trusted. Reports unresolved components
                //      (in the tree but the file can't be found — distinct from suppressed) and dangling mates. ----
                var inspectSpec = req["inspect"] as JObject;
                if (inspectSpec != null)
                {
                    resultPath = (string)req["resultPath"];
                    string aPath = (string)inspectSpec["assemblyPath"];
                    // Universal PEEK: doc-type by extension, ONE open, report open status + type facts, close. Triage only —
                    // NOT a validation. A model is only "good" once a real handler runs GREEN against it.
                    int dt = aPath.EndsWith(".sldprt", StringComparison.OrdinalIgnoreCase) ? (int)swDocumentTypes_e.swDocPART
                                : aPath.EndsWith(".slddrw", StringComparison.OrdinalIgnoreCase) ? (int)swDocumentTypes_e.swDocDRAWING
                                : (int)swDocumentTypes_e.swDocASSEMBLY;
                    int ie = 0, iw = 0;
                    var m = app.OpenDoc6(aPath, dt,
                                (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref ie, ref iw) as IModelDoc2;
                    res["openErrors"] = ie; res["openWarnings"] = iw; res["docType"] = dt;
                    if (m == null) { res["error"] = "could not open " + aPath; res["ok"] = false; return; }
                    try { m.ForceRebuild3(false); } catch { }
                    int rebuildErrors = 0; try { rebuildErrors = m.Extension.GetWhatsWrongCount(); } catch { }
                    res["rebuildErrors"] = rebuildErrors;
                    int feats = 0; try { var f = m.FirstFeature() as Feature; while (f != null) { feats++; f = f.GetNextFeature() as Feature; } } catch { }
                    res["featureCount"] = feats;
                    var ad = m as AssemblyDoc;
                    if (ad != null)
                    {
                        int unresolved = 0, suppressed = 0, total = 0, missingFile = 0;
                        foreach (var o in (ad.GetComponents(true) as object[]) ?? new object[0])
                        {
                            var c = o as Component2; if (c == null) continue; total++;
                            // missingFile is independent of suppression state: on this build, opening an assembly
                            // with a genuinely absent referenced file auto-SUPPRESSES that component (not left
                            // "unresolved-but-present" as an older build's behavior was) — File.Exists on the
                            // STILL-RETAINED stored path is the one signal that survives either way.
                            string cp = null; try { cp = c.GetPathName(); } catch { }
                            if (!string.IsNullOrEmpty(cp)) { bool exists = false; try { exists = System.IO.File.Exists(cp); } catch { } if (!exists) missingFile++; }
                            bool sup = false; try { sup = c.IsSuppressed(); } catch { }
                            if (sup) { suppressed++; continue; }
                            bool gone = false;
                            try { gone = c.GetModelDoc2() == null; } catch { gone = true; }
                            if (gone) unresolved++;
                        }
                        res["totalComponents"] = total; res["unresolvedComponents"] = unresolved; res["suppressedComponents"] = suppressed; res["missingFileComponents"] = missingFile;
                    }
                    var pd = m as PartDoc;
                    if (pd != null)
                    {
                        int solids = 0, surfaces = 0;
                        try { var b = pd.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[]; solids = b?.Length ?? 0; } catch { }
                        try { var b = pd.GetBodies2((int)swBodyType_e.swSheetBody, false) as object[]; surfaces = b?.Length ?? 0; } catch { }
                        res["solidBodies"] = solids; res["surfaceBodies"] = surfaces;
                        bool sheetMetal = false;
                        try { var f = m.FirstFeature() as Feature; while (f != null) { var tn = f.GetTypeName2(); if (tn != null && (tn.IndexOf("SheetMetal", StringComparison.OrdinalIgnoreCase) >= 0 || tn == "SMBaseFlange" || tn.StartsWith("SM"))) { sheetMetal = true; break; } f = f.GetNextFeature() as Feature; } } catch { }
                        res["isSheetMetal"] = sheetMetal;
                    }
                    if (dt == (int)swDocumentTypes_e.swDocDRAWING)
                    {
                        int views = 0, sheets = 0;
                        try { var dd = m as DrawingDoc; var names = dd.GetSheetNames() as string[]; sheets = names?.Length ?? 0;
                              var vs = dd.GetViews() as object[];
                              if (vs != null) foreach (var sv in vs) { var arr = sv as object[]; if (arr != null) views += arr.Length; } } catch { }
                        res["drawingSheets"] = sheets; res["drawingViews"] = views;
                        // missingViewModels: same File.Exists-on-stored-name signal the assembly branch uses above,
                        // needed by gen-*-fixture scripts to prove a renamed/moved model actually breaks a view's
                        // reference before repair_missing_references' sibling (update_sheet_references) trusts it.
                        int missingViewModels = 0;
                        try
                        {
                            var dd2 = m as DrawingDoc;
                            string drwDir = null; try { drwDir = System.IO.Path.GetDirectoryName(aPath); } catch { }
                            var vw = dd2.GetFirstView() as IView; bool firstV = true;
                            while (vw != null)
                            {
                                if (!firstV)
                                {
                                    string rm = null; try { rm = vw.GetReferencedModelName(); } catch { }
                                    if (!string.IsNullOrEmpty(rm))
                                    {
                                        string resolved = rm;
                                        try { if (!System.IO.Path.IsPathRooted(resolved) && drwDir != null) resolved = System.IO.Path.Combine(drwDir, resolved); } catch { }
                                        bool ex = false; try { ex = System.IO.File.Exists(resolved); } catch { }
                                        if (!ex) missingViewModels++;
                                    }
                                }
                                firstV = false;
                                vw = vw.GetNextView() as IView;
                            }
                        }
                        catch { }
                        res["missingViewModels"] = missingViewModels;
                    }
                    res["ok"] = true;
                    try { app.CloseDoc(m.GetTitle()); } catch { }
                    return;
                }

                // ---- FEATURE RECIPE execution (parametric generation): run a recipe -> native part/assembly + verify,
                //      and LOG the whole thing to the recipe corpus (generated content; description masked). ----
                var recipeSpec = req["recipe"] as JObject;
                if (recipeSpec != null)
                {
                    resultPath = (string)req["resultPath"];
                    string outDir = (string)req["outDir"] ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ForgeRecipes");
                    var recipe = Recipe.FromJObject(recipeSpec);
                    var rr = RecipeExecutor.Execute(app, recipe, outDir);
                    try
                    {
                        string masked = ForgeData.Mask((string)req["description"] ?? recipe.Name, new string[0]);
                        ForgeData.LogRecipe((string)req["source"] ?? "breaker", masked, recipe.Raw, rr.ApiTrail,
                            rr.Built, rr.Verified, rr.Measured, rr.FailedOp, rr.Error);
                    }
                    catch { }
                    foreach (var prop in ((JObject)SafeParse(JsonConvert.SerializeObject(rr))).Properties()) res[prop.Name] = prop.Value;
                    return;
                }

                string asmPath = (string)req["assemblyPath"];
                string intent = (string)req["handlerIntent"] ?? "mate all the bolts";
                resultPath = (string)req["resultPath"];
                string shotPath = (string)req["screenshotPath"];
                res["assemblyPath"] = asmPath;
                res["handlerIntent"] = intent;

                // ---- STAGE TRACE. The result file is only written when the whole run finishes, so a case that never
                //      returns leaves NO evidence at all — which is exactly the state the seller's real assemblies
                //      were in. This appends one flushed line per stage next to the result, so even a killed run says
                //      where the time went. Instrument before theorising (docs/SOLIDWORKS-GOTCHAS.md). ----
                var traceSw = System.Diagnostics.Stopwatch.StartNew();
                string tracePath = resultPath + ".trace.log";
                Action<string> trace = stage =>
                {
                    // NOTE: SolidWorks.Interop.sldworks also defines `Environment`, so System. is spelled out here.
                    try { File.AppendAllText(tracePath, traceSw.ElapsedMilliseconds + "ms\t" + stage + System.Environment.NewLine); } catch { }
                };
                trace("start " + asmPath);

                // Pre-open hygiene: clear stale ~$ lock files a prior crash left in this model's folder, or the open
                // below can freeze / come up read-only. Keeps the 24/7 test-loop alive across its own crash+reopen cycle.
                CleanOrphanLocks(app, asmPath);
                trace("locks-cleaned");

                // ---- open the model (part or assembly, by extension) ----
                int errs = 0, warns = 0;
                int docType = asmPath.EndsWith(".sldprt", StringComparison.OrdinalIgnoreCase)
                    ? (int)swDocumentTypes_e.swDocPART
                    : asmPath.EndsWith(".slddrw", StringComparison.OrdinalIgnoreCase)
                    ? (int)swDocumentTypes_e.swDocDRAWING : (int)swDocumentTypes_e.swDocASSEMBLY;
                model = app.OpenDoc6(asmPath, docType,
                    (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref errs, ref warns) as IModelDoc2;
                if (model == null) { res["error"] = "OpenDoc6 failed errs=" + errs + " warns=" + warns; Write(resultPath, res); return; }
                // On this build a MISSING reference is only visible in OpenDoc6's return codes — SW auto-suppresses
                // the orphaned components, so post-open the document looks perfectly healthy (see docs/SOLIDWORKS-GOTCHAS.md).
                // Record the open-time codes so handlers can classify a "red wave" that is really a broken reference.
                OpenState.Record(asmPath, errs, warns);
                res["openErrors"] = errs; res["openWarnings"] = warns;
                try { title = model.GetTitle(); } catch { }
                trace("opened (errs=" + errs + " warns=" + warns + ")");
                try { model.ForceRebuild3(false); } catch { }
                trace("open-rebuild done");

                // ---- RUN 0: pre-handler baseline, so handlers that change STATE (mirror adds a component,
                //      fix-red-wave clears over-defines) can be verified by an independent DELTA, not just absolutes ----
                // gtOnly: the ground-truth blocks THIS case asserts on. Absent (the default) = measure everything,
                // exactly as before. Present = targeted mode, which is what makes a real 55MB assembly finishable.
                string[] gtOnly = null;
                { var ga = req["gtOnly"] as JArray;
                  if (ga != null && ga.Count > 0) { gtOnly = new string[ga.Count]; for (int gk = 0; gk < ga.Count; gk++) gtOnly[gk] = (string)ga[gk]; } }

                GroundTruth.Trace = trace;
                // skipRebuild: the open-rebuild two lines above JUST ran and nothing has mutated the doc since —
                // no handler has executed yet — so GT's own internal rebuild would be pure redundant cost on this
                // FIRST measurement only (speedup #2, see GroundTruth.Measure's skipRebuild doc comment).
                res["run0GroundTruth"] = SafeParse(GroundTruth.Measure(app, model, intent, gtOnly, skipRebuild: true));
                trace("run0 groundtruth done");

                bool useIntent = (bool?)req["useIntent"] ?? false;
                bool statsOnly = (bool?)req["statsOnly"] ?? false;
                bool rehearse = (bool?)req["rehearse"] ?? false;
                var chain = req["chain"] as JArray;   // test-loop: a SEQUENCE of intents run on the SAME model (state carries over)

                if (rehearse)
                {
                    // LIVE REHEARSAL: run the handler ONCE and LEAVE the model open on screen for a human to inspect.
                    // No idempotency re-run, no run1 GT, no screenshot, no auto-close (title=null skips the finally's CloseDoc).
                    var rlog = new List<string>();
                    object rr = null; string rex = null;
                    try { rr = await (useIntent ? IntentDispatch(app, model, intent, Collect(rlog)) : Dispatch(app, model, intent, Collect(rlog))); }
                    catch (Exception rxe) { rex = rxe.GetType().Name + ": " + rxe.Message; }
                    res["rehearse"] = true;
                    res["run1Log"] = new JArray(rlog);
                    res["run1Result"] = rr != null ? SafeParse(JsonConvert.SerializeObject(rr)) : null;
                    res["exception"] = rex;
                    res["ok"] = true;
                    try { model.ForceRebuild3(false); } catch { }
                    try { model.ViewZoomtofit2(); } catch { }
                    title = null;   // leave the doc OPEN for manual inspection
                }
                else if (statsOnly)
                {
                    // test-loop stat extraction: just the independent baseline stats (run0), no handler, no screenshot.
                    res["statsOnly"] = true; res["ok"] = true;
                }
                else if (chain != null && chain.Count > 0)
                {
                    // ---- WORKFLOW CHAIN: state left by one handler is the input the next must survive. Grade the
                    //      whole chain on the same invariants; a per-step GT delta exposes which handler broke it. ----
                    var steps = new JArray();
                    int stepNo = 0;
                    foreach (var it in chain)
                    {
                        string si = (string)it;
                        var slog = new List<string>();
                        object sr = null; string sex = null;
                        try { sr = await (useIntent ? IntentDispatch(app, model, si, Collect(slog)) : Dispatch(app, model, si, Collect(slog))); }
                        catch (Exception sxe) { sex = sxe.GetType().Name + ": " + sxe.Message; }
                        steps.Add(new JObject {
                            ["step"] = ++stepNo, ["intent"] = si,
                            ["log"] = new JArray(slog),
                            ["result"] = sr != null ? SafeParse(JsonConvert.SerializeObject(sr)) : null,
                            ["groundTruth"] = SafeParse(GroundTruth.Measure(app, model, intent, gtOnly)),
                            ["exception"] = sex
                        });
                    }
                    res["chainSteps"] = steps;
                    res["run1GroundTruth"] = steps.Count > 0 ? steps[steps.Count - 1]["groundTruth"] : null; // final state = last step
                }
                else
                {
                    // ---- RUN 1 ----
                    var log1 = new List<string>();
                    var r1 = await (useIntent ? IntentDispatch(app, model, intent, Collect(log1)) : Dispatch(app, model, intent, Collect(log1)));
                    trace("run1 handler done");
                    res["run1Log"] = new JArray(log1);
                    res["run1Result"] = r1 != null ? SafeParse(JsonConvert.SerializeObject(r1)) : null;
                    res["run1GroundTruth"] = SafeParse(GroundTruth.Measure(app, model, intent, gtOnly));
                    trace("run1 groundtruth done");

                    // ---- FAILURE HARVEST (failures are gold). INDEPENDENT of the handler's own claim: a
                    //      no-result, a handler-reported error, an unverified write, or geometry corruption (rebuild
                    //      errors / over-defines introduced vs the run0 baseline) is captured with full before/after
                    //      context. Written to the local failure corpus — the highest-signal training data Forge makes. ----
                    try
                    {
                        var g0j = res["run0GroundTruth"] as JObject;
                        var g1j = res["run1GroundTruth"] as JObject;
                        var r1j = res["run1Result"] as JObject;
                        string fmode = FailureMode(r1j, g0j, g1j);
                        if (fmode != null)
                        {
                            string crew = log1.Count > 0 ? log1[0].Split('|')[0].Trim() : "unknown";
                            bool ver = r1j != null && r1j["Verified"] != null && r1j["Verified"].Type == JTokenType.Boolean && (bool)r1j["Verified"];
                            ForgeData.LogFailure((string)req["source"] ?? "harness", crew, ForgeData.Mask(intent, new string[0]), null,
                                docType == (int)swDocumentTypes_e.swDocPART ? "part" : "assembly", fmode,
                                g0j, g1j, new JArray(log1), r1j, ver, r1j == null ? null : (string)r1j["Error"]);
                        }
                    }
                    catch { }

                    // ---- RUN 2 (idempotency — no reload) ----
                    var log2 = new List<string>();
                    var r2 = await (useIntent ? IntentDispatch(app, model, intent, Collect(log2)) : Dispatch(app, model, intent, Collect(log2)));
                    res["run2Log"] = new JArray(log2);
                    res["run2Result"] = r2 != null ? SafeParse(JsonConvert.SerializeObject(r2)) : null;
                    trace("run2 handler done");
                    var gt2 = SafeParse(GroundTruth.Measure(app, model, intent, gtOnly));
                    trace("run2 groundtruth done");
                    res["run2GroundTruth"] = gt2;
                    bool run2SaidAlready = log2.Exists(l => l.IndexOf("already assembled", StringComparison.OrdinalIgnoreCase) >= 0);
                    res["run2ReportedAlreadyAssembled"] = run2SaidAlready;
                }

                // ---- screenshots of the final state, from 4 angles so Ravi can confirm geometry (skip for stats-only) ----
                if (!statsOnly && !string.IsNullOrEmpty(shotPath))
                {
                    try { Directory.CreateDirectory(Path.GetDirectoryName(shotPath)); } catch { }
                    string stem = shotPath.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase)
                        ? shotPath.Substring(0, shotPath.Length - 4) : shotPath;
                    var views = new[] {
                        new[]{ "iso", "*Isometric" }, new[]{ "front", "*Front" },
                        new[]{ "top", "*Top" },       new[]{ "right", "*Right" },
                    };
                    var shots = new JArray();
                    foreach (var v in views)
                    {
                        try
                        {
                            model.ShowNamedView2(v[1], -1);
                            model.ViewZoomtofit2();
                            model.GraphicsRedraw2();
                            string p = stem + "-" + v[0] + ".bmp";
                            if (model.SaveBMP(p, 1200, 800)) shots.Add(new JObject { ["angle"] = v[0], ["path"] = p });
                        }
                        catch { }
                    }
                    res["screenshots"] = shots;
                    if (shots.Count > 0) res["screenshot"] = (string)shots[0]["path"]; // iso, for the summary
                }

                res["ok"] = true;
            }
            catch (Exception ex)
            {
                res["ok"] = false;
                res["exception"] = ex.GetType().Name + ": " + ex.Message;
                // an exception mid-run is the sharpest failure signal — harvest it with whatever state we captured.
                try
                {
                    ForgeData.LogFailure("harness", "exception", ForgeData.Mask((string)res["handlerIntent"] ?? "", new string[0]),
                        null, null, "exception", res["run0GroundTruth"] as JObject, res["run1GroundTruth"] as JObject,
                        res["run1Log"] as JArray, res["run1Result"] as JObject, false, ex.GetType().Name + ": " + ex.Message);
                }
                catch { }
            }
            finally
            {
                res["finishedUtc"] = DateTime.UtcNow.ToString("o");
                // close WITHOUT saving — model resets to its loose on-disk state for the next iteration
                try { if (title != null) app.CloseDoc(title); } catch { }
                Write(resultPath, res);
            }
        }

        // ---- classify a run as a failure INDEPENDENTLY of the handler's own success claim. Returns the failure
        //      mode string, or null if the run looks clean. Geometry corruption is judged by comparing the run0
        //      baseline to the run1 GroundTruth (rebuild errors / over-defines introduced) — never the handler's word. ----
        private static string FailureMode(JObject r1, JObject g0, JObject g1)
        {
            if (r1 == null) return "no_result";
            string err = (string)r1["Error"];
            if (!string.IsNullOrWhiteSpace(err)) return "handler_error";
            int rb0 = FindFirstInt(g0, "rebuildErrors"), rb1 = FindFirstInt(g1, "rebuildErrors");
            if (rb1 > rb0 && rb1 > 0) return "rebuild_errors";
            int od0 = FindFirstInt(g0, "overDefinedComponents"); if (od0 < 0) od0 = FindFirstInt(g0, "overDefined");
            int od1 = FindFirstInt(g1, "overDefinedComponents"); if (od1 < 0) od1 = FindFirstInt(g1, "overDefined");
            if (od1 > od0 && od1 > 0) return "over_defined";
            var v = r1["Verified"];
            if (v != null && v.Type == JTokenType.Boolean && (bool)v == false) return "not_verified";
            return null;
        }

        // first int-valued property with this name anywhere in the tree, else -1 (order: this object's own props, then descend).
        private static int FindFirstInt(JToken t, string name)
        {
            var o = t as JObject;
            if (o != null)
            {
                foreach (var p in o.Properties())
                    if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) && p.Value.Type == JTokenType.Integer) return (int)p.Value;
                foreach (var p in o.Properties()) { int r = FindFirstInt(p.Value, name); if (r >= 0) return r; }
            }
            else if (t is JArray a)
            {
                foreach (var e in a) { int r = FindFirstInt(e, name); if (r >= 0) return r; }
            }
            return -1;
        }

        // The handler entry both the panel and the harness call. For now: mate-all-fasteners → AutoMate.Run
        // (the same programmatic entry the panel uses). New handlers slot in here by intent.
        private static async Task<object> Dispatch(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            string i = (intent ?? "").Trim().ToLowerInvariant();
            // FindFeaturesByType FIRST: it is the narrowest feature matcher (needs a SEARCH verb + a type noun + no
            // write verb), so "find all holes under 10mm" can't be swallowed by GetFeatureInfo's radius/diameter rule
            // or by FilletChamfer's write matcher further down. A bare "list the features" has no type noun and still
            // falls through to GetFeatureTree.
            // FindFeatureByName BEFORE FindFeaturesByType: a spoken feature name often CONTAINS a type noun ("the
            // feature called seed hole"), which the type search would otherwise grab. Requiring called/named makes
            // this the more specific matcher of the two.
            // BatchRenameFeatures (tool 146) FIRST: it is the narrowest rename matcher (a rename/prefix VERB **and** a
            // batch scope word **and** a feature noun), so "rename all the fillets to EdgeFillet" can't be taken by
            // rename_feature (tool 145, the SINGULAR rename) further down. A bare "rename Fillet1 to TopFillet" has no
            // batch word and still reaches rename_feature.
            if (BatchRenameFeatures.IsIntent(i)) return await BatchRenameFeatures.Run(app, model, intent, emit);
            // OpenDocument (tool 124) BEFORE SetComponentLightweight: "open X.sldasm lightweight" carries the bare
            // word "lightweight" that SetComponentLightweight also fires on (and it doesn't require an "open" verb
            // at all), so the OPEN-TIME load option has to be claimed first or it lands on the wrong handler and
            // errors ("ASSEMBLY setting") because the CURRENTLY active doc isn't an assembly. OpenDocument requires
            // an ordered "open ... file/document/part/assembly" phrase or an explicit .sldprt/.sldasm path, and hard-
            // excludes DetectFileHealth's safe/ok/healthy/corrupt vocabulary, so it can't take "is it safe to open
            // this" or GetActiveDocument's "what document is open" (noun precedes the verb there).
            if (OpenDocument.IsIntent(i)) return await OpenDocument.Run(app, model, intent, null, emit);
            // SetComponentLightweight (tool 157): it owns the lightweight vocabulary for an ALREADY-open assembly's
            // components, and its "resolve" branch is fenced off from the red-wave fixer (it bails on any
            // mate/error/reference wording), so a "resolve the mate errors" prompt can never land here.
            if (SetComponentLightweight.IsIntent(i)) return await SetComponentLightweight.Run(app, model, intent, emit);
            // GetFileReferences (tool 130) BEFORE the dependency/impact matchers: it is the FILE-scoped question
            // ("what files does this assembly reference"), and it hard-bails on feature/dimension/mate scopes and on
            // the ghost/stale wording tool 247 owns, so it can never take those.
            // FindWhereUsed (tool 131) is the REVERSE lookup of GetFileReferences (130) — "which assemblies use this
            // part", not "what does this part reference". Checked FIRST of the pair: its where-used / who-uses phrasing
            // is the more specific, and a prompt like "what files use this part" would otherwise be taken by 130.
            if (FindWhereUsed.IsIntent(i)) return await FindWhereUsed.Run(app, model, intent, emit);
            // PackAndGo (tool 133, WRITE) BEFORE GetFileReferences: its "pack and go" jargon phrase is distinct
            // and more specific than GetFileReferences' plain what/list/show + file/reference pairing, but is
            // checked first anyway so no future wording overlap can shadow it.
            if (PackAndGo.IsIntent(i)) return await PackAndGo.Run(app, model, intent, emit);
            // UpdateSheetReferences (tool 114, WRITE) BEFORE RepairMissingReferences: it requires an explicit
            // drawing-scope noun (sheet/drawing/view) that RepairMissingReferences' matcher never checks for, so
            // it's the strictly more specific of the two and must be tried first (a drawing-scoped phrase like
            // "update sheet references" would otherwise fall into repair_missing_references' assembly-only Run,
            // which fails closed with "not an assembly" instead of reaching the right handler).
            if (UpdateSheetReferences.IsIntent(i)) return await UpdateSheetReferences.Run(app, model, intent, emit);
            // InsertBomTable (tool 113, WRITE) requires an insert-ish verb AND the "bom"/"bill of materials" noun —
            // no other matcher in this build claims "bom", so it's disjoint by vocabulary alone.
            if (InsertBomTable.IsIntent(i)) return await InsertBomTable.Run(app, model, intent, emit);
            // CleanBomTable (tool 161, WRITE) requires the same "bom" noun but a clean-ish verb and explicitly
            // EXCLUDES insert-ish verbs, so it can never shadow InsertBomTable regardless of dispatch order.
            if (CleanBomTable.IsIntent(i)) return await CleanBomTable.Run(app, model, intent, emit);
            // RepairBalloonReferences (tool 160, WRITE) requires the "balloon(s)" noun — no other matcher in this
            // build claims it, so it's disjoint by vocabulary alone.
            if (RepairBalloonReferences.IsIntent(i)) return await RepairBalloonReferences.Run(app, model, intent, emit);
            // RepairMate (tool 62, WRITE) requires a repair verb AND the "mate(s)" noun explicitly — dispatched
            // BEFORE RepairMissingReferences (132) since 132's noun set (reference/component/file, no "mate") never
            // matches "repair the mate" anyway, but specific-first stays the house rule.
            if (RepairMate.IsIntent(i)) return await RepairMate.Run(app, model, intent, emit);
            // RepairMissingReferences (tool 132, WRITE) requires a repair verb (repair/fix/resolve/relink/...) AND
            // missing/broken/unresolved/can't-find wording — disjoint from GetFileReferences' query verbs (what/
            // list/show/...) and from DetectGhostReferences' ghost/stale/orphan/dangling vocabulary (tool 247).
            if (RepairMissingReferences.IsIntent(i)) return await RepairMissingReferences.Run(app, model, intent, emit);
            if (GetFileReferences.IsIntent(i)) return await GetFileReferences.Run(app, model, intent, emit);
            // BatchConvertFiles (tool 135, WRITE — new export files only, source never touched) requires an explicit
            // neutral-format noun (step/iges/parasolid), so it can never take get_file_references'/find_where_used's
            // plain "files" phrasing.
            if (BatchConvertFiles.IsIntent(i)) return await BatchConvertFiles.Run(app, model, intent, emit);
            // ImportFile (tool 137, WRITE — neutral format -> NEW native part, the reverse direction of
            // batch_convert_files) requires an explicit "import"/"bring in" verb, disjoint from BatchConvertFiles'
            // convert/export vocabulary and from OpenDocument's plain "open" verb, so none of the three can shadow
            // each other regardless of dispatch order.
            if (ImportFile.IsIntent(i)) return await ImportFile.Run(app, model, intent, null, emit);
            // SaveBodiesAsParts (tool 166, WRITE) — requires BOTH a body noun ("body"/"bodies") AND a part/file
            // noun together, so it's the more specific match against a "save the bodies as parts" phrase and must
            // be checked BEFORE SaveDocumentAs (whose broad "save...as" regex would otherwise also fire) and
            // BEFORE SplitBody (whose "split"+body/part noun regex would otherwise also fire on "split the bodies
            // into separate part files").
            if (SaveBodiesAsParts.IsIntent(i)) return await SaveBodiesAsParts.Run(app, model, intent, emit);
            // SaveDocumentAs (tool 126, WRITE — native-format copy only) hard-excludes the neutral-format nouns
            // batch_convert_files/flat_dxf/drawing-PDF own (step/iges/parasolid/dxf/dwg/pdf/stl), so a "save as
            // STEP" prompt still lands on batch_convert_files, not here.
            if (SaveDocumentAs.IsIntent(i)) return await SaveDocumentAs.Run(app, model, intent, emit);
            // SaveDocument (tool 125, WRITE — in-place Save3) excludes "as"/"copy" (save_document_as's territory),
            // the same neutral-format nouns, and the flat-pattern/laser/nest vocabulary flat_dxf owns, so a plain
            // "save the file" is the only thing that reaches it.
            if (SaveDocument.IsIntent(i)) return await SaveDocument.Run(app, model, intent, emit);
            // CloseDocument (tool 127, WRITE): requires an explicit path/extension or a file/document noun (Rule
            // #2), and hard-refuses to close the CURRENTLY active document (Forge's own working context), so it
            // can never collide with anything that doesn't literally say "close".
            if (CloseDocument.IsIntent(i)) return await CloseDocument.Run(app, model, intent, emit);
            // InsertStandardViews (tool 102, WRITE) BEFORE CreateDrawing: "create a drawing with standard views"
            // matches both (CreateDrawing on the bare "create a drawing", this on the explicit view signal) — the
            // more specific one (requires a view/standard-views word) must win so the views clause isn't dropped.
            if (InsertStandardViews.IsIntent(i)) return await InsertStandardViews.Run(app, model, intent, emit);
            // InsertView (tool 103, WRITE) requires EXACTLY ONE orientation word and no standard/orthographic/
            // projection signal, so it never collides with InsertStandardViews's standardWord-or-orthoHits>=2
            // threshold. Its verb list (insert/add/create/generate/put/give me) deliberately excludes "make", so
            // "make the front view half scale" still lands on SetViewScale below, not here.
            if (InsertView.IsIntent(i)) return await InsertView.Run(app, model, intent, emit);
            // SetViewScale (tool 107, WRITE) requires an explicit "scale" word + a set/change/make/update/adjust
            // verb, which InsertStandardViews's verb+ortho-count matcher never satisfies alone ("make the front
            // view half scale" has only 1 ortho word, below its own >=2 threshold), so the two never collide.
            if (SetViewScale.IsIntent(i)) return await SetViewScale.Run(app, model, intent, emit);
            // DeleteView (tool 106, WRITE) requires an explicit delete/remove verb, which neither InsertStandardViews
            // nor SetViewScale's verb lists include, so it never collides with either.
            if (DeleteView.IsIntent(i)) return await DeleteView.Run(app, model, intent, emit);
            // AddNote (tool 112, WRITE) requires the explicit "note" noun, which no other drawing handler's matcher
            // uses, so it never collides.
            if (AddNote.IsIntent(i)) return await AddNote.Run(app, model, intent, emit);
            // InsertSectionView (tool 104, WRITE) requires the explicit "section" word AND excludes "detail" (tool
            // 105's territory), a vocabulary no other drawing-view matcher above claims, so it's disjoint by word
            // alone from InsertStandardViews/InsertView/SetViewScale/DeleteView.
            if (InsertSectionView.IsIntent(i)) return await InsertSectionView.Run(app, model, intent, emit);
            // InsertDetailView (tool 105, WRITE) requires the explicit "detail" word, disjoint from InsertSectionView's
            // required "section" word and every other drawing-view matcher above.
            if (InsertDetailView.IsIntent(i)) return await InsertDetailView.Run(app, model, intent, emit);
            // AddDrawingDimension (tool 109, WRITE) excludes "model" (108's word), "dangling/broken/repair/reattach"
            // (110/111's words), and any standalone numeric value (set_dimension's territory), so it's disjoint by
            // vocabulary from every other dimension handler above and below.
            if (AddDrawingDimension.IsIntent(i)) return await AddDrawingDimension.Run(app, model, intent, emit);
            // ImportModelDimensions (tool 108, WRITE) requires the explicit "model" word alongside a dimension
            // noun and an import-flavored verb (import/pull/bring) — deliberately excludes show/display/list so
            // it can never collide with GetDimensions (a part-scoped READ that owns those verbs).
            if (ImportModelDimensions.IsIntent(i)) return await ImportModelDimensions.Run(app, model, intent, emit);
            // CreateDrawing (tool 101, WRITE) hard-excludes drawing_package's rebuild/export/pdf/dangling/
            // dimension/package/batch vocabulary and flat_dxf's format nouns, so "generate the drawing package"
            // still reaches DrawingPkg, never this.
            if (CreateDrawing.IsIntent(i)) return await CreateDrawing.Run(app, model, intent, emit);
            // InsertNewPartInContext (tool 230, WRITE) BEFORE CreatePart — both fire on "new part", but this one
            // additionally demands explicit in-context/top-down/referencing/attached-to wording; CreatePart itself
            // also carries a matching exclusion (defense-in-depth, not just ordering).
            if (InsertNewPartInContext.IsIntent(i)) return await InsertNewPartInContext.Run(app, model, intent, emit);
            // CreatePart/CreateAssembly (tools 228/229, WRITE): narrow "new/blank/empty part|assembly" object
            // phrase, distinct nouns from CreateDrawing's "drawing" so no ordering dependency between the three.
            if (CreatePart.IsIntent(i)) return await CreatePart.Run(app, model, intent, emit);
            if (CreateAssembly.IsIntent(i)) return await CreateAssembly.Run(app, model, intent, emit);
            // FullyDefineSketch (tool 150, WRITE) BEFORE DiagnoseSketch: "fully define the under-defined sketches"
            // carries the word "under-defined" that DiagnoseSketch also fires on, but this one demands an explicit
            // WRITE verb (fully define / constrain / auto-dimension). Placing it first keeps that write from falling
            // through to the read-only diagnosis.
            if (FullyDefineSketch.IsIntent(i)) return await FullyDefineSketch.Run(app, model, intent, emit);
            // DiagnoseSketch (tool 149) BEFORE GetSketches (the plain count): both fire on the sketch noun, but this
            // one additionally demands a diagnostic question (why / diagnose / under-defined / unconstrained), so
            // "list the sketches" still reaches get_sketch_info.
            if (DiagnoseSketch.IsIntent(i)) return await DiagnoseSketch.Run(app, model, intent, emit);
            // CreateTreeFolder (tool 148) requires a FOLDER noun, which no other feature-tree handler claims, so it
            // sits with the tree family and can't collide with batch_rename_features (146) or the feature reads.
            if (CreateTreeFolder.IsIntent(i)) return await CreateTreeFolder.Run(app, model, intent, emit);
            // ListDanglingDimensions (tool 110, READ) BEFORE GetDrawingViews/DrawingPkg: both of those would also
            // fire on "dangling dims on this drawing" phrasing (a view/sheet/drawing noun alongside "dangling"),
            // so the narrower, dangling-specific matcher goes first (first-match-wins).
            if (ListDanglingDimensions.IsIntent(i)) return await ListDanglingDimensions.Run(app, model, intent, emit);
            // GetDrawingViews (tool 258, READ) BEFORE the drawing WRITERS further down: it is the only drawing handler
            // that takes no write/export verb at all, so putting it first costs the writers nothing and stops
            // "what views are on this drawing" from falling through to DrawingPkg's rebuild-and-export.
            if (GetDrawingViews.IsIntent(i)) return await GetDrawingViews.Run(app, model, intent, emit);
            // GetDrivingDimensions (tool 259, READ) BEFORE GetDimensions (tool 26): tool 26 owns the broad
            // "list the dimensions" roster, so a prompt about DRIVING/parametric/named dimensions has to be claimed
            // by the specific handler first. It needs a dimension noun AND a parametric-control word, so tool 26's
            // plain roster prompts are untouched.
            if (GetDrivingDimensions.IsIntent(i)) return await GetDrivingDimensions.Run(app, model, intent, emit);
            // SetRebuildVerification (tool 159) needs BOTH a verification word and a rebuild word, and excludes error/
            // drawing wording, so it can't take RedWave's "fix the rebuild errors" or DrawingPkg's "rebuild drawings".
            if (SetRebuildVerification.IsIntent(i)) return await SetRebuildVerification.Run(app, model, intent, emit);
            // GetCutList (tool 165, READ) BEFORE the feature-type matcher — "cut list" contains "cut" (a feature type)
            // and "list" (a search verb), so FindFeaturesByType would steal it. It GROUPS solid bodies into unique
            // shapes with quantities; needs a cut-list or body+group/unique/quantity signal, so a plain "list the cut
            // features" (no body/group noun) still lands on FindFeaturesByType, and "how many bodies" on list_bodies.
            if (GetCutList.IsIntent(i)) return await GetCutList.Run(app, model, intent, emit);
            // GetFixtureCapacity BEFORE nothing in particular needs to shadow it — its matcher demands a
            // capacity/how-many word AND a parts/pieces word AND a fixture/jig/hold/take/fit word all in one
            // short window, so it can't collide with GetCutList's "cut list"/body-group vocabulary above or
            // CountNamedComponents' "how many X in/on/inside" phrasing further down (that one needs an ending
            // in/on/inside/within preposition this doesn't have). test-loop wrong-answer fix (count-clamping-
            // positions): "what's the max parts this fixture can take" was falling through to a generic scan.
            if (GetFixtureCapacity.IsIntent(i)) return await GetFixtureCapacity.Run(app, model, intent, emit);
            // ResolveLocalizedNames (tool 245, READ) BEFORE the feature-name/type matchers — a "localized/non-english
            // feature names, resolve by type" ask INTERPRETS the tree by language-independent type. It requires a
            // localization signal (localized / non-english / a language name / "real type … name"), so a plain
            // find-feature-by-name, find-by-type, or list-features prompt is NOT shadowed.
            if (ResolveLocalizedNames.IsIntent(i)) return await ResolveLocalizedNames.Run(app, model, intent, emit);
            if (FindFeatureByName.IsIntent(i)) return await FindFeatureByName.Run(app, model, intent, emit);
            // CountThroughHoles (through-vs-blind hole READ, e.g. "how many holes go all the way through the
            // part") BEFORE FindFeaturesByType — that tool's "how many" + "hole" vocabulary is broader and would
            // otherwise shadow this more specific through/blind geometric question (test-loop wrong-answer
            // grinder-count-through-holes).
            if (CountThroughHoles.IsIntent(i)) return await CountThroughHoles.Run(app, model, intent, emit);
            if (FindFeaturesByType.IsIntent(i)) return await FindFeaturesByType.Run(app, model, intent, emit);
            // ListFeatureDependencies BEFORE Impact (tool 70): Impact's broad "what depends|dependen" regex owns
            // DIMENSION dependents; this one is the FEATURE-scoped question ("what features depend on Seed-Hole"),
            // so it must be the more specific matcher and must be reached first.
            if (ListFeatureDependencies.IsIntent(i)) return await ListFeatureDependencies.Run(app, model, intent, emit);
            // GetSheetMetalProps BEFORE WallThickness (tool 182): both talk about thickness, but this one only fires
            // on sheet-metal vocabulary (sheet metal / bend radius / k-factor / gauge), so a plain "check wall
            // thickness below 1mm" still lands on the wall-thickness scan.
            // SetSheetMetalThickness (tool 172, WRITE) BEFORE ValidateSheetMetal/GetSheetMetalProps: requires a
            // set/change/make/update/thicken/thin verb WITH the thickness/gauge noun, so it's the more specific
            // match against "set the sheet metal thickness to 3mm"; GetSheetMetalProps' own matcher already
            // excludes write verbs so there's no shadow risk either way, but specific-first stays the convention.
            if (SetSheetMetalThickness.IsIntent(i)) return await SetSheetMetalThickness.Run(app, model, intent, emit);
            // ValidateSheetMetal BEFORE GetSheetMetalProps: both fire on "sheet metal", but validate additionally
            // demands a check/validate verb, so it is the more specific of the two and must be reached first.
            if (ValidateSheetMetal.IsIntent(i)) return await ValidateSheetMetal.Run(app, model, intent, emit);
            if (GetSheetMetalProps.IsIntent(i)) return await GetSheetMetalProps.Run(app, model, intent, emit);
            if (GetDimensions.IsIntent(i)) return await GetDimensions.Run(app, model, intent, emit);
            if (GetComponentTransform.IsIntent(i)) return await GetComponentTransform.Run(app, model, intent, emit);
            // RenameFileWithReferences BEFORE RenameComponent: both can share "rename ... to", but this one requires
            // the "file" noun (RenameComponent now excludes it) - order kept specific-first anyway per convention.
            if (RenameFileWithReferences.IsIntent(i)) return await RenameFileWithReferences.Run(app, model, intent, emit);
            // MoveFileWithReferences requires "move"+"file"+"to" — disjoint from MoveComponent/TransformAssembly/
            // ReorderFeature (all share "move" but need a component/assembly/feature-tree noun, never "file").
            if (MoveFileWithReferences.IsIntent(i)) return await MoveFileWithReferences.Run(app, model, intent, emit);
            if (RenameComponent.IsIntent(i)) return await RenameComponent.Run(app, model, intent, emit);
            if (GetFeatureInfo.IsIntent(i)) return await GetFeatureInfo.Run(app, model, intent, emit);
            // CreateCoordSys (tool 167) needs a coordinate-system noun AND a create verb, and bails on read/delete/
            // export wording — so "how many coordinate systems are there" still reaches list_reference_geometry and
            // "export a step using the Forge coordinate system" can never land here.
            if (CreateCoordSys.IsIntent(i)) return await CreateCoordSys.Run(app, model, intent, emit);
            if (CreateRefAxis.IsIntent(i)) return await CreateRefAxis.Run(app, model, intent, emit);
            if (MeasureDistance.IsIntent(i)) return await MeasureDistance.Run(app, model, intent, emit);
            // MeasureFaceGap (tool 28, check_clearance READ) — "how far apart are the mating faces"/"face gap"/
            // "clearance between the faces". Distinct from MeasureDistance above: that one needs explicit NAMED
            // components ("between X and Y"); this fires on a generic "the (mating) faces" with no names, and
            // measures the actual closest anti-parallel planar-face pair, not bounding-box centres.
            if (MeasureFaceGap.IsIntent(i)) return await MeasureFaceGap.Run(app, model, intent, emit);
            // Upsize (M6->M8) BEFORE SelectByFilter: "replace all the M6 bolts with M8" contains the KIND word "bolt"
            // and would otherwise be swallowed by the filter's roster. Upsize's matcher is far narrower (TWO distinct
            // M-sizes + a swap verb), so specific-first says it wins. Placed here, not just at line ~580.
            if (System.Text.RegularExpressions.Regex.Matches(i, @"\bm(\d+)\b").Count >= 2 &&
                System.Text.RegularExpressions.Regex.IsMatch(i, @"\b(upsize|replace|swap|change|convert|to)\b"))
                return await Upsize.Run(app, model, intent, emit);
            // ChangeComponentConfig (tool 39, WRITE) BEFORE SelectByFilter AND set_active_configuration: "switch all the
            // bolts to the M8x30 config" names the KIND word "bolt" (which the filter roster would swallow) and fires
            // "switch...to...config" (which the doc-level active-config switch would steal). Its matcher is narrower than
            // both (config-word + a switch verb + a component word + a NAMED non-numeric target), so specific-first wins.
            if (ChangeComponentConfig.IsIntent(i)) return await ChangeComponentConfig.Run(app, model, intent, emit);
            // InsertComponent (tool 29, WRITE) — "insert/add <file>.SLDPRT" carries a FILE reference (which add_boss/add_hole
            // never do), so it wins over the feature-add family; placed here among the component writes.
            if (InsertComponent.IsIntent(i)) return await InsertComponent.Run(app, model, intent, emit);
            // BatchReplaceComponents (tool 164, WRITE) BEFORE ReplaceComponent: "replace the plate with X and the
            // bolts with Y" carries the SAME single-target trigger words ReplaceComponent.IsIntent looks for, but
            // needs 2+ distinct file refs — more specific, so it must be checked first (same shadowing lesson).
            if (BatchReplaceComponents.IsIntent(i)) return await BatchReplaceComponents.Run(app, model, intent, emit);
            // ReplaceComponent (tool 31, WRITE) BEFORE SelectByFilter: "replace the bolts with <path>" names kind "bolt"
            // which the filter roster would swallow; its matcher additionally requires a FILE reference so it never steals
            // a plain filter/select. Instrument-first — ReplaceComponents is unproven headless on this build.
            if (ReplaceComponent.IsIntent(i)) return await ReplaceComponent.Run(app, model, intent, emit);
            // BatchUpdateMaterials (tool 141, WRITE) BEFORE SelectByFilter: "set all the bolts to steel" contains
            // SelectByFilter's own trigger words ("all the" + kind noun), so the more specific matcher (kind + verb +
            // material) must win. Same shadowing lesson as ReplaceComponent above.
            if (BatchUpdateMaterials.IsIntent(i)) return await BatchUpdateMaterials.Run(app, model, intent, emit);
            if (SelectByFilter.IsIntent(i)) return await SelectByFilter.Run(app, model, intent, emit);
            // GetSelectedEntities (tool 16, READ) BEFORE SelectFace/Edge/Component/Plane below: its compound test
            // phrasing ("select the largest face, then tell me what's selected") also contains "select"+"face",
            // which SelectFace's matcher alone would swallow. GetSelectedEntities additionally requires a
            // what/show/list/get/tell READ verb that none of the plain select_* matchers carry, so it is the more
            // specific one and must be checked first (specific-first).
            if (GetSelectedEntities.IsIntent(i)) return await GetSelectedEntities.Run(app, model, intent, emit);
            // ClearSelection (tool 17, WRITE-of-state) — "clear/deselect/unselect the selection" — a distinct
            // verb+noun pair no other selection tool shares, so it's disjoint and safe anywhere in this family.
            if (ClearSelection.IsIntent(i)) return await ClearSelection.Run(app, model, intent, emit);
            // SelectFace (tool 13, WRITE-of-state) — "select the top/bottom/left/right/largest face" requires
            // BOTH "select" and "face" explicit, disjoint from SelectByFilter's component-kind matcher above.
            if (SelectFace.IsIntent(i)) return await SelectFace.Run(app, model, intent, emit);
            // SelectComponent (tool 11, WRITE-of-state) — "select the <name> component" / a quoted name, ONE
            // named assembly component by (near-)exact name. Disjoint from SelectFace (face/edge/plane words
            // excluded) and SelectByFilter (bulk kind roster + "all/every/which/filter" excluded).
            if (SelectComponent.IsIntent(i)) return await SelectComponent.Run(app, model, intent, emit);
            // SelectEdge (tool 14, WRITE-of-state) — "select the longest/shortest [linear|circular] edge"
            // requires BOTH "select" and "edge" PLUS an extreme word, so it wins over GetEdges' bare READ
            // matcher ("edge"+"longest" with no "select") below — must be checked BEFORE GetEdges.IsIntent.
            if (SelectEdge.IsIntent(i)) return await SelectEdge.Run(app, model, intent, emit);
            // SelectPlane (tool 15, WRITE-of-state) — "select the front/top/right plane" or a custom named
            // plane. Disjoint from SelectFace/SelectEdge/SelectComponent (different noun each).
            if (SelectPlane.IsIntent(i)) return await SelectPlane.Run(app, model, intent, emit);
            if (GetRefGeometry.IsIntent(i)) return await GetRefGeometry.Run(app, model, intent, emit);
            // SkipPatternInstance (tool 52, WRITE) — the distinguishing verb "skip" belongs to no other pattern tool, so
            // it sits ahead of the pattern edit/read family without shadowing them.
            if (SkipPatternInstance.IsIntent(i)) return await SkipPatternInstance.Run(app, model, intent, emit);
            // LinearPatternComponent (tool 41, WRITE) — CREATES a new component pattern (component + count +
            // spacing). Must be checked BEFORE EditPatternSpacing: that handler's broad "pattern"+"apart/spacing/
            // pitch"+digit matcher would otherwise swallow a create phrase like "...component 3 times, 40mm
            // apart" — this one's narrower "linear(ly) pattern"+"component"+count requirement is the more
            // specific match (specific-first), and edit_pattern_spacing never fires on a brand-new pattern.
            if (LinearPatternComponent.IsIntent(i)) return await LinearPatternComponent.Run(app, model, intent, emit);
            if (EditPatternSpacing.IsIntent(i)) return await EditPatternSpacing.Run(app, model, intent, emit);
            if (EditPatternCount.IsIntent(i)) return await EditPatternCount.Run(app, model, intent, emit);
            if (GetPatternInfo.IsIntent(i)) return await GetPatternInfo.Run(app, model, intent, emit);
            // DetectSharedSketches (tool 252, READ) BEFORE GetSketches — "shared/reused sketch" / "sketch driving
            // multiple features" INTERPRETS the sketch->feature graph; a plain "list the sketches" stays with GetSketches.
            // Requires a shared/reused noun, so it never shadows the plain sketch LIST.
            if (DetectSharedSketches.IsIntent(i)) return await DetectSharedSketches.Run(app, model, intent, emit);
            if (GetSketches.IsIntent(i)) return await GetSketches.Run(app, model, intent, emit);
            if (GetMaterial.IsIntent(i)) return await GetMaterial.Run(app, model, intent, emit);
            if (GetBodies.IsIntent(i)) return await GetBodies.Run(app, model, intent, emit);
            if (RenameDimension.IsIntent(i)) return await RenameDimension.Run(app, model, intent, emit);
            if (GetComponentMass.IsIntent(i)) return await GetComponentMass.Run(app, model, intent, emit);
            if (FindOverDefined.IsIntent(i)) return await FindOverDefined.Run(app, model, intent, emit);
            // HandleRollbackBar (tool 240, READ) — "is the rollback bar set / what's below the bar / is the tree
            // complete". The distinguishing noun is the rollback bar; requires "rollback" (or "below the bar"/"tree
            // complete"), so it never shadows DetectFileHealth (health/preflight) or GetRebuildErrors (error list).
            if (HandleRollbackBar.IsIntent(i)) return await HandleRollbackBar.Run(app, model, intent, emit);
            // DetectFileHealth (tool 239, READ) — pre-flight "is this file safe / healthy to touch". Requires a
            // health/preflight/safe-to-touch noun (NOT an error-list ask), so it never shadows GetRebuildErrors below.
            if (DetectFileHealth.IsIntent(i)) return await DetectFileHealth.Run(app, model, intent, emit);
            // HandleLockedFiles (tool 248, READ) — locked/read-only/checked-out/permission-denied + file/document
            // noun, disjoint from DetectFileHealth (health/preflight/corruption) and GetFileReferences (which
            // explicitly excludes lock vocabulary), no ordering dependency.
            if (HandleLockedFiles.IsIntent(i)) return await HandleLockedFiles.Run(app, model, intent, emit);
            // DetectInContextWrites (tool 242, READ) — in-context/external-reference vocabulary, or "will editing
            // this also touch/change/affect other files/parts" phrasing. Disjoint from GetFileReferences (which
            // answers "what does this depend on", not "what feature-level operation would ripple where") and from
            // HandleLockedFiles (lock/read-only vocabulary); GetFileReferences gets a matching exclusion below it
            // never claims this handler's vocabulary is answered by it.
            if (DetectInContextWrites.IsIntent(i)) return await DetectInContextWrites.Run(app, model, intent, emit);
            // HandleUnknownFeatures (tool 243, READ) — unknown/third-party/plugin/macro-feature vocabulary,
            // disjoint from DetectInContextWrites (in-context/external-reference/ripple).
            if (HandleUnknownFeatures.IsIntent(i)) return await HandleUnknownFeatures.Run(app, model, intent, emit);
            // HandleAssemblyFeatures (tool 250, READ) — assembly-level cut/hole/fillet/chamfer/draft vocabulary,
            // disjoint from HandleUnknownFeatures (unknown/third-party type) and DetectInContextWrites (external-
            // file-reference risk) — this is about a feature's SCOPE (assembly vs component), not its type or refs.
            if (HandleAssemblyFeatures.IsIntent(i)) return await HandleAssemblyFeatures.Run(app, model, intent, emit);
            // TraceDerivedParts (tool 251, READ) — derived/lineage/derivation vocabulary, or trace+chain/parts.
            // Disjoint from DetectInContextWrites (one hop, in-context/ripple wording) — this walks the SAME
            // per-feature external-ref signal MULTIPLE hops across documents to map the full ancestry.
            if (TraceDerivedParts.IsIntent(i)) return await TraceDerivedParts.Run(app, model, intent, emit);
            // RecoverAutosave (tool 253, READ) — recover/restore/salvage + autosave/backup/crash vocabulary;
            // disjoint from HandleLockedFiles (lock/read-only) and DetectFileHealth (health/preflight/corruption).
            if (RecoverAutosave.IsIntent(i)) return await RecoverAutosave.Run(app, model, intent, emit);
            // HandleConfigExplosion (tool 255, READ, guard) — "activate/rebuild ALL/EVERY config(s)" bulk phrasing
            // or explicit config-explosion vocabulary; SetActiveConfiguration/RebuildDocument both exclude the
            // bulk-all/every phrasing so there's no shadowing either direction, but dispatched here (specific-first).
            if (HandleConfigExplosion.IsIntent(i)) return await HandleConfigExplosion.Run(app, model, intent, emit);
            // DetectSimulationArtifacts (tool 256, READ, guard) — weld-bead/belt-chain/sim-artifact vocabulary;
            // disjoint from HandleUnknownFeatures (generic third-party "MacroFeature" type, not a native type).
            if (DetectSimulationArtifacts.IsIntent(i)) return await DetectSimulationArtifacts.Run(app, model, intent, emit);
            // QuarantineFile (tool 257, READ+WRITE-of-state) — quarantine/isolate+file/poltergeist vocabulary;
            // Isolator.IsIsolateIntent got a matching exclusion so it never shadows this in either direction.
            if (QuarantineFile.IsIntent(i)) return await QuarantineFile.Run(app, model, intent, emit);
            // GetRebuildErrors (tool 96, READ) — "list the rebuild/feature errors". Excludes fix verbs (RedWave) and
            // requires a rebuild/feature error noun, so "list mate errors" still falls through to RedWave below.
            if (GetRebuildErrors.IsIntent(i)) return await GetRebuildErrors.Run(app, model, intent, emit);
            // ResolveDuplicatePaths (tool 241, READ) — same leaf filename resolving to TWO+ different folders (network
            // + local copy); requires a path/location/folder/network noun, disjoint from FindDuplicateComponents'
            // exact-path grouping (which now excludes this vocabulary), dispatched specific-first immediately before it.
            if (ResolveDuplicatePaths.IsIntent(i)) return await ResolveDuplicatePaths.Run(app, model, intent, emit);
            // FindDuplicateComponents (tool 136, READ) — components backed by the same file more than once; requires a
            // component/part noun and EXCLUDES the body noun, so it never shadows CompareBodies (bodies within a part).
            if (FindDuplicateComponents.IsIntent(i)) return await FindDuplicateComponents.Run(app, model, intent, emit);
            // CombineBodies (tool 219, WRITE) — boolean-union all solid bodies; combine/merge/union verb + body noun.
            // Distinct verb from CompareBodies (compare/duplicate/overlap), so no shadow; dispatched before it.
            if (CombineBodies.IsIntent(i)) return await CombineBodies.Run(app, model, intent, emit);
            // SplitBody (tool 220, WRITE, PROBE) — split verb + body/block/part/solid/half noun; disjoint from
            // CombineBodies' combine/merge/union verb.
            if (SplitBody.IsIntent(i)) return await SplitBody.Run(app, model, intent, emit);
            // RunImportDiagnostics (tool 154, WRITE, PROBE) — diagnostic verb + import/gap/face-heal noun; disjoint
            // from GetRebuildErrors' plain "rebuild/feature errors" phrasing (requires "import" explicitly).
            if (RunImportDiagnostics.IsIntent(i)) return await RunImportDiagnostics.Run(app, model, intent, emit);
            // CheckGeometryErrors (tool 156, READ, PROBE) — check/find/detect/scan verb + gap/sliver/geometry-error
            // noun, explicitly excludes "import" so it never shadows RunImportDiagnostics.
            if (CheckGeometryErrors.IsIntent(i)) return await CheckGeometryErrors.Run(app, model, intent, emit);
            // AddCenterMarks (tool 162, WRITE, PROBE) — add/insert/restore/put/show verb + "center mark(s)" noun.
            // No other matcher in this build claims "center mark", so no ordering dependency needed.
            if (AddCenterMarks.IsIntent(i)) return await AddCenterMarks.Run(app, model, intent, emit);
            // ReplaceSheetFormat (tool 163, WRITE) — replace/swap/fix/repair verb + "sheet format"/"title block"/
            // drawing-scoped "template" noun. Disjoint from ReplaceComponent (requires a component/part noun + a
            // quoted file/.sldprt/.sldasm reference) regardless of dispatch order.
            if (ReplaceSheetFormat.IsIntent(i)) return await ReplaceSheetFormat.Run(app, model, intent, emit);
            // UpdateRevisionTable (tool 184, WRITE) — add/log/record/update/bump verb + "revision(s)" noun. No
            // other matcher in this build claims the bare word "revision" as a trigger, so no ordering dependency
            // needed against compare_documents (verb "compare"/"diff").
            if (model is DrawingDoc && UpdateRevisionTable.IsIntent(i)) return await UpdateRevisionTable.Run(app, model, intent, emit);
            // CheckDraftingStandards (tool 185, READ) — check/audit/lint/validate/review/verify verb + "drafting/
            // dimensioning standard(s)"/"release-ready" noun. Excludes set/switch/change/use/convert (SetDraftingStandard's
            // verb set), so the two never collide regardless of dispatch order.
            if (model is DrawingDoc && CheckDraftingStandards.IsIntent(i)) return await CheckDraftingStandards.Run(app, model, intent, emit);
            // CompareBodies (tool 170, READ) — duplicate/overlapping BODIES within a part; requires a body/solid noun.
            if (CompareBodies.IsIntent(i)) return await CompareBodies.Run(app, model, intent, emit);
            // CheckPartSymmetry (tool 176, READ) — is this PART symmetric/handed; excludes component/feature mirror verbs.
            if (CheckPartSymmetry.IsIntent(i)) return await CheckPartSymmetry.Run(app, model, intent, emit);
            // AddSketchPolygon (tool 196, WRITE) — draw a regular N-gon sketch; needs a polygon noun so it can't collide.
            if (AddSketchPolygon.IsIntent(i)) return await AddSketchPolygon.Run(app, model, intent, emit);
            // AddSketchSlot (tool 195, WRITE) — draw a straight slot sketch; needs a slot noun.
            if (AddSketchSlot.IsIntent(i)) return await AddSketchSlot.Run(app, model, intent, emit);
            // AddSketchEllipse (tool 197, WRITE) — draw an ellipse sketch; needs an ellipse/oval noun.
            if (AddSketchEllipse.IsIntent(i)) return await AddSketchEllipse.Run(app, model, intent, emit);
            // AddConstructionGeometry (tool 204, WRITE) — draw a construction centerline; needs a construction/centerline noun.
            if (AddConstructionGeometry.IsIntent(i)) return await AddConstructionGeometry.Run(app, model, intent, emit);
            // AddSketchArc (tool 117, WRITE) — draw a centrepoint arc; excludes slot/construction arc nouns.
            if (AddSketchArc.IsIntent(i)) return await AddSketchArc.Run(app, model, intent, emit);
            // AddSketchEntity (tool 82, WRITE) — draw a basic line/circle/rectangle/point at explicit coordinates;
            // excludes arc/ellipse/polygon/slot/spline/text nouns so it never shadows those specific siblings.
            if (AddSketchEntity.IsIntent(i)) return await AddSketchEntity.Run(app, model, intent, emit);
            // AddSketchDimension (tool 83, WRITE) — draw an undimensioned sketch line and add a NEW dimension to it
            // with a value; requires the literal "sketch" word and excludes SetDimension's edit verbs.
            if (AddSketchDimension.IsIntent(i)) return await AddSketchDimension.Run(app, model, intent, emit);
            // AddSketchRelation (tool 84, WRITE) — draw two unrelated sketch lines and constrain them (parallel/
            // perpendicular/equal/collinear/coincident); requires the literal "sketch" word + a relation noun.
            if (AddSketchRelation.IsIntent(i)) return await AddSketchRelation.Run(app, model, intent, emit);
            // Create3DSketch (tool 123, WRITE) — start a new 3D sketch (routing/sweep-path prep); requires the
            // literal "3d"/"3-d" token, so it never collides with CreateSketch's plain "sketch" phrasing. Checked
            // BEFORE CreateSketch (specific-first); CreateSketch also excludes "3d" itself as a second guard.
            if (Create3DSketch.IsIntent(i)) return await Create3DSketch.Run(app, model, intent, emit);
            // ImportDxfToSketch (tool 205, WRITE) — bring an external DXF/DWG profile in as sketch entities; requires
            // the literal "dxf"/"dwg" token + an import/insert verb, and excludes FlatDxf's export/flatten verbs so
            // the two (import vs export direction) never shadow each other.
            if (ImportDxfToSketch.IsIntent(i)) return await ImportDxfToSketch.Run(app, model, intent, emit);
            // CreateLayoutSketch (tool 231, WRITE) BEFORE CreateSketch — both fire on "sketch", but this one
            // additionally demands explicit layout/skeleton/master-sketch wording and is ASSEMBLY-only; CreateSketch
            // itself also carries a matching exclusion (defense-in-depth, not just ordering).
            if (CreateLayoutSketch.IsIntent(i)) return await CreateLayoutSketch.Run(app, model, intent, emit);
            // CreateSketch (tool 81, WRITE) — start a new EMPTY sketch on a plane; excludes every shape noun (so it
            // never shadows AddSketchEntity/AddSketchArc/etc.) and every transform verb (pattern/mirror/offset/trim).
            if (CreateSketch.IsIntent(i)) return await CreateSketch.Run(app, model, intent, emit);
            // CreateCurve (tool 214, WRITE) — curve through XYZ points; BEFORE the sketch spline (specific-first): it
            // owns the qualified curve-FEATURE phrasing (through points / 3d / space / guide / xyz curve) and excludes
            // helix, while the spline keeps plain "curve"/"spline" — so the three never shadow each other.
            if (CreateCurve.IsIntent(i)) return await CreateCurve.Run(app, model, intent, emit);
            // AddSketchSpline (tool 116, WRITE) — draw a spline through points; needs a spline/curve/freeform noun.
            if (AddSketchSpline.IsIntent(i)) return await AddSketchSpline.Run(app, model, intent, emit);
            // AddSketchText (tool 198, WRITE) — insert a sketch-text note; needs a text/note/label noun.
            if (AddSketchText.IsIntent(i)) return await AddSketchText.Run(app, model, intent, emit);
            // OffsetSketchEntities (tool 199, WRITE) — offset a seeded sketch entity; needs an offset verb.
            if (OffsetSketchEntities.IsIntent(i)) return await OffsetSketchEntities.Run(app, model, intent, emit);
            // ConvertEntities (tool 200, WRITE) — project a body edge into a sketch; needs a convert/project verb.
            if (ConvertEntities.IsIntent(i)) return await ConvertEntities.Run(app, model, intent, emit);
            // TrimExtendSketch (tool 201, WRITE) — trim/extend a seeded sketch entity; needs a trim/extend verb.
            if (TrimExtendSketch.IsIntent(i)) return await TrimExtendSketch.Run(app, model, intent, emit);
            // AddRevolve (tool 118, WRITE) — revolve a profile 360 deg into a body; needs a revolve verb.
            if (AddRevolve.IsIntent(i)) return await AddRevolve.Run(app, model, intent, emit);
            // CreateSweptSurface (tool 223, WRITE, PROBE) — BEFORE AddSweep (specific-first): "add a swept SURFACE"
            // needs BOTH "surface" and a sweep verb, so it must be checked before AddSweep's bare sweep-verb matcher
            // (which would otherwise swallow it and produce a solid instead of a surface).
            if (CreateSweptSurface.IsIntent(i)) return await CreateSweptSurface.Run(app, model, intent, emit);
            // AddSweep (tool 119, WRITE) — sweep a circular profile along a path into a body; needs a sweep verb.
            if (AddSweep.IsIntent(i)) return await AddSweep.Run(app, model, intent, emit);
            // CreateBoundaryFeature (tool 210, WRITE, PROBE) — needs the specific "boundary" noun; must be checked
            // BEFORE AddLoft since AddLoft's matcher includes bare "blend" which "boundary blend" would also hit.
            if (CreateBoundaryFeature.IsIntent(i)) return await CreateBoundaryFeature.Run(app, model, intent, emit);
            // CreateThicken (tool 209, WRITE, PROBE) — needs "thicken" + "surface"; narrow so it never shadows
            // WallThickness's "thicken the wall to Xmm" write-chain phrasing (which never says "surface").
            if (CreateThicken.IsIntent(i)) return await CreateThicken.Run(app, model, intent, emit);
            // AddLoft (tool 120, WRITE) — loft between two profiles into a body; needs a loft/blend verb.
            if (AddLoft.IsIntent(i)) return await AddLoft.Run(app, model, intent, emit);
            // AddHelix (tool 213, WRITE) — thread a constant-pitch helix through a circle; needs a helix/coil verb.
            if (AddHelix.IsIntent(i)) return await AddHelix.Run(app, model, intent, emit);
            // CreateRib (tool 207, WRITE) — bridge an L corner with a gusset rib; needs a rib/gusset/stiffener noun.
            if (CreateRib.IsIntent(i)) return await CreateRib.Run(app, model, intent, emit);
            // CreateExtrudedSurface (tool 222, WRITE, PROBE) — extrude a sketch into a SURFACE body; needs BOTH
            // "surface" and an "extrud..." verb, so a bare "extrude a boss" (AddBoss) never lands here.
            if (CreateExtrudedSurface.IsIntent(i)) return await CreateExtrudedSurface.Run(app, model, intent, emit);
            // AddDome (tool 121, WRITE) — bulge a planar face outward into a rounded cap; needs a dome/bulge verb.
            if (AddDome.IsIntent(i)) return await AddDome.Run(app, model, intent, emit);
            if (GetComponentConfig.IsIntent(i)) return await GetComponentConfig.Run(app, model, intent, emit);
            if (GetAppearance.IsIntent(i)) return await GetAppearance.Run(app, model, intent, emit);
            if (GetPartNumber.IsIntent(i)) return await GetPartNumber.Run(app, model, intent, emit);
            // DissolveSubassembly (tool 40, WRITE) BEFORE ListSubassemblies: "dissolve the sub-assembly" carries the
            // sub-assembly noun ListSubassemblies also matches, but its distinctive verb "dissolve"/"flatten" belongs to
            // no read handler, so specific-first wins and neither shadows the other.
            if (DissolveSubassembly.IsIntent(i)) return await DissolveSubassembly.Run(app, model, intent, emit);
            // SetSubassemblyFlexibility (tool 158, WRITE) BEFORE ListSubassemblies: needs a flexib/rigid keyword
            // WITH the sub-assembly noun, so it never collides with DissolveSubassembly's dissolve/flatten verbs
            // or ListSubassemblies' plain count/list ask.
            if (SetSubassemblyFlexibility.IsIntent(i)) return await SetSubassemblyFlexibility.Run(app, model, intent, emit);
            if (ListSubassemblies.IsIntent(i)) return await ListSubassemblies.Run(app, model, intent, emit);
            if (FindFloating.IsIntent(i)) return await FindFloating.Run(app, model, intent, emit);
            if (SetActiveConfiguration.IsIntent(i)) return await SetActiveConfiguration.Run(app, model, intent, emit);
            // ValidateScaleSanity (tool 254, READ) BEFORE the units cluster + GetBoundingBox — a scale/unit-ERROR ask
            // ("is this the right scale", "units look off", "25.4x too big") INTERPRETS the box for a wrong-unit import.
            // It requires a scale/unit-error noun, so plain "set/change units" (SetDocumentUnits) and "what units is this"
            // (GetDocumentUnits) and a plain size read ("how big / bounding box", GetBoundingBox) are NOT shadowed.
            if (ValidateScaleSanity.IsIntent(i)) return await ValidateScaleSanity.Run(app, model, intent, emit);
            // NormalizeUnits (tool 244, READ/detect) BEFORE the units cluster — a MIXED-unit / consistency ask
            // ("are any parts in inches", "mixed units", "normalize the units") walks per-component units. It requires a
            // mix/consistency/normalize framing (or an "any parts in inches"-style hunt), so a plain "what units is
            // this" (GetDocumentUnits) and "set the units to X" (SetDocumentUnits) are NOT shadowed.
            if (NormalizeUnits.IsIntent(i)) return await NormalizeUnits.Run(app, model, intent, emit);
            if (SetAngularUnits.IsIntent(i)) return await SetAngularUnits.Run(app, model, intent, emit);
            if (SetDecimalPlaces.IsIntent(i)) return await SetDecimalPlaces.Run(app, model, intent, emit);
            // SetDraftingStandard (tool 232, WRITE, display) — ANSI/ISO/DIN/JIS/BS/GOST/GB; distinct vocabulary
            // (standard names) from units/decimal-places, so it never shadows or gets shadowed by them.
            if (SetDraftingStandard.IsIntent(i)) return await SetDraftingStandard.Run(app, model, intent, emit);
            if (SetDocumentUnits.IsIntent(i)) return await SetDocumentUnits.Run(app, model, intent, emit);
            if (GetDocumentUnits.IsIntent(i)) return await GetDocumentUnits.Run(app, model, intent, emit);
            if (DeleteCustomProperty.IsIntent(i)) return await DeleteCustomProperty.Run(app, model, intent, emit);
            if (GetCustomProperty.IsIntent(i)) return await GetCustomProperty.Run(app, model, intent, emit);
            if (GetMaterialDensity.IsIntent(i)) return await GetMaterialDensity.Run(app, model, intent, emit);
            // BatchUpdateCustomProperties (tool 139, WRITE) BEFORE SetCustomProperty: both fire on "set property X to
            // Y", but this one additionally demands an all/every/each + part/component scope word, so a bare
            // "set the property Reviewer to Forge" still goes to the single-document set_custom_property.
            if (BatchUpdateCustomProperties.IsIntent(i)) return await BatchUpdateCustomProperties.Run(app, model, intent, emit);
            // CopyPropertiesBetweenFiles (tool 142, WRITE) requires "copy" + "propert(y|ies)" + "from" — no other
            // property handler's verb list includes "copy" or "from", so it never collides.
            if (CopyPropertiesBetweenFiles.IsIntent(i)) return await CopyPropertiesBetweenFiles.Run(app, model, intent, null, emit);
            // CopySketchToPart (tool 152, WRITE) requires "copy" + "sketch" + "from" and explicitly excludes the
            // property noun, so it never collides with CopyPropertiesBetweenFiles right above.
            if (CopySketchToPart.IsIntent(i)) return await CopySketchToPart.Run(app, model, intent, null, emit);
            // InsertLibraryFeature (tool 218, WRITE) requires the explicit "library feature" phrase — disjoint from
            // every other insert/copy vocabulary in this build.
            if (InsertLibraryFeature.IsIntent(i)) return await InsertLibraryFeature.Run(app, model, intent, emit);
            if (SetCustomProperty.IsIntent(i)) return await SetCustomProperty.Run(app, model, intent, emit);
            // CopyConfiguration BEFORE Create/Rename/Delete: it fires only on copy/duplicate/clone, which none of the
            // others claim, but keeping it first makes the config cluster's precedence explicit.
            if (CopyConfiguration.IsIntent(i)) return await CopyConfiguration.Run(app, model, intent, emit);
            if (RenameConfiguration.IsIntent(i)) return await RenameConfiguration.Run(app, model, intent, emit);
            if (DeleteConfiguration.IsIntent(i)) return await DeleteConfiguration.Run(app, model, intent, emit);
            if (CreateConfiguration.IsIntent(i)) return await CreateConfiguration.Run(app, model, intent, emit);
            if (GetFaces.IsIntent(i)) return await GetFaces.Run(app, model, intent, emit);
            if (GetEdges.IsIntent(i)) return await GetEdges.Run(app, model, intent, emit);
            if (MeasureAngle.IsIntent(i)) return await MeasureAngle.Run(app, model, intent, emit);
            if (GetActiveDocument.IsIntent(i)) return await GetActiveDocument.Run(app, model, intent, emit);
            if (GetMateInfo.IsIntent(i)) return await GetMateInfo.Run(app, model, intent, emit);
            if (DeleteMate.IsIntent(i)) return await DeleteMate.Run(app, model, intent, emit);
            // DeleteComponent (tool 30, WRITE): "delete the bolts" names a component KIND and excludes feature/mate/config
            // words, so it never collides with delete_feature/delete_mate/delete_configuration. Naturally idempotent.
            if (DeleteComponent.IsIntent(i)) return await DeleteComponent.Run(app, model, intent, emit);
            if (SuppressMate.IsIntent(i)) return await SuppressMate.Run(app, model, intent, emit);
            // GetComponentInfo (per-instance flags) before Scout/scan — component classification, not a health count.
            if (GetComponentInfo.IsIntent(i)) return await GetComponentInfo.Run(app, model, intent, emit);
            // ListComponents (tool 2, the plain roster) AFTER GetComponentInfo, which explicitly claims the phrase
            // "list the components". This one answers the tree question instead, and bails on any flag/classification
            // wording, so the two can't fight over a prompt.
            if (ListComponents.IsIntent(i)) return await ListComponents.Run(app, model, intent, emit);
            // DetectGhostReferences BEFORE RedWave/Doctor: "stale/ghost/orphaned references" is a DIFFERENT diagnosis
            // from an over-defined red wave, and routing it to the mate-remover would delete healthy mates.
            if (DetectGhostReferences.IsIntent(i)) return await DetectGhostReferences.Run(app, model, intent, emit);
            // GetProperties (plain read) before ValidateProps (release-readiness) — both say "properties"; the plain
            // read wins only when no validate/release/ready wording is present.
            if (GetProperties.IsPropsIntent(i) &&
                !System.Text.RegularExpressions.Regex.IsMatch(i, @"\b(validate|release|ready|bom ready|missing)\b"))
                return await GetProperties.Run(app, model, intent, emit);
            // ListMates BEFORE AutoMate: "list all the mates" contains "mate" and would otherwise be assembled, not listed.
            if (ListMates.IsListMatesIntent(i)) return await ListMates.Run(app, model, intent, emit);
            // AddConcentricMate (tool 55, WRITE) BEFORE AutoMate: "add a concentric mate between the bolt and the plate"
            // contains "mate" and would otherwise be swallowed by the fastener auto-mate pipeline; its matcher requires
            // the word "concentric" + an add verb so it never collides with AutoMate / suppress_mate / delete_mate.
            if (AddConcentricMate.IsIntent(i)) return await AddConcentricMate.Run(app, model, intent, emit);
            if (AddCoincidentMate.IsIntent(i)) return await AddCoincidentMate.Run(app, model, intent, emit);
            if (AddParallelMate.IsIntent(i)) return await AddParallelMate.Run(app, model, intent, emit);
            if (AddDistanceMate.IsIntent(i)) return await AddDistanceMate.Run(app, model, intent, emit);
            if (AddAngleMate.IsIntent(i)) return await AddAngleMate.Run(app, model, intent, emit);
            // AddWidthMate (tool 58, WRITE): "center the tab in the channel" / "width mate" — a centring word, distinct
            // from the other add_*_mate verbs; dispatched before AutoMate so "mate all the bolts" is unaffected.
            if (AddWidthMate.IsIntent(i)) return await AddWidthMate.Run(app, model, intent, emit);
            // EditMateValue (tool 59, WRITE): "change the distance mate to 25mm" — a NON-add verb + 'mate' + a number.
            // Checked before SetDimension (which also matches change/set + a number, but now excludes 'mate').
            if (EditMateValue.IsIntent(i)) return await EditMateValue.Run(app, model, intent, emit);
            if (AutoMate.IsMateIntent(i)) return await AutoMate.Run(app, model, emit);
            // Pattern ADDS new component instances into empty holes; Mate SEATS bolts that already exist. The two
            // vocabularies don't overlap ("pattern"/"populate"/"put a bolt in every hole" vs "mate"/"assemble"/"tighten"),
            // but keep this check adjacent to mate so the boundary is explicit and neither can shadow the other.
            // PatternSketchEntities (tool 203, WRITE) — linear pattern of a SKETCH entity. Requires an explicit "sketch"
            // noun, so it's the MOST specific of the pattern family and MUST be checked before PatternFeature/Component
            // (whose broad "pattern" verb would otherwise steal "pattern the sketch").
            if (PatternSketchEntities.IsIntent(i)) return await PatternSketchEntities.Run(app, model, intent, emit);
            // PatternFeature (pattern a FEATURE on a part: hole/cut/boss) must be checked BEFORE PatternComponent — it is
            // the more specific matcher (requires a feature noun AND excludes component/bolt/part words), so it never
            // shadows component patterns, but PatternComponent's broad "pattern" verb would otherwise steal a feature intent.
            if (PatternFeature.IsPatternFeatureIntent(i)) return await PatternFeature.Run(app, model, intent, emit);
            // (LinearPatternComponent, tool 41, is checked much earlier — alongside EditPatternSpacing/
            // EditPatternCount/GetPatternInfo — since ITS broad "pattern"+digit matchers would otherwise shadow it.)
            // CircularPatternComponent (tool 42, WRITE) — a GENERIC circular component pattern (any named
            // component, explicit count/angle). Must be checked BEFORE PatternComponent: that handler's broad
            // "pattern"/"circular pattern" matcher would otherwise swallow it (specific-first) — this one's
            // narrower "circular(ly) pattern"+"component"+count requirement is the more specific match.
            if (CircularPatternComponent.IsIntent(i)) return await CircularPatternComponent.Run(app, model, intent, emit);
            // PatternDrivenPatternComponent (tool 44, WRITE) — follows an EXISTING feature pattern (LPattern/
            // CirPattern) already on another component, rather than a user-given count/spacing (41/42) or bare
            // hole/cylinder geometry with no backing feature (PatternComponent below). Its "existing/feature/hole
            // pattern" + follow/match/driven wording is disjoint from PatternComponent's broad bare "pattern" verb,
            // but must still be checked BEFORE it (specific-first).
            if (PatternDrivenPatternComponent.IsIntent(i)) return await PatternDrivenPatternComponent.Run(app, model, intent, emit);
            // SketchDrivenPatternComponent (tool 45, WRITE) — places copies at an existing SKETCH's points, the
            // final member of the pattern-component family. Its "sketch"+"point(s)"+"component" wording is
            // disjoint from PatternSketchEntities (tool 203, no "component" word) and from PatternComponent's
            // broad bare "pattern" verb, but must still be checked BEFORE the latter (specific-first).
            if (SketchDrivenPatternComponent.IsIntent(i)) return await SketchDrivenPatternComponent.Run(app, model, intent, emit);
            if (PatternComponent.IsPatternIntent(i)) return await PatternComponent.Run(app, model, intent, emit);
            // Specialized read/fix handlers FIRST — they must win over Scout's broad scan regex (which also matches
            // "diagnose"/"check"/"inspect") and over the plain Mirror/Batcher/Simplifier matchers.
            if (RedWave.IsFixIntent(i)) return await RedWave.Run(app, model, intent, emit);
            // SetFixed (fix/float components, WRITE) — AFTER RedWave so "fix the mate errors" stays red-wave; a
            // "fix everything / float all the parts" (no mate/error word) routes here.
            if (SetFixed.IsSetFixedIntent(i)) return await SetFixed.Run(app, model, intent, emit);
            // AutoNumberParts (WRITE — assign part numbers) BEFORE ValidateProps (READ — check part numbers) and Scout, so
            // "assign part numbers"/"number the parts"/"auto-number" routes to the fix, never to the read audit or scan regex.
            if (AutoNumberParts.IsAutoNumberPartsIntent(i)) return await AutoNumberParts.Run(app, model, intent, emit);
            // ValidateProps is the NARROWER properties/release check — must win over Doctor's broad audit so a
            // "check properties"/"missing materials" prompt is never swallowed by the doctor. First match wins.
            if (ValidateProps.IsValidatePropsIntent(i)) return await ValidateProps.Run(app, model, intent, emit);  // BEFORE Doctor
            // AuditToolbox BEFORE Doctor: "audit the toolbox fasteners" shares the word "audit" with Doctor's
            // health-audit intent, but the toolbox/fastener qualifier makes it the more specific match.
            if (AuditToolbox.IsAuditIntent(i)) return await AuditToolbox.Run(app, model, intent, emit);
            if (Doctor.IsDoctorIntent(i)) return await Doctor.Run(app, model, intent, emit);   // BEFORE Scout
            if (Interfere.IsInterfereIntent(i)) return await Interfere.Run(app, model, intent, emit);
            if (FindDupes.IsFindDupesIntent(i)) return await FindDupes.Run(app, model, intent, emit);
            // GeometryDefeature (PART geometry-strip, WRITE) BEFORE Simplifier so a "defeature / remove the small holes /
            // simplify this imported part" command isn't swallowed by feature-suppression Simplify (a no-op on dumb solids).
            if (GeometryDefeature.IsDefeatureIntent(i)) return await GeometryDefeature.Run(app, model, intent, emit);
            // ShellPart (PART hollow-out, WRITE) BEFORE WallThickness/Simplifier so a "shell"/"hollow" command isn't
            // swallowed by a read matcher. "shell to Nmm" = WRITE; "check wall thickness" = READ wall_thickness.
            if (ShellPart.IsShellIntent(i)) return await ShellPart.Run(app, model, intent, emit);
            // ScalePart (PART uniform geometry scale, WRITE). Distinct from resize/upsize: "scale 2x"/"shrink to 0.5"
            // is a WHOLE-PART geometry scale; "change M6 to M8" is a Toolbox FASTENER swap (Resizer) — no collision.
            if (ScalePart.IsScaleIntent(i)) return await ScalePart.Run(app, model, intent, emit);
            // CreateVariableFillet (tool 217, WRITE) — BEFORE the constant FilletChamfer (specific-first): it additionally
            // requires the "variable/varying/tapered/graduated" qualifier, so a plain "fillet Nmm" still routes to the
            // constant handler and only a variable-fillet phrasing reaches this one.
            if (CreateVariableFillet.IsIntent(i)) return await CreateVariableFillet.Run(app, model, intent, emit);
            // FilletChamfer (PART edge fillet/chamfer, WRITE) — a "fillet/round/chamfer/bevel/break … Nmm" command.
            // Disjoint verbs from defeature/simplify (excludes "suppress"); requires a fillet/round/chamfer/bevel/break verb.
            if (FilletChamfer.IsFilletChamferIntent(i)) return await FilletChamfer.Run(app, model, intent, emit);
            // CreateThread (PART cosmetic-thread WRITE) — "tap the holes M6 / add cosmetic threads / thread the bores".
            // Requires a thread/tap verb, so it never collides with fillet/defeature/shell/scale.
            if (CreateThread.IsCreateThreadIntent(i)) return await CreateThread.Run(app, model, intent, emit);
            // AddBoltCircle (PART: drill N holes equally spaced on a circle, WRITE) — MUST be checked BEFORE AddHole:
            // "put 5 bolt holes equally spaced on a 4.5 inch circle" also satisfies AddHole's broad add-verb+hole
            // match, but AddBoltCircle's narrower count+circle requirement makes it the more specific matcher
            // (specific-first). Distinct from the READ-ONLY MeasureBoltCircle (reports an EXISTING pattern, never
            // writes) — disjoint vocabularies: a count glued to "hole(s)" (ADD) vs "count/how many holes" (READ).
            if (AddBoltCircle.IsAddBoltCircleIntent(i)) return await AddBoltCircle.Run(app, model, intent, emit);
            // AddHole (PART: drill a through-hole, WRITE) — "add/drill/put/bore a hole" + optional size. ADDS a hole
            // (distinct from geometry_defeature which REMOVES holes).
            if (AddHole.IsAddHoleIntent(i)) return await AddHole.Run(app, model, intent, emit);
            // AddBoss (PART: add a boss/pad, WRITE) — "add/create/put a boss/pad/pillar/stud" + optional size. ADDS
            // material (distinct from add_hole which removes).
            if (AddBoss.IsAddBossIntent(i)) return await AddBoss.Run(app, model, intent, emit);
            // CreateWrap (PART: emboss/deboss a sketch profile onto a face, WRITE) — tool 211. "emboss/deboss a
            // circle onto the face". Keys on the emboss/deboss/engrave/scribe noun (or "wrap" + a face/sketch word),
            // so it never collides with add_boss/add_hole (different verbs).
            if (CreateWrap.IsIntent(i)) return await CreateWrap.Run(app, model, intent, emit);
            // AddCountersink (conical flat-head recess, WRITE) — checked BEFORE AddHole/AddCounterbore: fires only on the
            // "countersink/countersunk/csk/flat head" noun, so it never collides with plain holes or counterbores.
            if (AddCountersink.IsAddCountersinkIntent(i)) return await AddCountersink.Run(app, model, intent, emit);
            // AddCounterbore (stepped hole, WRITE) — checked BEFORE AddHole/AddPocket: "counterbore" is a specific noun and
            // AddHole would otherwise grab "add a ... hole"-ish phrasing. AddCounterbore only fires on the counterbore noun.
            if (AddCounterbore.IsAddCounterboreIntent(i)) return await AddCounterbore.Run(app, model, intent, emit);
            // AddPocket (mill a rectangular pocket/slot, WRITE) — PART only; before AddHole is irrelevant (distinct nouns).
            if (AddPocket.IsAddPocketIntent(i)) return await AddPocket.Run(app, model, intent, emit);
            // (PatternFeature is checked ahead of PatternComponent, below, so the component matcher can't shadow it.)
            // CreateRefPlane (insert an offset reference plane, WRITE) — reference geometry, part or assembly.
            if (CreateRefPlane.IsCreateRefPlaneIntent(i)) return await CreateRefPlane.Run(app, model, intent, emit);
            // SetDimension (set ONE named model dimension by plain English, WRITE). Broad "change/set/make … <number>"
            // matcher, but it self-excludes scale ("2x") and fastener swaps ("M6→M8"); placed AFTER the specific geometry
            // writes so those win, and it needs a standalone number so material/color verbs (no number) don't reach it.
            // ListEquations (READ-only report of equations/globals) — list/show/what + global word; before the write
            // equation handlers so "list the globals" is never treated as an add/edit/delete.
            if (ListEquations.IsListEquationsIntent(i)) return await ListEquations.Run(app, model, intent, emit);
            // DeleteEquation (remove an equation/global) — delete/remove/drop verb + global word; distinct from add/edit.
            if (DeleteEquation.IsDeleteEquationIntent(i)) return await DeleteEquation.Run(app, model, intent, emit);
            // AddEquation (create a global variable) — checked BEFORE EditEquation: add/create/define verb + global word;
            // EditEquation owns set/change/make/update, so the two don't collide.
            if (AddEquation.IsAddEquationIntent(i)) return await AddEquation.Run(app, model, intent, emit);
            // EditEquation (change an equation/global-variable value) — checked BEFORE set_dimension: its matcher REQUIRES
            // an equation/global/variable word, so it only fires when an equation is explicitly named and never steals a
            // plain "change the length to 100" dimension edit (which has no equation word → set_dimension).
            if (EditEquation.IsEditEquationIntent(i)) return await EditEquation.Run(app, model, intent, emit);
            // ConfigSpecificDimension (tool 90, WRITE) BEFORE set_dimension: both fire on "set <dim> to <n>", but this one
            // additionally demands a config word, so "set the depth to 30 in Variant-1" scopes per-config while
            // "set the depth to 30" stays with the active-config set_dimension.
            if (ConfigSpecificDimension.IsIntent(i)) return await ConfigSpecificDimension.Run(app, model, intent, emit);
            // EditFeatureParameter (tool 73, WRITE) BEFORE SetDimension: "change the depth of Boss-Extrude1 to 40mm" edits
            // the extrude feature-data directly; its matcher demands "depth" + an extrude reference so a bare
            // "set the depth to 30" (a named dimension) still falls through to set_dimension.
            if (EditFeatureParameter.IsIntent(i)) return await EditFeatureParameter.Run(app, model, intent, emit);
            // EditLastFeature (tool 236, WRITE) BEFORE SetDimension: SetDimension's matcher is broad (any verb + any
            // number), so "make it deeper by 5mm" would otherwise be swallowed as a bare dimension edit. Its own
            // matcher is narrow — the unique deeper/shallower vocabulary, used nowhere else in the dispatch table.
            if (EditLastFeature.IsIntent(i)) return await EditLastFeature.Run(app, model, intent, emit);
            if (SetDimension.IsSetDimensionIntent(i)) return await SetDimension.Run(app, model, intent, emit);
            // WallThickness (PART min-wall) BEFORE the broad Simplifier/Scout matchers so "thickness" isn't swallowed.
            if (WallThickness.IsWallThicknessIntent(i)) return await WallThickness.Run(app, model, intent, emit);
            // GetMassProps (mass/volume/COM READ) BEFORE Scout — "what does this weigh"/"mass properties" is not a scan.
            if (GetMassProps.IsMassPropsIntent(i)) return await GetMassProps.Run(app, model, intent, emit);
            // MeasureBoltCircle (bolt-circle/PCD/flange-class READ) BEFORE GetBoundingBox — "bolt circle"/"pcd"/
            // "class 150" is a specific pattern-geometry ask, not a generic size/footprint query.
            if (MeasureBoltCircle.IsBoltCircleIntent(i)) return await MeasureBoltCircle.Run(app, model, intent, emit);
            // CountNamedComponents (named-part-type count READ, e.g. "how many servos"/"count the rollers") BEFORE
            // GetBoundingBox — excludes hole/pattern/feature/face/edge/body/mate vocabulary so it can't shadow their
            // own specific handlers.
            if (CountNamedComponents.IsIntent(i)) return await CountNamedComponents.Run(app, model, intent, emit);
            // CountGearTeeth (gear tooth-count READ, e.g. "how many teeth"/"tooth count on both bevel gears") BEFORE
            // GetBoundingBox — geometry-only tooth count, missing-capability gap closed cycle 13.
            if (CountGearTeeth.IsIntent(i)) return await CountGearTeeth.Run(app, model, intent, emit);
            // HoleSpacing (hole-to-hole distance READ, e.g. "how far apart are the bolt holes") — a linear/pair
            // spacing question, disjoint vocabulary from MeasureBoltCircle's circular-pattern PCD phrasing above
            // and from GetBoundingBox's generic overall-size query below (test-loop wrong-answer
            // measure-mounting-hole-distance).
            if (HoleSpacing.IsHoleSpacingIntent(i)) return await HoleSpacing.Run(app, model, intent, emit);
            // MeshOpenings (opening-count-per-row READ, e.g. "openings across one row of the mesh") — disjoint
            // vocabulary from the others (test-loop wrong-answer count-mesh-cells).
            if (MeshOpenings.IsMeshOpeningsIntent(i)) return await MeshOpenings.Run(app, model, intent, emit);
            // ArcHeight (camber READ, e.g. "arc height of this spring") BEFORE GetBoundingBox — a location-specific
            // curve measurement, not a generic overall-size query (test-loop wrong-answer measure-arc-height).
            if (ArcHeight.IsArcHeightIntent(i)) return await ArcHeight.Run(app, model, intent, emit);
            // GetBoundingBox (overall size READ) BEFORE Scout — "footprint"/"how big"/"bounding box" is not a scan.
            if (GetBoundingBox.IsBoundingBoxIntent(i)) return await GetBoundingBox.Run(app, model, intent, emit);
            // CaptureViewport (tool 234, READ/export — the LLM's eyes) — "screenshot/snapshot/take a picture/show me
            // an image" renders the current view to a PNG. Never writes to the model; excludes "section" (tool 235).
            if (CaptureViewport.IsIntent(i)) return await CaptureViewport.Run(app, model, intent, emit);
            // CaptureSection (tool 235, READ/export) — screenshot with a live section cut, to verify internal
            // geometry (wall thickness, hole depths). Disjoint vocabulary from InsertSectionView (tool 104, DRAWING
            // sheets only, insert/add/create/cut + "view" word) and from CaptureViewport (excludes "section").
            if (CaptureSection.IsIntent(i)) return await CaptureSection.Run(app, model, intent, emit);
            // GetConfigs (list configurations READ) BEFORE Scout — "list the configs"/"which config" is not a scan.
            if (GetConfigs.IsConfigsIntent(i)) return await GetConfigs.Run(app, model, intent, emit);
            // GetFeatureTree (feature-tree summary READ) BEFORE Scout — "list the features"/"feature breakdown" is not a scan.
            if (GetFeatureTree.IsFeatureTreeIntent(i)) return await GetFeatureTree.Run(app, model, intent, emit);
            if (Profiler.IsProfileIntent(i)) return await Profiler.Run(app, model, intent, emit);
            // RebuildDocument (tool 95) AFTER Profiler/GetRebuildErrors/SetRebuildVerification/RedWave: a bare "rebuild
            // the model" (no error/verif/slow/fix word) is the recompute; its exclusions keep the specific ones ahead.
            if (RebuildDocument.IsIntent(i)) return await RebuildDocument.Run(app, model, intent, emit);
            if (Compare.IsCompareIntent(i)) return await Compare.Run(app, model, intent, null, emit);
            if (System.Text.RegularExpressions.Regex.IsMatch(i, @"\b(what breaks|what depends|impact|dependen|if i change|what happens if)\b"))
                return await Impact.Run(app, model, intent, emit);
            // TransformAssembly (move/rotate the WHOLE assembly as one rigid set, WRITE) BEFORE Mirror/Exploder. Guarded by
            // a whole-assembly scope word (whole/entire assembly | everything | to the origin) so a single-part move /
            // explode / mirror is never swallowed. "move the whole assembly up 100mm", "rotate everything 90 about Z".
            if (TransformAssembly.IsTransformIntent(i)) return await TransformAssembly.Run(app, model, intent, emit);
            // MoveComponent (translate ONE floating component, WRITE) AFTER TransformAssembly — fires on move/nudge/shift/
            // slide + a linear direction, and NEVER when a whole-assembly scope word is present (that's transform_assembly).
            if (MoveComponent.IsMoveComponentIntent(i)) return await MoveComponent.Run(app, model, intent, emit);
            // RotateComponent (rotate ONE floating component about its own centre, WRITE) — a rotate/turn/spin verb + an
            // angle, never a whole-assembly scope word (that's transform_assembly). "rotate the bolt 90 degrees about Z".
            if (RotateComponent.IsRotateComponentIntent(i)) return await RotateComponent.Run(app, model, intent, emit);
            // MirrorSketchEntities (tool 202, WRITE) — mirror a SKETCH entity (circle/line/segment) across a centerline.
            // Most specific of the mirror family (requires a sketch-entity noun), so it's checked first.
            if (MirrorSketchEntities.IsIntent(i)) return await MirrorSketchEntities.Run(app, model, intent, emit);
            // MirrorFeature (mirror a FEATURE on a part: hole/cut/boss) is checked BEFORE the component/body Mirror — it
            // is the more specific matcher (requires a feature noun AND excludes component/assembly words), so it never
            // shadows a component mirror, but Mirror's broad "\bmirror\b" would otherwise steal a feature intent.
            if (MirrorFeature.IsMirrorFeatureIntent(i)) return await MirrorFeature.Run(app, model, intent, emit);
            if (Mirror.IsMirrorIntent(i) && MirrorSkip.IsSkipIntent(null, intent)) return await MirrorSkip.Run(app, model, intent, emit);
            if (Mirror.IsMirrorIntent(i)) return await Mirror.Run(app, model, intent, emit);
            // RepairExplodedView (tool 193, WRITE) BEFORE Exploder: Exploder's \bexploded\b alone would otherwise
            // wrongly claim "repair the exploded view" — repair/reattach/fix vocabulary is the more specific matcher.
            if (RepairExplodedView.IsIntent(i)) return await RepairExplodedView.Run(app, model, intent, emit);
            if (Exploder.IsExplodeIntent(i)) return await Exploder.Run(app, model, intent, emit);
            if (KnitSurfacesToSolid.IsIntent(i)) return await KnitSurfacesToSolid.Run(app, model, intent, emit);
            if (ArrangeDrawingAnnotations.IsIntent(i)) return await ArrangeDrawingAnnotations.Run(app, model, intent, emit);
            // ManageDesignTable (tool 194, WRITE) — unique "design table" vocabulary, no ordering dependency with
            // any other matcher in this codebase.
            if (ManageDesignTable.IsIntent(i)) return await ManageDesignTable.Run(app, model, intent, emit);
            // FillSurface (tool 226, WRITE) — fill/patch + surface(s) vocabulary, disjoint from KnitSurfacesToSolid
            // (knit/stitch/sew + surfaces) and CreateThicken (thicken + surface), no ordering dependency.
            if (FillSurface.IsIntent(i)) return await FillSurface.Run(app, model, intent, emit);
            // DescribeGeometry (tool 237, READ) — describe/explain + face/geometry/shape/surface vocabulary,
            // disjoint from GetSelectedEntities (what/show/list/get/tell + selected) and GetFeatureInfo (named
            // feature-tree parameters like "depth of Boss-Extrude1"), no ordering dependency.
            if (DescribeGeometry.IsIntent(i)) return await DescribeGeometry.Run(app, model, intent, emit);
            // HighlightEntities (tool 238, WRITE-of-state) — highlight/flash/light up + face/hole/bore vocabulary,
            // disjoint from DescribeGeometry (describe/explain) and SelectFace (select + face, no highlight/flash
            // verb), no ordering dependency.
            if (HighlightEntities.IsIntent(i)) return await HighlightEntities.Run(app, model, intent, emit);
            // Isolator intentionally NOT harness-routed: its visibility ops (HideComponent2 / Component2.Visible)
            // HANG the add-in headlessly on this 3DEXPERIENCE build and wedged the SW launcher. Panel-only for now.
            // Drawing/DXF writers BEFORE the broad simplify/batch matchers so "rebuild drawings" isn't swallowed.
            // BatchExportDrawings (tool 134, WRITE) requires the explicit drawing noun AND dxf/dwg format — checked
            // BEFORE FlatDxf because FlatDxf's own matcher (dxf/dwg + export-verb) doesn't check for a drawing noun
            // at all and would otherwise misroute a drawing-scoped DXF request into its sheet-metal flat-pattern
            // scan (which only applies to a PART/ASSEMBLY, never a drawing). DrawingPkg already excludes dxf/dwg
            // wording itself, so there's no three-way ambiguity — only Forge vs FlatDxf needed resolving.
            if (BatchExportDrawings.IsIntent(i)) return await BatchExportDrawings.Run(app, model, intent, emit);
            if (FlatDxf.IsFlatDxfIntent(i)) return await FlatDxf.Run(app, model, intent, emit);   // more specific (dxf/flat-pattern) -> before DrawingPkg
            if (DrawingPkg.IsDrawingPkgIntent(i)) return await DrawingPkg.Run(app, model, intent, emit);
            // Upsize (M6->M8): two distinct M-sizes + a swap verb, so it doesn't shadow single-size resize intents.
            if (System.Text.RegularExpressions.Regex.Matches(i, @"\bm(\d+)\b").Count >= 2 &&
                System.Text.RegularExpressions.Regex.IsMatch(i, @"\b(upsize|replace|swap|change|convert|to)\b"))
                return await Upsize.Run(app, model, intent, emit);
            // ApplyAppearance BEFORE Materializer/Batcher — "color by material" contains the word "material" (Materializer
            // would otherwise swallow it), and "color all the components red" must beat batch print-prep. Keyword-guarded
            // (color/colour/paint/appearance, or a bare color word); the intent layer is primary.
            if (ApplyAppearance.IsAppearanceIntent(i)) return await ApplyAppearance.Run(app, model, intent, emit);
            // SuppressComponents BEFORE Batcher/Simplifier/Scout — "suppress all the components"/"strip the assembly" must
            // not fall through to batch print-prep or the scan regex. Keyword-guarded (suppress|strip); intent layer is primary.
            // SuppressFeature (PART feature suppress/unsuppress in the ACTIVE config, WRITE) BEFORE SuppressComponents/
            // Simplifier. PART-guarded so an assembly component-suppress still reaches SuppressComponents below.
            // RenameFeature (metadata write on a PART) — checked before SuppressFeature; "rename X to Y" won't match the
            // suppress verb, but keep it adjacent so the feature-write family is contiguous.
            if (model is PartDoc && RenameFeature.IsRenameFeatureIntent(i)) return await RenameFeature.Run(app, model, intent, emit);
            // ReorderFeature (PART: move a feature earlier/later in the tree, WRITE) — PART-guarded, adjacent to the other
            // feature-tree writes. Needs a positional word (before/after) to fire on "move", so it can't grab move_face
            // or a component move; "reorder ..." alone is specific enough. Sits before SuppressFeature (no verb overlap).
            if (model is PartDoc && ReorderFeature.IsReorderFeatureIntent(i)) return await ReorderFeature.Run(app, model, intent, emit);
            // ConfigFeatureSuppression (tool 91, WRITE) BEFORE SuppressFeature: both fire on "suppress <feature>", but this
            // one additionally demands an explicit config word, so "suppress the hole in Variant-1" scopes per-config while
            // "suppress the hole" stays with the active-config suppress_feature.
            if (model is PartDoc && ConfigFeatureSuppression.IsIntent(i)) return await ConfigFeatureSuppression.Run(app, model, intent, emit);
            if (model is PartDoc && SuppressFeature.IsSuppressFeatureIntent(i)) return await SuppressFeature.Run(app, model, intent, emit);
            // DeleteReplaceFace (tool 227, WRITE) BEFORE DeleteFeature: requires the explicit "face" noun ("delete
            // the fillet FACE" / "remove the top FACE"), so it's the more specific match against a B-rep-level ask
            // — DeleteFeature's own "fillet/round/chamfer/..." target regex doesn't require "face" and would
            // otherwise also fire and try (wrongly) to delete a parametric feature instead of a single face.
            if (DeleteReplaceFace.IsIntent(i)) return await DeleteReplaceFace.Run(app, model, intent, emit);
            // RunDfmChecks (tool 180, READ, PART) — narrow "dfm"/"machinability"/"non-standard hole size"/"deep
            // narrow hole" vocabulary, no overlap with CountThroughHoles/HoleSpacing/AddHole's plain "hole" asks.
            if (model is PartDoc && RunDfmChecks.IsIntent(i)) return await RunDfmChecks.Run(app, model, intent, emit);
            // ExportBom (tool 191, WRITE, ASSEMBLY) — export/save/write verb only, so InsertBomTable's own
            // insert/add/create/generate/make verb set never shadows it (or vice versa); doc-type gate (assembly
            // vs drawing) is the second, independent disambiguator.
            if (model is AssemblyDoc && ExportBom.IsIntent(i)) return await ExportBom.Run(app, model, intent, emit);
            // DeleteFeature (PART: permanently delete features by type/name, WRITE) — PART-guarded. "delete the fillets"
            // = permanent delete (vs suppress = reversible, vs geometry_defeature = face-delete). DeleteSelection2 no-prompt.
            if (model is PartDoc && DeleteFeature.IsDeleteFeatureIntent(i)) return await DeleteFeature.Run(app, model, intent, emit);
            // UnsuppressComponents (re-activate suppressed components) — checked BEFORE SuppressComponents: the suppress
            // matcher uses \bsuppress\b which does NOT fire inside "unsuppress", but "restore/bring back the parts" must
            // route here, so keep unsuppress first.
            if (UnsuppressComponents.IsUnsuppressIntent(i)) return await UnsuppressComponents.Run(app, model, intent, emit);
            if (SuppressComponents.IsSuppressIntent(i)) return await SuppressComponents.Run(app, model, intent, emit);
            if (Batcher.IsBatchIntent(i)) return await Batcher.Run(app, model, intent, emit);
            if (Simplifier.IsSimplifyIntent(i)) return await Simplifier.Run(app, model, intent, emit);
            if (Materializer.IsMaterialIntent(i)) return await Materializer.Run(app, model, intent, emit);
            if (Scout.IsScanIntent(i)) return await Scout.Run(app, model, emit);
            await emit("Harness", null, "done", "no handler for intent: " + intent);
            return null;
        }

        // Intent-driven dispatch: parse the raw prompt through the AI intent layer, route by the parsed action.
        // Wires the data/robustness spine: name-masked event log (Rule #12), crash lockfile, per-run timing.
        private static async Task<object> IntentDispatch(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            int comp, fast, mate; var names = InventoryFor(model, out comp, out fast, out mate);
            string masked = ForgeData.Mask(intent, names);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            ForgeData.RunBegin("intent", masked, comp);

            await emit("Intent", "understanding the command", "run", null);
            GroundTruth.Trace?.Invoke("intent-parse: calling IntentLayer.Parse");
            var plan = await IntentLayer.Parse(intent, model);
            GroundTruth.Trace?.Invoke("intent-parse: IntentLayer.Parse returned " + plan.Operations.Count + " op(s), err=" + (plan.Error ?? "(none)"));
            if (plan.Error != null)
            {
                await emit("Intent", null, "fail", plan.Error);
                ForgeData.LogEvent("intent", masked, plan, "error", false, 0, comp, fast, mate, "harness", sw.ElapsedMilliseconds, "parse");
                ForgeData.RunEnd(0, 0); return plan;
            }
            string action = plan.Operations.Count > 0 ? plan.Operations[0].Action : "(none)";
            await emit("Intent", null, "done", "parsed " + plan.Operations.Count + " op(s), action=" + action + ", conf=" + plan.Confidence.ToString("F2") + (plan.Ambiguities.Count > 0 ? ", ambiguities=" + plan.Ambiguities.Count : ""));

            // test-loop wrong-route fix (flange-14-pressure-300psi, the regression corpus): a pressure/burst-rating
            // question ("is this rated for 300 psi?") has no geometric answer, whatever action the cloud guessed —
            // state the honest limit BEFORE any routing, not after a misrouted handler runs and ignores the question.
            if (HonestLimits.IsPressureRatingQuestion(intent))
            {
                await emit("Intent", null, "done", "pressure-rating question — stating the capability limit, not routing");
                ForgeData.LogEvent("(honest-limit)", masked, plan, "asked", false, comp, comp, fast, mate, "harness", sw.ElapsedMilliseconds, null);
                ForgeData.RunEnd(0, comp);
                return new { NeedsConfirm = true, Question = HonestLimits.PressureRatingLimitMessage, Error = (string)null };
            }

            // test-loop wrong-route fix (flange-17-why-leaking, the regression corpus): "flange
            // leakin' - is the gasket surface right or bolts too loose?" has no geometric answer whatever action
            // the cloud guessed — state the honest limit BEFORE any routing, same mechanism as the pressure check.
            if (HonestLimits.IsLeakDiagnosisQuestion(intent))
            {
                await emit("Intent", null, "done", "leak-diagnosis question — stating the capability limit, not routing");
                ForgeData.LogEvent("(honest-limit)", masked, plan, "asked", false, comp, comp, fast, mate, "harness", sw.ElapsedMilliseconds, null);
                ForgeData.RunEnd(0, comp);
                return new { NeedsConfirm = true, Question = HonestLimits.LeakDiagnosisLimitMessage, Error = (string)null };
            }

            // test-loop wrong-answer fix (flange-13-weld-onto-tube): "weld the flange to the tube end" has no action
            // in the cloud's vocabulary, so it guessed the closest one — mate — and silently bolted a fastener
            // instead of doing (or honestly declining) what was actually asked. Same mechanism as the two
            // HonestLimits guards above, same guard as ForgePanel.Pipeline.cs RunViaPipeline, kept in sync.
            if (HonestLimits.IsWeldRequest(intent))
            {
                await emit("Intent", null, "done", "weld request — stating the capability limit, not routing to mate");
                ForgeData.LogEvent("(honest-limit)", masked, plan, "asked", false, comp, comp, fast, mate, "harness", sw.ElapsedMilliseconds, null);
                ForgeData.RunEnd(0, comp);
                return new { NeedsConfirm = true, Question = HonestLimits.WeldLimitMessage, Error = (string)null };
            }

            // test-loop hedged cluster fix (create-enclosure, add-motor-mount, design-ball, compose-into-cable-
            // assembly, combine-with-linear-rail, chain-walk-cycle): a generative-synthesis or motion-simulation
            // ask has no action in the cloud's vocabulary (0 ops), so the zero-op fallback below used to ask a
            // vague "what would you like me to do?" instead of naming the actual limit. Same mechanism as the
            // HonestLimits guards above, same guard as ForgePanel.Pipeline.cs RunViaPipeline, kept in sync.
            if (HonestLimits.IsGenerativeSynthesisRequest(intent))
            {
                await emit("Intent", null, "done", "generative-synthesis/motion request — stating the capability limit, not a vague ask");
                ForgeData.LogEvent("(honest-limit)", masked, plan, "asked", false, comp, comp, fast, mate, "harness", sw.ElapsedMilliseconds, null);
                ForgeData.RunEnd(0, comp);
                return new { NeedsConfirm = true, Question = HonestLimits.GenerativeSynthesisLimitMessage, Error = (string)null };
            }

            // test-loop unclear fix (hull-vague-improve): a purely subjective aesthetic ask ("more sleek", "logo pop
            // more", "looks off") carries enough words for the cloud to parse low-confidence ops and silently run
            // the WRONG action instead of asking what "better" concretely means — this scenario's own expected
            // behavior IS to clarify, not act. Same mechanism as the HonestLimits guards above, same guard as
            // ForgePanel.Pipeline.cs RunViaPipeline, kept in sync.
            if (HonestLimits.IsVagueAestheticRequest(intent))
            {
                await emit("Intent", null, "done", "vague aesthetic request — asking what 'better' means, not guessing an action");
                ForgeData.LogEvent("(honest-limit)", masked, plan, "asked", false, comp, comp, fast, mate, "harness", sw.ElapsedMilliseconds, null);
                ForgeData.RunEnd(0, comp);
                return new { NeedsConfirm = true, Question = HonestLimits.VagueAestheticClarifyMessage, Error = (string)null };
            }

            // test-loop hedged fix (replace-battery): "need a bigger battery, about 1.5 times the volume of the
            // current one" on an assembly parses LOW confidence (0.45) and 0 ops from the cloud, so it falls to
            // the zero-op fallback's clarifying question instead of attempting the scale. A digit-"times"-"volume"
            // phrasing is narrow and unambiguous enough to route authoritatively before the cloud's own zero-op
            // path, same shape as the guards above (and mirrored in ForgePanel.Pipeline.cs RunViaPipeline for the
            // live UI path). ScalePart.Run itself resolves a NAMED sub-component ("battery") to its own PartDoc
            // when given an assembly, and reads "N times the volume" as a volume ratio (cube-root to a linear
            // factor), not a literal linear scale.
            if (System.Text.RegularExpressions.Regex.IsMatch(intent ?? "", @"\d+(\.\d+)?\s*times\b.{0,25}\bvolume\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                await emit("Intent", null, "done", "volume-ratio scale request — routing to scale_part authoritatively");
                var sr = await ScalePart.Run(app, model, intent, emit);
                ForgeData.LogEvent("scale_part", masked, plan, sr.NeedsConfirm ? "asked" : (sr.Error != null ? "error" : "executed"), sr.Verified, comp, comp, fast, mate, "harness", sw.ElapsedMilliseconds, sr.Error);
                ForgeData.RunEnd(0, comp);
                return sr;
            }

            // test-loop wrong-route fix (count-faces, the regression corpus): "how many faces on
            // this surface?" was misrouted to list_features because get_faces has no registered live-pipeline
            // spec — override AUTHORITATIVELY regardless of what the cloud guessed, same mechanism as the two
            // HonestLimits guards above, same guard as ForgePanel.Pipeline.cs RunViaPipeline, kept in sync.
            if (GetFaces.IsIntent(intent))
            {
                var gf = await GetFaces.Run(app, model, intent, emit);
                bool gfOk = gf.Error == null;
                await emit("Intent", null, "done", "routed to get_faces (authoritative override)");
                ForgeData.LogEvent("get_face_normal", masked, plan, gfOk ? "executed" : "error", gfOk, comp, comp, fast, mate, "harness", sw.ElapsedMilliseconds, gfOk ? null : "get_face_normal");
                ForgeData.RunEnd(gfOk ? comp : 0, comp);
                return gf;
            }

            // Same live-dispatch gap as get_faces above: GetEdges (get_edge_length) has no registered live-pipeline
            // spec either — override AUTHORITATIVELY, kept in sync with ForgePanel.Pipeline.cs RunViaPipeline.
            if (GetEdges.IsIntent(intent))
            {
                var ge = await GetEdges.Run(app, model, intent, emit);
                bool geOk = ge.Error == null;
                await emit("Intent", null, "done", "routed to get_edge_length (authoritative override)");
                ForgeData.LogEvent("get_edge_length", masked, plan, geOk ? "executed" : "error", geOk, comp, comp, fast, mate, "harness", sw.ElapsedMilliseconds, geOk ? null : "get_edge_length");
                ForgeData.RunEnd(geOk ? comp : 0, comp);
                return ge;
            }

            // test-loop hedged finding (flange-02-count-holes, the regression corpus): "tell me the number of
            // bolt holes in this assembly" / "whats the hole pattern on the leg bottoms" reach the cloud, which
            // has no bolt-circle/hole-pattern action in its vocabulary (same gap as flange-bolt-circle-class) —
            // MeasureBoltCircle.IsBoltCircleIntent already covers "bolt circle"/"pcd"/"class N" but not these
            // "number of holes"/"hole pattern" phrasings, and it had no authoritative pre-cloud override here
            // (only wired into the offline Dispatch() used by useIntent:false tests) — so the live useIntent:true
            // path the test-loop actually exercises fell through to the legacy clarify-question path. Broadened the
            // regex AND added the override, same mechanism as GetFaces/GetEdges/GetBodies/GetMaterial above.
            // MUST run BEFORE GetBodies below: test-loop wrong-route fix (count-mounting-holes-3kw) — "how many
            // mounting holes are on the 3 kW motor BODY?" incidentally contains "body", so GetBodies.IsIntent
            // (which only checks for the word body/bodies + a count word, no hole exclusion) was winning the race
            // and returning a plain solid-body count instead of the hole count actually asked for. Specific-first.
            if (MeasureBoltCircle.IsBoltCircleIntent(intent))
            {
                var mb = await MeasureBoltCircle.Run(app, model, intent, emit);
                bool mbOk = mb.Error == null;
                await emit("Intent", null, "done", "routed to measure_bolt_circle (authoritative override)");
                ForgeData.LogEvent("measure_bolt_circle", masked, plan, mbOk ? "executed" : "error", mbOk, comp, comp, fast, mate, "harness", sw.ElapsedMilliseconds, mbOk ? null : "measure_bolt_circle");
                ForgeData.RunEnd(mbOk ? comp : 0, comp);
                return mb;
            }

            // test-loop hedged fix (rim-add-bolt-holes): "put 5 bolt holes equally spaced on a 4.5 inch circle" has no
            // add_bolt_circle action in the cloud's vocabulary — it parses the closest known one (add_hole, conf
            // 0.75), which only ever drills ONE hole at a face's centre, so the request silently fails ("couldn't
            // find a planar face" / no geometry change). MUST run BEFORE MeasureBoltCircle would matter (it doesn't
            // collide — see AddBoltCircle.IsAddBoltCircleIntent) and authoritatively regardless of what the cloud
            // guessed, same mechanism as the overrides above, kept in sync with ForgePanel.Pipeline.cs RunViaPipeline.
            if (AddBoltCircle.IsAddBoltCircleIntent(intent))
            {
                var abc = await AddBoltCircle.Run(app, model, intent, emit);
                bool abcOk = abc.Error == null;
                await emit("Intent", null, "done", "routed to add_bolt_circle (authoritative override)");
                ForgeData.LogEvent("add_bolt_circle", masked, plan, abcOk ? "executed" : "error", abc.Verified, comp, comp, fast, mate, "harness", sw.ElapsedMilliseconds, abcOk ? null : "add_bolt_circle");
                ForgeData.RunEnd(abcOk ? comp : 0, comp);
                return abc;
            }

            // test-loop wrong-answer fix (count-servos/count-rollers, the regression corpus): "how many of those
            // servo things are in the robot?" / "count the rollers in this bearing" has no named-part-type-count
            // action in the cloud's vocabulary, so it lands on a generic fallback (list_bodies/list_features) that
            // answers a different, broader question. Same authoritative-override mechanism as GetFaces/GetEdges/
            // GetBodies/GetMaterial/MeasureBoltCircle above, kept in sync with ForgePanel.Pipeline.cs RunViaPipeline.
            // Also MUST run before GetBodies: its own IsIntent already excludes body/bodies vocabulary so it can't
            // shadow list_bodies itself, but placed here to stay next to MeasureBoltCircle (same specific-first shape).
            if (CountNamedComponents.IsIntent(intent))
            {
                var cn = await CountNamedComponents.Run(app, model, intent, emit);
                bool cnOk = cn.Error == null;
                await emit("Intent", null, "done", "routed to count_named_components (authoritative override)");
                ForgeData.LogEvent("count_named_components", masked, plan, cnOk ? "executed" : "error", cnOk, comp, comp, fast, mate, "harness", sw.ElapsedMilliseconds, cnOk ? null : "count_named_components");
                ForgeData.RunEnd(cnOk ? comp : 0, comp);
                return cn;
            }

            // Missing-capability gap (count-gear-teeth/change-dual-bevel-gear-teeth/count-sun-teeth from the regression corpus
            // regression corpus): "check tooth count on both bevel gears"/"sun gear teeth count?" had no handler at
            // all. Same authoritative-override mechanism as CountNamedComponents above.
            if (CountGearTeeth.IsIntent(intent))
            {
                var gt = await CountGearTeeth.Run(app, model, intent, emit);
                bool gtOk = gt.Error == null;
                await emit("Intent", null, "done", "routed to count_gear_teeth (authoritative override)");
                ForgeData.LogEvent("count_gear_teeth", masked, plan, gtOk ? "executed" : "error", gtOk, comp, comp, fast, mate, "harness", sw.ElapsedMilliseconds, gtOk ? null : "count_gear_teeth");
                ForgeData.RunEnd(gtOk ? comp : 0, comp);
                return gt;
            }

            // test-loop wrong-answer fix (measure-thickness-center): a plain "how thick is the metal in the middle of
            // that curve?" has no action in the cloud parser's vocabulary at all, so it declined outright instead of
            // routing to wall_thickness. Same authoritative-override mechanism as MeasureBoltCircle/CountNamedComponents
            // above, kept in sync with ForgePanel.Pipeline.cs RunViaPipeline.
            if (WallThickness.IsStandaloneThicknessQuestion(intent))
            {
                var wt = await WallThickness.Run(app, model, intent, emit);
                bool wtOk = wt.Error == null;
                await emit("Intent", null, "done", "routed to wall_thickness (authoritative override)");
                ForgeData.LogEvent("wall_thickness", masked, plan, wtOk ? "executed" : "error", wtOk, comp, comp, fast, mate, "harness", sw.ElapsedMilliseconds, wtOk ? null : "wall_thickness");
                ForgeData.RunEnd(wtOk ? comp : 0, comp);
                return wt;
            }

            // test-loop wrong-answer fix (grinder-count-through-holes): "how many holes go all the way through the
            // part" has no action in the cloud parser's vocabulary at all, so it fell to list_features. Same
            // authoritative-override mechanism as WallThickness above, kept in sync with ForgePanel.Pipeline.cs.
            if (CountThroughHoles.IsIntent(intent))
            {
                var th = await CountThroughHoles.Run(app, model, intent, emit);
                bool thOk = th.Error == null;
                await emit("Intent", null, "done", "routed to count_through_holes (authoritative override)");
                ForgeData.LogEvent("count_through_holes", masked, plan, thOk ? "executed" : "error", thOk, comp, comp, fast, mate, "harness", sw.ElapsedMilliseconds, thOk ? null : "count_through_holes");
                ForgeData.RunEnd(thOk ? comp : 0, comp);
                return th;
            }

            // test-loop wrong-answer fix (count-mesh-cells): "count of openings across one row of the mesh" has no
            // action in the cloud parser's vocabulary at all, so it fell to a generic assembly scan. Same
            // authoritative-override mechanism as WallThickness above, kept in sync with ForgePanel.Pipeline.cs.
            if (MeshOpenings.IsMeshOpeningsIntent(intent))
            {
                var mo = await MeshOpenings.Run(app, model, intent, emit);
                bool moOk = mo.Error == null;
                await emit("Intent", null, "done", "routed to mesh_openings (authoritative override)");
                ForgeData.LogEvent("mesh_openings", masked, plan, moOk ? "executed" : "error", moOk, comp, comp, fast, mate, "harness", sw.ElapsedMilliseconds, moOk ? null : "mesh_openings");
                ForgeData.RunEnd(moOk ? comp : 0, comp);
                return mo;
            }

            // test-loop wrong-answer fix (measure-mounting-hole-distance): "how far apart are the bolt holes?" has no
            // action in the cloud parser's vocabulary at all, so it declined outright instead of routing to a real
            // hole-to-hole spacing measurement. Distinct from MeasureBoltCircle (assumes a CIRCULAR pattern). Same
            // authoritative-override mechanism as WallThickness above, kept in sync with ForgePanel.Pipeline.cs.
            if (HoleSpacing.IsHoleSpacingIntent(intent))
            {
                var hs = await HoleSpacing.Run(app, model, intent, emit);
                bool hsOk = hs.Error == null;
                await emit("Intent", null, "done", "routed to hole_spacing (authoritative override)");
                ForgeData.LogEvent("hole_spacing", masked, plan, hsOk ? "executed" : "error", hsOk, comp, comp, fast, mate, "harness", sw.ElapsedMilliseconds, hsOk ? null : "hole_spacing");
                ForgeData.RunEnd(hsOk ? comp : 0, comp);
                return hs;
            }

            // test-loop wrong-answer fix (measure-arc-height): "arc height of this spring?" has no action in the cloud
            // parser's vocabulary at all, so it declined outright instead of routing to a real camber measurement.
            // Same authoritative-override mechanism as WallThickness above, kept in sync with
            // ForgePanel.Pipeline.cs RunViaPipeline.
            if (ArcHeight.IsArcHeightIntent(intent))
            {
                var ah = await ArcHeight.Run(app, model, intent, emit);
                bool ahOk = ah.Error == null;
                await emit("Intent", null, "done", "routed to arc_height (authoritative override)");
                ForgeData.LogEvent("arc_height", masked, plan, ahOk ? "executed" : "error", ahOk, comp, comp, fast, mate, "harness", sw.ElapsedMilliseconds, ahOk ? null : "arc_height");
                ForgeData.RunEnd(ahOk ? comp : 0, comp);
                return ah;
            }

            if (GetBodies.IsIntent(intent))
            {
                var gb = await GetBodies.Run(app, model, intent, emit);
                bool gbOk = gb.Error == null;
                await emit("Intent", null, "done", "routed to list_bodies (authoritative override)");
                ForgeData.LogEvent("list_bodies", masked, plan, gbOk ? "executed" : "error", gbOk, comp, comp, fast, mate, "harness", sw.ElapsedMilliseconds, gbOk ? null : "list_bodies");
                ForgeData.RunEnd(gbOk ? comp : 0, comp);
                return gb;
            }

            // test-loop wrong-answer fix (count-clamping-positions, the regression corpus): "what's
            // the max parts this fixture can take" has no capacity action in the cloud's vocabulary, so it fell
            // through to a generic scan that only reported the CURRENT component count (3), not the fixture's
            // capacity (6, the dominant duplicate-body group on the real "GC Vise Fixture for 6 Round or Square
            // Pieces" part). Same authoritative-override mechanism as GetBodies/CountNamedComponents above.
            if (GetFixtureCapacity.IsIntent(intent))
            {
                var fc = await GetFixtureCapacity.Run(app, model, intent, emit);
                bool fcOk = fc.Error == null;
                await emit("Intent", null, "done", "routed to get_fixture_capacity (authoritative override)");
                ForgeData.LogEvent("get_fixture_capacity", masked, plan, fcOk ? "executed" : "error", fcOk, comp, comp, fast, mate, "harness", sw.ElapsedMilliseconds, fcOk ? null : "get_fixture_capacity");
                ForgeData.RunEnd(fcOk ? comp : 0, comp);
                return fc;
            }

            // Same live-dispatch gap: GetMaterial (get_material) has no registered spec either. Its own IsIntent
            // already excludes set/change/apply/density phrasing so it can't shadow set_material or
            // get_material_density.
            if (GetMaterial.IsIntent(intent))
            {
                var gm = await GetMaterial.Run(app, model, intent, emit);
                bool gmOk = gm.Error == null;
                await emit("Intent", null, "done", "routed to get_material (authoritative override)");
                ForgeData.LogEvent("get_material", masked, plan, gmOk ? "executed" : "error", gmOk, comp, comp, fast, mate, "harness", sw.ElapsedMilliseconds, gmOk ? null : "get_material");
                ForgeData.RunEnd(gmOk ? comp : 0, comp);
                return gm;
            }

            // Same live-dispatch gap: ListMates (list_mates) has no registered spec either. Run() self-guards
            // (assembly-only). IsListMatesIntent excludes fix/repair/add/delete so it can't shadow fix_red_wave.
            if (ListMates.IsListMatesIntent(intent))
            {
                var lm = await ListMates.Run(app, model, intent, emit);
                bool lmOk = lm.Error == null;
                await emit("Intent", null, "done", "routed to list_mates (authoritative override)");
                ForgeData.LogEvent("list_mates", masked, plan, lmOk ? "executed" : "error", lmOk, comp, comp, fast, mate, "harness", sw.ElapsedMilliseconds, lmOk ? null : "list_mates");
                ForgeData.RunEnd(lmOk ? comp : 0, comp);
                return lm;
            }

            // Same live-dispatch gap: GetComponentInfo (get_component_info) has no registered spec either.
            // Run() self-guards (assembly-only). Checked before list_components would be (still unwired).
            if (GetComponentInfo.IsIntent(intent))
            {
                var ci = await GetComponentInfo.Run(app, model, intent, emit);
                bool ciOk = ci.Error == null;
                await emit("Intent", null, "done", "routed to get_component_info (authoritative override)");
                ForgeData.LogEvent("get_component_info", masked, plan, ciOk ? "executed" : "error", ciOk, comp, comp, fast, mate, "harness", sw.ElapsedMilliseconds, ciOk ? null : "get_component_info");
                ForgeData.RunEnd(ciOk ? comp : 0, comp);
                return ci;
            }

            // Same live-dispatch gap, this time a WRITE: RenameComponent (rename_component) has no registered
            // live-pipeline spec either — override AUTHORITATIVELY, kept in sync with ForgePanel.Pipeline.cs
            // RunViaPipeline. The handler resolves its own target and asks ONE question via Error on 0/many
            // matches (Rule #2), so no extra confirm gate is needed here.
            if (RenameComponent.IsIntent(intent))
            {
                var rc = await RenameComponent.Run(app, model, intent, emit);
                bool rcOk = rc.Error == null;
                await emit("Intent", null, "done", "routed to rename_component (authoritative override)");
                ForgeData.LogEvent("rename_component", masked, plan, rcOk ? "executed" : "error", rcOk, comp, comp, fast, mate, "harness", sw.ElapsedMilliseconds, rcOk ? null : "rename_component");
                ForgeData.RunEnd(rcOk ? comp : 0, comp);
                return rc;
            }

            // test-loop hedge fix (change-green-button-color, the regression corpus): a PART document has no
            // sub-components for the cloud's ambiguity check to search against, so a descriptor of the part ITSELF
            // ("the green button's color") — not a sub-target — makes the cloud bail with 0 ops + "this is a part
            // file, not an assembly" instead of just coloring the open part. ApplyAppearance.RunPart already does
            // exactly that (colors the whole part, ignoring any named sub-target — a single part has nothing else
            // it could mean). Authoritative override, PART-only: assemblies keep going through the normal
            // cloud/HSpec path unchanged (already correctly routes there, e.g. hull-color-live-route).
            if ((model as AssemblyDoc) == null && !GetAppearance.IsIntent(intent) && ApplyAppearance.IsAppearanceIntent(intent))
            {
                var aa = await ApplyAppearance.Run(app, model, intent, emit);
                bool aaOk = aa.Error == null;
                await emit("Intent", null, "done", "routed to apply_appearance (authoritative override, part doc)");
                ForgeData.LogEvent("apply_appearance", masked, plan, aaOk ? "executed" : "error", aaOk, comp, comp, fast, mate, "harness", sw.ElapsedMilliseconds, aaOk ? null : "apply_appearance");
                ForgeData.RunEnd(aaOk ? comp : 0, comp);
                return aa;
            }

            // test-loop hedged findings change-roller-taper-angle / change-inner-ring-bore: the LIVE panel
            // (ForgePanel.Pipeline.cs RunViaPipeline) already retries SetDimension.IsSetDimensionIntent on a
            // zero-op cloud parse via TryLocalFallback/LocalActionFor — but THIS file's IntentDispatch had no
            // equivalent retry, so the regression harness could never exercise that live behavior for a
            // set_dimension-shaped ask the cloud fails to parse at all (it fell straight to the generic zero-op
            // ambiguity below instead). Mirror the same last-resort retry here. SetDimension's own matcher is
            // broad (verb + a number), so — same as LocalActionFor — this MUST run last, only once the cloud has
            // already failed to parse anything; SetDimension.Run itself now also resolves a NAMED sub-component on
            // an assembly (same technique as ScalePart's replace-battery fix) so this actually opens and edits the
            // named part instead of just asking a nicer question.
            if (plan.Operations.Count == 0 && SetDimension.IsSetDimensionIntent(intent))
            {
                var sdr = await SetDimension.Run(app, model, intent, emit);
                bool sdOk = sdr.Error == null;
                await emit("Intent", null, "done", "routed to set_dimension (authoritative override, zero-op retry)");
                ForgeData.LogEvent("set_dimension", masked, plan, sdOk ? "executed" : "error", sdr.Verified, comp, comp, fast, mate, "harness", sw.ElapsedMilliseconds, sdOk ? null : "set_dimension");
                ForgeData.RunEnd(sdOk ? comp : 0, comp);
                return sdr;
            }

            // Rule #2: zero parsed operations is a genuine ambiguity, not a silent no-op (test-loop no-change finding
            // flange-07-blank-off, the regression corpus: the parser found 0 ops + 3 ambiguities and
            // this switch's `default:` case just logged "no intent-executor for '(none)'" and did/said NOTHING to the
            // user — a hard fail distinct from "asked" or "refused"). Surface the parser's own ambiguity — or a
            // generic fallback if it didn't produce one — as ONE honest clarifying question instead.
            if (plan.Operations.Count == 0)
            {
                string q = (plan.Ambiguities != null && plan.Ambiguities.Count > 0)
                    ? plan.Ambiguities[0]
                    : "I didn't catch a specific action in that — what would you like me to do?";
                await emit("Intent", null, "done", "0 ops parsed — asking: " + q);
                ForgeData.LogEvent("(none)", masked, plan, "asked", false, comp, comp, fast, mate, "harness", sw.ElapsedMilliseconds, null);
                ForgeData.RunEnd(0, comp);
                return new { NeedsConfirm = true, Question = q, Error = (string)null };
            }

            var firstOp = plan.Operations.Count > 0 ? plan.Operations[0] : null;
            GroundTruth.Trace?.Invoke("chain leg 1/" + plan.Operations.Count + " (" + action + ") dispatch start");
            var first = await DispatchOp(app, model, WrapOp(firstOp, plan), firstOp, action, intent, emit);
            GroundTruth.Trace?.Invoke("chain leg 1/" + plan.Operations.Count + " (" + action + ") dispatch done, verified=" + first.Verified);
            if (plan.Operations.Count <= 1)
            {
                ForgeData.LogEvent(action, masked, plan, first.Outcome, first.Verified, comp, comp, fast, mate, "harness", sw.ElapsedMilliseconds, first.Err);
                ForgeData.RunEnd(first.Verified ? comp : 0, comp);
                return first.Result;
            }

            // MULTI-OP CHAIN (test-loop false-success flange-12-chain-mate-upsize-interfere: "mate the flanges, bump
            // up the bolt size, check for interference" parsed 3 ops but this switch only ever ran op[0] — the
            // other two were silently dropped, never even mentioned in the result. Every op the parser found now
            // runs in order and is reported in `legs`, even one with no executor yet (Rule #4: partial success
            // beats total failure — but nothing gets dropped without a trace).
            var legs = new List<ChainLeg> { new ChainLeg { Action = action, Outcome = first.Outcome, Verified = first.Verified, Err = first.Err, Result = first.Result } };
            bool allVerified = first.Verified;
            for (int idx = 1; idx < plan.Operations.Count; idx++)
            {
                var op = plan.Operations[idx];
                string a = (op.Action ?? "").Trim();
                if (a.Length == 0) continue;

                // test-loop wrong-answer fix (chain-thickness-arc-check): the cloud decides every chain step's action
                // in one upfront call with no per-step text handed back (IntentOperation carries no raw-text field),
                // so a tail clause like "...then measure thickness at the center" has no location-scoped-thickness
                // action in the cloud's CHAIN vocabulary and gets substituted with a generic fallback
                // (get_mass_properties) — an irrelevant answer to "how thick". The chain's ONE shared `intent`
                // string (the whole original sentence) is available to every leg, so re-run the same authoritative
                // check used for the single-shot path (line ~1002 above): a generic-fallback leg whose FULL intent
                // text is a genuine thickness question gets corrected to wall_thickness before dispatch.
                if ((a == "get_mass_properties" || a == "get_bounding_box") && WallThickness.IsGenericThicknessQuestion(intent))
                    a = op.Action = "wall_thickness";

                await emit("Intent", null, "run", "chain step " + (idx + 1) + "/" + plan.Operations.Count + ": " + a);
                GroundTruth.Trace?.Invoke("chain leg " + (idx + 1) + "/" + plan.Operations.Count + " (" + a + ") dispatch start");
                var leg = await DispatchOp(app, model, WrapOp(op, plan), op, a, intent, emit);
                GroundTruth.Trace?.Invoke("chain leg " + (idx + 1) + "/" + plan.Operations.Count + " (" + a + ") dispatch done, verified=" + leg.Verified);
                legs.Add(new ChainLeg { Action = a, Outcome = leg.Outcome, Verified = leg.Verified, Err = leg.Err, Result = leg.Result });
                allVerified = allVerified && leg.Verified;
            }

            ForgeData.LogEvent(action, masked, plan, allVerified ? "executed" : "partial", allVerified, comp, comp, fast, mate, "harness", sw.ElapsedMilliseconds, allVerified ? null : "chain-partial");
            ForgeData.RunEnd(allVerified ? comp : 0, comp);
            return new { chain = true, opsCount = plan.Operations.Count, allVerified, legs, primary = first.Result };
        }

        // wraps ONE op as a single-op plan for DispatchOp, carrying over the ORIGINAL plan's Confidence/Ambiguities
        // (a bare `new IntentPlan()` defaults Confidence to 0, which a case like set_material's RunIntent reads
        // directly — a fresh 0.0 would make it treat every single-op command as low-confidence and ask instead of
        // acting, a regression from the chain-continuation refactor caught while fixing flange-05-material-steel).
        private static IntentPlan WrapOp(IntentOperation op, IntentPlan source)
        {
            var p = new IntentPlan { Confidence = source?.Confidence ?? 0, Ambiguities = source?.Ambiguities ?? new List<string>() };
            if (op != null) p.Operations.Add(op);
            return p;
        }

        private class ChainLeg { public string Action; public string Outcome; public bool Verified; public string Err; public object Result; }
        private class OpOutcome { public object Result; public string Outcome = "executed"; public bool Verified = true; public string Err; }

        // ---- runs ONE parsed operation against its handler. Extracted from IntentDispatch so a multi-op plan can
        //      run every leg through the identical single-op logic (same switch, same per-handler wiring) instead
        //      of duplicating it. `plan` here is a ONE-OP wrapper around `op` — every case that reads `plan.Operations`
        //      sees exactly the op it's dispatching, whether this is leg 1 of 1 or leg 3 of 3. ----
        private static async Task<OpOutcome> DispatchOp(ISldWorks app, IModelDoc2 model, IntentPlan plan, IntentOperation op, string action, string intent, Func<string, string, string, string, Task> emit)
        {
            object result; string outcome = "executed"; bool verified = true; string err = null;
            bool isAsm = model is AssemblyDoc;

            // test-loop false-success fix (add-belt-clip, add-hex-flat, design-handle, hull-generate-keel,
            // hull-add-window-cutout — the regression corpus): the cloud, LOW confidence, routes a
            // named real-world feature ("belt clip", "keel", "hex flat") to a GENERIC primitive handler (a plain
            // round boss/pocket/hole/wrap at a face centre). The handler then genuinely builds and verifies that
            // primitive — real geometry, not a lie — but reporting it as satisfying "a belt clip" is the dishonest
            // part. Decline before cutting metal instead of quietly substituting a look-alike shape. Shared with
            // ForgePanel.Pipeline.cs via HonestLimits.IsGenericPrimitiveMismatch (one definition, no drift).
            if (HonestLimits.IsGenericPrimitiveMismatch(action, intent, plan?.Confidence ?? 0))
            {
                return new OpOutcome
                {
                    Outcome = "asked",
                    Verified = false,
                    Err = null,
                    Result = new { NeedsConfirm = true, Question = HonestLimits.GenericPrimitiveMismatchMessage, Error = (string)null }
                };
            }
            switch (action)
            {
                case "set_material":
                    var mr = await Materializer.RunIntent(app, model, plan, emit);
                    result = mr; if (mr.NeedsConfirm) { outcome = "asked"; verified = false; } else if (mr.Error != null) { outcome = "error"; verified = false; err = "material"; }
                    break;
                case "mate": result = await AutoMate.Run(app, model, emit); break;
                case "mirror":
                    result = MirrorSkip.IsSkipIntent(plan.Operations.Count > 0 ? plan.Operations[0] : null, intent)
                        ? (object)await MirrorSkip.Run(app, model, intent, emit)
                        : await Mirror.Run(app, model, intent, emit);
                    break;
                case "explode": case "collapse": result = await Exploder.Run(app, model, intent, emit); break;
                case "repair_exploded_view": result = await RepairExplodedView.Run(app, model, intent, emit); break;
                case "knit_surfaces_to_solid": result = await KnitSurfacesToSolid.Run(app, model, intent, emit); break;
                case "arrange_drawing_annotations": result = await ArrangeDrawingAnnotations.Run(app, model, intent, emit); break;
                case "manage_design_table": result = await ManageDesignTable.Run(app, model, intent, emit); break;
                case "fill_surface": result = await FillSurface.Run(app, model, intent, emit); break;
                case "describe_geometry": result = await DescribeGeometry.Run(app, model, intent, emit); break;
                case "highlight_entities": result = await HighlightEntities.Run(app, model, intent, emit); break;
                case "scan": result = await Scout.Run(app, model, emit); break;
                case "diagnose": result = await Doctor.Run(app, model, intent, emit); break;
                case "validate_props": result = await ValidateProps.Run(app, model, intent, emit); break;
                case "auto_number_parts": result = await AutoNumberParts.Run(app, model, intent, emit); break;
                case "find_duplicates": result = await FindDupes.Run(app, model, intent, emit); break;
                case "wall_thickness": result = await WallThickness.Run(app, model, intent, emit); break;
                case "arc_height": result = await ArcHeight.Run(app, model, intent, emit); break;
                case "hole_spacing": result = await HoleSpacing.Run(app, model, intent, emit); break;
                case "mesh_openings": result = await MeshOpenings.Run(app, model, intent, emit); break;
                case "count_through_holes": result = await CountThroughHoles.Run(app, model, intent, emit); break;
                case "interference": result = await Interfere.Run(app, model, intent, emit); break;
                case "change_impact": result = await Impact.Run(app, model, intent, emit); break;
                case "compare_versions": result = await Compare.Run(app, model, intent, null, emit); break;
                case "rebuild_profile": result = await Profiler.Run(app, model, intent, emit); break;
                case "fix_red_wave": result = await RedWave.Run(app, model, intent, emit); break;
                case "upsize": result = await Upsize.Run(app, model, intent, emit); break;
                case "audit_toolbox": result = await AuditToolbox.Run(app, model, intent, emit); break;
                case "list_mates": result = await ListMates.Run(app, model, intent, emit); break;
                case "get_custom_properties": result = await GetProperties.Run(app, model, intent, emit); break;
                case "get_component_info": result = await GetComponentInfo.Run(app, model, intent, emit); break;
                case "get_dimension_value": case "list_dimensions": result = await GetDimensions.Run(app, model, intent, emit); break;
                case "get_component_transform": result = await GetComponentTransform.Run(app, model, intent, emit); break;
                case "rename_component": result = await RenameComponent.Run(app, model, intent, emit); break;
                case "rename_file_with_references": result = await RenameFileWithReferences.Run(app, model, intent, emit); break;
                case "move_file_with_references": result = await MoveFileWithReferences.Run(app, model, intent, emit); break;
                case "get_feature_info": result = await GetFeatureInfo.Run(app, model, intent, emit); break;
                case "create_reference_axis": result = await CreateRefAxis.Run(app, model, intent, emit); break;
                case "measure_distance": result = await MeasureDistance.Run(app, model, intent, emit); break;
                case "check_clearance": result = await MeasureFaceGap.Run(app, model, intent, emit); break;
                case "select_components_by_filter": result = await SelectByFilter.Run(app, model, intent, emit); break;
                case "batch_update_materials": result = await BatchUpdateMaterials.Run(app, model, intent, emit); break;
                case "suppress_mate": case "unsuppress_mate": result = await SuppressMate.Run(app, model, intent, emit); break;
                case "delete_mate": result = await DeleteMate.Run(app, model, intent, emit); break;
                case "add_concentric_mate": result = await AddConcentricMate.Run(app, model, intent, emit); break;
                case "add_coincident_mate": result = await AddCoincidentMate.Run(app, model, intent, emit); break;
                case "add_parallel_mate": result = await AddParallelMate.Run(app, model, intent, emit); break;
                case "add_distance_mate": result = await AddDistanceMate.Run(app, model, intent, emit); break;
                case "add_angle_mate": result = await AddAngleMate.Run(app, model, intent, emit); break;
                case "add_width_mate": result = await AddWidthMate.Run(app, model, intent, emit); break;
                case "edit_mate_value": result = await EditMateValue.Run(app, model, intent, emit); break;
                case "delete_component": result = await DeleteComponent.Run(app, model, intent, emit); break;
                case "dissolve_subassembly": result = await DissolveSubassembly.Run(app, model, intent, emit); break;
                case "set_subassembly_flexibility": result = await SetSubassemblyFlexibility.Run(app, model, intent, emit); break;
                case "get_mate_info": result = await GetMateInfo.Run(app, model, intent, emit); break;
                case "get_active_document": result = await GetActiveDocument.Run(app, model, intent, emit); break;
                case "measure_angle": result = await MeasureAngle.Run(app, model, intent, emit); break;
                case "get_edge_length": result = await GetEdges.Run(app, model, intent, emit); break;
                case "get_face_normal": result = await GetFaces.Run(app, model, intent, emit); break;
                case "create_configuration": result = await CreateConfiguration.Run(app, model, intent, emit); break;
                case "delete_configuration": result = await DeleteConfiguration.Run(app, model, intent, emit); break;
                case "rename_configuration": result = await RenameConfiguration.Run(app, model, intent, emit); break;
                case "set_custom_property": result = await SetCustomProperty.Run(app, model, intent, emit); break;
                case "batch_update_custom_properties": result = await BatchUpdateCustomProperties.Run(app, model, intent, emit); break;
                case "delete_custom_property": result = await DeleteCustomProperty.Run(app, model, intent, emit); break;
                case "get_custom_property": result = await GetCustomProperty.Run(app, model, intent, emit); break;
                case "get_material_density": result = await GetMaterialDensity.Run(app, model, intent, emit); break;
                case "find_features_by_type": result = await FindFeaturesByType.Run(app, model, intent, emit); break;
                case "find_feature_by_name": result = await FindFeatureByName.Run(app, model, intent, emit); break;
                case "copy_configuration": result = await CopyConfiguration.Run(app, model, intent, emit); break;
                case "list_components": result = await ListComponents.Run(app, model, intent, emit); break;
                case "detect_ghost_references": result = await DetectGhostReferences.Run(app, model, intent, emit); break;
                case "list_feature_dependencies": result = await ListFeatureDependencies.Run(app, model, intent, emit); break;
                case "get_sheet_metal_properties": result = await GetSheetMetalProps.Run(app, model, intent, emit); break;
                case "set_sheet_metal_thickness": result = await SetSheetMetalThickness.Run(app, model, intent, emit); break;
                case "validate_sheet_metal": result = await ValidateSheetMetal.Run(app, model, intent, emit); break;
                case "set_document_units": result = await SetDocumentUnits.Run(app, model, intent, emit); break;
                case "set_angular_units": result = await SetAngularUnits.Run(app, model, intent, emit); break;
                case "set_decimal_places": result = await SetDecimalPlaces.Run(app, model, intent, emit); break;
                case "set_document_properties": result = await SetDraftingStandard.Run(app, model, intent, emit); break;
                case "get_document_units": result = await GetDocumentUnits.Run(app, model, intent, emit); break;
                case "set_active_configuration": result = await SetActiveConfiguration.Run(app, model, intent, emit); break;
                case "change_component_config": result = await ChangeComponentConfig.Run(app, model, intent, emit); break;
                case "replace_component": result = await ReplaceComponent.Run(app, model, intent, emit); break;
                case "batch_replace_components": result = await BatchReplaceComponents.Run(app, model, intent, emit); break;
                case "insert_component": result = await InsertComponent.Run(app, model, intent, emit); break;
                case "find_floating_components": result = await FindFloating.Run(app, model, intent, emit); break;
                case "list_subassemblies": result = await ListSubassemblies.Run(app, model, intent, emit); break;
                case "get_part_number": result = await GetPartNumber.Run(app, model, intent, emit); break;
                case "get_appearance": result = await GetAppearance.Run(app, model, intent, emit); break;
                case "get_component_config": result = await GetComponentConfig.Run(app, model, intent, emit); break;
                case "find_over_defined_components": result = await FindOverDefined.Run(app, model, intent, emit); break;
                case "get_rebuild_errors": result = await GetRebuildErrors.Run(app, model, intent, emit); break;
                case "detect_file_health": result = await DetectFileHealth.Run(app, model, intent, emit); break;
                case "handle_locked_files": result = await HandleLockedFiles.Run(app, model, intent, emit); break;
                case "detect_in_context_writes": result = await DetectInContextWrites.Run(app, model, intent, emit); break;
                case "handle_unknown_features": result = await HandleUnknownFeatures.Run(app, model, intent, emit); break;
                case "handle_assembly_features": result = await HandleAssemblyFeatures.Run(app, model, intent, emit); break;
                case "trace_derived_parts": result = await TraceDerivedParts.Run(app, model, intent, emit); break;
                case "recover_autosave": result = await RecoverAutosave.Run(app, model, intent, emit); break;
                case "handle_config_explosion": result = await HandleConfigExplosion.Run(app, model, intent, emit); break;
                case "detect_simulation_artifacts": result = await DetectSimulationArtifacts.Run(app, model, intent, emit); break;
                case "quarantine_file": result = await QuarantineFile.Run(app, model, intent, emit); break;
                case "repair_mate": result = await RepairMate.Run(app, model, intent, emit); break;
                case "handle_rollback_bar": result = await HandleRollbackBar.Run(app, model, intent, emit); break;
                case "rebuild_document": result = await RebuildDocument.Run(app, model, intent, emit); break;
                case "compare_bodies": result = await CompareBodies.Run(app, model, intent, emit); break;
                case "find_duplicate_components": result = await FindDuplicateComponents.Run(app, model, intent, emit); break;
                case "resolve_duplicate_paths": result = await ResolveDuplicatePaths.Run(app, model, intent, emit); break;
                case "combine_bodies": result = await CombineBodies.Run(app, model, intent, emit); break;
                case "split_body": result = await SplitBody.Run(app, model, intent, emit); break;
                case "delete_replace_face": result = await DeleteReplaceFace.Run(app, model, intent, emit); break;
                case "run_dfm_checks": result = await RunDfmChecks.Run(app, model, intent, emit); break;
                case "export_bom": result = await ExportBom.Run(app, model, intent, emit); break;
                case "save_bodies_as_parts": result = await SaveBodiesAsParts.Run(app, model, intent, emit); break;
                case "run_import_diagnostics": result = await RunImportDiagnostics.Run(app, model, intent, emit); break;
                case "check_geometry_errors": result = await CheckGeometryErrors.Run(app, model, intent, emit); break;
                case "add_center_marks": result = await AddCenterMarks.Run(app, model, intent, emit); break;
                case "replace_sheet_format": result = await ReplaceSheetFormat.Run(app, model, intent, emit); break;
                case "update_revision_table": result = await UpdateRevisionTable.Run(app, model, intent, emit); break;
                case "check_drafting_standards": result = await CheckDraftingStandards.Run(app, model, intent, emit); break;
                case "check_part_symmetry": result = await CheckPartSymmetry.Run(app, model, intent, emit); break;
                case "add_sketch_polygon": result = await AddSketchPolygon.Run(app, model, intent, emit); break;
                case "add_sketch_slot": result = await AddSketchSlot.Run(app, model, intent, emit); break;
                case "add_sketch_ellipse": result = await AddSketchEllipse.Run(app, model, intent, emit); break;
                case "add_construction_geometry": result = await AddConstructionGeometry.Run(app, model, intent, emit); break;
                case "add_sketch_arc": result = await AddSketchArc.Run(app, model, intent, emit); break;
                case "add_sketch_spline": result = await AddSketchSpline.Run(app, model, intent, emit); break;
                case "add_sketch_text": result = await AddSketchText.Run(app, model, intent, emit); break;
                case "add_sketch_entity": result = await AddSketchEntity.Run(app, model, intent, emit); break;
                case "add_sketch_dimension": result = await AddSketchDimension.Run(app, model, intent, emit); break;
                case "add_sketch_relation": result = await AddSketchRelation.Run(app, model, intent, emit); break;
                case "create_sketch": result = await CreateSketch.Run(app, model, intent, emit); break;
                case "create_layout_sketch": result = await CreateLayoutSketch.Run(app, model, intent, emit); break;
                case "create_3d_sketch": result = await Create3DSketch.Run(app, model, intent, emit); break;
                case "import_dxf_to_sketch": result = await ImportDxfToSketch.Run(app, model, intent, emit); break;
                case "offset_sketch_entities": result = await OffsetSketchEntities.Run(app, model, intent, emit); break;
                case "convert_entities": result = await ConvertEntities.Run(app, model, intent, emit); break;
                case "trim_extend": result = await TrimExtendSketch.Run(app, model, intent, emit); break;
                case "mirror_sketch_entities": result = await MirrorSketchEntities.Run(app, model, intent, emit); break;
                case "pattern_sketch_entities": result = await PatternSketchEntities.Run(app, model, intent, emit); break;
                case "add_revolve": result = await AddRevolve.Run(app, model, intent, emit); break;
                case "add_sweep": result = await AddSweep.Run(app, model, intent, emit); break;
                case "add_loft": result = await AddLoft.Run(app, model, intent, emit); break;
                case "create_boundary_feature": result = await CreateBoundaryFeature.Run(app, model, intent, emit); break;
                case "create_thicken": result = await CreateThicken.Run(app, model, intent, emit); break;
                case "add_helix": result = await AddHelix.Run(app, model, intent, emit); break;
                case "create_curve": result = await CreateCurve.Run(app, model, intent, emit); break;
                case "create_rib": result = await CreateRib.Run(app, model, intent, emit); break;
                case "create_extruded_surface": result = await CreateExtrudedSurface.Run(app, model, intent, emit); break;
                case "create_swept_lofted_surface": result = await CreateSweptSurface.Run(app, model, intent, emit); break;
                case "add_dome": result = await AddDome.Run(app, model, intent, emit); break;
                case "get_component_mass": result = await GetComponentMass.Run(app, model, intent, emit); break;
                case "rename_dimension": result = await RenameDimension.Run(app, model, intent, emit); break;
                case "list_bodies": result = await GetBodies.Run(app, model, intent, emit); break;
                case "get_material": result = await GetMaterial.Run(app, model, intent, emit); break;
                case "get_sketch_info": result = await GetSketches.Run(app, model, intent, emit); break;
                case "get_pattern_info": result = await GetPatternInfo.Run(app, model, intent, emit); break;
                case "edit_pattern_count": result = await EditPatternCount.Run(app, model, intent, emit); break;
                case "edit_pattern_spacing": result = await EditPatternSpacing.Run(app, model, intent, emit); break;
                case "skip_pattern_instance": result = await SkipPatternInstance.Run(app, model, intent, emit); break;
                case "list_reference_geometry": result = await GetRefGeometry.Run(app, model, intent, emit); break;
                case "drawing_package": result = await DrawingPkg.Run(app, model, intent, emit); break;
                case "flat_dxf": result = await FlatDxf.Run(app, model, intent, emit); break;
                case "batch_export_drawings": result = await BatchExportDrawings.Run(app, model, intent, emit); break;
                case "resize": result = await Resizer.Run(app, model, intent, emit); break;
                case "suppress_components": result = await SuppressComponents.Run(app, model, intent, emit); break;
                case "unsuppress_components": result = await UnsuppressComponents.Run(app, model, intent, emit); break;
                case "apply_appearance":
                    var aar = await ApplyAppearance.Run(app, model, intent, emit);
                    result = aar; if (aar.Error != null) { outcome = "error"; verified = false; err = "appearance"; }
                    else { verified = aar.Failed == 0 && (aar.Colored > 0 || (aar.Matched > 0 && aar.AlreadyColored == aar.Matched)); }
                    break;
                case "pattern_component": result = await PatternComponent.Run(app, model, intent, emit); break;
                case "linear_pattern_components": result = await LinearPatternComponent.Run(app, model, intent, emit); break;
                case "circular_pattern_components": result = await CircularPatternComponent.Run(app, model, intent, emit); break;
                case "pattern_driven_pattern": result = await PatternDrivenPatternComponent.Run(app, model, intent, emit); break;
                case "sketch_driven_pattern": result = await SketchDrivenPatternComponent.Run(app, model, intent, emit); break;
                case "shell_part": result = await ShellPart.Run(app, model, intent, emit); break;
                case "scale_part": result = await ScalePart.Run(app, model, intent, emit); break;
                case "create_variable_fillet": result = await CreateVariableFillet.Run(app, model, intent, emit); break;
                case "fillet_chamfer": result = await FilletChamfer.Run(app, model, intent, emit); break;
                case "create_thread": result = await CreateThread.Run(app, model, intent, emit); break;
                case "add_hole": result = await AddHole.Run(app, model, intent, emit); break;
                case "add_bolt_circle": result = await AddBoltCircle.Run(app, model, intent, emit); break;
                case "add_boss": result = await AddBoss.Run(app, model, intent, emit); break;
                case "create_wrap": result = await CreateWrap.Run(app, model, intent, emit); break;
                case "add_pocket": result = await AddPocket.Run(app, model, intent, emit); break;
                case "add_counterbore": result = await AddCounterbore.Run(app, model, intent, emit); break;
                case "add_countersink": result = await AddCountersink.Run(app, model, intent, emit); break;
                case "pattern_feature": result = await PatternFeature.Run(app, model, intent, emit); break;
                case "mirror_feature": result = await MirrorFeature.Run(app, model, intent, emit); break;
                case "create_reference_plane": result = await CreateRefPlane.Run(app, model, intent, emit); break;
                case "create_coordinate_system": result = await CreateCoordSys.Run(app, model, intent, emit); break;
                case "get_mass_properties": result = await GetMassProps.Run(app, model, intent, emit); break;
                case "get_bounding_box": result = await GetBoundingBox.Run(app, model, intent, emit); break;
                case "capture_viewport": result = await CaptureViewport.Run(app, model, intent, emit); break;
                case "capture_section": result = await CaptureSection.Run(app, model, intent, emit); break;
                case "select_face": result = await SelectFace.Run(app, model, intent, emit); break;
                case "select_component": result = await SelectComponent.Run(app, model, intent, emit); break;
                case "select_edge": result = await SelectEdge.Run(app, model, intent, emit); break;
                case "select_plane": result = await SelectPlane.Run(app, model, intent, emit); break;
                case "get_selected_entities": result = await GetSelectedEntities.Run(app, model, intent, emit); break;
                case "clear_selection": result = await ClearSelection.Run(app, model, intent, emit); break;
                case "measure_bolt_circle": result = await MeasureBoltCircle.Run(app, model, intent, emit); break;
                case "count_named_components": result = await CountNamedComponents.Run(app, model, intent, emit); break;
                case "count_gear_teeth": result = await CountGearTeeth.Run(app, model, intent, emit); break;
                case "validate_scale_sanity": result = await ValidateScaleSanity.Run(app, model, intent, emit); break;
                case "detect_shared_sketches": result = await DetectSharedSketches.Run(app, model, intent, emit); break;
                case "resolve_localized_names": result = await ResolveLocalizedNames.Run(app, model, intent, emit); break;
                case "get_cut_list": result = await GetCutList.Run(app, model, intent, emit); break;
                case "get_fixture_capacity": result = await GetFixtureCapacity.Run(app, model, intent, emit); break;
                case "normalize_units": result = await NormalizeUnits.Run(app, model, intent, emit); break;
                case "list_configurations": result = await GetConfigs.Run(app, model, intent, emit); break;
                case "list_features": result = await GetFeatureTree.Run(app, model, intent, emit); break;
                case "suppress_feature": case "unsuppress_feature": result = await SuppressFeature.Run(app, model, intent, emit); break;
                case "set_config_feature_suppression": result = await ConfigFeatureSuppression.Run(app, model, intent, emit); break;
                case "rename_feature": result = await RenameFeature.Run(app, model, intent, emit); break;
                case "batch_rename_features": result = await BatchRenameFeatures.Run(app, model, intent, emit); break;
                case "reorder_feature": result = await ReorderFeature.Run(app, model, intent, emit); break;
                case "set_component_lightweight": case "set_component_resolved": result = await SetComponentLightweight.Run(app, model, intent, emit); break;
                case "get_file_references": result = await GetFileReferences.Run(app, model, intent, emit); break;
                case "repair_missing_references": result = await RepairMissingReferences.Run(app, model, intent, emit); break;
                case "update_sheet_references": result = await UpdateSheetReferences.Run(app, model, intent, emit); break;
                case "insert_bom_table": result = await InsertBomTable.Run(app, model, intent, emit); break;
                case "clean_bom_table": result = await CleanBomTable.Run(app, model, intent, emit); break;
                case "repair_balloon_references": result = await RepairBalloonReferences.Run(app, model, intent, emit); break;
                case "pack_and_go": result = await PackAndGo.Run(app, model, intent, emit); break;
                case "diagnose_sketch": result = await DiagnoseSketch.Run(app, model, intent, emit); break;
                case "fully_define_sketch": result = await FullyDefineSketch.Run(app, model, intent, emit); break;
                case "find_where_used": result = await FindWhereUsed.Run(app, model, intent, emit); break;
                case "batch_convert_files": result = await BatchConvertFiles.Run(app, model, intent, emit); break;
                case "import_file": result = await ImportFile.Run(app, model, intent, null, emit); break;
                case "open_document": result = await OpenDocument.Run(app, model, intent, null, emit); break;
                case "save_document_as": result = await SaveDocumentAs.Run(app, model, intent, emit); break;
                case "save_document": result = await SaveDocument.Run(app, model, intent, emit); break;
                case "close_document": result = await CloseDocument.Run(app, model, intent, emit); break;
                case "create_drawing": result = await CreateDrawing.Run(app, model, intent, emit); break;
                case "create_part": result = await CreatePart.Run(app, model, intent, emit); break;
                case "insert_new_part_in_context": result = await InsertNewPartInContext.Run(app, model, intent, emit); break;
                case "create_assembly": result = await CreateAssembly.Run(app, model, intent, emit); break;
                case "insert_standard_views": result = await InsertStandardViews.Run(app, model, intent, emit); break;
                case "create_tree_folder": result = await CreateTreeFolder.Run(app, model, intent, emit); break;
                case "set_rebuild_verification": result = await SetRebuildVerification.Run(app, model, intent, emit); break;
                case "get_drawing_views": result = await GetDrawingViews.Run(app, model, intent, emit); break;
                case "get_driving_dimensions": result = await GetDrivingDimensions.Run(app, model, intent, emit); break;
                case "delete_feature": result = await DeleteFeature.Run(app, model, intent, emit); break;
                case "set_fixed": case "fix_component": case "float_component": result = await SetFixed.Run(app, model, intent, emit); break;
                case "set_dimension": result = await SetDimension.Run(app, model, intent, emit); break;
                case "edit_feature_parameter": result = await EditFeatureParameter.Run(app, model, intent, emit); break;
                case "edit_last_feature": result = await EditLastFeature.Run(app, model, intent, emit); break;
                case "set_config_specific_dimension": result = await ConfigSpecificDimension.Run(app, model, intent, emit); break;
                case "edit_equation": result = await EditEquation.Run(app, model, intent, emit); break;
                case "add_equation": result = await AddEquation.Run(app, model, intent, emit); break;
                case "delete_equation": result = await DeleteEquation.Run(app, model, intent, emit); break;
                case "list_equations": result = await ListEquations.Run(app, model, intent, emit); break;
                case "transform_assembly": result = await TransformAssembly.Run(app, model, intent, emit); break;
                case "move_component": result = await MoveComponent.Run(app, model, intent, emit); break;
                case "rotate_component": result = await RotateComponent.Run(app, model, intent, emit); break;
                case "geometry_defeature": result = await GeometryDefeature.Run(app, model, intent, emit); break;
                case "simplify": result = isAsm ? (object)await Batcher.Run(app, model, intent, emit) : await Simplifier.Run(app, model, intent, emit); break;
                case "isolate": await emit("Intent", null, "done", "isolate deferred (hangs headless on this build)"); result = plan; outcome = "refused"; verified = false; break;
                default:
                    {
                        // test-loop no-change finding (change-outer-ring-diameter): a NON-EMPTY op list with an action
                        // name the switch doesn't recognize (a cloud hallucination/placeholder like "unknown") fell
                        // through here to a bare "unrouted" log with no real result object — a silent-ish dead end,
                        // unlike the zero-op path above (plan.Operations.Count == 0) which already asks an honest
                        // question. Same root cause, same fix: surface the parser's own ambiguity (or a generic
                        // fallback) as ONE clarifying question instead of a technical dead end.
                        string q = (plan.Ambiguities != null && plan.Ambiguities.Count > 0)
                            ? plan.Ambiguities[0]
                            : "I didn't recognize a specific action for that — what would you like me to do?";
                        await emit("Intent", null, "done", "no intent-executor for '" + action + "' — asking: " + q);
                        result = new { NeedsConfirm = true, Question = q, Error = (string)null };
                        outcome = "asked"; verified = false;
                    }
                    break;
            }

            // test-loop false-success fix (suppress-rotate-unsuppress, the regression corpus): the
            // MIDDLE leg of a 3-op chain ("suppress the axis, rotate the assembly 45, then unsuppress the axis")
            // asked a clarifying question (TransformAssembly.Run set NeedsConfirm/Question, nothing moved) but its
            // switch case above ("transform_assembly": result = ...; break;) never checked that field, so `verified`
            // stayed at its default `true` — the chain looked fully verified even though the rotate never happened.
            // Most cases above handle their own result explicitly (e.g. set_material checks mr.NeedsConfirm), but
            // dozens of single-line cases don't. Rather than hand-patch every one, a generic post-switch check: if
            // this case left `verified` at the untouched default AND `err` unset, but `result` itself carries a
            // NeedsConfirm==true (or a non-null Error), that's a genuine unresolved leg — correct it here once,
            // covering every handler with that shape instead of one case at a time. No-op for handlers that already
            // self-report (verified/err already reflect the real state) or that carry neither field.
            if (verified && err == null && result != null)
            {
                // Most result classes declare NeedsConfirm/Error as plain public FIELDS, not properties (e.g.
                // TransformAssemblyResult) — GetProperty alone silently finds nothing on those, so check both.
                var t = result.GetType();
                bool needsConfirm = ReadBoolMember(t, result, "NeedsConfirm");
                if (needsConfirm)
                {
                    outcome = "asked"; verified = false;
                }
                else
                {
                    string resultErr = ReadStringMember(t, result, "Error");
                    if (resultErr != null) { outcome = "error"; verified = false; err = resultErr; }
                    else
                    {
                        // Found regression-sweeping cut-smooth-chain-live: a handler can legitimately report
                        // Verified=false with NEITHER NeedsConfirm nor Error set (e.g. GeometryDefeature/
                        // DeleteFeature's "ran, found nothing, but this wasn't even the right kind of request"
                        // honest-false path) — the checks above never look at the result's OWN Verified field at
                        // all, so this specific shape of dishonesty (ran, self-reports unverified, but the local
                        // `verified` default of true survives untouched) slipped through every prior fix in this
                        // generic block. Respect the result's own Verified field when the type declares one —
                        // authoritative per Rule #6, same as every handler's own fail-closed doctrine.
                        bool? resultVerified = ReadNullableBoolMember(t, result, "Verified");
                        if (resultVerified.HasValue && !resultVerified.Value) verified = false;
                    }
                }
            }
            return new OpOutcome { Result = result, Outcome = outcome, Verified = verified, Err = err };
        }

        private static bool ReadBoolMember(Type t, object obj, string name)
        {
            var f = t.GetField(name);
            if (f != null && f.FieldType == typeof(bool)) return (bool)f.GetValue(obj);
            var p = t.GetProperty(name);
            if (p != null && p.PropertyType == typeof(bool)) return (bool)p.GetValue(obj);
            return false;
        }

        private static string ReadStringMember(Type t, object obj, string name)
        {
            var f = t.GetField(name);
            if (f != null && f.FieldType == typeof(string)) return (string)f.GetValue(obj);
            var p = t.GetProperty(name);
            if (p != null && p.PropertyType == typeof(string)) return (string)p.GetValue(obj);
            return null;
        }

        // like ReadBoolMember, but distinguishes "no such member" (null) from "member is false" — needed so the
        // generic chain-honesty check can tell "this result type doesn't declare Verified at all" (leave the
        // switch case's own verified tracking alone) apart from "it declares Verified and it's false" (respect it).
        private static bool? ReadNullableBoolMember(Type t, object obj, string name)
        {
            var f = t.GetField(name);
            if (f != null && f.FieldType == typeof(bool)) return (bool)f.GetValue(obj);
            var p = t.GetProperty(name);
            if (p != null && p.PropertyType == typeof(bool)) return (bool)p.GetValue(obj);
            return null;
        }

        private static List<string> InventoryFor(IModelDoc2 model, out int comp, out int fast, out int mate)
        {
            comp = 0; fast = 0; mate = 0; var names = new List<string>();
            var asm = model as AssemblyDoc; if (asm == null) return names;
            foreach (var o in (asm.GetComponents(true) as object[]) ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue; bool s = false; try { s = c.IsSuppressed(); } catch { } if (s) continue;
                comp++; string nm = null; try { nm = c.Name2; } catch { } if (nm != null) { names.Add(nm); if (IntentLayer.ClassifyKind(nm) == "bolt" || IntentLayer.ClassifyKind(nm) == "nut") fast++; }
            }
            return names;
        }

        // emit → flat log line, so the JSON carries the same diagnostic trail the panel renders
        private static Func<string, string, string, string, Task> Collect(List<string> into)
        {
            return (agent, gloss, state, result) =>
            {
                into.Add((agent ?? "·") + " | " + (state ?? "") + " | " + (result ?? gloss ?? ""));
                return Task.CompletedTask;
            };
        }

        private static JToken SafeParse(string json)
        { try { return JToken.Parse(json); } catch { return new JValue(json); } }

        private static void Write(string path, JObject res)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, res.ToString());
            }
            catch { }
        }
    }
}
