using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Forge.Providers;
using Newtonsoft.Json.Linq;

namespace Forge.SolidWorks
{
    /// <summary>
    /// BYOK direct intent parse. When the user has configured their own LLM provider
    /// (%APPDATA%\Forge\config.json + a DPAPI-stored key), the intent prompt is sent to
    /// THEIR provider directly — Forge's hosted endpoint is bypassed and the user's key
    /// pays for the call. Returns null when unconfigured or on any provider/parse failure,
    /// so the caller falls back to the hosted path (fail closed, never a fabricated plan).
    /// The system prompt ships as a plain asset (prompts\intent-system.txt) beside the DLL.
    /// </summary>
    internal static class IntentParseDirect
    {
        private static string _systemPrompt; // lazy-loaded once

        /// <summary>Returns the plan JObject on success, null to fall back to the hosted parser.</summary>
        public static async Task<JObject> TryParse(string prompt, JObject ctxObj)
        {
            IProviderClient client;
            string system;
            try
            {
                client = ProviderFactory.Resolve();
                if (client == null) return null;
                system = LoadSystemPrompt();
                if (string.IsNullOrEmpty(system)) return null;
            }
            catch { return null; }

            string ctxText = (ctxObj != null ? ctxObj.ToString(Newtonsoft.Json.Formatting.None) : "{}");
            if (ctxText.Length > 12000) ctxText = ctxText.Substring(0, 12000);
            string user = "Context:\n" + ctxText + "\n\nUser said: \"" + (prompt ?? "").Trim() + "\"\n\nReturn the parse JSON.";

            var opts = new ProviderOptions { MaxTokens = 1500, Temperature = 0 };
            for (int attempt = 0; attempt < 2; attempt++) // one JSON-repair retry, then give up
            {
                try
                {
                    var msgs = new List<ChatMessage>
                    {
                        new ChatMessage { Role = "system", Content = system },
                        new ChatMessage { Role = "user", Content = attempt == 0 ? user : user + "\n\nOutput ONLY the JSON object. No markdown fences, no commentary." }
                    };
                    var res = await client.CompleteAsync(msgs, opts, CancellationToken.None).ConfigureAwait(false);
                    var plan = ExtractJson(res != null ? res.Text : null);
                    if (plan == null) continue; // repair retry
                    if (!(plan["operations"] is JArray)) plan["operations"] = new JArray();
                    if (!(plan["ambiguities"] is JArray)) plan["ambiguities"] = new JArray();
                    if (plan["confidence"] == null || plan["confidence"].Type != JTokenType.Float && plan["confidence"].Type != JTokenType.Integer)
                        plan["confidence"] = ((JArray)plan["operations"]).Count > 0 ? 0.7 : 0.0;
                    IntentRate.Note(res.TokIn, res.TokOut);
                    GroundTruth.Trace?.Invoke("intent-parse: direct provider=" + client.Id + " model=" + res.Model);
                    return plan;
                }
                catch (OperationCanceledException) { return null; }
                catch (Exception ex)
                {
                    GroundTruth.Trace?.Invoke("intent-parse: direct failed (" + ex.GetType().Name + ") — falling back to hosted");
                    return null; // provider/network errors: no point retrying the same call
                }
            }
            return null;
        }

        // Recover the plan JObject from raw LLM text. Free-tier models wrap JSON in markdown fences and pad it
        // with chatter — SafeJsonExtractor strips those and returns the first balanced JSON object (arrays are
        // wrapped or reduced to their first object). Null when nothing recoverable, so the caller falls back.
        // Labtec bug #2: a scalar answer is never indexed here — the envelope is an object or null.
        private static JObject ExtractJson(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            string err;
            return SafeJsonExtractor.ExtractResponseEnvelope(text, out err);
        }

        private static string LoadSystemPrompt()
        {
            if (_systemPrompt != null) return _systemPrompt;
            try
            {
                string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                string p = Path.Combine(dir, "prompts", "intent-system.txt");
                if (File.Exists(p)) _systemPrompt = File.ReadAllText(p);
            }
            catch { }
            return _systemPrompt ?? "";
        }
    }
}
