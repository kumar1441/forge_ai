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
                throw new ProviderException("No model configured for provider " + Id + " — set \"model\" in config.json or pass ProviderOptions.Model.");

            string url;
            string body;
            try { url = BuildUrl(opts); body = BuildBody(messages, opts, model); }
            catch (ProviderException) { throw; }
            catch (Exception ex) { throw new ProviderException("Provider request failed to build (" + ex.GetType().Name + "): " + ex.Message, ex); }

            // Transient provider overload (429/5xx incl. Anthropic-style 529) retries with backoff
            // instead of failing the run on the first busy signal. Non-transient errors throw at once.
            // (A fresh HttpRequestMessage per attempt: Framework HttpClient won't resend one.)
            for (int attempt = 0; ; attempt++)
            {
                HttpRequestMessage req;
                try { req = new HttpRequestMessage(HttpMethod.Post, url); }
                catch (Exception ex) { throw new ProviderException("Provider misconfigured (bad base URL \"" + url + "\"): " + ex.Message, ex); }
                using (req)
                {
                    if (!string.IsNullOrEmpty(_apiKey))
                        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _apiKey);
                    req.Content = new StringContent(body, Encoding.UTF8, "application/json");

                    HttpResponseMessage resp;
                    try { resp = await Http.SendAsync(req, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (TaskCanceledException) { throw new ProviderException("Provider request timed out."); }
                    catch (HttpRequestException ex) { throw new ProviderException("Provider network failure: " + ex.Message, ex); }
                    catch (InvalidOperationException ex) { throw new ProviderException("Provider misconfigured (bad base URL \"" + url + "\"): " + ex.Message, ex); }
                    catch (UriFormatException ex) { throw new ProviderException("Provider misconfigured (bad base URL \"" + url + "\"): " + ex.Message, ex); }

                    using (resp)
                    {
                        string text;
                        try { text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false); }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch (OperationCanceledException) { throw new ProviderException("Provider request timed out."); }
                        if (resp.IsSuccessStatusCode)
                            return Parse(text, model);
                        int code = (int)resp.StatusCode;
                        bool transient = code == 408 || code == 429 || code == 500 || code == 502 || code == 503 || code == 504 || code == 529;
                        if (!transient || attempt >= 3)
                            throw new ProviderException("Provider HTTP " + code + " (" + resp.StatusCode + "): " + Truncate(text));
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
                if (opts.MaxTokens.HasValue) j["max_tokens"] = opts.MaxTokens.Value;
                if (opts.Temperature.HasValue) j["temperature"] = opts.Temperature.Value;
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
            catch (Exception ex) { throw new ProviderException("Invalid ToolsJson (not a JSON array): " + ex.Message, ex); }
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
                throw new ProviderException("No model configured for provider " + Id + " — set \"model\" in config.json or pass ProviderOptions.Model.");
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
                throw new ProviderException("No model configured for provider " + Id + " — set \"model\" in config.json or pass ProviderOptions.Model.");
            var j = new JObject { ["model"] = model, ["messages"] = messages };
            if (opts != null)
            {
                if (opts.MaxTokens.HasValue) j["max_tokens"] = opts.MaxTokens.Value;
                if (opts.Temperature.HasValue) j["temperature"] = opts.Temperature.Value;
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
                catch (Exception ex) { throw new ProviderException("Provider misconfigured (bad base URL \"" + url + "\"): " + ex.Message, ex); }
                if (!string.IsNullOrEmpty(_apiKey))
                    req0.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _apiKey);
                req0.Content = new StringContent(body, Encoding.UTF8, "application/json");
                try { resp = await Http.SendAsync(req0, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { try { req0.Dispose(); } catch { } throw; }
                catch (TaskCanceledException) { try { req0.Dispose(); } catch { } throw new ProviderException("Provider request timed out."); }
                catch (HttpRequestException ex) { try { req0.Dispose(); } catch { } throw new ProviderException("Provider network failure: " + ex.Message, ex); }
                catch (InvalidOperationException ex) { try { req0.Dispose(); } catch { } throw new ProviderException("Provider misconfigured (bad base URL \"" + url + "\"): " + ex.Message, ex); }
                catch (UriFormatException ex) { try { req0.Dispose(); } catch { } throw new ProviderException("Provider misconfigured (bad base URL \"" + url + "\"): " + ex.Message, ex); }
                try { req0.Dispose(); } catch { } // sent: release it on every path (success + retries)
                if (resp.IsSuccessStatusCode) break;
                string t = "";
                try { t = await resp.Content.ReadAsStringAsync().ConfigureAwait(false); } catch { }
                int code = (int)resp.StatusCode;
                try { resp.Dispose(); } catch { }
                resp = null;
                bool transient = code == 408 || code == 429 || code == 500 || code == 502 || code == 503 || code == 504 || code == 529;
                if (!transient || attempt >= 3)
                    throw new ProviderException("Provider HTTP " + code + " (" + ((System.Net.HttpStatusCode)code) + "): " + Truncate(t));
                try { await Task.Delay(TimeSpan.FromSeconds(5 * (attempt + 1)), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            }

            using (resp)
            {
                    using (var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    // Cancel must break a stalled mid-stream read: ReadLineAsync takes no token,
                    // so disposing the response on cancel is what unblocks the pending read.
                    using (ct.Register(() => { try { resp.Dispose(); } catch { } }))
                    {
                        string line;
                        try
                        {
                        while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                        {
                            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                            line = line.Trim();
                            if (line.Length < 5 || !line.StartsWith("data:")) continue;
                            string data = line.Substring(5).Trim();
                            if (data == "[DONE]") break;
                            JObject evt;
                            try { evt = JObject.Parse(data); }
                            catch { continue; }
                            // 200-then-error mid-stream: without this the error event is skipped
                            // as delta-less and the run ends with misleading "no content".
                            var streamErr = evt["error"];
                            if (streamErr != null && streamErr.Type != JTokenType.Null)
                            {
                                string em = (streamErr as JObject) != null ? (string)((JObject)streamErr)["message"] : null;
                                throw new ProviderException("Provider stream error: " + (em ?? streamErr.ToString()));
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
                            if (!string.IsNullOrEmpty(dc)) { contentAcc.Append(dc); try { if (onDelta != null) onDelta("content", dc); } catch { } }
                            if (!string.IsNullOrEmpty(dr)) { reasonAcc.Append(dr); try { if (onDelta != null) onDelta("reasoning", dr); } catch { } }
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
                                            if (frag != null) part.ArgumentsJson = (part.ArgumentsJson ?? "") + frag;
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
                throw new ProviderException("Provider returned no content.");
            return new ProviderResult { Text = content, TokIn = tokIn, TokOut = tokOut, Model = gotModel ?? model, ToolCalls = calls };
        }

        private static ProviderResult Parse(string text, string fallbackModel)
        {
            JObject j;
            try { j = JObject.Parse(text); }
            catch (Exception ex) { throw new ProviderException("Provider returned non-JSON: " + Truncate(text), ex); }

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
                throw new ProviderException("Provider returned no content. " + Truncate(text));

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
            return s.Length > 500 ? s.Substring(0, 500) : s;
        }
    }
}
