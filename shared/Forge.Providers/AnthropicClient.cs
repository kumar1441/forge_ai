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
    /// Speaks the Anthropic /v1/messages wire format (x-api-key + anthropic-version headers).
    /// System turns are hoisted into the top-level "system" field, as the API requires.
    /// </summary>
    public class AnthropicClient : IProviderClient
    {
        private const string ApiVersion = "2023-06-01";
        private static readonly HttpClient Http = CreateClient();
        private readonly string _apiKey;
        private readonly string _baseUrl;
        private readonly string _defaultModel;

        public AnthropicClient(string apiKey, string baseUrl = "https://api.anthropic.com/v1", string defaultModel = null)
        {
            _apiKey = apiKey ?? "";
            _baseUrl = string.IsNullOrWhiteSpace(baseUrl) ? "https://api.anthropic.com/v1" : baseUrl.TrimEnd('/');
            _defaultModel = defaultModel;
        }

        public string Id { get { return "anthropic"; } }

        private static HttpClient CreateClient()
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
            // Thinking models run uncapped (50k+ tokens) — the call stays open as long as they need.
            // Static lifetime is intentional (avoids socket exhaustion); DNS staleness is accepted —
            // a base-URL host change requires process restart. Do not switch to per-call clients.
            return new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        }

        private static void ApplyCustomHeaders(HttpRequestMessage req, ProviderOptions opts)
        {
            if (opts == null || opts.Headers == null) return;
            foreach (var kv in opts.Headers)
            {
                if (string.IsNullOrEmpty(kv.Key)) continue;
                if (string.Equals(kv.Key, "x-api-key", StringComparison.OrdinalIgnoreCase))
                    req.Headers.Remove("x-api-key");
                req.Headers.TryAddWithoutValidation(kv.Key, kv.Value ?? "");
            }
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

            // Transient overload (429/5xx incl. 529) retries with backoff; fresh message per
            // attempt since Framework HttpClient won't resend one. Non-transient errors throw at once.
            for (int attempt = 0; ; attempt++)
            {
                HttpRequestMessage req;
                try { req = new HttpRequestMessage(HttpMethod.Post, url); }
                catch (Exception ex) { throw new ProviderException("Provider address looks wrong (bad base URL \"" + url + "\") — check the provider URL in settings, then resend: " + Truncate(ex.Message), ex); }
                using (req)
                {
                    if (!string.IsNullOrEmpty(_apiKey))
                        req.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
                    req.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
                    ApplyCustomHeaders(req, opts);
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
                        // Bound what we parse: a garbled gateway must error, never OOM the host.
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

        public async Task<ProviderResult> CompleteStreamingAsync(
            IReadOnlyList<ChatMessage> messages, ProviderOptions opts, CancellationToken ct, System.Action<string, string> onDelta)
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

        private async Task<ProviderResult> CompleteStreamingBodyAsync(
            string body, string model, ProviderOptions opts, CancellationToken ct, Action<string, string> onDelta)
        {
            string url = BuildUrl(opts);
            var contentAcc = new StringBuilder(8192);
            var reasonAcc = new StringBuilder(4096);
            var toolParts = new Dictionary<int, ToolCall>();
            string gotModel = null;
            int tokIn = 0, tokOut = 0;
            string finishReason = null;
            string firstNonData = null;
            string evtType = null;

            HttpResponseMessage resp = null;
            for (int attempt = 0; ; attempt++)
            {
                HttpRequestMessage req0;
                try { req0 = new HttpRequestMessage(HttpMethod.Post, url); }
                catch (Exception ex) { throw new ProviderException("Provider address looks wrong (bad base URL \"" + url + "\") — check the provider URL in settings, then resend: " + Truncate(ex.Message), ex); }
                if (!string.IsNullOrEmpty(_apiKey))
                    req0.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
                req0.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
                ApplyCustomHeaders(req0, opts);
                req0.Content = new StringContent(body, Encoding.UTF8, "application/json");
                try { resp = await Http.SendAsync(req0, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { try { req0.Dispose(); } catch { } throw; }
                catch (TaskCanceledException) { try { req0.Dispose(); } catch { } throw new ProviderException("Provider request timed out after 10 minutes — the model is overloaded; try again or switch to a faster model."); }
                catch (HttpRequestException ex) { try { req0.Dispose(); } catch { } throw new ProviderException("Provider network failure (" + Truncate(ex.Message) + ") — check the connection and try again.", ex); }
                catch (InvalidOperationException ex) { try { req0.Dispose(); } catch { } throw new ProviderException("Provider address looks wrong (bad base URL \"" + url + "\") — check the provider URL in settings, then resend: " + Truncate(ex.Message), ex); }
                catch (UriFormatException ex) { try { req0.Dispose(); } catch { } throw new ProviderException("Provider address looks wrong (bad base URL \"" + url + "\") — check the provider URL in settings, then resend: " + Truncate(ex.Message), ex); }
                try { req0.Dispose(); } catch { }
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
            string pendingFrag = null;
            using (resp)
            using (var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
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
                        if (line[0] == ':') continue;
                        if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
                        {
                            evtType = line.Substring(6).Trim();
                            continue;
                        }
                        if (line == "data") line = "data:";
                        if (line.Length < 5 || !line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                        {
                            if (firstNonData == null && line.Length > 0)
                                firstNonData = line.Length > 500 ? line.Substring(0, 500) : line;
                            continue;
                        }
                        string data = line.Substring(5).Trim().TrimStart((char)0xFEFF);
                        if (data.Length == 0) continue;
                        if (pendingFrag != null) { data = pendingFrag + data; pendingFrag = null; }
                        JObject evt;
                        try { evt = JObject.Parse(data); }
                        catch
                        {
                            pendingFrag = data.Length < 100000 ? data : null;
                            continue;
                        }
                        string type = (string)evt["type"];
                        if (string.IsNullOrEmpty(type) && !string.IsNullOrEmpty(evtType)) type = evtType;
                        if (type == "error")
                        {
                            string em = (string)evt["error"]?["message"] ?? (string)evt["message"];
                            string ec = (string)evt["error"]?["type"];
                            throw new ProviderException("Provider stream error" + (string.IsNullOrWhiteSpace(ec) ? "" : " [" + ec.Trim() + "]") + " (" + Truncate(em ?? evt.ToString()) + ") — try again; if it repeats, switch model.");
                        }
                        if (type == "message_start")
                        {
                            var msg = evt["message"] as JObject;
                            if (msg != null)
                            {
                                string mn = (string)msg["model"];
                                if (!string.IsNullOrWhiteSpace(mn)) gotModel = mn;
                                try
                                {
                                    var usage = msg["usage"];
                                    if (usage != null) tokIn = (int?)usage["input_tokens"] ?? tokIn;
                                }
                                catch { }
                            }
                            continue;
                        }
                        if (type == "content_block_start")
                        {
                            int idx = (int?)evt["index"] ?? 0;
                            var cb = evt["content_block"] as JObject;
                            string bt = cb != null ? (string)cb["type"] : null;
                            if (bt == "tool_use")
                                toolParts[idx] = new ToolCall { Id = (string)cb["id"], Name = (string)cb["name"], ArgumentsJson = "" };
                            continue;
                        }
                        if (type == "content_block_delta")
                        {
                            var delta = evt["delta"] as JObject;
                            if (delta != null)
                            {
                                string dt = (string)delta["type"];
                                string dtx = (string)delta["text"];
                                string dth = (string)delta["thinking"];
                                if (!string.IsNullOrEmpty(dtx))
                                {
                                    if (contentAcc.Length < 200000) contentAcc.Append(dtx.Length + contentAcc.Length > 200000 ? dtx.Substring(0, 200000 - contentAcc.Length) : dtx);
                                    try { if (onDelta != null) onDelta("content", dtx); } catch { }
                                }
                                if (!string.IsNullOrEmpty(dth))
                                {
                                    if (reasonAcc.Length < 100000) reasonAcc.Append(dth.Length + reasonAcc.Length > 100000 ? dth.Substring(0, 100000 - reasonAcc.Length) : dth);
                                    try { if (onDelta != null) onDelta("reasoning", dth); } catch { }
                                }
                                if (dt == "input_json_delta")
                                {
                                    string frag = (string)delta["partial_json"];
                                    if (!string.IsNullOrEmpty(frag))
                                    {
                                        int idx = (int?)evt["index"] ?? 0;
                                        ToolCall part;
                                        if (!toolParts.TryGetValue(idx, out part)) { part = new ToolCall { ArgumentsJson = "" }; toolParts[idx] = part; }
                                        string existing = part.ArgumentsJson ?? "";
                                        if (existing.Length < 100000)
                                            part.ArgumentsJson = existing + (frag.Length > 100000 - existing.Length ? frag.Substring(0, 100000 - existing.Length) : frag);
                                    }
                                }
                            }
                            continue;
                        }
                        if (type == "message_delta")
                        {
                            var d = evt["delta"] as JObject;
                            if (d != null)
                            {
                                string sr = (string)d["stop_reason"];
                                if (!string.IsNullOrEmpty(sr)) finishReason = sr;
                            }
                            try
                            {
                                var usage = evt["usage"];
                                if (usage != null) tokOut = (int?)usage["output_tokens"] ?? tokOut;
                            }
                            catch { }
                            continue;
                        }
                        if (type == "message_stop") break;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (ObjectDisposedException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
                catch (IOException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
            }

            string content = contentAcc.ToString();
            string reasoning = reasonAcc.ToString();
            if (string.Equals(finishReason, "max_tokens", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(content) && !string.IsNullOrWhiteSpace(reasoning))
                    throw new ProviderException("Response truncated at the output-token limit while thinking — partial reasoning discarded. Retry or shorten the conversation.");
                throw new ProviderException("Response was truncated at the output-token limit (stop_reason=max_tokens).");
            }
            if (string.IsNullOrWhiteSpace(content)) content = reasoning;
            var ordered = new List<int>(toolParts.Keys);
            ordered.Sort();
            var calls = new List<ToolCall>(ordered.Count);
            foreach (int k in ordered) calls.Add(toolParts[k]);
            if (string.IsNullOrWhiteSpace(content) && calls.Count == 0)
                throw new ProviderException("Provider returned no content" + (string.IsNullOrEmpty(firstNonData) ? "" : " (detail: " + Truncate(firstNonData) + ")") + " — try again or rephrase.");
            return new ProviderResult
            {
                Text = content,
                Reasoning = string.IsNullOrEmpty(reasoning) ? null : reasoning,
                TokIn = tokIn,
                TokOut = tokOut,
                Model = gotModel ?? model,
                ToolCalls = calls
            };
        }

        private string BuildUrl(ProviderOptions opts)
        {
            string root = _baseUrl;
            if (opts != null && !string.IsNullOrWhiteSpace(opts.BaseUrlOverride))
                root = opts.BaseUrlOverride.TrimEnd('/');
            return root + "/messages";
        }

        private static int ThinkingBudget(string effort)
        {
            if (string.IsNullOrWhiteSpace(effort)) return 0;
            switch (effort.Trim().ToLowerInvariant())
            {
                case "low": return 1024;
                case "medium": return 4096;
                case "high": return 8192;
                case "xhigh": return 16384;
                case "max": return 32768;
                default: return 0;
            }
        }

        private static string BuildBody(IReadOnlyList<ChatMessage> messages, ProviderOptions opts, string model, bool stream = false)
        {
            int maxTokens = ProviderFactory.ClampOutputTokens(model,
                (opts != null && opts.MaxTokens.HasValue) ? opts.MaxTokens.Value : 1024);
            string effort = opts != null ? opts.ReasoningEffort : null;
            int budget = ProviderFactory.SupportsExtendedThinking(model) ? ThinkingBudget(effort) : 0;
            if (budget > 0 && maxTokens < budget + 1024) maxTokens = budget + 1024;
            var j = new JObject();
            j["model"] = model;
            j["max_tokens"] = maxTokens;
            if (budget > 0) j["thinking"] = new JObject { ["type"] = "enabled", ["budget_tokens"] = budget };
            if (opts != null && opts.Temperature.HasValue && budget == 0 && ProviderFactory.SupportsTemperature(model)) j["temperature"] = opts.Temperature.Value;

            var system = new StringBuilder();
            var arr = new JArray();
            foreach (var m in messages)
            {
                if (m == null || m.Content == null) continue;
                string role = (m.Role ?? "user").Trim().ToLowerInvariant();
                if (role == "system")
                {
                    if (system.Length > 0) system.Append("\n");
                    system.Append(m.Content);
                }
                else
                {
                    // Anthropic rejects consecutive same-role messages (400), so merge
                    // runs of user (or assistant) turns instead of sending them back-to-back.
                    string nrole = role == "assistant" ? "assistant" : "user";
                    var last = arr.Count > 0 ? arr[arr.Count - 1] as JObject : null;
                    if (last != null && string.Equals((string)last["role"], nrole, System.StringComparison.Ordinal))
                        last["content"] = (string)last["content"] + "\n" + m.Content;
                    else
                        arr.Add(new JObject { ["role"] = nrole, ["content"] = m.Content });
                }
            }
            if (arr.Count == 0)
                throw new ProviderException("Provider request has no user/assistant messages to send.");
            if (system.Length > 0) j["system"] = system.ToString();
            j["messages"] = arr;
            if (stream) j["stream"] = true;

            return j.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static ProviderResult Parse(string text, string fallbackModel)
        {
            JObject j;
            try { j = JObject.Parse(text); }
            catch (Exception ex) { throw new ProviderException("Provider returned an unreadable reply — try again; if it repeats, switch model. (detail: " + Truncate(text) + ")", ex); }

            // A 200 with an error envelope must surface the message, not "no content".
            var apiErr = j["error"];
            if (apiErr != null && apiErr.Type != JTokenType.Null)
            {
                string em = (apiErr as JObject) != null ? (string)((JObject)apiErr)["message"] : null;
                throw new ProviderException("Provider error (" + Truncate(em ?? apiErr.ToString()) + ") — try again; if it repeats, switch model.");
            }

            var content = j["content"] as JArray;
            // Concatenate every text block: tool_use blocks carry no text and the answer
            // may span several blocks, so first-only drops content.
            // Tool-use-only turns carry no text: collect tool_use blocks so the agent
            // loop can act instead of failing with "no content".
            var sb = new StringBuilder();
            var think = new StringBuilder();
            var toolCalls = new System.Collections.Generic.List<ToolCall>();
            if (content != null)
                foreach (var b in content)
                {
                    var bo = b as JObject;
                    if (bo == null) continue;
                    string t = (string)bo["text"];
                    if (!string.IsNullOrEmpty(t)) sb.Append(t);
                    // Extended-thinking turns carry the reasoning in "thinking" blocks;
                    // keep it as a fallback so a thinking-only stop still returns text
                    // (same fallback the OpenAI client applies to reasoning_content).
                    if (sb.Length == 0 && string.Equals((string)bo["type"], "thinking", System.StringComparison.Ordinal))
                    {
                        string th = (string)bo["thinking"];
                        if (!string.IsNullOrEmpty(th)) think.Append(th);
                    }
                    if ((string)bo["type"] == "tool_use")
                    {
                        JToken input = bo["input"];
                        string argStr = "";
                        try
                        {
                            if (input != null && input.Type != JTokenType.Null)
                                argStr = input.Type == JTokenType.String ? (string)input : input.ToString(Newtonsoft.Json.Formatting.None);
                        }
                        catch { argStr = ""; }
                        toolCalls.Add(new ToolCall { Id = (string)bo["id"], Name = (string)bo["name"], ArgumentsJson = argStr ?? "" });
                    }
                }
            string outText = sb.Length > 0 ? sb.ToString() : (think.Length > 0 ? think.ToString() : null);
            if (string.IsNullOrEmpty(outText) && toolCalls.Count == 0)
                throw new ProviderException("Provider returned no content — try again or rephrase. (detail: " + Truncate(text) + ")");

            long tokIn = 0, tokOut = 0;
            try
            {
                tokIn = (long?)(j["usage"]?["input_tokens"]) ?? 0;
                tokOut = (long?)(j["usage"]?["output_tokens"]) ?? 0;
            }
            catch { }

            return new ProviderResult
            {
                Text = outText ?? "",
                Reasoning = think.Length > 0 ? think.ToString() : null,
                TokIn = tokIn,
                TokOut = tokOut,
                Model = (string)j["model"] ?? fallbackModel,
                ToolCalls = toolCalls.Count > 0 ? toolCalls : null
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
