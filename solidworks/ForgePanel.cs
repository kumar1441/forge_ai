using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using Forge.Providers;

namespace Forge.SolidWorks
{
    /// <summary>
    /// The Forge task pane. Hosts panel.html in WebView2 and bridges its messages
    /// to the SolidWorks variant loop. COM-visible with a ProgID so SolidWorks can
    /// create it via ITaskpaneView.AddControl.
    /// </summary>
    [Guid("b2d4f6a8-1c3e-4d5f-8a9b-0c1d2e3f4a5b")]
    [ProgId("Forge.ForgePanel")]
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    public partial class ForgePanel : UserControl
    {
        private WebView2 _web;
        private string _pendingIntent; // the conversation so far when Forge has asked a question
        private string _attachedFile;  // a file the user handed Forge via the attach button (e.g. the 2nd version for Compare)
        private Timer _bifrost;        // polls the cross-tool feed for changes from other tools
        private string _bifrostSince;  // poll cursor (newest event seen)
        private readonly System.Collections.Generic.HashSet<string> _bifrostSeen = new System.Collections.Generic.HashSet<string>();
        private string _lastBoardChange; // most recent cross-tool board change (for impact follow-ups)
        private System.Collections.Generic.List<EditChange> _pendingIncoming; // a teammate's change awaiting "apply"
        // Session memory of the last change_impact run so a follow-up ("show me the pattern", "highlight it") resolves
        // to the named dependent and SHOWS it, instead of hedging (Demo 3, RULE #2 on follow-ups).
        private string _lastImpactTarget;
        private string _lastImpactPrimary;   // the dependent to "show" (usually the pattern)
        private System.Collections.Generic.List<string> _lastImpactDependents;

        public ForgePanel()
        {
            _web = new WebView2 { Dock = DockStyle.Fill };
            Controls.Add(_web);
            _ = InitAsync();
        }

        private async Task InitAsync()
        {
            string userData = Path.Combine(Path.GetTempPath(), "ForgeSW_WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, userData);
            await _web.EnsureCoreWebView2Async(env);

            _web.CoreWebView2.WebMessageReceived += OnWebMessage;

            string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string html = Path.Combine(dir, "panel.html");
            _web.CoreWebView2.Navigate(new Uri(html).AbsoluteUri);

            // Tell the panel what's real once the page is up: first-run onboarding (trap.md 2b/2c â€” the
            // panel gating on its own localStorage so this is harmless every session) + the current usage-data
            // state so the Settings toggle reflects ForgeData.ShareUsage (host = source of truth).
            _web.CoreWebView2.NavigationCompleted += (s, e) =>
            {
                Send(new { type = "telemetryState", on = ForgeData.ShareUsage });
                if (!_firstrunShown) { _firstrunShown = true; Send(new { type = "firstrun" }); }
            };

            // Bifrost: watch for board changes pushed from KiCad/Altium to this part's project.
            _bifrost = new Timer { Interval = 15000 };
            _bifrost.Tick += BifrostTick;
            _bifrost.Start();

            // Headless test harness: watch for a request file the external orchestrator drops, run it in-process.
            _harness = new Timer { Interval = 2000 };
            _harness.Tick += HarnessTick;
            _harness.Start();
        }

        private Timer _harness;
        private bool _harnessBusy;
        private static bool _noticesShown;   // crash-recovery + update notice, once per session
        private static bool _firstrunShown;  // onboarding overlay, once per process (panel also gates on localStorage)
        private string _currentRunId;        // per-command id: ties thumbs feedback + corrections to the run

        // Central post-run logging for EVERY live handler: masked event -> the data spine (fills the telemetry store +
        // Layer-3 replay + crash-recovery close), plus correction capture (rephrase pairing + arms undo).
        // Everything is masked here; hard exclusions live in ForgeData. Best-effort â€” never surfaces to the user.
        private void LogRun(string handler, string intent, IModelDoc2 doc, string outcome, bool verified,
            int itemCount, IntentPlan plan = null, string errorCode = null, long durationMs = 0)
        {
            try
            {
                string masked = ForgeData.Mask(intent, IntentLayer.ComponentNames(doc));
                int comp = 0; try { var a = doc as AssemblyDoc; if (a != null) comp = a.GetComponentCount(false); } catch { }
                ForgeData.LogEvent(handler, masked, plan, outcome, verified, itemCount, comp, 0, 0, SwVer(), durationMs, errorCode);
                ForgeData.RunEnd(verified ? itemCount : 0, itemCount);
                CorrectionWatcher.RecordRun(_currentRunId, handler, masked, outcome, doc);
            }
            catch { }
        }

        // Material executor body â€” shared by the material router branch and the intent-layer fallback so a
        // typo'd material command ("make teh bolts brras") still reaches the same resolve-verify path.
        private async Task RunMaterialPlan(IModelDoc2 doc, IntentPlan mPlan, string intent)
        {
            Func<string, string, string, string, Task> emitM = async (agent, gloss, state, result) =>
            { Send(new { type = "step", agent, gloss, state, result }); await Task.Delay(60); };
            MaterialResult mtr = await Materializer.RunIntent(SwAddin.SwApp, doc, mPlan, emitM);
            Telemetry.Log("edit_run", success: mtr.Error == null && !mtr.NeedsConfirm, featuresCount: mtr.Applied, swVersion: SwVer(),
                opsUsed: OpCounter.Count, errorCode: mtr.Error == null ? null : "material", questionType: "edit", questionSummary: "material change (intent)");
            string mOutcome = mtr.NeedsConfirm ? "asked" : (mtr.Error != null ? "error" : "executed");
            LogRun("set_material", intent, doc, mOutcome, mtr.Error == null && !mtr.NeedsConfirm, mtr.Applied, mPlan, mtr.Error != null ? "material" : null);
            if (mtr.NeedsConfirm) { Send(new { type = "answer", answer = mtr.Question }); return; }   // Rule #2: ask one question
            if (mtr.Error != null) { Send(new { type = "error", message = mtr.Error }); return; }
            Send(new { type = "answer", answer = mtr.Info, runId = _currentRunId, handler = "set_material" });   // runId -> thumbs feedback
        }

        private async void HarnessTick(object sender, EventArgs e)
        {
            if (_harnessBusy) return;
            try
            {
                string dir = Path.Combine(Path.GetTempPath(), "forge-harness");
                string req = Path.Combine(dir, "request.json");
                if (!File.Exists(req)) return;
                _harnessBusy = true;
                // claim the request so we run it exactly once
                string claimed = Path.Combine(dir, "request.running.json");
                try { if (File.Exists(claimed)) File.Delete(claimed); File.Move(req, claimed); } catch { _harnessBusy = false; return; }
                if (SwAddin.SwApp != null) await Harness.RunFromRequest(SwAddin.SwApp, claimed);
                try { File.Delete(claimed); } catch { }
            }
            catch { }
            finally { _harnessBusy = false; }
        }

        // Project key both tools agree on = the part's file name (without extension).
        private static string ProjectKey(IModelDoc2 model)
        {
            string p = model?.GetPathName();
            return string.IsNullOrEmpty(p) ? null : Path.GetFileNameWithoutExtension(p);
        }

        private async void BifrostTick(object sender, EventArgs e)
        {
            try
            {
                var model = SwAddin.SwApp?.ActiveDoc as IModelDoc2;
                string project = ProjectKey(model);
                if (string.IsNullOrEmpty(project)) return;

                var events = await ForgeApi.PollBifrost(project, _bifrostSince);
                foreach (var ev in events)
                {
                    _bifrostSince = ev.CreatedAt;
                    if (!string.IsNullOrEmpty(ev.Id) && !_bifrostSeen.Add(ev.Id)) continue; // already shown

                    if (ev.Dims != null)
                    {
                        // A teammate's shared model version â€” diff vs our current part (CAD git pull).
                        var current = VariantGenerator.ReadDimensions(model);
                        var mine = new System.Collections.Generic.Dictionary<string, double>();
                        foreach (var d in current) if (!mine.ContainsKey(d.Name)) mine[d.Name] = d.ValueMm;

                        var changes = new System.Collections.Generic.List<EditChange>();
                        var diff = new System.Collections.Generic.List<string>();
                        foreach (var ds in ev.Dims)
                        {
                            double cur;
                            if (mine.TryGetValue(ds.Name, out cur) && Math.Abs(cur - ds.ValueMm) > 0.01)
                            {
                                changes.Add(new EditChange { Name = ds.Name, Label = ds.Name, Unit = "mm", Value = ds.ValueMm });
                                diff.Add(ds.Name + ": " + Math.Round(cur, 2) + " â†’ " + Math.Round(ds.ValueMm, 2) + " mm");
                            }
                        }
                        if (changes.Count > 0)
                        {
                            _pendingIncoming = changes;
                            Send(new { type = "incoming", summary = ev.Summary, diff });
                        }
                    }
                    else
                    {
                        _lastBoardChange = ev.Summary; // board change â€” remember for "what does it impact?"
                        Send(new { type = "bifrost", source = ev.Source, summary = ev.Summary });
                    }
                }
            }
            catch { /* polling is best-effort */ }
        }

        private async void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string raw = e.TryGetWebMessageAsString();
            dynamic msg;
            try { msg = JsonConvert.DeserializeObject<dynamic>(raw); }
            catch { return; }

            string action = (string)msg.action;
            try { PanelCapture.Log("in", new { action, raw }); } catch { }
            try
            {
                if (action == "readModel") ReadModel();
                else if (action == "generate") await Generate((string)msg.intent);
                else if (action == "attach") DoAttach();
                else if (action == "clearAttach") { _attachedFile = null; Send(new { type = "attachCleared" }); }
                else if (action == "reset")   // rehearsal/recording: wipe the panel + every pending thread for a clean take
                {
                    _pendingIntent = null; _attachedFile = null; _pendingConfirm = null;
                    _lastImpactTarget = null; _lastImpactPrimary = null; _lastImpactDependents = null;
                    Send(new { type = "attachCleared" });
                }
                else if (action == "highlight") HighlightByName(msg);
                else if (action == "clearHighlight") { var m = SwAddin.SwApp?.ActiveDoc as IModelDoc2; if (m != null) m.ClearSelection2(true); }
                else if (action == "feedback")
                {
                    int? s = null; string t = null;
                    try { s = (int)msg.sentiment; } catch { }
                    try { t = (string)msg.text; } catch { }
                    Telemetry.Log("feedback", sentiment: s, feedback: t);
                    // New: thumbs on a run card -> rating (+1/-1) + one-tap reason, tagged to the exact run trace.
                    try
                    {
                        string runId = null, reason = null, handler = null;
                        try { runId = (string)msg.runId; } catch { }
                        try { reason = (string)msg.reason; } catch { }
                        try { handler = (string)msg.handler; } catch { }
                        int rating = (s.HasValue && s.Value == 0) ? -1 : (s ?? 1);
                        if (runId != null) ForgeData.LogFeedback(runId, handler, rating, reason, t);
                    }
                    catch { }
                }

                // FDL 4.4 ConfirmGate buttons: [Forge it] = feed the affirmative to the pending preview
                // (same path as typing "go"); [Not yet] = stand down, keep the model untouched.
                else if (action == "confirm")
                {
                    bool accept = false;
                    try { accept = (bool)msg.accept; } catch { }
                    if (accept) { if (await ResolvePendingConfirm("go")) { } }
                    else { _pendingConfirm = null; }
                }

                // Onboarding "I have a key" (trap.md 2b): provider + key -> config.json (provider/baseUrl/model,
                // key NEVER in the file) + the DPAPI-protected key store. This is the BYOK close that makes
                // `winget install` -> open SW -> paste key -> done feel like one step.
                else if (action == "saveKey")
                {
                    string key = null, provider = null;
                    try { key = (string)msg.key; } catch { }
                    try { provider = (string)msg.provider; } catch { }
                    if (string.IsNullOrWhiteSpace(key))
                    { Send(new { type = "answer", answer = "No key entered â€” staying on the keyless local path." }); return; }
                    try
                    {
                        SaveProviderKey(provider, key.Trim());
                        Send(new { type = "answer", answer = "Provider key saved â€” encrypted locally (DPAPI), never leaves this machine." });
                    }
                    catch (Exception ex) { Send(new { type = "error", message = "Couldn't save the key: " + ex.Message }); }
                }

                // Settings usage-data toggle (trap.md item 2: off switch easy to find). Host = source of truth.
                else if (action == "telemetry")
                {
                    bool on = true;
                    try { on = (bool)msg.on; } catch { }
                    ForgeData.ShareUsage = on;
                    Send(new { type = "answer", answer = "Anonymous usage sharing is now " + (on ? "ON" : "OFF") + "." });
                }
            }
            catch (Exception ex)
            {
                // Telemetry gets the exception TYPE only â€” never ex.Message (it can contain a file path).
                Telemetry.Log("crash", success: false, errorCode: ex.GetType().Name, swVersion: SwVer());
                Send(new { type = "error", message = ex.Message });
            }
        }

        // The attach affordance (panel-testing.md): the ONLY way a real user hands Forge a specific file without
        // typing a PC path. Opens the native file picker; the chosen file becomes _attachedFile, which handlers
        // that need a second file/model (Compare) pick up. A human action end-to-end â€” no path is ever typed.
        private void DoAttach()
        {
            try
            {
                using (var dlg = new OpenFileDialog())
                {
                    dlg.Title = "Attach a file for Forge to use";
                    dlg.Filter = "SolidWorks files (*.sldprt;*.sldasm;*.slddrw)|*.sldprt;*.sldasm;*.slddrw|All files (*.*)|*.*";
                    dlg.CheckFileExists = true;
                    // STA + UI thread already (WinForms UserControl), so ShowDialog is safe here.
                    if (dlg.ShowDialog(this) == DialogResult.OK && !string.IsNullOrEmpty(dlg.FileName))
                    {
                        _attachedFile = dlg.FileName;
                        Send(new { type = "attached", name = Path.GetFileName(_attachedFile) });
                    }
                }
            }
            catch (Exception ex)
            {
                Send(new { type = "error", message = "Couldn't attach that file: " + ex.Message });
            }
        }

        private IModelDoc2 ActivePart()
        {
            var model = (IModelDoc2)SwAddin.SwApp?.ActiveDoc;
            if (model == null) throw new Exception("Open a part first.");
            if ((int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
                throw new Exception("Active document must be a part (.SLDPRT).");
            return model;
        }

        private void ReadModel()
        {
            // Fail quietly: if there's no active part yet, just show the idle "open a part" state
            // instead of a red error (the panel polls/reads on load before a doc may be active).
            var model = SwAddin.SwApp?.ActiveDoc as IModelDoc2;
            if (model == null || (int)model.GetType() != (int)swDocumentTypes_e.swDocPART)
            {
                Send(new { type = "model", title = (string)null, dimensions = new object[0] });
                return;
            }
            var dims = VariantGenerator.ReadDimensions(model);
            Send(new
            {
                type = "model",
                title = Path.GetFileName(model.GetPathName()),
                dimensions = dims.ConvertAll(d => new { name = d.Name, valueMm = Math.Round(d.ValueMm, 3), feature = d.Feature, type = d.Type })
            });
        }

        private static string SwVer() { try { return SwAddin.SwApp.RevisionNumber(); } catch { return null; } }

        // ---- BYOK wiring (README "Get a free key" + ProviderFactory): writes %APPDATA%\Forge\config.json
        //      {provider, baseUrl, model} â€” the API key is NEVER in the file â€” and stores the key via
        //      KeyStore (DPAPI, current-user). Presets match the README's student instructions exactly so
        //      the onboarding key box configures a real, working provider. ----
        private static void SaveProviderKey(string provider, string key)
        {
            var preset = ProviderPresetFor((provider ?? "").Trim().ToLowerInvariant());
            KeyStore.Save(preset.Provider, key);
            string dir = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "Forge");
            Directory.CreateDirectory(dir);
            string cfgPath = Path.Combine(dir, "config.json");
            JObject cfg;
            try { cfg = JObject.Parse(File.ReadAllText(cfgPath)); } catch { cfg = new JObject(); }
            cfg["provider"] = preset.Provider;
            cfg["baseUrl"] = preset.BaseUrl;
            cfg["model"] = preset.Model;
            File.WriteAllText(cfgPath, cfg.ToString());
        }

        private class ProviderPreset
        {
            public string Provider, BaseUrl, Model;
            public ProviderPreset(string provider, string baseUrl, string model) { Provider = provider; BaseUrl = baseUrl; Model = model; }
        }

        private static ProviderPreset ProviderPresetFor(string slug)
        {
            switch (slug)
            {
                case "openrouter": return new ProviderPreset("openai-compatible", "https://openrouter.ai/api/v1", "openai/gpt-4o-mini");
                case "openai":     return new ProviderPreset("openai-compatible", "https://api.openai.com/v1", "gpt-4o-mini");
                case "anthropic":  return new ProviderPreset("anthropic", "https://api.anthropic.com/v1", "claude-3-5-sonnet-20241022");
                case "ollama":     return new ProviderPreset("openai-compatible", "http://localhost:11434/v1", "llama3.2");
                case "deepseek":
                default:           return new ProviderPreset("openai-compatible", "https://api.deepseek.com/v1", "deepseek-chat");
            }
        }

        private async Task Generate(string intent)
        {
            _currentRunId = Guid.NewGuid().ToString("N").Substring(0, 12);   // tags this run for thumbs feedback + correction capture
            // Trial gates â€” run nothing if we couldn't set up safely, or the trial has ended.
            if (!TrialInit.Ready)
            { Send(new { type = "error", message = "Forge couldn't set up its safe workspace â€” nothing was run." }); return; }
            if (TrialInit.IsExpired())
            { Telemetry.Log("expiry_block"); Send(new { type = "error", message = TrialInit.ContactLine }); return; }

            // Hidden dev command: trial cost/usage diagnostics (no operation, no model needed).
            if ((intent ?? "").Trim().ToLowerInvariant() == "trial diagnostics")
            { Send(new { type = "impact", explanation = CostLedger.Diagnostics(), affected = new string[0] }); return; }

            // DEMO ROUTE â€” authoritative, and BEFORE anything that can `await`/yield (the one-time UpdateNotice
            // below does a network call). flat-pattern-DXF / drawing-package / interference route here so (a) a
            // misclassification can't drop them to the generic answer path, and (b) â€” critical for interference â€”
            // their apartment-bound COM runs on THIS UI/STA thread, never a post-await threadpool thread (a
            // threadpool COM read on the InterferenceDetectionManager access-violates and takes SolidWorks down).
            if (await TryDemoRouteFirst(intent)) return;

            // One-time session notices: an interrupted last run (crash recovery) + a newer build available.
            if (!_noticesShown)
            {
                _noticesShown = true;
                try { var crash = ForgeData.CheckInterrupted(); if (crash != null) Send(new { type = "answer", answer = crash }); } catch { }
                try { var upd = await ForgeData.UpdateNotice(); if (upd != null) Send(new { type = "answer", answer = upd }); } catch { }
            }

            // Privacy / anonymous-usage control (data contract in docs/DATA.md).
            string _lc = (intent ?? "").Trim().ToLowerInvariant();
            if (System.Text.RegularExpressions.Regex.IsMatch(_lc, @"\b(share data|usage data|privacy|telemetry|what do you collect|data sharing)\b"))
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(_lc, @"\b(off|stop|disable|don't|dont)\b")) ForgeData.ShareUsage = false;
                else if (System.Text.RegularExpressions.Regex.IsMatch(_lc, @"\b(on|start|enable)\b")) ForgeData.ShareUsage = true;
                Send(new { type = "answer", answer = ForgeData.PrivacySummary() }); return;
            }

            // Enforce mode: once the operation cap is reached, every command is blocked.
            if (OpCounter.LimitReached)
            { Telemetry.Log("limit_block", opsUsed: OpCounter.Count); Send(new { type = "error", message = "Trial limit reached â€” contact Ravi 628-363-1774" }); return; }

            // A preview was shown last turn and is awaiting a "go" â€” resolve it before anything else.
            if (await ResolvePendingConfirm(intent)) return;

            // Change-impact FOLLOW-UP (Demo 3, RULE #2): after "what breaks if I change X?" a reply like
            // "show me the pattern" / "highlight it" references the dependent we just named â€” resolve + SHOW it
            // instead of re-asking. Only fires when we have a remembered impact AND the reply is anaphoric.
            if (TryImpactFollowup(intent)) return;

            // Bulk resize fasteners (Demo #6, M6â†’M8) â€” route AUTHORITATIVELY before the cloud parse. A clear
            // "replace all the M6 bolts with M8" must ACT, never fall to the parser's spurious "bolt sizes not
            // stated" ambiguity + the destructive-write hedge (RULE #2). Gated on 2 M-tokens so it's specific.
            if (Resizer.IsResizeIntent((intent ?? "").Trim().ToLowerInvariant()))
            {
                var doc = SwAddin.SwApp?.ActiveDoc as IModelDoc2;
                if (doc == null) { Send(new { type = "error", message = "Open your assembly first." }); return; }
                if ((int)doc.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
                { Send(new { type = "error", message = "Open the assembly (.SLDASM) to resize fasteners." }); return; }

                OpCounter.Increment();
                ForgeData.RunBegin("resize", ForgeData.Mask(intent, IntentLayer.ComponentNames(doc)), 0);
                Func<string, string, string, string, Task> emitR = async (agent, gloss, state, result) =>
                { Send(new { type = "step", agent, gloss, state, result }); await Task.Delay(60); };

                ResizeResult rr = await Resizer.Run(SwAddin.SwApp, doc, intent, emitR);
                Telemetry.Log("edit_run", success: rr.Error == null && rr.Switched > 0, featuresCount: rr.Switched, swVersion: SwVer(),
                    opsUsed: OpCounter.Count, errorCode: rr.Error == null ? null : "resize", questionType: "edit", questionSummary: "bulk resize fasteners");
                if (rr.Error != null) { LogRun("resize", intent, doc, "error", false, 0, errorCode: "resize"); Send(new { type = "error", message = rr.Error }); return; }
                LogRun("resize", intent, doc, "executed", rr.Error == null && rr.Switched > 0, rr.Switched);
                Send(new { type = "answer", answer = rr.Info, runId = _currentRunId, handler = "resize" });
                return;
            }

            // â˜… SHARED HANDLER PIPELINE (parse-first): the single path every handler flows through, so all handlers
            //   â€” and future ones â€” get parse -> confirm-or-ask -> preview -> execute -> verify -> log uniformly.
            //   Returns false only when the cloud parser is unreachable or the action is unknown; then the
            //   per-handler regex blocks below run as the OFFLINE fallback (and the legacy variant path after them).
            if (await RunViaPipeline(intent)) return;

            // Auto-mate â€” the first "doer". Works on an ASSEMBLY, so intercept here BEFORE ActivePart()
            // (which only accepts parts). Deploys the Gauge â†’ Torque â†’ Sentinel crew with live narration.
            if (AutoMate.IsMateIntent((intent ?? "").Trim().ToLowerInvariant()))
            {
                var doc = SwAddin.SwApp?.ActiveDoc as IModelDoc2;
                if (doc == null) { Send(new { type = "error", message = "Open your assembly first." }); return; }
                if ((int)doc.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
                { Send(new { type = "error", message = "Open the assembly (.SLDASM) to mate fasteners." }); return; }

                OpCounter.Increment();
                ForgeData.RunBegin("mate", ForgeData.Mask(intent, IntentLayer.ComponentNames(doc)), 0);
                var swM = System.Diagnostics.Stopwatch.StartNew();
                Func<string, string, string, string, Task> emit = async (agent, gloss, state, result) =>
                { Send(new { type = "step", agent, gloss, state, result }); await Task.Delay(60); };

                MateResult mr = await AutoMate.Run(SwAddin.SwApp, doc, emit);
                Telemetry.Log("edit_run", success: mr.Error == null && mr.Mated > 0, featuresCount: mr.Mated,
                    durationMs: swM.ElapsedMilliseconds, swVersion: SwVer(), opsUsed: OpCounter.Count,
                    errorCode: mr.Error == null ? null : "auto_mate", questionType: "edit", questionSummary: "auto-mate fasteners");

                if (mr.Error != null) { LogRun("mate", intent, doc, "error", false, 0, errorCode: "auto_mate", durationMs: swM.ElapsedMilliseconds); Send(new { type = "error", message = mr.Error }); return; }
                LogRun("mate", intent, doc, "executed", mr.Error == null && mr.Mated > 0, mr.Mated, durationMs: swM.ElapsedMilliseconds);
                Send(new { type = "mated", count = mr.Mated, seated = mr.Seated, proud = mr.Proud, failed = mr.Failed, clean = mr.RebuildClean, runId = _currentRunId, handler = "mate" });
                return;
            }

            // Mirror â€” reflect a component to the other side of a principal plane. Assembly-only.
            if (Mirror.IsMirrorIntent((intent ?? "").Trim().ToLowerInvariant()))
            {
                var doc = SwAddin.SwApp?.ActiveDoc as IModelDoc2;
                if (doc == null) { Send(new { type = "error", message = "Open your assembly first." }); return; }
                if ((int)doc.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
                { Send(new { type = "error", message = "Open the assembly (.SLDASM) to mirror a component." }); return; }

                OpCounter.Increment();
                ForgeData.RunBegin("mirror", ForgeData.Mask(intent, IntentLayer.ComponentNames(doc)), 0);
                Func<string, string, string, string, Task> emitMir = async (agent, gloss, state, result) =>
                { Send(new { type = "step", agent, gloss, state, result }); await Task.Delay(60); };

                MirrorResult mir = await Mirror.Run(SwAddin.SwApp, doc, intent, emitMir);
                Telemetry.Log("edit_run", success: mir.Error == null && (mir.Created || mir.AlreadyDone), swVersion: SwVer(),
                    opsUsed: OpCounter.Count, errorCode: mir.Error == null ? null : "mirror", questionType: "edit", questionSummary: "mirror component");
                if (mir.Error != null) { LogRun("mirror", intent, doc, "error", false, 0, errorCode: "mirror"); Send(new { type = "error", message = mir.Error }); return; }
                LogRun("mirror", intent, doc, "executed", mir.Created || mir.AlreadyDone, mir.Created ? 1 : 0);
                Send(new { type = "answer", answer = mir.AlreadyDone ? "Already mirrored." : mir.Info, runId = _currentRunId, handler = "mirror" });
                return;
            }

            // Explode â€” spread an assembly's parts apart. Assembly-only, so intercept here too.
            if (Exploder.IsExplodeIntent((intent ?? "").Trim().ToLowerInvariant()))
            {
                var doc = SwAddin.SwApp?.ActiveDoc as IModelDoc2;
                if (doc == null) { Send(new { type = "error", message = "Open your assembly first." }); return; }
                if ((int)doc.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
                { Send(new { type = "error", message = "Open the assembly (.SLDASM) to explode it." }); return; }

                OpCounter.Increment();
                ForgeData.RunBegin("explode", ForgeData.Mask(intent, IntentLayer.ComponentNames(doc)), 0);
                Func<string, string, string, string, Task> emitX = async (agent, gloss, state, result) =>
                { Send(new { type = "step", agent, gloss, state, result }); await Task.Delay(60); };

                ExplodeResult xr = await Exploder.Run(SwAddin.SwApp, doc, intent, emitX);
                Telemetry.Log("edit_run", success: xr.Error == null, featuresCount: xr.Moved, swVersion: SwVer(),
                    opsUsed: OpCounter.Count, errorCode: xr.Error == null ? null : "explode", questionType: "edit", questionSummary: "explode assembly");
                if (xr.Error != null) { LogRun("explode", intent, doc, "error", false, 0, errorCode: "explode"); Send(new { type = "error", message = xr.Error }); return; }
                LogRun("explode", intent, doc, "executed", xr.Error == null, xr.Moved);
                Send(new { type = "answer", answer = xr.Info, runId = _currentRunId, handler = "explode" });
                return;
            }

            // Material change â€” now goes through the AI INTENT LAYER (parse -> resolve -> confirm-or-ask -> execute
            // -> verify). Supports DIFFERENT materials per part/kind, typos, and library mapping. First migrated handler.
            if (Materializer.IsMaterialIntent((intent ?? "").Trim().ToLowerInvariant()))
            {
                var doc = SwAddin.SwApp?.ActiveDoc as IModelDoc2;
                if (doc == null) { Send(new { type = "error", message = "Open a part or assembly first." }); return; }

                OpCounter.Increment();
                ForgeData.RunBegin("set_material", ForgeData.Mask(intent, IntentLayer.ComponentNames(doc)), 0);
                var mPlan = await IntentLayer.Parse(intent, doc);
                if (mPlan.Error != null) { Send(new { type = "error", message = mPlan.Error }); return; }
                await RunMaterialPlan(doc, mPlan, intent);
                return;
            }

            // Scan / assembly doctor â€” READ-ONLY report on an assembly. Assembly-only, intercept before ActivePart.
            if (Scout.IsScanIntent((intent ?? "").Trim().ToLowerInvariant()))
            {
                var doc = SwAddin.SwApp?.ActiveDoc as IModelDoc2;
                if (doc == null) { Send(new { type = "error", message = "Open your assembly first." }); return; }
                if ((int)doc.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
                { Send(new { type = "error", message = "Open the assembly (.SLDASM) to scan it." }); return; }

                OpCounter.Increment();
                Func<string, string, string, string, Task> emitSc = async (agent, gloss, state, result) =>
                { Send(new { type = "step", agent, gloss, state, result }); await Task.Delay(60); };

                ScoutResult scr = await Scout.Run(SwAddin.SwApp, doc, emitSc);
                Telemetry.Log("read_run", success: scr.Error == null, featuresCount: scr.Total, swVersion: SwVer(),
                    opsUsed: OpCounter.Count, errorCode: scr.Error == null ? null : "scan", questionType: "assess", questionSummary: "scan assembly");
                if (scr.Error != null) { LogRun("scan", intent, doc, "error", false, 0, errorCode: "scan"); Send(new { type = "error", message = scr.Error }); return; }
                LogRun("scan", intent, doc, "executed", scr.Error == null, scr.Total);
                Send(new { type = "answer", answer = scr.Info, runId = _currentRunId, handler = "scan" });
                return;
            }

            // Isolate / show-all â€” assembly visibility. Intercept before ActivePart.
            if (Isolator.IsIsolateIntent((intent ?? "").Trim().ToLowerInvariant()))
            {
                var doc = SwAddin.SwApp?.ActiveDoc as IModelDoc2;
                if (doc == null) { Send(new { type = "error", message = "Open your assembly first." }); return; }
                if ((int)doc.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
                { Send(new { type = "error", message = "Open the assembly (.SLDASM) to isolate parts." }); return; }

                OpCounter.Increment();
                ForgeData.RunBegin("isolate", ForgeData.Mask(intent, IntentLayer.ComponentNames(doc)), 0);
                Func<string, string, string, string, Task> emitI = async (agent, gloss, state, result) =>
                { Send(new { type = "step", agent, gloss, state, result }); await Task.Delay(60); };

                IsolateResult ir = await Isolator.Run(SwAddin.SwApp, doc, intent, emitI);
                Telemetry.Log("edit_run", success: ir.Error == null, featuresCount: ir.Hidden, swVersion: SwVer(),
                    opsUsed: OpCounter.Count, errorCode: ir.Error == null ? null : "isolate", questionType: "edit", questionSummary: "isolate subsystem");
                if (ir.Error != null) { LogRun("isolate", intent, doc, "error", false, 0, errorCode: "isolate"); Send(new { type = "error", message = ir.Error }); return; }
                LogRun("isolate", intent, doc, "executed", ir.Error == null, ir.Hidden);
                Send(new { type = "answer", answer = ir.Info, runId = _currentRunId, handler = "isolate" });
                return;
            }

            // Batch â€” apply print-prep across EVERY part in an assembly. Assembly-only; check before Simplifier.
            if (Batcher.IsBatchIntent((intent ?? "").Trim().ToLowerInvariant()))
            {
                var doc = SwAddin.SwApp?.ActiveDoc as IModelDoc2;
                if (doc == null) { Send(new { type = "error", message = "Open your assembly first." }); return; }
                if ((int)doc.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
                { Send(new { type = "error", message = "Open the assembly (.SLDASM) to batch-simplify its parts." }); return; }

                OpCounter.Increment();
                ForgeData.RunBegin("batch", ForgeData.Mask(intent, IntentLayer.ComponentNames(doc)), 0);
                Func<string, string, string, string, Task> emitB = async (agent, gloss, state, result) =>
                { Send(new { type = "step", agent, gloss, state, result }); await Task.Delay(60); };

                BatchResult br = await Batcher.Run(SwAddin.SwApp, doc, intent, emitB);
                Telemetry.Log("edit_run", success: br.Error == null, featuresCount: br.TotalSuppressed, swVersion: SwVer(),
                    opsUsed: OpCounter.Count, errorCode: br.Error == null ? null : "batch", questionType: "edit", questionSummary: "batch simplify parts");
                if (br.Error != null) { LogRun("batch", intent, doc, "error", false, 0, errorCode: "batch"); Send(new { type = "error", message = br.Error }); return; }
                LogRun("batch", intent, doc, "executed", br.Error == null, br.TotalSuppressed);
                Send(new { type = "answer", answer = br.Info, runId = _currentRunId, handler = "batch" });
                return;
            }

            // Print-prep / simplify. On a PART â†’ simplify that part. On an ASSEMBLY â†’ simplify ALL its parts
            // (batch), so the user doesn't have to say "all the parts". Works on either doc type.
            if (Simplifier.IsSimplifyIntent((intent ?? "").Trim().ToLowerInvariant()))
            {
                var doc = SwAddin.SwApp?.ActiveDoc as IModelDoc2;
                if (doc == null) { Send(new { type = "error", message = "Open a part or assembly first." }); return; }

                OpCounter.Increment();
                ForgeData.RunBegin("simplify", ForgeData.Mask(intent, IntentLayer.ComponentNames(doc)), 0);
                Func<string, string, string, string, Task> emitS = async (agent, gloss, state, result) =>
                { Send(new { type = "step", agent, gloss, state, result }); await Task.Delay(60); };

                if ((int)doc.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY)
                {
                    BatchResult br = await Batcher.Run(SwAddin.SwApp, doc, intent, emitS);
                    Telemetry.Log("edit_run", success: br.Error == null, featuresCount: br.TotalSuppressed, swVersion: SwVer(),
                        opsUsed: OpCounter.Count, errorCode: br.Error == null ? null : "batch", questionType: "edit", questionSummary: "batch simplify parts");
                    if (br.Error != null) { LogRun("batch", intent, doc, "error", false, 0, errorCode: "batch"); Send(new { type = "error", message = br.Error }); return; }
                    LogRun("batch", intent, doc, "executed", br.Error == null, br.TotalSuppressed);
                    Send(new { type = "answer", answer = br.Info, runId = _currentRunId, handler = "batch" });
                    return;
                }

                SimplifyResult sr = await Simplifier.Run(SwAddin.SwApp, doc, intent, emitS);
                Telemetry.Log("edit_run", success: sr.Error == null, featuresCount: sr.Suppressed, swVersion: SwVer(),
                    opsUsed: OpCounter.Count, errorCode: sr.Error == null ? null : "simplify", questionType: "edit", questionSummary: "simplify / print-prep");
                if (sr.Error != null) { LogRun("simplify", intent, doc, "error", false, 0, errorCode: "simplify"); Send(new { type = "error", message = sr.Error }); return; }
                LogRun("simplify", intent, doc, "executed", sr.Error == null, sr.Suppressed);
                Send(new { type = "answer", answer = sr.Info, runId = _currentRunId, handler = "simplify" });
                return;
            }

            var model = ActivePart();
            var dims = VariantGenerator.ReadDimensions(model);
            var deps = VariantGenerator.ReadDependencies(model);

            // The engineer can point at the exact dimension/feature by selecting it in SolidWorks.
            // Mark those so the AI targets what they pinned instead of guessing.
            var selected = GetSelectedDimNames(model, dims);
            foreach (var d in dims) d.Selected = selected.Contains(d.Name);
            var selectedFeatures = GetSelectedFeatureNames(model);

            // CAD git: share my version, or apply a teammate's pending change.
            string cmd = (intent ?? "").Trim().ToLowerInvariant();
            if (cmd.Contains("share") && (cmd.Contains("version") || cmd.Contains("change") || cmd.Contains("part") || cmd.Contains("team") || cmd.Contains("my")))
            {
                string project = ProjectKey(model);
                if (string.IsNullOrEmpty(project)) throw new Exception("Save the part before sharing.");
                await ForgeApi.PushModel(project, System.Environment.UserName, dims);
                Send(new { type = "shared", count = dims.Count });
                return;
            }
            if (_pendingIncoming != null && (cmd == "apply" || cmd.StartsWith("apply") || cmd == "accept" || cmd == "pull" || cmd.Contains("update my")))
            {
                EditResult er = VariantGenerator.ApplyEdit(SwAddin.SwApp, model, _pendingIncoming, false);
                _pendingIncoming = null;
                Send(new { type = "edit", changed = er.Changed, healthy = er.Healthy, reverted = er.Reverted, issue = er.Issue, error = er.Error });
                return;
            }

            // Carry the conversation: if Forge just asked a question, fold the earlier intent in so a
            // short reply ("do 20") resolves against what was being discussed.
            string effective = string.IsNullOrEmpty(_pendingIntent) ? intent : (_pendingIntent + ". " + intent);

            Send(new { type = "status", message = "Thinkingâ€¦" });
            ActResult act = await ForgeApi.Act(effective, dims, deps, selectedFeatures, _lastBoardChange);

            // Enforce mode: the server hit the per-operation token ceiling (assembly too large).
            if (act.Action == "ceiling")
            {
                Telemetry.Log("token_ceiling_hit", success: false, swVersion: SwVer(),
                    tokensIn: act.Meter.TokensIn, tokensOut: act.Meter.TokensOut, opsUsed: OpCounter.Count);
                Send(new { type = "error", message = "This assembly is too large for the trial version â€” contact Ravi 628-363-1774" });
                return;
            }

            // Ambiguous: ask, and remember the thread for the engineer's next reply.
            if (act.Action == "clarify")
            {
                Telemetry.Log("couldnt_map", success: false, swVersion: SwVer(), questionType: act.QuestionType, questionSummary: act.QuestionSummary);
                _pendingIntent = effective;
                Send(new { type = "clarify", message = act.Clarify });
                return;
            }
            _pendingIntent = null; // a real action happened â€” the conversation thread is resolved

            // In-place dependency-aware edit: change the part, rebuild, report what (if anything) broke.
            if (act.Action == "edit")
            {
                if (act.EditChanges == null || act.EditChanges.Count == 0)
                    throw new Exception("Forge could not map that to a change.");
                Send(new { type = "status", message = "Applying your change on a safe copyâ€¦" });
                OpCounter.Increment();
                var swE = System.Diagnostics.Stopwatch.StartNew();
                var guardE = OriginalGuard.Capture(SafeIO.GetOriginalPaths(SwAddin.SwApp, model));
                EditResult er = VariantGenerator.ApplyEdit(SwAddin.SwApp, model, act.EditChanges, act.Force);

                // Tripwire: prove the original is byte-for-byte unchanged before reporting anything.
                if (!guardE.AllIntact())
                { Telemetry.Log("guardrail_violation", success: false, swVersion: SwVer()); Send(new { type = "error", message = "Forge stopped: a safety check flagged a change to an original. Your files are untouched â€” please tell Ravi." }); return; }

                CostLedger.Record("edit_run", 1, act.Meter.TokensIn, act.Meter.TokensOut, act.Meter.EstCostUsd, act.Meter.CacheHits, act.Meter.LlmCalls);
                Telemetry.Log("edit_run", success: er.Error == null, featuresCount: act.EditChanges.Count, durationMs: swE.ElapsedMilliseconds, swVersion: SwVer(), errorCode: er.Error == null ? null : "edit_error",
                    tokensIn: act.Meter.TokensIn, tokensOut: act.Meter.TokensOut, modelName: act.Meter.ModelName, llmCalls: act.Meter.LlmCalls, cacheHits: act.Meter.CacheHits, estCostUsd: act.Meter.EstCostUsd, opsUsed: OpCounter.Count,
                    questionType: act.QuestionType, questionSummary: act.QuestionSummary);

                if (er.Reverted) _pendingIntent = effective;
                Send(new { type = "edit", changed = er.Changed, healthy = er.Healthy, reverted = er.Reverted, issue = er.Issue, error = er.Error, path = er.Path });
                return;
            }

            // Reasoning, not a change: explain the impact AND light up the affected features in 3D. Read-only.
            if (act.Action == "change_impact")
            {
                OpCounter.Increment();
                HighlightFeatures(model, act.Affected);
                CostLedger.Record("whatbreaks_query", 0, act.Meter.TokensIn, act.Meter.TokensOut, act.Meter.EstCostUsd, act.Meter.CacheHits, act.Meter.LlmCalls);
                Telemetry.Log("whatbreaks_query", success: true, featuresCount: act.Affected != null ? act.Affected.Count : 0, swVersion: SwVer(),
                    tokensIn: act.Meter.TokensIn, tokensOut: act.Meter.TokensOut, modelName: act.Meter.ModelName, llmCalls: act.Meter.LlmCalls, cacheHits: act.Meter.CacheHits, estCostUsd: act.Meter.EstCostUsd, opsUsed: OpCounter.Count,
                    questionType: act.QuestionType, questionSummary: act.QuestionSummary);
                Send(new { type = "impact", explanation = act.Explanation, affected = act.Affected });
                return;
            }

            // A direct, precise answer to a question (value/count/yes-no/short info). Crisp, not a paragraph.
            if (act.Action == "answer")
            {
                OpCounter.Increment();
                HighlightFeatures(model, act.Affected);
                CostLedger.Record("answer_query", 0, act.Meter.TokensIn, act.Meter.TokensOut, act.Meter.EstCostUsd, act.Meter.CacheHits, act.Meter.LlmCalls);
                Telemetry.Log("answer_query", success: true, swVersion: SwVer(),
                    tokensIn: act.Meter.TokensIn, tokensOut: act.Meter.TokensOut, modelName: act.Meter.ModelName, llmCalls: act.Meter.LlmCalls, cacheHits: act.Meter.CacheHits, estCostUsd: act.Meter.EstCostUsd, opsUsed: OpCounter.Count,
                    questionType: act.QuestionType, questionSummary: act.QuestionSummary);
                Send(new { type = "answer", answer = act.Answer, affected = act.Affected });
                return;
            }

            // Otherwise it's a variant-generation request.
            VariantPlan plan = act.Variant;
            if (plan == null || plan.Changes == null || plan.Changes.Count == 0 || plan.Count <= 0)
                throw new Exception("Forge could not map that request to any dimensions.");

            string what = string.Join(" + ", plan.Changes.ConvertAll(c => c.Label));
            string drawNote = (plan.Drawings || plan.DrawOriginal) ? " + drawings" : "";
            Send(new { type = "status", message = "Generating " + plan.Count + " variants (" + what + drawNote + ")â€¦" });

            // SolidWorks COM must run on the UI/STA thread â€” we are already on it.
            OpCounter.Increment();
            var swV = System.Diagnostics.Stopwatch.StartNew();
            var guardV = OriginalGuard.Capture(SafeIO.GetOriginalPaths(SwAddin.SwApp, model));
            VariantSummary summary = VariantGenerator.Generate(SwAddin.SwApp, model, plan.Changes, plan.Count, plan.Drawings, plan.DrawOriginal);

            // Tripwire: the base part and its references must be byte-for-byte unchanged.
            if (!guardV.AllIntact())
            { Telemetry.Log("guardrail_violation", success: false, swVersion: SwVer()); Send(new { type = "error", message = "Forge stopped: a safety check flagged a change to an original. Your files are untouched â€” please tell Ravi." }); return; }

            int cleanV = summary.Variants.FindAll(v => v.Success).Count;
            CostLedger.Record("variant_run", plan.Count, act.Meter.TokensIn, act.Meter.TokensOut, act.Meter.EstCostUsd, act.Meter.CacheHits, act.Meter.LlmCalls);
            Telemetry.Log("variant_run", success: cleanV > 0, partsCount: plan.Count, featuresCount: plan.Changes.Count, durationMs: swV.ElapsedMilliseconds, swVersion: SwVer(),
                tokensIn: act.Meter.TokensIn, tokensOut: act.Meter.TokensOut, modelName: act.Meter.ModelName, llmCalls: act.Meter.LlmCalls, cacheHits: act.Meter.CacheHits, estCostUsd: act.Meter.EstCostUsd, opsUsed: OpCounter.Count,
                questionType: act.QuestionType, questionSummary: act.QuestionSummary);
            if (plan.Drawings || plan.DrawOriginal) Telemetry.Log("drawing_generated", success: cleanV > 0, partsCount: cleanV, swVersion: SwVer(), opsUsed: OpCounter.Count);

            Send(new
            {
                type = "variants",
                description = plan.Description,
                dimensionName = summary.DimensionName,
                unit = summary.Unit,
                variants = summary.Variants.ConvertAll(v => new { label = v.Label, path = v.Path, drawingPath = v.DrawingPath, success = v.Success, error = v.Error, healthy = v.Healthy, issue = v.Issue })
            });
        }

        // Full names of dimensions the engineer pinned in SolidWorks â€” a dimension directly, a
        // feature/sketch (all its dims), or a cylindrical FACE (the hole/boss they clicked, matched
        // to the dimension that drives its diameter). Empty set if nothing pinned.
        private static System.Collections.Generic.HashSet<string> GetSelectedDimNames(
            IModelDoc2 model, System.Collections.Generic.List<DimInfo> dims)
        {
            var names = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var sm = (ISelectionMgr)model.SelectionManager;
                int count = sm.GetSelectedObjectCount2(-1);
                for (int i = 1; i <= count; i++)
                {
                    object o = sm.GetSelectedObject6(i, -1);
                    if (o == null) continue;

                    var dd = o as IDisplayDimension;
                    if (dd != null)
                    {
                        var d = (Dimension)dd.GetDimension2(0);
                        if (d != null) names.Add(d.FullName);
                        continue;
                    }

                    var feat = o as Feature;
                    if (feat != null)
                    {
                        var fd = (DisplayDimension)feat.GetFirstDisplayDimension();
                        while (fd != null)
                        {
                            var d = (Dimension)fd.GetDimension2(0);
                            if (d != null) names.Add(d.FullName);
                            fd = (DisplayDimension)feat.GetNextDisplayDimension(fd);
                        }
                        continue;
                    }

                    // A clicked cylindrical face (the hole/boss): read its diameter and match it to
                    // the dimension whose value equals that diameter (or radius).
                    var face = o as IFace2;
                    if (face != null)
                    {
                        var surf = face.GetSurface() as Surface;
                        if (surf != null && surf.IsCylinder())
                        {
                            double[] cp = (double[])surf.CylinderParams; // [ox,oy,oz, ax,ay,az, radius(m)]
                            if (cp != null && cp.Length >= 7)
                            {
                                double rMm = cp[6] * 1000.0;
                                double dMm = rMm * 2.0;
                                foreach (var dim in dims)
                                    if (Math.Abs(dim.ValueMm - dMm) < 0.6 || Math.Abs(dim.ValueMm - rMm) < 0.6)
                                        names.Add(dim.Name);
                            }
                        }
                    }
                }
            }
            catch { /* selection is best-effort context */ }
            return names;
        }

        // Names of features the engineer selected â€” a feature directly, or via a clicked face
        // (e.g. clicking a chamfer/fillet face). Used as the subject for impact questions.
        private static System.Collections.Generic.List<string> GetSelectedFeatureNames(IModelDoc2 model)
        {
            var names = new System.Collections.Generic.List<string>();
            try
            {
                var sm = (ISelectionMgr)model.SelectionManager;
                int count = sm.GetSelectedObjectCount2(-1);
                for (int i = 1; i <= count; i++)
                {
                    object o = sm.GetSelectedObject6(i, -1);
                    if (o == null) continue;

                    var feat = o as Feature;
                    if (feat != null) { if (!names.Contains(feat.Name)) names.Add(feat.Name); continue; }

                    var face = o as IFace2;
                    if (face != null)
                    {
                        var ff = face.GetFeature() as Feature;
                        if (ff != null && !names.Contains(ff.Name)) names.Add(ff.Name);
                    }
                }
            }
            catch { /* best-effort context */ }
            return names;
        }

        // Light up the named features in the 3D model so the engineer SEES what's affected.
        // ---- Change-impact follow-up: "show/highlight/isolate the pattern / it / that" after a change_impact run.
        //      Resolves the anaphor to the dependent we just named, selects+zooms it, and answers with its name â€”
        //      never a "one quick question". Returns true iff it handled the message. ----
        private bool TryImpactFollowup(string intent)
        {
            if (string.IsNullOrEmpty(_lastImpactPrimary)) return false;
            string i = (intent ?? "").Trim().ToLowerInvariant();
            if (i.Length == 0) return false;

            bool showVerb = System.Text.RegularExpressions.Regex.IsMatch(i,
                @"\b(show|highlight|select|isolate|zoom|find|where|which|point|light up|see)\b");
            // anaphoric reference back to what we just named (pronoun, "the pattern/feature/it", or the actual name)
            bool anaphor = System.Text.RegularExpressions.Regex.IsMatch(i,
                @"\b(it|that|those|these|them|this|the (exact |specific )?(pattern|feature|dependent|dependents|one|mate|sketch)s?)\b");
            if (!anaphor && !string.IsNullOrEmpty(_lastImpactTarget) && i.Contains(_lastImpactTarget.ToLowerInvariant())) anaphor = true;
            if (_lastImpactDependents != null)
                foreach (var d in _lastImpactDependents)
                    if (!string.IsNullOrEmpty(d) && i.Contains(d.ToLowerInvariant())) { anaphor = true; break; }
            if (!showVerb || !anaphor) return false;

            var model = SwAddin.SwApp?.ActiveDoc as IModelDoc2;
            if (model == null) { Send(new { type = "error", message = "Open the model again to show that." }); return true; }

            // pick the referenced dependent: a named one if the reply spells it, else the primary (usually the pattern)
            string pick = _lastImpactPrimary;
            if (_lastImpactDependents != null)
                foreach (var d in _lastImpactDependents)
                    if (!string.IsNullOrEmpty(d) && i.Contains(d.ToLowerInvariant())) { pick = d; break; }

            HighlightFeatures(model, new System.Collections.Generic.List<string> { pick });
            Send(new { type = "answer", answer = "Highlighted " + pick + " in the tree â€” that's the dependent of " +
                (_lastImpactTarget ?? "the feature") + ".", affected = new[] { pick }, runId = _currentRunId, handler = "change_impact" });
            return true;
        }

        private static void HighlightFeatures(IModelDoc2 model, System.Collections.Generic.List<string> names)
        {
            try
            {
                model.ClearSelection2(true);
                bool any = false;
                foreach (var n in names)
                {
                    if (string.IsNullOrEmpty(n)) continue;
                    if (model.Extension.SelectByID2(n, "BODYFEATURE", 0, 0, 0, true, 0, null, 0)) any = true;
                }
                if (any) model.ViewZoomToSelection();
            }
            catch { /* highlight is best-effort */ }
        }

        // Light up parts/features whose NAME contains any of the given substrings â€” robust to exact
        // naming. Handles assemblies (select components) and parts (select features). Used by the
        // demo panel to show a teammate's changed parts glowing.
        private void HighlightByName(dynamic msg)
        {
            var model = SwAddin.SwApp?.ActiveDoc as IModelDoc2;
            if (model == null) return;

            var subs = new System.Collections.Generic.List<string>();
            try { foreach (var m in msg.match) { var s = (string)m; if (!string.IsNullOrEmpty(s)) subs.Add(s.ToLowerInvariant()); } }
            catch { }
            if (subs.Count == 0) return;

            try
            {
                model.ClearSelection2(true);
                bool any = false;

                if ((int)model.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY)
                {
                    object[] comps = ((AssemblyDoc)model).GetComponents(false) as object[];
                    if (comps != null)
                        foreach (var o in comps)
                        {
                            var comp = o as Component2;
                            if (comp == null) continue;
                            string name = (comp.Name2 ?? "").ToLowerInvariant();
                            foreach (var sub in subs)
                                if (name.Contains(sub)) { if (comp.Select4(true, null, false)) any = true; break; }
                        }
                }
                else
                {
                    // Part: this engine is imported SURFACE/solid BODIES named per component â€” select bodies by name.
                    var partDoc = model as PartDoc;
                    if (partDoc != null)
                    {
                        object[] bodies = partDoc.GetBodies2((int)swBodyType_e.swAllBodies, false) as object[];
                        if (bodies != null)
                            foreach (var b in bodies)
                            {
                                var body = b as Body2;
                                if (body == null) continue;
                                string name = (body.Name ?? "").ToLowerInvariant();
                                foreach (var sub in subs)
                                    if (name.Contains(sub)) { if (body.Select2(true, null)) any = true; break; }
                            }
                    }
                }

                if (any) model.ViewZoomToSelection();
            }
            catch { /* best-effort */ }
        }

        private void Send(object payload)
        {
            try { PanelCapture.Log("out", payload); } catch { }
            // Send as a STRING: the panel does JSON.parse(e.data). PostWebMessageAsJson would make
            // e.data an already-parsed object, so JSON.parse throws and every message is dropped
            // (that was the "stuck on On itâ€¦" bug â€” the panel never got the done/status messages).
            string json = JsonConvert.SerializeObject(payload);
            if (_web.InvokeRequired)
                _web.BeginInvoke(new Action(() => _web.CoreWebView2.PostWebMessageAsString(json)));
            else
                _web.CoreWebView2?.PostWebMessageAsString(json);
        }
    }
}
