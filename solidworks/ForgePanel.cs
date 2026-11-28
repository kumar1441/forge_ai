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
        private string _lastRelayedClarify; // the last cloud clarify the panel relayed (repeat dead-end guard)
        private string _attachedFile;  // a file the user handed Forge via the attach button (e.g. the 2nd version for Compare)
        private readonly System.Collections.Generic.List<string> _mentionedFiles = new System.Collections.Generic.List<string>();  // @-mentioned paths for the current prompt
        private Timer _bifrost;        // polls the cross-tool feed for changes from other tools
        private NotifyIcon _notify;      // OS taskbar icon for approval/done/teammate toasts (WinForms, no new packages)
        private IntPtr _notifyHicon = IntPtr.Zero; // owned HICON from Bitmap.GetHicon (Icon.FromHandle never owns it): freed in Shutdown
        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);
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
            OpenAiCompatibleClient.SessionIdProvider = CurrentChatSid;
            _ = InitAsync();
        }

        // OpenCode Go (zen/go) requires a stable x-opencode-session per conversation. Read
        // McpBuild's current chat sid under its own Sessions lock; any reflection-shape change
        // or failure degrades to null (the client then falls back to its per-process GUID).
        private static string CurrentChatSid()
        {
            try
            {
                var t = typeof(McpBuild);
                const BindingFlags bf = BindingFlags.NonPublic | BindingFlags.Static;
                var fSessions = t.GetField("Sessions", bf);
                var fCurSid = t.GetField("CurrentSid", bf);
                if (fSessions == null || fCurSid == null) return null;
                object sessions = fSessions.GetValue(null);
                if (sessions == null) return null;
                lock (sessions) { return fCurSid.GetValue(null) as string; }
            }
            catch { return null; }
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

            // Tell the panel what's real once the page is up: first-run onboarding + the current usage-data
            // state so the Settings toggle reflects Telemetry.Enabled (host = source of truth, persisted).
            _web.CoreWebView2.NavigationCompleted += (s, e) =>
            {
                Send(new { type = "telemetryState", on = Telemetry.Enabled });
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

            // OS desktop toasts (approval arrival, run completion, teammate updates). Best-effort:
            // a missing icon or torn-down handle must never break the panel.
            InitNotify();
            LoadAutoApprove();
            LoadHostPreferences();
            try { McpBuild.NotifyHook = (tool, args) => { try { NotifyToast("Forge approval needed", tool); } catch { } }; } catch { }
            try { McpBridge.ServerToolsChanged += name => { try { Send(new { type = "mcpToolsChanged", name }); } catch { } }; } catch { }
        }

        private Timer _harness;
        private bool _harnessBusy;
        private static bool _noticesShown;   // crash-recovery + update notice, once per session
        private static bool _firstrunShown;  // onboarding overlay, once per process (panel also gates on localStorage)
        private string _currentRunId;        // per-command id: ties thumbs feedback + corrections to the run
        private int _enhanceSeq;              // Enhance supersede guard: only the newest Enhance may write
        private static System.Threading.CancellationTokenSource _runCts;     // current Generate run (Esc/stop cancels it)
        private static System.Threading.CancellationTokenSource _enhanceCts; // current Enhance call (Esc/stop cancels it)
        private static System.Threading.CancellationTokenSource _compactCts; // current Compact call: own token, linked to the run's cancel, never cancels the run
        private static int _activeRuns;                                    // >0 while a Generate await chain is in flight (loadHistory guard)

        private class QueuedFollowUp
        {
            public string id;
            public string intent;
            public string paths;
            public long createdAt;
        }
        private class SidQueue
        {
            public bool paused;
            public readonly List<QueuedFollowUp> items = new List<QueuedFollowUp>();
        }
        private static readonly object QueueLock = new object();
        private static readonly Dictionary<string, SidQueue> Queues = new Dictionary<string, SidQueue>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, bool> Dispatching = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> LastIntentBySid = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, bool> InterruptedBySid = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private static string _lastRunSid;
        private static string _activeRunSid;
        private static int CompactReservedTokens = 16000;
        private static int CompactTailTurns = 2;
        private const int McpMinTimeoutMs = 1000;

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
                SyncMcpProjectScope();
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

        private static string ActiveAssemblyPath()
        {
            try
            {
                var app = SwAddin.SwApp;
                var doc = app != null ? app.ActiveDoc as IModelDoc2 : null;
                return doc == null ? null : doc.GetPathName();
            }
            catch { return null; }
        }

        private static void SyncMcpProjectScope()
        {
            try { McpBridge.ProjectKey = McpBridge.ProjectKeyForAssembly(ActiveAssemblyPath()); }
            catch { McpBridge.ProjectKey = ""; }
        }

        private static string ConfiguredReasoningEffort(string model = null)
        {
            try
            {
                var cfg = ReadForgeConfig();
                if (!string.IsNullOrWhiteSpace(model))
                {
                    string per = (string)cfg["reasoningEffort_" + ModelKey(model)];
                    if (!string.IsNullOrWhiteSpace(per)) return per.Trim().ToLowerInvariant();
                }
                string global = (string)cfg["reasoningEffort"];
                if (!string.IsNullOrWhiteSpace(global)) return global.Trim().ToLowerInvariant();
            }
            catch { }
            return null;
        }

        public static string ConfiguredReasoningEffortFor(string model)
        {
            return ConfiguredReasoningEffort(model);
        }

        private async void BifrostTick(object sender, EventArgs e)
        {
            try
            {
                if (!Telemetry.Enabled) return;   // usage sharing OFF: no background poll — nothing leaves this machine
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
                            try { NotifyToast("Teammate shared an update", ev.Summary); } catch { }
                        }
                    }
                    else
                    {
                        _lastBoardChange = ev.Summary; // board change â€” remember for "what does it impact?"
                        Send(new { type = "bifrost", source = ev.Source, summary = ev.Summary });
                        try { NotifyToast("Board change from " + ev.Source, ev.Summary); } catch { }
                    }
                }
            }
            catch { /* polling is best-effort */ }
        }

        private async void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string raw = null;
            try { raw = e.TryGetWebMessageAsString(); } catch { return; }
            dynamic msg = null;
            try { msg = JsonConvert.DeserializeObject<dynamic>(raw); }
            catch { return; }
            if (msg == null) return;

            // Non-object payloads ("\"generate\"", "[1,2]", "42") have no .action: the binder
            // throws outside any handler try, which would escape this async-void into SW.
            string action = null;
            try { action = (string)msg.action; }
            catch { return; }
            if (string.IsNullOrEmpty(action)) return;
            if (action == "mcpSet" || action == "mcpAdd" || action == "mcpUpdate" || action == "mcpRemove" || action == "mcpStart" || action == "mcpStop") SyncMcpProjectScope();
            try { PanelCapture.Log("in", new { action, raw }); } catch { }
            try
            {
                if (action == "readModel") ReadModel();
                else if (action == "generate")
                {
                    // Garbage fields degrade to empty, never to a binder throw: a missing sid
                    // keeps the "default" chat, a non-array paths is ignored, a null intent
                    // short-circuits inside Generate.
                    string gIntent = null, gSid = null;
                    Newtonsoft.Json.Linq.JArray gPaths = null;
                    try { gIntent = (string)msg.intent; } catch { }
                    try { gSid = (string)msg.sid; } catch { }
                    try { gPaths = msg.paths as Newtonsoft.Json.Linq.JArray; } catch { }
                    await RunGenerateTracked(gIntent, gSid, gPaths, null, false, false, true);
                }
                else if (action == "queue")
                {
                    string qIntent = null, qSid = null;
                    Newtonsoft.Json.Linq.JArray qPaths = null;
                    try { qIntent = (string)msg.intent; } catch { }
                    try { qSid = (string)msg.sid; } catch { }
                    try { qPaths = msg.paths as Newtonsoft.Json.Linq.JArray; } catch { }
                    if (string.IsNullOrWhiteSpace(qIntent))
                    { Send(new { type = "error", message = "Nothing to queue — type the follow-up you want to send after this run." }); return; }
                    if (string.IsNullOrWhiteSpace(qSid)) qSid = QueueActiveSid();
                    QueueAdd(qSid, qIntent.Trim(), qPaths);
                }
                else if (action == "queueEdit")
                {
                    string eid = null, eIntent = null;
                    try { eid = (string)msg.id; } catch { }
                    try { eIntent = (string)msg.intent; } catch { }
                    if (string.IsNullOrWhiteSpace(eid) || string.IsNullOrWhiteSpace(eIntent))
                    { Send(new { type = "error", message = "A queued item needs its id and a replacement intent." }); return; }
                    string esid = QueueSidForItem(eid);
                    if (esid == null) { Send(new { type = "error", message = "Queued item not found — it may already have been sent." }); return; }
                    QueueEdit(esid, eid.Trim(), eIntent.Trim());
                }
                else if (action == "queueDelete")
                {
                    string did = null;
                    try { did = (string)msg.id; } catch { }
                    string dsid = QueueSidForItem(did);
                    if (dsid == null) { Send(new { type = "error", message = "Queued item not found — nothing to remove." }); return; }
                    QueueRemove(dsid, did.Trim());
                }
                else if (action == "queueMove")
                {
                    string mid = null; int index = -1;
                    try { mid = (string)msg.id; } catch { }
                    try { var iv = msg.index; if (iv != null) index = Convert.ToInt32((object)iv); } catch { }
                    string msid = QueueSidForItem(mid);
                    if (msid == null || index < 0) { Send(new { type = "error", message = "Couldn't reorder — give a queued item id and a target index." }); return; }
                    QueueMove(msid, mid.Trim(), index);
                }
                else if (action == "queueSendNow")
                {
                    string nid = null;
                    try { nid = (string)msg.id; } catch { }
                    string nsid = QueueSidForItem(nid);
                    if (nsid == null) { Send(new { type = "error", message = "Queued item not found — nothing to send." }); return; }
                    QueuedFollowUp item = null;
                    lock (QueueLock)
                    {
                        SidQueue q;
                        if (Queues.TryGetValue(nsid, out q))
                        {
                            for (int i = 0; i < q.items.Count; i++)
                                if (string.Equals(q.items[i].id, nid, StringComparison.OrdinalIgnoreCase)) { item = q.items[i]; q.items.RemoveAt(i); break; }
                        }
                    }
                    if (item == null) { Send(new { type = "error", message = "Queued item not found — nothing to send." }); return; }
                    SendQueued(nsid);
                    Newtonsoft.Json.Linq.JArray npaths = null;
                    try { if (!string.IsNullOrWhiteSpace(item.paths)) npaths = Newtonsoft.Json.Linq.JArray.Parse(item.paths); } catch { }
                    await RunGenerateTracked(item.intent, nsid, npaths, item.id, false, false, true, true);
                }
                else if (action == "queuePause")
                {
                    bool paused = false;
                    try { paused = (bool)msg.paused; } catch { }
                    string psid = null;
                    try { psid = (string)msg.sid; } catch { }
                    if (string.IsNullOrWhiteSpace(psid)) psid = QueueActiveSid();
                    lock (QueueLock) { QueueFor(psid).paused = paused; }
                    PersistQueue(psid);
                    SendQueueState(psid);
                    SendQueued(psid);
                }
                else if (action == "continue")
                {
                    if (System.Threading.Volatile.Read(ref _activeRuns) > 0)
                    { Send(new { type = "error", code = "busy", message = "Finish or stop the current run first, then continue — history is untouched." }); return; }
                    string csid = null;
                    try { csid = (string)msg.sid; } catch { }
                    if (string.IsNullOrWhiteSpace(csid)) csid = QueueActiveSid();
                    string last = LastIntent(csid);
                    if (string.IsNullOrWhiteSpace(last))
                    { Send(new { type = "error", message = "Nothing to continue in this chat — send a prompt first." }); return; }
                    bool interrupted;
                    lock (InterruptedBySid) { interrupted = InterruptedBySid.ContainsKey(csid) && InterruptedBySid[csid]; }
                    bool agentTurn = false;
                    try { agentTurn = McpBuild.IsMcpCreate(last); } catch { }
                    if (interrupted && agentTurn)
                    {
                        try { TryStripLastUserTurn(csid); } catch { }
                    }
                    lock (InterruptedBySid) { InterruptedBySid[csid] = false; }
                    Send(new { type = "continued", sid = csid, interrupted });
                    await RunGenerateTracked(last, csid, null, null, false, true, true);
                }
                else if (action == "fork")
                {
                    string fsid = null; int fTurn = -1;
                    try { fsid = (string)msg.sid; } catch { }
                    try { var tv = msg.turnIndex; if (tv != null) fTurn = Convert.ToInt32((object)tv); } catch { }
                    if (string.IsNullOrWhiteSpace(fsid)) fsid = QueueActiveSid();
                    string newSid;
                    try { newSid = ForkSession(fsid, fTurn >= 0 ? (int?)fTurn : null); }
                    catch (Exception ex) { Send(new { type = "error", message = "Couldn't fork that chat (" + BoundDetail(ex.Message, 200) + ") — try again." }); return; }
                    Send(new { type = "forked", sid = newSid, sourceSid = fsid });
                }
                else if (action == "share")
                {
                    string ssid = null;
                    try { ssid = (string)msg.sid; } catch { }
                    if (string.IsNullOrWhiteSpace(ssid)) ssid = QueueActiveSid();
                    DoShareTranscript(ssid);
                }
                else if (action == "diff")
                {
                    string dsid = null;
                    try { dsid = (string)msg.sid; } catch { }
                    if (string.IsNullOrWhiteSpace(dsid)) dsid = QueueActiveSid();
                    Send(new { type = "diff", sid = dsid, lines = DiffLastAnswers(dsid) });
                }
                else if (action == "timeline")
                {
                    string tsid = null;
                    try { tsid = (string)msg.sid; } catch { }
                    if (string.IsNullOrWhiteSpace(tsid)) tsid = QueueActiveSid();
                    Send(new { type = "timeline", sid = tsid, events = BuildTimeline(tsid) });
                }
                else if (action == "enhance") await EnhancePrompt((string)msg.text);
                // Transcript compaction (Kilo parity): summarize older turns so long sessions stay readable.
                // Unary call is fine — summaries are short and fast. Plain-text summary, fences stripped.
                else if (action == "compact")
                {
                    string myRun = _currentRunId; // a newer run/clear supersedes this compact: stay silent when stale
                    string text = null;
                    try { text = (string)msg.text; } catch { }
                    if (string.IsNullOrWhiteSpace(text))
                    { Send(new { type = "error", message = "Nothing to compact yet — send a prompt first, then /compact." }); return; }
                    IProviderClient c2;
                    string compactModel;
                    try { c2 = ResolveRunClient(out compactModel); } catch { c2 = null; compactModel = null; }
                    if (c2 == null)
                    {
                        string cslug = null;
                        try { var ccfg = ReadForgeConfig(); cslug = PresetSlugFor((string)ccfg["provider"], (string)ccfg["baseUrl"]); } catch { cslug = null; }
                        string cwhere = "Settings → API key";
                        if (!string.IsNullOrWhiteSpace(cslug) && !string.Equals(cslug.Trim(), "ollama", StringComparison.OrdinalIgnoreCase))
                        {
                            string cenv = Forge.Providers.ProviderFactory.EnvSlug(cslug);
                            cwhere += " (or set " + (cenv == null ? "FORGE_PROVIDER_KEY" : "FORGE_PROVIDER_KEY_" + cenv) + ")";
                        }
                        Send(new { type = "error", message = "Compaction needs a provider — save a key in " + cwhere + ", then compact again. History kept as-is.", localCompact = true }); return;
                    }
                    try
                    {
                        // Dedicated token: stop() cancels _compactCts, and it follows the active run's
                        // cancellation, but none of this ever cancels the run itself (compact is host-invoked).
                        try { if (_compactCts != null) _compactCts.Cancel(); } catch { }
                        try { if (_compactCts != null) _compactCts.Dispose(); } catch { }
                        System.Threading.CancellationToken runLink = System.Threading.CancellationToken.None;
                        try { if (_runCts != null) runLink = _runCts.Token; } catch { }
                        _compactCts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(runLink);
                        var compactCt = _compactCts.Token;
                        // A hostile/pasted transcript must not become a token blowup: summarize the
                        // recent tail (older turns matter less than what was just built).
                        string compactText = text.Trim();
                        if (compactText.Length > 8000) compactText = "…(truncated) " + compactText.Substring(compactText.Length - 8000);
                        var msgs = new List<ChatMessage>
                        {
                            new ChatMessage { Role = "system", Content = "Summarize this CAD assistant chat into 5-10 short bullets: what was built or changed, key dims and models, open issues. Plain text, no fences, no commentary." },
                            new ChatMessage { Role = "user", Content = compactText }
                        };
                        string compactUseModel = ProviderFactory.SmallModelForCurrentConfig() ?? compactModel;
                        try { McpBuild.ReasoningEffort = ConfiguredReasoningEffortFor(compactUseModel); } catch { }
                        var res = await c2.CompleteAsync(msgs,
                            new ProviderOptions { MaxTokens = 1000, Model = compactUseModel, ReasoningEffort = ConfiguredReasoningEffort(compactUseModel), Headers = ExpandedHeaderDictionary(CurrentProviderSlug()), SessionId = ActiveSidForProvider() },
                            compactCt);
                        if (myRun != _currentRunId) return; // superseded while awaiting: the new run owns the panel
                        string s = res != null ? res.Text : null;
                        try { if (res != null) { IntentRate.Note(res.TokIn, res.TokOut); McpBuild.NoteCtxCurrent(res.TokIn, res.TokOut); } } catch { }
                        if (!string.IsNullOrWhiteSpace(s))
                        {
                            s = System.Text.RegularExpressions.Regex.Replace(s.Trim(), @"^```\w*\n?|```$", "").Trim();
                            if (myRun != _currentRunId) return; // skip a stale compact result
                            Send(new { type = "compacted", summary = s });
                        }
                        else { if (myRun != _currentRunId) return; Send(new { type = "error", message = "Compaction came back empty — keeping full history; try again.", localCompact = true }); }
                    }
                    catch (OperationCanceledException)
                    {
                        // stop()/supersede killed this compact; the panel's fallback keeps the titles.
                        return;
                    }
                    catch (Exception ex)
                    {
                        if (myRun != _currentRunId) return; // skip a stale failure card
                        Send(new { type = "error", message = "Compaction failed (" + BoundDetail(ex.Message, 200) + ") — keeping full history; try again.", localCompact = true });
                    }
                }
                else if (action == "usage")   // top-bar context dropdown: session context stats + legacy daily totals
                {
                    SendUsage();
                }
                else if (action == "setCompact")
                {
                    try
                    {
                        bool on = true; int at = 0;
                        try { on = (bool)msg.on; } catch { }
                        try { var av = msg.at; if (av != null) at = Convert.ToInt32((object)av); } catch { }
                        if (at <= 0)
                        {
                            try { at = Convert.ToInt32((object)ReadForgeConfig()["compactThresholdPct"]); } catch { at = 0; }
                            if (at <= 0) { try { at = McpBuild.CompactAt; } catch { at = 0; } }
                        }
                        if (at <= 0) at = 60;
                        if (at < 1) at = 1; if (at > 100) at = 100;
                        int reserve = CompactReservedTokens, tail = CompactTailTurns;
                        try { var rv = msg.reservedTokens; if (rv != null) { int v = Convert.ToInt32((object)rv); if (v > 0) reserve = v; } } catch { }
                        try { var tv = msg.tailTurns; if (tv != null) { int v = Convert.ToInt32((object)tv); if (v > 0) tail = v; } } catch { }
                        if (tail > 10) tail = 10;
                        McpBuild.ConfigureCompaction(on, at, reserve, tail);
                        CompactReservedTokens = reserve;
                        CompactTailTurns = tail;
                        var cfg = ReadForgeConfig();
                        cfg["compactOn"] = on;
                        cfg["compactThresholdPct"] = at;
                        cfg["compactReservedTokens"] = reserve;
                        cfg["compactTailTurns"] = tail;
                        WriteForgeConfigAtomic(cfg);
                        Send(new { type = "compactConfig", on, thresholdPct = at, reservedTokens = reserve, tailTurns = tail });
                    }
                    catch { }
                }
                else if (action == "setAutoApprove")   // Kilo-style approval policy from the settings cog
                {
                    try
                    {
                        try { McpBuild.ApproveMaster = (bool)msg.master; } catch { }
                        try { McpBuild.ApproveRead = (bool)msg.read; } catch { }
                        try { McpBuild.ApproveBuild = (bool)msg.build; } catch { }
                        try { McpBuild.ApproveDestructive = (bool)msg.destructive; } catch { }
                    }
                    catch { }
                    try
                    {
                        string dir = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "Forge");
                        Directory.CreateDirectory(dir);
                        string cfgPath = Path.Combine(dir, "config.json");
                        JObject cfg;
                        try { cfg = JObject.Parse(File.ReadAllText(cfgPath)); } catch { cfg = new JObject(); }
                        cfg["autoApprove"] = new JObject
                        {
                            { "master", McpBuild.ApproveMaster },
                            { "read", McpBuild.ApproveRead },
                            { "build", McpBuild.ApproveBuild },
                            { "destructive", McpBuild.ApproveDestructive }
                        };
                        string cfgTmp = cfgPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                        try
                        {
                            File.WriteAllText(cfgTmp, cfg.ToString());
                            if (File.Exists(cfgPath)) File.Replace(cfgTmp, cfgPath, null);
                            else File.Move(cfgTmp, cfgPath);
                        }
                        catch
                        {
                            try { File.Delete(cfgTmp); } catch { }
                            throw;
                        }
                    }
                    catch { }
                }
                else if (action == "setWorkStyle")
                {
                    string style = null;
                    try { style = (string)msg.style; } catch { }
                    style = (style ?? "").Trim().ToLowerInvariant();
                    string styleNorm = style.Replace("-", "").Replace("_", "").Replace(" ", "");
                    if (styleNorm == "review" || styleNorm == "reviewfirst") style = "review-first";
                    else if (styleNorm == "autonomy" || styleNorm == "autonomous" || styleNorm == "highautonomy" || styleNorm == "auto") style = "high-autonomy";
                    if (style != "review-first" && style != "high-autonomy")
                    { Send(new { type = "error", message = "Unknown work style — use review-first or high-autonomy." }); return; }
                    try
                    {
                        var cfg = ReadForgeConfig();
                        cfg["workStyle"] = style;
                        WriteForgeConfigAtomic(cfg);
                        ApplyWorkStyle(style);
                        Send(new { type = "workStyle", style });
                        SendProviderSnapshot();
                    }
                    catch (Exception ex) { Send(new { type = "error", message = "Couldn't save work style (" + BoundDetail(ex.Message, 200) + ") — try again." }); }
                }
                else if (action == "setReasoningEffort")
                {
                    string effort = null, model = null;
                    try { effort = (string)msg.effort; } catch { }
                    try { model = (string)msg.model; } catch { }
                    effort = (effort ?? "").Trim().ToLowerInvariant();
                    string[] efforts = { "none", "low", "medium", "high", "xhigh", "max" };
                    if (Array.IndexOf(efforts, effort) < 0)
                    { Send(new { type = "error", message = "Unknown reasoning effort — use none, low, medium, high, xhigh, or max." }); return; }
                    try
                    {
                        var cfg = ReadForgeConfig();
                        cfg["reasoningEffort"] = effort;
                        string em = (model ?? "").Trim();
                        if (em.Length > 0) cfg["reasoningEffort_" + ModelKey(em)] = effort;
                        WriteForgeConfigAtomic(cfg);
                        Send(new { type = "reasoningEffort", effort, model = em.Length > 0 ? em : null });
                        SendProviderSnapshot();
                    }
                    catch (Exception ex) { Send(new { type = "error", message = "Couldn't save reasoning effort (" + BoundDetail(ex.Message, 200) + ") — try again." }); }
                }
                else if (action == "approveReply")   // verdict on a paused approval card: approve / always / reject
                {
                    try { McpBuild.ResolveApproval((string)msg.id, (string)msg.decision); }
                    catch { }
                }
                else if (action == "allowTool")   // slash /allow + settings ✕ on denied list: always-allow (or remove)
                {
                    try
                    {
                        string t = (string)msg.tool;
                        bool remove = false;
                        try { remove = (bool)msg.remove; } catch { }
                        if (!string.IsNullOrWhiteSpace(t))
                        {
                            if (remove) McpBuild.Unallow(t.Trim());
                            else McpBuild.AddAlwaysAllow(t.Trim());
                            Send(new { type = "rules", allow = McpBuild.AlwaysList(), deny = McpBuild.DenyList() });
                            if (!remove) Send(new { type = "answer", answer = "Always allowing '" + t.Trim() + "' from here on." });
                        }
                    }
                    catch { }
                }
                else if (action == "denyTool")   // slash /deny|/undeny: block (or unblock) one tool outright
                {
                    try
                    {
                        string t = (string)msg.tool;
                        bool denied = true;
                        try { denied = (bool)msg.denied; } catch { }
                    if (!string.IsNullOrWhiteSpace(t) && McpBuild.SetDenied(t.Trim(), denied))
                    {
                        Send(new { type = "rules", allow = McpBuild.AlwaysList(), deny = McpBuild.DenyList() });
                        Send(new { type = "answer", answer = denied ? "Denied '" + t.Trim() + "' — the agent will work around it." : "Un-denied '" + t.Trim() + "' — back to normal policy." });
                    }
                    }
                    catch { }
                }
                else if (action == "toolPolicyGet")   // per-tool Ask dropdowns: union of live tools + seen tools + explicit policy
                {
                    try { await SendToolPolicyAsync(); } catch { }
                }
                else if (action == "toolPolicySet")   // {tool, mode: allow|ask|deny|clear} -> persist + refresh rules+toolPolicy
                {
                    try
                    {
                        string t = null, mode = null;
                        try { t = (string)msg.tool; } catch { }
                        try { mode = (string)msg.mode; } catch { }
                        if (!string.IsNullOrWhiteSpace(t))
                        {
                            string m = (mode ?? "").Trim().ToLowerInvariant();
                            if (m == "clear" || m == "" || m == "null" || m == "default") McpBuild.SetToolPolicy(t.Trim(), null);
                            else if (m == "allow" || m == "ask" || m == "deny") McpBuild.SetToolPolicy(t.Trim(), m);
                            else { Send(new { type = "error", message = "Unknown tool policy mode '" + mode + "' — use allow, ask, deny, or clear." }); return; }
                            Send(new { type = "rules", allow = McpBuild.AlwaysList(), deny = McpBuild.DenyList() });
                            try { await SendToolPolicyAsync(); } catch { }
                        }
                    }
                    catch { }
                }
                else if (action == "attach") DoAttach();
                else if (action == "pasteAttach")
                {
                    try { ApplyPasteAttach(msg.files as JArray); }
                    catch (Exception ex) { Send(new { type = "error", message = "Couldn't attach pasted files (" + BoundDetail(ex.Message, 200) + ") — try again." }); }
                }
                else if (action == "initContext") { DoInitContext(msg); }
                else if (action == "clearAttach") { _attachedFile = null; lock (_mentionedFiles) { _mentionedFiles.Clear(); } Send(new { type = "attachCleared" }); }
                else if (action == "revokeShare")   // no server revoke route exists in this client: be honest, links auto-expire
                {
                    Send(new { type = "answer", answer = "Revoke isn't wired to the share server in this build — shared links expire automatically after 7 days." });
                }

                // @-mention file search (Kilo parity): open SolidWorks documents first (most likely
                // targets), then the active document's folder (depth 2, CAD files first), filtered by q.
                else if (action == "files")
                {
                    try
                    {
                        string q = "";
                        try { q = ((string)msg.q ?? "").Trim().ToLowerInvariant(); } catch { }
                        var out_ = new Newtonsoft.Json.Linq.JArray();
                        var seen = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
                        try
                        {
                            var app = SwAddin.SwApp;
                            if (app != null)
                            {
                                object docs = null;
                                try { docs = app.GetDocuments(); } catch { }
                                var arr = docs as object[];
                                if (arr != null) foreach (var d in arr)
                                {
                                    var md = d as IModelDoc2;
                                    string p = null;
                                    try { p = md != null ? md.GetPathName() : null; } catch { }
                                    if (string.IsNullOrWhiteSpace(p) || !System.IO.File.Exists(p)) continue;
                                    string nm = System.IO.Path.GetFileName(p);
                                    if (!string.IsNullOrEmpty(q) && nm.ToLowerInvariant().IndexOf(q, System.StringComparison.Ordinal) < 0) continue;
                                    if (seen.Add(p)) out_.Add(new Newtonsoft.Json.Linq.JObject { ["name"] = nm, ["path"] = p, ["open"] = true });
                                    if (out_.Count >= 50) break;
                                }
                            }
                        }
                        catch { }
                        try
                        {
                            string dir = null;
                            try
                            {
                                var app2 = SwAddin.SwApp;
                                var doc = app2 != null ? app2.ActiveDoc as IModelDoc2 : null;
                                string pn = doc != null ? doc.GetPathName() : null;
                                if (!string.IsNullOrWhiteSpace(pn)) dir = System.IO.Path.GetDirectoryName(pn);
                            }
                            catch { }
                            if (string.IsNullOrWhiteSpace(dir)) dir = System.IO.Directory.GetCurrentDirectory();
                            if (System.IO.Directory.Exists(dir) && out_.Count < 50)
                            {
                                var files = System.IO.Directory.GetFiles(dir, "*", System.IO.SearchOption.TopDirectoryOnly);
                                var sub = new string[0];
                                try { sub = System.IO.Directory.GetDirectories(dir); } catch { }
                                var all = new System.Collections.Generic.List<string>(files);
                                foreach (var s in sub)
                                {
                                    if (all.Count >= 200) break;
                                    try { all.AddRange(System.IO.Directory.GetFiles(s, "*", System.IO.SearchOption.TopDirectoryOnly)); } catch { }
                                }
                                all.Sort((a, b) => CadScore(b).CompareTo(CadScore(a)));
                                foreach (var f in all)
                                {
                                    if (out_.Count >= 50) break;
                                    string nm = System.IO.Path.GetFileName(f);
                                    if (!string.IsNullOrEmpty(q) && nm.ToLowerInvariant().IndexOf(q, System.StringComparison.Ordinal) < 0) continue;
                                    if (seen.Add(f)) out_.Add(new Newtonsoft.Json.Linq.JObject { ["name"] = nm, ["path"] = f, ["open"] = false });
                                }
                            }
                        }
                        catch { }
                        Send(new { type = "files", files = out_ });
                    }
                    catch (Exception ex) { Send(new { type = "error", message = "File search failed (" + BoundDetail(ex.Message, 200) + ") — try again or type the file name." }); }
                }
                else if (action == "stop")   // Kilo-style interrupt: cancel the in-flight run/enhance/compact; partial CoT stays
                {
                    try { if (_runCts != null) _runCts.Cancel(); } catch { }
                    try { if (_enhanceCts != null) _enhanceCts.Cancel(); } catch { }
                    try { if (_compactCts != null) _compactCts.Cancel(); } catch { }
                    try
                    {
                        string stopSid = !string.IsNullOrWhiteSpace(_activeRunSid) ? _activeRunSid : _lastRunSid;
                        if (!string.IsNullOrWhiteSpace(stopSid)) { _lastRunSid = stopSid; lock (InterruptedBySid) { InterruptedBySid[stopSid] = true; } }
                    }
                    catch { }
                    _pendingIntent = null; _pendingConfirm = null; _attachedFile = null;
                }
                else if (action == "revert")   // Kilo-style revert: drop the pending thread + truncate the agent chat so the resent prompt starts clean
                {
                    // Stop the in-flight run first: otherwise it keeps appending answers after the truncate.
                    try { if (_runCts != null) _runCts.Cancel(); } catch { }
                    _pendingIntent = null; _pendingConfirm = null;
                    _lastImpactTarget = null; _lastImpactPrimary = null; _lastImpactDependents = null;
                    try
                    {
                        string rsid = null;
                        try { rsid = (string)msg.sid; } catch { }
                        // keep arrives as int normally, but a string/double/null from the panel
                        // must never silently become 0 (which wipes to the system prompt).
                        int keep = 0; bool haveKeep = false;
                        try
                        {
                            // msg.keep is a dynamic JToken: `is long` never matches a JValue, so
                            // convert instead of type-testing (int, double, and numeric strings all land).
                            var k = msg.keep;
                            if (k != null)
                            {
                                bool ok = false; int v = 0;
                                try { string ks = (string)k; ok = !string.IsNullOrWhiteSpace(ks) && int.TryParse(ks.Trim(), out v); }
                                catch { ok = false; }
                                if (!ok) { try { double kd = (double)k; v = (int)kd; ok = true; } catch { ok = false; } }
                                if (ok) { keep = v; haveKeep = true; }
                            }
                        }
                        catch { haveKeep = false; }
                        if (string.IsNullOrWhiteSpace(rsid)) McpBuild.ClearHistory();
                        else if (!haveKeep) Send(new { type = "error", message = "Couldn't rewind — I lost track of which turn to keep to, so history is untouched. Click a turn's ↩ revert button to rewind." });
                        else McpBuild.TruncateHistory(rsid, keep);
                        try { SendUsage(); } catch { } // pill recomputes from kept history immediately
                    }
                    catch { }
                }
                else if (action == "loadHistory")   // restore a chat: load its persisted agent conversation
                {
                    // EnsureSessionPublic flips CurrentSid; doing that mid-run makes NoteCtxCurrent (and the
                    // pill) credit the loaded chat instead of the one still executing. Refuse until it ends.
                    if (System.Threading.Volatile.Read(ref _activeRuns) > 0)
                    {
                        Send(new { type = "error", code = "busy", message = "Finish or stop the current run first, then load a chat — history is untouched." });
                        return;
                    }
                    try
                    {
                        string lsid = null;
                        try { lsid = (string)msg.sid; } catch { }
                        if (!string.IsNullOrWhiteSpace(lsid))
                        {
                            McpBuild.EnsureSessionPublic(lsid);
                            var turnsData = BuildHostTurns(lsid);
                            Send(new { type = "historyLoaded", turns = McpBuild.HistoryCount(), sid = lsid, turns_data = turnsData });
                        }
                    }
                    catch (Exception ex) { Send(new { type = "error", message = "Couldn't load chat history (" + BoundDetail(ex.Message, 200) + ") — try again." }); }
                }
                else if (action == "mcpConfig")
                {
                    try
                    {
                        string cmd, args; bool enabled; int tmo;
                        McpBridge.DescribeFull(out cmd, out args, out enabled, out tmo);
                        bool running = false;
                        try { running = McpBridge.IsRunning; } catch { }
                        Send(new { type = "mcpConfig", enabled = enabled, timeoutMs = tmo, command = cmd, args = args, running = running, servers = AttachMcpOAuthStatus(McpBridge.DescribeAll()) });
                    }
                    catch (Exception ex) { Send(new { type = "error", message = "Couldn't read MCP config (" + BoundDetail(ex.Message, 200) + ") — check Settings → MCP server, then try again." }); }
                }
                else if (action == "mcpSet")
                {
                    try
                    {
                        bool hasEnabled = false;
                        try { hasEnabled = msg.enabled != null; } catch { }
                        int tmo = 0; bool hasTmo = false;
                        try { object to = msg.timeoutMs; if (to != null) { tmo = Convert.ToInt32(to); hasTmo = true; } } catch { }
                        if (hasTmo && tmo < McpMinTimeoutMs)
                        { Send(new { type = "error", message = "Timeout must be \u22651 second (1000 ms) — e.g. /mcps timeout <name> 10." }); }
                        else
                        {
                            string setName = null;
                            try { setName = (string)msg.name; } catch { }
                            string resolved, rerr;
                            if (!TryResolveServerName(setName, out resolved, out rerr))
                            { Send(new { type = "error", message = rerr + " Change takes effect on the next build." }); }
                            else
                            {
                                string canonical;
                                string werr = McpWriteServer(msg, resolved, false, out canonical);
                                if (werr != null) Send(new { type = "error", message = werr });
                                else
                                {
                                    SendMcpConfig();
                                    bool en2 = true; int t2 = tmo;
                                    try
                                    {
                                        foreach (var s in McpBridge.LoadEffectiveServers())
                                            if (s != null && string.Equals(s.Name, resolved, System.StringComparison.OrdinalIgnoreCase)) { en2 = s.Enabled; t2 = s.TimeoutMs; break; }
                                    }
                                    catch { }
                                    string word = hasEnabled ? (en2 ? "enabled" : "disabled") + " " : "updated ";
                                    Send(new { type = "answer", answer = "MCP server '" + canonical + "' " + word + "(" + (t2 / 1000) + "s timeout). Change takes effect on the next build." });
                                }
                            }
                        }
                    }
                    catch (Exception ex) { Send(new { type = "error", message = "Couldn't save MCP settings (" + BoundDetail(ex.Message, 200) + ") — check Settings → MCP server, then try again." }); }
                }
                else if (action == "mcpAdd")
                {
                    try
                    {
                        string addName = null;
                        try { addName = (string)msg.name; } catch { }
                        string canonical;
                        string werr = McpWriteServer(msg, addName, true, out canonical);
                        if (werr != null) Send(new { type = "error", message = werr });
                        else
                        {
                            SendMcpConfig();
                            Send(new { type = "answer", answer = "MCP server '" + canonical + "' added — it joins the failover order for the next build." });
                        }
                    }
                    catch (Exception ex) { Send(new { type = "error", message = "Couldn't add the MCP server (" + BoundDetail(ex.Message, 200) + ") — check Settings → MCP server, then try again." }); }
                }
                else if (action == "mcpUpdate")
                {
                    try
                    {
                        string updName = null;
                        try { updName = (string)msg.name; } catch { }
                        string canonical;
                        string werr = McpWriteServer(msg, updName, false, out canonical);
                        if (werr != null) Send(new { type = "error", message = werr });
                        else SendMcpConfig();
                    }
                    catch (Exception ex) { Send(new { type = "error", message = "Couldn't update the MCP server (" + BoundDetail(ex.Message, 200) + ") — check Settings → MCP server, then try again." }); }
                }
                else if (action == "mcpRemove")
                {
                    try
                    {
                        string rmName = null;
                        try { rmName = (string)msg.name; } catch { }
                        if (string.IsNullOrWhiteSpace(rmName))
                        { Send(new { type = "error", message = "Couldn't remove the MCP server — no name was sent." }); }
                        else
                        {
                            McpBridge.RemoveServer(rmName.Trim());
                            SendMcpConfig();
                            Send(new { type = "answer", answer = "MCP server '" + rmName.Trim() + "' removed." });
                        }
                    }
                    catch (Exception ex) { Send(new { type = "error", message = "Couldn't remove the MCP server (" + BoundDetail(ex.Message, 200) + ") — check Settings → MCP server, then try again." }); }
                }
                else if (action == "mcpStart")
                {
                    try
                    {
                        string stName = null;
                        try { stName = (string)msg.name; } catch { }
                        string resolved, rerr;
                        if (!TryResolveServerName(stName, out resolved, out rerr))
                        { Send(new { type = "error", message = rerr }); }
                        else
                        {
                            await McpBridge.EnsureStartedAsync(resolved, 0, System.Threading.CancellationToken.None);
                            SendMcpConfig();
                            Send(new { type = "answer", answer = "MCP server '" + resolved + "' started." });
                        }
                    }
                    catch (OperationCanceledException) { Send(new { type = "error", message = "MCP start stopped — resend to try again." }); }
                    catch (Exception ex) { Send(new { type = "error", message = "Couldn't start the MCP server (" + BoundDetail(ex.Message, 200) + ") — check Settings → MCP server, then try again." }); }
                }
                else if (action == "mcpStop")
                {
                    try
                    {
                        string spName = null;
                        try { spName = (string)msg.name; } catch { }
                        string resolved, rerr;
                        if (!TryResolveServerName(spName, out resolved, out rerr))
                        { Send(new { type = "error", message = rerr }); }
                        else
                        {
                            McpBridge.Stop(resolved);
                            SendMcpConfig();
                            Send(new { type = "answer", answer = "MCP server '" + resolved + "' stopped." });
                        }
                    }
                    catch (Exception ex) { Send(new { type = "error", message = "Couldn't stop the MCP server (" + BoundDetail(ex.Message, 200) + ") — it may already be stopped." }); }
                }
                else if (action == "mcpOAuthStart")
                {
                    string oaName = null;
                    try { oaName = (string)msg.name; } catch { }
                    string resolved, rerr;
                    if (!TryResolveServerName(oaName, out resolved, out rerr))
                    { Send(new { type = "error", message = rerr }); }
                    else
                    {
                        string st = "none";
                        try
                        {
                            bool ok = false;
                            using (var oats = new System.Threading.CancellationTokenSource(System.TimeSpan.FromMinutes(6)))
                            { try { ok = await McpBridge.StartOAuthAsync(resolved, oats.Token); } catch (System.OperationCanceledException) { ok = false; } }
                            try { st = McpBridge.OAuthStatus(resolved) ?? "none"; } catch { st = "none"; }
                            Send(new { type = "mcpAuthStatus", name = resolved, status = st, needsAuth = McpBridge.IsOAuthRequired(resolved) });
                            if (ok) SendMcpConfig();
                            else Send(new { type = "error", message = "OAuth sign-in for '" + resolved + "' did not complete (status: " + st + ") — run Sign in again and approve the browser request." });
                        }
                        catch (System.OperationCanceledException) { Send(new { type = "error", message = "OAuth sign-in for '" + resolved + "' stopped — resend to try again." }); }
                        catch (System.Exception ex)
                        {
                            try { st = McpBridge.OAuthStatus(resolved) ?? "none"; } catch { st = "none"; }
                            Send(new { type = "mcpAuthStatus", name = resolved, status = st, needsAuth = McpBridge.IsOAuthRequired(resolved) });
                            Send(new { type = "error", message = "Couldn't sign in '" + resolved + "' (" + BoundDetail(ex.Message, 200) + ") — check the oauth block in Settings → MCP server, then try again." });
                        }
                    }
                }
                else if (action == "mcpOAuthClear")
                {
                    string ocName = null;
                    try { ocName = (string)msg.name; } catch { }
                    string resolved, rerr;
                    if (!TryResolveServerName(ocName, out resolved, out rerr))
                    { Send(new { type = "error", message = rerr }); }
                    else
                    {
                        try { McpBridge.ClearOAuthToken(resolved); } catch { }
                        string st = "none";
                        try { st = McpBridge.OAuthStatus(resolved) ?? "none"; } catch { st = "none"; }
                        bool need = false;
                        try { need = McpBridge.IsOAuthRequired(resolved); } catch { need = false; }
                        Send(new { type = "mcpAuthStatus", name = resolved, status = st, needsAuth = need });
                        SendMcpConfig();
                        Send(new { type = "answer", answer = "MCP server '" + resolved + "' signed out — run Sign in before the next build." });
                    }
                }
                else if (action == "mcpLog")
                {
                    string lname = null;
                    try { lname = (string)msg.name; } catch { }
                    string text = "";
                    try { text = McpBridge.ServerLog(string.IsNullOrWhiteSpace(lname) ? null : lname.Trim()) ?? ""; } catch { }
                    Send(new { type = "mcpLog", name = lname, text });
                }
                else if (action == "mcpPing")
                {
                    string pingName = null;
                    try { pingName = (string)msg.name; } catch { }
                    try
                    {
                        string target = string.IsNullOrWhiteSpace(pingName) ? null : pingName.Trim();
                        var pingWatch = System.Diagnostics.Stopwatch.StartNew();
                        bool pingOk = false;
                        try { pingOk = await McpBridge.PingAsync(target, System.Threading.CancellationToken.None); } catch { pingOk = false; }
                        pingWatch.Stop();
                        int pingRtt = (int)pingWatch.ElapsedMilliseconds;
                        string pingDisplay = target;
                        try { string pr, pe; if (TryResolveServerName(target, out pr, out pe)) pingDisplay = pr; } catch { }
                        if (pingOk) Send(new { type = "mcpPing", name = pingDisplay, ok = true, rttMs = pingRtt });
                        else Send(new { type = "mcpPing", name = pingDisplay, ok = false, rttMs = pingRtt, hint = "No reply — start the server in Settings → MCP servers, then ping again." });
                    }
                    catch { try { Send(new { type = "mcpPing", name = pingName, ok = false, rttMs = 0, hint = "Ping failed before a reply — check Settings → MCP servers, then try again." }); } catch { } }
                }
                else if (action == "reset")   // rehearsal/recording: wipe the panel + every pending thread for a clean take
                {
                    try { if (_runCts != null) _runCts.Cancel(); } catch { }
                    try { if (_enhanceCts != null) _enhanceCts.Cancel(); } catch { }
                    try { if (_compactCts != null) _compactCts.Cancel(); } catch { }
                    _currentRunId = null; // superseded runs go silent: late answers + think deltas must not land in the clean take
                    _pendingIntent = null; _attachedFile = null; _pendingConfirm = null;
                    _pendingIncoming = null; _lastBoardChange = null; _lastRelayedClarify = null;
                    lock (_mentionedFiles) { _mentionedFiles.Clear(); }
                    _lastImpactTarget = null; _lastImpactPrimary = null; _lastImpactDependents = null;
                    try { McpBuild.ClearHistory(); } catch { } // fresh agent conversation (+ fails orphaned approval gates)
                    try { McpBuild.CardSender = null; } catch { } // drop the dead run's card sender with its approvals
                    Send(new { type = "attachCleared" });
                }
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
                    string key = null, provider = null, model = null;
                    try { key = (string)msg.key; } catch { }
                    try { provider = (string)msg.provider; } catch { }
                    try { model = (string)msg.model; } catch { }
                    if (string.IsNullOrWhiteSpace(key))
                    { Send(new { type = "answer", answer = "No key entered — staying on the keyless local path." }); return; }
                    try
                    {
                        var keyMissing = new List<string>();
                        string xkey = ExpandEnvTokens(key.Trim(), keyMissing);
                        SaveProviderKey(provider, xkey, model);
                        Send(new { type = "answer", answer = "Provider key saved — encrypted with Windows secure storage on this machine; it never leaves the device." });
                        if (keyMissing.Count > 0) Send(new { type = "answer", answer = "Warning: unresolved environment variable(s): " + string.Join(", ", keyMissing.ToArray()) + " — left as literal text in the saved key; set them and re-save to expand." });
                        // Confirm the new key state to the panel (pill/ctx/settings) directly:
                        // the panel's follow-up getProvider can race this handler on a busy UI thread.
                        try
                        {
                            var scfg = ReadForgeConfig();
                            string sp2 = (string)scfg["provider"], sbu2 = (string)scfg["baseUrl"];
                            string sslug2 = PresetSlugFor(sp2, sbu2);
                            Send(new { type = "provider", slug = sslug2, provider = sp2, baseUrl = sbu2, displayName = ProviderFactory.DisplayNameFor(sp2, sbu2), model = (string)scfg["model"], dynamic = IsDynamicProvider(sp2), hasKey = !string.IsNullOrWhiteSpace(StoredProviderKeyForSlug(sslug2, sp2)), customModels = GetCustomModels(sslug2), customProviders = GetCustomProviders() });
                        }
                        catch { }
                    }
                    catch (Exception ex) { Send(new { type = "error", message = "Couldn't save the key (" + BoundDetail(ex.Message, 200) + ") — check the key and try again." }); }
                }

                // Settings → API key → "Remove saved key": the panel may hint a slug, but the
                // host derives the real one (its own config is the source of truth) and only
                // deletes that per-slug key file. KeyStore.Delete never throws. A fresh provider
                // payload follows so hasKey/the key row repaint (opencode resumes keyless public).
                else if (action == "keyRemove")
                {
                    string rslug = null;
                    try { rslug = (string)msg.slug; } catch { }
                    rslug = SanitizeCustomSlug(rslug);
                    if (string.IsNullOrWhiteSpace(rslug))
                    {
                        try
                        {
                            var rcfg = ReadForgeConfig();
                            rslug = PresetSlugFor((string)rcfg["provider"], (string)rcfg["baseUrl"]);
                        }
                        catch { }
                        rslug = SanitizeCustomSlug(rslug);
                    }
                    if (string.IsNullOrWhiteSpace(rslug))
                    { Send(new { type = "error", message = "No provider to remove a key for — pick one first." }); return; }
                    try
                    {
                        KeyStore.Delete(rslug);
                        var cfg = ReadForgeConfig();
                        string sp = (string)cfg["provider"], sbu = (string)cfg["baseUrl"];
                        string sslug = PresetSlugFor(sp, sbu);
                        Send(new { type = "provider", slug = sslug, provider = sp, baseUrl = sbu, displayName = ProviderFactory.DisplayNameFor(sp, sbu), model = (string)cfg["model"], dynamic = IsDynamicProvider(sp), hasKey = !string.IsNullOrWhiteSpace(StoredProviderKeyForSlug(sslug, sp)), customModels = GetCustomModels(sslug), customProviders = GetCustomProviders() });
                        Send(new { type = "answer", answer = "Saved key removed for " + rslug + (string.Equals(rslug, "opencode", StringComparison.OrdinalIgnoreCase) ? " — keyless public models are available again." : " — paste a new key in Settings → API key when you need one.") });
                    }
                    catch (Exception ex) { Send(new { type = "error", message = "Couldn't remove the saved key (" + BoundDetail(ex.Message, 200) + ") — try again." }); }
                }

                // Dynamic model list: the panel asks the host to GET {baseUrl}/models with the
                // pasted key (no browser CORS issues, key never touches page storage) and returns
                // the servable chat-model ids so the user picks from what's live right now.
                else if (action == "fetchModels")
                {
                    string key = null, provider = null;
                    try { key = (string)msg.key; } catch { }
                    try { provider = (string)msg.provider; } catch { }
                    // Sanitize at entry (panel parity): key/config lookups below must use
                    // the same slug PresetSlugFor returns, never the raw pasted text.
                    string fslug = SanitizeCustomSlug(provider);
                    if (string.IsNullOrWhiteSpace(fslug))
                    { Send(new { type = "models", models = new string[0], modelMeta = new JArray(), error = "Unknown provider '" + provider + "' — pick a provider from the dropdown, then Refresh." }); return; }
                    var preset = ProviderPresetFor(fslug);
                    // Anthropic-wire endpoints (built-in or anthropic-kind custom) don't serve an
                    // OpenAI-style Bearer /models list — fail fast with guidance instead of a raw HTTP error.
                    if (string.Equals(preset.Provider, "anthropic", StringComparison.OrdinalIgnoreCase))
                    { Send(new { type = "models", models = new string[0], modelMeta = new JArray(), error = "This endpoint has no OpenAI-style model list — add the model id under Custom models instead." }); return; }
                    // Ollama and OpenCode Zen serve /models with no key (same exemption as
                    // refreshModels): only keyed providers fail closed here. OpenCode's keyless
                    // GET /models uses the literal bearer token "public" (free models only).
                    bool flocal = string.Equals(fslug, "ollama", StringComparison.OrdinalIgnoreCase)
                               || string.Equals(fslug, "opencode", StringComparison.OrdinalIgnoreCase);
                    if (string.IsNullOrWhiteSpace(key)) key = StoredProviderKeyForSlug(fslug, preset.Provider);
                    if (string.IsNullOrWhiteSpace(key) && !flocal)
                    { Send(new { type = "models", models = new string[0], modelMeta = new JArray(), error = "nokey" }); return; }
                    if (string.Equals(fslug, "opencode", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(key)) key = "public";
                    try
                    {
                        var ids = await FetchServableModels(preset.BaseUrl, (key ?? "").Trim(), fslug);
                        Send(new { type = "models", models = ids, modelMeta = ModelMetaArray(ids, fslug), selected = preset.Model });
                    }
                    catch (Exception ex) { Send(new { type = "models", models = new string[0], modelMeta = new JArray(), error = MapModelsError(ex) }); }
                }

                // Persistent chat model picker (Kilo-style): current provider/model for the pill,
                // live refresh using the STORED key (no paste needed), and switching the model.
                else if (action == "getProvider")
                {
                    try
                    {
                        var cfg = ReadForgeConfig();
                        string p = (string)cfg["provider"];
                        string bu = (string)cfg["baseUrl"];
                        string gslug = PresetSlugFor(p, bu);
                        Send(new { type = "provider", slug = gslug, provider = p, baseUrl = bu, displayName = ProviderFactory.DisplayNameFor(p, bu), model = (string)cfg["model"], dynamic = IsDynamicProvider(p), hasKey = !string.IsNullOrWhiteSpace(StoredProviderKeyForSlug(gslug, p)), customModels = GetCustomModels(gslug), customProviders = GetCustomProviders() });
                        Send(new { type = "rules", allow = McpBuild.AlwaysList(), deny = McpBuild.DenyList() });
                    }
                    catch (Exception ex) { Send(new { type = "provider", error = "Couldn't load provider settings (" + BoundDetail(ex.Message, 200) + ") — try again." }); }
                }
                else if (action == "setProvider")
                {
                    string provider = null;
                    try { provider = (string)msg.provider; } catch { }
                    try
                    {
                        // Sanitize at entry (panel parity): KeyStore/config keys are read
                        // back via PresetSlugFor (sanitized), so switch on the same slug.
                        string slug = SanitizeCustomSlug(provider);
                        if (string.IsNullOrWhiteSpace(slug))
                            throw new Exception("Unknown provider '" + provider + "'.");
                        if (Array.IndexOf(KnownSlugs, slug) < 0 && FindCustomProvider(slug) == null)
                            throw new Exception("Unknown provider '" + provider + "'.");
                        var preset = ProviderPresetFor(slug);
                        var cfg = ReadForgeConfig();
                        // Remember the outgoing provider's model so switching back restores
                        // the user's pick instead of resetting to the preset default.
                        try
                        {
                            string prevSlug = PresetSlugFor((string)cfg["provider"], (string)cfg["baseUrl"]);
                            string prevModel = (string)cfg["model"];
                            if (!string.IsNullOrWhiteSpace(prevSlug) && !string.IsNullOrWhiteSpace(prevModel)
                                && !string.Equals(prevSlug, slug, StringComparison.OrdinalIgnoreCase))
                                cfg["model_" + prevSlug] = prevModel.Trim();
                        }
                        catch { }
                        cfg["provider"] = preset.Provider;
                        cfg["baseUrl"] = preset.BaseUrl;
                        string remembered = null;
                        try { remembered = (string)cfg["model_" + slug]; } catch { }
                        cfg["model"] = !string.IsNullOrWhiteSpace(remembered) ? remembered.Trim() : preset.Model;
                        WriteForgeConfig(cfg);
                        // New window, new budget: drop the old provider's context totals so the
                        // pill recomputes from zero instead of old-cur/new-window (spurious % + compact).
                        try { ResetCtxForModelSwitch(); } catch { }
                        string p = preset.Provider;
                        string keptModel = null;
                        try { keptModel = (string)cfg["model"]; } catch { }
                        Send(new { type = "provider", slug = slug, provider = p, baseUrl = preset.BaseUrl, displayName = ProviderFactory.DisplayNameFor(p, preset.BaseUrl), model = keptModel ?? preset.Model, dynamic = IsDynamicProvider(p), hasKey = !string.IsNullOrWhiteSpace(StoredProviderKeyForSlug(slug, p)), customModels = GetCustomModels(slug), customProviders = GetCustomProviders() });
                        string dispName = ProviderFactory.DisplayNameFor(p, preset.BaseUrl);
                        string freeClause = string.Equals(slug, "opencode", StringComparison.OrdinalIgnoreCase)
                            ? " — no key needed, free models only (public)."
                            : " — pick another model from the pill if you like.";
                        Send(new { type = "answer", answer = "Provider switched to " + dispName + " (" + slug + ") · " + (keptModel ?? preset.Model) + freeClause });
                    }
                    catch (Exception ex) { Send(new { type = "error", message = "Couldn't switch provider (" + BoundDetail(ex.Message, 200) + ") — check the selection/key and try again." }); }
                }
                else if (action == "refreshModels")
                {
                    try
                    {
                        var cfg = ReadForgeConfig();
                        string p = (string)cfg["provider"];
                        string baseUrl = (string)cfg["baseUrl"];
                        if (!IsDynamicProvider(p)) throw new Exception("This provider has a fixed model.");
                        if (string.IsNullOrWhiteSpace(baseUrl)) throw new Exception("No provider configured yet.");
                        string rslug = null;
                        try { rslug = PresetSlugFor(p, baseUrl); } catch { rslug = null; }
                        bool rlocal = !string.IsNullOrWhiteSpace(rslug) && string.Equals(rslug.Trim(), "ollama", StringComparison.OrdinalIgnoreCase);
                        string key = StoredProviderKeyForSlug(PresetSlugFor(p, baseUrl), p);
                        // OpenCode Zen keyless path: GET /models also uses "public" (free models).
                        bool ropencode = string.Equals(rslug, "opencode", StringComparison.OrdinalIgnoreCase);
                        if (ropencode && string.IsNullOrWhiteSpace(key)) key = "public";
                        if (string.IsNullOrWhiteSpace(key) && !rlocal && !ropencode) throw new Exception("No key saved for " + (rslug ?? p) + " — save one in Settings → API key (or set " + (Forge.Providers.ProviderFactory.EnvSlug(rslug) == null ? "FORGE_PROVIDER_KEY" : "FORGE_PROVIDER_KEY_" + Forge.Providers.ProviderFactory.EnvSlug(rslug)) + "), then Refresh.");
                        var ids = await FetchServableModels(baseUrl, (key ?? "").Trim(), rslug);
                        Send(new { type = "models", models = ids, modelMeta = ModelMetaArray(ids, rslug), selected = (string)cfg["model"] });
                    }
                    catch (Exception ex) { Send(new { type = "models", models = new string[0], modelMeta = new JArray(), error = MapModelsError(ex) }); }
                }
                else if (action == "setModel")
                {
                    string model = null;
                    try { model = (string)msg.model; } catch { }
                    if (string.IsNullOrWhiteSpace(model))
                    { Send(new { type = "error", message = "No model picked — choose one in the model picker (▲) or Settings → Provider, then try again." }); return; }
                    try
                    {
                        var cfg = ReadForgeConfig();
                        cfg["model"] = model.Trim();
                        try
                        {
                            string sms = PresetSlugFor((string)cfg["provider"], (string)cfg["baseUrl"]);
                            if (!string.IsNullOrWhiteSpace(sms)) cfg["model_" + sms] = model.Trim();
                        }
                        catch { }
                        WriteForgeConfig(cfg);
                        // New window, new budget: drop the old model's context totals so the
                        // pill recomputes from zero instead of old-cur/new-window (spurious % + compact).
                        try { ResetCtxForModelSwitch(); } catch { }
                        string sp = (string)cfg["provider"], sbu = (string)cfg["baseUrl"];
                        string smslug = PresetSlugFor(sp, sbu);
                        Send(new { type = "provider", slug = smslug, provider = sp, baseUrl = sbu, displayName = ProviderFactory.DisplayNameFor(sp, sbu), model = (string)cfg["model"], dynamic = IsDynamicProvider(sp), hasKey = !string.IsNullOrWhiteSpace(StoredProviderKeyForSlug(smslug, sp)), customModels = GetCustomModels(smslug), customProviders = GetCustomProviders() });
                        Send(new { type = "answer", answer = "Model switched to " + model.Trim() + " — it applies to the next run." });
                    }
                    catch (Exception ex) { Send(new { type = "error", message = "Couldn't switch model (" + BoundDetail(ex.Message, 200) + ") — check the selection/key and try again." }); }
                }

                // Custom model registration: per-slug ids the user registers by hand
                // (config.json customModels.<slug> = [{id, label?}]). The slug is always
                // derived host-side from the current provider/baseUrl — the panel never
                // sends it, so a stale dropdown can't file a model under the wrong key.
                // Custom ids are never probed via /models; they merge, never delete.
                else if (action == "customModelAdd")
                {
                    string cid = null, clabel = null;
                    try { cid = (string)msg.id; } catch { }
                    try { clabel = (string)msg.label; } catch { }
                    if (string.IsNullOrWhiteSpace(cid))
                    { Send(new { type = "error", message = "No model id entered — type the exact model id, then Add." }); return; }
                    try
                    {
                        var cfg = ReadForgeConfig();
                        string cslug = PresetSlugFor((string)cfg["provider"], (string)cfg["baseUrl"]);
                        var arr = AddCustomModel(cslug, cid, clabel);
                        cfg = ReadForgeConfig();
                        string sp = (string)cfg["provider"], sbu = (string)cfg["baseUrl"];
                        string sslug = PresetSlugFor(sp, sbu);
                        Send(new { type = "provider", slug = sslug, provider = sp, baseUrl = sbu, displayName = ProviderFactory.DisplayNameFor(sp, sbu), model = (string)cfg["model"], dynamic = IsDynamicProvider(sp), hasKey = !string.IsNullOrWhiteSpace(StoredProviderKeyForSlug(sslug, sp)), customModels = arr, customProviders = GetCustomProviders() });
                        Send(new { type = "answer", answer = "Custom model '" + cid.Trim() + "' added for " + (cslug ?? sslug) + " — pick it from the pill or Settings." });
                    }
                    catch (Exception ex) { Send(new { type = "error", message = "Couldn't add the custom model (" + BoundDetail(ex.Message, 200) + ") — check the id and try again." }); }
                }
                else if (action == "customModelRemove")
                {
                    string cid = null;
                    try { cid = (string)msg.id; } catch { }
                    if (string.IsNullOrWhiteSpace(cid))
                    { Send(new { type = "error", message = "No model id given — nothing to remove." }); return; }
                    try
                    {
                        var cfg = ReadForgeConfig();
                        string cslug = PresetSlugFor((string)cfg["provider"], (string)cfg["baseUrl"]);
                        var arr = RemoveCustomModel(cslug, cid);
                        cfg = ReadForgeConfig();
                        string sp = (string)cfg["provider"], sbu = (string)cfg["baseUrl"];
                        string sslug = PresetSlugFor(sp, sbu);
                        Send(new { type = "provider", slug = sslug, provider = sp, baseUrl = sbu, displayName = ProviderFactory.DisplayNameFor(sp, sbu), model = (string)cfg["model"], dynamic = IsDynamicProvider(sp), hasKey = !string.IsNullOrWhiteSpace(StoredProviderKeyForSlug(sslug, sp)), customModels = arr, customProviders = GetCustomProviders() });
                        Send(new { type = "answer", answer = "Custom model '" + cid.Trim() + "' removed — the live list is unchanged." });
                    }
                    catch (Exception ex) { Send(new { type = "error", message = "Couldn't remove the custom model (" + BoundDetail(ex.Message, 200) + ") — try again." }); }
                }

                // User-defined custom providers (Settings → Custom providers): add/remove a
                // {slug,label,kind,baseUrl} entry. Keys stay per-slug in KeyStore, models in
                // customModels.<slug> — both work untouched once the slug is registered.
                else if (action == "customProviderAdd")
                {
                    string pslug = null, plabel = null, pkind = null, purl = null;
                    try { pslug = (string)msg.slug; } catch { }
                    try { plabel = (string)msg.label; } catch { }
                    try { pkind = (string)msg.kind; } catch { }
                    try { purl = (string)msg.baseUrl; } catch { }
                    try
                    {
                        var missing = new List<string>();
                        string xurl = ExpandEnvTokens(purl, missing);
                        Newtonsoft.Json.Linq.JObject xheaders = ParseProviderHeaders(msg);
                        var list = CustomProviderAdd(pslug, plabel, pkind, xurl, false, xheaders);
                        var cfg = ReadForgeConfig();
                        string sp = (string)cfg["provider"], sbu = (string)cfg["baseUrl"];
                        string sslug = PresetSlugFor(sp, sbu);
                        Send(new { type = "provider", slug = sslug, provider = sp, baseUrl = sbu, displayName = ProviderFactory.DisplayNameFor(sp, sbu), model = (string)cfg["model"], dynamic = IsDynamicProvider(sp), hasKey = !string.IsNullOrWhiteSpace(StoredProviderKeyForSlug(sslug, sp)), customModels = GetCustomModels(sslug), customProviders = list });
                        Send(new { type = "answer", answer = "Custom provider '" + SanitizeCustomSlug(pslug) + "' added — pick it in the provider dropdown, save its key, then add its model ids under Custom models." });
                        if (missing.Count > 0) Send(new { type = "answer", answer = "Warning: unresolved environment variable(s): " + string.Join(", ", missing.ToArray()) + " — left as literal text; set them and re-save to expand." });
                    }
                    catch (Exception ex) { Send(new { type = "error", message = "Couldn't add the custom provider (" + BoundDetail(ex.Message, 200) + ")." }); }
                }
                // Edit an existing custom provider in place. Upsert is keyed by the unchanged
                // slug, so its KeyStore key file and customModels.<slug> entries ride along;
                // CustomProviderAdd(..., update: true) re-runs the exact same validation/collision
                // guards as Add (reserved slugs, baseUrl vs customs + built-ins incl. opencode/opencode-go,
                // label impersonation, kind allowlist, http(s)).
                else if (action == "customProviderUpdate")
                {
                    string pslug = null, plabel = null, pkind = null, purl = null;
                    try { pslug = (string)msg.slug; } catch { }
                    try { plabel = (string)msg.label; } catch { }
                    try { pkind = (string)msg.kind; } catch { }
                    try { purl = (string)msg.baseUrl; } catch { }
                    try
                    {
                        var missing = new List<string>();
                        string xurl = ExpandEnvTokens(purl, missing);
                        Newtonsoft.Json.Linq.JObject xheaders = ParseProviderHeaders(msg);
                        var list = CustomProviderAdd(pslug, plabel, pkind, xurl, true, xheaders);
                        var cfg = ReadForgeConfig();
                        string sp = (string)cfg["provider"], sbu = (string)cfg["baseUrl"];
                        string sslug = PresetSlugFor(sp, sbu);
                        Send(new { type = "provider", slug = sslug, provider = sp, baseUrl = sbu, displayName = ProviderFactory.DisplayNameFor(sp, sbu), model = (string)cfg["model"], dynamic = IsDynamicProvider(sp), hasKey = !string.IsNullOrWhiteSpace(StoredProviderKeyForSlug(sslug, sp)), customModels = GetCustomModels(sslug), customProviders = list });
                        Send(new { type = "answer", answer = "Custom provider '" + SanitizeCustomSlug(pslug) + "' updated — its saved key and model ids were kept." });
                        if (missing.Count > 0) Send(new { type = "answer", answer = "Warning: unresolved environment variable(s): " + string.Join(", ", missing.ToArray()) + " — left as literal text; set them and re-save to expand." });
                    }
                    catch (Exception ex) { Send(new { type = "error", message = "Couldn't update the custom provider (" + BoundDetail(ex.Message, 200) + ")." }); }
                }
                else if (action == "customProviderRemove")
                {
                    string pslug = null;
                    try { pslug = (string)msg.slug; } catch { }
                    try
                    {
                        string gone = SanitizeCustomSlug(pslug);
                        bool wasActive = false;
                        try
                        {
                            var cfgBefore = ReadForgeConfig();
                            wasActive = string.Equals(PresetSlugFor((string)cfgBefore["provider"], (string)cfgBefore["baseUrl"]), gone, StringComparison.OrdinalIgnoreCase);
                        }
                        catch { }
                        var list = CustomProviderRemove(pslug);
                        var cfg = ReadForgeConfig();
                        string sp = (string)cfg["provider"], sbu = (string)cfg["baseUrl"];
                        string sslug = PresetSlugFor(sp, sbu);
                        Send(new { type = "provider", slug = sslug, provider = sp, baseUrl = sbu, displayName = ProviderFactory.DisplayNameFor(sp, sbu), model = (string)cfg["model"], dynamic = IsDynamicProvider(sp), hasKey = !string.IsNullOrWhiteSpace(StoredProviderKeyForSlug(sslug, sp)), customModels = GetCustomModels(sslug), customProviders = list });
                        Send(new { type = "answer", answer = "Custom provider '" + gone + "' removed — " + (wasActive ? "it was active, so the provider fell back to deepseek (its key and model ids are kept on disk)." : "its key and model ids are kept on disk in case you re-add it.") });
                    }
                    catch (Exception ex) { Send(new { type = "error", message = "Couldn't remove the custom provider (" + BoundDetail(ex.Message, 200) + ")." }); }
                }

                // Settings usage-data toggle (trap.md item 2: off switch easy to find). Host = source of truth:
                // persists the choice and pushes it into Telemetry.Enabled so Telemetry.Log honors it.
                else if (action == "telemetry")
                {
                    bool on = false;
                    try { on = (bool)msg.on; } catch { }
                    Telemetry.Enabled = on;
                }

                // Settings notifications toggle (OS toasts: approval + done + teammate). Host = source of
                // truth: persists the choice in %APPDATA%\Forge\config.json ("notify", default ON when absent).
                else if (action == "notifyGet")
                {
                    bool on = true;
                    try { on = NotifyEnabled(); } catch { on = true; }
                    Send(new { type = "notifyConfig", on });
                }
                else if (action == "notifySet")
                {
                    bool on = true;
                    try { on = (bool)msg.on; } catch { }
                    try { SetNotify(on); } catch { }
                    Send(new { type = "notifyConfig", on });
                }
            }
            catch (Exception ex)
            {
                // Telemetry gets the exception TYPE only — never ex.Message (it can contain a file path).
                Telemetry.Log("crash", success: false, errorCode: ex.GetType().Name, swVersion: SwVer());
                Send(new { type = "error", message = "Couldn't finish that run (" + BoundDetail(ex.Message, 300) + ") — check the model for partial changes, then retry or say what you expected." });
            }
        }

        /// <summary>Push the full MCP server list to the panel (legacy fields kept for back-compat).</summary>
        private static Newtonsoft.Json.Linq.JArray AttachMcpOAuthStatus(Newtonsoft.Json.Linq.JArray servers)
        {
            try
            {
                if (servers != null) foreach (var t in servers)
                {
                    try
                    {
                        var o = t as Newtonsoft.Json.Linq.JObject;
                        if (o == null) continue;
                        string nm = (string)o["name"];
                        if (string.IsNullOrWhiteSpace(nm)) nm = "default";
                        string st = "none";
                        try { st = McpBridge.OAuthStatus(nm) ?? "none"; } catch { st = "none"; }
                        bool need = false;
                        try { need = McpBridge.IsOAuthRequired(nm); } catch { need = false; }
                        o["authStatus"] = st;
                        o["needsAuth"] = need;
                    }
                    catch { }
                }
            }
            catch { }
            return servers;
        }

        private void SendMcpConfig()
        {
            string cmd, args; bool enabled; int tmo;
            McpBridge.DescribeFull(out cmd, out args, out enabled, out tmo);
            bool running = false;
            try { running = McpBridge.IsRunning; } catch { }
            Newtonsoft.Json.Linq.JArray servers = new Newtonsoft.Json.Linq.JArray();
            try { servers = McpBridge.DescribeAll(); } catch { }
            try { AttachMcpOAuthStatus(servers); } catch { }
            Send(new { type = "mcpConfig", enabled = enabled, timeoutMs = tmo, command = cmd, args = args, running = running, servers = servers });
        }

        /// <summary>Send the per-tool policy snapshot: cached seen tools first, then a bounded live refresh. Never blocks the message thread on uvx download / RPC and never throws.</summary>
        private async System.Threading.Tasks.Task SendToolPolicyAsync()
        {
            try
            {
                var names = new System.Collections.Generic.List<string>();
                var seen = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
                try
                {
                    System.Collections.Generic.List<string> seenTools = null;
                    try { lock (McpBuild.SeenTools) { seenTools = new System.Collections.Generic.List<string>(McpBuild.SeenTools); } } catch { }
                    if (seenTools != null) foreach (var s in seenTools) { try { if (!string.IsNullOrWhiteSpace(s) && seen.Add(s.Trim())) names.Add(s.Trim()); } catch { } }
                }
                catch { }
                Newtonsoft.Json.Linq.JObject pol = new Newtonsoft.Json.Linq.JObject();
                try
                {
                    var dict = McpBuild.GetToolPolicies();
                    if (dict != null) foreach (var kv in dict) { try { pol[kv.Key] = kv.Value; } catch { } }
                }
                catch { }
                // Serve the cache first: the panel renders instantly even when the MCP server is cold.
                try
                {
                    var cached = new System.Collections.Generic.List<string>(names);
                    try { cached.Sort(System.StringComparer.OrdinalIgnoreCase); } catch { }
                    Send(new { type = "toolPolicy", tools = new Newtonsoft.Json.Linq.JArray(cached.ToArray()), policy = pol });
                }
                catch { }
                // Bounded live refresh (fire-and-await with timeout, never an unbounded block):
                // uvx download / 60s RPC must not stall the OnWebMessage thread.
                try
                {
                    Newtonsoft.Json.Linq.JArray live = null;
                    try
                    {
                        using (var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5)))
                        { try { live = await McpBridge.ListAllToolsAsync(cts.Token).ConfigureAwait(false); } catch { live = null; } }
                    }
                    catch { live = null; }
                    if (live == null) return;
                    bool grew = false;
                    foreach (var t in live)
                    {
                        try
                        {
                            var o = t as Newtonsoft.Json.Linq.JObject;
                            string n = o != null ? (string)o["name"] : (string)t;
                            if (string.IsNullOrWhiteSpace(n)) continue;
                            n = n.Trim();
                            if (seen.Add(n)) { names.Add(n); grew = true; }
                        }
                        catch { }
                    }
                    if (!grew) return;
                    try { names.Sort(System.StringComparer.OrdinalIgnoreCase); } catch { }
                    Send(new { type = "toolPolicy", tools = new Newtonsoft.Json.Linq.JArray(names.ToArray()), policy = pol });
                }
                catch { }
            }
            catch { }
        }

        /// <summary>Read msg.args as string[]: accepts a JArray or a quote-aware space-joined string.</summary>
        private static string[] ParseMcpArgs(dynamic msg)
        {
            try
            {
                var tok = msg.args as Newtonsoft.Json.Linq.JToken;
                if (tok is Newtonsoft.Json.Linq.JArray)
                {
                    var list = new System.Collections.Generic.List<string>();
                    foreach (var t in (Newtonsoft.Json.Linq.JArray)tok)
                    {
                        string s = null;
                        try { s = (string)t; } catch { }
                        if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
                    }
                    return list.ToArray();
                }
                string sarg = null;
                try { sarg = (string)msg.args; } catch { }
                if (string.IsNullOrWhiteSpace(sarg)) return null;
                var parts = new System.Collections.Generic.List<string>();
                var cur = new System.Text.StringBuilder();
                bool inQ = false;
                foreach (char c in sarg)
                {
                    if (c == '"') { inQ = !inQ; continue; }
                    if (!inQ && char.IsWhiteSpace(c)) { if (cur.Length > 0) { parts.Add(cur.ToString()); cur.Length = 0; } continue; }
                    cur.Append(c);
                }
                if (cur.Length > 0) parts.Add(cur.ToString());
                return parts.ToArray();
            }
            catch { return null; }
        }

        private static bool McpHas(dynamic msg, string key)
        {
            try { var o = msg as Newtonsoft.Json.Linq.JObject; return o != null && o[key] != null; }
            catch { return false; }
        }

        private static string McpStr(dynamic msg, string key)
        {
            try { var o = msg as Newtonsoft.Json.Linq.JObject; if (o == null) return null; return (string)o[key]; }
            catch { return null; }
        }

        private static Newtonsoft.Json.Linq.JToken McpTok(dynamic msg, string key)
        {
            try { var o = msg as Newtonsoft.Json.Linq.JObject; if (o == null) return null; return o[key]; }
            catch { return null; }
        }

        private static bool McpTryStringMap(Newtonsoft.Json.Linq.JToken tok, out System.Collections.Generic.Dictionary<string, string> map)
        {
            map = null;
            var o = tok as Newtonsoft.Json.Linq.JObject;
            if (o == null) return false;
            var d = new System.Collections.Generic.Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var p in o.Properties())
            {
                if (p.Value == null || p.Value.Type != Newtonsoft.Json.Linq.JTokenType.String) return false;
                d[p.Name] = (string)p.Value;
            }
            map = d;
            return true;
        }

        /// <summary>Fetch the existing server, patch only fields the panel sent, then save through McpBridge (unknown fields survive). Returns an error message, or null on success.</summary>
        private static string McpWriteServer(dynamic msg, string rawName, bool createIfMissing, out string canonical)
        {
            canonical = null;
            try
            {
                if (string.IsNullOrWhiteSpace(rawName))
                    return createIfMissing ? "Couldn't add the MCP server — give it a name first." : "Couldn't update the MCP server — no name was sent.";
                string name = rawName.Trim();

                string scopeKey = null;
                try { scopeKey = McpBridge.ProjectKey; } catch { }
                bool scoped = !string.IsNullOrWhiteSpace(scopeKey);
                var list = scoped ? McpBridge.LoadServersForProject(scopeKey) : McpBridge.LoadServers();
                if (list == null) list = new System.Collections.Generic.List<McpBridge.McpServerConfig>();
                McpBridge.McpServerConfig entry = null;
                foreach (var srv in list)
                    if (srv != null && string.Equals(srv.Name, name, System.StringComparison.OrdinalIgnoreCase)) { entry = srv; break; }
                if (entry == null)
                {
                    if (!createIfMissing) return "Unknown MCP server '" + name + "' — add it first.";
                    entry = new McpBridge.McpServerConfig { Name = name };
                    list.Add(entry);
                }
                canonical = entry.Name;

                string s;
                if (McpHas(msg, "type"))
                {
                    s = McpStr(msg, "type");
                    if (string.IsNullOrWhiteSpace(s)) return "MCP server 'type' must be 'stdio', 'http', or 'sse'.";
                    s = s.Trim().ToLowerInvariant();
                    if (s != "stdio" && s != "http" && s != "sse") return "MCP server 'type' must be 'stdio', 'http', or 'sse'.";
                    entry.Type = s;
                }
                else if (createIfMissing) entry.Type = "stdio";

                if (McpHas(msg, "url"))
                {
                    s = McpStr(msg, "url");
                    if (createIfMissing) entry.Url = s ?? "";
                    else if (!string.IsNullOrWhiteSpace(s)) entry.Url = s.Trim();
                }
                if (McpHas(msg, "cwd"))
                {
                    s = McpStr(msg, "cwd");
                    if (createIfMissing) entry.Cwd = s ?? "";
                    else if (!string.IsNullOrWhiteSpace(s)) entry.Cwd = s.Trim();
                }

                System.Collections.Generic.Dictionary<string, string> map;
                Newtonsoft.Json.Linq.JToken tok;
                if (McpHas(msg, "headers"))
                {
                    tok = McpTok(msg, "headers");
                    if (tok != null && tok.Type != Newtonsoft.Json.Linq.JTokenType.Null)
                    {
                        if (!McpTryStringMap(tok, out map)) return "MCP 'headers' must be an object of string values.";
                        entry.Headers = map;
                    }
                }
                if (McpHas(msg, "env"))
                {
                    tok = McpTok(msg, "env");
                    if (tok != null && tok.Type != Newtonsoft.Json.Linq.JTokenType.Null)
                    {
                        if (!McpTryStringMap(tok, out map)) return "MCP 'env' must be an object of string values.";
                        entry.Env = map;
                    }
                }

                if (McpHas(msg, "command"))
                {
                    s = McpStr(msg, "command");
                    if (!string.IsNullOrWhiteSpace(s)) entry.Command = s.Trim();
                }
                string[] args = ParseMcpArgs(msg);
                if (args != null && args.Length > 0) entry.Args = args;

                if (McpHas(msg, "enabled"))
                {
                    tok = McpTok(msg, "enabled");
                    if (tok == null || tok.Type != Newtonsoft.Json.Linq.JTokenType.Boolean) return "MCP 'enabled' must be true or false.";
                    entry.Enabled = (bool)tok;
                }
                if (McpHas(msg, "timeoutMs"))
                {
                    tok = McpTok(msg, "timeoutMs");
                    int v;
                    if (tok == null || tok.Type != Newtonsoft.Json.Linq.JTokenType.Integer || !int.TryParse(tok.ToString(), out v) || v < McpMinTimeoutMs)
                        return "MCP 'timeoutMs' must be a number in milliseconds (≥1000).";
                    entry.TimeoutMs = v;
                }
                if (McpHas(msg, "startTimeoutMs"))
                {
                    tok = McpTok(msg, "startTimeoutMs");
                    int v;
                    if (tok == null || tok.Type != Newtonsoft.Json.Linq.JTokenType.Integer || !int.TryParse(tok.ToString(), out v) || v < McpMinTimeoutMs)
                        return "MCP 'startTimeoutMs' must be a number in milliseconds (≥1000).";
                    entry.StartTimeoutMs = v;
                }

                string ty = entry.Type;
                if (string.IsNullOrWhiteSpace(ty)) ty = "stdio";
                bool tyRemote = string.Equals(ty, "http", System.StringComparison.OrdinalIgnoreCase)
                    || string.Equals(ty, "sse", System.StringComparison.OrdinalIgnoreCase);
                if (tyRemote)
                {
                    if (string.IsNullOrWhiteSpace(entry.Url)) return "HTTP MCP servers need a url — set \"url\" in the server entry and try again.";
                }
                else if (string.IsNullOrWhiteSpace(entry.Command))
                    return "stdio MCP servers need a command — set \"command\" in the server entry and try again.";

                if (McpHas(msg, "watchPaths")) entry.WatchPaths = McpStringArray(McpTok(msg, "watchPaths"));
                if (McpHas(msg, "alwaysAllow")) entry.AlwaysAllow = McpStringArray(McpTok(msg, "alwaysAllow"));
                if (McpHas(msg, "disabledTools")) entry.DisabledTools = McpStringArray(McpTok(msg, "disabledTools"));
                if (McpHas(msg, "oauth"))
                {
                    tok = McpTok(msg, "oauth");
                    if (tok == null || tok.Type != Newtonsoft.Json.Linq.JTokenType.Object)
                    {
                        if (tok != null && tok.Type != Newtonsoft.Json.Linq.JTokenType.Null)
                            return "MCP 'oauth' must be an object with clientId, authorizationUrl, and tokenUrl.";
                        entry.OAuth = null;
                    }
                    else
                    {
                        var oo = (Newtonsoft.Json.Linq.JObject)tok;
                        bool hasOAuthUpdate = false;
                        try
                        {
                            Newtonsoft.Json.Linq.JToken ht;
                            ht = oo["clientId"];
                            if (ht != null && ht.Type == Newtonsoft.Json.Linq.JTokenType.String && !string.IsNullOrWhiteSpace((string)ht)) hasOAuthUpdate = true;
                            ht = oo["authorizationUrl"];
                            if (ht != null && ht.Type == Newtonsoft.Json.Linq.JTokenType.String && !string.IsNullOrWhiteSpace((string)ht)) hasOAuthUpdate = true;
                            ht = oo["tokenUrl"];
                            if (ht != null && ht.Type == Newtonsoft.Json.Linq.JTokenType.String && !string.IsNullOrWhiteSpace((string)ht)) hasOAuthUpdate = true;
                            ht = oo["scope"];
                            if (ht != null && ht.Type != Newtonsoft.Json.Linq.JTokenType.Null)
                            {
                                if (ht.Type == Newtonsoft.Json.Linq.JTokenType.Array)
                                {
                                    foreach (var e in (Newtonsoft.Json.Linq.JArray)ht)
                                    {
                                        string sv = null;
                                        try { sv = (string)e; } catch { sv = null; }
                                        if (!string.IsNullOrWhiteSpace(sv)) { hasOAuthUpdate = true; break; }
                                    }
                                }
                                else hasOAuthUpdate = true;
                            }
                            ht = oo["redirectPort"];
                            if (ht != null && ht.Type != Newtonsoft.Json.Linq.JTokenType.Null) hasOAuthUpdate = true;
                        }
                        catch { }
                        if (createIfMissing || hasOAuthUpdate)
                        {
                            var oc = entry.OAuth ?? new McpBridge.McpServerConfig.McpOAuthConfig();
                            Newtonsoft.Json.Linq.JToken of;
                            of = oo["clientId"];
                            if (of != null && of.Type == Newtonsoft.Json.Linq.JTokenType.String) oc.ClientId = ((string)of ?? "").Trim();
                            of = oo["authorizationUrl"];
                            if (of != null && of.Type == Newtonsoft.Json.Linq.JTokenType.String) oc.AuthorizationUrl = ((string)of ?? "").Trim();
                            of = oo["tokenUrl"];
                            if (of != null && of.Type == Newtonsoft.Json.Linq.JTokenType.String) oc.TokenUrl = ((string)of ?? "").Trim();
                            of = oo["scope"];
                            if (of != null)
                            {
                                if (of.Type == Newtonsoft.Json.Linq.JTokenType.Array)
                                {
                                    var sc = new System.Collections.Generic.List<string>();
                                    foreach (var e in (Newtonsoft.Json.Linq.JArray)of)
                                    {
                                        string sv = null;
                                        try { sv = (string)e; } catch { sv = null; }
                                        if (!string.IsNullOrWhiteSpace(sv) && sc.Count < 200) sc.Add(sv.Trim());
                                    }
                                    oc.Scope = sc.ToArray();
                                }
                                else if (of.Type == Newtonsoft.Json.Linq.JTokenType.String)
                                {
                                    string raw = ((string)of ?? "").Trim();
                                    oc.Scope = raw.Length == 0 ? new string[0]
                                        : raw.Split(new char[] { ' ', '\t', '\r', '\n' }, System.StringSplitOptions.RemoveEmptyEntries);
                                }
                                else return "MCP 'oauth.scope' must be an array of strings.";
                            }
                            of = oo["redirectPort"];
                            if (of != null && of.Type != Newtonsoft.Json.Linq.JTokenType.Null)
                            {
                                int rp = 0;
                                try { rp = System.Convert.ToInt32(of.ToString()); }
                                catch { return "MCP 'oauth.redirectPort' must be a port number (1-65535)."; }
                                if (rp < 1 || rp > 65535) return "MCP 'oauth.redirectPort' must be a port number (1-65535).";
                                oc.RedirectPort = rp;
                            }
                            entry.OAuth = oc;
                        }
                    }
                }
                if (entry.OAuth != null && tyRemote)
                {
                    if (string.IsNullOrWhiteSpace(entry.OAuth.ClientId)
                        || string.IsNullOrWhiteSpace(entry.OAuth.AuthorizationUrl)
                        || string.IsNullOrWhiteSpace(entry.OAuth.TokenUrl))
                        return "MCP servers of type 'http'/'sse' with OAuth need clientId, authorizationUrl, and tokenUrl — set all three in the oauth block, then try again.";
                }
                if (scoped) McpBridge.SaveServersForProject(scopeKey, list);
                else McpBridge.SaveServers(list);
                return null;
            }
            catch (Exception ex) { return "Couldn't save the MCP server (" + BoundDetail(ex.Message, 200) + ") — check Settings → MCP server, then try again."; }
        }

        /// <summary>Resolve msg.name to a canonical server name (trim, case-insensitive). Null/empty → servers[0] iff exactly one; else error listing names. Never throws.</summary>
        private static bool TryResolveServerName(string raw, out string name, out string error)
        {
            name = null; error = null;
            try
            {
                var servers = McpBridge.LoadEffectiveServers();
                var names = new System.Collections.Generic.List<string>();
                if (servers != null) foreach (var s in servers) if (s != null && !string.IsNullOrWhiteSpace(s.Name)) names.Add(s.Name.Trim());
                string t = (raw ?? "").Trim();
                if (t.Length == 0)
                {
                    if (names.Count == 1) { name = names[0]; return true; }
                    error = names.Count == 0 ? "No MCP servers are configured — add one under Settings → MCP server first."
                        : "Specify a server: " + string.Join(", ", names.ToArray()) + " — e.g. /mcps start " + names[0] + ".";
                    return false;
                }
                foreach (var n in names) if (string.Equals(n, t, System.StringComparison.OrdinalIgnoreCase)) { name = n; return true; }
                error = "Unknown MCP server '" + t + "'. Known: " + (names.Count > 0 ? string.Join(", ", names.ToArray()) : "(none)") + ".";
                return false;
            }
            catch { error = "Couldn't resolve the MCP server name — try again."; return false; }
        }

        private static int CadScore(string path)
        {
            string e = "";
            try { e = System.IO.Path.GetExtension(path).ToLowerInvariant(); } catch { }
            if (e == ".sldprt" || e == ".sldasm") return 3;
            if (e == ".slddrw" || e == ".step" || e == ".stp" || e == ".iges" || e == ".igs" || e == ".stl" || e == ".dxf" || e == ".dwg") return 2;
            return 1;
        }

        // Resolve @-mentions typed (or restored) in the prompt: exact posted paths first, then
        // @tokens matched against open docs + the active-doc folder. Feeds Compare-class handlers.
        private System.Collections.Generic.List<string> ResolveMentions(string intent, Newtonsoft.Json.Linq.JArray posted)
        {
            var found = new System.Collections.Generic.List<string>();
            var seen = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            System.Action<string> add = p =>
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(p) || !System.IO.File.Exists(p)) return;
                    string full = System.IO.Path.GetFullPath(p);
                    if (seen.Add(full)) found.Add(full);
                }
                catch { }
            };
            try
            {
                // A hostile panel message can post a 100k-entry array: cap what we stat.
                int postedSeen = 0;
                if (posted != null) foreach (var t in posted)
                {
                    if (++postedSeen > 100) break;
                    string s = t != null ? t.ToString() : null;
                    if (!string.IsNullOrWhiteSpace(s)) add(s);
                }
            }
            catch { }
            try
            {
                var m = System.Text.RegularExpressions.Regex.Matches(intent ?? "", @"@([^\s@]+)");
                if (m.Count > 0)
                {
                    var open = new System.Collections.Generic.List<string>();
                    try
                    {
                        var app = SwAddin.SwApp;
                        object docs = app != null ? app.GetDocuments() : null;
                        var arr = docs as object[];
                        if (arr != null) foreach (var d in arr)
                        {
                            var md = d as IModelDoc2;
                            string p = null;
                            try { p = md != null ? md.GetPathName() : null; } catch { }
                            if (!string.IsNullOrWhiteSpace(p)) open.Add(p);
                        }
                    }
                    catch { }
                    string dir = null;
                    try
                    {
                        var app2 = SwAddin.SwApp;
                        var doc = app2 != null ? app2.ActiveDoc as IModelDoc2 : null;
                        string pn = doc != null ? doc.GetPathName() : null;
                        if (!string.IsNullOrWhiteSpace(pn)) dir = System.IO.Path.GetDirectoryName(pn);
                    }
                    catch { }
                    foreach (System.Text.RegularExpressions.Match t in m)
                    {
                        string tok = t.Groups[1].Value.TrimEnd(',', '.', ';', ')', ']', '"', '\'');
                        if (string.IsNullOrWhiteSpace(tok)) continue;
                        if (System.IO.File.Exists(tok)) { add(tok); continue; }
                        string hit = open.Find(p => string.Equals(System.IO.Path.GetFileName(p), tok, System.StringComparison.OrdinalIgnoreCase));
                        if (hit != null) { add(hit); continue; }
                        if (!string.IsNullOrWhiteSpace(dir))
                        {
                            string cand = System.IO.Path.Combine(dir, tok);
                            if (System.IO.File.Exists(cand)) add(cand);
                        }
                    }
                }
            }
            catch { }
            return found;
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
                        SetAttachedFile(dlg.FileName);
                    }
                }
            }
            catch (Exception ex)
            {
                Send(new { type = "error", message = "Couldn't attach that file (" + BoundDetail(ex.Message, 200) + ") — try again." });
            }
        }

        private void SetAttachedFile(string path)
        {
            _attachedFile = path;
            Send(new { type = "attached", name = Path.GetFileName(path) });
        }

        private static string SafePasteName(string name)
        {
            string s = (name ?? "").Trim();
            try { s = Path.GetFileName(s.Replace('/', '\\')); } catch { }
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c.ToString(), "_");
            s = s.Trim();
            if (s.Length == 0 || s == "." || s == "..") s = "paste.bin";
            if (s.Length > 80)
            {
                string ext = Path.GetExtension(s);
                string stem = s.Substring(0, s.Length - ext.Length);
                int keep = Math.Max(1, 80 - ext.Length);
                if (stem.Length > keep) stem = stem.Substring(stem.Length - keep);
                s = stem + ext;
            }
            return s;
        }

        private static string UniquePasteName(string safeName)
        {
            string ext = Path.GetExtension(safeName) ?? "";
            string stem = ext.Length > 0 ? safeName.Substring(0, safeName.Length - ext.Length) : safeName;
            return stem + "-" + Guid.NewGuid().ToString("N").Substring(0, 12) + ext;
        }

        private static void PruneOldPastes(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                var cutoff = DateTime.UtcNow.AddHours(-24);
                foreach (var f in Directory.GetFiles(dir))
                {
                    try { if (File.GetLastWriteTimeUtc(f) < cutoff) File.Delete(f); } catch { }
                }
            }
            catch { }
        }

        private void ApplyPasteAttach(JArray files)
        {
            if (files == null || files.Count == 0) { Send(new { type = "error", message = "Nothing to attach — no files were pasted." }); return; }
            string dir = Path.Combine(Path.GetTempPath(), "Forge-paste");
            PruneOldPastes(dir);
            int taken = 0;
            foreach (var item in files)
            {
                if (taken >= 3) { Send(new { type = "error", message = "Only the first 3 pasted files were attached." }); break; }
                var f = item as JObject; if (f == null) continue;
                string name = (string)f["name"];
                string path = (string)f["path"];
                string b64 = (string)f["bytesBase64"];
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    SetAttachedFile(path);
                    taken++;
                    continue;
                }
                if (string.IsNullOrWhiteSpace(b64)) continue;
                byte[] bytes;
                try { bytes = Convert.FromBase64String(b64); }
                catch { Send(new { type = "error", message = "Skipped '" + SafePasteName(name) + "' — invalid file data." }); continue; }
                if (bytes.Length > 10 * 1024 * 1024)
                { Send(new { type = "error", message = "Skipped '" + SafePasteName(name) + "' — 10 MB limit per pasted file." }); continue; }
                try
                {
                    Directory.CreateDirectory(dir);
                    string target = Path.Combine(dir, UniquePasteName(SafePasteName(name)));
                    File.WriteAllBytes(target, bytes);
                    SetAttachedFile(target);
                    taken++;
                }
                catch (Exception ex) { Send(new { type = "error", message = "Couldn't save pasted '" + SafePasteName(name) + "' (" + BoundDetail(ex.Message, 120) + ")." }); }
            }
        }

        private void DoInitContext(dynamic msg)
        {
            string sid = null;
            try { sid = (string)msg.sid; } catch { }
            if (string.IsNullOrWhiteSpace(sid)) sid = _lastRunSid;
            if (string.IsNullOrWhiteSpace(sid)) sid = CurrentChatSid();
            if (string.IsNullOrWhiteSpace(sid)) sid = "default";

            ISldWorks app = null;
            IModelDoc2 doc = null;
            try { app = SwAddin.SwApp; } catch { }
            try { if (app != null) doc = app.ActiveDoc as IModelDoc2; } catch { }
            if (app == null || doc == null)
            {
                Send(new { type = "initContext", sid, summary = "No active SolidWorks document is open — open a part or assembly, then retry to capture CAD context.", available = false });
                return;
            }

            string path = null, type = "unknown", ver = null;
            int open = 0;
            try { path = doc.GetPathName(); } catch { }
            try
            {
                int t = doc.GetType();
                if (t == (int)swDocumentTypes_e.swDocPART) type = "part";
                else if (t == (int)swDocumentTypes_e.swDocASSEMBLY) type = "assembly";
                else if (t == (int)swDocumentTypes_e.swDocDRAWING) type = "drawing";
            }
            catch { }
            try { var mi = app.GetType().GetMethod("Version", Type.EmptyTypes); if (mi != null) ver = mi.Invoke(app, null) as string; } catch { }
            if (string.IsNullOrWhiteSpace(ver)) ver = SwVer();
            if (string.IsNullOrWhiteSpace(ver)) ver = "SolidWorks";
            try { var docs = app.GetDocuments() as object[]; if (docs != null) open = docs.Length; } catch { }

            string summary = ver + " · " + type + " · " + open + (open == 1 ? " document" : " documents") + " open"
                + (string.IsNullOrWhiteSpace(path) ? "" : " · " + Path.GetFileName(path));
            try { McpBuild.SetCadContext(sid, summary, path, type, open, ver); } catch { }
            Send(new { type = "initContext", sid, summary, available = true, docPath = path, docType = type, openDocCount = open, swVersion = ver });
        }

        private IModelDoc2 ActivePart()
        {
            var model = (IModelDoc2)SwAddin.SwApp?.ActiveDoc;
            if (model == null) throw new Exception("Open a part first (.SLDPRT) — open it, then resend.");
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

        private static string SwVer() { try { return SwAddin.SwApp.RevisionNumber(); }             catch { return null; }
        }

        private static string ActiveSidForProvider()
        {
            try { if (!string.IsNullOrWhiteSpace(_activeRunSid)) return _activeRunSid; } catch { }
            try { if (!string.IsNullOrWhiteSpace(_lastRunSid)) return _lastRunSid; } catch { }
            return null;
        }

        private static void SetMcpBuildSessionId(string sid)
        {
            try
            {
                var f = typeof(McpBuild).GetField("SessionId", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (f == null || f.FieldType != typeof(string)) return;
                try { f.SetValue(null, sid); } catch { }
            }
            catch { }
        }

        private static string GetMcpBuildSessionId()
        {
            try
            {
                var f = typeof(McpBuild).GetField("SessionId", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (f == null || f.FieldType != typeof(string)) return null;
                try { return f.GetValue(null) as string; } catch { }
            }
            catch { }
            return null;
        }

        // ---- BYOK wiring (README "Get a free key" + ProviderFactory): writes %APPDATA%\Forge\config.json
        //      {provider, baseUrl, model} â€” the API key is NEVER in the file â€” and stores the key via
        //      KeyStore (DPAPI, current-user). Presets match the README's student instructions exactly so
        //      the onboarding key box configures a real, working provider. ----
        private static readonly string[] KnownSlugs = { "deepseek", "openrouter", "nvidia", "openai", "anthropic", "ollama", "opencode", "opencode-go" };

        private static void SaveProviderKey(string provider, string key, string model = null)
        {
            // Sanitize at entry (panel parity): reads file keys via PresetSlugFor
            // (sanitized), so writes must use the same slug or keys strand.
            string slug = SanitizeCustomSlug(provider);
            if (string.IsNullOrWhiteSpace(slug))
                throw new Exception("Unknown provider '" + provider + "'.");
            if (Array.IndexOf(KnownSlugs, slug) < 0 && FindCustomProvider(slug) == null)
                throw new Exception("Unknown provider '" + provider + "'.");
            var preset = ProviderPresetFor(slug);
            // Per-slug key file: every OpenAI-compatible preset used to share one
            // "openai-compatible.key", so saving an NVIDIA key silently wiped the
            // DeepSeek key (and vice versa). Lookups try the slug file first.
            KeyStore.Save(slug, key);
            string dir = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "Forge");
            Directory.CreateDirectory(dir);
            string cfgPath = Path.Combine(dir, "config.json");
            JObject cfg;
            try { cfg = JObject.Parse(File.ReadAllText(cfgPath)); } catch { cfg = new JObject(); }
            cfg["provider"] = preset.Provider;
            cfg["baseUrl"] = preset.BaseUrl;
            cfg["model"] = string.IsNullOrWhiteSpace(model) ? preset.Model : model.Trim();
            cfg["model_" + slug] = (string)cfg["model"];
            // Regression guard: customModels.<slug> lives alongside these keys and is
            // never cleared here — saving a key or switching providers keeps customs.
            // Atomic config write (temp + replace): a crash mid-save must never leave a
            // half-written config.json that ReadForgeConfig then reads as "no provider".
            string cfgTmp = cfgPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(cfgTmp, cfg.ToString());
                if (File.Exists(cfgPath)) File.Replace(cfgTmp, cfgPath, null);
                else File.Move(cfgTmp, cfgPath);
            }
            catch
            {
                try { File.Delete(cfgTmp); } catch { }
                throw;
            }
        }

        private class ProviderPreset
        {
            public string Provider, BaseUrl, Model;
            public ProviderPreset(string provider, string baseUrl, string model) { Provider = provider; BaseUrl = baseUrl; Model = model; }
        }

        // Models that are endpoints, not chat models (embeddings, guards, detectors, translators).
        private static readonly string[] NonChatModelHints =
            { "embed", "guard", "safety", "detect", "translate", "clip", "retriev", "rerank", "ocr" };

        // OpenCode Zen's keyless "public" list mixes chat-completions models with
        // messages/responses families this panel can't drive. Allow-list the chat families
        // (and their *-free variants) so claude-*/gpt-*/gemini-*/jev/qwen3.[5-8]-(max|plus)
        // never reach the picker. The NonChatModelHints filter above still applies to all.
        private static readonly string[] OpenCodeChatFamilies =
            { "glm", "kimi", "minimax", "deepseek", "qwen", "grok", "nemotron", "ling", "mimo", "big-pickle" };

        // OpenCode Go (paid subscription) serves a different, chat-completions-only catalog:
        // glm/kimi/deepseek/mimo/longcat/hy/space-bunny/minimax. Responses-only families
        // (grok-*, gpt-*-luna, muse-spark-*), Messages-only families (qwen3.*-max/plus, jev,
        // claude-*, gemini-*) and every other id are excluded. Hybrid names with a digit
        // suffix (hy3-preview, hy4, minimax-m3) still belong to their leading family.
        private static readonly string[] OpenCodeGoChatFamilies =
            { "glm", "kimi", "minimax", "deepseek", "mimo", "longcat", "hy", "space-bunny" };

        private static JObject ReadForgeConfig()
        {
            string p = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "Forge", "config.json");
            if (!File.Exists(p)) return new JObject();
            try { return JObject.Parse(File.ReadAllText(p)); } catch { return new JObject(); }
        }

        private static void WriteForgeConfig(JObject cfg)
        {
            string dir = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "Forge");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "config.json"), cfg.ToString());
        }

        // OS desktop toasts master switch (%APPDATA%\Forge\config.json "notify"). Missing or
        // corrupt config reads as ON; these never throw — a toast must never fail a run.
        private static bool NotifyEnabled()
        {
            try
            {
                JObject cfg = ReadForgeConfig();
                JToken v = cfg["notify"];
                if (v == null) return true;
                if (v.Type == JTokenType.Boolean) return (bool)v;
                string s = ((string)v ?? "").Trim().ToLowerInvariant();
                if (s == "1" || s == "true" || s == "on" || s == "yes") return true;
                if (s == "0" || s == "false" || s == "off" || s == "no") return false;
                return true;
            }
            catch { return true; }
        }

        private static void LoadAutoApprove()
        {
            try
            {
                JObject cfg = ReadForgeConfig();
                JObject a = cfg["autoApprove"] as JObject;
                if (a == null) return;
                McpBuild.ApproveMaster = AutoApproveBool(a, "master", McpBuild.ApproveMaster);
                McpBuild.ApproveRead = AutoApproveBool(a, "read", McpBuild.ApproveRead);
                McpBuild.ApproveBuild = AutoApproveBool(a, "build", McpBuild.ApproveBuild);
                McpBuild.ApproveDestructive = AutoApproveBool(a, "destructive", McpBuild.ApproveDestructive);
            }
            catch { }
        }

        private static bool AutoApproveBool(JObject cfg, string key, bool fallback)
        {
            try
            {
                JToken v = cfg == null ? null : cfg[key];
                if (v == null) return fallback;
                if (v.Type == JTokenType.Boolean) return (bool)v;
                if (v.Type == JTokenType.Integer || v.Type == JTokenType.Float) return Convert.ToDouble(v) != 0;
                string s = ((string)v ?? "").Trim().ToLowerInvariant();
                if (s == "1" || s == "true" || s == "on" || s == "yes") return true;
                if (s == "0" || s == "false" || s == "off" || s == "no") return false;
            }
            catch { }
            return fallback;
        }

        private static void SetNotify(bool on)
        {
            try
            {
                JObject cfg = ReadForgeConfig();
                cfg["notify"] = on;
                WriteForgeConfig(cfg);
            }
            catch { }
        }

        // ---- Custom model registration (per-slug map in config.json) ----
        // Shape: {"customModels":{"deepseek":[{"id":"deepseek-reasoner","label":"Reasoner (custom)"}],"openrouter":[]}}
        // Keys are onboarding slugs (KnownSlugs) plus the "openai-compatible" fallback
        // for custom endpoints. Values are [{id, label?}]. Never probed via /models:
        // live refresh merges, never deletes, custom entries. Tolerates a missing or
        // corrupt config/file (returns empty, never throws to SW). DPAPI untouched.
        private static string NormCustomSlug(string slug)
        {
            try { return (slug ?? "").Trim().ToLowerInvariant(); } catch { return ""; }
        }

        private static JArray GetCustomModels(string slug)
        {
            try
            {
                string s = NormCustomSlug(slug);
                if (string.IsNullOrWhiteSpace(s)) return new JArray();
                var cfg = ReadForgeConfig();
                var cm = cfg["customModels"] as JObject;
                if (cm == null) return new JArray();
                var arr = cm[s] as JArray;
                if (arr == null) return new JArray();
                var clean = new JArray();
                foreach (var e in arr)
                {
                    try
                    {
                        string id = e is JObject ? (string)((JObject)e)["id"] : (string)e;
                        if (string.IsNullOrWhiteSpace(id)) continue;
                        var o = new JObject { ["id"] = id.Trim() };
                        string lab = e is JObject ? (string)((JObject)e)["label"] : null;
                        if (!string.IsNullOrWhiteSpace(lab)) o["label"] = lab.Trim();
                        clean.Add(o);
                    }
                    catch { }
                }
                return clean;
            }
            catch { return new JArray(); }
        }

        private static void WriteForgeConfigAtomic(JObject cfg)
        {
            string dir = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "Forge");
            Directory.CreateDirectory(dir);
            string cfgPath = Path.Combine(dir, "config.json");
            string cfgTmp = cfgPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(cfgTmp, cfg.ToString());
                if (File.Exists(cfgPath)) File.Replace(cfgTmp, cfgPath, null);
                else File.Move(cfgTmp, cfgPath);
            }
            catch
            {
                try { File.Delete(cfgTmp); } catch { }
                throw;
            }
        }

        private static JArray AddCustomModel(string slug, string id, string label)
        {
            string s = NormCustomSlug(slug);
            string nid = (id ?? "").Trim();
            if (string.IsNullOrWhiteSpace(s)) throw new Exception("No provider configured yet.");
            if (string.IsNullOrWhiteSpace(nid)) throw new Exception("Empty model id.");
            string nlab = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
            JObject cfg;
            try { cfg = ReadForgeConfig(); } catch { cfg = new JObject(); }
            if (cfg == null) cfg = new JObject();
            var cm = cfg["customModels"] as JObject;
            if (cm == null) { cm = new JObject(); cfg["customModels"] = cm; }
            var arr = cm[s] as JArray;
            if (arr == null) { arr = new JArray(); cm[s] = arr; }
            bool found = false;
            foreach (var e in arr)
            {
                try
                {
                    var o = e as JObject;
                    string eid = o != null ? (string)o["id"] : (string)e;
                    if (string.Equals((eid ?? "").Trim(), nid, StringComparison.OrdinalIgnoreCase))
                    {
                        found = true;
                        if (o != null)
                        {
                            if (nlab != null) o["label"] = nlab;
                            else o.Remove("label");
                        }
                        break;
                    }
                }
                catch { }
            }
            if (!found)
            {
                if (arr.Count >= 50) throw new Exception("Custom model cap reached for " + s + " (50) — remove one first.");
                var o = new JObject { ["id"] = nid };
                if (nlab != null) o["label"] = nlab;
                arr.Add(o);
            }
            WriteForgeConfigAtomic(cfg);
            return GetCustomModels(s);
        }

        private static JArray RemoveCustomModel(string slug, string id)
        {
            string s = NormCustomSlug(slug);
            string nid = (id ?? "").Trim();
            if (string.IsNullOrWhiteSpace(s)) throw new Exception("No provider configured yet.");
            if (string.IsNullOrWhiteSpace(nid)) throw new Exception("Empty model id.");
            JObject cfg;
            try { cfg = ReadForgeConfig(); } catch { cfg = new JObject(); }
            if (cfg == null) cfg = new JObject();
            try
            {
                var cm = cfg["customModels"] as JObject;
                var arr = cm != null ? cm[s] as JArray : null;
                if (arr != null)
                {
                    JToken drop = null;
                    foreach (var e in arr)
                    {
                        try
                        {
                            var o = e as JObject;
                            string eid = o != null ? (string)o["id"] : (string)e;
                            if (string.Equals((eid ?? "").Trim(), nid, StringComparison.OrdinalIgnoreCase)) { drop = e; break; }
                        }
                        catch { }
                    }
                    if (drop != null) { arr.Remove(drop); WriteForgeConfigAtomic(cfg); }
                }
            }
            catch { }
            return GetCustomModels(s);
        }

        // ---- User-defined custom providers (config.json customProviders) ----
        // Shape: {"customProviders":[{"slug":"corp-gw","label":"Corp gateway","kind":"openai-compatible","baseUrl":"https://...","headers":{"X-Key":"{env:MY_TOKEN}"}}]}
        // headers are stored as RAW templates ({env:VAR}/${VAR}/%VAR%) and expanded at USE time against
        // the process environment into ProviderOptions.Headers / McpBuild.ProviderHeaders — never at save.
        // kind picks the wire client (OpenAiCompatibleClient incl. reasoning fallback, or
        // AnthropicClient — retries/temperature/small-model follow the kind automatically);
        // the slug keys KeyStore + customModels exactly like a built-in slug. Corrupt entries
        // are ignored, never thrown. Deleting the active provider falls back to deepseek
        // (the ProviderPresetFor default), matching existing fallback behavior. The reserved
        // set also blocks "openai-compatible" so a custom slug can never collide with the
        // client-id fallback in PresetSlugFor.
        private static readonly string[] ReservedSlugs = { "deepseek", "openrouter", "nvidia", "openai", "anthropic", "ollama", "opencode", "opencode-go", "openai-compatible" };
        private static readonly string[] CustomKinds = { "openai-compatible", "anthropic" };

        private static string SanitizeCustomSlug(string slug)
        {
            try
            {
                string s = (slug ?? "").Trim().ToLowerInvariant().Replace('_', '-').Replace(' ', '-');
                var sb = new System.Text.StringBuilder();
                foreach (char c in s)
                {
                    if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-') sb.Append(c);
                }
                string r = sb.ToString().Trim('-');
                if (r.Length == 0) return null;
                if (r.Length > 40) r = r.Substring(0, 40).Trim('-');
                if (r.Length == 0) return null;
                return r;
            }
            catch { return null; }
        }

        private static string NormCustomBaseUrl(string url)
        {
            try
            {
                string u = (url ?? "").Trim().TrimEnd('/');
                if (string.IsNullOrWhiteSpace(u)) return null;
                System.Uri uri;
                if (!System.Uri.TryCreate(u, System.UriKind.Absolute, out uri)) return null;
                if (!string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase)) return null;
                return u;
            }
            catch { return null; }
        }

        private static JArray ReadCustomProvidersRaw()
        {
            try
            {
                var cfg = ReadForgeConfig();
                var arr = cfg != null ? cfg["customProviders"] as JArray : null;
                return arr ?? new JArray();
            }
            catch { return new JArray(); }
        }

        private static JArray GetCustomProviders()
        {
            var clean = new JArray();
            try
            {
                foreach (var e in ReadCustomProvidersRaw())
                {
                    try
                    {
                        var o = e as JObject;
                        if (o == null) continue;
                        string s = SanitizeCustomSlug((string)o["slug"]);
                        string kind = ((string)o["kind"] ?? "").Trim().ToLowerInvariant();
                        string bu = NormCustomBaseUrl((string)o["baseUrl"]);
                        // Ignore bad entries (empty slug/url, unknown kind, reserved slug, dupes):
                        // a corrupt config must never break provider resolution.
                        if (string.IsNullOrWhiteSpace(s) || string.IsNullOrWhiteSpace(bu)) continue;
                        if (Array.IndexOf(CustomKinds, kind) < 0) continue;
                        if (Array.IndexOf(ReservedSlugs, s) >= 0) continue;
                        bool dupe = false;
                        foreach (var c in clean)
                        {
                            try { if (string.Equals((string)((JObject)c)["slug"], s, StringComparison.OrdinalIgnoreCase)) { dupe = true; break; } }
                            catch { }
                        }
                        if (dupe) continue;
                        string lab = ((string)o["label"] ?? "").Trim();
                        var n = new JObject { ["slug"] = s, ["kind"] = kind, ["baseUrl"] = bu };
                        if (!string.IsNullOrWhiteSpace(lab)) n["label"] = lab.Length > 60 ? lab.Substring(0, 60) : lab;
                        try
                        {
                            var hh = o["headers"] as JObject;
                            if (hh != null) n["headers"] = hh.DeepClone();
                        }
                        catch { }
                        clean.Add(n);
                    }
                    catch { }
                }
            }
            catch { }
            return clean;
        }

        private static JObject FindCustomProvider(string slug)
        {
            try
            {
                string s = SanitizeCustomSlug(slug);
                if (string.IsNullOrWhiteSpace(s)) return null;
                foreach (var e in GetCustomProviders())
                {
                    try
                    {
                        var o = e as JObject;
                        if (o != null && string.Equals((string)o["slug"], s, StringComparison.OrdinalIgnoreCase)) return o;
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        private static JObject FindCustomProviderByBaseUrl(string baseUrl)
        {
            try
            {
                string bu = NormCustomBaseUrl(baseUrl);
                if (string.IsNullOrWhiteSpace(bu)) return null;
                foreach (var e in GetCustomProviders())
                {
                    try
                    {
                        var o = e as JObject;
                        if (o != null && string.Equals((string)o["baseUrl"], bu, StringComparison.OrdinalIgnoreCase)) return o;
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        // Add (update:false) or edit-in-place (update:true) a {slug,label,kind,baseUrl} custom.
        // Both paths re-run the exact same validation/collision guards. Upsert is keyed by the
        // unchanged slug, so the provider's KeyStore key file and customModels.<slug> entries
        // are never touched.
        private static JArray CustomProviderAdd(string slug, string label, string kind, string baseUrl, bool update = false, JObject headers = null)
        {
            string s = SanitizeCustomSlug(slug);
            if (string.IsNullOrWhiteSpace(s)) throw new Exception("Give the provider a slug (lowercase letters, digits, hyphens) — e.g. corp-gw.");
            string k = ((kind ?? "").Trim().ToLowerInvariant());
            if (Array.IndexOf(CustomKinds, k) < 0) throw new Exception("Pick a provider type: openai-compatible or anthropic.");
            string bu = NormCustomBaseUrl(baseUrl);
            if (string.IsNullOrWhiteSpace(bu)) throw new Exception("Base URL must be an http(s) address — e.g. https://gw.corp.example/v1.");
            if (Array.IndexOf(ReservedSlugs, s) >= 0) throw new Exception("'" + s + "' is a built-in provider — pick a different slug.");
            // Base-URL collision guard: PresetSlugFor and ProviderFactory.SlugFor match
            // customs by baseUrl FIRST, so a second custom reusing an existing baseUrl
            // (custom or built-in) would silently alias the first slug and strand keys/models.
            // On edit the entry being changed is excluded (it may keep its own base URL).
            try
            {
                foreach (var e in GetCustomProviders())
                {
                    var o = e as JObject;
                    if (o == null) continue;
                    string es = SanitizeCustomSlug((string)o["slug"]);
                    if (update && !string.IsNullOrWhiteSpace(es) && string.Equals(es, s, StringComparison.OrdinalIgnoreCase)) continue;
                    string eb = null;
                    try { eb = NormCustomBaseUrl((string)o["baseUrl"]); }
                    catch { continue; }
                    if (string.IsNullOrWhiteSpace(eb)) continue;
                    if (string.Equals(eb, bu, StringComparison.OrdinalIgnoreCase)) throw new Exception("That base URL is already registered — pick the existing entry or use a different URL.");
                }
            }
            catch (Exception ex)
            {
                if (ex.Message != null && ex.Message.StartsWith("That base URL")) throw;
            }
            try
            {
                foreach (var b in new string[] { "deepseek", "openrouter", "nvidia", "openai", "anthropic", "ollama", "opencode", "opencode-go" })
                {
                    string pb = null;
                    try { var preset = ProviderPresetFor(b); if (preset != null) pb = NormCustomBaseUrl(preset.BaseUrl); }
                    catch { continue; }
                    if (string.IsNullOrWhiteSpace(pb)) continue;
                    if (string.Equals(pb, bu, StringComparison.OrdinalIgnoreCase)) throw new Exception("That base URL is already registered — pick the existing entry or use a different URL.");
                }
            }
            catch (Exception ex)
            {
                if (ex.Message != null && ex.Message.StartsWith("That base URL")) throw;
            }
            JObject cfg;
            try { cfg = ReadForgeConfig(); } catch { cfg = new JObject(); }
            if (cfg == null) cfg = new JObject();
            // Capture active state before the entry changes: resolution is baseUrl-first, so if
            // an edited custom was active its config baseUrl (and wire kind) must follow the edit
            // or hasKey/model lookups would fall back to the reserved "openai-compatible" slug.
            bool wasActive = string.Equals(PresetSlugFor((string)cfg["provider"], (string)cfg["baseUrl"]), s, StringComparison.OrdinalIgnoreCase);
            var raw = cfg["customProviders"] as JArray;
            if (raw == null) { raw = new JArray(); cfg["customProviders"] = raw; }
            JToken existing = null;
            foreach (var e in raw)
            {
                var o = e as JObject;
                string es = null;
                try { es = o == null ? null : SanitizeCustomSlug((string)o["slug"]); } catch { }
                if (!string.IsNullOrWhiteSpace(es) && string.Equals(es, s, StringComparison.OrdinalIgnoreCase)) { existing = e; break; }
            }
            if (update)
            {
                if (existing == null) throw new Exception("No custom provider '" + s + "' found — nothing to update.");
            }
            else
            {
                if (existing != null) throw new Exception("A custom provider '" + s + "' already exists — delete it first to replace it.");
                if (GetCustomProviders().Count >= 20) throw new Exception("Custom provider cap reached (20) — remove one first.");
            }
            string lab = (label ?? "").Trim();
            // Label impersonation guard: a custom label matching a built-in provider display name
            // (ProviderFactory.DisplayNameFor: DeepSeek/OpenRouter/NVIDIA/OpenAI/Anthropic/Ollama/OpenCode Zen/OpenCode Go)
            // would masquerade in the picker. Reject case-insensitively like the other validations.
            if (!string.IsNullOrWhiteSpace(lab))
            {
                foreach (var b in new string[] { "DeepSeek", "OpenRouter", "NVIDIA", "OpenAI", "Anthropic", "Ollama", "OpenCode Zen", "OpenCode Go" })
                    if (string.Equals(lab, b, StringComparison.OrdinalIgnoreCase)) throw new Exception("'" + lab + "' is a built-in provider name — pick a different label.");
            }
            if (headers == null && update && existing is JObject)
            {
                var oldHeaders = ((JObject)existing)["headers"] as JObject;
                if (oldHeaders != null) headers = (JObject)oldHeaders.DeepClone();
            }
            var n = new JObject { ["slug"] = s, ["kind"] = k, ["baseUrl"] = bu };
            if (!string.IsNullOrWhiteSpace(lab)) n["label"] = lab.Length > 60 ? lab.Substring(0, 60) : lab;
            if (headers != null && headers.Count > 0) n["headers"] = headers.DeepClone();
            if (update) existing.Replace(n);
            else raw.Add(n);
            if (update && wasActive) { cfg["provider"] = k; cfg["baseUrl"] = bu; }
            WriteForgeConfigAtomic(cfg);
            return GetCustomProviders();
        }

        private static JArray CustomProviderRemove(string slug)
        {
            string s = SanitizeCustomSlug(slug);
            if (string.IsNullOrWhiteSpace(s)) throw new Exception("No provider slug given — nothing to remove.");
            if (Array.IndexOf(ReservedSlugs, s) >= 0) throw new Exception("'" + s + "' is built-in and can't be removed.");
            JObject cfg;
            try { cfg = ReadForgeConfig(); } catch { cfg = new JObject(); }
            if (cfg == null) cfg = new JObject();
            bool dropped = false;
            try
            {
                var raw = cfg["customProviders"] as JArray;
                if (raw != null)
                {
                    JToken hit = null;
                    foreach (var e in raw)
                    {
                        try
                        {
                            var o = e as JObject;
                            if (o != null && string.Equals(SanitizeCustomSlug((string)o["slug"]), s, StringComparison.OrdinalIgnoreCase)) { hit = e; break; }
                        }
                        catch { }
                    }
                    if (hit != null) { raw.Remove(hit); dropped = true; }
                }
            }
            catch { }
            if (!dropped) throw new Exception("No custom provider '" + s + "' found — nothing to remove.");
            // The removed provider's key file and customModels entries stay on disk: never
            // silently lose secrets or model ids (re-adding the slug restores them).
            // If it was active, fall back to deepseek (the ProviderPresetFor default).
            try
            {
                if (string.Equals(PresetSlugFor((string)cfg["provider"], (string)cfg["baseUrl"]), s, StringComparison.OrdinalIgnoreCase))
                {
                    var fb = ProviderPresetFor("deepseek");
                    cfg["provider"] = fb.Provider;
                    cfg["baseUrl"] = fb.BaseUrl;
                    string remembered = null;
                    try { remembered = (string)cfg["model_deepseek"]; } catch { }
                    cfg["model"] = !string.IsNullOrWhiteSpace(remembered) ? remembered.Trim() : fb.Model;
                }
            }
            catch { }
            WriteForgeConfigAtomic(cfg);
            return GetCustomProviders();
        }

        private static bool IsDynamicProvider(string provider)
        {
            return string.Equals((provider ?? "").Trim(), "openai-compatible", StringComparison.OrdinalIgnoreCase);
        }

        // Map a stored provider/baseUrl back to the onboarding slug so settings can show it
        // ("nvidia", not the "openai-compatible" client id). Custom endpoints report as-is.
        private static string PresetSlugFor(string provider, string baseUrl)
        {
            string p = (provider ?? "").Trim().ToLowerInvariant();
            string bu = (baseUrl ?? "").Trim().ToLowerInvariant();
            // User-defined customs win over every built-in heuristic below: an anthropic-kind
            // custom stores provider "anthropic", and a compatible custom may sit on a built-in
            // host — either would otherwise collapse into the wrong slug and file keys/models
            // under the wrong provider. Bad custom entries are ignored, never thrown.
            try
            {
                var byUrl = FindCustomProviderByBaseUrl(baseUrl);
                if (byUrl != null)
                {
                    string cs = (string)byUrl["slug"];
                    if (!string.IsNullOrWhiteSpace(cs)) return cs.Trim().ToLowerInvariant();
                }
                var bySlug = FindCustomProvider(p);
                if (bySlug != null)
                {
                    string cs2 = (string)bySlug["slug"];
                    if (!string.IsNullOrWhiteSpace(cs2)) return cs2.Trim().ToLowerInvariant();
                }
            }
            catch { }
            if (p == "anthropic") return "anthropic";
            if (p == "ollama") return "ollama";
            // Ollama presets store the client id "openai-compatible" with a localhost
            // baseUrl — map those back to the "ollama" slug so the settings dropdown,
            // pill, and key check round-trip instead of showing "openai-compatible".
            if (bu.Contains("localhost") || bu.Contains("127.0.0.1") || bu.Contains("::1")) return "ollama";
            if (bu.Contains("integrate.api.nvidia.com")) return "nvidia";
            if (bu.Contains("openrouter.ai")) return "openrouter";
            if (bu.Contains("opencode.ai")) return "opencode";
            if (bu.Contains("api.deepseek.com")) return "deepseek";
            if (bu.Contains("api.openai.com")) return "openai";
            if (p == "openai-compatible") return "openai-compatible";
            return p;
        }

        private static string StoredProviderKey(string provider)
        {
            try { var k = KeyStore.Load(provider); if (!string.IsNullOrWhiteSpace(k)) return k; } catch { }
            // An empty/whitespace export counts as unset (same null contract as KeyStore.Load):
            // callers use this to decide hasKey and to build auth headers.
            try { var k = System.Environment.GetEnvironmentVariable("FORGE_PROVIDER_KEY"); return string.IsNullOrWhiteSpace(k) ? null : k; }
            catch { return null; }
        }

        // Slug-aware key lookup: per-slug files first (see SaveProviderKey), then a per-slug
        // env var (FORGE_PROVIDER_KEY_<SLUG>), then the legacy shared client-id file (adopted
        // once under the current slug), then the bare FORGE_PROVIDER_KEY env fallback.
        // Order matters: a key saved for one provider must never authenticate another.
        private static string StoredProviderKeyForSlug(string slug, string provider)
        {
            string s = (slug ?? "").Trim().ToLowerInvariant();
            string pid = (provider ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(s))
            {
                try
                {
                    var k = KeyStore.Load(s);
                    if (!string.IsNullOrWhiteSpace(k)) return k;
                }
                catch { }
                // Per-slug env beats the legacy shared file: an explicit key for THIS
                // provider outranks an unattributed old one. Checked even when slug ==
                // provider (e.g. anthropic) so the advertised FORGE_PROVIDER_KEY_<SLUG>
                // is actually read; ollama needs no key so it has no env.
                if (!string.Equals(s, "ollama", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        string eslug = Forge.Providers.ProviderFactory.EnvSlug(s);
                        var k = eslug == null ? null : System.Environment.GetEnvironmentVariable("FORGE_PROVIDER_KEY_" + eslug);
                        if (!string.IsNullOrWhiteSpace(k)) return k;
                    }
                    catch { }
                }
                // Kilo parity: OPENCODE_API_KEY is accepted alongside FORGE_PROVIDER_KEY_OPENCODE,
                // and also for opencode-go which shares the Zen credential infra (key required).
                if (string.Equals(s, "opencode", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(s, "opencode-go", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var k = System.Environment.GetEnvironmentVariable("OPENCODE_API_KEY");
                        if (!string.IsNullOrWhiteSpace(k)) return k;
                    }
                    catch { }
                }
                if (!string.Equals(s, pid, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var legacy = KeyStore.Load(pid);
                        if (!string.IsNullOrWhiteSpace(legacy))
                        {
                            bool saved = false;
                            try { KeyStore.Save(s, legacy); saved = true; } catch { saved = false; }
                            if (saved) { try { KeyStore.Delete(pid); } catch { } }
                            return legacy;
                        }
                    }
                    catch { }
                    // Missed everything slug-specific: fall through to StoredProviderKey(pid)
                    // so the bare FORGE_PROVIDER_KEY single-provider fallback still applies.
                }
            }
            return StoredProviderKey(pid);
        }

        internal static IProviderClient ResolveRunClient(out string model)
        {
            model = null;
            try
            {
                JObject cfg = ReadForgeConfig();
                string provider = null, baseUrl = null, m = null;
                try { provider = (string)cfg["provider"]; } catch { provider = null; }
                try { baseUrl = (string)cfg["baseUrl"]; } catch { baseUrl = null; }
                try { m = (string)cfg["model"]; } catch { m = null; }
                if (m != null) { m = m.Trim(); if (m.Length == 0) m = null; }
                model = m;
                string slug = null;
                try { slug = PresetSlugFor(provider, baseUrl); } catch { slug = null; }
                string key = null;
                try { key = StoredProviderKeyForSlug(slug, provider); } catch { key = null; }
                if (!string.IsNullOrWhiteSpace(model) && !string.IsNullOrWhiteSpace(baseUrl) && !string.IsNullOrWhiteSpace(key))
                {
                    IProviderClient c = null;
                    try { c = ProviderFactory.ResolveFor(slug, baseUrl, model, key); } catch { c = null; }
                    if (c != null) return c;
                }
            }
            catch { }
            model = null;
            try { return ProviderFactory.Resolve(out model); }
            catch { model = null; return null; }
        }

        // A 401/403 from /models almost always means a wrong or revoked key, not a broken
        // endpoint — report "nokey" so the panel shows key guidance instead of a raw HTTP error.
        // Anything else is an actionable wrapper, never the raw exception text: a bare
        // "Model list HTTP 500" or JSON-shape jargon tells the user nothing to act on.
        private static string MapModelsError(Exception ex)
        {
            string m = ex != null ? ex.Message : "";
            if ((m ?? "").Contains(" 401") || (m ?? "").Contains("(401)")) return "nokey";
            if ((m ?? "").Contains(" 403") || (m ?? "").Contains("(403)")) return "nokey";
            return "Couldn't list models (" + BoundDetail(m, 200) + ") — check the connection/key, then Refresh; or pick the model manually.";
        }

        // User-facing detail bound: raw exception text stays labeled and capped, never bare.
        private static string BoundDetail(string s, int max)
        {
            try
            {
                if (string.IsNullOrEmpty(s)) return "unexpected error";
                s = s.Trim();
                return s.Length <= max ? s : s.Substring(0, max) + "…";
            }
            catch { return "unexpected error"; }
        }

        // chatFilterSlug: null = no family filter; "opencode"/"opencode-go" apply their
        // respective chat-completions allow-lists. The Go list also admits digit-suffixed
        // family names (hy3-preview, hy4, minimax-m3) without loosening the public Zen list.
        private static async Task<System.Collections.Generic.List<string>> FetchServableModels(string baseUrl, string apiKey, string chatFilterSlug = null)
        {
            var ids = new System.Collections.Generic.List<string>();
            using (var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) })
            using (var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, baseUrl.TrimEnd('/') + "/models"))
            {
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
                using (var resp = await http.SendAsync(req).ConfigureAwait(false))
                {
                    string text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode) throw new Exception("Model list request failed (HTTP " + (int)resp.StatusCode + " " + resp.StatusCode + ") — the provider refused the list; check the key, then Refresh.");
                    var data = JObject.Parse(text)["data"] as JArray;
                    if (data == null) throw new Exception("The provider returned a model list Forge couldn't read — try Refresh again; if it repeats, set the model manually.");
                    bool go = string.Equals(chatFilterSlug, "opencode-go", StringComparison.OrdinalIgnoreCase);
                    string[] families = go ? OpenCodeGoChatFamilies
                        : (string.Equals(chatFilterSlug, "opencode", StringComparison.OrdinalIgnoreCase) ? OpenCodeChatFamilies : null);
                    foreach (var e in data)
                    {
                        string id = (string)e["id"];
                        if (string.IsNullOrWhiteSpace(id)) continue;
                        string lc = id.ToLowerInvariant();
                        bool skip = false;
                        foreach (var h in NonChatModelHints) { if (lc.Contains(h)) { skip = true; break; } }
                    if (!skip && families != null)
                    {
                        string name = lc;
                        int slash = name.LastIndexOf('/');
                        if (slash >= 0) name = name.Substring(slash + 1);
                        bool allowedFamily = false;
                        foreach (var f in families)
                        {
                            if (name == f || name.StartsWith(f + "-", StringComparison.Ordinal)) { allowedFamily = true; break; }
                            // Go-only: a non-letter boundary still belongs to the leading
                            // family (hy3-preview, hy4, minimax-m3). The public Zen list
                            // keeps its strict prefix rule so qwen3.*-(max|plus) stay out.
                            if (go && name.Length > f.Length && name.StartsWith(f, StringComparison.Ordinal)
                                && !char.IsLetter(name[f.Length])) { allowedFamily = true; break; }
                        }
                        if (!allowedFamily)
                        {
                            string path = "chat";
                            try { path = ProviderFactory.WirePathFor(baseUrl, id); } catch { path = "chat"; }
                            if (!string.Equals(path, "chat", StringComparison.OrdinalIgnoreCase)) allowedFamily = true;
                        }
                        if (!allowedFamily) skip = true;
                    }
                        if (!skip) ids.Add(id);
                    }
                }
            }
            ids.Sort();
            return ids;
        }

        private static ProviderPreset ProviderPresetFor(string slug)
        {
            // User-defined custom: kind is the stored client id (openai-compatible |
            // anthropic) so Resolve routes to the right wire client with its retries,
            // temperature gate, and reasoning fallback. Model starts as the remembered
            // pick, else the first registered custom id, else empty (user adds one).
            try
            {
                var custom = FindCustomProvider(slug);
                if (custom != null)
                {
                    string kind = ((string)custom["kind"] ?? "openai-compatible").Trim();
                    string curl = ((string)custom["baseUrl"] ?? "").Trim().TrimEnd('/');
                    string cmodel = null;
                    try
                    {
                        var ca = GetCustomModels(slug);
                        if (ca != null && ca.Count > 0) cmodel = (string)((JObject)ca[0])["id"];
                    }
                    catch { }
                    return new ProviderPreset(kind, curl, string.IsNullOrWhiteSpace(cmodel) ? "" : cmodel.Trim());
                }
            }
            catch { }
            switch (slug)
            {
                case "openrouter": return new ProviderPreset("openai-compatible", "https://openrouter.ai/api/v1", "openai/gpt-4o-mini");
                case "opencode":   return new ProviderPreset("openai-compatible", "https://opencode.ai/zen/v1", "glm-5.3-flash");
                case "opencode-go": return new ProviderPreset("openai-compatible", "https://opencode.ai/zen/go/v1", "glm-5.3-flash");
                case "nvidia":     return new ProviderPreset("openai-compatible", "https://integrate.api.nvidia.com/v1", "moonshotai/kimi-k3.1-pro");
                case "openai":     return new ProviderPreset("openai-compatible", "https://api.openai.com/v1", "gpt-6-luna"); // gpt-4o-mini snapshot shuts down 2027-01-14
                case "anthropic":  return new ProviderPreset("anthropic", "https://api.anthropic.com/v1", "claude-3-5-sonnet-20241022");
                case "ollama":     return new ProviderPreset("openai-compatible", "http://localhost:11434/v1", "llama3.2");
                case "deepseek":
                default:           return new ProviderPreset("openai-compatible", "https://api.deepseek.com/v1", "deepseek-flash"); // deepseek-chat discontinued 2026-07-24
            }
        }

        // Prompt enrichment (Kilo "Enhance Prompt" port, SolidWorks-tailored): a dedicated single LLM call —
        // no agent, no tools — that rewrites the draft in the composer into a concrete dimensioned build/edit
        // spec. The result goes back into the input for review, never auto-sent. Needs a provider (keyless
        // chat can't call an LLM), so keyless users get a pointer instead of silence.
        private async Task EnhancePrompt(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                Send(new { type = "enhanced", text = "Describe what to build or change — e.g. 'a wall-mount bracket 100x80x6 with 2 M6 holes'. Then ✨ Enhance turns it into a concrete spec; review, edit, send." });
                return;
            }
            // Same blob policy as Generate: fail closed before the streaming call.
            if (text.Length > 4000)
            {
                Send(new { type = "error", message = "That draft is too long (" + text.Length + " chars, cap is 4000) — trim it to the key dims, then ✨ again." });
                return;
            }
            IProviderClient client;
            string picked;
            try { client = ResolveRunClient(out picked); }
            catch { client = null; picked = null; }
            if (client == null)
            {
                string eslug0 = null;
                try { var ec0 = ReadForgeConfig(); eslug0 = PresetSlugFor((string)ec0["provider"], (string)ec0["baseUrl"]); } catch { eslug0 = null; }
                string ewhere = "Settings → API key";
                    if (!string.IsNullOrWhiteSpace(eslug0) && !string.Equals(eslug0.Trim(), "ollama", StringComparison.OrdinalIgnoreCase))
                    {
                        string eenv = Forge.Providers.ProviderFactory.EnvSlug(eslug0);
                        ewhere += " (or set " + (eenv == null ? "FORGE_PROVIDER_KEY" : "FORGE_PROVIDER_KEY_" + eenv) + ")";
                    }
                Send(new { type = "error", message = "Enhance needs an LLM provider — save a key in " + ewhere + ", then try again." });
                return;
            }
            try { if (_enhanceCts != null) _enhanceCts.Cancel(); } catch { }
            try { if (_enhanceCts != null) _enhanceCts.Dispose(); } catch { }
            _enhanceCts = new System.Threading.CancellationTokenSource();
            var ect = _enhanceCts.Token;
            int myE = ++_enhanceSeq; // a newer Enhance owns the panel: stay silent when stale
            System.Diagnostics.Stopwatch watch = null; // hoisted: catch blocks below report elapsed time
            // Small model for rewrites: cheap + fast, falls back to the picked model
            // when no known-cheap model exists for this provider. Temp 0.7: rewrites
            // want a little variance, not determinism.
            string eprovider = null, ebase = null;
            try { var ecfg = ReadForgeConfig(); eprovider = (string)ecfg["provider"]; ebase = (string)ecfg["baseUrl"]; } catch { }
            string esmall = ProviderFactory.GetSmallModel(eprovider, ebase);
            string useModel = string.IsNullOrWhiteSpace(esmall) ? picked : esmall;
            try
            {
                var msgs = new List<ChatMessage>
                {
                    new ChatMessage { Role = "system", Content = EnhanceInstruction },
                    new ChatMessage { Role = "user", Content = "Draft request to enhance, not answer:\n\n" + text.Trim() }
                };
                // Stream the chain-of-thought live (Kilo-style) instead of a dead timer: reasoning
                // deltas print into a thinking block as they arrive; the cleaned result still lands
                // in the composer for review. Marshalled to the UI thread — the stream runs on IO threads.
                    // picked is the run's Resolve snapshot: a picker switch mid-stream can't mix models.
                    var ui = System.Threading.SynchronizationContext.Current;
                    try { watch = System.Diagnostics.Stopwatch.StartNew(); } catch { }
                Send(new { type = "thinkStart", model = useModel, label = "Enhancing" });
                // Temp 0.7 only when the model accepts custom temperatures (Kilo enhance-prompt.ts);
                // reasoning variants get the provider default instead of a rejected param.
                double? enhanceTemp = ProviderFactory.SupportsTemperature(useModel) ? 0.7 : (double?)null;
                string enhanceEffort = ConfiguredReasoningEffort(useModel);
                try { McpBuild.ReasoningEffort = enhanceEffort; } catch { }
                ProviderResult res;
                try
                {
                    res = await client.CompleteStreamingAsync(msgs,
                        new ProviderOptions { MaxTokens = 32000, Temperature = enhanceTemp, Model = useModel, ReasoningEffort = enhanceEffort, Headers = ExpandedHeaderDictionary(CurrentProviderSlug()), SessionId = ActiveSidForProvider() },
                        ect,
                        (kind, delta) =>
                        {
                            if (kind != "reasoning" || string.IsNullOrEmpty(delta)) return;
                            if (myE != _enhanceSeq) return; // superseded: don't interleave the new Enhance's block
                            try
                            {
                                if (ui != null) ui.Post(_ => { try { if (myE != _enhanceSeq) return; Send(new { type = "think", text = delta }); } catch { } }, null);
                                else Send(new { type = "think", text = delta });
                            }
                            catch { }
                        });
                }
                catch (ProviderException px) when ((px.Message ?? "").Contains("Streaming is not supported"))
                {
                    // Non-streaming backends (Anthropic): same prompt as one unary call, no live deltas.
                    res = await client.CompleteAsync(msgs,
                        new ProviderOptions { MaxTokens = 32000, Temperature = enhanceTemp, Model = useModel, ReasoningEffort = enhanceEffort, Headers = ExpandedHeaderDictionary(CurrentProviderSlug()), SessionId = ActiveSidForProvider() },
                        ect);
                }
                watch.Stop();
                if (myE != _enhanceSeq) return; // superseded while streaming: the new Enhance owns the panel
                Send(new { type = "thinkEnd", seconds = (int)watch.Elapsed.TotalSeconds });
                string clean = CleanEnhanced(res != null ? res.Text : null);
                if (string.IsNullOrWhiteSpace(clean)) throw new Exception("empty enhancement");
                try { if (res != null) IntentRate.Note(res.TokIn, res.TokOut); } catch { }
                try { if (res != null) McpBuild.NoteCtxCurrent(res.TokIn, res.TokOut); } catch { } // enhance spends session context too
                Send(new { type = "enhanced", text = clean });
            }
            catch (OperationCanceledException)
            {
                if (myE != _enhanceSeq) return; // superseded: the new Enhance owns the panel
                try { Send(new { type = "thinkEnd", seconds = 0, interrupted = true }); } catch { }
                Send(new { type = "answer", answer = "Enhance interrupted — partial thinking is kept above. Edit and resend, or ✨ again." });
            }
            catch (Exception ex)
            {
                if (myE != _enhanceSeq) return; // superseded: the new Enhance owns the panel
                try { Send(new { type = "thinkEnd", seconds = watch != null ? (int)watch.Elapsed.TotalSeconds : 0, interrupted = true }); } catch { }
                string detail = BoundDetail(ex.Message, 300);
                if ((detail ?? "").Contains("timed out"))
                    detail = "TIMED OUT after 10 minutes — the model is overloaded or thinking too long; try again or switch to a faster model in the picker";
                Send(new { type = "error", message = "Enhance failed: " + detail + " — your draft is untouched, just send it as-is." });
            }
        }

        private static string CleanEnhanced(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            string s = System.Text.RegularExpressions.Regex.Replace(text.Trim(), @"^```\w*\n?|```$", "").Trim();
            var m = System.Text.RegularExpressions.Regex.Match(s, @"^(['""])([\s\S]*)\1$");
            if (m.Success) s = m.Groups[2].Value.Trim();

            // Strip chain-of-thought / instruction-echo preamble: drop leading lines until the actual
            // request starts (a build verb, spec noun, or question), then remove narration / rule-echo lines.
            // Never return null for non-empty input: a rewrite the filters don't recognize is still more
            // useful than an "empty enhancement" error, so fall back to the fence-stripped text.
            string[] lines = s.Split(new[] { "\r\n", "\n" }, System.StringSplitOptions.None);
            var buildRx = new System.Text.RegularExpressions.Regex(
                @"^\s*(build|create|make|design|modify|change|add|assemble|machine|model|overall|envelope|part|assembly|what|which|how|when|where|why|is|are|can|do|does|should)\b|\?\s*$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var narrRx = new System.Text.RegularExpressions.Regex(
                @"^\s*(the user wants|the draft|key rules|rules?:|here'?s|here is|let me|i('ll| will| need to| should)|okay|sure|draft:|enhanced (prompt|request)?\s*:|plan:|explanation:|return only|treat the next|starting directly)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            // Numbered instruction echoes only. "metric"/"fillets" are deliberately NOT here: they are
            // ordinary CAD words ("2. 4x M6 metric holes") and stripping them eats legit spec lines.
            var ruleEchoRx = new System.Text.RegularExpressions.Regex(
                @"^\s*\d+\.\s+.*(treat the next|return only|keep the user|assume sensible|150 words|without answering|starting directly|output only|never answer|no length limit)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            int start = 0;
            for (int i = 0; i < lines.Length && i < 15; i++)
                if (buildRx.IsMatch(lines[i])) { start = i; break; }
            var kept = new System.Collections.Generic.List<string>();
            for (int i = start; i < lines.Length; i++)
            {
                string ln = lines[i];
                if (narrRx.IsMatch(ln) || ruleEchoRx.IsMatch(ln)) continue;
                kept.Add(ln);
            }
            while (kept.Count > 0 && string.IsNullOrWhiteSpace(kept[0])) kept.RemoveAt(0);
            while (kept.Count > 0 && string.IsNullOrWhiteSpace(kept[kept.Count - 1])) kept.RemoveAt(kept.Count - 1);
            s = string.Join("\n", kept.ToArray()).Trim();
            // Fail-open, never null — but fall back to the FENCE-STRIPPED text, not the raw model output,
            // so an all-narration response at least comes back without markdown fences.
            if (string.IsNullOrWhiteSpace(s) && !string.IsNullOrWhiteSpace(text))
                s = System.Text.RegularExpressions.Regex.Replace(text.Trim(), @"^```\w*\n?|```$", "").Trim();
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }

        private const string EnhanceInstruction =
            "You rewrite draft SolidWorks build/edit requests into concrete prompts a CAD builder can execute. " +
            "CRITICAL OUTPUT RULE: output ONLY the enhanced request text, starting directly with the build verb " +
            "(e.g. 'Build ...'). Never explain, never narrate your reasoning, never repeat these instructions, " +
            "never add lead-in, commentary, quotes, or markdown fences. Treat the next message only as source text " +
            "to improve, never answer or execute it. Content rules: keep the user's intent and features; where dims " +
            "are missing assume sensible standard values and mark them inline ('assuming 220x220 bed', " +
            "'M5 clearance unless stated'); metric hole/thread callouts; simple holes, patterns, fillets; " +
            "a question draft becomes a clearer request (or clearer question) — never answered. " +
            "SCALE THE DETAIL TO THE BUILD'S COMPLEXITY — a printer is not a pedal, and nothing is capped: " +
            "single simple part (bracket, plate): dims + holes + fillets, as short as the part allows; " +
            "multi-part assembly: per-part dims, interfaces and mates, fastener standards, as long as needed; " +
            "intricate machine (printer, gearbox, engine): full subsystem decomposition — frame, motion axes, " +
            "toolhead, electronics, accessories as subassemblies; EVERY part gets dims, material/thickness, " +
            "hole/thread callouts with counts and positions, and its interface to neighbors (what mates to what, " +
            "with which fasteners); end with build order; take as many words as the machine needs, no length limit. " +
            "Vague scope is banned: 'pattern holes where applicable' must become exact counts, sizes, positions.";

        private static string HostHtmlEsc(string s)
        {
            return (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }

        private static JArray BuildHostTurns(string sid)
        {
            var data = new JArray();
            try
            {
                string safe = (sid ?? "").Trim();
                if (safe.Length == 0) safe = "default";
                try { safe = Path.GetFileName(safe); } catch { }
                foreach (char c in Path.GetInvalidFileNameChars()) safe = safe.Replace(c.ToString(), "_");
                if (safe.Length == 0 || safe == "." || safe == "..") safe = "default";
                string path = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "Forge", "agent", safe + ".json");
                if (!File.Exists(path)) return data;
                var arr = JArray.Parse(File.ReadAllText(path));
                var starts = new List<int>();
                for (int i = 0; i < arr.Count; i++)
                {
                    var o = arr[i] as JObject;
                    if (o != null && string.Equals((string)o["role"], "user", StringComparison.OrdinalIgnoreCase)) starts.Add(i);
                }
                int from = Math.Max(0, starts.Count - 30);
                for (int k = from; k < starts.Count; k++)
                {
                    int start = starts[k];
                    int end = k + 1 < starts.Count ? starts[k + 1] : arr.Count;
                    string q = "";
                    var body = new System.Text.StringBuilder();
                    for (int i = start; i < end; i++)
                    {
                        var o = arr[i] as JObject;
                        if (o == null) continue;
                        string role = (string)o["role"] ?? "";
                        string content = (string)o["content"] ?? "";
                        if (string.Equals(role, "user", StringComparison.OrdinalIgnoreCase))
                        {
                            if (i == start) q = content;
                            continue;
                        }
                        if (content.Length == 0) continue;
                        if (body.Length > 0) body.Append("<br>");
                        body.Append(HostHtmlEsc(content));
                    }
                    string bodyHtml = body.ToString();
                    if (bodyHtml.Length > 59900) bodyHtml = bodyHtml.Substring(0, 59900) + "… (truncated)";
                    string a = bodyHtml.Length > 0 ? "<div class=\"card\"><div class=\"answer\">" + bodyHtml + "</div></div>" : "";
                    if (q.Length > 60000) q = q.Substring(0, 60000) + "… (truncated)";
                    data.Add(new JObject { ["q"] = q, ["a"] = a });
                }
            }
            catch { }
            return data;
        }

        private async Task Generate(string intent, string sid, Newtonsoft.Json.Linq.JArray postedPaths)
        {
            SyncMcpProjectScope();
            try { McpBuild.ProviderHeaders = ExpandedHeaderDictionary(CurrentProviderSlug()); } catch { McpBuild.ProviderHeaders = null; }
            try { McpBuild.ReasoningEffort = ConfiguredReasoningEffortFor(ProviderFactory.CurrentModel()); } catch { McpBuild.ReasoningEffort = null; }
            if (string.IsNullOrWhiteSpace(intent)) intent = "";
            // A pasted blob (50KB dumps, emoji walls, log files) must fail closed here: no regex
            // storm in the matchers, no token blowup in the LLM calls, no bloat in telemetry logs.
            if (intent.Length > 4000) { Send(new { type = "error", message = "That request is too long (" + intent.Length + " chars, cap is 4000) — paste just the key dims and steps, then send." }); return; }
            if (string.IsNullOrWhiteSpace(sid)) sid = null; // McpBuild falls back to "default"
            // Cancel the in-flight run BEFORE minting the new id: a stale run's last-gasp
            // Send then carries its own (dead) id instead of the new run's.
            try { if (_runCts != null) _runCts.Cancel(); } catch { }
            try { if (_runCts != null) _runCts.Dispose(); } catch { }
            _runCts = new System.Threading.CancellationTokenSource();
            var runCt = _runCts.Token;
            // A run supersedes any in-flight Enhance: cancel its call and invalidate its visuals so a
            // late "enhanced"/think delta can't clobber the composer after this run starts.
            try { if (_enhanceCts != null) _enhanceCts.Cancel(); } catch { }
            _enhanceSeq++;
            _currentRunId = Guid.NewGuid().ToString("N").Substring(0, 12);   // tags this run for thumbs feedback + correction capture
            string myRun = _currentRunId; // a newer send supersedes this run: abandon silently when stale

            // @-mentioned files ride with the prompt (posted paths + @tokens in the text). The first
            // doubles as the attach-button file so Compare-class handlers pick it up with no doc open.
            try
            {
                var mentions = ResolveMentions(intent, postedPaths);
                lock (_mentionedFiles) { _mentionedFiles.Clear(); _mentionedFiles.AddRange(mentions); }
                if (string.IsNullOrWhiteSpace(_attachedFile) && mentions.Count > 0) _attachedFile = mentions[0];
            }
            catch { }

            // DEMO ROUTE â€” authoritative, and BEFORE anything that can `await`/yield (the one-time UpdateNotice
            // below does a network call). flat-pattern-DXF / drawing-package / interference route here so (a) a
            // misclassification can't drop them to the generic answer path, and (b) â€” critical for interference â€”
            // their apartment-bound COM runs on THIS UI/STA thread, never a post-await threadpool thread (a
            // threadpool COM read on the InterferenceDetectionManager access-violates and takes SolidWorks down).
            if (await TryDemoRouteFirst(intent)) return;
            if (myRun != _currentRunId) return; // superseded while awaiting: the new run owns the panel

            // One-time session notices: an interrupted last run (crash recovery) + a newer build available.
            if (!_noticesShown)
            {
                _noticesShown = true;
                try { var crash = ForgeData.CheckInterrupted(); if (crash != null) Send(new { type = "answer", answer = crash }); } catch { }
                if (Telemetry.Enabled) { try { var upd = await ForgeData.UpdateNotice(); if (upd != null && myRun == _currentRunId) Send(new { type = "answer", answer = upd }); } catch { } }
            }
            if (myRun != _currentRunId) return; // superseded during the notices: stay silent

            // Privacy / anonymous-usage control (data contract in docs/DATA.md).
            string _lc = (intent ?? "").Trim().ToLowerInvariant();
            if (System.Text.RegularExpressions.Regex.IsMatch(_lc, @"\b(share data|usage data|privacy|telemetry|what do you collect|data sharing)\b"))
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(_lc, @"\b(off|stop|disable|don't|dont)\b")) Telemetry.Enabled = false;
                else if (System.Text.RegularExpressions.Regex.IsMatch(_lc, @"\b(on|start|enable)\b")) Telemetry.Enabled = true;
                Send(new { type = "answer", answer = ForgeData.PrivacySummary() }); return;
            }

            // A preview was shown last turn and is awaiting a "go" â€” resolve it before anything else.
            if (await ResolvePendingConfirm(intent)) return;
            if (myRun != _currentRunId) return; // superseded while awaiting: the new run owns the panel

            // Change-impact FOLLOW-UP (Demo 3, RULE #2): after "what breaks if I change X?" a reply like
            // "show me the pattern" / "highlight it" references the dependent we just named â€” resolve + SHOW it
            // instead of re-asking. Only fires when we have a remembered impact AND the reply is anaphoric.
            if (TryImpactFollowup(intent)) return;
            if (myRun != _currentRunId) return; // superseded while awaiting: the new run owns the panel

            // FROM-SCRATCH CREATE — before every edit-oriented matcher (resizer/mate/mirror/...): an
            // enriched build spec is full of fastener callouts (M3/M4/M5) and mate words ("mount") that
            // those matchers would otherwise claim with no doc open ("Open your assembly first (.SLDASM) — open it, then resend"). Verb-gated
            // (create/make/build/...), so edit flows keep theirs. Needs no open document.
            if (await TryRunMcpBuild(intent, runCt, sid)) return;
            if (myRun != _currentRunId) return; // superseded while awaiting: the new run owns the panel
            if (await TryRunCreateRecipe(intent, runCt)) return;
            if (myRun != _currentRunId) return;

            // Bulk resize fasteners (Demo #6, M6â†’M8) â€” route AUTHORITATIVELY before the cloud parse. A clear
            // "replace all the M6 bolts with M8" must ACT, never fall to the parser's spurious "bolt sizes not
            // stated" ambiguity + the destructive-write hedge (RULE #2). Gated on 2 M-tokens so it's specific.
            if (Resizer.IsResizeIntent((intent ?? "").Trim().ToLowerInvariant()))
            {
                var doc = SwAddin.SwApp?.ActiveDoc as IModelDoc2;
                if (doc == null) { Send(new { type = "error", message = "Open your assembly first (.SLDASM) — open it, then resend." }); return; }
                if ((int)doc.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
                { Send(new { type = "error", message = "Open the assembly (.SLDASM) to resize fasteners." }); return; }

                OpCounter.Increment();
                ForgeData.RunBegin("resize", ForgeData.Mask(intent, IntentLayer.ComponentNames(doc)), 0);
                Func<string, string, string, string, Task> emitR = async (agent, gloss, state, result) =>
                { Send(new { type = "step", agent, gloss, state, result }); await Task.Delay(60); };

                ResizeResult rr = await Resizer.Run(SwAddin.SwApp, doc, intent, emitR);
                if (myRun != _currentRunId) return; // superseded while awaiting: the new run owns the panel
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
            bool piped = false;
            try { piped = await RunViaPipeline(intent, runCt, sid); }
            catch (OperationCanceledException)
            {
                // Stop/revert during the pipeline: stay silent when a newer run owns the
                // panel, otherwise report the interrupt once.
                if (myRun != _currentRunId) return;
                Send(new { type = "answer", answer = "Interrupted — nothing further ran. Edit and resend.", runId = _currentRunId });
                return;
            }
            if (myRun != _currentRunId) return;
            if (piped) return;

            // Auto-mate â€” the first "doer". Works on an ASSEMBLY, so intercept here BEFORE ActivePart()
            // (which only accepts parts). Deploys the Gauge â†’ Torque â†’ Sentinel crew with live narration.
            if (AutoMate.IsMateIntent((intent ?? "").Trim().ToLowerInvariant()))
            {
                var doc = SwAddin.SwApp?.ActiveDoc as IModelDoc2;
                if (doc == null) { Send(new { type = "error", message = "Open your assembly first (.SLDASM) — open it, then resend." }); return; }
                if ((int)doc.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
                { Send(new { type = "error", message = "Open the assembly (.SLDASM) to mate fasteners." }); return; }

                OpCounter.Increment();
                ForgeData.RunBegin("mate", ForgeData.Mask(intent, IntentLayer.ComponentNames(doc)), 0);
                var swM = System.Diagnostics.Stopwatch.StartNew();
                Func<string, string, string, string, Task> emit = async (agent, gloss, state, result) =>
                { Send(new { type = "step", agent, gloss, state, result }); await Task.Delay(60); };

                MateResult mr = await AutoMate.Run(SwAddin.SwApp, doc, emit);
                if (myRun != _currentRunId) return; // superseded while awaiting: the new run owns the panel
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
                if (doc == null) { Send(new { type = "error", message = "Open your assembly first (.SLDASM) — open it, then resend." }); return; }
                if ((int)doc.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
                { Send(new { type = "error", message = "Open the assembly (.SLDASM) to mirror a component." }); return; }

                OpCounter.Increment();
                ForgeData.RunBegin("mirror", ForgeData.Mask(intent, IntentLayer.ComponentNames(doc)), 0);
                Func<string, string, string, string, Task> emitMir = async (agent, gloss, state, result) =>
                { Send(new { type = "step", agent, gloss, state, result }); await Task.Delay(60); };

                MirrorResult mir = await Mirror.Run(SwAddin.SwApp, doc, intent, emitMir);
                if (myRun != _currentRunId) return; // superseded while awaiting: the new run owns the panel
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
                if (doc == null) { Send(new { type = "error", message = "Open your assembly first (.SLDASM) — open it, then resend." }); return; }
                if ((int)doc.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
                { Send(new { type = "error", message = "Open the assembly (.SLDASM) to explode it." }); return; }

                OpCounter.Increment();
                ForgeData.RunBegin("explode", ForgeData.Mask(intent, IntentLayer.ComponentNames(doc)), 0);
                Func<string, string, string, string, Task> emitX = async (agent, gloss, state, result) =>
                { Send(new { type = "step", agent, gloss, state, result }); await Task.Delay(60); };

                ExplodeResult xr = await Exploder.Run(SwAddin.SwApp, doc, intent, emitX);
                if (myRun != _currentRunId) return; // superseded while awaiting: the new run owns the panel
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
                if (doc == null) { Send(new { type = "error", message = "Open a part or assembly first (.SLDPRT/.SLDASM) — open it, then resend." }); return; }

                OpCounter.Increment();
                ForgeData.RunBegin("set_material", ForgeData.Mask(intent, IntentLayer.ComponentNames(doc)), 0);
                var mPlan = await IntentLayer.Parse(intent, doc);
                if (myRun != _currentRunId) return; // superseded while awaiting: the new run owns the panel
                if (mPlan.Error != null) { Send(new { type = "error", message = mPlan.Error }); return; }
                await RunMaterialPlan(doc, mPlan, intent);
                return;
            }

            // Scan / assembly doctor â€” READ-ONLY report on an assembly. Assembly-only, intercept before ActivePart.
            if (Scout.IsScanIntent((intent ?? "").Trim().ToLowerInvariant()))
            {
                var doc = SwAddin.SwApp?.ActiveDoc as IModelDoc2;
                if (doc == null) { Send(new { type = "error", message = "Open your assembly first (.SLDASM) — open it, then resend." }); return; }
                if ((int)doc.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
                { Send(new { type = "error", message = "Open the assembly (.SLDASM) to scan it." }); return; }

                OpCounter.Increment();
                Func<string, string, string, string, Task> emitSc = async (agent, gloss, state, result) =>
                { Send(new { type = "step", agent, gloss, state, result }); await Task.Delay(60); };

                ScoutResult scr = await Scout.Run(SwAddin.SwApp, doc, emitSc);
                if (myRun != _currentRunId) return; // superseded while awaiting: the new run owns the panel
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
                if (doc == null) { Send(new { type = "error", message = "Open your assembly first (.SLDASM) — open it, then resend." }); return; }
                if ((int)doc.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
                { Send(new { type = "error", message = "Open the assembly (.SLDASM) to isolate parts." }); return; }

                OpCounter.Increment();
                ForgeData.RunBegin("isolate", ForgeData.Mask(intent, IntentLayer.ComponentNames(doc)), 0);
                Func<string, string, string, string, Task> emitI = async (agent, gloss, state, result) =>
                { Send(new { type = "step", agent, gloss, state, result }); await Task.Delay(60); };

                IsolateResult ir = await Isolator.Run(SwAddin.SwApp, doc, intent, emitI);
                if (myRun != _currentRunId) return; // superseded while awaiting: the new run owns the panel
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
                if (doc == null) { Send(new { type = "error", message = "Open your assembly first (.SLDASM) — open it, then resend." }); return; }
                if ((int)doc.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
                { Send(new { type = "error", message = "Open the assembly (.SLDASM) to batch-simplify its parts." }); return; }

                OpCounter.Increment();
                ForgeData.RunBegin("batch", ForgeData.Mask(intent, IntentLayer.ComponentNames(doc)), 0);
                Func<string, string, string, string, Task> emitB = async (agent, gloss, state, result) =>
                { Send(new { type = "step", agent, gloss, state, result }); await Task.Delay(60); };

                BatchResult br = await Batcher.Run(SwAddin.SwApp, doc, intent, emitB);
                if (myRun != _currentRunId) return; // superseded while awaiting: the new run owns the panel
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
                if (doc == null) { Send(new { type = "error", message = "Open a part or assembly first (.SLDPRT/.SLDASM) — open it, then resend." }); return; }

                OpCounter.Increment();
                ForgeData.RunBegin("simplify", ForgeData.Mask(intent, IntentLayer.ComponentNames(doc)), 0);
                Func<string, string, string, string, Task> emitS = async (agent, gloss, state, result) =>
                { Send(new { type = "step", agent, gloss, state, result }); await Task.Delay(60); };

                if ((int)doc.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY)
                {
                    BatchResult br = await Batcher.Run(SwAddin.SwApp, doc, intent, emitS);
                    if (myRun != _currentRunId) return; // superseded while awaiting: the new run owns the panel
                    Telemetry.Log("edit_run", success: br.Error == null, featuresCount: br.TotalSuppressed, swVersion: SwVer(),
                        opsUsed: OpCounter.Count, errorCode: br.Error == null ? null : "batch", questionType: "edit", questionSummary: "batch simplify parts");
                    if (br.Error != null) { LogRun("batch", intent, doc, "error", false, 0, errorCode: "batch"); Send(new { type = "error", message = br.Error }); return; }
                    LogRun("batch", intent, doc, "executed", br.Error == null, br.TotalSuppressed);
                    Send(new { type = "answer", answer = br.Info, runId = _currentRunId, handler = "batch" });
                    return;
                }

                SimplifyResult sr = await Simplifier.Run(SwAddin.SwApp, doc, intent, emitS);
                if (myRun != _currentRunId) return; // superseded while awaiting: the new run owns the panel
                Telemetry.Log("edit_run", success: sr.Error == null, featuresCount: sr.Suppressed, swVersion: SwVer(),
                    opsUsed: OpCounter.Count, errorCode: sr.Error == null ? null : "simplify", questionType: "edit", questionSummary: "simplify / print-prep");
                if (sr.Error != null) { LogRun("simplify", intent, doc, "error", false, 0, errorCode: "simplify"); Send(new { type = "error", message = sr.Error }); return; }
                LogRun("simplify", intent, doc, "executed", sr.Error == null, sr.Suppressed);
                Send(new { type = "answer", answer = sr.Info, runId = _currentRunId, handler = "simplify" });
                return;
            }

            // Document-aware fall-through (Labtec bug #1): a doc-generic / free-form ask must never die with a bare
            // "must be a part" dead-end. No doc or drawing -> a written message. Part OR assembly -> let the router
            // answer with real context; only the part-MUTATION actions below (edit / variant / share / apply)
            // require a part, and those get a friendly message, never a raw throw.
            var model = SwAddin.SwApp?.ActiveDoc as IModelDoc2;
            if (model == null)
            {
                Send(new { type = "error", message = "Open a part or assembly first (.SLDPRT/.SLDASM) — open it, then resend." });
                return;
            }
            bool isPart = (int)model.GetType() == (int)swDocumentTypes_e.swDocPART;
            if ((int)model.GetType() == (int)swDocumentTypes_e.swDocDRAWING)
            {
                Send(new { type = "error", message = "Forge edits parts and assemblies, not drawings - open a model and ask again." });
                return;
            }
            var dims = VariantGenerator.ReadDimensions(model);
            var deps = VariantGenerator.ReadDependencies(model);

            // The engineer can point at the exact dimension/feature by selecting it in SolidWorks.
            // Mark those so the AI targets what they pinned instead of guessing.
            var selected = GetSelectedDimNames(model, dims);
            foreach (var d in dims) d.Selected = selected.Contains(d.Name);
            var selectedFeatures = GetSelectedFeatureNames(model);

            // CAD git: share my version, or apply a teammate's pending change. Part-only (dims-based).
            string cmd = (intent ?? "").Trim().ToLowerInvariant();
            if (cmd.Contains("share") && (cmd.Contains("version") || cmd.Contains("change") || cmd.Contains("part") || cmd.Contains("team") || cmd.Contains("my")))
            {
                if (!isPart)
                { Send(new { type = "error", message = "Sharing works on a part - open the part you want to share." }); return; }
                string project = ProjectKey(model);
                if (string.IsNullOrEmpty(project)) { Send(new { type = "error", message = "Save the part before sharing." }); return; }
                if (!Telemetry.Enabled) { Send(new { type = "error", message = "Sharing is OFF while usage sharing is OFF — say \"share data on\" or flip it in Settings to share. Nothing left this machine." }); return; }
                await ForgeApi.PushModel(project, "", dims);   // anonymous: never send the Windows username
                if (myRun != _currentRunId) return; // superseded while awaiting: the new run owns the panel
                Send(new { type = "shared", count = dims.Count });
                return;
            }
            if (_pendingIncoming != null && (cmd == "apply" || cmd.StartsWith("apply") || cmd == "accept" || cmd == "pull" || cmd.Contains("update my")))
            {
                if (!isPart)
                { Send(new { type = "error", message = "That applies a part change - open the part it belongs to." }); return; }
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
            if (myRun != _currentRunId) return; // superseded while awaiting: the new run owns the panel

            // The server hit the per-operation token ceiling (the model is too large for one pass).
            if (act.Action == "ceiling")
            {
                Telemetry.Log("token_ceiling_hit", success: false, swVersion: SwVer(),
                    tokensIn: act.Meter.TokensIn, tokensOut: act.Meter.TokensOut, opsUsed: OpCounter.Count);
                Send(new { type = "error", message = "That model is too large for one operation - simplify the part or split the request into smaller steps." });
                return;
            }

            // Ambiguous: ask, and remember the thread for the engineer's next reply.
            // Labtec bug #1: the router's "clarify" can be a CANNED dead-end ("didn't map that to one of my tools")
            // or the same unanswerable question every turn. Never relay that back and forth - the user gets ONE
            // concrete suggestion and the thread is dropped so it can't repeat.
            if (act.Action == "clarify")
            {
                string cl = (act.Clarify ?? "").Trim();
                bool repeated = _lastRelayedClarify != null && string.Equals(_lastRelayedClarify, cl, StringComparison.Ordinal);
                if (IsCloudDeadEnd(cl) || repeated)
                {
                    Telemetry.Log("couldnt_map", success: false, swVersion: SwVer(), questionType: act.QuestionType, questionSummary: act.QuestionSummary);
                    SendUnmappedSuggestion();
                    return;
                }
                Telemetry.Log("couldnt_map", success: false, swVersion: SwVer(), questionType: act.QuestionType, questionSummary: act.QuestionSummary);
                _pendingIntent = effective;
                _lastRelayedClarify = cl;
                Send(new { type = "clarify", message = cl });
                return;
            }
            _pendingIntent = null; // a real action happened â€” the conversation thread is resolved

            // In-place dependency-aware edit: change the part, rebuild, report what (if anything) broke.
            if (act.Action == "edit")
            {
                if (!isPart)
                { Send(new { type = "error", message = "That change edits a part - open the part you want to change." }); return; }
                if (act.EditChanges == null || act.EditChanges.Count == 0)
                { SendUnmappedSuggestion(); return; }   // dead-end: the router named "edit" but produced no change
                _lastRelayedClarify = null;
                Send(new { type = "status", message = "Applying your change on a safe copyâ€¦" });
                OpCounter.Increment();
                var swE = System.Diagnostics.Stopwatch.StartNew();
                var guardE = OriginalGuard.Capture(SafeIO.GetOriginalPaths(SwAddin.SwApp, model));
                EditResult er = VariantGenerator.ApplyEdit(SwAddin.SwApp, model, act.EditChanges, act.Force);

                // Tripwire: prove the original is byte-for-byte unchanged before reporting anything.
                if (!guardE.AllIntact())
                { Telemetry.Log("guardrail_violation", success: false, swVersion: SwVer()); Send(new { type = "error", message = "Forge stopped: a safety check flagged a change to an original. Your files are untouched â€” rephrase with a smaller change and resend." }); return; }

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
                _lastRelayedClarify = null;
                OpCounter.Increment();
                HighlightFeatures(model, act.Affected);
                Telemetry.Log("whatbreaks_query", success: true, featuresCount: act.Affected != null ? act.Affected.Count : 0, swVersion: SwVer(),
                    tokensIn: act.Meter.TokensIn, tokensOut: act.Meter.TokensOut, modelName: act.Meter.ModelName, llmCalls: act.Meter.LlmCalls, cacheHits: act.Meter.CacheHits, estCostUsd: act.Meter.EstCostUsd, opsUsed: OpCounter.Count,
                    questionType: act.QuestionType, questionSummary: act.QuestionSummary);
                Send(new { type = "impact", explanation = act.Explanation, affected = act.Affected });
                return;
            }

            // A direct, precise answer to a question (value/count/yes-no/short info). Crisp, not a paragraph.
            if (act.Action == "answer")
            {
                if (IsCloudDeadEnd(act.Answer)) { SendUnmappedSuggestion(); return; }   // Labtec #1: never relay a dead-end "answer"
                _lastRelayedClarify = null;
                OpCounter.Increment();
                HighlightFeatures(model, act.Affected);
                Telemetry.Log("answer_query", success: true, swVersion: SwVer(),
                    tokensIn: act.Meter.TokensIn, tokensOut: act.Meter.TokensOut, modelName: act.Meter.ModelName, llmCalls: act.Meter.LlmCalls, cacheHits: act.Meter.CacheHits, estCostUsd: act.Meter.EstCostUsd, opsUsed: OpCounter.Count,
                    questionType: act.QuestionType, questionSummary: act.QuestionSummary);
                Send(new { type = "answer", answer = act.Answer, affected = act.Affected });
                return;
            }

            // Otherwise it's a variant-generation request.
            VariantPlan plan = act.Variant;
            if (!isPart)
            { Send(new { type = "error", message = "Variants are part changes - open the part first." }); return; }
            if (plan == null || plan.Changes == null || plan.Changes.Count == 0 || plan.Count <= 0)
            { SendUnmappedSuggestion(); return; }   // dead-end: no dimensions mapped to a real variant change
            _lastRelayedClarify = null;

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
            { Telemetry.Log("guardrail_violation", success: false, swVersion: SwVer()); Send(new { type = "error", message = "Forge stopped: a safety check flagged a change to an original. Your files are untouched â€” rephrase with a smaller change and resend." }); return; }

            int cleanV = summary.Variants.FindAll(v => v.Success).Count;
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

        // OS taskbar icon for approval/done/teammate toasts. Reuses forge.bmp (shipped next to
        // the DLL) converted to an Icon once, SystemIcons.Information when the bitmap is missing.
        // Never throws: toasts are best-effort and must never break the panel or a run.
        private void InitNotify()
        {
            try
            {
                var n = new NotifyIcon();
                try
                {
                    string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                    string bmpPath = Path.Combine(dir ?? "", "forge.bmp");
                        if (File.Exists(bmpPath))
                        {
                            using (var bmp = new System.Drawing.Bitmap(bmpPath))
                            {
                                IntPtr h = bmp.GetHicon();
                                n.Icon = System.Drawing.Icon.FromHandle(h);
                                _notifyHicon = h; // FromHandle never takes ownership: Shutdown must DestroyIcon it
                            }
                        }
                }
                catch { }
                if (n.Icon == null) { try { n.Icon = System.Drawing.SystemIcons.Information; } catch { } }
                try { n.Text = "Forge"; } catch { }
                n.Visible = true;
                try { n.BalloonTipClicked += (s, e) => { try { _web.Focus(); } catch { } }; } catch { }
                _notify = n;
                try { Disposed += (s, e) => { try { Shutdown(); } catch { } }; } catch { }
            }
            catch { }
        }

        // Task-pane teardown (SwAddin.DisconnectFromSW calls this, then Dispose, before
        // nulling the panel): the Disposed event alone never fired there, leaving a ghost
        // tray icon plus a leaked HICON (Icon.FromHandle never owns its handle, so the
        // wrapper's Dispose can never free it). Hide + dispose the NotifyIcon and destroy
        // the owned HICON; the shared SystemIcons fallback holds no owned handle (Zero)
        // and is never destroyed. Idempotent, never throws.
        public void Shutdown()
        {
            try
            {
                var old = _notify; _notify = null;
                IntPtr h = _notifyHicon; _notifyHicon = IntPtr.Zero;
                try { if (old != null) { old.Visible = false; old.Dispose(); } } catch { }
                try { if (h != IntPtr.Zero) DestroyIcon(h); } catch { }
            }
            catch { }
        }

        // OS desktop toast: approval arrival, run completion, teammate updates. Guards the
        // config switch, clamps to tray limits (titles <= 63 chars, text <= 255 chars, no
        // newlines), marshals to the UI thread, 10s timeout. Never throws.
        private void NotifyToast(string title, string text)
        {
            try
            {
                if (!NotifyEnabled()) return;
                var n = _notify;
                if (n == null) return;
                string t = (title ?? "Forge").Replace("\r", " ").Replace("\n", " ");
                string b = (text ?? "").Replace("\r", " ").Replace("\n", " ");
                if (t.Length > 63) t = t.Substring(0, 63);
                if (b.Length > 255) b = b.Substring(0, 255);
                if (string.IsNullOrWhiteSpace(b)) return;
                try
                {
                    if (InvokeRequired)
                        BeginInvoke(new Action(() => { try { n.ShowBalloonTip(10000, t, b, ToolTipIcon.Info); } catch { } }));
                    else
                        n.ShowBalloonTip(10000, t, b, ToolTipIcon.Info);
                }
                catch { /* control torn down mid-toast */ }
            }
            catch { }
        }

        private string QueueActiveSid()
        {
            if (!string.IsNullOrWhiteSpace(_lastRunSid)) return SafeSidLocal(_lastRunSid);
            try { string s = CurrentChatSid(); if (!string.IsNullOrWhiteSpace(s)) return SafeSidLocal(s); } catch { }
            return "default";
        }

        private static string SafeSidLocal(string sid)
        {
            string s = (sid ?? "").Trim();
            if (s.Length == 0) return "default";
            try { s = Path.GetFileName(s); } catch { }
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c.ToString(), "_");
            if (s.Length == 0 || s == "." || s == "..") return "default";
            return s;
        }

        private static string AgentSessionPath(string sid)
        {
            return Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "Forge", "agent", SafeSidLocal(sid) + ".json");
        }

        private static void AtomicWriteJson(string path, JToken token)
        {
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllText(tmp, token.ToString(Newtonsoft.Json.Formatting.None));
                    if (File.Exists(path)) File.Replace(tmp, path, null);
                    else File.Move(tmp, path);
                }
                catch
                {
                    try { File.Delete(tmp); } catch { }
                    throw;
                }
            }
            catch { }
        }

        private SidQueue QueueFor(string sid)
        {
            string key = SafeSidLocal(sid);
            SidQueue q;
            if (Queues.TryGetValue(key, out q)) return q;
            q = new SidQueue();
            try
            {
                string p = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "Forge", "agent", key + ".queue.json");
                if (File.Exists(p))
                {
                    var o = JObject.Parse(File.ReadAllText(p));
                    try { q.paused = (bool)o["paused"]; } catch { }
                    var arr = o["items"] as JArray;
                    if (arr != null) foreach (var t in arr)
                    {
                        var it = t as JObject;
                        if (it == null) continue;
                        var qi = new QueuedFollowUp
                        {
                            id = (string)it["id"],
                            intent = (string)it["intent"],
                            paths = (string)it["paths"],
                            createdAt = (long?)it["createdAt"] ?? 0
                        };
                        if (!string.IsNullOrWhiteSpace(qi.id) && !string.IsNullOrWhiteSpace(qi.intent)) q.items.Add(qi);
                        if (q.items.Count >= 50) break;
                    }
                }
            }
            catch { }
            Queues[key] = q;
            return q;
        }

        private void PersistQueue(string sid)
        {
            try
            {
                string key = SafeSidLocal(sid);
                SidQueue q;
                lock (QueueLock) { if (!Queues.TryGetValue(key, out q)) return; }
                var arr = new JArray();
                foreach (var it in q.items)
                    arr.Add(new JObject { ["id"] = it.id, ["intent"] = it.intent, ["paths"] = it.paths, ["createdAt"] = it.createdAt });
                string p = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "Forge", "agent", key + ".queue.json");
                AtomicWriteJson(p, new JObject { ["paused"] = q.paused, ["items"] = arr });
            }
            catch { }
        }

        private void QueueAdd(string sid, string intent, JArray paths)
        {
            string ps = null;
            try { if (paths != null) ps = paths.ToString(Newtonsoft.Json.Formatting.None); } catch { }
            lock (QueueLock)
            {
                SidQueue q = QueueFor(sid);
                if (q.items.Count >= 50)
                { Send(new { type = "error", message = "Follow-up queue is full (50) — remove an item before adding another." }); return; }
                q.items.Add(new QueuedFollowUp
                {
                    id = Guid.NewGuid().ToString("N").Substring(0, 12),
                    intent = intent,
                    paths = ps,
                    createdAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                });
                PersistQueue(sid);
            }
            SendQueued(sid);
        }

        private string QueueSidForItem(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            lock (QueueLock)
            {
                foreach (var kv in Queues)
                {
                    foreach (var it in kv.Value.items)
                        if (string.Equals(it.id, id, StringComparison.OrdinalIgnoreCase)) return kv.Key;
                }
            }
            string key = SafeSidLocal(QueueActiveSid());
            string p = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "Forge", "agent", key + ".queue.json");
            try
            {
                if (File.Exists(p))
                {
                    var o = JObject.Parse(File.ReadAllText(p));
                    var arr = o["items"] as JArray;
                    if (arr != null) foreach (var t in arr)
                    {
                        var jo = t as JObject;
                        if (jo != null && string.Equals((string)jo["id"], id, StringComparison.OrdinalIgnoreCase)) return key;
                    }
                }
            }
            catch { }
            return null;
        }

        private void QueueEdit(string sid, string id, string intent)
        {
            lock (QueueLock)
            {
                SidQueue q = QueueFor(sid);
                foreach (var it in q.items)
                    if (string.Equals(it.id, id, StringComparison.OrdinalIgnoreCase)) { it.intent = intent; break; }
                PersistQueue(sid);
            }
            SendQueued(sid);
        }

        private void QueueRemove(string sid, string id)
        {
            lock (QueueLock)
            {
                SidQueue q = QueueFor(sid);
                for (int i = 0; i < q.items.Count; i++)
                    if (string.Equals(q.items[i].id, id, StringComparison.OrdinalIgnoreCase)) { q.items.RemoveAt(i); break; }
                PersistQueue(sid);
            }
            SendQueued(sid);
        }

        private void QueueMove(string sid, string id, int index)
        {
            lock (QueueLock)
            {
                SidQueue q = QueueFor(sid);
                int from = -1;
                for (int i = 0; i < q.items.Count; i++)
                    if (string.Equals(q.items[i].id, id, StringComparison.OrdinalIgnoreCase)) { from = i; break; }
                if (from >= 0)
                {
                    var item = q.items[from];
                    q.items.RemoveAt(from);
                    if (index > q.items.Count) index = q.items.Count;
                    if (index < 0) index = 0;
                    q.items.Insert(index, item);
                }
                PersistQueue(sid);
            }
            SendQueued(sid);
        }

        private void SendQueued(string sid)
        {
            string key = SafeSidLocal(sid);
            var items = new JArray();
            bool paused = false;
            lock (QueueLock)
            {
                SidQueue q;
                if (Queues.TryGetValue(key, out q))
                {
                    paused = q.paused;
                    foreach (var it in q.items)
                        items.Add(new JObject { ["id"] = it.id, ["intent"] = it.intent, ["createdAt"] = it.createdAt });
                }
            }
            Send(new { type = "queued", sid = key, items, paused });
        }

        private void SendQueueState(string sid)
        {
            string key = SafeSidLocal(sid);
            bool paused = false;
            lock (QueueLock)
            {
                SidQueue q;
                if (Queues.TryGetValue(key, out q)) paused = q.paused;
            }
            Send(new { type = "queueState", sid = key, paused });
        }

        private string LastIntent(string sid)
        {
            string key = SafeSidLocal(sid);
            string intent = null;
            lock (LastIntentBySid) { LastIntentBySid.TryGetValue(key, out intent); }
            if (!string.IsNullOrWhiteSpace(intent)) return intent;
            return ReadLastUserIntent(key);
        }

        private static string ReadLastUserIntent(string sid)
        {
            try
            {
                string p = AgentSessionPath(sid);
                if (!File.Exists(p)) return null;
                var arr = JArray.Parse(File.ReadAllText(p));
                for (int i = arr.Count - 1; i >= 0; i--)
                {
                    var o = arr[i] as JObject;
                    if (o == null || !string.Equals((string)o["role"], "user", StringComparison.OrdinalIgnoreCase)) continue;
                    string c = (string)o["content"] ?? "";
                    if (c.Length > 4000) c = c.Substring(c.Length - 4000);
                    return c;
                }
            }
            catch { }
            return null;
        }

        private static void TryStripLastUserTurn(string sid)
        {
            bool hasApi = false;
            try { hasApi = typeof(McpBuild).GetMethod("StripLastUserTurn", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static) != null; } catch { }
            if (hasApi) { try { McpBuild.StripLastUserTurn(sid); } catch { } return; }
            try
            {
                McpBuild.EnsureSessionPublic(sid);
                var t = typeof(McpBuild);
                const BindingFlags bf = BindingFlags.NonPublic | BindingFlags.Static;
                var fSessions = t.GetField("Sessions", bf);
                var mSafe = t.GetMethod("SafeSid", bf);
                var mSave = t.GetMethod("SaveSession", bf);
                if (fSessions == null || mSafe == null || mSave == null) return;
                object sessions = fSessions.GetValue(null);
                if (sessions == null) return;
                string key = mSafe.Invoke(null, new object[] { sid }) as string;
                if (string.IsNullOrEmpty(key)) key = "default";
                object sessionObj = null;
                lock (sessions)
                {
                    var dict = sessions as System.Collections.IDictionary;
                    if (dict != null) sessionObj = dict[key];
                }
                if (sessionObj == null) return;
                var fMsgs = sessionObj.GetType().GetField("Msgs", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var fTurns = sessionObj.GetType().GetField("TurnStarts", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (fMsgs == null || fTurns == null) return;
                var msgs = fMsgs.GetValue(sessionObj) as System.Collections.IList;
                var turns = fTurns.GetValue(sessionObj) as System.Collections.IList;
                if (msgs == null || turns == null) return;
                lock (msgs)
                {
                    int last = -1;
                    for (int i = msgs.Count - 1; i >= 0; i--)
                    {
                        var jo = msgs[i] as JObject;
                        if (jo != null && string.Equals((string)jo["role"], "user", StringComparison.OrdinalIgnoreCase)) { last = i; break; }
                    }
                    if (last < 0) return;
                    msgs.RemoveAt(last);
                    for (int i = turns.Count - 1; i >= 0; i--)
                    {
                        try { if (Convert.ToInt32(turns[i]) == last) { turns.RemoveAt(i); break; } } catch { }
                    }
                }
                try { mSave.Invoke(null, new object[] { sid }); } catch { }
            }
            catch { }
        }

        private async Task RunGenerateTracked(string intent, string sid, JArray paths, string queueId, bool force, bool continued, bool autoDispatch, bool dispatchOwner = false)
        {
            if (string.IsNullOrWhiteSpace(sid)) sid = QueueActiveSid();
            sid = SafeSidLocal(sid);
            lock (LastIntentBySid) { LastIntentBySid[sid] = intent ?? ""; }
            _lastRunSid = sid;
            _activeRunSid = sid;
            if (continued) lock (InterruptedBySid) { InterruptedBySid[sid] = false; }
            Func<string> priorSidProvider = null;
            string priorMcpBuildSid = null;
            try
            {
                priorSidProvider = OpenAiCompatibleClient.SessionIdProvider;
                string capturedSid = sid;
                OpenAiCompatibleClient.SessionIdProvider = () => capturedSid;
            }
            catch { }
            try { priorMcpBuildSid = GetMcpBuildSessionId(); } catch { }
            try { SetMcpBuildSessionId(sid); } catch { }
            System.Threading.Interlocked.Increment(ref _activeRuns);
            try { await Generate(intent, sid, paths); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Send(new { type = "error", message = "Run failed (" + BoundDetail(ex.Message, 300) + ") — check the model for partial changes." }); }
            finally
            {
                try { if (priorSidProvider != null) OpenAiCompatibleClient.SessionIdProvider = priorSidProvider; } catch { }
                try { SetMcpBuildSessionId(priorMcpBuildSid); } catch { }
                if (string.Equals(_activeRunSid, sid, StringComparison.OrdinalIgnoreCase)) _activeRunSid = null;
                System.Threading.Interlocked.Decrement(ref _activeRuns);
                if (dispatchOwner) lock (QueueLock) { Dispatching.Remove(sid); }
                try { SendQueued(sid); } catch { }
                if (autoDispatch && !force)
                { try { var _ = DispatchNextQueued(sid); } catch { } }
            }
        }

        private async Task DispatchNextQueued(string sid)
        {
            string key = SafeSidLocal(sid);
            try
            {
                QueuedFollowUp next = null;
                if (System.Threading.Volatile.Read(ref _activeRuns) > 0) return;
                lock (QueueLock)
                {
                    SidQueue q;
                    if (!Queues.TryGetValue(key, out q) || q.paused || q.items.Count == 0) return;
                    bool dispatching;
                    Dispatching.TryGetValue(key, out dispatching);
                    if (dispatching) return;
                    Dispatching[key] = true;
                    next = q.items[0];
                    q.items.RemoveAt(0);
                    PersistQueue(key);
                }
                SendQueued(key);
                JArray paths = null;
                try { if (!string.IsNullOrWhiteSpace(next.paths)) paths = JArray.Parse(next.paths); } catch { }
                await RunGenerateTracked(next.intent, key, paths, next.id, false, false, true, true);
            }
            finally
            {
                lock (QueueLock) { Dispatching.Remove(key); }
            }
        }

        private string ForkSession(string sourceSid, int? turnIndex)
        {
            string src = AgentSessionPath(sourceSid);
            var arr = new JArray();
            if (File.Exists(src)) arr = JArray.Parse(File.ReadAllText(src));
            if (turnIndex.HasValue)
            {
                int seen = -1, cut = arr.Count;
                for (int i = 0; i < arr.Count; i++)
                {
                    var o = arr[i] as JObject;
                    if (o == null || !string.Equals((string)o["role"], "user", StringComparison.OrdinalIgnoreCase)) continue;
                    seen++;
                    if (seen == turnIndex.Value) { cut = i; break; }
                }
                while (arr.Count > cut) arr.RemoveAt(arr.Count - 1);
            }
            string dir = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "Forge", "agent");
            string newSid, dst;
            do
            {
                newSid = "fork-" + Guid.NewGuid().ToString("N").Substring(0, 12);
                dst = Path.Combine(dir, newSid + ".json");
            }
            while (File.Exists(dst));
            AtomicWriteJson(dst, arr);
            try
            {
                string sctx = src.Substring(0, src.Length - ".json".Length) + ".ctx.json";
                if (File.Exists(sctx)) File.Copy(sctx, dst.Substring(0, dst.Length - ".json".Length) + ".ctx.json", true);
            }
            catch { }
            McpBuild.EnsureSessionPublic(newSid);
            return newSid;
        }

        private void DoShareTranscript(string sid)
        {
            string md = BuildMarkdownTranscript(sid);
            try
            {
                using (var dlg = new SaveFileDialog())
                {
                    dlg.Title = "Save Forge conversation";
                    dlg.Filter = "Markdown (*.md)|*.md|All files (*.*)|*.*";
                    dlg.FileName = "forge-session-" + SafeSidLocal(sid) + ".md";
                    if (dlg.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(dlg.FileName))
                    { Send(new { type = "shared", path = (string)null, cancelled = true }); return; }
                    File.WriteAllText(dlg.FileName, md);
                    Send(new { type = "shared", path = dlg.FileName });
                }
            }
            catch (Exception ex) { Send(new { type = "error", message = "Couldn't save the transcript (" + BoundDetail(ex.Message, 200) + ") — try a different folder." }); }
        }

        private static string BuildMarkdownTranscript(string sid)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("# Forge conversation — ").Append(SafeSidLocal(sid)).Append("\n\n");
            try
            {
                string p = AgentSessionPath(sid);
                if (!File.Exists(p)) { sb.Append("(no messages)\n"); return sb.ToString(); }
                var arr = JArray.Parse(File.ReadAllText(p));
                var starts = new List<int>();
                for (int i = 0; i < arr.Count; i++)
                {
                    var o = arr[i] as JObject;
                    if (o != null && string.Equals((string)o["role"], "user", StringComparison.OrdinalIgnoreCase)) starts.Add(i);
                }
                for (int k = 0; k < starts.Count; k++)
                {
                    int start = starts[k];
                    int end = k + 1 < starts.Count ? starts[k + 1] : arr.Count;
                    var q = arr[start] as JObject;
                    string qc = q != null ? ((string)q["content"] ?? "") : "";
                    sb.Append("## Turn ").Append(k + 1).Append("\n\n**You:** ").Append(qc).Append("\n\n");
                    for (int i = start + 1; i < end; i++)
                    {
                        var o = arr[i] as JObject;
                        if (o == null) continue;
                        string role = (string)o["role"] ?? "";
                        string c = (string)o["content"] ?? "";
                        if (string.IsNullOrWhiteSpace(c)) continue;
                        sb.Append("**").Append(role).Append(":** ").Append(c).Append("\n\n");
                    }
                }
            }
            catch (Exception ex) { sb.Append("\n(transcript read failed: ").Append(BoundDetail(ex.Message, 200)).Append(")\n"); }
            return sb.ToString();
        }

        private static JArray DiffLastAnswers(string sid)
        {
            var lines = new JArray();
            try
            {
                string p = AgentSessionPath(sid);
                if (!File.Exists(p)) return lines;
                var arr = JArray.Parse(File.ReadAllText(p));
                var answers = new List<string>();
                foreach (var t in arr)
                {
                    var o = t as JObject;
                    if (o == null || !string.Equals((string)o["role"], "assistant", StringComparison.OrdinalIgnoreCase)) continue;
                    string c = (string)o["content"] ?? "";
                    if (!string.IsNullOrWhiteSpace(c)) answers.Add(c);
                }
                if (answers.Count == 0) return lines;
                string newer = answers[answers.Count - 1];
                string older = answers.Count > 1 ? answers[answers.Count - 2] : "";
                string[] aAll = older.Replace("\r\n", "\n").Split('\n');
                int n = Math.Min(aAll.Length, 200);
                string[] a = new string[n];
                Array.Copy(aAll, a, n);
                string[] bAll = newer.Replace("\r\n", "\n").Split('\n');
                int m = Math.Min(bAll.Length, 200);
                string[] b = new string[m];
                Array.Copy(bAll, b, m);
                int[,] dp = new int[n + 1, m + 1];
                for (int i = n - 1; i >= 0; i--)
                    for (int j = m - 1; j >= 0; j--)
                        dp[i, j] = string.Equals(a[i], b[j], StringComparison.Ordinal) ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);
                int x = 0, y = 0;
                while (x < n && y < m)
                {
                    if (string.Equals(a[x], b[y], StringComparison.Ordinal))
                    { lines.Add(new JObject { ["type"] = "same", ["text"] = a[x] }); x++; y++; }
                    else if (dp[x + 1, y] >= dp[x, y + 1])
                    { lines.Add(new JObject { ["type"] = "del", ["text"] = a[x] }); x++; }
                    else
                    { lines.Add(new JObject { ["type"] = "add", ["text"] = b[y] }); y++; }
                }
                while (x < n) { lines.Add(new JObject { ["type"] = "del", ["text"] = a[x] }); x++; }
                while (y < m) { lines.Add(new JObject { ["type"] = "add", ["text"] = b[y] }); y++; }
            }
            catch { }
            return lines;
        }

        private static JArray BuildTimeline(string sid)
        {
            var events = new JArray();
            try
            {
                string p = AgentSessionPath(sid);
                if (!File.Exists(p)) return events;
                var arr = JArray.Parse(File.ReadAllText(p));
                string at = File.GetLastWriteTimeUtc(p).ToString("o");
                foreach (var t in arr)
                {
                    var o = t as JObject;
                    if (o == null) continue;
                    string role = (string)o["role"] ?? "";
                    string content = (string)o["content"] ?? "";
                    var tc = o["tool_calls"] as JArray;
                    if (string.Equals(role, "user", StringComparison.OrdinalIgnoreCase))
                        events.Add(new JObject { ["kind"] = "turn", ["label"] = content.Length > 160 ? content.Substring(0, 160) + "…" : content, ["at"] = at });
                    else if (string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase) && tc != null && tc.Count > 0)
                    {
                        var names = new List<string>();
                        foreach (var c in tc)
                        {
                            var co = c as JObject;
                            var fn = co != null ? co["function"] as JObject : null;
                            string nm = fn != null ? (string)fn["name"] : null;
                            if (!string.IsNullOrWhiteSpace(nm)) names.Add(nm.Trim());
                        }
                        events.Add(new JObject { ["kind"] = "tool", ["label"] = string.Join(", ", names.ToArray()), ["at"] = at });
                    }
                    else if (string.Equals(role, "tool", StringComparison.OrdinalIgnoreCase))
                        events.Add(new JObject { ["kind"] = (content ?? "").StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase) ? "error" : "tool", ["label"] = content.Length > 160 ? content.Substring(0, 160) + "…" : content, ["at"] = at });
                    else if (string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(content))
                        events.Add(new JObject { ["kind"] = "turn", ["label"] = content.Length > 160 ? content.Substring(0, 160) + "…" : content, ["at"] = at });
                }
            }
            catch { }
            return events;
        }

        private static void ApplyWorkStyle(string style)
        {
            if (string.Equals(style, "review-first", StringComparison.OrdinalIgnoreCase))
            {
                McpBuild.ApproveMaster = false;
                McpBuild.ApproveRead = true;
                McpBuild.ApproveBuild = true;
                McpBuild.ApproveDestructive = false;
                return;
            }
            McpBuild.ApproveMaster = true;
            McpBuild.ApproveRead = true;
            McpBuild.ApproveBuild = true;
            McpBuild.ApproveDestructive = false;
        }

        private static int DefaultMaxOutput()
        {
            try
            {
                var cfg = ReadForgeConfig();
                string slug = PresetSlugFor((string)cfg["provider"], (string)cfg["baseUrl"]);
                int maxOut = ProviderFactory.MaxOutputTokensFor(slug, (string)cfg["model"], 0);
                if (maxOut > 0) return maxOut;
            }
            catch { }
            return 16000;
        }

        private void LoadHostPreferences()
        {
            try
            {
                var cfg = ReadForgeConfig();
                string ws = (string)cfg["workStyle"];
                if (!string.IsNullOrWhiteSpace(ws)) ApplyWorkStyle(ws.Trim());
                int pct = 60, reserve = Math.Min(20000, DefaultMaxOutput()), tail = 2;
                try { pct = Convert.ToInt32((object)cfg["compactThresholdPct"]); } catch { }
                try { reserve = Convert.ToInt32((object)cfg["compactReservedTokens"]); } catch { }
                try { tail = Convert.ToInt32((object)cfg["compactTailTurns"]); } catch { }
                if (pct < 1) pct = 1; if (pct > 100) pct = 100;
                if (reserve < 0) reserve = 0;
                if (tail < 0) tail = 0; if (tail > 10) tail = 10;
                bool compactOn = true;
                try { var cv = cfg["compactOn"]; if (cv != null) compactOn = (bool)cv; } catch { }
                try { McpBuild.ConfigureCompaction(compactOn, pct, reserve, tail); } catch { }
                CompactReservedTokens = reserve;
                CompactTailTurns = tail;
            }
            catch { }
        }

        private static string ModelKey(string model)
        {
            string k = SanitizeCustomSlug(model);
            return string.IsNullOrWhiteSpace(k) ? "default" : k;
        }

        private void SendProviderSnapshot()
        {
            try
            {
                var cfg = ReadForgeConfig();
                string p = (string)cfg["provider"], bu = (string)cfg["baseUrl"];
                string slug = PresetSlugFor(p, bu);
                Send(new { type = "provider", slug, provider = p, baseUrl = bu, displayName = ProviderFactory.DisplayNameFor(p, bu), model = (string)cfg["model"], dynamic = IsDynamicProvider(p), hasKey = !string.IsNullOrWhiteSpace(StoredProviderKeyForSlug(slug, p)), customModels = GetCustomModels(slug), customProviders = GetCustomProviders() });
            }
            catch { }
        }

        private static string ExpandEnvTokens(string value, List<string> missing)
        {
            if (string.IsNullOrEmpty(value)) return value;
            string s = value;
            try
            {
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\{env:([A-Za-z_][A-Za-z0-9_]*)\}", m => EnvToken(m.Groups[1].Value, m.Value, missing));
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\$\{([A-Za-z_][A-Za-z0-9_]*)\}", m => EnvToken(m.Groups[1].Value, m.Value, missing));
                s = System.Text.RegularExpressions.Regex.Replace(s, @"%([A-Za-z_][A-Za-z0-9_]*)%", m => EnvToken(m.Groups[1].Value, m.Value, missing));
            }
            catch { }
            return s;
        }

        private static string EnvToken(string name, string original, List<string> missing)
        {
            try
            {
                string v = System.Environment.GetEnvironmentVariable(name);
                if (!string.IsNullOrEmpty(v)) return v;
            }
            catch { }
            if (missing != null && !missing.Contains(name)) missing.Add(name);
            return original;
        }

        private static JObject ParseProviderHeaders(dynamic msg)
        {
            JObject raw = null;
            try { raw = msg.headers as JObject; } catch { }
            if (raw == null || raw.Count == 0) return null;
            var clean = new JObject();
            foreach (var pr in raw.Properties())
            {
                string v = null;
                try { v = (string)pr.Value; } catch { }
                if (v == null) continue;
                clean[pr.Name] = v;
            }
            return clean.Count > 0 ? clean : null;
        }

        public static string CurrentProviderSlug()
        {
            try { var cfg = ReadForgeConfig(); return PresetSlugFor((string)cfg["provider"], (string)cfg["baseUrl"]); }
            catch { return null; }
        }

        private static JObject ExpandedProviderHeaders(string slug)
        {
            var result = new JObject();
            try
            {
                var p = FindCustomProvider(slug);
                var hh = p == null ? null : p["headers"] as JObject;
                if (hh == null) return null;
                var missing = new List<string>();
                foreach (var pr in hh.Properties())
                {
                    string v = null;
                    try { v = (string)pr.Value; } catch { }
                    if (v == null) continue;
                    result[pr.Name] = ExpandEnvTokens(v, missing);
                }
            }
            catch { }
            return result.Count > 0 ? result : null;
        }

        private static Dictionary<string, string> ExpandedHeaderDictionary(string slug)
        {
            var jo = ExpandedProviderHeaders(slug);
            if (jo == null) return null;
            try { return jo.ToObject<Dictionary<string, string>>(); } catch { return null; }
        }

        public static System.Collections.Generic.Dictionary<string, string> ExpandedProviderHeadersFor(string slug)
        {
            try
            {
                var d = ExpandedHeaderDictionary(slug);
                return (d != null && d.Count > 0) ? d : null;
            }
            catch { return null; }
        }

        private static string[] McpStringArray(JToken tok)
        {
            var arr = tok as JArray;
            var clean = new List<string>();
            if (arr == null) return clean.ToArray();
            foreach (var t in arr)
            {
                string s = null;
                try { s = (string)t; } catch { }
                if (!string.IsNullOrWhiteSpace(s) && clean.Count < 200) clean.Add(s.Trim());
            }
            return clean.ToArray();
        }

        private static bool IsFreeModel(string id, string slug)
        {
            string m = (id ?? "").Trim().ToLowerInvariant();
            if (m.Contains("-free") || m.Contains(":free") || m.Contains("free-")) return true;
            if (string.Equals(slug, "opencode", StringComparison.OrdinalIgnoreCase) && m.Contains("flash")) return true;
            return false;
        }

        private static bool IsByokSlug(string slug)
        {
            string s = (slug ?? "").Trim().ToLowerInvariant();
            if (s.Length == 0) return true;
            if (s == "opencode" || s == "ollama") return false;
            return true;
        }

        private static bool IsVisionModel(string id)
        {
            string m = (id ?? "").Trim().ToLowerInvariant();
            return m.Contains("vision") || m.Contains("gemini") || m.Contains("gpt-4o") || m.Contains("claude") || m.Contains("kimi");
        }

        private static JObject ModelMeta(string id, string slug)
        {
            bool free = IsFreeModel(id, slug), byok = IsByokSlug(slug);
            int window = 0, maxOut = 0;
            bool exact = false, reasoning = false, temperature = false, pricingUnknown = true;
            string label = id;
            JObject pricing = null;
            try
            {
                var info = ProviderFactory.ModelInfo(slug, id);
                if (info != null)
                {
                    label = (string)info["label"] ?? label;
                    free = (bool?)info["free"] ?? free;
                    byok = (bool?)info["byok"] ?? byok;
                    window = (int?)info["contextWindow"] ?? 0;
                    maxOut = (int?)info["maxOutput"] ?? 0;
                    exact = (bool?)info["contextWindowExact"] ?? false;
                    reasoning = ((bool?)info["reasoning"] ?? false) || ProviderFactory.IsReasoningModel(id);
                    temperature = (bool?)info["temperatureSupported"] ?? ProviderFactory.SupportsTemperature(id);
                    pricingUnknown = (bool?)info["pricingUnknown"] ?? true;
                    var p = info["pricing"] as JObject;
                    if (p != null) { pricing = (JObject)p.DeepClone(); pricingUnknown = false; }
                }
            }
            catch
            {
                try { var w = ProviderFactory.ContextWindowFor(id); window = w.Item1; exact = w.Item2; } catch { }
                try { reasoning = ProviderFactory.IsReasoningModel(id); } catch { }
                try { temperature = ProviderFactory.SupportsTemperature(id); } catch { }
            }
            if (pricing == null) pricingUnknown = true;
            var caps = new JArray();
            if (reasoning) caps.Add("reasoning");
            caps.Add("tools");
            if (temperature) caps.Add("temperature");
            if (IsVisionModel(id)) caps.Add("vision");
            if (free) caps.Add("free");
            return new JObject
            {
                ["id"] = id,
                ["label"] = label,
                ["free"] = free,
                ["byok"] = byok,
                ["pricing"] = pricing,
                ["pricingUnknown"] = pricingUnknown,
                ["contextWindow"] = window,
                ["contextWindowExact"] = exact,
                ["maxOutput"] = maxOut,
                ["capabilities"] = caps,
                ["reasoning"] = reasoning,
                ["temperatureSupported"] = temperature
            };
        }

        private static JArray ModelMetaArray(IEnumerable<string> ids, string slug)
        {
            var metas = new JArray();
            if (ids == null) return metas;
            foreach (var id in ids) metas.Add(ModelMeta(id ?? "", slug));
            return metas;
        }

        private static object WithModelMeta(object payload)
        {
            var joIn = payload as Newtonsoft.Json.Linq.JObject;
            string t = null;
            if (joIn != null)
            {
                try { t = (string)joIn["type"]; } catch { return payload; }
            }
            else
            {
                try
                {
                    var tp = payload.GetType().GetProperty("type", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    if (tp != null) t = tp.GetValue(payload, null) as string;
                }
                catch { return payload; }
            }
            if (t != "models" && t != "provider") return payload;
            Newtonsoft.Json.Linq.JObject jo = joIn;
            if (jo == null) { try { jo = Newtonsoft.Json.Linq.JObject.FromObject(payload); } catch { return payload; } }
            else { try { jo = (Newtonsoft.Json.Linq.JObject)jo.DeepClone(); } catch { return payload; } }
            try
            {
                var metas = new JArray();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (t == "models")
                {
                    var ids = jo["models"] as JArray;
                    string slug = null;
                    try { slug = (string)jo["slug"]; } catch { }
                    if (string.IsNullOrWhiteSpace(slug))
                    { try { var mcfg = ReadForgeConfig(); slug = PresetSlugFor((string)mcfg["provider"], (string)mcfg["baseUrl"]); } catch { } }
                    if (ids != null) foreach (var id in ids)
                    {
                        string s = null;
                        try { s = (string)id; } catch { }
                        if (string.IsNullOrWhiteSpace(s) || !seen.Add(s)) continue;
                        metas.Add(ModelMeta(s, slug));
                    }
                    if (jo["modelMeta"] == null) jo["modelMeta"] = metas;
                }
                else
                {
                    string slug = null, cur = null;
                    try { slug = (string)jo["slug"]; } catch { }
                    try { cur = (string)jo["model"]; } catch { }
                    if (!string.IsNullOrWhiteSpace(cur) && seen.Add(cur)) metas.Add(ModelMeta(cur, slug));
                    foreach (var known in KnownSlugs)
                    {
                        var preset = ProviderPresetFor(known);
                        if (preset == null || string.IsNullOrWhiteSpace(preset.Model)) continue;
                        if (seen.Add(preset.Model)) metas.Add(ModelMeta(preset.Model, known));
                    }
                    var customs = GetCustomModels(slug);
                    foreach (var c in customs)
                    {
                        string cid = c is JObject ? (string)((JObject)c)["id"] : null;
                        if (string.IsNullOrWhiteSpace(cid) || !seen.Add(cid)) continue;
                        metas.Add(ModelMeta(cid, slug));
                    }
                    jo["modelMeta"] = metas;
                    try { jo["reasoningEffortCapability"] = ProviderFactory.ReasoningEffortCapabilityTable(); } catch { }
                    try
                    {
                        var cfg = ReadForgeConfig();
                        jo["workStyle"] = (string)cfg["workStyle"] ?? "high-autonomy";
                        jo["reasoningEffort"] = (string)cfg["reasoningEffort"] ?? "medium";
                        try { jo["reasoningEffortSupported"] = ProviderFactory.SupportsReasoningEffort(slug); } catch { }
                        try { jo["compactThresholdPct"] = Convert.ToInt32((object)cfg["compactThresholdPct"]); } catch { jo["compactThresholdPct"] = McpBuild.CompactAt; }
                        jo["compactReservedTokens"] = CompactReservedTokens;
                        jo["compactTailTurns"] = CompactTailTurns;
                    }
                    catch { }
                }
            }
            catch { }
            return jo;
        }

        private void Send(object payload)
        {
            // Never throws: every caller runs on paths that end in async-void (OnWebMessage) or
            // fire-and-forget emits, where a torn-down WebView2 (ObjectDisposedException /
            // InvalidOperationException) would otherwise escape to SolidWorks and take it down.
            try
            {
                try { PanelCapture.Log("out", payload); } catch { }
                // Send as a STRING: the panel does JSON.parse(e.data). PostWebMessageAsJson would make
                // e.data an already-parsed object, so JSON.parse throws and every message is dropped
                // (that was the "stuck on On itâ€¦" bug â€” the panel never got the done/status messages).
                object wire = payload;
                try { wire = WithCtx(payload); } catch { wire = payload; }
                try { wire = WithModelMeta(wire); } catch { }
                string json = JsonConvert.SerializeObject(wire);
                var web = _web;
                if (web == null || web.IsDisposed) return;
                try
                {
                    if (web.InvokeRequired)
                        web.BeginInvoke(new Action(() => { try { web.CoreWebView2?.PostWebMessageAsString(json); } catch { } }));
                    else
                        web.CoreWebView2?.PostWebMessageAsString(json);
                }
                catch { /* control torn down mid-post */ }
            }
            catch { }
        }

        // Model/provider switch: the pill's % is cur/ContextWindowFor(currentModel) with
        // cur recorded under the OLD model, so a switch to a smaller window spikes the %
        // and can trip spurious auto-compact. Reset the current session's context totals
        // (McpBuild's own clear path: CtxCur/CtxOutSum/CtxTurns under its CtxLock, sidecar
        // re-saved) so the new window starts clean. History (Msgs) is untouched — minimum:
        // reset, no per-window snapshots. McpBuild exposes no per-sid ctx reset
        // (ClearHistory would also wipe every conversation), so touch the current-sid
        // entries under McpBuild's own lock; any reflection shape change degrades to a
        // no-op, never a throw.
        // Global lock order (McpBuild): Sessions -> CtxLock -> Msgs. Sequential only —
        // read the sid under Sessions, release, then take CtxLock. Never nest.
        private static void ResetCtxForModelSwitch()
        {
            try
            {
                var t = typeof(McpBuild);
                const BindingFlags bf = BindingFlags.NonPublic | BindingFlags.Static;
                var fLock = t.GetField("CtxLock", bf);
                var fCur = t.GetField("CtxCur", bf);
                var fOut = t.GetField("CtxOutSum", bf);
                var fTurns = t.GetField("CtxTurns", bf);
                var fSessions = t.GetField("Sessions", bf);
                var fCurSid = t.GetField("CurrentSid", bf);
                var mSafe = t.GetMethod("SafeSid", bf);
                var mSave = t.GetMethod("SaveCtx", bf);
                if (fLock == null || fCur == null || fOut == null || fTurns == null
                    || fSessions == null || fCurSid == null || mSafe == null || mSave == null) return;
                object sessions = fSessions.GetValue(null);
                object ctxLock = fLock.GetValue(null);
                if (sessions == null || ctxLock == null) return;
                string sid;
                lock (sessions) { sid = fCurSid.GetValue(null) as string; } // release before taking CtxLock
                string key = mSafe.Invoke(null, new object[] { sid }) as string;
                if (string.IsNullOrEmpty(key)) key = "default";
                lock (ctxLock)
                {
                    try
                    {
                        var cur = fCur.GetValue(null) as System.Collections.IDictionary;
                        var sum = fOut.GetValue(null) as System.Collections.IDictionary;
                        var turns = fTurns.GetValue(null) as System.Collections.IDictionary;
                        if (cur == null || sum == null || turns == null) return;
                        // Fresh empty per-turn list of McpBuild's private CtxTurn element type.
                        object emptyTurns;
                        try
                        {
                            var elem = t.GetNestedType("CtxTurn", BindingFlags.NonPublic);
                            if (elem == null) return;
                            emptyTurns = Activator.CreateInstance(typeof(List<>).MakeGenericType(elem));
                        }
                        catch { return; }
                        turns[key] = emptyTurns;
                        cur[key] = 0L;
                        sum[key] = 0L;
                    }
                    catch { return; }
                }
                try { mSave.Invoke(null, new object[] { sid }); } catch { } // re-persist the zeroed sidecar (locks internally)
            }
            catch { }
        }

        // Kilo parity: every answer/step-flavored message carries live context stats
        // (ctxUsed/ctxWindow/ctxTurns/ctxOut/ctxApprox/ctxModel) so the pill reflects
        // REAL session usage without an extra round-trip. One funnel, never throws.
        private static object WithCtx(object payload)
        {
            Newtonsoft.Json.Linq.JObject jo = payload as Newtonsoft.Json.Linq.JObject;
            if (jo == null) { try { jo = Newtonsoft.Json.Linq.JObject.FromObject(payload); } catch { return payload; } }
            else { try { jo = (Newtonsoft.Json.Linq.JObject)jo.DeepClone(); } catch { return payload; } }
            string t = null;
            try { t = (string)jo["type"]; } catch { return payload; }
            if (t != "answer" && t != "step" && t != "status" && t != "enhanced" && t != "compacted" && t != "historyLoaded") return payload;
            try
            {
                long cu, co; int ctn;
                McpBuild.CtxSnapshotCurrent(out cu, out co, out ctn);
                string m = null;
                try { m = Forge.Providers.ProviderFactory.CurrentModel(); } catch { m = null; }
                var w = Forge.Providers.ProviderFactory.ContextWindowFor(m);
                jo["ctxUsed"] = cu; jo["ctxOut"] = co; jo["ctxTurns"] = ctn;
                jo["ctxWindow"] = w.Item1; jo["ctxApprox"] = !w.Item2;
                if (!string.IsNullOrWhiteSpace(m)) jo["ctxModel"] = m;
            }
            catch { }
            return jo;
        }

        // Top-bar context dropdown: per-session context stats (+ legacy daily totals).
        private void SendUsage()
        {
            try
            {
                long cu, co; int ctn;
                McpBuild.CtxSnapshotCurrent(out cu, out co, out ctn);
                string m = null;
                try { m = Forge.Providers.ProviderFactory.CurrentModel(); } catch { m = null; }
                var w = Forge.Providers.ProviderFactory.ContextWindowFor(m);
                Send(new { type = "usage", tokensIn = IntentRate.TokensInToday, tokensOut = IntentRate.TokensOutToday, agentMsgs = McpBuild.HistoryCount(), agentTokens = McpBuild.AgentTokCount, ctxUsed = cu, ctxOut = co, ctxTurns = ctn, ctxWindow = w.Item1, ctxApprox = !w.Item2, ctxModel = m });
            }
            catch (Exception ex) { Send(new { type = "usage", error = "Couldn't load usage — try again. (detail: " + BoundDetail(ex.Message, 200) + ")" }); }
        }
    }
}
