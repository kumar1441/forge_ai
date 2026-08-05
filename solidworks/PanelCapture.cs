using System;
using System.IO;
using Newtonsoft.Json;

namespace Forge.SolidWorks
{
    /// <summary>
    /// Dev-mode OBSERVABILITY (panel-testing.md). When enabled it logs EVERYTHING the panel does — every inbound
    /// user message, the intent internals (raw text, parsed action, confidence, ambiguities, routed handler,
    /// acted-vs-asked), and every rendered outbound message — to a JSONL file so we + the test-loop can see every
    /// teeny-tiny detail of a human-like session and build the failure corpus.
    ///
    /// It grants NO powers: it is read-only logging of the SAME UI a person drives, off by default, and never
    /// touches the model. Enable with the env var FORGE_PANEL_CAPTURE=1 (or drop a file %TEMP%\forge-capture\ON).
    /// Best-effort throughout — a logging failure never disturbs the panel.
    /// </summary>
    public static class PanelCapture
    {
        private static readonly object Gate = new object();
        private static bool? _enabled;
        private static string _file;

        public static bool Enabled
        {
            get
            {
                if (_enabled.HasValue) return _enabled.Value;
                bool on = false;
                try
                {
                    string env = Environment.GetEnvironmentVariable("FORGE_PANEL_CAPTURE");
                    on = env == "1" || string.Equals(env, "true", StringComparison.OrdinalIgnoreCase);
                    if (!on) on = File.Exists(Path.Combine(Dir(), "ON"));   // file flag = easy toggle without restart
                }
                catch { }
                _enabled = on;
                return on;
            }
        }

        // Re-read the flag (the file toggle can change mid-session without a restart).
        public static void Refresh() { _enabled = null; }

        private static string Dir()
        {
            string d = Path.Combine(Path.GetTempPath(), "forge-capture");
            try { if (!Directory.Exists(d)) Directory.CreateDirectory(d); } catch { }
            return d;
        }

        private static string File_()
        {
            if (_file != null) return _file;
            // one file per add-in session; UTC stamp so runs sort chronologically
            string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            _file = Path.Combine(Dir(), "panel-" + stamp + ".jsonl");
            return _file;
        }

        // kind: "in" (user msg) | "parse" (intent internals) | "out" (rendered panel msg) | "note"
        public static void Log(string kind, object payload)
        {
            if (!Enabled) return;
            try
            {
                var row = new
                {
                    ts = DateTime.UtcNow.ToString("o"),
                    kind,
                    data = payload
                };
                string line = JsonConvert.SerializeObject(row);
                lock (Gate) { File.AppendAllText(File_(), line + Environment.NewLine); }
            }
            catch { /* observability is best-effort — never disturb the panel */ }
        }
    }
}
