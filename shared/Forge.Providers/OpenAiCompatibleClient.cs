using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Forge.Providers
{
    /// <summary>
    /// Speaks the OpenAI /chat/completions wire format. One client covers every
    /// OpenAI-compatible host: api.openai.com, api.deepseek.com, OpenRouter, local
    /// Ollama / vLLM / LM Studio, etc. — pick the host via the baseUrl constructor arg.
    /// </summary>
    public class OpenAiCompatibleClient : IProviderClient
    {
        private static readonly HttpClient Http = CreateClient();
        // Single-entry parse cache: ToolsJson is identical on ~every agent-loop turn, and
        // JArray.Parse re-tokenizes that same multi-KB string per call. Hit = DeepClone only.
        private static readonly object ToolsCacheGate = new object();
        private static string CachedToolsJson;
        private static JArray CachedToolsArray;
        private readonly string _apiKey;
        private readonly string _baseUrl;
        private readonly string _defaultModel;

        public OpenAiCompatibleClient(string apiKey, string baseUrl = "https://api.openai.com/v1", string defaultModel = null)
        {
            _apiKey = apiKey ?? "";
            _baseUrl = string.IsNullOrWhiteSpace(baseUrl) ? "https://api.openai.com/v1" : baseUrl.TrimEnd('/');
            _defaultModel = defaultModel;
        }

        public string Id { get { return "openai-compatible"; } }

        private static HttpClient CreateClient()
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
            // Thinking models run uncapped (50k+ tokens) — the call stays open as long as they need.
            // Static lifetime is intentional (avoids socket exhaustion); DNS staleness is accepted —
            // a base-URL host change requires process restart. Do not switch to per-call clients.
            return new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        }

        public async Task<ProviderResult> CompleteAsync(IReadOnlyList<ChatMessage> messages, ProviderOptions opts, CancellationToken ct)
        {
            if (messages == null) throw new ArgumentNullException("messages");
            string model = (opts != null && !string.IsNullOrWhiteSpace(opts.Model)) ? opts.Model : _defaultModel;
            if (string.IsNullOrWhiteSpace(model))
                throw new ProviderException("No model selected for " + ProviderFactory.DisplayNameFor(Id, _baseUrl) + " — pick one in the model picker (▲), then resend.");
            string noKey = null;
            try { noKey = ProviderFactory.MissingKeyMessage(Id, _baseUrl); } catch { noKey = null; }
            if (!string.IsNullOrWhiteSpace(noKey) && string.IsNullOrWhiteSpace(_apiKey)) throw new ProviderException(noKey);

            string url;
            string body;
            try { url = BuildUrl(opts); body = BuildBody(messages, opts, model); }
            catch (ProviderException) { throw; }
            catch (Exception ex) { throw new ProviderException("Provider request failed to build (" + Truncate(ex.Message) + ") — rephrase or split the request, then resend.", ex); }

            // Transient provider overload (429/5xx incl. Anthropic-style 529) retries with backoff
            // instead of failing the run on the first busy signal. Non-transient errors throw at once.
            // (A fresh HttpRequestMessage per attempt: Framework HttpClient won't resend one.)
            for (int attempt = 0; ; attempt++)
            {
                HttpRequestMessage req;
                try { req = new HttpRequestMessage(HttpMethod.Post, url); }
                catch (Exception ex) { throw new ProviderException("Provider address looks wrong (bad base URL \"" + url + "\") — check the provider URL in settings, then resend: " + Truncate(ex.Message), ex); }
                using (req)
                {
                    if (!string.IsNullOrEmpty(_apiKey))
                        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _apiKey);
                    req.Content = new StringContent(body, Encoding.UTF8, "application/json");

                    HttpResponseMessage resp;
                    try { resp = await Http.SendAsync(req, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (TaskCanceledException) { throw new ProviderException("Provider request timed out after 10 minutes — the model is overloaded; try again or switch to a faster model."); }
                    catch (HttpRequestException ex) { throw new ProviderException("Provider network failure (" + Truncate(ex.Message) + ") — check the connection and try again.", ex); }
                    catch (InvalidOperationException ex) { throw new ProviderException("Provider address looks wrong (bad base URL \"" + url + "\") — check the provider URL in settings, then resend: " + Truncate(ex.Message), ex); }
                    catch (UriFormatException ex) { throw new ProviderException("Provider address looks wrong (bad base URL \"" + url + "\") — check the provider URL in settings, then resend: " + Truncate(ex.Message), ex); }

                    using (resp)
                    {
                        string text;
                        try { text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false); }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch (OperationCanceledException) { throw new ProviderException("Provider request timed out after 10 minutes — the model is overloaded; try again or switch to a faster model."); }
                        // A hostile/garbled gateway can stream megabytes of non-JSON: bound what we parse.
                        if (text != null && text.Length > 5000000) throw new ProviderException("Provider returned an oversized response (" + text.Length + " chars) — retry or split the request.");
                        if (resp.IsSuccessStatusCode)
                            return Parse(text, model);
                        int code = (int)resp.StatusCode;
                        bool transient = code == 408 || code == 429 || code == 500 || code == 502 || code == 503 || code == 504 || code == 529;
                        if (!transient || attempt >= 3)
                        {
                            if (code == 401 || code == 403) throw new ProviderException(ProviderFactory.AuthRefusedMessage(Id, _baseUrl, code));
                            throw new ProviderException("Provider request failed (" + HttpHint(code, resp.StatusCode) + "): " + Truncate(text) + " — try again; if it repeats, wait and resend or switch model.");
                        }
                        try { await Task.Delay(TimeSpan.FromSeconds(5 * (attempt + 1)), ct).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    }
                }
            }
        }

        private string BuildUrl(ProviderOptions opts)
        {
            string root = _baseUrl;
            if (opts != null && !string.IsNullOrWhiteSpace(opts.BaseUrlOverride))
                root = opts.BaseUrlOverride.TrimEnd('/');
            return root + "/chat/completions";
        }

        // Callers pass large output caps (16k/32k) for planning, but a model's context window bounds
        // what it can actually emit. Clamp to min(requested, window/2) with a 4096 floor so small-window
        // models (e.g. gpt-3.5 at 16k -> 8k) don't 400 on an output cap larger than their context.
        private static int ClampOutputTokens(string model, int requested)
        {
            int window = 128000;
            try { var cw = ProviderFactory.ContextWindowFor(model); if (cw != null && cw.Item1 > 0) window = cw.Item1; } catch { }
            int budget = System.Math.Max(4096, window / 2);
            return requested > 0 ? System.Math.Min(requested, budget) : budget;
        }

        private static void ApplyMaxOutputTokens(JObject j, ProviderOptions opts, string model)
        {
            if (opts == null || !opts.MaxTokens.HasValue) return;
            // Reasoning models reject max_tokens and require max_completion_tokens instead.
            string field = ProviderFactory.IsReasoningModel(model) ? "max_completion_tokens" : "max_tokens";
            j[field] = ClampOutputTokens(model, opts.MaxTokens.Value);
        }

        private static string BuildBody(IReadOnlyList<ChatMessage> messages, ProviderOptions opts, string model, bool stream = false)
        {
            var j = new JObject();
            j["model"] = model;

            var arr = new JArray();
            foreach (var m in messages)
            {
                if (m == null || m.Content == null) continue;
                arr.Add(new JObject { ["role"] = string.IsNullOrEmpty(m.Role) ? "user" : m.Role, ["content"] = m.Content });
            }
            j["messages"] = arr;

            if (opts != null)
            {
                ApplyMaxOutputTokens(j, opts, model);
                // Central gate (callers with hardcoded Temperature=0 still omit it here): reasoning
                // variants reject custom temperatures, so unsupported models send no field at all.
                if (opts.Temperature.HasValue && ProviderFactory.SupportsTemperature(model)) j["temperature"] = opts.Temperature.Value;
                if (!string.IsNullOrWhiteSpace(opts.ToolsJson))
                {
                    // tool_choice without a tools array is a 400 on strict gateways: only send
                    // it when the tools actually attached.
                    j["tools"] = GetCachedToolsArray(opts.ToolsJson);
                    j["tool_choice"] = string.IsNullOrWhiteSpace(opts.ToolChoice) ? "auto" : opts.ToolChoice;
                }
            }
            if (stream) j["stream"] = true;
            return j.ToString(Newtonsoft.Json.Formatting.None);
        }

        // Same-string cache for the ToolsJson array. Keyed by full ordinal string equality
        // (never hash-only: collisions would attach the wrong tools). The cached instance is
        // never handed out — every caller gets a DeepClone — so concurrent bodies cannot steal
        // each other's "tools" node via JToken reparenting. Failures are never cached and throw
        // the exact original message. Misses return the fresh parse directly (no extra clone).
        private static JArray GetCachedToolsArray(string toolsJson)
        {
            lock (ToolsCacheGate)
            {
                if (toolsJson == CachedToolsJson && CachedToolsArray != null)
                    return (JArray)CachedToolsArray.DeepClone();
            }
            JArray parsed;
            try { parsed = JArray.Parse(toolsJson); }
            catch (Exception ex) { throw new ProviderException("The tool list couldn't be read — retry; if it repeats, restart SolidWorks. (detail: " + Truncate(ex.Message) + ")", ex); }
            lock (ToolsCacheGate)
            {
                CachedToolsJson = toolsJson;
                CachedToolsArray = (JArray)parsed.DeepClone();
            }
            return parsed;
        }

        /// <summary>
        /// Streaming chat completions (SSE). onDelta fires per chunk as ("reasoning"|"content", text)
        /// so the UI can print the model's chain-of-thought live, Kilo-style, instead of a dead timer.
        /// Reasoning deltas come from reasoning_content/reasoning; content deltas are the answer draft.
        /// Returns the accumulated result (same multi-shape fallbacks as Parse).
        /// </summary>
        public async Task<ProviderResult> CompleteStreamingAsync(
            IReadOnlyList<ChatMessage> messages, ProviderOptions opts, CancellationToken ct, Action<string, string> onDelta)
        {
            if (messages == null) throw new ArgumentNullException("messages");
            string model = (opts != null && !string.IsNullOrWhiteSpace(opts.Model)) ? opts.Model : _defaultModel;
            if (string.IsNullOrWhiteSpace(model))
                throw new ProviderException("No model selected for " + ProviderFactory.DisplayNameFor(Id, _baseUrl) + " — pick one in the model picker (▲), then resend.");
            string noKeyS = null;
            try { noKeyS = ProviderFactory.MissingKeyMessage(Id, _baseUrl); } catch { noKeyS = null; }
            if (!string.IsNullOrWhiteSpace(noKeyS) && string.IsNullOrWhiteSpace(_apiKey)) throw new ProviderException(noKeyS);
            string body = BuildBody(messages, opts, model, true);
            return await CompleteStreamingBodyAsync(body, model, opts, ct, onDelta);
        }

        /// <summary>
        /// Same SSE stream, but the message array is supplied raw (JArray) so agent-loop turns can
        /// carry tool_calls and tool-role results, which ChatMessage cannot represent.
        /// </summary>
        public async Task<ProviderResult> CompleteStreamingRawAsync(
            JArray messages, ProviderOptions opts, CancellationToken ct, Action<string, string> onDelta)
        {
            if (messages == null) throw new ArgumentNullException("messages");
            string model = (opts != null && !string.IsNullOrWhiteSpace(opts.Model)) ? opts.Model : _defaultModel;
            if (string.IsNullOrWhiteSpace(model))
                throw new ProviderException("No model selected for " + ProviderFactory.DisplayNameFor(Id, _baseUrl) + " — pick one in the model picker (▲), then resend.");
            string noKeyR = null;
            try { noKeyR = ProviderFactory.MissingKeyMessage(Id, _baseUrl); } catch { noKeyR = null; }
            if (!string.IsNullOrWhiteSpace(noKeyR) && string.IsNullOrWhiteSpace(_apiKey)) throw new ProviderException(noKeyR);
            var j = new JObject { ["model"] = model, ["messages"] = messages };
            if (opts != null)
            {
                ApplyMaxOutputTokens(j, opts, model);
                // Central gate (callers with hardcoded Temperature=0 still omit it here): reasoning
                // variants reject custom temperatures, so unsupported models send no field at all.
                if (opts.Temperature.HasValue && ProviderFactory.SupportsTemperature(model)) j["temperature"] = opts.Temperature.Value;
                if (!string.IsNullOrWhiteSpace(opts.ToolsJson))
                {
                    // tool_choice without a tools array is a 400 on strict gateways: only send
                    // it when the tools actually attached.
                    j["tools"] = GetCachedToolsArray(opts.ToolsJson);
                    j["tool_choice"] = string.IsNullOrWhiteSpace(opts.ToolChoice) ? "auto" : opts.ToolChoice;
                }
            }
            j["stream"] = true;
            return await CompleteStreamingBodyAsync(j.ToString(Newtonsoft.Json.Formatting.None), model, opts, ct, onDelta);
        }

        private async Task<ProviderResult> CompleteStreamingBodyAsync(
            string body, string model, ProviderOptions opts, CancellationToken ct, Action<string, string> onDelta)
        {
            string url = BuildUrl(opts);
            var contentAcc = new StringBuilder(8192);
            var reasonAcc = new StringBuilder(4096);
            var toolParts = new System.Collections.Generic.Dictionary<int, ToolCall>();
            string gotModel = null;
            int tokIn = 0, tokOut = 0;

            // Transient overload on the initial response retries with backoff (same policy as unary).
            // A fresh request per attempt: Framework HttpClient won't resend a used message.
            HttpResponseMessage resp = null;
            for (int attempt = 0; ; attempt++)
            {
                HttpRequestMessage req0;
                try { req0 = new HttpRequestMessage(HttpMethod.Post, url); }
                catch (Exception ex) { throw new ProviderException("Provider address looks wrong (bad base URL \"" + url + "\") — check the provider URL in settings, then resend: " + Truncate(ex.Message), ex); }
                if (!string.IsNullOrEmpty(_apiKey))
                    req0.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _apiKey);
                req0.Content = new StringContent(body, Encoding.UTF8, "application/json");
                try { resp = await Http.SendAsync(req0, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { try { req0.Dispose(); } catch { } throw; }
                catch (TaskCanceledException) { try { req0.Dispose(); } catch { } throw new ProviderException("Provider request timed out after 10 minutes — the model is overloaded; try again or switch to a faster model."); }
                catch (HttpRequestException ex) { try { req0.Dispose(); } catch { } throw new ProviderException("Provider network failure (" + Truncate(ex.Message) + ") — check the connection and try again.", ex); }
                catch (InvalidOperationException ex) { try { req0.Dispose(); } catch { } throw new ProviderException("Provider address looks wrong (bad base URL \"" + url + "\") — check the provider URL in settings, then resend: " + Truncate(ex.Message), ex); }
                catch (UriFormatException ex) { try { req0.Dispose(); } catch { } throw new ProviderException("Provider address looks wrong (bad base URL \"" + url + "\") — check the provider URL in settings, then resend: " + Truncate(ex.Message), ex); }
                try { req0.Dispose(); } catch { } // sent: release it on every path (success + retries)
                if (resp.IsSuccessStatusCode) break;
                string t = "";
                try { t = await resp.Content.ReadAsStringAsync().ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { try { resp.Dispose(); } catch { } resp = null; throw; }
                catch { }
                ct.ThrowIfCancellationRequested();
                int code = (int)resp.StatusCode;
                try { resp.Dispose(); } catch { }
                resp = null;
                bool transient = code == 408 || code == 429 || code == 500 || code == 502 || code == 503 || code == 504 || code == 529;
                if (!transient || attempt >= 3)
                {
                    if (code == 401 || code == 403) throw new ProviderException(ProviderFactory.AuthRefusedMessage(Id, _baseUrl, code));
                    throw new ProviderException("Provider request failed (" + HttpHint(code, (System.Net.HttpStatusCode)code) + "): " + Truncate(t) + " — try again; if it repeats, wait and resend or switch model.");
                }
                try { await Task.Delay(TimeSpan.FromSeconds(5 * (attempt + 1)), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            }

            ct.ThrowIfCancellationRequested();
            // Split-frame reassembly: one JSON event may arrive as several data: lines, so a
            // failed parse is stashed once and prepended to the next payload. firstNonData
            // surfaces a non-SSE (HTML/JSON) error body in the final error. Both are read below
            // the using blocks, hence declared here.
            string pendingFrag = null;
            string firstNonData = null;
            using (resp)
            {
                    using (var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    // Cancel must break a stalled mid-stream read: ReadLineAsync takes no token,
                    // so disposing the response on cancel is what unblocks the pending read.
                    // (Installed after ReadAsStreamAsync has returned: a cancel landing in that
                    // narrow window is caught by the per-line check on the first line below.)
                    using (ct.Register(() => { try { resp.Dispose(); } catch { } }))
                    {
                        ct.ThrowIfCancellationRequested();
                        string line;
                        try
                        {
                        while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                        {
                            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                            line = line.Trim().TrimStart((char)0xFEFF);
                            if (line.Length == 0) continue;
                            if (line[0] == ':') continue; // SSE comment / heartbeat
                            if (line == "data") line = "data:";
                            if (line.Length < 5 || !line.StartsWith("data:"))
                            {
                                // Non-SSE error body mid-stream (200-then-HTML/JSON): sniff a
                                // structured error envelope, else stash the head for the final error.
                                if (firstNonData == null && line.Length > 0)
                                    firstNonData = line.Length > 500 ? line.Substring(0, 500) : line;
                                if (line.StartsWith("{") && line.Contains("\"error\""))
                                {
                                    try
                                    {
                                        var rawErr = JObject.Parse(line);
                                        var re = rawErr["error"];
                                if (re != null && re.Type != JTokenType.Null)
                                {
                                    string em2 = (re as JObject) != null ? (string)((JObject)re)["message"] : null;
                                    string code2 = (re as JObject) != null ? (string)((JObject)re)["code"] : null;
                                    throw new ProviderException("Provider stream error" + (string.IsNullOrWhiteSpace(code2) ? "" : " [" + code2.Trim() + "]") + " (" + Truncate(em2 ?? re.ToString()) + ") — try again; if it repeats, switch model.");
                                }
                                    }
                                    catch (ProviderException) { throw; }
                                    catch { }
                                }
                                continue;
                            }
                            string data = line.Substring(5).Trim().TrimStart((char)0xFEFF);
                            if (data.Length == 0) continue; // keepalive
                            if (data == "[DONE]") break;
                            if (pendingFrag != null) { data = pendingFrag + data; pendingFrag = null; }
                            JObject evt;
                            try { evt = JObject.Parse(data); }
                            catch
                            {
                                // Split frame: hold one bounded fragment for the next data: line.
                                // A truncated tail at EOF is discarded; prior deltas are kept.
                                pendingFrag = data.Length < 100000 ? data : null;
                                continue;
                            }
                            // 200-then-error mid-stream: without this the error event is skipped
                            // as delta-less and the run ends with misleading "no content".
                        var streamErr = evt["error"];
                        if (streamErr != null && streamErr.Type != JTokenType.Null)
                        {
                            string em = (streamErr as JObject) != null ? (string)((JObject)streamErr)["message"] : null;
                            string scode = (streamErr as JObject) != null ? (string)((JObject)streamErr)["code"] : null;
                            throw new ProviderException("Provider stream error" + (string.IsNullOrWhiteSpace(scode) ? "" : " [" + scode.Trim() + "]") + " (" + Truncate(em ?? streamErr.ToString()) + ") — try again; if it repeats, switch model.");
                        }
                             try
                             {
                                 string mn = (string)evt["model"];
                                 if (!string.IsNullOrWhiteSpace(mn)) gotModel = mn;
                                 try
                                 {
                                     var usage = evt["usage"];
                                     if (usage != null)
                                     {
                                         // Nullable casts: a non-numeric usage shape must not drop
                                         // this event's deltas below.
                                         tokIn = (int?)usage["prompt_tokens"] ?? tokIn;
                                         tokOut = (int?)usage["completion_tokens"] ?? tokOut;
                                     }
                                 }
                                 catch { }
                                var choices = evt["choices"] as JArray;
                                var choice = choices != null && choices.Count > 0 ? choices[0] : null;
                            // Some gateways emit message (not delta) chunks mid-stream: read both.
                            JObject delta = choice != null ? choice["delta"] as JObject : null;
                            JObject msgObj = choice != null ? choice["message"] as JObject : null;
                            JObject src = delta ?? msgObj;
                            if (src == null) continue;
                            string dc = (string)src["content"];
                            string dr = (string)src["reasoning_content"] ?? (string)src["reasoning"];
                            // Accumulator caps: a runaway stream must degrade to truncation, never OOM.
                            // 200k/100k chars sit far above any valid MaxTokens-bounded turn.
                            if (!string.IsNullOrEmpty(dc)) { if (contentAcc.Length < 200000) contentAcc.Append(dc.Length + contentAcc.Length > 200000 ? dc.Substring(0, 200000 - contentAcc.Length) : dc); try { if (onDelta != null) onDelta("content", dc); } catch { } }
                            if (!string.IsNullOrEmpty(dr)) { if (reasonAcc.Length < 100000) reasonAcc.Append(dr.Length + reasonAcc.Length > 100000 ? dr.Substring(0, 100000 - reasonAcc.Length) : dr); try { if (onDelta != null) onDelta("reasoning", dr); } catch { } }
                            var tcs = src["tool_calls"] as JArray;
                            if (tcs != null)
                            {
                                foreach (var tc in tcs)
                                {
                                    var o = tc as JObject; if (o == null) continue;
                                    // Some gateways omit index or send it as a string: never let a
                                    // bad index drop the whole tool call. Index-less fragments
                                    // belong to the in-flight call, so reuse the highest key.
                                    int idx = -1;
                                    try { idx = (int?)o["index"] ?? -1; } catch { idx = -1; }
                                    if (idx < 0)
                                    {
                                        idx = 0;
                                        foreach (int k in toolParts.Keys) if (k > idx) idx = k;
                                    }
                                    ToolCall part;
                                    if (!toolParts.TryGetValue(idx, out part)) { part = new ToolCall(); toolParts[idx] = part; }
                                    string tid = (string)o["id"]; if (!string.IsNullOrEmpty(tid)) part.Id = tid;
                                    var fn = o["function"] as JObject;
                                    if (fn != null)
                                    {
                                        string fnn = (string)fn["name"]; if (!string.IsNullOrEmpty(fnn)) part.Name = fnn;
                                        JToken argTok = fn["arguments"];
                                        if (argTok != null && argTok.Type != JTokenType.Null)
                                        {
                                            string frag = argTok.Type == JTokenType.String
                                                ? (string)argTok
                                                : argTok.ToString(Newtonsoft.Json.Formatting.None);
                                            // Per-call arg cap: a hostile stream fragment-loop must not pile MBs
                                            // into one call's arguments (the executor rejects oversize args loudly).
                                            if (frag != null && (part.ArgumentsJson == null || part.ArgumentsJson.Length < 100000)) part.ArgumentsJson = (part.ArgumentsJson ?? "") + frag;
                                        }
                                    }
                                }
                            }
                            }
                            catch (ProviderException) { throw; }
                            catch { continue; }
                        }
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch (ObjectDisposedException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
                        catch (IOException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
                    }
                }

            string content = contentAcc.ToString();
            if (string.IsNullOrWhiteSpace(content)) content = reasonAcc.ToString();
            // Order by the streamed index: Dictionary order is not a call order.
            var ordered = new System.Collections.Generic.List<int>(toolParts.Keys);
            ordered.Sort();
            var calls = new System.Collections.Generic.List<ToolCall>(ordered.Count);
            foreach (int k in ordered) calls.Add(toolParts[k]);
            if (string.IsNullOrWhiteSpace(content) && calls.Count == 0)
                throw new ProviderException("Provider returned no content" + (string.IsNullOrEmpty(firstNonData) ? "" : " (detail: " + Truncate(firstNonData) + ")") + " — try again or rephrase.");
            return new ProviderResult { Text = content, TokIn = tokIn, TokOut = tokOut, Model = gotModel ?? model, ToolCalls = calls };
        }

        private static ProviderResult Parse(string text, string fallbackModel)
        {
            JObject j;
            try { j = JObject.Parse(text); }
            catch (Exception ex) { throw new ProviderException("Provider returned an unreadable reply — try again; if it repeats, switch model. (detail: " + Truncate(text) + ")", ex); }

            var choices = j["choices"] as JArray;
            var choice = choices != null && choices.Count > 0 ? choices[0] : null;
            // NIM serves chat, reasoning, and legacy shapes. Accept any of them so every listed
            // model is usable: standard content, DeepSeek-style reasoning_content, alternate
            // reasoning field, then legacy text. Reasoning-only output is still a real answer.
            string content = null;
            var calls = new System.Collections.Generic.List<ToolCall>();
            if (choice != null)
            {
                content = (string)choice["message"]?["content"];
                if (string.IsNullOrWhiteSpace(content))
                {
                    // Some gateways return content as parts [{type:text,text:...}] instead of a string.
                    var parts = choice["message"]?["content"] as JArray;
                    if (parts != null)
                    {
                        var sb = new StringBuilder();
                        foreach (var p in parts)
                        {
                            string t = (string)(p as JObject)?["text"];
                            if (!string.IsNullOrEmpty(t)) sb.Append(t);
                        }
                        if (sb.Length > 0) content = sb.ToString();
                    }
                }
                if (string.IsNullOrWhiteSpace(content)) content = (string)choice["message"]?["reasoning_content"];
                if (string.IsNullOrWhiteSpace(content)) content = (string)choice["message"]?["reasoning"];
                if (string.IsNullOrWhiteSpace(content)) content = (string)choice["text"];
                // Tool-only turn: no text content but the model requested function calls.
                // Without this Parse throws "no content" and the agent loop can never use tools.
                try
                {
                    var rawTcs = choice["message"]?["tool_calls"] as JArray;
                    if (rawTcs != null)
                        foreach (var tc in rawTcs)
                        {
                            var o = tc as JObject; if (o == null) continue;
                            var fn = o["function"] as JObject;
                            JToken argTok = fn != null ? fn["arguments"] : null;
                            string argStr = null;
                            if (argTok != null && argTok.Type != JTokenType.Null)
                                argStr = argTok.Type == JTokenType.String ? (string)argTok : argTok.ToString(Newtonsoft.Json.Formatting.None);
                            calls.Add(new ToolCall
                            {
                                Id = (string)o["id"],
                                Name = fn != null ? (string)fn["name"] : null,
                                ArgumentsJson = argStr ?? ""
                            });
                        }
                }
                catch { }
            }
            if (string.IsNullOrWhiteSpace(content) && calls.Count == 0)
                throw new ProviderException("Provider returned no content — try again or rephrase. (detail: " + Truncate(text) + ")");

            long tokIn = 0, tokOut = 0;
            try
            {
                tokIn = (long?)(j["usage"]?["prompt_tokens"]) ?? 0;
                tokOut = (long?)(j["usage"]?["completion_tokens"]) ?? 0;
            }
            catch { }

            return new ProviderResult
            {
                Text = content ?? "",
                TokIn = tokIn,
                TokOut = tokOut,
                Model = (string)j["model"] ?? fallbackModel,
                ToolCalls = calls.Count > 0 ? calls : null
            };
        }

        private static string Truncate(string s)
        {
            if (s == null) return "";
            return s.Length > 500 ? s.Substring(0, 500) + "…" : s;
        }

        // Plain-language HTTP status: a bare "HTTP 400" tells the user nothing to act on.
        private static string HttpHint(int code, object status)
        {
            try
            {
                switch (code)
                {
                    case 400: return "HTTP 400 bad request (" + status + ") — the provider rejected the parameters";
                    case 404: return "HTTP 404 not found (" + status + ") — the model or endpoint doesn't exist";
                    case 408: return "HTTP 408 timeout (" + status + ") — the provider was too slow";
                    case 413: return "HTTP 413 too large (" + status + ") — the request was too big; split it";
                    case 429: return "HTTP 429 rate-limited (" + status + ") — the provider is busy";
                    case 500: return "HTTP 500 provider error (" + status + ") — the provider failed internally";
                    case 502: return "HTTP 502 bad gateway (" + status + ") — the provider is down";
                    case 503: return "HTTP 503 unavailable (" + status + ") — the provider is overloaded";
                    case 504: return "HTTP 504 gateway timeout (" + status + ") — the provider timed out";
                    case 529: return "HTTP 529 overloaded (" + status + ") — the provider is overloaded";
                    default: return "HTTP " + code + " (" + status + ") — unexpected provider response";
                }
            }
            catch { return "HTTP " + code + " — unexpected provider response"; }
        }
    }
}
