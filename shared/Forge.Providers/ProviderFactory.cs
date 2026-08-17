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
            JObject cfg = ReadConfig();
            if (cfg == null) return null;

            string provider = (string)cfg["provider"];
            if (string.IsNullOrWhiteSpace(provider)) return null;

            string baseUrl = (string)cfg["baseUrl"];
            string model = (string)cfg["model"];
            // Key: DPAPI store first (settings pane), then the FORGE_PROVIDER_KEY env var.
            // Never from a plaintext field in config.json — keys don't live in files.
            string apiKey = KeyStore.Load(provider);
            if (string.IsNullOrEmpty(apiKey))
            {
                try { apiKey = Environment.GetEnvironmentVariable("FORGE_PROVIDER_KEY"); } catch { }
            }

            switch (provider.Trim().ToLowerInvariant())
            {
                case "openai-compatible":
                case "openai":
                    return new OpenAiCompatibleClient(apiKey, baseUrl, model);
                case "anthropic":
                    return new AnthropicClient(apiKey, baseUrl, model);
                default:
                    return null;
            }
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
    }
}
