using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Forge.SolidWorks
{
    /// <summary>
    /// Forge data foundation (per the data contract in docs/DATA.md). Anonymous install ID that survives add-in
    /// updates (%APPDATA%, not the DLL folder), name-masking enforced BEFORE anything is stored, opt-in settings
    /// (default OFF), assembly-class bucketing, per-run time-saved heuristic, and ONE append-only event log that
    /// feeds telemetry + Layer 3 replay + the failures file. HARD EXCLUSIONS are enforced here in code: no geometry,
    /// no dimensions, no file names, no feature tree, no raw component names, no screenshots, no BOM.
    /// </summary>
    public static class ForgeData
    {
        public const int SchemaVersion = 1;
        public const string AddinVersion = "2026.07.21";  // bump when a new DLL ships; compared to /api/version

        // ---- update channel: returns a "new build available" notice if the server version is newer, else null ----
        public static async Task<string> UpdateNotice()
        {
            try
            {
                var j = await Cloud.GetJsonAsync("https://www.getforge.build/api/version");
                string v = (string)j["version"];
                if (!string.IsNullOrEmpty(v) && string.CompareOrdinal(v, AddinVersion) > 0)
                    return "A newer Forge build (" + v + ") is available — " + (string)j["notes"] + " Download: " + (string)j["download_url"];
            }
            catch { }
            return null;
        }

        // ---- one-line summary of the data contract, for the panel privacy control ----
        public static string PrivacySummary() =>
            (ShareUsage ? "Anonymous usage sharing is ON" : "Anonymous usage sharing is OFF") +
            ". Collected (when ON): masked commands, what Forge understood, verified outcome, part-count buckets, time saved — tied only to an anonymous install ID. " +
            "NEVER collected: geometry, dimensions, file names, part names, screenshots, or BOM. Say 'share data off' or 'share data on' to change it.";
        private static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Forge");
        private static string P(string f) => Path.Combine(Dir, f);

        // ---- anonymous install ID (stable across DLL updates; no account, no PII) ----
        private static string _install;
        public static string InstallId
        {
            get
            {
                if (_install != null) return _install;
                try
                {
                    Directory.CreateDirectory(Dir);
                    var f = P("install-id.txt");
                    if (File.Exists(f)) _install = File.ReadAllText(f).Trim();
                    if (string.IsNullOrEmpty(_install)) { _install = Guid.NewGuid().ToString("N"); File.WriteAllText(f, _install); }
                }
                catch { _install = "anon"; }
                return _install;
            }
        }

        // ---- opt-in (default OFF). OFF => zero events leave the machine. ----
        public static bool ShareUsage
        {
            get { try { var f = P("settings.json"); if (File.Exists(f)) return (bool?)(JObject.Parse(File.ReadAllText(f))["shareUsage"]) ?? false; } catch { } return false; }
            set { try { Directory.CreateDirectory(Dir); File.WriteAllText(P("settings.json"), new JObject { ["shareUsage"] = value }.ToString()); } catch { } }
        }

        // ---- NAME MASKING: replace any real component name (and file-name-looking tokens) with <part>. Applied
        //      before a prompt is ever logged or sent. Longer names first so substrings don't leak. ----
        public static string Mask(string prompt, IEnumerable<string> componentNames)
        {
            if (string.IsNullOrEmpty(prompt)) return prompt ?? "";
            string s = prompt;
            try
            {
                var names = (componentNames ?? Enumerable.Empty<string>())
                    .Where(n => !string.IsNullOrWhiteSpace(n) && n.Length >= 2)
                    .SelectMany(n => new[] { n, n.Contains("@") ? n.Substring(0, n.IndexOf('@')) : n, n.Contains("-") ? n.Substring(0, n.LastIndexOf('-')) : n })
                    .Distinct().OrderByDescending(n => n.Length);
                foreach (var n in names)
                    s = System.Text.RegularExpressions.Regex.Replace(s, System.Text.RegularExpressions.Regex.Escape(n), "<part>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                // strip anything that looks like a file name
                s = System.Text.RegularExpressions.Regex.Replace(s, @"[\w\-]+\.(sldprt|sldasm|slddrw|step|stp|iges|igs|x_t)", "<file>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }
            catch { }
            return s;
        }

        // ---- assembly class buckets (numbers only, no identities) ----
        public static string Bucket(int n)
        {
            if (n <= 0) return "0"; if (n <= 5) return "1-5"; if (n <= 25) return "6-25"; if (n <= 100) return "26-100";
            if (n <= 250) return "101-250"; if (n <= 1000) return "251-1000"; return "1000+";
        }

        // ---- per-run time-saved heuristic (seconds) vs a manual equivalent, per handler ----
        public static int TimeSavedSec(string handler, int itemCount)
        {
            switch ((handler ?? "").ToLowerInvariant())
            {
                case "set_material": case "material": return itemCount * 25;   // ~25s/part manually
                case "mate": return itemCount * 45;                            // ~45s/fastener
                case "mirror": return 90; case "explode": return 60; case "collapse": return 20;
                case "simplify": return itemCount * 20; case "resize": return itemCount * 30;
                case "isolate": return 30; case "scan": return 120;            // manual audit
                default: return 30;
            }
        }

        // ---- ONE event log. Appends locally ALWAYS (Layer 3 replay + failures review), and posts to the cloud only
        //      when ShareUsage is ON. Everything here is already masked/bucketed — hard exclusions enforced above. ----
        public static void LogEvent(string handler, string maskedPrompt, IntentPlan plan, string outcome, bool verifiedOk,
            int itemCount, int componentCount, int fastenerCount, int mateCount, string swBuild, long durationMs, string errorCode = null)
        {
            try
            {
                var ev = new JObject
                {
                    ["schema_version"] = SchemaVersion,
                    ["ts"] = DateTime.UtcNow.ToString("o"),
                    ["install_id"] = InstallId,
                    ["handler"] = handler,
                    ["prompt_masked"] = maskedPrompt,
                    ["parse"] = plan == null ? null : new JObject
                    {
                        ["ops"] = plan.Operations?.Count ?? 0,
                        ["actions"] = new JArray((plan.Operations ?? new List<IntentOperation>()).Select(o => o.Action)),
                        ["ambiguities"] = plan.Ambiguities?.Count ?? 0,
                        ["confidence"] = plan.Confidence
                    },
                    ["outcome"] = outcome,          // executed | asked | refused | error
                    ["verified"] = verifiedOk,
                    ["error_code"] = errorCode,
                    ["followup"] = (string)null,     // set later by RecordFollowup (accepted/undone/retried/rephrased)
                    ["comp_bucket"] = Bucket(componentCount),
                    ["fastener_bucket"] = Bucket(fastenerCount),
                    ["mate_bucket"] = Bucket(mateCount),
                    ["time_saved_sec"] = TimeSavedSec(handler, itemCount),
                    ["duration_ms"] = durationMs,
                    ["sw_build"] = swBuild,
                    ["tokens_in_today"] = IntentRate.TokensInToday,
                    ["tokens_out_today"] = IntentRate.TokensOutToday
                };
                Append("events.jsonl", ev);
                if (errorCode != null || outcome == "error") Append("failures.jsonl", ev);  // Rule #12: failures -> Layer 3 cases

                if (ShareUsage)
                {
                    string json = ev.ToString(Formatting.None);
                    Task.Run(async () => { try { await Cloud.PostAsync("https://www.getforge.build/api/telemetry", json); } catch { } });
                }
            }
            catch { }
        }

        // ---- CORRECTION CAPTURE (highest-signal): the delta between what Forge did and what the human wanted.
        //      signal = "undone" (user reverted Forge's action) or "rephrase" (a failed prompt then a working one). ----
        public static void LogCorrection(string handler, string maskedPrompt, string signal, string userDidMasked, string rephraseOfMasked = null)
        {
            try
            {
                var ev = new JObject
                {
                    ["schema_version"] = SchemaVersion, ["ts"] = DateTime.UtcNow.ToString("o"), ["install_id"] = InstallId,
                    ["type"] = "correction", ["handler"] = handler, ["signal"] = signal,
                    ["prompt_masked"] = maskedPrompt, ["user_did"] = userDidMasked, ["rephrase_of"] = rephraseOfMasked
                };
                Append("events.jsonl", ev); Append("corrections.jsonl", ev);
                if (ShareUsage) { string j = ev.ToString(Formatting.None); Task.Run(async () => { try { await Cloud.PostAsync("https://www.getforge.build/api/telemetry", j); } catch { } }); }
            }
            catch { }
        }

        // ---- THUMBS + one-tap reason: tagged to the exact run. rating: +1 / -1. reason: wrong_parts | too_much | didnt_work. ----
        public static void LogFeedback(string runId, string handler, int rating, string reason, string text)
        {
            try
            {
                var ev = new JObject
                {
                    ["schema_version"] = SchemaVersion, ["ts"] = DateTime.UtcNow.ToString("o"), ["install_id"] = InstallId,
                    ["type"] = "feedback", ["run_id"] = runId, ["handler"] = handler,
                    ["rating"] = rating, ["reason"] = (reason ?? "").Length > 24 ? reason.Substring(0, 24) : reason,
                    ["text"] = string.IsNullOrEmpty(text) ? null : (text.Length > 300 ? text.Substring(0, 300) : text)
                };
                Append("events.jsonl", ev);
                if (rating < 0) Append("failures.jsonl", ev);  // thumbs-down feeds the improvement backlog
                if (ShareUsage) { string j = ev.ToString(Formatting.None); Task.Run(async () => { try { await Cloud.PostAsync("https://www.getforge.build/api/telemetry", j); } catch { } }); }
            }
            catch { }
        }

        // ---- RECIPE CORPUS: every parametric generation — test-loop or user — logs
        //      description -> recipe -> API sequence -> rebuild/verify -> failure. GENERATED CONTENT ONLY (the
        //      recipe + API trail are Forge-authored, never user model data); the description is masked like any
        //      prompt. Schema-versioned (recipeSchema from the recipe itself + our event schema_version). Local
        //      recipes.jsonl is the dataset; cloud sync is a follow-up (dedicated /api/recipes sink). ----
        public const int RecipeCorpusVersion = 1;
        public static void LogRecipe(string source, string maskedDescription, JObject recipe, JArray apiTrail,
            bool built, bool verified, JObject measured, string failedOp, string error)
        {
            try
            {
                var ev = new JObject
                {
                    ["schema_version"] = SchemaVersion,
                    ["corpus_version"] = RecipeCorpusVersion,
                    ["recipe_schema"] = (recipe != null ? (int?)recipe["schemaVersion"] : null),
                    ["ts"] = DateTime.UtcNow.ToString("o"),
                    ["install_id"] = InstallId,
                    ["type"] = "recipe",
                    ["source"] = source,                       // "breaker" | "user"
                    ["description_masked"] = maskedDescription, // masked NL request (user) or generated brief (breaker)
                    ["category"] = (recipe != null ? (string)recipe["category"] : null),
                    ["recipe"] = recipe,                        // generated content — safe to store verbatim
                    ["api_trail"] = apiTrail,                   // ordered op -> api -> ok -> note
                    ["built"] = built,
                    ["verified"] = verified,
                    ["measured"] = measured,
                    ["failed_op"] = failedOp,
                    ["error"] = error
                };
                Append("recipes.jsonl", ev);                    // the corpus dataset
                if (!verified) Append("recipe-failures.jsonl", ev);  // failures the dev loop / test-loop ladder consume
            }
            catch { }
        }
        public static string RecipesPath => P("recipes.jsonl");

        // ---- FAILURE CORPUS (failures are gold). Every handler break — didn't verify, corrupted
        //      geometry (rebuild errors / over-define up), or threw — is harvested with the FULL context needed to
        //      learn from it: the masked intent, the parse, the model state BEFORE and AFTER, the attempted API/crew
        //      trail, and the exact failure mode. Written LOCALLY ONLY (never auto-synced: the before/after state
        //      holds real geometry measurements = customer IP; syncing is a separate opt-in sink). Schema-versioned.
        //      During our own testing (harness/test-loop on our models) this is pure training data; on a client machine
        //      it stays local until they choose to share. This is the highest-signal dataset Forge produces. ----
        public const int FailureCorpusVersion = 1;
        public static void LogFailure(string source, string handler, string maskedIntent, JObject plan, string docType,
            string failureMode, JObject stateBefore, JObject stateAfter, JArray attempted, JObject result, bool verified, string error)
        {
            try
            {
                var ev = new JObject
                {
                    ["schema_version"] = SchemaVersion,
                    ["corpus_version"] = FailureCorpusVersion,
                    ["ts"] = DateTime.UtcNow.ToString("o"),
                    ["install_id"] = InstallId,
                    ["type"] = "failure",
                    ["source"] = source,                 // harness | breaker | panel
                    ["handler"] = handler,
                    ["intent_masked"] = maskedIntent,
                    ["parse"] = plan,                    // ops/actions/ambiguities/confidence (may be null on offline path)
                    ["doc_type"] = docType,              // part | assembly
                    ["failure_mode"] = failureMode,      // not_verified | rebuild_errors | over_defined | exception | geometry_anomaly
                    ["state_before"] = stateBefore,      // GroundTruth baseline (run0) — real geometry state, local only
                    ["state_after"] = stateAfter,        // GroundTruth after the op (run1)
                    ["attempted"] = attempted,           // ordered crew/API trail the handler emitted
                    ["result"] = result,                 // the handler's own (masked) result object
                    ["verified"] = verified,
                    ["error"] = error
                };
                Append("failures-corpus.jsonl", ev);
            }
            catch { }
        }
        public static string FailureCorpusPath => P("failures-corpus.jsonl");

        private static readonly object _lock = new object();
        private static void Append(string file, JObject ev)
        {
            try { lock (_lock) { Directory.CreateDirectory(Dir); File.AppendAllText(P(file), ev.ToString(Formatting.None) + "\n"); } } catch { }
        }
        public static string EventsPath => P("events.jsonl");
        public static string FailuresPath => P("failures.jsonl");

        // ---- CRASH/HANG RECOVERY: a run-state lockfile. Begin before a write op, End after verification. If SW
        //      crashes or the add-in hangs mid-run, the file is left "running"; next launch reads it and tells the
        //      user exactly what was verified vs unknown — never silently pretends the last run finished. ----
        private static string LockPath => P("run.lock");
        public static void RunBegin(string handler, string maskedPrompt, int targetCount)
        {
            try { Directory.CreateDirectory(Dir); File.WriteAllText(LockPath, new JObject { ["handler"] = handler, ["prompt"] = maskedPrompt, ["targets"] = targetCount, ["state"] = "running", ["startedUtc"] = DateTime.UtcNow.ToString("o") }.ToString(Formatting.None)); } catch { }
        }
        public static void RunEnd(int verified, int total)
        {
            try { File.WriteAllText(LockPath, new JObject { ["state"] = "done", ["verified"] = verified, ["total"] = total, ["endedUtc"] = DateTime.UtcNow.ToString("o") }.ToString(Formatting.None)); } catch { }
        }
        // returns a user-facing message if the LAST run was interrupted (running), else null. Clears the lock.
        public static string CheckInterrupted()
        {
            try
            {
                if (!File.Exists(LockPath)) return null;
                var j = JObject.Parse(File.ReadAllText(LockPath));
                string state = (string)j["state"];
                try { File.Delete(LockPath); } catch { }
                if (state == "running")
                    return "Your last command (" + (string)j["handler"] + ") was interrupted before Forge could confirm it. " +
                           (int?)j["targets"] + " item(s) were in progress — please check the model; Forge didn't verify the result. Undo (Ctrl+Z) if it looks wrong.";
            }
            catch { }
            return null;
        }
    }

    internal static class Cloud
    {
        private static readonly System.Net.Http.HttpClient Http = Make();
        private static System.Net.Http.HttpClient Make()
        {
            var c = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            c.DefaultRequestHeaders.Add("Authorization", "Bearer " + ForgeConfig.ApiKey);
            return c;
        }
        public static Task PostAsync(string url, string json) => Http.PostAsync(url, new System.Net.Http.StringContent(json, Encoding.UTF8, "application/json"));
        public static async Task<JObject> GetJsonAsync(string url)
        {
            var resp = await Http.GetAsync(url);
            return JObject.Parse(await resp.Content.ReadAsStringAsync());
        }
    }
}
