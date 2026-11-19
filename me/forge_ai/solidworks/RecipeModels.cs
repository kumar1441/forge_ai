using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Forge.SolidWorks
{
    /// <summary>
    /// A parsed feature recipe (see docs/RECIPE-SCHEMA.md). Deliberately JObject-driven — the feature ops vary a
    /// lot, so the RecipeExecutor switches on each feature's "op" and reads params from the JObject, matching how
    /// the rest of the add-in handles SW-API JSON. This keeps the schema extensible without new C# types per op.
    /// </summary>
    public class Recipe
    {
        public JObject Raw;
        public int SchemaVersion => (int?)Raw["schemaVersion"] ?? 1;
        public string Kind => ((string)Raw["kind"] ?? "part").ToLowerInvariant();          // part | assembly
        public string Category => ((string)Raw["category"] ?? "").ToLowerInvariant();       // bracket | plate | ...
        public string Name => (string)Raw["name"] ?? ("recipe-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        public string Units => ((string)Raw["units"] ?? "mm").ToLowerInvariant();
        public JArray Features => (Raw["features"] as JArray) ?? new JArray();
        public JObject Verify => Raw["verify"] as JObject;

        public bool IsAssembly => Kind == "assembly";

        public static Recipe Parse(string json)
        {
            try { return FromJObject(JObject.Parse(json)); }
            catch (Exception ex) { return new Recipe { Raw = new JObject { ["_parseError"] = ex.Message, ["features"] = new JArray() } }; }
        }
        public static Recipe FromJObject(JObject o) => new Recipe { Raw = o ?? new JObject() };

        public string ParseError => (string)Raw["_parseError"];
        public override string ToString() => Raw?.ToString(Formatting.None) ?? "{}";

        // ---- the CURRENT scope the create-part handler will build (v1). Schema can express more; handler refuses beyond. ----
        public static readonly string[] V1Categories = { "bracket", "plate", "flange", "spacer", "enclosure" };
    }

    /// <summary>
    /// What the executor returns after running a recipe: whether it built + VERIFIED (fail-closed), the produced
    /// file, the measured geometry, the native-tree check, the ordered API step trail (for the recipe corpus),
    /// and any failure detail. verified is true ONLY when rebuild is clean AND the tree is native AND the
    /// requested verify dims matched what was measured on the real body.
    /// </summary>
    public class RecipeResult
    {
        public bool Built;                 // the executor ran to completion (file saved)
        public bool RebuildClean;          // GetWhatsWrongCount == 0
        public bool NativeTree;            // real modeling features present (not a dumb solid)
        public bool DimsMatch;             // verify block measured OK on the result
        public bool Verified => Built && RebuildClean && NativeTree && DimsMatch;
        public string Path;                // produced .SLDPRT / .SLDASM
        public int FeatureCount;
        public JObject Measured;           // measured bounding box / hole counts etc.
        public JArray ApiTrail;            // ordered list of {op, api, ok, note} — the corpus's API-sequence
        public string Error;               // first fatal error, if any
        public string FailedOp;            // the op that failed

        public RecipeResult() { ApiTrail = new JArray(); Measured = new JObject(); }
        public void Step(string op, string api, bool ok, string note = null)
        { ApiTrail.Add(new JObject { ["op"] = op, ["api"] = api, ["ok"] = ok, ["note"] = note }); }
    }
}
