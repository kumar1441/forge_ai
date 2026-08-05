using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    /// <summary>
    /// INDEPENDENT ground truth for quarantine_file (tool 257). Re-derives the pass/fail outcome counts from
    /// the SAME request text via a SEPARATE tokenize-and-lookup approach (split on non-letters, membership
    /// test against independent word sets) rather than the handler's own regexes — sharing no code. Also
    /// independently checks whether the handler's marker sidecar actually landed on disk when quarantined.
    /// </summary>
    public static partial class GroundTruth
    {
        private static readonly string[] PassWords = { "ok", "okay", "fine", "worked", "works", "working", "passed", "pass", "success", "successful", "succeeded", "clean" };
        private static readonly string[] FailWords = { "crash", "crashed", "crashes", "fail", "failed", "fails", "failing", "error", "errored", "broke", "broken" };

        public static JObject MeasureQuarantineFile(IModelDoc2 model, string intent)
        {
            var mo = new JObject();
            string c = (intent ?? "").ToLowerInvariant();
            var tokens = new System.Text.RegularExpressions.Regex(@"[a-z]+").Matches(c).Cast<System.Text.RegularExpressions.Match>().Select(m => m.Value).ToArray();

            int pass = tokens.Count(t => PassWords.Contains(t));
            int fail = tokens.Count(t => FailWords.Contains(t));
            mo["expectedPassCount"] = pass;
            mo["expectedFailCount"] = fail;
            mo["expectedQuarantined"] = pass >= 1 && fail >= 1;

            string path = null; try { path = model != null ? model.GetPathName() : null; } catch { }
            bool markerExists = false;
            if (!string.IsNullOrWhiteSpace(path)) { try { markerExists = File.Exists(path + ".forge-quarantine.json"); } catch { } }
            mo["expectedMarkerExists"] = markerExists;
            return mo;
        }
    }
}
