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
    /// Speaks the OpenAI /chat/completions wire format. One client covers every
    /// OpenAI-compatible host: api.openai.com, api.deepseek.com, OpenRouter, local
    /// Ollama / vLLM / LM Studio, etc. — pick the host via the baseUrl constructor arg.
    /// </summary>
    public class OpenAiCompatibleClient : IProviderClient
    {
        private static readonly HttpClient Http = CreateClient();
        private readonly string _apiKey;
        private readonly string _baseUrl;
        private readonly string _defaultModel;

        public OpenAiCompatibleClient(string apiKey, string baseUrl = "https://api.openai.com/v1", string defaultModel = null)
        {
            _apiKey = apiKey ?? "";
            _baseUrl = (baseUrl ?? "").TrimEnd('/');
            _defaultModel = defaultModel;
        }

        public string Id { get { return "openai-compatible"; } }

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
                    req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _apiKey);
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
            return root + "/chat/completions";
        }

        private static string BuildBody(IReadOnlyList<ChatMessage> messages, ProviderOptions opts, string model)
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
            }
            return j.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static ProviderResult Parse(string text, string fallbackModel)
        {
            JObject j;
            try { j = JObject.Parse(text); }
            catch (Exception ex) { throw new ProviderException("Provider returned non-JSON: " + Truncate(text), ex); }

            var choices = j["choices"] as JArray;
            var choice = choices != null && choices.Count > 0 ? choices[0] : null;
            string content = choice != null ? (string)choice["message"]?["content"] : null;
            if (string.IsNullOrEmpty(content))
                throw new ProviderException("Provider returned no content.");

            long tokIn = (long?)(j["usage"]?["prompt_tokens"]) ?? 0;
            long tokOut = (long?)(j["usage"]?["completion_tokens"]) ?? 0;

            return new ProviderResult
            {
                Text = content,
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
