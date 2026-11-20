using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Forge.Providers;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    /// <summary>
    /// CreateFromRecipe — the chat path for from-scratch builds BEYOND a bare primitive or a confident
    /// two-step compound ("mounting bracket 100x80x8 with 4 counterbored M5 holes and 2mm fillets").
    /// Those intents hit CreateGuardrail in the single-shape handlers, so without this route the chat
    /// honestly refuses them. With a provider configured, the user's own LLM authors a feature RECIPE
    /// (the schema RecipeExecutor already executes + verifies fail-closed), which then builds a NATIVE
    /// parametric part/assembly that stays open in SolidWorks — exactly what the edit tools then refine.
    ///
    /// Fail-closed throughout: no provider -> caller falls through to the existing honest refusal; bad
    /// recipe JSON / unknown ops / executor failure -> an honest error naming the failed step, never a
    /// silent partial. Keyless (no provider) chat keeps today's behaviour byte-for-byte.
    /// </summary>
    public static class CreateFromRecipe
    {
        private static readonly Regex CreateVerbRx = new Regex(
            @"\b(create|make|build|design|generate|model|fabricate|draft)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // Shape/document nouns worth a multi-step build (must pair with a create verb above).
        private static readonly Regex ShapeNounRx = new Regex(
            @"\b(part|assembly|plate|block|cube|bracket|flange|cylinder|boss|box|housing|enclosure|mount|washer|ring|tube|pipe|cone|sphere|torus|shaft|rod|base|frame|cover|lid|panel|blank|slab)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly HashSet<string> KnownOps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "sketch", "extrude", "cut", "hole", "linear_pattern", "circular_pattern",
            "fillet", "chamfer", "plane", "component", "mate"
        };

        /// <summary>
        /// True when this intent is a refused-class CREATE: create-like AND carrying a guardrail cue
        /// (second feature, position, machining detail, connector), but NOT a confident two-step compound
        /// (that keeps its deterministic path). Bare primitives return false — they already build.
        /// </summary>
        public static bool IsRecipeCreate(string intent)
        {
            string text = (intent ?? "").Trim();
            if (text.Length == 0) return false;
            if (!CreateVerbRx.IsMatch(text)) return false;
            if (!ShapeNounRx.IsMatch(text)) return false;
            if (CreateGuardrail.UnsupportedCue(text) == null) return false; // bare primitive: existing path
            var comp = CompoundCreate.Parse(text);
            if (comp != null && comp.IsCompound && comp.Error == null) return false; // confident 2-step: existing path
            return true;
        }

        public class RecipeCreateOutcome
        {
            public bool Attempted;      // false -> caller falls through to existing paths
            public bool Verified;
            public string Summary;      // human-readable result line
            public string Error;
        }

        public static async Task<RecipeCreateOutcome> Run(
            ISldWorks app, string intent, Func<string, string, string, string, Task> emit)
        {
            var outcome = new RecipeCreateOutcome();
            if (!IsRecipeCreate(intent)) return outcome;

            IProviderClient client;
            try
            {
                client = ProviderFactory.Resolve();
                if (client == null) return outcome; // keyless: fall through to the honest refusal
            }
            catch { return outcome; }

            outcome.Attempted = true;
            await emit("Builder", "planning the multi-step build", "run", null);

            JObject recipeJson;
            try
            {
                recipeJson = await AuthorRecipe(client, intent);
                if (recipeJson == null)
                {
                    outcome.Error = "I couldn't turn that into a build plan — try splitting it into smaller steps (e.g. the plate first, then the holes).";
                    return outcome;
                }
            }
            catch (Exception ex)
            {
                GroundTruth.Trace?.Invoke("create-recipe: author failed (" + ex.GetType().Name + ")");
                outcome.Error = "The build planner failed (" + ex.GetType().Name + ") — try again or split the request into smaller steps.";
                return outcome;
            }

            string validation = ValidateRecipe(recipeJson);
            if (validation != null) { outcome.Error = validation; return outcome; }

            // Name the recipe after the ask so the doc title is meaningful.
            try
            {
                if (string.IsNullOrWhiteSpace((string)recipeJson["name"]))
                    recipeJson["name"] = "forge-" + Regex.Replace(intent.Trim().ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
                string nm = ((string)recipeJson["name"] ?? "forge-part");
                if (nm.Length > 48) recipeJson["name"] = nm.Substring(0, 48);
            }
            catch { }

            RecipeResult res;
            try
            {
                await emit("Builder", "building " + ((string)recipeJson["name"] ?? "the part") + " feature by feature", "run", null);
                res = await Task.Run(() => RecipeExecutor.Execute(app, Recipe.FromJObject(recipeJson), Path.GetTempPath(), true));
            }
            catch (Exception ex)
            {
                outcome.Error = "The build failed before it started (" + ex.GetType().Name + ": " + ex.Message + ").";
                return outcome;
            }

            if (res == null) { outcome.Error = "The build returned nothing — check the model."; return outcome; }
            if (!string.IsNullOrEmpty(res.Error) || !string.IsNullOrEmpty(res.FailedOp))
            {
                outcome.Error = "The build stopped at '" + (res.FailedOp ?? "unknown step") + "'" +
                    (string.IsNullOrEmpty(res.Error) ? "" : ": " + res.Error) +
                    " — what built so far is open in SolidWorks; simplify that step and ask again.";
                return outcome;
            }

            outcome.Verified = res.Verified;
            outcome.Summary = Summarize(res);
            return outcome;
        }

        private static string Summarize(RecipeResult res)
        {
            var parts = new List<string> { "built " + res.FeatureCount + " features" };
            try
            {
                var box = res.Measured["boundingBoxMm"] as JObject;
                if (box != null)
                    parts.Add("measures " + box["x"] + " x " + box["y"] + " x " + box["z"] + " mm");
            }
            catch { }
            try
            {
                if (res.Measured["holesRequested"] != null)
                    parts.Add(res.Measured["cylindricalFaces"] + " cylindrical faces vs " + res.Measured["holesRequested"] + " holes asked");
            }
            catch { }
            parts.Add(res.RebuildClean ? "rebuild clean" : "rebuild has warnings — check the tree");
            if (!res.NativeTree) parts.Add("tree is not fully native");
            if (!res.DimsMatch) parts.Add("some requested dims didn't measure out — check the model");
            return "Created from scratch (" + string.Join(", ", parts) + "). It stays open — keep editing it with plain English.";
        }

        // Pre-execution sanity: the executor fails closed on its own, but rejecting unknown ops / runaway
        // sizes HERE keeps the error about the PLAN (fix the ask) instead of a half-built part.
        private static string ValidateRecipe(JObject r)
        {
            var feats = r["features"] as JArray;
            if (feats == null || feats.Count == 0) return "The plan came back empty — rephrase the request with concrete dims.";
            if (feats.Count > 40) return "That plan has " + feats.Count + " steps (cap is 40) — split it into two builds.";
            string kind = ((string)r["kind"] ?? "part").ToLowerInvariant();
            if (kind != "part" && kind != "assembly") return "Unknown recipe kind '" + kind + "'.";
            foreach (var t in feats)
            {
                var f = t as JObject; if (f == null) return "A plan step isn't an object — ask again.";
                string op = ((string)f["op"] ?? "").Trim().ToLowerInvariant();
                if (!KnownOps.Contains(op)) return "The plan uses '" + op + "', which the builder doesn't know — rephrase without it.";
            }
            return null;
        }

        private static async Task<JObject> AuthorRecipe(IProviderClient client, string intent)
        {
            var opts = new ProviderOptions { MaxTokens = 2500, Temperature = 0, Model = ProviderFactory.CurrentModel() };
            string user = "User said: \"" + (intent ?? "").Trim() + "\"\n\nOutput ONLY the recipe JSON object. No markdown fences, no commentary.";
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var msgs = new List<ChatMessage>
                {
                    new ChatMessage { Role = "system", Content = RecipeSystemPrompt },
                    new ChatMessage { Role = "user", Content = attempt == 0 ? user : user + " JSON object only." }
                };
                ProviderResult res;
                try { res = await client.CompleteAsync(msgs, opts, CancellationToken.None); }
                catch { return null; }
                string err;
                var recipe = SafeJsonExtractor.ExtractResponseEnvelope(res != null ? res.Text : null, out err);
                if (recipe != null && recipe["features"] is JArray)
                {
                    try { IntentRate.Note(res.TokIn, res.TokOut); } catch { }
                    try { GroundTruth.Trace?.Invoke("create-recipe: authored model=" + res.Model); } catch { }
                    return recipe;
                }
            }
            return null;
        }

        // The builder's contract, derived from RecipeExecutor's op switch. Deliberately narrow: every op
        // here is live-SW-API-backed and verified fail-closed; anything else is rejected by ValidateRecipe.
        private const string RecipeSystemPrompt =
            "You author parametric CAD feature recipes as JSON. Rules: ALL dimensions in MILLIMETRES. " +
            "First feature is usually a sketch on Front/Top/Right. Extrude/cut reference sketch ids. " +
            "Reply with ONLY the JSON object.\n" +
            "Schema: {\"kind\":\"part|assembly\",\"name\":\"short-slug\",\"units\":\"mm\"," +
            "\"features\":[...],\"verify\":{\"boundingBoxMm\":{\"x\":N,\"y\":N,\"z\":N},\"holes\":N,\"features\":N}}\n" +
            "Feature ops (id strings link steps):\n" +
            "- {\"op\":\"sketch\",\"id\":\"s1\",\"plane\":\"Front|Top|Right|top|bottom|front|back|left|right|offset:<plane>:<mm>|<plane-id>\"," +
            "\"entities\":[{\"type\":\"rectangle\",\"x\":N,\"y\":N,\"w\":N,\"h\":N}|{\"type\":\"circle\",\"cx\":N,\"cy\":N,\"r\":N}|" +
            "{\"type\":\"line\",\"x1\":N,\"y1\":N,\"x2\":N,\"y2\":N}|{\"type\":\"polygon\",\"cx\":N,\"cy\":N,\"r\":N,\"sides\":N}|" +
            "{\"type\":\"slot\",\"x1\":N,\"y1\":N,\"x2\":N,\"y2\":N,\"r\":N}]} " +
            "Sketch coords are offsets on that plane in mm.\n" +
            "- {\"op\":\"extrude\",\"id\":\"e1\",\"sketch\":\"s1\",\"depthMm\":N,\"dir\":\"positive|negative|both\",\"merge\":true}\n" +
            "- {\"op\":\"cut\",\"id\":\"c1\",\"sketch\":\"s2\",\"depthMm\":N,\"throughAll\":true|false,\"dir\":\"positive|negative\"}\n" +
            "- {\"op\":\"hole\",\"id\":\"h1\",\"onFace\":\"top|bottom|front|back|left|right|<plane-id>\",\"standard\":\"simple|counterbore|countersink|tapped\"," +
            "\"diameterMm\":N,\"depthMm\":N,\"throughAll\":true,\"positions\":[{\"x\":N,\"y\":N}]} " +
            "Positions are mm offsets on that face. Counterbore/countersink get a shallow second cut automatically.\n" +
            "- {\"op\":\"linear_pattern\",\"id\":\"p1\",\"feature\":\"h1\",\"dir\":\"x|y|z\",\"count\":N,\"spacingMm\":N}\n" +
            "- {\"op\":\"circular_pattern\",\"id\":\"p1\",\"feature\":\"h1\",\"axis\":\"x|y|z\",\"count\":N,\"angleDeg\":360,\"equal\":true}\n" +
            "- {\"op\":\"fillet\",\"id\":\"f1\",\"target\":\"all_edges|vertical_edges|top_face_edges|bottom_face_edges|feature:<id>\",\"radiusMm\":N}\n" +
            "- {\"op\":\"chamfer\",\"id\":\"c1\",\"target\":\"all_edges|vertical_edges|top_face_edges|bottom_face_edges|feature:<id>\",\"distanceMm\":N,\"angleDeg\":45}\n" +
            "- {\"op\":\"plane\",\"id\":\"pl1\",\"from\":\"Top|Front|Right\",\"offsetMm\":N}\n" +
            "Assembly only: {\"op\":\"component\",\"id\":\"a\",\"recipe\":{nested PART recipe},\"atMm\":{\"x\":N,\"y\":N,\"z\":N}} " +
            "(no partPath: nested recipe builds the part), " +
            "{\"op\":\"mate\",\"type\":\"coincident|concentric|distance\",\"a\":{\"component\":\"a\"},\"b\":{\"component\":\"b\"},\"distanceMm\":N}. " +
            "Mates resolve largest-planar / smallest-cylindrical faces automatically.\n" +
            "Verify block: include boundingBoxMm ONLY if the request states overall size; holes = through-hole count asked; " +
            "features = minimum feature count. Prefer simple|throughAll holes, blind extrudes, top_face_edges fillets.";
    }
}
