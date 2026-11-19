using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Forge.SolidWorks
{
    /// <summary>
    /// Anonymous usage data — strictly opt-in and OFF by default (privacy-first). Fire-and-forget:
    /// it NEVER blocks or delays an operation, NEVER throws, and NEVER sends anything from the user's
    /// documents (no file names, paths, part names, or geometry — only counts, codes, versions,
    /// durations, and the user's own feedback words). Silent if offline or when sharing is OFF.
    /// The Settings "Usage data" toggle (or saying "share data on/off") flips <see cref="Enabled"/>,
    /// which is persisted in %APPDATA%\Forge\settings.json so the choice sticks across sessions.
    /// </summary>
    internal static class Telemetry
    {
        private const string Url = "https://www.getforge.build/api/usage/event";

        /// <summary>Opt-in switch, persisted. OFF by default — Log() is a no-op until the user enables it.</summary>
        public static bool Enabled
        {
            get { return ForgeData.ShareUsage; }
            set { ForgeData.ShareUsage = value; }
        }

        private static readonly HttpClient Http = MakeClient();
        private static HttpClient MakeClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            c.DefaultRequestHeaders.Add("Authorization", "Bearer " + ForgeConfig.ApiKey);
            return c;
        }

        public static void Log(
            string eventType,
            bool? success = null,
            int? partsCount = null,
            int? featuresCount = null,
            long? durationMs = null,
            string swVersion = null,
            string errorCode = null,
            int? sentiment = null,
            string feedback = null,
            long? tokensIn = null,
            long? tokensOut = null,
            string modelName = null,
            int? llmCalls = null,
            int? cacheHits = null,
            double? estCostUsd = null,
            int? opsUsed = null,
            string questionType = null,
            string questionSummary = null)
        {
            try
            {
                if (!Enabled) return;   // usage sharing OFF — nothing leaves this machine

                var payload = new
                {
                    event_type = eventType,
                    success,
                    parts_count = partsCount,
                    features_count = featuresCount,
                    duration_ms = durationMs,
                    sw_version = swVersion,
                    error_code = errorCode,
                    sentiment,
                    feedback,
                    tokens_in = tokensIn,
                    tokens_out = tokensOut,
                    model_name = modelName,
                    llm_calls_count = llmCalls,
                    cache_hits = cacheHits,
                    est_cost_usd = estCostUsd,
                    ops_used = opsUsed,
                    question_type = questionType,
                    question_summary = questionSummary
                };
                string json = JsonConvert.SerializeObject(payload);

                // Detach: telemetry must never sit on the operation's thread.
                Task.Run(async () =>
                {
                    try { await Http.PostAsync(Url, new StringContent(json, Encoding.UTF8, "application/json")); }
                    catch { /* offline / blocked — swallow, telemetry is best-effort */ }
                });
            }
            catch { /* never let telemetry surface */ }
        }
    }
}
