using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;

namespace Forge.SolidWorks
{
    public class QuarantineFileResult
    {
        public bool Success;
        public int PassCount;
        public int FailCount;
        public bool Quarantined;
        public string MarkerPath;   // written only when Quarantined
        public string Info;
        public string Error;
    }

    /// <summary>
    /// QuarantineFile (tool 257, READ + WRITE-of-state) — "a file that fails inconsistently (works, crashes,
    /// works): flag, isolate from batch, report for human eyes — never retry-loop a poltergeist."
    ///
    /// ARCHITECTURAL NOTE (why this doesn't self-track crash history): if SolidWorks genuinely crashes, the
    /// in-process add-in dies WITH it — Forge cannot observe or persist "I just crashed" from inside a dead
    /// process. The real caller here is an EXTERNAL batch/watchdog loop (exactly what `run-harness.ps1`'s own
    /// kill+relaunch+detect-crash cycle already is) that has been tracking a file's recent pass/fail attempts
    /// itself and is now asking Forge for a recommendation — so v1 takes that attempt HISTORY as an explicit
    /// input in the request text (e.g. "this file's last attempts were ok, crashed, ok — quarantine it?"),
    /// rather than guessing at cross-invocation persistent state. This also keeps every request fully self-
    /// contained and rerun-safe (same result every time, no hidden accumulating state to break idempotency —
    /// the same convention every other tool in this catalog relies on).
    ///
    /// Classification: mixed pass+fail words in the SAME request = a genuine poltergeist (Quarantined=true) —
    /// isolate it, write a marker sidecar, and tell a human, never auto-retry. All-pass or all-fail is NOT this
    /// tool's job (all-fail is a normal broken file — `detect_file_health`'s territory, not inconsistency).
    /// </summary>
    public static class QuarantineFile
    {
        private static readonly Regex PassWord = new Regex(@"\b(ok|okay|fine|worked|works|working|passed|pass|success|successful|succeeded|clean)\b", RegexOptions.IgnoreCase);
        private static readonly Regex FailWord = new Regex(@"\b(crash(ed|es)?|fail(ed|s|ing)?|error(ed)?|broke|broken)\b", RegexOptions.IgnoreCase);

        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            bool quarantineWord = Regex.IsMatch(c, @"\bquarantine\b");
            bool isolateFileWord = Regex.IsMatch(c, @"\bisolate\b") && Regex.IsMatch(c, @"\b(file|files|document|documents)\b");
            bool poltergeistWord = Regex.IsMatch(c, @"\bpoltergeist\b") || Regex.IsMatch(c, @"\bfails? inconsistently\b") || Regex.IsMatch(c, @"\bworks?\b.{0,20}\bcrash");
            return quarantineWord || isolateFileWord || poltergeistWord;
        }

        public static async Task<QuarantineFileResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new QuarantineFileResult();
            string c = intent ?? "";

            int passCount = PassWord.Matches(c).Count;
            int failCount = FailWord.Matches(c).Count;
            res.PassCount = passCount;
            res.FailCount = failCount;

            if (passCount == 0 && failCount == 0)
            {
                res.Error = "No attempt history in this request — tell me the recent outcomes, e.g. \"last attempts: ok, crashed, ok\".";
                return res;
            }

            await emit("Sentinel", "weighing " + passCount + " pass / " + failCount + " fail outcome" + ((passCount + failCount) == 1 ? "" : "s"), "run", null);

            res.Quarantined = passCount >= 1 && failCount >= 1;
            res.Success = true;

            if (res.Quarantined)
            {
                string path = null; try { path = model != null ? model.GetPathName() : null; } catch { }
                if (!string.IsNullOrWhiteSpace(path))
                {
                    try
                    {
                        string markerPath = path + ".forge-quarantine.json";
                        string json = "{\"quarantined\":true,\"passCount\":" + passCount + ",\"failCount\":" + failCount +
                                      ",\"reason\":\"fails inconsistently (poltergeist) — isolated from automated batch processing, needs a human\",\"utc\":\"" + DateTime.UtcNow.ToString("o") + "\"}";
                        File.WriteAllText(markerPath, json);
                        res.MarkerPath = markerPath;
                    }
                    catch { }
                }
                res.Info = "Inconsistent: " + passCount + " pass / " + failCount + " fail — this is a poltergeist, not a real fix-once bug. Quarantined: isolate it from the batch, never auto-retry, flag for a human." +
                           (res.MarkerPath != null ? " Marker written: " + res.MarkerPath : "");
            }
            else if (failCount == 0)
            {
                res.Info = passCount + " pass, 0 fail — consistently fine, no quarantine needed.";
            }
            else
            {
                res.Info = failCount + " fail, 0 pass — consistently broken, not a poltergeist (that's a real bug to fix, see detect_file_health), not quarantined here.";
            }
            await emit("Sentinel", null, "done", res.Info);
            return res;
        }
    }
}
