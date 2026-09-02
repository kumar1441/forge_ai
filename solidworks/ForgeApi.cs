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

        // Labtec bug #2: the router is an LLM and can return a SCALAR where an object or array was expected
        // (a terse clarify-answer re-parse). Indexing a child on a scalar JValue throws
        // "Cannot access child value on Newtonsoft.Json.Linq.JValue" and would crash the add-in — these readers
        // fail CLOSED (empty array / null / no children) so a malformed field can never take the panel down.
        private static JArray Arr(JToken t) => t as JArray ?? new JArray();
        private static string Str(JObject o, string key)
        {
            if (o == null) return null;
            var t = o[key];
            return t == null || t is JValue ? (string)t : null;   // an object/array value is never a string
        }
        private static double? Num(JObject o, string key)
        {
            if (o == null) return null;
            var t = o[key] as JValue;
            if (t == null) return null;
            if (t.Type == JTokenType.Integer) return (long)t;
            if (t.Type == JTokenType.Float) return (double)t;
            return null;
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
                boardChange
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

            var m = j["meter"] as JObject;   // scalar "meter" (LLM slip) -> skip, never index it
            if (m != null)
            {
                r.Meter = new ActMeter
                {
                    TokensIn = (long?)Num(m, "tokens_in") ?? 0,
                    TokensOut = (long?)Num(m, "tokens_out") ?? 0,
                    LlmCalls = (int?)Num(m, "llm_calls") ?? 0,
                    CacheHits = (int?)Num(m, "cache_hits") ?? 0,
                    EstCostUsd = Num(m, "est_cost_usd") ?? 0,
                    ModelName = Str(m, "model_name")
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
                r.Answer = Str(j, "answer") ?? "";
                foreach (var a in Arr(j["affected"]))   // affected: array of plain names (JValues)
                    if (a is JValue) { var name = (string)a; if (!string.IsNullOrEmpty(name)) r.Affected.Add(name); }
            }
            else if (r.Action == "edit")
            {
                r.Force = (bool?)j["force"] ?? false;
                foreach (var c in Arr(j["changes"]))   // each change must be an object; a scalar can never carry one
                {
                    var cj = c as JObject;
                    if (cj == null) continue;
                    r.EditChanges.Add(new EditChange
                    {
                        Name = Str(cj, "dimensionName"),
                        Label = Str(cj, "label") ?? Str(cj, "dimensionName"),
                        Unit = Str(cj, "unit") ?? "mm",
                        Value = Num(cj, "value") ?? 0
                    });
                }
            }
            else if (r.Action == "change_impact")
            {
                r.Explanation = Str(j, "explanation") ?? "";
                foreach (var a in Arr(j["affected"]))
                {
                    if (!(a is JValue)) continue;
                    var name = (string)a;
                    if (!string.IsNullOrEmpty(name)) r.Affected.Add(name);
                }
            }
            else r.Clarify = Str(j, "clarify") ?? "";
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
            var events = Arr(j["events"]);   // feed is newest-first; emit oldest-first
            for (int i = events.Count - 1; i >= 0; i--)
            {
                var ej = events[i] as JObject;
                if (ej == null) continue;   // scalar event (LLM slip) -> skip
                var bev = new BifrostEvent
                {
                    Id = Str(ej, "id"),
                    Source = Str(ej, "source"),
                    Summary = Str(ej, "summary"),
                    CreatedAt = Str(ej, "created_at"),
                };
                var payload = ej["payload"] as JObject;   // a scalar payload can never carry dims
                if (payload != null)
                {
                    var dimsArr = Arr(payload["dims"]);
                    if (dimsArr.Count > 0)
                    {
                        bev.Dims = new List<DimSnap>();
                        foreach (var d in dimsArr)
                        {
                            var dj = d as JObject;
                            if (dj == null) continue;
                            bev.Dims.Add(new DimSnap { Name = Str(dj, "name"), ValueMm = Num(dj, "valueMm") ?? 0 });
                        }
                    }
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

            foreach (var c in Arr(j["changes"]))
            {
                var cj = c as JObject;
                if (cj == null) continue;   // scalar change (LLM slip) carries no dimension
                var vals = new List<double>();
                foreach (var v in Arr(cj["values"]))
                {
                    var nv = v as JValue;
                    if (nv == null) continue;
                    if (nv.Type == JTokenType.Integer) { vals.Add((long)nv); continue; }
                    if (nv.Type == JTokenType.Float) { vals.Add((double)nv); continue; }
                }

                plan.Changes.Add(new DimChange
                {
                    Name = Str(cj, "dimensionName"),
                    Label = Str(cj, "label") ?? Str(cj, "dimensionName"),
                    Unit = Str(cj, "unit") ?? "mm",
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
