using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    // ---- parsed plan from /api/intent (LANGUAGE understanding only; targets are DESCRIPTORS) ----
    public class IntentTarget { public string Selector; public string Value; public string Kind; public string Position; }
    public class IntentOperation
    {
        public string Action;
        public List<IntentTarget> Targets = new List<IntentTarget>();
        public JObject Parameters = new JObject();
        public string Material => (string)Parameters?["material"];
        public string Scope => (string)Parameters?["scope"];
        public string From => (string)Parameters?["from"];
        public string To => (string)Parameters?["to"];
    }
    public class IntentPlan
    {
        public List<IntentOperation> Operations = new List<IntentOperation>();
        public List<string> Ambiguities = new List<string>();
        public double Confidence;
        public string Error;
    }

    /// <summary>
    /// The intent layer: parse (cloud LLM) -> resolve (deterministic, here) -> confirm-or-ask -> execute -> verify.
    /// Handlers stop keyword-matching raw text (Robustness Rule #1). Targets from the parser are DESCRIPTORS; this
    /// class resolves them to real components using fastener classification + spatial position + fuzzy names (Rule #2:
    /// zero/multiple matches -> ambiguity, never a guess).
    /// </summary>
    public static class IntentLayer
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        private const string BaseUrl = "https://www.getforge.build";

        // ---- PARSE: raw prompt + live context (+ short per-doc session memory for "do the same"/"the other") -> plan ----
        public static async Task<IntentPlan> Parse(string prompt, IModelDoc2 model)
        {
            var plan = new IntentPlan();
            if (!IntentRate.Allow()) { plan.Error = "Slow down — too many commands in a short time. Try again in a moment."; return plan; }
            string docKey = SessionMemory.KeyFor(model);
            var ctxObj = JObject.Parse(BuildContext(model));
            var recent = SessionMemory.Recent(docKey);
            if (recent.Count > 0) ctxObj["recentCommands"] = new JArray(recent);   // resolve pronouns/references
            // BYOK first: the user's own configured provider pays for the parse; null -> hosted path.
            var direct = await IntentParseDirect.TryParse(prompt, ctxObj);
            if (direct != null)
            {
                ApplyPlan(plan, direct);
                if (plan.Operations.Count > 0 && plan.Error == null) SessionMemory.Record(docKey, prompt);
                return plan;
            }

            var body = "{\"prompt\":" + JsonConvert.SerializeObject(prompt ?? "") + ",\"context\":" + ctxObj.ToString(Formatting.None) + "}";
            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/api/intent"))
                {
                    req.Headers.Add("Authorization", "Bearer " + ForgeConfig.ApiKey);
                    req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    GroundTruth.Trace?.Invoke("intent-parse: POST /api/intent start");
                    var resp = await Http.SendAsync(req);
                    GroundTruth.Trace?.Invoke("intent-parse: POST /api/intent responded " + (int)resp.StatusCode);
                    string text = await resp.Content.ReadAsStringAsync();
                    if (!resp.IsSuccessStatusCode) { plan.Error = "Forge couldn't understand that (server " + (int)resp.StatusCode + ")."; return plan; }
                    var j = JObject.Parse(text);
                    IntentRate.Note((long?)j["meter"]?["tokens_in"] ?? 0, (long?)j["meter"]?["tokens_out"] ?? 0);
                    ApplyPlan(plan, j["plan"] as JObject);
                }
            }
            catch (Exception ex) { plan.Error = "Forge needs an internet connection to understand commands — " + ex.Message; }
            if (plan.Operations.Count > 0 && plan.Error == null) SessionMemory.Record(docKey, prompt);
            return plan;
        }

        // ---- plan JObject -> IntentPlan shaping, shared by the BYOK and hosted parse paths ----
        // Labtec bug #2: re-parsing a terse clarify answer, a free-tier LLM can return "operations" as a bare
        // scalar or an array of plain strings instead of objects. Indexing a child on that scalar JValue throws
        // "Cannot access child value on Newtonsoft.Json.Linq.JValue" — every element is type-guarded below so a
        // non-object element is skipped, never indexed.
        private static void ApplyPlan(IntentPlan plan, JObject p)
        {
            if (p == null) { plan.Error = "Empty plan."; return; }
            plan.Confidence = (double?)p["confidence"] ?? 0;
            foreach (var a in (p["ambiguities"] as JArray) ?? new JArray())
                if (a is JValue) { var s = (string)a; if (!string.IsNullOrEmpty(s)) plan.Ambiguities.Add(s); }
            foreach (var o in (p["operations"] as JArray) ?? new JArray())
            {
                var oj = o as JObject;
                if (oj == null) continue;   // scalar op (e.g. a bare action name) can never carry action/params
                var op = new IntentOperation { Action = (string)oj["action"] ?? "", Parameters = (oj["parameters"] as JObject) ?? new JObject() };
                // some params are placed at top level for non-material ops
                foreach (var key in new[] { "scope", "from", "to", "material", "target" })
                    if (oj[key] != null && op.Parameters[key] == null) op.Parameters[key] = oj[key];
                foreach (var t in (oj["targets"] as JArray) ?? new JArray())
                {
                    var tj = t as JObject;
                    if (tj == null) continue;   // scalar target can't be resolved
                    op.Targets.Add(new IntentTarget { Selector = (string)tj["selector"], Value = (string)tj["value"], Kind = (string)tj["kind"], Position = (string)tj["position"] });
                }
                plan.Operations.Add(op);
            }
        }

        // ---- CONTEXT: components with name/kind/position/material for the parser (masked-safe: names go to the parser
        //      only to resolve targets; telemetry masks them separately) ----
        public static string BuildContext(IModelDoc2 model)
        {
            var root = new JObject();
            if (model == null) { root["docType"] = "none"; return root.ToString(Formatting.None); }
            int dt = (int)model.GetType();
            root["docType"] = dt == (int)swDocumentTypes_e.swDocASSEMBLY ? "assembly" : (dt == (int)swDocumentTypes_e.swDocPART ? "part" : "drawing");
            var arr = new JArray();
            var asm = model as AssemblyDoc;
            if (asm != null)
            {
                object[] comps = asm.GetComponents(true) as object[]; // top-level
                foreach (var o in comps ?? new object[0])
                {
                    var c = o as Component2; if (c == null) continue;
                    bool sup = false; try { sup = c.IsSuppressed(); } catch { } if (sup) continue;
                    string nm = null; try { nm = c.Name2; } catch { }
                    var jo = new JObject { ["name"] = nm, ["kind"] = ClassifyKind(nm) };
                    double[] ctr = BoxCenter(c);
                    if (ctr != null) { jo["posXmm"] = Math.Round(ctr[0] * 1000, 1); jo["posYmm"] = Math.Round(ctr[1] * 1000, 1); jo["posZmm"] = Math.Round(ctr[2] * 1000, 1); }
                    try { var pd = c.GetModelDoc2() as PartDoc; if (pd != null) { string db; jo["material"] = pd.GetMaterialPropertyName2("", out db); } } catch { }
                    arr.Add(jo);
                }
            }
            root["components"] = arr;
            return root.ToString(Formatting.None);
        }

        // ---- RESOLVE: a target DESCRIPTOR -> the actual components (deterministic). Rule #2: report [] and let the
        //      caller flag ambiguity on zero/too-many, never guess. ----
        public static List<Component2> ResolveTarget(AssemblyDoc asm, IntentTarget t, out string note)
        {
            note = null;
            var all = TopLevel(asm);
            if (t == null || t.Selector == null) { note = "no target"; return new List<Component2>(); }
            switch (t.Selector.ToLowerInvariant())
            {
                case "all":
                    return all;
                case "kind":
                    {
                        var k = Singular((t.Value ?? t.Kind ?? "").ToLowerInvariant());
                        var hits = all.Where(c => ClassifyKind(SafeName(c)) == k).ToList();
                        if (hits.Count == 0) note = "no " + k + " in this assembly";
                        return hits;
                    }
                case "spatial":
                    {
                        var k = Singular((t.Kind ?? "").ToLowerInvariant());
                        var pool = string.IsNullOrEmpty(k) ? all : all.Where(c => ClassifyKind(SafeName(c)) == k).ToList();
                        if (pool.Count == 0) { note = "no " + k + " to place " + t.Position; return new List<Component2>(); }
                        var pick = PickSpatial(pool, t.Position);
                        if (pick == null) { note = "couldn't resolve '" + t.Position + " " + k + "'"; return new List<Component2>(); }
                        return new List<Component2> { pick };
                    }
                case "name":
                default:
                    {
                        var q = Singular((t.Value ?? "").ToLowerInvariant());
                        // exact-ish, then contains, then kind fallback
                        var exact = all.Where(c => Singular(SafeName(c).ToLowerInvariant()) == q).ToList();
                        if (exact.Count > 0) return exact;
                        var contains = all.Where(c => SafeName(c).ToLowerInvariant().Contains(q) || q.Contains(ClassifyKind(SafeName(c)))).ToList();
                        if (contains.Count == 0) note = "nothing matches '" + t.Value + "'";
                        return contains;
                    }
            }
        }

        // pick the component with the extreme position for top/bottom/left/right/front/back
        private static Component2 PickSpatial(List<Component2> pool, string position)
        {
            int axis; bool max;
            switch ((position ?? "top").ToLowerInvariant())
            {
                case "bottom": axis = 1; max = false; break;
                case "top": axis = 1; max = true; break;
                case "left": axis = 0; max = false; break;
                case "right": axis = 0; max = true; break;
                case "back": axis = 2; max = false; break;
                case "front": axis = 2; max = true; break;
                default: axis = 1; max = true; break;
            }
            // if the pool spreads more along a different axis, use that axis instead (assembly principal axis)
            var centers = pool.Select(BoxCenter).Where(c => c != null).ToList();
            if (centers.Count == 0) return null;
            double[] spread = new double[3];
            for (int a = 0; a < 3; a++) { var vals = centers.Select(c => c[a]); spread[a] = vals.Max() - vals.Min(); }
            if (spread[axis] < 1e-4) { int best = 0; for (int a = 1; a < 3; a++) if (spread[a] > spread[best]) best = a; axis = best; }
            Component2 pick = null; double bestv = max ? double.MinValue : double.MaxValue;
            foreach (var c in pool) { var ctr = BoxCenter(c); if (ctr == null) continue; double v = ctr[axis]; if ((max && v > bestv) || (!max && v < bestv)) { bestv = v; pick = c; } }
            return pick;
        }

        // component names (for masking prompts before logging)
        public static List<string> ComponentNames(IModelDoc2 model)
        {
            var names = new List<string>();
            var asm = model as AssemblyDoc; if (asm == null) return names;
            foreach (var o in (asm.GetComponents(true) as object[]) ?? new object[0])
            { var c = o as Component2; if (c == null) continue; try { var n = c.Name2; if (!string.IsNullOrEmpty(n)) names.Add(n); } catch { } }
            return names;
        }

        // ---- shared component classification (Rule #8: from the live model) ----
        public static string ClassifyKind(string name)
        {
            if (string.IsNullOrEmpty(name)) return "other";
            var n = name.ToLowerInvariant();
            if (Has(n, "nut", "ecrou", "dai_oc", "4032", "4034", "din 934")) return "nut";
            if (Has(n, "washer", "rondelle", "dem_ven")) return "washer";
            if (Has(n, "bolt", "screw", "hcs", "shcs", "cap", "socket", "hex", "stud", "boulon", "bulong", "din 9", "iso 40", "iso 76", "iso 106", "b18")) return "bolt";
            // French "vis" (screw) as a bare Contains collides with the English tool name "vise"/"vice" (test-loop
            // hedge change-vise-material: "GC Vise Fixture for 6 Round or Square Pieces-1" — the ASSEMBLY'S OWN
            // main body — was misclassified "bolt" purely because "vise" contains "vis", so the cloud's component
            // snapshot told the LLM parser there was no main body, only bolts/pins, and it asked instead of acting.
            if (n.Contains("vis") && !n.Contains("vise")) return "bolt";
            if (Has(n, "flange")) return "flange";
            if (Has(n, "shaft", "axle")) return "shaft";
            if (Has(n, "gear", "sproket", "sprocket", "pinion")) return "gear";
            if (Has(n, "bracket", "plate", "b2p", "mount")) return "bracket";
            return "other";
        }

        private static bool Has(string n, params string[] xs) { foreach (var x in xs) if (n.Contains(x)) return true; return false; }
        private static string Singular(string s) => (s ?? "").Trim().TrimEnd('s');
        private static string SafeName(Component2 c) { try { return c.Name2 ?? ""; } catch { return ""; } }
        private static List<Component2> TopLevel(AssemblyDoc asm)
        {
            var list = new List<Component2>();
            foreach (var o in (asm.GetComponents(true) as object[]) ?? new object[0])
            { var c = o as Component2; if (c == null) continue; bool s = false; try { s = c.IsSuppressed(); } catch { } if (!s) list.Add(c); }
            return list;
        }
        private static double[] BoxCenter(Component2 c)
        {
            try { double[] b = c.GetBox(false, false) as double[]; if (b == null || b.Length < 6) return null; return new[] { (b[0] + b[3]) / 2, (b[1] + b[4]) / 2, (b[2] + b[5]) / 2 }; }
            catch { return null; }
        }
    }

    // ---- rate/cost guard (Ravi #5): cap parse calls/min; daily token spend logged to telemetry ----
    public static class IntentRate
    {
        private static readonly Queue<DateTime> Calls = new Queue<DateTime>();
        private const int MaxPerMin = 20;
        public static long TokensInToday, TokensOutToday;
        private static DateTime _day = DateTime.UtcNow.Date;
        public static bool Allow()
        {
            lock (Calls)
            {
                var now = DateTime.UtcNow;
                while (Calls.Count > 0 && (now - Calls.Peek()).TotalSeconds > 60) Calls.Dequeue();
                if (Calls.Count >= MaxPerMin) return false;
                Calls.Enqueue(now); return true;
            }
        }
        public static void Note(long tin, long tout)
        {
            if (DateTime.UtcNow.Date != _day) { _day = DateTime.UtcNow.Date; TokensInToday = TokensOutToday = 0; }
            TokensInToday += tin; TokensOutToday += tout;
        }
    }

    // ---- session memory (Character #7): last few commands PER DOCUMENT, in memory only, cleared on doc close.
    //      Never written to disk (data-liability boundary). Lets the parser resolve "do the same" / "the other one". ----
    public static class SessionMemory
    {
        private static readonly Dictionary<string, List<string>> Mem = new Dictionary<string, List<string>>();
        private const int Keep = 5;
        public static void Record(string docKey, string prompt)
        {
            if (string.IsNullOrEmpty(docKey) || string.IsNullOrEmpty(prompt)) return;
            lock (Mem)
            {
                if (!Mem.TryGetValue(docKey, out var list)) { list = new List<string>(); Mem[docKey] = list; }
                list.Add(prompt); while (list.Count > Keep) list.RemoveAt(0);
            }
        }
        public static List<string> Recent(string docKey)
        {
            lock (Mem) { return (docKey != null && Mem.TryGetValue(docKey, out var l)) ? new List<string>(l) : new List<string>(); }
        }
        public static void Clear(string docKey) { lock (Mem) { if (docKey != null) Mem.Remove(docKey); } }

        // Normalized doc key: leaf name without extension, lowercased — stable whether the source is a
        // full path (FileCloseNotify) or a window title (GetTitle), so record-side and clear-side always match.
        public static string KeyFor(string pathOrTitle)
        {
            if (string.IsNullOrEmpty(pathOrTitle)) return null;
            try { return System.IO.Path.GetFileNameWithoutExtension(pathOrTitle).Trim().ToLowerInvariant(); }
            catch { return pathOrTitle.Trim().ToLowerInvariant(); }
        }
        public static string KeyFor(IModelDoc2 m)
        {
            if (m == null) return null;
            string s = null; try { s = m.GetPathName(); } catch { }
            if (string.IsNullOrEmpty(s)) { try { s = m.GetTitle(); } catch { } }
            return KeyFor(s);
        }
    }
}
