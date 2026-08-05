using System;
using System.Collections.Generic;
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
            _baseUrl = (baseUrl ?? "").TrimEnd('/');
            _defaultModel = defaultModel;
        }

        public string Id { get { return "anthropic"; } }

        private static HttpClient CreateClient()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            return new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        }

        public async Task<ProviderResult> CompleteAsync(IReadOnlyList<ChatMessage> messages, ProviderOptions opts, CancellationToken ct)
        {
            if (messages == null) throw new ArgumentNullException("messages");
            string model = (opts != null && !string.IsNullOrWhiteSpace(opts.Model)) ? opts.Model : _defaultModel;
            if (string.IsNullOrWhiteSpace(model))
                throw new ProviderException("No model configured for provider " + Id + " — set \"model\" in config.json or pass ProviderOptions.Model.");

            string url = BuildUrl(opts);
            string body = BuildBody(messages, opts, model);

            using (var req = new HttpRequestMessage(HttpMethod.Post, url))
            {
                if (!string.IsNullOrEmpty(_apiKey))
                    req.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
                req.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
                req.Content = new StringContent(body, Encoding.UTF8, "application/json");

                HttpResponseMessage resp;
                try { resp = await Http.SendAsync(req, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (TaskCanceledException) { throw new ProviderException("Provider request timed out."); }
                catch (HttpRequestException ex) { throw new ProviderException("Provider network failure: " + ex.Message, ex); }

                using (resp)
                {
                    string text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                        throw new ProviderException("Provider HTTP " + (int)resp.StatusCode + " (" + resp.StatusCode + "): " + Truncate(text));
                    return Parse(text, model);
                }
            }
        }

        private string BuildUrl(ProviderOptions opts)
        {
            string root = _baseUrl;
            if (opts != null && !string.IsNullOrWhiteSpace(opts.BaseUrlOverride))
                root = opts.BaseUrlOverride.TrimEnd('/');
            return root + "/messages";
        }

        private static string BuildBody(IReadOnlyList<ChatMessage> messages, ProviderOptions opts, string model)
        {
            var j = new JObject();
            j["model"] = model;
            j["max_tokens"] = (opts != null && opts.MaxTokens.HasValue) ? opts.MaxTokens.Value : 1024;
            if (opts != null && opts.Temperature.HasValue) j["temperature"] = opts.Temperature.Value;

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
                    arr.Add(new JObject { ["role"] = role == "assistant" ? "assistant" : "user", ["content"] = m.Content });
                }
            }
            if (system.Length > 0) j["system"] = system.ToString();
            j["messages"] = arr;

            return j.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static ProviderResult Parse(string text, string fallbackModel)
        {
            JObject j;
            try { j = JObject.Parse(text); }
            catch (Exception ex) { throw new ProviderException("Provider returned non-JSON: " + Truncate(text), ex); }

            var content = j["content"] as JArray;
            var first = content != null && content.Count > 0 ? content[0] : null;
            string outText = first != null ? (string)first["text"] : null;
            if (string.IsNullOrEmpty(outText))
                throw new ProviderException("Provider returned no content.");

            long tokIn = (long?)(j["usage"]?["input_tokens"]) ?? 0;
            long tokOut = (long?)(j["usage"]?["output_tokens"]) ?? 0;

            return new ProviderResult
            {
                Text = outText,
                TokIn = tokIn,
                TokOut = tokOut,
                Model = (string)j["model"] ?? fallbackModel
            };
        }

        private static string Truncate(string s)
        {
            if (s == null) return "";
            return s.Length > 500 ? s.Substring(0, 500) : s;
        }
    }
}
