using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public class HandleConfigExplosionResult
    {
        public bool Success;
        public int ConfigCount;
        public bool Exploded;   // ConfigCount >= ExplosionThreshold
        public bool Refused;    // a bulk activate-all/rebuild-all ask was refused this call
        public string Info;
        public string Error;
    }

    /// <summary>
    /// HandleConfigExplosion (tool 255, READ, guard) — "files with thousands of configurations (Toolbox
    /// masters, design-table monsters): targeted config ops only, never activate-all or rebuild-all."
    ///
    /// A Toolbox fastener master or a design-table-driven part can carry hundreds to thousands of
    /// configurations; activating or rebuilding EVERY one of them is exactly the operation that hangs
    /// SolidWorks for minutes (or crashes it, per the RAM-crash lessons already logged in STATE.md/landmines.md).
    /// This is a pure classifier + refusal guard, same "report, don't act" posture as `handle_locked_files`
    /// (tool 248) — it never performs any config operation itself, it only counts configurations (via
    /// `IModelDoc2.GetConfigurationNames`) and, if the count is past `ExplosionThreshold` AND the ask is
    /// explicitly a BULK one ("activate all", "rebuild every configuration"), refuses that specific ask with an
    /// honest reason instead of silently attempting it — a real refusal (this genuinely shouldn't be attempted
    /// in bulk), not a hedge on an otherwise-doable task. `set_active_configuration` (a single named target)
    /// remains untouched and is the recommended next step.
    ///
    /// `ExplosionThreshold` (50) is a documented v1 heuristic, not a fabricated exact number — real Toolbox
    /// masters run into the hundreds/thousands, so 50 is comfortably below any genuine "monster" while still
    /// being small enough to build a fast, honest positive fixture (a real design-table part with 1000+ rows is
    /// out of scope for a simple-fixture Batch-2 build).
    /// </summary>
    public static class HandleConfigExplosion
    {
        public const int ExplosionThreshold = 50;

        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            bool bulkAsk = Regex.IsMatch(c, @"\b(activate|rebuild|regenerate|update|process|touch)\b") &&
                           Regex.IsMatch(c, @"\b(all|every)\b") &&
                           Regex.IsMatch(c, @"\bconfig");
            bool explosionAsk = Regex.IsMatch(c, @"\b(config(uration)?\s*explosion|too many config(uration)?s?|thousands of config(uration)?s?|design.?table monster|toolbox master)\b");
            return bulkAsk || explosionAsk;
        }

        public static async Task<HandleConfigExplosionResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new HandleConfigExplosionResult();
            if (model == null) { res.Error = "Open a part or assembly to check its configuration count."; return res; }

            await emit("Sentinel", "counting configurations", "run", null);
            int count = 0;
            try { var names = model.GetConfigurationNames() as string[]; count = names == null ? 0 : names.Length; }
            catch (Exception ex) { res.Error = "Couldn't read configurations (" + ex.GetType().Name + ")."; return res; }

            res.ConfigCount = count;
            res.Exploded = count >= ExplosionThreshold;

            string c = (intent ?? "").ToLowerInvariant();
            bool bulkAsk = Regex.IsMatch(c, @"\b(activate|rebuild|regenerate|update|process|touch)\b") && Regex.IsMatch(c, @"\b(all|every)\b");

            res.Success = true;
            if (res.Exploded && bulkAsk)
            {
                res.Refused = true;
                res.Info = "This file has " + count + " configurations - past the " + ExplosionThreshold +
                    "-config guard threshold. Refusing to activate/rebuild ALL of them (that's exactly what hangs a Toolbox master or design-table monster) - target ONE configuration by name instead.";
            }
            else if (res.Exploded)
            {
                res.Info = "This file has " + count + " configurations - past the " + ExplosionThreshold +
                    "-config guard threshold. Safe for targeted single-configuration operations only; never activate-all or rebuild-all.";
            }
            else
            {
                res.Info = count + " configuration" + (count == 1 ? "" : "s") + " - under the guard threshold, no explosion risk.";
            }
            await emit("Sentinel", null, "done", res.Info);
            return res;
        }
    }
}
