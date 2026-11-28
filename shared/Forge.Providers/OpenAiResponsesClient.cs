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
    public class OpenAiResponsesClient : IProviderClient
    {
        private static readonly HttpClient Http = CreateClient();
        private static readonly string FallbackSessionId = Guid.NewGuid().ToString("N");
        private static readonly object ToolsCacheGate = new object();
        private static string CachedToolsJson;
        private static JArray CachedToolsArray;
        private readonly string _apiKey;
        private readonly string _baseUrl;
        private readonly string _defaultModel;

        public OpenAiResponsesClient(string apiKey, string baseUrl = "https://opencode.ai/zen/go/v1", string defaultModel = null)
        {
            _apiKey = apiKey ?? "";
            _baseUrl = string.IsNullOrWhiteSpace(baseUrl) ? "https://opencode.ai/zen/go/v1" : baseUrl.TrimEnd('/');
            _defaultModel = defaultModel;
        }

        public string Id { get { return "openai-responses"; } }

        private static HttpClient CreateClient()
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
            return new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        }

        private static void ApplyZenGoHeaders(HttpRequestMessage req, string url, ProviderOptions opts)
        {
            if (string.IsNullOrEmpty(url) || url.IndexOf("/zen/go", StringComparison.OrdinalIgnoreCase) < 0) return;
            string sid = null;
            if (opts != null && !string.IsNullOrWhiteSpace(opts.SessionId)) sid = opts.SessionId;
            if (string.IsNullOrWhiteSpace(sid))
            {
                try { sid = OpenAiCompatibleClient.SessionIdProvider != null ? OpenAiCompatibleClient.SessionIdProvider() : null; } catch { sid = null; }
            }
            if (string.IsNullOrWhiteSpace(sid)) sid = FallbackSessionId;
            req.Headers.TryAddWithoutValidation("x-opencode-session", sid);
            req.Headers.TryAddWithoutValidation("User-Agent", "Forge/1.0 (+SolidWorks add-in)");
        }

        private static void ApplyCustomHeaders(HttpRequestMessage req, ProviderOptions opts)
        {
            if (opts == null || opts.Headers == null) return;
            foreach (var kv in opts.Headers)
            {
                if (string.IsNullOrEmpty(kv.Key)) continue;
                if (string.Equals(kv.Key, "Authorization", StringComparison.OrdinalIgnoreCase))
                    req.Headers.Remove("Authorization");
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

            for (int attempt = 0; ; attempt++)
            {
                HttpRequestMessage req;
                try { req = new HttpRequestMessage(HttpMethod.Post, url); }
                catch (Exception ex) { throw new ProviderException("Provider address looks wrong (bad base URL \"" + url + "\") — check the provider URL in settings, then resend: " + Truncate(ex.Message), ex); }
                using (req)
                {
                    if (!string.IsNullOrEmpty(_apiKey))
                        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _apiKey);
                    ApplyZenGoHeaders(req, url, opts);
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
            return root + "/responses";
        }

        private static int ClampOutputTokens(string model, int requested)
        {
            return ProviderFactory.ClampOutputTokens(model, requested);
        }

        private string NormalizeReasoningEffort(ProviderOptions opts)
        {
            if (opts == null || string.IsNullOrWhiteSpace(opts.ReasoningEffort)) return null;
            string e = opts.ReasoningEffort.Trim().ToLowerInvariant();
            if (e == "none") return null;
            if (e != "low" && e != "medium" && e != "high" && e != "xhigh" && e != "max") return null;
            return e;
        }

        private string BuildBody(IReadOnlyList<ChatMessage> messages, ProviderOptions opts, string model, bool stream = false)
        {
            var j = new JObject();
            j["model"] = model;
            var arr = new JArray();
            foreach (var m in messages)
            {
                if (m == null || m.Content == null) continue;
                arr.Add(new JObject { ["role"] = string.IsNullOrEmpty(m.Role) ? "user" : m.Role, ["content"] = m.Content });
            }
            if (arr.Count == 0)
                throw new ProviderException("Provider request has no user/assistant messages to send.");
            j["input"] = arr;
            if (opts != null)
            {
                if (opts.MaxTokens.HasValue)
                    j["max_output_tokens"] = ClampOutputTokens(model, opts.MaxTokens.Value);
                if (opts.Temperature.HasValue && ProviderFactory.SupportsTemperature(model)) j["temperature"] = opts.Temperature.Value;
                string effort = NormalizeReasoningEffort(opts);
                if (effort != null)
                    j["reasoning"] = new JObject { ["effort"] = effort, ["summary"] = "auto" };
                if (!string.IsNullOrWhiteSpace(opts.ToolsJson))
                {
                    j["tools"] = GetCachedToolsArray(opts.ToolsJson);
                    j["tool_choice"] = string.IsNullOrWhiteSpace(opts.ToolChoice) ? "auto" : opts.ToolChoice;
                }
            }
            if (stream) j["stream"] = true;
            return j.ToString(Newtonsoft.Json.Formatting.None);
        }

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
            var shaped = new JArray();
            foreach (var tok in parsed)
            {
                var o = tok as JObject;
                if (o == null) continue;
                var fn = o["function"] as JObject;
                if (fn == null) { shaped.Add((JToken)o.DeepClone()); continue; }
                var t = new JObject { ["type"] = "function" };
                if (fn["name"] != null) t["name"] = (JToken)fn["name"].DeepClone();
                if (fn["description"] != null) t["description"] = (JToken)fn["description"].DeepClone();
                if (fn["parameters"] != null) t["parameters"] = (JToken)fn["parameters"].DeepClone();
                shaped.Add(t);
            }
            lock (ToolsCacheGate)
            {
                CachedToolsJson = toolsJson;
                CachedToolsArray = (JArray)shaped.DeepClone();
            }
            return shaped;
        }

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

        private static void AppendCapped(StringBuilder sb, string s, int cap)
        {
            if (string.IsNullOrEmpty(s)) return;
            if (sb.Length >= cap) return;
            sb.Append(s.Length + sb.Length > cap ? s.Substring(0, cap - sb.Length) : s);
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
            bool lengthTruncated = false;

            HttpResponseMessage resp = null;
            for (int attempt = 0; ; attempt++)
            {
                HttpRequestMessage req0;
                try { req0 = new HttpRequestMessage(HttpMethod.Post, url); }
                catch (Exception ex) { throw new ProviderException("Provider address looks wrong (bad base URL \"" + url + "\") — check the provider URL in settings, then resend: " + Truncate(ex.Message), ex); }
                if (!string.IsNullOrEmpty(_apiKey))
                    req0.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _apiKey);
                ApplyZenGoHeaders(req0, url, opts);
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
            string firstNonData = null;
            string evtType = null;
            using (resp)
            {
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
                            if (data == "[DONE]") break;
                            if (pendingFrag != null) { data = pendingFrag + data; pendingFrag = null; }
                            JObject evt;
                            try { evt = JObject.Parse(data); }
                            catch
                            {
                                pendingFrag = data.Length < 100000 ? data : null;
                                continue;
                            }
                            try
                            {
                                string type = (string)evt["type"];
                                if (string.IsNullOrEmpty(type)) type = evtType;
                                string mn = (string)evt["model"];
                                if (!string.IsNullOrWhiteSpace(mn)) gotModel = mn;
                                var errorEnv = evt["error"];
                                if ((string.Equals(type, "error", StringComparison.OrdinalIgnoreCase) || string.Equals(evtType, "error", StringComparison.OrdinalIgnoreCase)) && errorEnv != null && errorEnv.Type != JTokenType.Null)
                                {
                                    string em = (errorEnv as JObject) != null ? (string)((JObject)errorEnv)["message"] : null;
                                    string ecode = (errorEnv as JObject) != null ? (string)((JObject)errorEnv)["code"] : null;
                                    throw new ProviderException("Provider stream error" + (string.IsNullOrWhiteSpace(ecode) ? "" : " [" + ecode.Trim() + "]") + " (" + Truncate(em ?? errorEnv.ToString()) + ") — try again; if it repeats, switch model.");
                                }
                                if (errorEnv != null && errorEnv.Type != JTokenType.Null && evt["output"] == null && evt["choices"] == null && evt["response"] == null && string.IsNullOrEmpty(type))
                                {
                                    string em2 = (errorEnv as JObject) != null ? (string)((JObject)errorEnv)["message"] : null;
                                    throw new ProviderException("Provider stream error (" + Truncate(em2 ?? errorEnv.ToString()) + ") — try again; if it repeats, switch model.");
                                }
                                var choices = evt["choices"] as JArray;
                                if (choices != null && choices.Count > 0)
                                {
                                    var choice = choices[0];
                                    string fr = (string)choice["finish_reason"];
                                    if (!string.IsNullOrEmpty(fr)) finishReason = fr;
                                    JObject delta = choice["delta"] as JObject;
                                    JObject msgObj = choice["message"] as JObject;
                                    JObject src = delta ?? msgObj;
                                    if (src != null)
                                    {
                                        string dc = (string)src["content"];
                                        string dr = (string)src["reasoning_content"] ?? (string)src["reasoning"];
                                        if (!string.IsNullOrEmpty(dc)) { AppendCapped(contentAcc, dc, 200000); try { if (onDelta != null) onDelta("content", dc); } catch { } }
                                        if (!string.IsNullOrEmpty(dr)) { AppendCapped(reasonAcc, dr, 100000); try { if (onDelta != null) onDelta("reasoning", dr); } catch { } }
                                        var tcs = src["tool_calls"] as JArray;
                                        if (tcs != null)
                                        {
                                            foreach (var tc in tcs)
                                            {
                                                var o = tc as JObject; if (o == null) continue;
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
                                                        string frag = argTok.Type == JTokenType.String ? (string)argTok : argTok.ToString(Newtonsoft.Json.Formatting.None);
                                                        if (frag != null && (part.ArgumentsJson == null || part.ArgumentsJson.Length < 100000)) part.ArgumentsJson = (part.ArgumentsJson ?? "") + frag;
                                                    }
                                                }
                                            }
                                        }
                                    }
                                    continue;
                                }
                                if (string.Equals(type, "response.output_text.delta", StringComparison.OrdinalIgnoreCase))
                                {
                                    string s = (string)evt["delta"] ?? (string)evt["text"];
                                    if (!string.IsNullOrEmpty(s)) { AppendCapped(contentAcc, s, 200000); try { if (onDelta != null) onDelta("content", s); } catch { } }
                                }
                                else if (string.Equals(type, "response.reasoning_summary_text.delta", StringComparison.OrdinalIgnoreCase))
                                {
                                    string s = (string)evt["delta"] ?? (string)evt["text"];
                                    if (!string.IsNullOrEmpty(s)) { AppendCapped(reasonAcc, s, 100000); try { if (onDelta != null) onDelta("reasoning", s); } catch { } }
                                }
                                else if (string.Equals(type, "response.output_text.done", StringComparison.OrdinalIgnoreCase))
                                {
                                    string s = (string)evt["text"];
                                    if (!string.IsNullOrEmpty(s) && contentAcc.Length == 0) { AppendCapped(contentAcc, s, 200000); try { if (onDelta != null) onDelta("content", s); } catch { } }
                                }
                                else if (string.Equals(type, "response.reasoning_summary_text.done", StringComparison.OrdinalIgnoreCase))
                                {
                                    string s = (string)evt["text"];
                                    if (!string.IsNullOrEmpty(s) && reasonAcc.Length == 0) { AppendCapped(reasonAcc, s, 100000); try { if (onDelta != null) onDelta("reasoning", s); } catch { } }
                                }
                                else if (string.Equals(type, "response.output_item.added", StringComparison.OrdinalIgnoreCase))
                                {
                                    var item = evt["item"] as JObject;
                                    if (item != null && string.Equals((string)item["type"], "function_call", StringComparison.OrdinalIgnoreCase))
                                    {
                                        int idx = (int?)evt["output_index"] ?? 0;
                                        ToolCall part;
                                        if (!toolParts.TryGetValue(idx, out part)) { part = new ToolCall(); toolParts[idx] = part; }
                                        string cid = (string)item["call_id"] ?? (string)item["id"];
                                        if (!string.IsNullOrEmpty(cid)) part.Id = cid;
                                        string inm = (string)item["name"];
                                        if (!string.IsNullOrEmpty(inm)) part.Name = inm;
                                        if (part.ArgumentsJson == null) part.ArgumentsJson = "";
                                    }
                                }
                                else if (string.Equals(type, "response.function_call_arguments.delta", StringComparison.OrdinalIgnoreCase))
                                {
                                    int idx = (int?)evt["output_index"] ?? 0;
                                    string frag = (string)evt["delta"];
                                    if (!string.IsNullOrEmpty(frag))
                                    {
                                        ToolCall part;
                                        if (!toolParts.TryGetValue(idx, out part)) { part = new ToolCall { ArgumentsJson = "" }; toolParts[idx] = part; }
                                        string existing = part.ArgumentsJson ?? "";
                                        if (existing.Length < 100000)
                                            part.ArgumentsJson = existing + (frag.Length > 100000 - existing.Length ? frag.Substring(0, 100000 - existing.Length) : frag);
                                    }
                                }
                                else if (string.Equals(type, "response.output_item.done", StringComparison.OrdinalIgnoreCase))
                                {
                                    var item = evt["item"] as JObject;
                                    if (item != null && string.Equals((string)item["type"], "function_call", StringComparison.OrdinalIgnoreCase))
                                    {
                                        int idx = (int?)evt["output_index"] ?? 0;
                                        ToolCall part;
                                        if (!toolParts.TryGetValue(idx, out part)) { part = new ToolCall(); toolParts[idx] = part; }
                                        string cid = (string)item["call_id"] ?? (string)item["id"];
                                        if (!string.IsNullOrEmpty(cid)) part.Id = cid;
                                        string inm = (string)item["name"];
                                        if (!string.IsNullOrEmpty(inm)) part.Name = inm;
                                        string args = (string)item["arguments"];
                                        if (args != null) part.ArgumentsJson = args;
                                    }
                                }
                                else if (string.Equals(type, "response.completed", StringComparison.OrdinalIgnoreCase))
                                {
                                    var respObj = evt["response"] as JObject;
                                    if (respObj == null) continue;
                                    string rmn = (string)respObj["model"];
                                    if (!string.IsNullOrWhiteSpace(rmn)) gotModel = rmn;
                                    try
                                    {
                                        var usage = respObj["usage"];
                                        if (usage != null)
                                        {
                                            tokIn = (int?)usage["input_tokens"] ?? (int?)usage["prompt_tokens"] ?? tokIn;
                                            tokOut = (int?)usage["output_tokens"] ?? (int?)usage["completion_tokens"] ?? tokOut;
                                        }
                                    }
                                    catch { }
                                    string status = (string)respObj["status"];
                                    if (string.Equals(status, "incomplete", StringComparison.OrdinalIgnoreCase))
                                    {
                                        var det = respObj["incomplete_details"] as JObject;
                                        string reason = det != null ? (string)det["reason"] : null;
                                        if (string.Equals(reason, "max_output_tokens", StringComparison.OrdinalIgnoreCase))
                                            lengthTruncated = true;
                                        else
                                            throw new ProviderException("Provider response incomplete (" + Truncate(reason ?? status) + ") — try again; if it repeats, switch model.");
                                        continue;
                                    }
                                    if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
                                    {
                                        var ferr = respObj["error"] as JObject;
                                        string fem = ferr != null ? (string)ferr["message"] : null;
                                        throw new ProviderException("Provider response failed (" + Truncate(fem ?? "unknown error") + ") — try again; if it repeats, switch model.");
                                    }
                                    var outArr = respObj["output"] as JArray;
                                    if (outArr != null)
                                    {
                                        var tmpContent = new StringBuilder();
                                        var tmpReason = new StringBuilder();
                                        int pos = 0;
                                        foreach (var itemTok in outArr)
                                        {
                                            var io = itemTok as JObject;
                                            if (io == null) { pos++; continue; }
                                            string itype = (string)io["type"];
                                            if (string.Equals(itype, "message", StringComparison.OrdinalIgnoreCase))
                                            {
                                                var outContentArr = io["content"] as JArray;
                                                if (outContentArr != null)
                                                {
                                                    foreach (var cTok in outContentArr)
                                                    {
                                                        var co = cTok as JObject;
                                                        if (co == null) continue;
                                                        string ctType = (string)co["type"];
                                                        if (string.Equals(ctType, "output_text", StringComparison.OrdinalIgnoreCase) || string.Equals(ctType, "refusal", StringComparison.OrdinalIgnoreCase))
                                                        {
                                                            string tt = (string)co["text"];
                                                            if (!string.IsNullOrEmpty(tt)) tmpContent.Append(tt);
                                                        }
                                                    }
                                                }
                                            }
                                            else if (string.Equals(itype, "reasoning", StringComparison.OrdinalIgnoreCase))
                                            {
                                                var sum = io["summary"] as JArray;
                                                if (sum != null)
                                                {
                                                    foreach (var sTok in sum)
                                                    {
                                                        var so = sTok as JObject;
                                                        if (so == null) continue;
                                                        string tt = (string)so["text"];
                                                        if (!string.IsNullOrEmpty(tt)) tmpReason.Append(tt);
                                                    }
                                                }
                                            }
                                            else if (string.Equals(itype, "function_call", StringComparison.OrdinalIgnoreCase))
                                            {
                                                ToolCall part;
                                                if (!toolParts.TryGetValue(pos, out part)) { part = new ToolCall(); toolParts[pos] = part; }
                                                string cid = (string)io["call_id"] ?? (string)io["id"];
                                                if (!string.IsNullOrEmpty(cid)) part.Id = cid;
                                                string inm = (string)io["name"];
                                                if (!string.IsNullOrEmpty(inm)) part.Name = inm;
                                                string args = (string)io["arguments"];
                                                if (args != null) part.ArgumentsJson = args;
                                                else if (part.ArgumentsJson == null) part.ArgumentsJson = "";
                                            }
                                            pos++;
                                        }
                                        if (contentAcc.Length == 0 && tmpContent.Length > 0)
                                        {
                                            string s = tmpContent.ToString();
                                            AppendCapped(contentAcc, s, 200000);
                                            try { if (onDelta != null) onDelta("content", s); } catch { }
                                        }
                                        if (reasonAcc.Length == 0 && tmpReason.Length > 0)
                                        {
                                            string s = tmpReason.ToString();
                                            AppendCapped(reasonAcc, s, 100000);
                                            try { if (onDelta != null) onDelta("reasoning", s); } catch { }
                                        }
                                    }
                                }
                                else if (string.Equals(type, "response.failed", StringComparison.OrdinalIgnoreCase) || string.Equals(type, "response.incomplete", StringComparison.OrdinalIgnoreCase))
                                {
                                    var respObj = evt["response"] as JObject;
                                    string detail = null;
                                    if (respObj != null)
                                    {
                                        var det = respObj["incomplete_details"] as JObject;
                                        detail = det != null ? (string)det["reason"] : (string)respObj["status"];
                                        var ferr = respObj["error"] as JObject;
                                        if (ferr != null && !string.IsNullOrWhiteSpace((string)ferr["message"])) detail = (string)ferr["message"];
                                    }
                                    if (detail != null && detail.Trim().ToLowerInvariant() == "max_output_tokens")
                                        lengthTruncated = true;
                                    else
                                        throw new ProviderException("Provider response " + type + " (" + Truncate(detail ?? type) + ") — try again; if it repeats, switch model.");
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
            string reasoning = reasonAcc.ToString();
            if (lengthTruncated || string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(content) && !string.IsNullOrWhiteSpace(reasoning))
                    throw new ProviderException("Response truncated at the output-token limit while thinking — partial reasoning discarded. Retry or shorten the conversation.");
                if (string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase))
                    throw new ProviderException("Response was truncated at the output-token limit (finish_reason=length).");
                throw new ProviderException("Response was truncated at the output-token limit.");
            }
            if (string.IsNullOrWhiteSpace(content)) content = reasoning;
            var ordered = new List<int>(toolParts.Keys);
            ordered.Sort();
            var calls = new List<ToolCall>(ordered.Count);
            foreach (int k in ordered) calls.Add(toolParts[k]);
            if (string.IsNullOrWhiteSpace(content) && calls.Count == 0)
                throw new ProviderException("Provider returned no content" + (string.IsNullOrEmpty(firstNonData) ? "" : " (detail: " + Truncate(firstNonData) + ")") + " — try again or rephrase.");
            return new ProviderResult { Text = content, Reasoning = string.IsNullOrEmpty(reasoning) ? null : reasoning, TokIn = tokIn, TokOut = tokOut, Model = gotModel ?? model, ToolCalls = calls };
        }

        private static ProviderResult Parse(string text, string fallbackModel)
        {
            JObject j;
            try { j = JObject.Parse(text); }
            catch (Exception ex) { throw new ProviderException("Provider returned an unreadable reply — try again; if it repeats, switch model. (detail: " + Truncate(text) + ")", ex); }

            var apiErr = j["error"];
            if (apiErr != null && apiErr.Type != JTokenType.Null)
            {
                var apiErrObj = apiErr as JObject;
                string em0 = apiErrObj != null ? (string)apiErrObj["message"] : null;
                if (em0 == null && apiErr.Type == JTokenType.String) em0 = (string)apiErr;
                throw new ProviderException("Provider error (" + Truncate(em0 ?? apiErr.ToString()) + ") — try again; if it repeats, switch model.");
            }

            var choices = j["choices"] as JArray;
            if (j["output"] == null && choices != null && choices.Count > 0)
            {
                var choice = choices[0];
                string content = (string)choice["message"]?["content"];
                if (string.IsNullOrWhiteSpace(content))
                {
                    var parts = choice["message"]?["content"] as JArray;
                    if (parts != null)
                    {
                        var sbp = new StringBuilder();
                        foreach (var p in parts)
                        {
                            string pt = (string)(p as JObject)?["text"];
                            if (!string.IsNullOrEmpty(pt)) sbp.Append(pt);
                        }
                        if (sbp.Length > 0) content = sbp.ToString();
                    }
                }
                string reasoning = (string)choice["message"]?["reasoning_content"] ?? (string)choice["message"]?["reasoning"];
                if (string.IsNullOrWhiteSpace(content)) content = reasoning;
                if (string.IsNullOrWhiteSpace(content)) content = (string)choice["text"];
                var chatCalls = new List<ToolCall>();
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
                            chatCalls.Add(new ToolCall
                            {
                                Id = (string)o["id"],
                                Name = fn != null ? (string)fn["name"] : null,
                                ArgumentsJson = argStr ?? ""
                            });
                        }
                }
                catch { }
                if (string.IsNullOrWhiteSpace(content) && chatCalls.Count == 0)
                    throw new ProviderException("Provider returned no content — try again or rephrase. (detail: " + Truncate(text) + ")");
                long cIn = 0, cOut = 0;
                try
                {
                    cIn = (long?)(j["usage"]?["prompt_tokens"]) ?? 0;
                    cOut = (long?)(j["usage"]?["completion_tokens"]) ?? 0;
                }
                catch { }
                return new ProviderResult
                {
                    Text = content ?? "",
                    Reasoning = string.IsNullOrEmpty(reasoning) ? null : reasoning,
                    TokIn = cIn,
                    TokOut = cOut,
                    Model = (string)j["model"] ?? fallbackModel,
                    ToolCalls = chatCalls.Count > 0 ? chatCalls : null
                };
            }

            JObject resp = j["response"] as JObject;
            if (resp == null) resp = j;
            string status = (string)resp["status"];
            if (string.Equals(status, "incomplete", StringComparison.OrdinalIgnoreCase))
            {
                var det = resp["incomplete_details"] as JObject;
                string reason = det != null ? (string)det["reason"] : null;
                if (string.Equals(reason, "max_output_tokens", StringComparison.OrdinalIgnoreCase))
                    throw new ProviderException("Response was truncated at the output-token limit.");
                throw new ProviderException("Provider response incomplete (" + Truncate(reason ?? status) + ") — try again; if it repeats, switch model.");
            }
            if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
            {
                var ferr = resp["error"] as JObject;
                string fem = ferr != null ? (string)ferr["message"] : null;
                throw new ProviderException("Provider response failed (" + Truncate(fem ?? "unknown error") + ") — try again; if it repeats, switch model.");
            }

            var sb = new StringBuilder();
            var think = new StringBuilder();
            var toolCalls = new List<ToolCall>();
            var output = resp["output"] as JArray;
            if (output != null)
            {
                foreach (var itemTok in output)
                {
                    var io = itemTok as JObject;
                    if (io == null) continue;
                    string itype = (string)io["type"];
                    if (string.Equals(itype, "message", StringComparison.OrdinalIgnoreCase))
                    {
                        var outContentArr = io["content"] as JArray;
                        if (outContentArr != null)
                        {
                            foreach (var cTok in outContentArr)
                            {
                                var co = cTok as JObject;
                                if (co == null) continue;
                                string ctType = (string)co["type"];
                                if (string.Equals(ctType, "output_text", StringComparison.OrdinalIgnoreCase) || string.Equals(ctType, "refusal", StringComparison.OrdinalIgnoreCase))
                                {
                                    string tt = (string)co["text"];
                                    if (!string.IsNullOrEmpty(tt)) sb.Append(tt);
                                }
                            }
                        }
                    }
                    else if (string.Equals(itype, "reasoning", StringComparison.OrdinalIgnoreCase))
                    {
                        var sum = io["summary"] as JArray;
                        if (sum != null)
                        {
                            foreach (var sTok in sum)
                            {
                                var so = sTok as JObject;
                                if (so == null) continue;
                                string tt = (string)so["text"];
                                if (!string.IsNullOrEmpty(tt)) think.Append(tt);
                            }
                        }
                    }
                    else if (string.Equals(itype, "function_call", StringComparison.OrdinalIgnoreCase))
                    {
                        toolCalls.Add(new ToolCall
                        {
                            Id = (string)io["call_id"] ?? (string)io["id"],
                            Name = (string)io["name"],
                            ArgumentsJson = (string)io["arguments"] ?? ""
                        });
                    }
                }
            }
            string outText = sb.Length > 0 ? sb.ToString() : (think.Length > 0 ? think.ToString() : null);
            if (string.IsNullOrEmpty(outText) && toolCalls.Count == 0)
                throw new ProviderException("Provider returned no content — try again or rephrase. (detail: " + Truncate(text) + ")");

            long tokIn = 0, tokOut = 0;
            try
            {
                var usage = resp["usage"];
                if (usage != null)
                {
                    tokIn = (long?)usage["input_tokens"] ?? (long?)usage["prompt_tokens"] ?? 0;
                    tokOut = (long?)usage["output_tokens"] ?? (long?)usage["completion_tokens"] ?? 0;
                }
            }
            catch { }

            return new ProviderResult
            {
                Text = outText ?? "",
                Reasoning = think.Length > 0 ? think.ToString() : null,
                TokIn = tokIn,
                TokOut = tokOut,
                Model = (string)resp["model"] ?? fallbackModel,
                ToolCalls = toolCalls.Count > 0 ? toolCalls : null
            };
        }

        private static string Truncate(string s)
        {
            if (s == null) return "";
            return s.Length > 500 ? s.Substring(0, 500) + "…" : s;
        }

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
