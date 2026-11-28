using System;
using System.IO;
using Newtonsoft.Json.Linq;

namespace Forge.Providers
{
    /// <summary>
    /// Builds an IProviderClient from %APPDATA%\Forge\config.json
    /// {"provider":"openai-compatible","baseUrl":"https://api.deepseek.com/v1","model":"deepseek-flash"}
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
            // Slug-only configs (provider "opencode"/"opencode-go" with no baseUrl) fall back
            // to the matching zen gateway instead of silently drifting to api.openai.com.
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                // OpenCode Go must stay on zen/go/v1 (NOT inference/go): the Go gateway keys
                // routing/prompt caching on x-opencode-session, which OpenAiCompatibleClient
                // sends only for /zen/go base URLs. Changing this breaks session header detection.
                if (string.Equals((provider ?? "").Trim(), "opencode-go", StringComparison.OrdinalIgnoreCase))
                    baseUrl = "https://opencode.ai/zen/go/v1";
                else if (string.Equals((provider ?? "").Trim(), "opencode", StringComparison.OrdinalIgnoreCase))
                    baseUrl = "https://opencode.ai/zen/v1";
            }
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
            // OpenCode Zen (Kilo parity): no key anywhere -> literal bearer token "public";
            // the gateway then serves only zero-cost models. Never applied to other slugs
            // (in particular opencode-go: that gateway needs a paid subscription key).
            if (string.IsNullOrEmpty(apiKey)
                && string.Equals(slug, "opencode", System.StringComparison.OrdinalIgnoreCase))
            {
                apiKey = "public";
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
                case "opencode":
                case "opencode-go":
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
            if (bu.Contains("api.deepseek.com")) return "deepseek-flash";
            if (bu.Contains("api.openai.com")) return "gpt-6-luna";
        if (bu.Contains("api.anthropic.com")) return "claude-haiku-4-5";
            return null;
        }

        /// <summary>
        /// True unless the model is a known reasoning variant that rejects or ignores custom
        /// temperature (o-series, GPT-5/6 reasoning, codex, thinking variants — per OpenAI docs,
        /// only defaults are accepted). GPT-4-class models are deliberately INCLUDED: they accept
        /// custom temperature, matching Kilo's per-model capabilities gate for that family.
        /// When false, callers omit Temperature so the provider default applies.
        /// </summary>
        public static bool SupportsTemperature(string model)
        {
            if (IsReasoningModel(model)) return false;
            if (!string.IsNullOrWhiteSpace(model) && model.Trim().ToLowerInvariant().Contains("magistral")) return false;
            return true;
        }

        /// <summary>
        /// True for the reasoning family (o-series, GPT-5/6, codex, thinking/reasoning variants) that
        /// rejects custom temperature AND requires max_completion_tokens instead of max_tokens.
        /// Single source of truth for both capability gates; never throws.
        /// </summary>
        public static bool IsReasoningModel(string model)
        {
            if (string.IsNullOrWhiteSpace(model)) return false;
            string m = model.Trim().ToLowerInvariant();
            if (System.Text.RegularExpressions.Regex.IsMatch(m, @"(^|[^a-z0-9])(o[1-9]|gpt-[56]|codex)([^a-z0-9]|$)")) return true;
            if (System.Text.RegularExpressions.Regex.IsMatch(m, @"(^|[^a-z0-9])(think|thinking|reason|reasoning|reasoner)([^a-z0-9]|$)")) return true;
            return false;
        }

        private static readonly string[] ReasoningEffortYesSlugs = { "openai", "openrouter", "xai", "grok" };
        private static readonly string[] ReasoningEffortNoSlugs = { "deepseek", "anthropic", "ollama", "nvidia", "opencode", "opencode-go" };
        private static readonly string[] ByokSlugs = { "openai", "deepseek", "openrouter", "anthropic", "xai", "grok", "nvidia", "opencode-go" };
        private static readonly string[] FreeSlugs = { "opencode", "ollama", "local", "public" };

        public static JObject ReasoningEffortCapabilityTable()
        {
            var j = new JObject();
            foreach (var s in ReasoningEffortYesSlugs) j[s] = true;
            foreach (var s in ReasoningEffortNoSlugs) j[s] = false;
            return j;
        }

        public static bool SupportsReasoningEffort(string slugOrModel)
        {
            if (string.IsNullOrWhiteSpace(slugOrModel)) return false;
            string s = slugOrModel.Trim().ToLowerInvariant();
            foreach (var x in ReasoningEffortYesSlugs) if (s == x) return true;
            foreach (var x in ReasoningEffortNoSlugs) if (s == x) return false;
            if (s.Contains("deepseek")) return false;
            if (s.Contains("grok")) return true;
            return System.Text.RegularExpressions.Regex.IsMatch(s, @"(^|[^a-z0-9])(o[1-9]|gpt-[56]|codex)([^a-z0-9]|$)");
        }

        public static bool AcceptsReasoningEffort(string baseUrl, string model)
        {
            string bu = (baseUrl ?? "").Trim().ToLowerInvariant();
            if (bu.Contains("deepseek") || bu.Contains("anthropic") || bu.Contains("ollama")
                || bu.Contains("nvidia") || bu.Contains("opencode")) return false;
            string slug = null;
            try { slug = SlugFor(null, bu); } catch { slug = null; }
            string sl = (slug ?? "").Trim().ToLowerInvariant();
            if (sl == "deepseek" || sl == "anthropic" || sl == "ollama" || sl == "nvidia" || sl == "opencode" || sl == "opencode-go") return false;
            if (bu.Contains("api.x.ai") || bu.Contains("xai") || bu.Contains("grok")) return true;
            string m = (model ?? "").Trim().ToLowerInvariant();
            if (m.Contains("deepseek")) return false;
            if (m.Contains("grok")) return true;
            if (System.Text.RegularExpressions.Regex.IsMatch(m, @"(^|[^a-z0-9])(o[1-9]|gpt-[56]|codex)([^a-z0-9]|$)")) return true;
            return string.IsNullOrEmpty(sl);
        }

        public static bool SupportsExtendedThinking(string model)
        {
            if (IsReasoningModel(model)) return true;
            string m = (model ?? "").Trim().ToLowerInvariant();
            return m.Contains("claude");
        }

        public static int ClampOutputTokens(string model, int requested)
        {
            int window = 128000;
            try { var cw = ContextWindowFor(model); if (cw != null && cw.Item1 > 0) window = cw.Item1; } catch { }
            int budget = System.Math.Max(4096, window / 2);
            return requested > 0 ? System.Math.Min(requested, budget) : budget;
        }

        public static int MaxOutputTokensFor(string slug, string modelId, int requested)
        {
            if (string.IsNullOrWhiteSpace(modelId)) return 0;
            var cw = ContextWindowFor(modelId);
            if (cw == null || !cw.Item2 || cw.Item1 <= 0) return 0;
            int cap = ClampOutputTokens(modelId, int.MaxValue);
            return requested > 0 ? System.Math.Min(requested, cap) : cap;
        }

        private static string LabelFor(string modelId, string slug)
        {
            if (string.IsNullOrWhiteSpace(modelId)) return DisplayNameFor(slug, null);
            string m = modelId.Trim();
            string[] parts = m.Split('-');
            for (int i = 0; i < parts.Length; i++)
                if (parts[i].Length > 0) parts[i] = char.ToUpperInvariant(parts[i][0]) + parts[i].Substring(1);
            return string.Join(" ", parts);
        }

        public static JObject ModelInfo(string slug, string modelId)
        {
            string s = (slug ?? "").Trim().ToLowerInvariant();
            var cw = ContextWindowFor(modelId);
            int window = cw != null && cw.Item1 > 0 ? cw.Item1 : 128000;
            bool exact = cw != null && cw.Item2;
            bool free = false, byok = false;
            foreach (var x in FreeSlugs) if (s == x) free = true;
            foreach (var x in ByokSlugs) if (s == x) byok = true;
            bool thinking = string.Equals(s, "anthropic", System.StringComparison.OrdinalIgnoreCase) && SupportsExtendedThinking(modelId);
            bool reasoning = IsReasoningModel(modelId) || thinking;
            bool temp = SupportsTemperature(modelId) && !thinking;
            var caps = new JArray { "streaming", "tools", "max-output-tokens" };
            if (reasoning) caps.Add("reasoning");
            if (thinking) caps.Add("thinking");
            if (temp) caps.Add("temperature");
            return new JObject
            {
                ["id"] = modelId ?? "",
                ["label"] = LabelFor(modelId, s),
                ["free"] = free,
                ["byok"] = byok,
                ["pricing"] = JValue.CreateNull(),
                ["pricingUnknown"] = true,
                ["contextWindow"] = window,
                ["contextWindowExact"] = exact,
                ["maxOutput"] = MaxOutputTokensFor(s, modelId, 0),
                ["capabilities"] = caps,
                ["reasoning"] = reasoning,
                ["temperatureSupported"] = temp
            };
        }

        /// <summary>
        /// Approximate context window (input tokens) for a model id. Returns
        /// (window, exact): exact=false means the id was unknown and the 128k
        /// default applies — the panel labels it "approx". Never throws, never
        /// probes the network; unknown/custom ids fall through to the default.
        /// </summary>
        public static Tuple<int, bool> ContextWindowFor(string model)
        {
            try
            {
                string m = (model ?? "").Trim().ToLowerInvariant();
                if (m.Length == 0) return Tuple.Create(128000, false);
                if (m.Contains("claude")) return Tuple.Create(200000, true); // haiku-4-5/sonnet/opus incl. 4-5 family
                if (m.Contains("gpt-6") || System.Text.RegularExpressions.Regex.IsMatch(m, @"gpt-5\.[4-9]"))
                    return Tuple.Create(m.Contains("mini") || m.Contains("nano") ? 400000 : 1050000, true);
                if (m.Contains("gpt-5")) return Tuple.Create(400000, true);
                if (m.Contains("gpt-3.5")) return Tuple.Create(16000, true);
                if (m.Contains("gpt-4") || System.Text.RegularExpressions.Regex.IsMatch(m, @"(^|[^a-z0-9])o[1-9]([^a-z0-9]|$)")) return Tuple.Create(128000, true); // 4-class, o-series
                if (m.Contains("deepseek")) return Tuple.Create(1000000, System.Text.RegularExpressions.Regex.IsMatch(m, @"(^|[^a-z0-9])v4([^a-z0-9]|$)"));
                if (m.Contains("gemini")) return Tuple.Create(1000000, false);
                if (m.Contains("kimi-k3") || (m.Contains("moonshot") && m.Contains("k3"))) return Tuple.Create(1048576, false);
                if (m.Contains("kimi") || m.Contains("moonshot")) return Tuple.Create(262144, false);
                if (m.Contains("glm-5.3-flash-free")) return Tuple.Create(200000, true);
                if (m.Contains("glm-5") || m.Contains("glm5")) return Tuple.Create(m.Contains("-free") ? 200000 : 1000000, false);
                if (m.Contains("glm-4")) return Tuple.Create(200000, false);
                if (m.Contains("llama") || m.Contains("qwen") || m.Contains("mistral") ||
                    m.Contains("gemma") || m.Contains("phi") || m.Contains("nvidia") ||
                    m.Contains("ollama")) return Tuple.Create(128000, false); // local/open weights vary by build
                return Tuple.Create(128000, false);
            }
            catch { return Tuple.Create(128000, false); }
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
            // User-defined customs show their own label.
            try
            {
                JObject cfg = ReadConfig();
                var list = cfg != null ? cfg["customProviders"] as JArray : null;
                if (list != null)
                {
                    foreach (var e in list)
                    {
                        try
                        {
                            var o = e as JObject;
                            if (o != null && string.Equals(((string)o["slug"] ?? "").Trim(), slug.Trim(), StringComparison.OrdinalIgnoreCase))
                            {
                                string lab = ((string)o["label"] ?? "").Trim();
                                return string.IsNullOrWhiteSpace(lab) ? slug.Trim() : lab;
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
            switch (slug.Trim().ToLowerInvariant())
            {
                case "deepseek": return "DeepSeek";
                case "openrouter": return "OpenRouter";
                case "nvidia": return "NVIDIA";
                case "openai": return "OpenAI";
                case "anthropic": return "Anthropic";
                case "ollama": return "Ollama";
                case "opencode": return "OpenCode Zen";
                case "opencode-go": return "OpenCode Go";
                default: return "the provider";
            }
        }

            /// <summary>Env-var-safe slug fragment (uppercase, hyphens/spaces to underscores).</summary>
        public static string EnvSlug(string slug)
        {
            if (string.IsNullOrWhiteSpace(slug)) return null;
            var s = System.Text.RegularExpressions.Regex.Replace(slug.Trim().ToUpperInvariant(), @"[^A-Z0-9]+", "_");
            s = s.Trim('_'); // a slug of only separators ("---") is no slug: fall back to bare
            return string.IsNullOrEmpty(s) ? null : s;
        }

        /// <summary>Per-slug key env var for the current config; null when no key is needed (Ollama/local).</summary>
        public static string KeyEnvFor(string provider, string baseUrl)
        {
            string slug = null;
            try { slug = SlugFor(provider, baseUrl); } catch { slug = null; }
            if (string.IsNullOrWhiteSpace(slug)) return "FORGE_PROVIDER_KEY";
            if (string.Equals(slug.Trim(), "ollama", System.StringComparison.OrdinalIgnoreCase)) return null;
            string env = null;
            try { env = EnvSlug(slug); } catch { env = null; }
            return string.IsNullOrEmpty(env) ? "FORGE_PROVIDER_KEY" : "FORGE_PROVIDER_KEY_" + env;
        }

        /// <summary>Missing-key refusal naming the right env var for the current slug; null when no key is needed.</summary>
        public static string MissingKeyMessage(string provider, string baseUrl)
        {
            string env = null, display = "the provider", slug = null;
            try { env = KeyEnvFor(provider, baseUrl); } catch { env = null; }
            try { display = DisplayNameFor(provider, baseUrl); } catch { }
            try { slug = SlugFor(provider, baseUrl); } catch { slug = null; }
            if (env == null) return null;
            if (string.Equals(slug, "opencode-go", StringComparison.OrdinalIgnoreCase))
                return "OpenCode Go subscription required — save your OpenCode Go key in Settings → API key (or set " + env + "), then resend.";
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
        // onboarding slug, since keys are filed per slug. User-defined customs
        // (config.json customProviders) resolve by exact baseUrl match — or by the slug
        // itself for hand-edited configs — so a custom key is never filed under, or read
        // from, a built-in slug. Bad entries are ignored, never thrown.
        private static string SlugFor(string provider, string baseUrl)
        {
            string p = (provider ?? "").Trim().ToLowerInvariant();
            string bu = (baseUrl ?? "").Trim().ToLowerInvariant();
            try
            {
                JObject cfg = ReadConfig();
                var list = cfg != null ? cfg["customProviders"] as JArray : null;
                if (list != null)
                {
                    foreach (var e in list)
                    {
                        try
                        {
                            var o = e as JObject;
                            if (o == null) continue;
                            string cs = ((string)o["slug"] ?? "").Trim().ToLowerInvariant();
                            string cb = ((string)o["baseUrl"] ?? "").Trim().TrimEnd('/').ToLowerInvariant();
                            if (cs.Length == 0 || cb.Length == 0) continue;
                            // Panel parity (ForgePanel.GetCustomProviders CustomKinds): ignore
                            // entries whose kind is not openai-compatible/anthropic, so a key
                            // is never filed under, or resolved from, a non-conforming slug.
                            string ck = ((string)o["kind"] ?? "").Trim().ToLowerInvariant();
                            if (ck != "openai-compatible" && ck != "anthropic") continue;
                            if (!string.IsNullOrWhiteSpace(bu) && bu.TrimEnd('/') == cb) return cs;
                            if (!string.IsNullOrWhiteSpace(p) && p == cs) return cs;
                        }
                        catch { }
                    }
                }
            }
            catch { }
            if (p == "anthropic") return "anthropic";
            if (p == "ollama") return "ollama";
            if (bu.Contains("integrate.api.nvidia.com")) return "nvidia";
            if (bu.Contains("openrouter.ai")) return "openrouter";
            if (bu.Contains("opencode.ai")) return bu.Contains("/zen/go") ? "opencode-go" : "opencode";
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
                        string es = EnvSlug(s);
                        var k = es == null ? null : Environment.GetEnvironmentVariable("FORGE_PROVIDER_KEY_" + es);
                        if (!string.IsNullOrWhiteSpace(k)) return k;
                    }
                    catch { }
                }
                // Kilo parity for OpenCode Zen and its Go sibling: OPENCODE_API_KEY is read
                // in addition to FORGE_PROVIDER_KEY_OPENCODE[_GO] (slug env above) and the
                // per-slug key file. Go shares the Zen credential infra but requires a key.
                if (string.Equals(s, "opencode", System.StringComparison.OrdinalIgnoreCase)
                    || string.Equals(s, "opencode-go", System.StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var k = Environment.GetEnvironmentVariable("OPENCODE_API_KEY");
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
