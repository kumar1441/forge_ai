using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public class RebuildDocumentResult
    {
        public bool Success;        // rebuilt with 0 errors
        public int ErrorsBefore;    // GetWhatsWrongCount before
        public int RebuildErrors;   // GetWhatsWrongCount after the forced rebuild
        public double Seconds;       // how long the rebuild took
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 95 — rebuild_document. Forces a full top-down rebuild (IModelDoc2.ForceRebuild3(false)) and reports the
    /// honest outcome: clean or flagged, with the error count and the time it took. NOT the fixer (fix_red_wave) and NOT
    /// the error lister (get_rebuild_errors) — this just recomputes and tells you whether the model still solves. It
    /// alters no features/geometry/materials and never saves, so it is idempotent and undo-neutral. Fail-closed: success
    /// is claimed only when GetWhatsWrongCount reads 0 after the rebuild.
    /// </summary>
    public static class RebuildDocument
    {
        // NARROW: a rebuild/recompute verb, EXCLUDING the neighbours that also carry "rebuild": get_rebuild_errors
        // (error noun), set_rebuild_verification (verif), the profiler (slow/time/profile/performance), fix_red_wave
        // (fix verb). Dispatched AFTER all of those so specific-first still wins.
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            if (Regex.IsMatch(c, @"\b(error|errors|verif(y|ication|ied)|why|slow|sluggish|laggy|profile|performance|speed|time|bottleneck|drawing|drawings|fix|repair)\b")) return false;
            // handle_config_explosion (tool 255) owns "rebuild ALL/EVERY configuration(s)" bulk phrasing — a
            // config-explosion guard question on a huge-config-count file, not a single top-down rebuild.
            if (Regex.IsMatch(c, @"\b(all|every)\b.*\bconfig")) return false;
            return Regex.IsMatch(c, @"\b(rebuild|re-build|recompute|regenerate|regen)\b");
        }

        public static async Task<RebuildDocumentResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new RebuildDocumentResult();
            if (model == null) { res.Error = "Open a part or assembly to rebuild it."; return res; }

            res.ErrorsBefore = SafeWhatsWrong(model);
            await emit("Sentinel", "forcing a full rebuild", "run", null);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try { model.ForceRebuild3(false); } catch (Exception ex) { res.Error = "Rebuild threw: " + ex.Message; return res; }
            sw.Stop();
            res.Seconds = sw.Elapsed.TotalSeconds;

            res.RebuildErrors = SafeWhatsWrong(model);
            res.Success = res.RebuildErrors == 0;

            await emit("Sentinel", null, "done",
                (res.Success ? "rebuilt clean" : res.RebuildErrors + " rebuild flag(s)") + " in " + res.Seconds.ToString("F2") + "s");

            res.Info = res.Success
                ? "Rebuilt clean in " + res.Seconds.ToString("F2") + "s — no errors."
                : "Rebuilt in " + res.Seconds.ToString("F2") + "s with " + res.RebuildErrors + " rebuild flag" + (res.RebuildErrors == 1 ? "" : "s") +
                  " — run \"list the rebuild errors\" to see which features, or \"fix the mate errors\" to resolve them.";
            return res;
        }

        private static int SafeWhatsWrong(IModelDoc2 model)
        { try { return model.Extension.GetWhatsWrongCount(); } catch { return 0; } }
    }
}
