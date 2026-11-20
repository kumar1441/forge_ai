using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Forge.Providers;
using Newtonsoft.Json.Linq;

namespace Forge.SolidWorks
{
    /// <summary>
    /// McpBuild — the chat path for MULTI-PART / assembly from-scratch builds ("gearbox with 3 gears",
    /// "bracket and a bolt", anything the deterministic 2-step can't split). Two modes:
    /// AGENT LOOP (openai-compatible providers): the model gets the live tool list as real function
    /// calls plus the running conversation history, then plans -> acts -> observes -> re-plans until the
    /// build is done — true agency across turns, not one-shot. SINGLE-SHOT fallback (other providers /
    /// tool-call failure): one JSON plan executed sequentially, as before.
    /// </summary>
    public static class McpBuild
    {
        // Agent conversation store, Kilo-style: per-chat history (system + user/assistant/tool turns)
        // PERSISTED to %APPDATA%\Forge\agent\{sid}.json on every mutation, loaded on restore, truncated
        // on revert, summarized (never silently dropped) past 60 messages. The loop re-sends it every
        // iteration, so the model observes what it just did — agency instead of one-shot.
        private class AgentSession
        {
            public readonly List<JObject> Msgs = new List<JObject>();
            public readonly List<int> TurnStarts = new List<int>(); // Msgs indices where user turns begin
        }

        private static readonly Dictionary<string, AgentSession> Sessions = new Dictionary<string, AgentSession>();
        private static string CurrentSid;
        public static int AgentMsgCount { get; private set; }
        private static long _agentTokCount;
        public static long AgentTokCount { get { return System.Threading.Interlocked.Read(ref _agentTokCount); } private set { System.Threading.Interlocked.Exchange(ref _agentTokCount, value); } }

        // Auto-approval policy (Kilo parity): master switch + per-category toggles + an always-allow
        // list persisted to disk. Read-only tools never mutate the model, so they always run;
        // destructive tools always ask unless explicitly allowed.
        static McpBuild()
        {
            try { LoadApprovals(); } catch { }
            try { LoadDenials(); } catch { }
        }
        public static bool ApproveMaster = true;
        public static bool ApproveRead = true;
        public static bool ApproveBuild = true;
        public static bool ApproveDestructive = false;
        private static readonly HashSet<string> AlwaysAllow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> Deny = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, System.Threading.Tasks.TaskCompletionSource<string>> PendingApprovals =
            new Dictionary<string, System.Threading.Tasks.TaskCompletionSource<string>>();

        /// <summary>Set by the pipeline per run so the gate can post approval cards to the panel.</summary>
        public static Action<JObject> CardSender;

        private static string ApprovalsPath()
        {
            return Path.Combine(AgentDir(), "..", "approvals.json");
        }

        public static void LoadApprovals()
        {
            try
            {
                string p = ApprovalsPath();
                if (!File.Exists(p)) return;
                var arr = JArray.Parse(File.ReadAllText(p));
                lock (AlwaysAllow) { foreach (var t in arr) { string s = (string)t; if (!string.IsNullOrWhiteSpace(s)) AlwaysAllow.Add(s.Trim()); } }
            }
            catch { }
        }

        public static void AddAlwaysAllow(string tool)
        {
            if (string.IsNullOrWhiteSpace(tool)) return;
            lock (AlwaysAllow) { AlwaysAllow.Add(tool.Trim()); }
            lock (Deny) { Deny.Remove(tool.Trim()); }
            SaveApprovals();
        }

        public static void Unallow(string tool)
        {
            if (string.IsNullOrWhiteSpace(tool)) return;
            lock (AlwaysAllow) { AlwaysAllow.Remove(tool.Trim()); }
            SaveApprovals();
        }

        public static System.Collections.Generic.List<string> AlwaysList()
        {
            lock (AlwaysAllow) { return new System.Collections.Generic.List<string>(AlwaysAllow); }
        }

        public static System.Collections.Generic.List<string> DenyList()
        {
            lock (Deny) { return new System.Collections.Generic.List<string>(Deny); }
        }

        private static void SaveApprovals()
        {
            try
            {
                var arr = new JArray();
                lock (AlwaysAllow) { foreach (var s in AlwaysAllow) arr.Add(s); }
                File.WriteAllText(ApprovalsPath(), arr.ToString(Newtonsoft.Json.Formatting.None));
                var arrD = new JArray();
                lock (Deny) { foreach (var s in Deny) arrD.Add(s); }
                File.WriteAllText(DenyPath(), arrD.ToString(Newtonsoft.Json.Formatting.None));
            }
            catch { }
        }

        private static string DenyPath()
        {
            return Path.Combine(AgentDir(), "..", "denials.json");
        }

        public static void LoadDenials()
        {
            try
            {
                string p = DenyPath();
                if (!File.Exists(p)) return;
                var arr = JArray.Parse(File.ReadAllText(p));
                lock (Deny) { foreach (var t in arr) { string s = (string)t; if (!string.IsNullOrWhiteSpace(s)) Deny.Add(s.Trim()); } }
            }
            catch { }
        }

        /// <returns>null = allowed, "ask" = needs card, "deny" = blocked.</returns>
        public static string ApprovalFor(string tool)
        {
            lock (Deny) { if (tool != null && Deny.Contains(tool)) return "deny"; }
            lock (AlwaysAllow) { if (tool != null && AlwaysAllow.Contains(tool)) return "allow"; }
            return null;
        }

        public static bool SetDenied(string tool, bool denied)
        {
            if (string.IsNullOrWhiteSpace(tool)) return false;
            tool = tool.Trim();
            lock (Deny) { if (denied) Deny.Add(tool); else Deny.Remove(tool); }
            if (denied) { lock (AlwaysAllow) { AlwaysAllow.Remove(tool); } }
            SaveApprovals();
            return true;
        }

        public static string ToolCategory(string name)
        {
            string n = (name ?? "").Trim().ToLowerInvariant();
            if (n.StartsWith("delete") || n.StartsWith("remove_") || n.StartsWith("close") || n.Contains("suppress") || n.Contains("kill"))
                return "destructive";
            if (n.StartsWith("get") || n.StartsWith("list") || n.StartsWith("search") || n.StartsWith("check") ||
                n.StartsWith("is_") || n.StartsWith("has_") || n.StartsWith("find") || n.StartsWith("measur") ||
                n.StartsWith("bounding") || n.StartsWith("mass") || n.StartsWith("count") || n.StartsWith("exist") ||
                n.StartsWith("compar") || n.StartsWith("interfer") || n.StartsWith("profil") || n.StartsWith("inspect") ||
                n.StartsWith("validat") || n.StartsWith("estim"))
                return "read";
            return "build";
        }

        private static bool IsApproved(string tool)
        {
            if (ApprovalFor(tool) == "deny") return false;
            if (ApprovalFor(tool) == "allow") return true;
            if (!ApproveMaster) return false;
            switch (ToolCategory(tool))
            {
                case "read": return ApproveRead;
                case "build": return ApproveBuild;
                default: return ApproveDestructive;
            }
        }

        /// <summary>Resolve an approval card reply from the panel (approve / always / reject).</summary>
        public static void ResolveApproval(string id, string decision)
        {
            System.Threading.Tasks.TaskCompletionSource<string> tcs = null;
            lock (PendingApprovals) { if (id != null && PendingApprovals.TryGetValue(id, out tcs)) PendingApprovals.Remove(id); }
            if (tcs == null) return;
            string d = (decision ?? "reject").Trim().ToLowerInvariant();
            if (d == "always")
            {
                // Remember the TOOL (not the run) — future calls skip the card entirely.
                try
                {
                    // Approve ids are "appr_" + 8 hex chars + "_" + tool. Tool names contain
                    // '_' (e.g. get_mass_properties), so split at the fixed prefix, not the last '_'.
                    string tool = null;
                    const string prefix = "appr_";
                    int sep = prefix.Length + 8;
                    if (id.StartsWith(prefix) && id.Length > sep + 1 && id[sep] == '_')
                        tool = id.Substring(sep + 1);
                    else
                    {
                        int us = id.LastIndexOf('_');
                        if (us >= 0) tool = id.Substring(us + 1);
                    }
                    if (!string.IsNullOrWhiteSpace(tool)) { lock (AlwaysAllow) { AlwaysAllow.Add(tool); } SaveApprovals(); }
                }
                catch { }
                d = "approve";
            }
            try { tcs.TrySetResult(d); } catch { }
        }

        /// <summary>
        /// Kilo-style gate: approved tools run straight through; anything else pauses the loop with
        /// an approval card and resumes on the user's verdict. Returns null to proceed, or a synthetic
        /// refused ToolResult when the user rejects.
        /// </summary>
        public static async Task<McpBridge.ToolResult> GateToolAsync(
            string tool, JObject args, Func<string, string, string, string, Task> emit, System.Threading.CancellationToken ct)
        {
            if (ApprovalFor(tool) == "deny")
                return new McpBridge.ToolResult { Ok = false, Text = "Tool '" + tool + "' is on your deny list — work around it or remove the denial." };
            if (IsApproved(tool)) return null;
            string id = "appr_" + Guid.NewGuid().ToString("N").Substring(0, 8) + "_" + tool;
            var tcs = new System.Threading.Tasks.TaskCompletionSource<string>();
            lock (PendingApprovals) { PendingApprovals[id] = tcs; }
            var sender = CardSender;
            if (sender == null)
            {
                // Panel is gone: no card can ever be answered, so fail closed instead of
                // parking the loop on a promise nobody will resolve.
                lock (PendingApprovals) { PendingApprovals.Remove(id); }
                return new McpBridge.ToolResult { Ok = false, Text = "Tool '" + tool + "' needs approval, but the panel is gone — resend with the panel open." };
            }
            try { sender(new JObject { ["type"] = "approve", ["id"] = id, ["tool"] = tool, ["args"] = args ?? new JObject() }); }
            catch
            {
                // Dead sender (panel torn down mid-run): same as no sender — fail closed, don't hang.
                lock (PendingApprovals) { PendingApprovals.Remove(id); }
                return new McpBridge.ToolResult { Ok = false, Text = "Tool '" + tool + "' needs approval, but the panel is gone — resend with the panel open." };
            }
            try { await emit("Builder", "needs approval: " + tool, "run", null); }
            catch { /* emit is best-effort: a dead panel must not break the gate wait */ }
            string verdict;
            try
            {
                await Task.WhenAny(tcs.Task, Task.Delay(-1, ct));
                // WhenAny can return the cancelled delay even though the verdict landed at the
                // same instant — prefer a completed approval over the cancellation.
                if (tcs.Task.IsCompleted) verdict = await tcs.Task;
                else throw new OperationCanceledException(ct);
            }
            finally { lock (PendingApprovals) { PendingApprovals.Remove(id); } }
            if (verdict == "approve") return null;
            return new McpBridge.ToolResult { Ok = false, Text = "User rejected this step — work around it or ask what to do instead." };
        }

        // Auto-compaction controls (Kilo parity: user-set threshold, disableable). Panel settings
        // cog owns the values; the host just honors them. at <= 0 or off = never compact.
        public static bool CompactOn = true;
        public static int CompactAt = 60;

        public static void EnsureSessionPublic(string sid) { EnsureSession(sid); }

        private static string AgentDir()
        {
            string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Forge", "agent");
            try { Directory.CreateDirectory(d); } catch { }
            return d;
        }

        private static string SafeSid(string sid)
        {
            // Sids ride in from the panel: never let "../" escape the agent dir, and never
            // let "" scatter callers across the "default" chat by accident.
            string s = (sid ?? "").Trim();
            if (s.Length == 0) return "default";
            try { s = Path.GetFileName(s); } catch { }
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c.ToString(), "_");
            s = s.Trim();
            if (s.Length == 0 || s == "." || s == "..") return "default";
            return s;
        }

        private static AgentSession EnsureSession(string sid)
        {
            sid = SafeSid(sid);
            lock (Sessions)
            {
                AgentSession s;
                if (Sessions.TryGetValue(sid, out s)) { CurrentSid = sid; AgentMsgCount = s.Msgs.Count; return s; }
                s = new AgentSession();
                Sessions[sid] = s;
                CurrentSid = sid;
                // Bound the in-memory table the same way SaveSession bounds the files on disk.
                if (Sessions.Count > 50)
                {
                    string drop = null;
                    foreach (var k in Sessions.Keys) { if (!string.Equals(k, sid, StringComparison.Ordinal)) { drop = k; break; } }
                    if (drop != null) Sessions.Remove(drop);
                }
                // File IO stays OUTSIDE the Sessions lock: holding it across disk reads stalls
                // every other chat (and risks stalling the UI thread on a locked/quota file).
                string p = null;
                try { p = Path.Combine(AgentDir(), sid + ".json"); } catch { }
                bool haveFile = false;
                try { haveFile = p != null && File.Exists(p); } catch { }
                if (haveFile)
                {
                    JArray arr = null;
                    try { arr = JArray.Parse(File.ReadAllText(p)); } catch { arr = null; }
                    if (arr != null)
                    {
                        lock (s.Msgs)
                        {
                            if (s.Msgs.Count == 0)
                            {
                                foreach (var t in arr) { var o = t as JObject; if (o != null) s.Msgs.Add(o); }
                                // Rebuild turn boundaries (user-role entries) since only messages persist.
                                for (int i = 0; i < s.Msgs.Count; i++)
                                    if ((string)s.Msgs[i]["role"] == "user") s.TurnStarts.Add(i);
                            }
                        }
                    }
                }
                AgentMsgCount = s.Msgs.Count;
                return s;
            }
        }

        private static void SaveSession(string sid)
        {
            try
            {
                AgentSession s;
                string safe = SafeSid(sid);
                lock (Sessions) { if (!Sessions.TryGetValue(safe, out s)) return; }
                var arr = new JArray();
                // Clone under the lock: the live JObjects stay parented to the session, so a
                // concurrent append can't re-parent (or tear) mid-serialize.
                lock (s.Msgs) { foreach (var m in s.Msgs) { try { arr.Add(m.DeepClone()); } catch { } } }
                File.WriteAllText(Path.Combine(AgentDir(), safe + ".json"), arr.ToString(Newtonsoft.Json.Formatting.None));
                // Prune old session files (keep newest 30).
                try
                {
                    var files = new DirectoryInfo(AgentDir()).GetFiles("*.json");
                    Array.Sort(files, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
                    for (int i = 30; i < files.Length; i++) { try { files[i].Delete(); } catch { } }
                }
                catch { }
            }
            catch { }
        }

        public static void ClearHistory()
        {
            lock (Sessions) { Sessions.Clear(); CurrentSid = null; }
            AgentMsgCount = 0;
            // Fail pending approval gates instead of orphaning them: the panel is gone and no
            // verdict can ever arrive, so waiting runs would hang until their own cancel.
            lock (PendingApprovals)
            {
                foreach (var kv in PendingApprovals)
                    try { kv.Value.TrySetResult("reject"); } catch { }
                PendingApprovals.Clear();
            }
        }

        public static int HistoryCount()
        {
            try
            {
                lock (Sessions)
                {
                    AgentSession s;
                    if (CurrentSid != null && Sessions.TryGetValue(CurrentSid, out s))
                    {
                        lock (s.Msgs) { return s.Msgs.Count; }
                    }
                }
            }
            catch { }
            return 0;
        }

        /// <summary>Revert a chat to its first keep user-turns (panel counts turns, host maps to messages).</summary>
        public static void TruncateHistory(string sid, int keepTurns)
        {
            if (string.IsNullOrWhiteSpace(sid)) return;
            if (keepTurns < 0) return; // garbage keep must never wipe: only >= 0 is meaningful
            var s = EnsureSession(sid);
            lock (s.Msgs)
            {
                if (keepTurns <= 0)
                {
                    // Keep only the system prompt: a truly fresh conversation on this chat.
                    JObject sys = s.Msgs.Count > 0 && (string)s.Msgs[0]["role"] == "system" ? s.Msgs[0] : null;
                    s.Msgs.Clear();
                    s.TurnStarts.Clear();
                    if (sys != null) s.Msgs.Add(sys);
                }
                else if (keepTurns < s.TurnStarts.Count)
                {
                    int cut = s.TurnStarts[keepTurns];
                    if (cut < 0) cut = 0;
                    if (cut > s.Msgs.Count) cut = s.Msgs.Count;
                    s.Msgs.RemoveRange(cut, s.Msgs.Count - cut);
                    s.TurnStarts.RemoveRange(keepTurns, s.TurnStarts.Count - keepTurns);
                }
                AgentMsgCount = s.Msgs.Count;
            }
            SaveSession(sid);
        }

        /// <summary>Summarize (never silently drop) the oldest half once history passes the threshold.</summary>
        private static async Task CompactHistoryIfNeeded(IProviderClient client, string sid, string model, System.Threading.CancellationToken ct)
        {
            if (!CompactOn || CompactAt <= 0) return; // the settings toggle alone disables — no magic numbers
            int at = CompactAt < 10 ? 60 : CompactAt;
            AgentSession session = EnsureSession(sid);
            int count;
            lock (session.Msgs) { count = session.Msgs.Count; }
            if (count <= at) return;
            int cut = count - 30; // summarize everything except the newest 30
            List<JObject> old;
            lock (session.Msgs) { old = session.Msgs.GetRange(1, cut - 1); } // keep [0]=system
            string summary = null;
            try
            {
                var msgs = new List<ChatMessage>
                {
                    new ChatMessage { Role = "system", Content = "Summarize this CAD agent history into durable facts: what was built, key dims/decisions, open issues. Plain text, under 200 words." },
                    new ChatMessage { Role = "user", Content = new JArray(old.ToArray()).ToString(Newtonsoft.Json.Formatting.None) }
                };
                var res = await client.CompleteAsync(msgs,
                    new ProviderOptions { MaxTokens = 800, Temperature = 0, Model = model },
                    ct);
                summary = res != null ? res.Text : null;
            }
            catch (OperationCanceledException) { return; } // stop wins: leave history untouched
            catch { summary = null; }
            if (ct.IsCancellationRequested) return;
            lock (session.Msgs)
            {
                // The history may have been truncated/reverted while the summary was authored;
                // a stale cut would corrupt the messages. Skip this round instead.
                if (session.Msgs.Count != count) return;
                int shift;
                if (string.IsNullOrWhiteSpace(summary))
                {
                    // LLM summary failed: keep a titles digest instead of silently dropping history.
                    var lines = new System.Collections.Generic.List<string>();
                    for (int i = 1; i < cut && i < session.Msgs.Count; i++)
                    {
                        var mm = session.Msgs[i];
                        string role = (string)mm["role"] ?? "?";
                        string cc = (string)mm["content"] ?? "";
                        if (role == "tool") cc = "tool result (" + cc.Length + " chars)";
                        else if (role == "assistant")
                        {
                            var tcs = mm["tool_calls"] as JArray;
                            cc = tcs != null && tcs.Count > 0 ? "tool calls" : cc;
                        }
                        if (cc.Length > 90) cc = cc.Substring(0, 90) + "…";
                        lines.Add("- [" + role + "] " + cc);
                    }
                    session.Msgs.RemoveRange(1, cut - 1);
                    session.Msgs.Insert(1, new JObject { ["role"] = "system", ["content"] = "[Earlier work compacted — summary unavailable, titles kept]\n" + string.Join("\n", lines.ToArray()) });
                    shift = cut - 2; // survivors at old i >= cut land at i-(cut-1), then insert shifts +1
                    try { GroundTruth.Trace?.Invoke("mcp-build: history compact titles-fallback, kept " + lines.Count + " titles"); } catch { }
                }
                else
                {
                    session.Msgs.RemoveRange(1, cut - 1);
                    session.Msgs.Insert(1, new JObject { ["role"] = "system", ["content"] = "[Earlier work compacted] " + summary.Trim() });
                    shift = cut - 2; // removal then insert shifts survivors one back: i-cut+2
                }
                // TurnStarts inside the removed span no longer point at a message, so drop them
                // (never clamp to 0 — that corrupts the next TruncateHistory cut).
                var kept = new List<int>(session.TurnStarts.Count);
                foreach (int t in session.TurnStarts)
                {
                    if (t <= 0) kept.Add(t);
                    else if (t >= cut) kept.Add(t - shift);
                }
                session.TurnStarts.Clear();
                session.TurnStarts.AddRange(kept);
            }
            SaveSession(sid);
        }

        private static readonly Regex CreateVerbRx = new Regex(
            @"\b(create|make|build|design|generate|model)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex AssemblyNounRx = new Regex(
            @"\b(assembl(?:y|ies)|sub[\s-]?assembl(?:y|ies))\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // Drawing/export-domain nouns are never a multi-part from-scratch build: "generate a drawing"
        // and "create a section view" edit the drawing of the CURRENT model, so they belong to the
        // drawing handlers downstream. "sheet" is deliberately absent — "sheet metal" is part-domain.
        private static readonly Regex DrawingDomainRx = new Regex(
            @"\b(drawings?|drafting|dxf|dwg|pdf|views?|flat[\s-]?pattern|section\s+views?|detail\s+views?)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // Edit phrasing that still carries a create verb ("make this thicker", "build up the flange",
        // "make the length 100mm", "make it 6 instances"): an MCP from-scratch build closes the current
        // doc, so claiming these destroys context. Narrow by construction — comparatives need a
        // demonstrative ("this/that/it"), dims need the definite article — so a bare "create a thicker
        // bracket" or "build the gearbox 100mm long" still routes here.
        private static readonly Regex EditCueRx = new Regex(
            @"\bbuil[dt]\s+up\b|\b(this|that|these|those|it)\b.{0,20}\b(thicker|thinner|taller|shorter|wider|narrower|larger|smaller|longer)\b|\b(the|this|that|it|its)\s+(length|width|height|depth|diameter|radius|thickness)\b|\bmake\s+it\s+\d",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// Explicit "mcp", assembly builds, tangled multi-steps — plus any create-like ask the deterministic
        /// single-shape handlers don't own ("build a gearbox" names no bare primitive). Bare primitives,
        /// blank part/assembly creates, and confident two-step compounds keep their deterministic paths.
        /// Single-part cue-carrying asks ("plate with holes") fall through to the recipe route below.
        /// </summary>
        public static bool IsMcpCreate(string intent)
        {
            string text = (intent ?? "").Trim();
            if (text.Length == 0) return false;
            if (Regex.IsMatch(text, @"\bmcp\b", RegexOptions.IgnoreCase)) return true;
            // Question/inspection openers ("review the model", "what is this") name the model noun
            // without asking to build — never a from-scratch create.
            if (Regex.IsMatch(text, @"^\s*(review|inspect|show|check|analy[sz]e|explain|describe|what|how|is|are|list|tell)\b", RegexOptions.IgnoreCase)) return false;
            if (!CreateVerbRx.IsMatch(text)) return false;
            if (DrawingDomainRx.IsMatch(text)) return false; // drawing/view/export: edit-domain, not a from-scratch build
            if (EditCueRx.IsMatch(text)) return false;       // "make this thicker" / "build up the flange": edits, not builds
            var comp = CompoundCreate.Parse(text);
            if (comp != null && comp.IsCompound && comp.Error == null) return false; // confident 2-step: deterministic
            if (comp != null && comp.Error != null) return true;                     // 3+-step / ambiguous multi: MCP
            if (AssemblyNounRx.IsMatch(text)) return true;
            if (CreateGuardrail.UnsupportedCue(text) == null && !OwnedByDeterministic(text)) return true;
            return false;
        }

        // A cue-free intent a single-shape handler would build deterministically (bare primitive or
        // blank part/assembly). Mirrors the pipeline's widened plate-family net so plain "block/cube"
        // phrasing stays deterministic too.
        private static bool OwnedByDeterministic(string text)
        {
            string c = text.ToLowerInvariant();
            if (CreatePart.IsIntent(text) || CreateAssembly.IsIntent(text)) return true;
            if (CreateSphere.IsIntent(text) || CreateFlange.IsIntent(text) || CreateCone.IsIntent(text) ||
                CreateTorus.IsIntent(text) || CreateTube.IsIntent(text) || CreateCylinder.IsIntent(text) ||
                CreatePlate.IsIntent(text))
                return true;
            if (Regex.IsMatch(c, @"\b(part|component|assembly|drawing|mate|sketch|pattern|configur|reference)\b")) return false;
            return Regex.IsMatch(c, @"\b(plate|panel|slab|blank|block|cube|rectangular)\b");
        }

        public class McpBuildOutcome
        {
            public bool Attempted;
            public string Summary;
            public string Error;
        }

        public static async Task<McpBuildOutcome> Run(string intent, Func<string, string, string, string, Task> emit, System.Threading.CancellationToken ct, Action<string, string> onThink)
        {
            var outcome = new McpBuildOutcome();
            if (!IsMcpCreate(intent)) return outcome;

            IProviderClient client;
            try
            {
                client = ProviderFactory.Resolve();
                if (client == null) return outcome; // keyless: fall through to the existing refusal
            }
            catch { return outcome; }

            outcome.Attempted = true;
            await emit("Builder", "starting the MCP build server", "run", null);

            JArray tools;
            try
            {
                await McpBridge.EnsureStartedAsync();
                tools = await McpBridge.ListToolsAsync();
                if (tools == null || tools.Count == 0) throw new McpBridge.McpException("tool list came back empty.");
            }
            catch (Exception ex)
            {
                outcome.Error = "Couldn't start the SolidWorks-MCP server: " + ex.Message;
                return outcome;
            }

            // Compact catalog: name + first line of description (88 tools fit the prompt easily).
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var lines = new List<string>();
            foreach (var t in tools)
            {
                var o = t as JObject; if (o == null) continue;
                string n = (string)o["name"]; if (string.IsNullOrWhiteSpace(n)) continue;
                names.Add(n);
                string d = ((string)o["description"] ?? "").Split('\n')[0].Trim();
                if (d.Length > 160) d = d.Substring(0, 160);
                lines.Add(n + ": " + d);
            }

            await emit("Builder", "planning " + lines.Count + " available modelling tools", "run", null);
            Tuple<JObject, string> authored;
            try { authored = await AuthorPlan(client, intent, lines, ct, onThink); }
            catch (OperationCanceledException) { outcome.Error = "INTERRUPTED"; return outcome; }
            catch (Exception ex)
            {
                outcome.Error = "The build planner failed (" + ex.GetType().Name + ") — try splitting the request into smaller steps.";
                return outcome;
            }
            var plan = authored.Item1;
            if (plan == null)
            {
                string why = string.IsNullOrEmpty(authored.Item2) ? "" : " (" + authored.Item2 + ")";
                outcome.Error = "I couldn't turn that into a tool plan" + why + " — rephrase with concrete parts and dims.";
                return outcome;
            }

            var steps = plan["steps"] as JArray;
            if (steps == null || steps.Count == 0 || steps.Count > 40)
            {
                outcome.Error = "The plan has " + (steps == null ? 0 : steps.Count) + " steps (chat builds cap at 40) — split it into smaller builds.";
                return outcome;
            }
            foreach (var s in steps)
            {
                var so = s as JObject;
                if (so == null || string.IsNullOrWhiteSpace((string)so["tool"]) || !names.Contains((string)so["tool"]))
                {
                    outcome.Error = "The plan calls an unknown tool ('" + (string)(so != null ? so["tool"] : null) + "') — ask again.";
                    return outcome;
                }
            }

            var log = new List<string>();
            int n_ = 0;
            foreach (var s in steps)
            {
                ct.ThrowIfCancellationRequested();
                var so = (JObject)s;
                string tool = (string)so["tool"];
                var args = so["args"] as JObject ?? new JObject();
                n_++;
                await emit("Builder", "step " + n_ + "/" + steps.Count + ": " + tool, "run", null);
                McpBridge.ToolResult r;
                var gate = await GateToolAsync(tool, args, emit, ct);
                if (gate != null)
                {
                    outcome.Error = "You rejected step " + n_ + " (" + tool + ") — what built so far stays open in SolidWorks; adjust and ask again.";
                    return outcome;
                }
                try
                {
                    // Cancellable mid-flight like the agent loop: stop abandons the wait instead
                    // of hanging on the server call's own (minutes-long) timeout.
                    var callTask = McpBridge.CallToolAsync(tool, args);
                    await Task.WhenAny(callTask, Task.Delay(-1, ct));
                    if (!callTask.IsCompleted) throw new OperationCanceledException(ct);
                    r = await callTask;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    outcome.Error = "Step " + n_ + " (" + tool + ") failed to run: " + ex.Message + " — what built so far stays open in SolidWorks.";
                    return outcome;
                }
                string text = (r.Text ?? "").Length > 600 ? r.Text.Substring(0, 600) + "…" : (r.Text ?? "");
                log.Add(tool + " -> " + (r.Ok ? "ok" : "FAILED") + " " + text);
                if (!r.Ok)
                {
                    outcome.Error = "Step " + n_ + " (" + tool + ") refused: " + text + " — what built so far stays open in SolidWorks; fix that step and ask again.";
                    return outcome;
                }
            }

            string measured = await BestEffortVerify(ct);
            outcome.Summary = "Built via the MCP server in " + steps.Count + " steps (" +
                string.Join("; ", log.ToArray()).Substring(0, Math.Min(400, string.Join("; ", log.ToArray()).Length)) +
                ")" + (string.IsNullOrEmpty(measured) ? "." : " Measured: " + measured);
            return outcome;
        }

        /// <summary>
        /// The agentic loop: history + real function calls + observe -> re-plan. Only for
        /// openai-compatible providers (function calling); anything else falls back to single-shot Run.
        /// If the model completes with zero tool calls, that means it can't/won't use tools — fall back
        /// to the JSON-plan path rather than pretending a text reply built anything.
        /// </summary>
        public static async Task<McpBuildOutcome> RunAgent(
            string intent, Func<string, string, string, string, Task> emit, System.Threading.CancellationToken ct, Action<string, string> onThink, string sid)
        {
            var outcome = new McpBuildOutcome();
            if (!IsMcpCreate(intent)) return outcome;

            IProviderClient client;
            try
            {
                client = ProviderFactory.Resolve();
                if (client == null) return outcome;
            }
            catch { return outcome; }
            if (client.Id != "openai-compatible")
                return await Run(intent, emit, ct, onThink); // no function calling here: single-shot path

            outcome.Attempted = true;
            JArray tools;
            try
            {
                await McpBridge.EnsureStartedAsync();
                tools = await McpBridge.ListToolsAsync();
                if (tools == null || tools.Count == 0) throw new McpBridge.McpException("tool list came back empty.");
            }
            catch (Exception ex)
            {
                outcome.Error = "Couldn't start the SolidWorks-MCP server: " + ex.Message;
                return outcome;
            }

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var fns = new JArray();
            foreach (var t in tools)
            {
                var o = t as JObject; if (o == null) continue;
                string n = (string)o["name"]; if (string.IsNullOrWhiteSpace(n)) continue;
                names.Add(n);
                fns.Add(new JObject
                {
                    ["type"] = "function",
                    ["function"] = new JObject
                    {
                        ["name"] = n,
                        ["description"] = (((string)o["description"]) ?? "").Split('\n')[0].Trim(),
                        ["parameters"] = o["inputSchema"] ?? new JObject { ["type"] = "object" }
                    }
                });
            }
            if (names.Count == 0)
            {
                outcome.Error = "Couldn't start the SolidWorks-MCP server: tool list came back empty.";
                return outcome;
            }
            string toolsJson = fns.ToString(Newtonsoft.Json.Formatting.None);
            string model = ProviderFactory.CurrentModel();

            var session = EnsureSession(sid);
            lock (session.Msgs)
            {
                if (session.Msgs.Count == 0 || (string)session.Msgs[0]["role"] != "system")
                {
                    session.Msgs.Insert(0, new JObject { ["role"] = "system", ["content"] = AgentSystemPrompt });
                    for (int ti = 0; ti < session.TurnStarts.Count; ti++) session.TurnStarts[ti]++;
                }
                session.TurnStarts.Add(session.Msgs.Count);
                session.Msgs.Add(new JObject { ["role"] = "user", ["content"] = (intent ?? "").Trim() });
                AgentMsgCount = session.Msgs.Count;
            }
            SaveSession(sid);
            await CompactHistoryIfNeeded(client, sid, model, ct);

            var opts = new ProviderOptions { MaxTokens = 16000, Temperature = 0, Model = model, ToolsJson = toolsJson, ToolChoice = "auto" };
            var log = new List<string>();
            int executed = 0;
            string lastCallSig = null;
            int repeatCount = 0;
            try
            {
                // No fixed turn cap: compaction bounds the context, so the loop runs until the model
                // finishes. The 200-iteration breaker is runaway protection only, not a budget.
                for (int iter = 1; ; iter++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (iter > 200 || executed > 200)
                    {
                        // iter bounds planning turns (unknown-tool spins execute nothing); executed
                        // bounds real tool calls (parallel batches pack many calls into one turn).
                        outcome.Error = "Stopped after " + iter + " planning turns / " + executed + " tool steps (cap 200) — the build looks stuck in a loop (" +
                            string.Join("; ", log.ToArray()) + "). What built so far stays open; rephrase to break the cycle.";
                        SaveSession(sid);
                        return outcome;
                    }
                    if (iter % 5 == 0) await CompactHistoryIfNeeded(client, sid, model, ct);
                    try { await emit("Builder", "thinking through the build (step " + iter + ")", "run", null); }
                    catch { /* dead panel: the loop still runs, the user just sees nothing */ }
                    List<JObject> hist;
                    lock (session.Msgs) { hist = new List<JObject>(session.Msgs); }
                    ProviderResult res = await StreamAgentTurn(client, new JArray(hist.ToArray()), opts, ct, onThink);
                    if (res == null) throw new Exception("empty provider response");
                    try { IntentRate.Note(res.TokIn, res.TokOut); } catch { }
                    System.Threading.Interlocked.Add(ref _agentTokCount, res.TokIn + res.TokOut);

                    if (res.ToolCalls == null || res.ToolCalls.Count == 0)
                    {
                        // Kilo parity: a text-only reply ends the turn — never pretend it built anything,
                        // and never force a second planning pass behind the model's back.
                        string summary = (res.Text ?? "").Trim();
                        if (summary.Length > 600) summary = summary.Substring(0, 600) + "…";
                        if (executed == 0 && string.IsNullOrEmpty(summary))
                        {
                            outcome.Error = "The model replied with nothing usable — no tool calls and no text. Try again or rephrase.";
                            SaveSession(sid);
                            return outcome;
                        }
                        outcome.Summary = executed == 0
                            ? summary
                            : ("Built in " + executed + " tool steps" + (string.IsNullOrEmpty(summary) ? "." : ". " + summary));
                        SaveSession(sid);
                        return outcome;
                    }

                    // Doom-loop guard (Kilo parity): the same mutating tool+args three times in a row
                    // means the model is stuck — stop loudly instead of burning the run to the breaker.
                    // Read-only repeats are legit polling (measure -> adjust -> re-measure), so they
                    // never trip the guard.
                    {
                        string sigName = res.ToolCalls.Count == 1 ? (res.ToolCalls[0].Name ?? "") : null;
                        string sig = res.ToolCalls.Count == 1 && !string.IsNullOrWhiteSpace(sigName) &&
                            !string.Equals(ToolCategory(sigName), "read", StringComparison.OrdinalIgnoreCase)
                            ? (sigName + "|" + (res.ToolCalls[0].ArgumentsJson ?? ""))
                            : null;
                        if (sig != null && sig == lastCallSig) { repeatCount++; } else if (sig != null) { repeatCount = 1; lastCallSig = sig; } else { repeatCount = 0; lastCallSig = null; }
                        if (repeatCount >= 3)
                        {
                            outcome.Error = "Stopped: the model repeated the same call 3 times (" +
                                (res.ToolCalls[0].Name ?? "?") + "). What built so far stays open; rephrase to break the cycle.";
                            SaveSession(sid);
                            return outcome;
                        }
                    }

                    // Record the assistant turn (with tool calls) so the next iteration observes it.
                    var asst = new JObject { ["role"] = "assistant", ["content"] = res.Text ?? "" };
                    var tcArr = new JArray();
                    foreach (var c in res.ToolCalls)
                        tcArr.Add(new JObject { ["id"] = c.Id ?? Guid.NewGuid().ToString("N"), ["type"] = "function", ["function"] = new JObject { ["name"] = c.Name, ["arguments"] = c.ArgumentsJson ?? "{}" } });
                    asst["tool_calls"] = tcArr;
                    lock (session.Msgs) { session.Msgs.Add(asst); AgentMsgCount = session.Msgs.Count; }

                    foreach (var c in res.ToolCalls)
                    {
                        ct.ThrowIfCancellationRequested();
                        string cid = c.Id;
                        if (string.IsNullOrWhiteSpace(cid)) cid = Guid.NewGuid().ToString("N");
                        if (string.IsNullOrWhiteSpace(c.Name) || !names.Contains(c.Name))
                        {
                            lock (session.Msgs) { session.Msgs.Add(new JObject { ["role"] = "tool", ["tool_call_id"] = cid, ["content"] = "ERROR: unknown tool '" + c.Name + "'. Use only listed tools." }); }
                            continue;
                        }
                        JObject args;
                        try { args = JObject.Parse(string.IsNullOrWhiteSpace(c.ArgumentsJson) ? "{}" : c.ArgumentsJson); }
                        catch
                        {
                            // Malformed args must never run as "{}" (wrong dims on a mutating tool).
                            // Feed the syntax error back and let the model re-emit the call.
                            lock (session.Msgs) { session.Msgs.Add(new JObject { ["role"] = "tool", ["tool_call_id"] = cid, ["content"] = "ERROR: arguments for '" + c.Name + "' were not valid JSON — retry the call with fixed arguments." }); }
                            continue;
                        }
                        executed++;
                        try { await emit("Builder", "step " + executed + ": " + c.Name, "run", null); }
                        catch { /* dead panel: keep executing, the user just sees nothing */ }
                        McpBridge.ToolResult r;
                        var gate = await GateToolAsync(c.Name, args, emit, ct);
                        if (gate != null) r = gate;
                        else
                        {
                            // Cancellable mid-flight: an orphaned server call is discarded, never applied twice.
                            var callTask = McpBridge.CallToolAsync(c.Name, args);
                            var finished = await Task.WhenAny(callTask, Task.Delay(-1, ct));
                            if (finished != callTask) throw new OperationCanceledException(ct);
                            try { r = await callTask; }
                            catch (Exception ex) { r = new McpBridge.ToolResult { Ok = false, Text = "transport failed: " + ex.Message }; }
                        }
                        string text = r.Text ?? "";
                        if (text.Length > 2000) text = text.Substring(0, 2000) + "…(truncated)";
                        log.Add(c.Name + " -> " + (r.Ok ? "ok" : "FAILED"));
                        lock (session.Msgs) { session.Msgs.Add(new JObject { ["role"] = "tool", ["tool_call_id"] = cid, ["content"] = (r.Ok ? "" : "FAILED: ") + text }); AgentMsgCount = session.Msgs.Count; }
                        if (!r.Ok)
                        {
                            // Don't stop: feed the failure back and let the model re-plan around it.
                            try { await emit("Builder", c.Name + " missed — re-planning around it", "run", null); }
                            catch { /* dead panel */ }
                        }
                    }
                    SaveSession(sid);
                }
            }
            catch (OperationCanceledException) { SaveSession(sid); outcome.Error = "INTERRUPTED"; return outcome; }
            catch (Exception ex)
            {
                SaveSession(sid);
                outcome.Error = "The agent loop failed (" + ex.GetType().Name + ": " + ex.Message + ") — try again or split the request.";
                return outcome;
            }
        }

        // One streamed agent turn: the full history goes over the wire as raw OpenAI messages
        // (ChatMessage can't carry tool_calls/tool results), deltas stream CoT via onThink.
        private static async Task<ProviderResult> StreamAgentTurn(
            IProviderClient client, JArray history, ProviderOptions opts, System.Threading.CancellationToken ct, Action<string, string> onThink)
        {
            var oc = client as OpenAiCompatibleClient;
            if (oc == null) throw new Exception("agent loop needs an OpenAI-compatible provider");
            var res = await oc.CompleteStreamingRawAsync(history, opts, ct, onThink);
            return res;
        }

        private const string AgentSystemPrompt =
            "You are Forge, a parametric CAD builder driving SolidWorks through tools. ALL dimensions in MILLIMETRES. " +
            "Rules: start with new_part (or new_assembly for multi-part builds); build the base solid first, then features; " +
            "observe every tool result and adapt — a failed call means try a different tool or dims, never repeat it verbatim; " +
            "verify with get_mass_properties or get_bounding_box when done; keep calling tools until the request is fully built; " +
            "then reply with a short summary including measured dims. No filler steps.";

        private static async Task<string> BestEffortVerify(System.Threading.CancellationToken ct)
        {
            foreach (string probe in new[] { "get_mass_properties", "get_bounding_box" })
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var r = await McpBridge.CallToolAsync(probe, new JObject(), 120000);
                    if (r.Ok && !string.IsNullOrWhiteSpace(r.Text))
                    {
                        string t = r.Text;
                        return t.Length > 300 ? t.Substring(0, 300) + "…" : t;
                    }
                }
                catch { }
            }
            return null;
        }

        private static async Task<Tuple<JObject, string>> AuthorPlan(IProviderClient client, string intent, List<string> catalog, System.Threading.CancellationToken ct, Action<string, string> onThink)
        {
            // Finite cap: uncapped lets reasoning models think forever and the call never returns.
            // Verified live: capped calls complete; uncapped hang past the 10-minute timeout.
            var opts = new ProviderOptions { MaxTokens = 32000, Temperature = 0, Model = ProviderFactory.CurrentModel() };
            string user = "User said: \"" + (intent ?? "").Trim() + "\"\n\nOutput ONLY the plan JSON. No markdown fences, no commentary.";
            string lastDiag = "no attempts made";
            for (int attempt = 0; attempt < 2; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                var msgs = new List<ChatMessage>
                {
                    new ChatMessage { Role = "system", Content = PlanSystemPrompt + "\n\nAvailable tools:\n" + string.Join("\n", catalog.ToArray()) },
                    new ChatMessage { Role = "user", Content = attempt == 0 ? user : user + " JSON object only." }
                };
                ProviderResult res;
                try { res = await client.CompleteStreamingAsync(msgs, opts, ct, onThink); }
                catch (OperationCanceledException) { throw; }
                catch (ProviderException px) when ((px.Message ?? "").Contains("Streaming is not supported"))
                {
                    // Non-streaming backends (Anthropic): same prompt as one unary call, no live deltas.
                    try { res = await client.CompleteAsync(msgs, opts, ct); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex2) { return new Tuple<JObject, string>(null, "provider call failed: " + ex2.GetType().Name + " " + ex2.Message); }
                }
                catch (Exception ex)
                {
                    string why = (ex.Message ?? "");
                    if (why.Contains("timed out"))
                        return new Tuple<JObject, string>(null, "TIMED OUT after 10 minutes of thinking — the model is overloaded or the plan is too big; retry, simplify, or switch to a faster model");
                    return new Tuple<JObject, string>(null, "provider call failed: " + ex.GetType().Name + " " + ex.Message);
                }
                string raw = res != null ? res.Text : null;
                string err;
                var plan = SafeJsonExtractor.ExtractResponseEnvelope(raw, out err);
                if (plan != null && !(plan["steps"] is JArray) && plan["tool_calls"] is JArray)
                    plan["steps"] = plan["tool_calls"];
                if (plan != null && plan["steps"] is JArray)
                {
                    try { IntentRate.Note(res.TokIn, res.TokOut); } catch { }
                    try { GroundTruth.Trace?.Invoke("mcp-build: planned model=" + res.Model); } catch { }
                    return new Tuple<JObject, string>(plan, null);
                }
                lastDiag = err ?? "no steps array";
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    string head = raw.Trim();
                    if (head.Length > 220) head = head.Substring(0, 220) + "…";
                    lastDiag += "; model said: " + head;
                }
            }
            return new Tuple<JObject, string>(null, lastDiag);
        }

        private const string PlanSystemPrompt =
            "You plan parametric SolidWorks builds as ordered MCP tool calls. Rules: ALL dimensions in MILLIMETRES. " +
            "Start with new_part (or new_assembly for multi-part builds). Build the base solid first, then features. " +
            "Prefer simple holes + patterns; verify each step's numbers against the request before emitting. " +
            "Reply with ONLY the JSON object.\n" +
            "Schema: {\"steps\":[{\"tool\":\"<exact tool name>\",\"args\":{...}}]} " +
            "Args use each tool's own parameter names (mm for lengths, degrees for angles). " +
            "Intricate machines legitimately need dozens of steps — use up to 40, one tool call per " +
            "part or feature, no filler steps.";
    }
}
