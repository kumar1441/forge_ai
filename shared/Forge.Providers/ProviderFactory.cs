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
            // Key order (mirrors the panel exactly): per-slug DPAPI file → per-slug env
            // (FORGE_PROVIDER_KEY_<SLUG>) → legacy shared file (adopted under the slug) → bare
            // FORGE_PROVIDER_KEY env as a single-provider fallback.
            // Never from a plaintext field in config.json — keys don't live in files.
            // Slug-aware throughout: a DeepSeek key must never be sent to api.openai.com just
            // because the bare env var is set while another provider is selected.
            string slug = SlugFor(provider, baseUrl);
            string apiKey = LoadKeyForSlug(slug, provider);
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
        /// Cheap model for rewrite chores (enhance, compaction summaries) — Kilo parity: explicit
        /// per-config override first, then known-cheap provider mappings, else null and the caller
        /// keeps the current model (intentional subset: no guessing at model IDs that may not exist).
        /// </summary>
        public static string GetSmallModel(string provider, string baseUrl)
        {
            try
            {
                JObject cfg = ReadConfig();
                string explicit_ = cfg != null ? (string)cfg["smallModel"] : null;
                if (!string.IsNullOrWhiteSpace(explicit_)) return explicit_.Trim();
            }
            catch { }
            string bu = (baseUrl ?? "").Trim().ToLowerInvariant();
            if (bu.Contains("api.deepseek.com")) return "deepseek-chat";
            if (bu.Contains("api.openai.com")) return "gpt-4o-mini";
            if (bu.Contains("api.anthropic.com")) return "claude-3-5-haiku-latest";
            return null;
        }

        /// <summary>
        /// True unless the model is a known reasoning variant that rejects custom temperatures
        /// (OpenAI o-series errors on anything but the default; thinking variants ignore it).
        /// When false, callers omit Temperature so the provider default applies.
        /// </summary>
        public static bool SupportsTemperature(string model)
        {
            if (string.IsNullOrWhiteSpace(model)) return true;
            string m = model.Trim().ToLowerInvariant();
            if (System.Text.RegularExpressions.Regex.IsMatch(m, @"(^|[^a-z0-9])(o[1-9]|gpt-5|codex)([^a-z0-9]|$)")) return false;
            if (m.Contains("think") || m.Contains("reason")) return false;
            return true;
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
        /// <summary>Display name for the current provider (what the Settings dropdown shows), never a client id.</summary>
        public static string DisplayNameFor(string provider, string baseUrl)
        {
            string slug = null;
            try { slug = SlugFor(provider, baseUrl); } catch { slug = null; }
            if (string.IsNullOrWhiteSpace(slug)) return "the provider";
            switch (slug.Trim().ToLowerInvariant())
            {
                case "deepseek": return "DeepSeek";
                case "openrouter": return "OpenRouter";
                case "nvidia": return "NVIDIA";
                case "openai": return "OpenAI";
                case "anthropic": return "Anthropic";
                case "ollama": return "Ollama";
                default: return slug.Trim();
            }
        }

        /// <summary>Per-slug key env var for the current config; null when no key is needed (Ollama/local).</summary>
        public static string KeyEnvFor(string provider, string baseUrl)
        {
            string slug = null;
            try { slug = SlugFor(provider, baseUrl); } catch { slug = null; }
            if (string.IsNullOrWhiteSpace(slug)) return "FORGE_PROVIDER_KEY";
            if (string.Equals(slug.Trim(), "ollama", StringComparison.OrdinalIgnoreCase)) return null;
            return "FORGE_PROVIDER_KEY_" + slug.Trim().ToUpperInvariant();
        }

        /// <summary>Missing-key refusal naming the right env var for the current slug; null when no key is needed.</summary>
        public static string MissingKeyMessage(string provider, string baseUrl)
        {
            string env = null, display = "the provider";
            try { env = KeyEnvFor(provider, baseUrl); } catch { env = null; }
            try { display = DisplayNameFor(provider, baseUrl); } catch { }
            if (env == null) return null;
            return "No API key saved for " + display + " — save one in Settings → API key (or set " + env + "), then resend.";
        }

        /// <summary>Auth-rejection (HTTP 401/403) naming the right key target for the current slug.</summary>
        public static string AuthRefusedMessage(string provider, string baseUrl, int code)
        {
            string env = null, display = "the provider";
            try { env = KeyEnvFor(provider, baseUrl); } catch { env = null; }
            try { display = DisplayNameFor(provider, baseUrl); } catch { }
            string where = env != null ? "the key for " + display + " in Settings → API key (or " + env + ")" : "the provider key in Settings → API key";
            return "The provider refused the request (HTTP " + code + ") — the API key is missing or wrong; check " + where + ", then resend.";
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
            if (bu.Contains("localhost") || bu.Contains("127.0.0.1") || bu.Contains("::1")) return "ollama";
            // Fall back to the stored provider value (matches the panel's PresetSlugFor):
            // a custom endpoint keeps its client id instead of collapsing to null and
            // advertising a different env var than the panel.
            if (!string.IsNullOrWhiteSpace(p)) return p;
            return null;
        }

        private static string LoadKeyForSlug(string slug, string provider)
        {
            string s = (slug ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(s))
            {
                try
                {
                    var k = KeyStore.Load(s);
                    if (!string.IsNullOrWhiteSpace(k)) return k;
                }
                catch { }
                // Per-slug env beats the legacy shared file (same order as the panel):
                // an explicit key for THIS provider outranks an unattributed old one.
                // Checked even when slug == provider (e.g. anthropic): the advertised
                // FORGE_PROVIDER_KEY_<SLUG> must actually be read.
                if (!string.Equals(s, "ollama", System.StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var k = Environment.GetEnvironmentVariable("FORGE_PROVIDER_KEY_" + s.Trim().ToUpperInvariant());
                        if (!string.IsNullOrWhiteSpace(k)) return k;
                    }
                    catch { }
                }
                if (!string.Equals(s, provider, System.StringComparison.OrdinalIgnoreCase))
                {
                    // Adopt the legacy shared file under the current slug (same as the panel path):
                    // a pre-fix key is attributed to the selected provider instead of silently
                    // serving every other provider too. Delete only after a successful save so
                    // a failed write can never lose the key.
                    try
                    {
                        var legacy = KeyStore.Load(provider);
                        if (!string.IsNullOrWhiteSpace(legacy))
                        {
                            bool saved = false;
                            try { KeyStore.Save(s, legacy); saved = true; } catch { saved = false; }
                            if (saved) { try { KeyStore.Delete(provider); } catch { } }
                            return legacy;
                        }
                    }
                    catch { }
                    return null;
                }
                // slug == provider (or custom client id): slug file + slug env already
                // checked above; the provider file is the same file, so stop here and let
                // the caller apply the bare FORGE_PROVIDER_KEY single-provider fallback.
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
