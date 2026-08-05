using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Forge.SolidWorks
{
    public class DimChange
    {
        public string Name;    // SolidWorks dimension full name, e.g. "D1@Sketch3"
        public string Label;   // friendly name, e.g. "bore"
        public string Unit;
        public double[] Values; // one value per variant
    }

    public class VariantPlan
    {
        public int Count;
        public bool Drawings;       // make drawings for the variants
        public bool DrawOriginal;   // also make a drawing for the base/original part
        public List<DimChange> Changes = new List<DimChange>();
        public string Description;
    }

    public class DimSnap { public string Name; public double ValueMm; }

    // A change on the project channel: a cross-tool board change (Dims == null) OR a teammate's
    // model snapshot for "CAD git" propagation (Dims != null).
    public class BifrostEvent
    {
        public string Id;
        public string Source;     // "kicad" | "altium" | "solidworks"
        public string Summary;
        public string CreatedAt;  // ISO timestamp (used as the poll cursor)
        public List<DimSnap> Dims; // present when this is a shared model version
    }

    // What the router (/api/act) decided the engineer wants.
    public class ActResult
    {
        public string Action;       // "generate_variants" | "edit" | "change_impact" | "clarify"
        public VariantPlan Variant; // when generate_variants
        public List<EditChange> EditChanges = new List<EditChange>(); // when edit
        public bool Force; // apply the edit even if it breaks something ("do it anyway")
        public string Explanation;  // when change_impact (plain-English impact)
        public List<string> Affected = new List<string>(); // feature names to highlight in 3D
        public string Clarify;      // when clarify (a question to ask the engineer)
        public string Answer;       // when answer (a short, precise reply)
        public string Description;
        public ActMeter Meter = new ActMeter();  // AI token/cost metering for this operation
        public string QuestionType;      // fact | assess | rationale | comprehend | impact | edit | variant | other
        public string QuestionSummary;  // generalized, IP-stripped topic for analytics
    }

    public class ActMeter
    {
        public long TokensIn;
        public long TokensOut;
        public int LlmCalls;
        public int CacheHits;
        public double EstCostUsd;
        public string ModelName;
    }

    /// <summary>
    /// Talks to the Forge cloud. Uses the shared plugin Bearer key (same as the MCP servers),
    /// so there is no per-user login inside SolidWorks.
    /// </summary>
    public static class ForgeApi
    {
        private const string BaseUrl = "https://www.getforge.build";

        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            c.DefaultRequestHeaders.Add("Authorization", "Bearer " + ForgeConfig.ApiKey);
            return c;
        }

        // The agentic entry point: send dimensions + dependency graph + intent to the router, which
        // classifies into an action (generate_variants / change_impact / clarify).
        public static async Task<ActResult> Act(string intent, List<DimInfo> dims, List<FeatureDep> deps, List<string> selectedFeatures, string boardChange)
        {
            var body = new
            {
                intent,
                dimensions = dims.ConvertAll(d => new { name = d.Name, valueMm = Math.Round(d.ValueMm, 4), feature = d.Feature, type = d.Type, selected = d.Selected }),
                dependencies = deps.ConvertAll(f => new { feature = f.Name, type = f.Type, affects = f.Affects }),
                selectedFeatures,
                boardChange,
                usage = new
                {
                    mode = UsageConfig.Mode,
                    model_light = UsageConfig.ModelLight,
                    model_primary = UsageConfig.ModelPrimary,
                    max_tokens_per_op = UsageConfig.MaxTokensPerOp
                }
            };

            var content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");
            HttpResponseMessage resp;
            try { resp = await Http.PostAsync(BaseUrl + "/api/act", content); }
            catch (HttpRequestException) { throw new Exception("Forge needs an internet connection to think — check your connection and try again."); }
            catch (TaskCanceledException) { throw new Exception("Forge couldn't reach the cloud (timed out) — check your connection and try again."); }
            string text = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                throw new Exception("Forge API " + (int)resp.StatusCode + ": " + text);

            JObject j = JObject.Parse(text);
            var r = new ActResult { Action = (string)j["action"] ?? "", Description = (string)j["description"] ?? "" };

            var m = j["meter"];
            if (m != null)
            {
                r.Meter = new ActMeter
                {
                    TokensIn = (long?)m["tokens_in"] ?? 0,
                    TokensOut = (long?)m["tokens_out"] ?? 0,
                    LlmCalls = (int?)m["llm_calls"] ?? 0,
                    CacheHits = (int?)m["cache_hits"] ?? 0,
                    EstCostUsd = (double?)m["est_cost_usd"] ?? 0,
                    ModelName = (string)m["model_name"]
                };
            }

            r.QuestionType = (string)j["question_type"] ?? "other";
            r.QuestionSummary = (string)j["question_summary"] ?? "";

            if (r.Action == "generate_variants")
            {
                r.Variant = ParsePlan(j);
            }
            else if (r.Action == "answer")
            {
                r.Answer = (string)j["answer"] ?? "";
                foreach (var a in (JArray)(j["affected"] ?? new JArray()))
                { var name = (string)a; if (!string.IsNullOrEmpty(name)) r.Affected.Add(name); }
            }
            else if (r.Action == "edit")
            {
                r.Force = (bool?)j["force"] ?? false;
                foreach (var c in (JArray)(j["changes"] ?? new JArray()))
                {
                    r.EditChanges.Add(new EditChange
                    {
                        Name = (string)c["dimensionName"],
                        Label = (string)c["label"] ?? (string)c["dimensionName"],
                        Unit = (string)c["unit"] ?? "mm",
                        Value = (double?)c["value"] ?? 0
                    });
                }
            }
            else if (r.Action == "change_impact")
            {
                r.Explanation = (string)j["explanation"] ?? "";
                foreach (var a in (JArray)(j["affected"] ?? new JArray()))
                {
                    var name = (string)a;
                    if (!string.IsNullOrEmpty(name)) r.Affected.Add(name);
                }
            }
            else r.Clarify = (string)j["clarify"] ?? "";
            return r;
        }

        // Bifrost: poll the cross-tool feed for changes from OTHER tools (e.g. a KiCad board change)
        // newer than `since`. Returns oldest-first so the panel shows them in order.
        public static async Task<List<BifrostEvent>> PollBifrost(string project, string since)
        {
            var list = new List<BifrostEvent>();
            string url = BaseUrl + "/api/bifrost/feed?project=" + Uri.EscapeDataString(project);
            if (!string.IsNullOrEmpty(since)) url += "&since=" + Uri.EscapeDataString(since);

            var resp = await Http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return list;
            string text = await resp.Content.ReadAsStringAsync();

            JObject j = JObject.Parse(text);
            var events = (JArray)(j["events"] ?? new JArray());
            for (int i = events.Count - 1; i >= 0; i--) // feed is newest-first; emit oldest-first
            {
                var e = events[i];
                var bev = new BifrostEvent
                {
                    Id = (string)e["id"],
                    Source = (string)e["source"],
                    Summary = (string)e["summary"],
                    CreatedAt = (string)e["created_at"],
                };
                var dimsArr = e["payload"]?["dims"] as JArray;
                if (dimsArr != null)
                {
                    bev.Dims = new List<DimSnap>();
                    foreach (var d in dimsArr)
                        bev.Dims.Add(new DimSnap { Name = (string)d["name"], ValueMm = (double?)d["valueMm"] ?? 0 });
                }
                list.Add(bev);
            }
            return list;
        }

        // CAD git: share the current part's dimensions to the project channel so teammates can pull.
        public static async Task PushModel(string project, string by, List<DimInfo> dims)
        {
            var body = new
            {
                project,
                source = "solidworks",
                summary = (string.IsNullOrEmpty(by) ? "A teammate" : by) + " shared their version of " + project,
                payload = new { by, dims = dims.ConvertAll(d => new { name = d.Name, valueMm = Math.Round(d.ValueMm, 4) }) }
            };
            var content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");
            await Http.PostAsync(BaseUrl + "/api/bifrost/push", content);
        }

        private static VariantPlan ParsePlan(JObject j)
        {
            var plan = new VariantPlan
            {
                Count = (int?)j["count"] ?? 0,
                Drawings = (bool?)j["drawings"] ?? false,
                DrawOriginal = (bool?)j["drawOriginal"] ?? false,
                Description = (string)j["description"] ?? ""
            };

            foreach (var c in (JArray)(j["changes"] ?? new JArray()))
            {
                var vals = new List<double>();
                foreach (var v in (JArray)(c["values"] ?? new JArray()))
                    vals.Add(v.Value<double>());

                plan.Changes.Add(new DimChange
                {
                    Name = (string)c["dimensionName"],
                    Label = (string)c["label"] ?? (string)c["dimensionName"],
                    Unit = (string)c["unit"] ?? "mm",
                    Values = vals.ToArray()
                });
            }

            if (plan.Count <= 0)
            {
                int max = 0;
                foreach (var c in plan.Changes) if (c.Values.Length > max) max = c.Values.Length;
                plan.Count = max;
            }
            return plan;
        }
    }
}
