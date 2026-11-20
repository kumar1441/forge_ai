using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
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
        public static long AgentTokCount { get; private set; }

        public static void EnsureSessionPublic(string sid) { EnsureSession(sid); }

        private static string AgentDir()
        {
            string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Forge", "agent");
            try { Directory.CreateDirectory(d); } catch { }
            return d;
        }

        private static AgentSession EnsureSession(string sid)
        {
            if (string.IsNullOrWhiteSpace(sid)) sid = "default";
            lock (Sessions)
            {
                AgentSession s;
                if (Sessions.TryGetValue(sid, out s)) { CurrentSid = sid; return s; }
                s = new AgentSession();
                try
                {
                    string p = Path.Combine(AgentDir(), sid + ".json");
                    if (File.Exists(p))
                    {
                        var arr = JArray.Parse(File.ReadAllText(p));
                        foreach (var t in arr) { var o = t as JObject; if (o != null) s.Msgs.Add(o); }
                        // Rebuild turn boundaries (user-role entries) since only messages persist.
                        for (int i = 0; i < s.Msgs.Count; i++)
                            if ((string)s.Msgs[i]["role"] == "user") s.TurnStarts.Add(i);
                    }
                }
                catch { }
                Sessions[sid] = s;
                CurrentSid = sid;
                return s;
            }
        }

        private static void SaveSession(string sid)
        {
            try
            {
                AgentSession s;
                lock (Sessions) { if (!Sessions.TryGetValue(sid ?? "default", out s)) return; }
                var arr = new JArray();
                lock (s.Msgs) { foreach (var m in s.Msgs) arr.Add(m); }
                File.WriteAllText(Path.Combine(AgentDir(), (sid ?? "default") + ".json"), arr.ToString(Newtonsoft.Json.Formatting.None));
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
            lock (Sessions) { Sessions.Clear(); }
            CurrentSid = null;
            AgentMsgCount = 0;
        }

        public static int HistoryCount()
        {
            try
            {
                lock (Sessions)
                {
                    AgentSession s;
                    if (CurrentSid != null && Sessions.TryGetValue(CurrentSid, out s)) return s.Msgs.Count;
                }
            }
            catch { }
            return 0;
        }

        /// <summary>Revert a chat to its first keep user-turns (panel counts turns, host maps to messages).</summary>
        public static void TruncateHistory(string sid, int keepTurns)
        {
            if (string.IsNullOrWhiteSpace(sid)) return;
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
                    s.Msgs.RemoveRange(cut, s.Msgs.Count - cut);
                    s.TurnStarts.RemoveRange(keepTurns, s.TurnStarts.Count - keepTurns);
                }
            }
            SaveSession(sid);
        }

        /// <summary>Summarize (never silently drop) the oldest half once history passes 60 messages.</summary>
        private static async Task CompactHistoryIfNeeded(IProviderClient client, string sid, string model)
        {
            AgentSession s = EnsureSession(sid);
            int count;
            lock (s.Msgs) { count = s.Msgs.Count; }
            if (count <= 60) return;
            int cut = count - 30; // summarize everything except the newest 30
            List<JObject> old;
            lock (s.Msgs) { old = s.Msgs.GetRange(1, cut - 1); } // keep [0]=system
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
                    System.Threading.CancellationToken.None);
                summary = res != null ? res.Text : null;
            }
            catch { summary = null; }
            lock (s.Msgs)
            {
                if (string.IsNullOrWhiteSpace(summary))
                {
                    // Bounded fallback: drop oldest (already bounded at 60, noted in trace).
                    try { GroundTruth.Trace?.Invoke("mcp-build: history compact fallback, dropped " + (cut - 1) + " msgs"); } catch { }
                    s.Msgs.RemoveRange(1, cut - 1);
                }
                else
                {
                    s.Msgs.RemoveRange(1, cut - 1);
                    s.Msgs.Insert(1, new JObject { ["role"] = "system", ["content"] = "[Earlier work compacted] " + summary.Trim() });
                }
                for (int i = 0; i < s.TurnStarts.Count; i++) s.TurnStarts[i] = Math.Max(0, s.TurnStarts[i] - (cut - 1));
                while (s.TurnStarts.Count > 0 && s.TurnStarts[0] < 0) s.TurnStarts.RemoveAt(0);
            }
            SaveSession(sid);
        }

        private static readonly Regex CreateVerbRx = new Regex(
            @"\b(create|make|build|design|generate|model)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex AssemblyNounRx = new Regex(
            @"\b(assembl(?:y|ies)|sub[\s-]?assembl(?:y|ies))\b",
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
            if (!CreateVerbRx.IsMatch(text)) return false;
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
                if (tools.Count == 0) throw new McpBridge.McpException("tool list came back empty.");
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
                try { r = await McpBridge.CallToolAsync(tool, args); }
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

            string measured = await BestEffortVerify();
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
                if (tools.Count == 0) throw new McpBridge.McpException("tool list came back empty.");
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
            string toolsJson = fns.ToString(Newtonsoft.Json.Formatting.None);
            string model = ProviderFactory.CurrentModel();

            var session = EnsureSession(sid);
            lock (session.Msgs)
            {
                if (session.Msgs.Count == 0 || (string)session.Msgs[0]["role"] != "system")
                    session.Msgs.Insert(0, new JObject { ["role"] = "system", ["content"] = AgentSystemPrompt });
                session.TurnStarts.Add(session.Msgs.Count);
                session.Msgs.Add(new JObject { ["role"] = "user", ["content"] = (intent ?? "").Trim() });
                AgentMsgCount = session.Msgs.Count;
            }
            SaveSession(sid);
            await CompactHistoryIfNeeded(client, sid, model);

            var opts = new ProviderOptions { MaxTokens = 16000, Temperature = 0, Model = model, ToolsJson = toolsJson, ToolChoice = "auto" };
            var log = new List<string>();
            int executed = 0;
            try
            {
                for (int iter = 1; iter <= 20; iter++)
                {
                    ct.ThrowIfCancellationRequested();
                    await emit("Builder", "thinking through the build (step " + iter + ")", "run", null);
                    List<JObject> hist;
                    lock (session.Msgs) { hist = new List<JObject>(session.Msgs); }
                    ProviderResult res = await StreamAgentTurn(client, new JArray(hist.ToArray()), opts, ct, onThink);
                    if (res == null) throw new Exception("empty provider response");
                    try { IntentRate.Note(res.TokIn, res.TokOut); } catch { }
                    AgentTokCount += res.TokIn + res.TokOut;

                    if (res.ToolCalls == null || res.ToolCalls.Count == 0)
                    {
                        if (executed == 0) // model won't use tools: single-shot JSON path instead
                            return await Run(intent, emit, ct, onThink);
                        string summary = (res.Text ?? "").Trim();
                        if (summary.Length > 600) summary = summary.Substring(0, 600) + "…";
                        outcome.Summary = "Built in " + executed + " tool steps" +
                            (string.IsNullOrEmpty(summary) ? "." : ". " + summary);
                        SaveSession(sid);
                        return outcome;
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
                        if (string.IsNullOrWhiteSpace(c.Name) || !names.Contains(c.Name))
                        {
                            lock (session.Msgs) { session.Msgs.Add(new JObject { ["role"] = "tool", ["tool_call_id"] = c.Id, ["content"] = "ERROR: unknown tool '" + c.Name + "'. Use only listed tools." }); }
                            continue;
                        }
                        JObject args;
                        try { args = JObject.Parse(string.IsNullOrWhiteSpace(c.ArgumentsJson) ? "{}" : c.ArgumentsJson); }
                        catch { args = new JObject(); }
                        executed++;
                        await emit("Builder", "step " + executed + ": " + c.Name, "run", null);
                        // Cancellable even mid-flight: an orphaned server call is discarded, never applied twice.
                        var callTask = McpBridge.CallToolAsync(c.Name, args);
                        var finished = await Task.WhenAny(callTask, Task.Delay(-1, ct));
                        if (finished != callTask) throw new OperationCanceledException(ct);
                        McpBridge.ToolResult r;
                        try { r = await callTask; }
                        catch (Exception ex) { r = new McpBridge.ToolResult { Ok = false, Text = "transport failed: " + ex.Message }; }
                        string text = r.Text ?? "";
                        if (text.Length > 2000) text = text.Substring(0, 2000) + "…(truncated)";
                        log.Add(c.Name + " -> " + (r.Ok ? "ok" : "FAILED"));
                        lock (session.Msgs) { session.Msgs.Add(new JObject { ["role"] = "tool", ["tool_call_id"] = c.Id, ["content"] = (r.Ok ? "" : "FAILED: ") + text }); AgentMsgCount = session.Msgs.Count; }
                        if (!r.Ok)
                        {
                            // Don't stop: feed the failure back and let the model re-plan around it.
                            await emit("Builder", c.Name + " missed — re-planning around it", "run", null);
                        }
                    }
                    SaveSession(sid);
                }
                outcome.Error = "Stopped after 20 tool steps with the build incomplete (" +
                    string.Join("; ", log.ToArray()) + ") — what built so far stays open. Simplify or split the request.";
                SaveSession(sid);
                return outcome;
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

        private static async Task<string> BestEffortVerify()
        {
            foreach (string probe in new[] { "get_mass_properties", "get_bounding_box" })
            {
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
