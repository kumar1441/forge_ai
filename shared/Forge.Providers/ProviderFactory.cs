using System;
using System.IO;
using Newtonsoft.Json.Linq;

namespace Forge.Providers
{
    /// <summary>
    /// Builds an IProviderClient from %APPDATA%\Forge\config.json
    /// {"provider":"openai-compatible","baseUrl":"https://api.deepseek.com/v1","model":"deepseek-chat"}
    /// with the API key read from KeyStore (keys\{provider}.key). Returns null when unconfigured
    /// so the caller can fall back to the cloud router.
    /// </summary>
    public static class ProviderFactory
    {
        public static IProviderClient Resolve()
        {
            string unused;
            return Resolve(out unused);
        }

        /// <summary>
        /// Single-read snapshot: the client AND the model come from the same config load, so a
        /// provider/model switch landing mid-run can never mix one run's key with another run's
        /// model. Prefer this over Resolve()+CurrentModel() for any multi-call run.
        /// </summary>
        public static IProviderClient Resolve(out string model)
        {
            model = null;
            JObject cfg = ReadConfig();
            if (cfg == null) return null;

            // Wrong-type config values ({"provider": 5}, arrays, objects) must read as
            // missing — never throw out of Resolve into the caller's fall-through.
            string provider = null, baseUrl = null;
            try { provider = (string)cfg["provider"]; } catch { provider = null; }
            if (string.IsNullOrWhiteSpace(provider)) return null;
            try { baseUrl = (string)cfg["baseUrl"]; } catch { baseUrl = null; }
            try { model = (string)cfg["model"]; } catch { model = null; }
            if (model != null) model = model.Trim();
            if (model != null && model.Length == 0) model = null;
            // Key: DPAPI store first (settings pane), then the FORGE_PROVIDER_KEY env var.
            // Never from a plaintext field in config.json — keys don't live in files.
            // Slug-aware: keys are stored per onboarding slug ("nvidia"), with the legacy
            // shared client-id file ("openai-compatible") as fallback — must match SaveProviderKey
            // or switching providers sends the wrong key to the wrong endpoint.
            string apiKey = LoadKeyForSlug(SlugFor(provider, baseUrl), provider);
            if (string.IsNullOrEmpty(apiKey))
            {
                try { apiKey = Environment.GetEnvironmentVariable("FORGE_PROVIDER_KEY"); } catch { }
            }

            switch (provider.Trim().ToLowerInvariant())
            {
                // Slug aliases: hand-edited or older configs may store the onboarding
                // slug ("ollama", "nvidia", ...) instead of the client id — all of
                // these speak the OpenAI wire format, so resolve them the same way.
                case "openai-compatible":
                case "openai":
                case "deepseek":
                case "openrouter":
                case "nvidia":
                case "ollama":
                    return new OpenAiCompatibleClient(apiKey, baseUrl, model);
                case "anthropic":
                    return new AnthropicClient(apiKey, baseUrl, model);
                default:
                    return null;
            }
        }

        /// <summary>
        /// Cheap model for rewrite chores (enhance, compaction summaries) — Kilo parity: small
        /// model first, main-model fallback. Only providers with a known-cheap mapping resolve here
        /// (intentional subset; elsewhere the caller keeps the current model).
        /// </summary>
        public static string GetSmallModel(string provider, string baseUrl)
        {
            string bu = (baseUrl ?? "").Trim().ToLowerInvariant();
            if (bu.Contains("api.deepseek.com")) return "deepseek-chat";
            if (bu.Contains("api.openai.com")) return "gpt-4o-mini";
            return null;
        }

        /// <summary>Small model for the current config (rewrite chores), or null to keep current.</summary>
        public static string SmallModelForCurrentConfig()
        {
            try
            {
                JObject cfg = ReadConfig();
                if (cfg == null) return null;
                return GetSmallModel((string)cfg["provider"], (string)cfg["baseUrl"]);
            }
            catch { return null; }
        }
        /// <summary>The model currently selected (model picker writes config.json["model"]).</summary>
        public static string CurrentModel()
        {
            try
            {
                JObject cfg = ReadConfig();
                string m = cfg != null ? (string)cfg["model"] : null;
                return string.IsNullOrWhiteSpace(m) ? null : m.Trim();
            }
            catch { return null; }
        }

        private static JObject ReadConfig()
        {
            try
            {
                string p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Forge", "config.json");
                if (!File.Exists(p)) return null;
                return JObject.Parse(File.ReadAllText(p));
            }
            catch
            {
                return null;
            }
        }

        // Mirror of the panel's PresetSlugFor: map a stored provider/baseUrl back to the
        // onboarding slug, since keys are filed per slug.
        private static string SlugFor(string provider, string baseUrl)
        {
            string p = (provider ?? "").Trim().ToLowerInvariant();
            string bu = (baseUrl ?? "").Trim().ToLowerInvariant();
            if (p == "anthropic") return "anthropic";
            if (p == "ollama") return "ollama";
            if (bu.Contains("integrate.api.nvidia.com")) return "nvidia";
            if (bu.Contains("openrouter.ai")) return "openrouter";
            if (bu.Contains("api.deepseek.com")) return "deepseek";
            if (bu.Contains("api.openai.com")) return "openai";
            if (bu.Contains("localhost") || bu.Contains("127.0.0.1") || bu.Contains("[::1]")) return "ollama";
            return null;
        }

        private static string LoadKeyForSlug(string slug, string provider)
        {
            if (!string.IsNullOrWhiteSpace(slug) && !string.Equals(slug, provider, System.StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var k = KeyStore.Load(slug);
                    if (!string.IsNullOrWhiteSpace(k)) return k;
                }
                catch { }
                // Adopt the legacy shared file under the current slug (same as the panel path):
                // a pre-fix key is attributed to the selected provider instead of silently
                // serving every other provider too.
                try
                {
                    var legacy = KeyStore.Load(provider);
                    if (!string.IsNullOrWhiteSpace(legacy))
                    {
                        try { KeyStore.Save(slug, legacy); } catch { }
                        try { KeyStore.Delete(provider); } catch { }
                        return legacy;
                    }
                }
                catch { }
                return null;
            }
            try
            {
                var k = KeyStore.Load(provider);
                if (!string.IsNullOrWhiteSpace(k)) return k;
            }
            catch { }
            return null;
        }
    }
}
