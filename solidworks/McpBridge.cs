using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Forge.SolidWorks
{
    /// <summary>
    /// McpBridge — a minimal MCP (Model Context Protocol) client over stdio for .NET Framework 4.8.
    /// Launches the SolidWorks-MCP server (default: `uvx solidworks-mcp`) as a child process and speaks
    /// newline-delimited JSON-RPC 2.0: initialize -> notifications/initialized -> tools/list -> tools/call.
    /// One bridge per SolidWorks session (started lazily on first use, stopped in DisconnectFromSW).
    /// The server drives the RUNNING SolidWorks instance itself, so chat can build multi-part models
    /// and assemblies without reimplementing the 88-tool surface natively.
    /// </summary>
    public static class McpBridge
    {
        private static readonly object Gate = new object();
        private static Process Proc;
        private static StreamWriter Stdin;
        private static Thread ReaderThread;
        private static Thread DrainThread;
        private static int NextId;
        private static readonly ConcurrentDictionary<int, TaskCompletionSource<string>> Pending =
            new ConcurrentDictionary<int, TaskCompletionSource<string>>();
        private static JArray CachedTools;
        // Serializes EnsureStarted handshakes: without this two concurrent callers each
        // start a server, the second kills the first's process, and both handshakes fail.
        private static readonly SemaphoreSlim StartGate = new SemaphoreSlim(1, 1);

        public class McpException : Exception
        {
            public McpException(string message) : base(message) { }
        }

        private class McpConfig
        {
            public string Command = "uvx";
            public string[] Args = new[] { "solidworks-mcp" };
            public int StartTimeoutMs = 300000;
            public bool Enabled = true;
            public int TimeoutMs = 300000;
        }

        private static McpConfig LoadConfig()
        {
            var cfg = new McpConfig();
            try
            {
                string p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Forge", "mcp.json");
                if (!File.Exists(p)) return cfg;
                var j = JObject.Parse(File.ReadAllText(p));
                if (!string.IsNullOrWhiteSpace((string)j["command"])) cfg.Command = ((string)j["command"]).Trim();
                var a = j["args"] as JArray;
                if (a != null && a.Count > 0)
                {
                    var list = new List<string>();
                    foreach (var t in a) { string s = (string)t; if (!string.IsNullOrWhiteSpace(s)) list.Add(s); }
                    if (list.Count > 0) cfg.Args = list.ToArray();
                }
                int tmo = (int?)j["startTimeoutMs"] ?? 0;
                if (tmo >= 30000) cfg.StartTimeoutMs = tmo;
                if (j["enabled"] != null) { try { cfg.Enabled = (bool)j["enabled"]; } catch { cfg.Enabled = true; } }
                int ptmo = (int?)j["timeoutMs"] ?? 0;
                if (ptmo >= 30000) cfg.TimeoutMs = ptmo;
            }
            catch { }
            return cfg;
        }

        private static string ConfigPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Forge", "mcp.json");
        }

        /// <summary>Persist MCP settings (merges with the existing file so command/args survive).</summary>
        public static void SaveConfig(bool? enabled, int timeoutMs)
        {
            try
            {
                string p = ConfigPath();
                JObject j = new JObject();
                try { if (File.Exists(p)) j = JObject.Parse(File.ReadAllText(p)); } catch { j = new JObject(); }
                if (enabled.HasValue) j["enabled"] = enabled.Value;
                if (timeoutMs >= 30000) j["timeoutMs"] = timeoutMs;
                Directory.CreateDirectory(Path.GetDirectoryName(p));
                File.WriteAllText(p, j.ToString(Newtonsoft.Json.Formatting.None));
            }
            catch { }
        }

        public static bool IsRunning
        {
            get { lock (Gate) { try { return Proc != null && !Proc.HasExited; } catch { return false; } } }
        }

    /// <summary>Launch configuration for status display (read-only snapshot).</summary>
    public static void Describe(out string command, out string args)
    {
        bool enabledDummy;
        int timeoutDummy;
        DescribeFull(out command, out args, out enabledDummy, out timeoutDummy);
    }

    public static void DescribeFull(out string command, out string args, out bool enabled, out int timeoutMs)
    {
        var cfg = LoadConfig();
        command = cfg.Command ?? "";
        args = cfg.Args != null ? string.Join(" ", cfg.Args) : "";
        enabled = cfg.Enabled;
        timeoutMs = cfg.TimeoutMs;
    }

        /// <summary>Start the server + handshake if needed. Throws McpException with a setup hint on failure.</summary>
        public static async Task EnsureStartedAsync(int timeoutMs = 0, CancellationToken ct = default(CancellationToken))
        {
            if (!LoadConfig().Enabled)
                throw new McpException("MCP server is disabled — turn it on in Settings → MCP server, then retry.");
            int initTimeout;
            bool acquired = false;
            try
            {
                if (ct.CanBeCanceled) await StartGate.WaitAsync(ct).ConfigureAwait(false);
                else await StartGate.WaitAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; } // stop during download: never enter the handshake
            acquired = true;
            try
            {
                lock (Gate)
                {
                    if (Proc != null) { try { if (!Proc.HasExited) return; } catch { } }
                    int want = timeoutMs > 0 ? timeoutMs : LoadConfig().StartTimeoutMs;
                    if (want < 30000) want = 60000; // StartLocked ignores <30s; handshake must still bound itself
                    initTimeout = want;
                    StartLocked();
                }
                // Handshake outside the process lock but inside the start gate (IO).
                try
                {
                    var init = new JObject
                    {
                        ["protocolVersion"] = "2024-11-05",
                        ["capabilities"] = new JObject(),
                        ["clientInfo"] = new JObject { ["name"] = "Forge", ["version"] = "1.0" }
                    };
                    JObject res = await RpcAsync("initialize", init, initTimeout, ct).ConfigureAwait(false);
                    if (res == null) throw new McpException("MCP initialize returned nothing.");
                    await NotifyAsync("notifications/initialized", new JObject());
                }
                catch (OperationCanceledException) { Stop(); throw; } // stop wins: never rewrap cancel as a handshake error
                catch (Exception ex)
                {
                    Stop();
                    if (ex is McpException) throw;
                    throw new McpException("MCP handshake failed: " + ex.Message + " — run `uvx solidworks-mcp --selftest` with SolidWorks open and retry.");
                }
            }
            finally { if (acquired) { try { StartGate.Release(); } catch { } } }
        }

        private static void StartLocked()
        {
            StopLocked();
            var cfg = LoadConfig();
            var psi = new ProcessStartInfo
            {
                FileName = cfg.Command,
                // Quote args containing spaces so installs under e.g. "C:\Program Files" launch.
                Arguments = string.Join(" ", Array.ConvertAll(cfg.Args, a => a.Contains(" ") && !a.StartsWith("\"") ? "\"" + a + "\"" : a)),
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true
            };
            try
            {
                Proc = Process.Start(psi);
                if (Proc == null) throw new McpException("Could not launch `" + cfg.Command + " " + string.Join(" ", cfg.Args) + "` — install uv (astral.sh/uv) and retry.");
                Stdin = new StreamWriter(Proc.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = true };
            }
            catch (McpException) { StopLocked(); throw; }
            catch (Exception ex)
            {
                StopLocked();
                throw new McpException("Could not launch `" + cfg.Command + "` (" + ex.GetType().Name + ": " + ex.Message + ") — install uv from astral.sh/uv, then `uvx solidworks-mcp --selftest` with SolidWorks open.");
            }

            ReaderThread = new Thread(ReaderLoop) { IsBackground = true, Name = "forge-mcp-reader" };
            ReaderThread.Start(Proc); // pass the process: the field may be replaced by a restart
            DrainThread = new Thread(DrainLoop) { IsBackground = true, Name = "forge-mcp-stderr" };
            DrainThread.Start(Proc.StandardError);
        }

        private static void ReaderLoop(object state)
        {
            var proc = state as Process;
            try
            {
                string line;
                var reader = proc != null ? proc.StandardOutput : Proc.StandardOutput;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    // A hostile/garbled server line must never reach the JSON parser unbounded.
                    if (line.Length > 1000000) continue;
                    int id = -1;
                    TaskCompletionSource<string> tcs = null;
                    try
                    {
                        var msg = JObject.Parse(line);
                        var jt = msg["id"];
                        if (jt == null || jt.Type == JTokenType.Null) continue; // notification from server: ignore
                        // Servers may echo string ids ("12"): never drop a reply on a cast.
                        if (jt.Type == JTokenType.Integer) id = jt.Value<int>();
                        else if (jt.Type == JTokenType.String) { if (!int.TryParse((string)jt, out id)) continue; }
                        else { try { id = Convert.ToInt32(((JValue)jt).Value); } catch { continue; } }
                        Pending.TryRemove(id, out tcs);
                    }
                    catch { continue; }
                    if (tcs != null) { try { tcs.TrySetResult(line); } catch { } }
                }
            }
            catch { }
            // Server died: fail everything outstanding. If a restart already swapped
            // in a new Proc, the new reader owns the current ids — don't steal them.
            lock (Gate) { if (!object.ReferenceEquals(Proc, proc)) return; }
            int[] dead;
            try { dead = new System.Collections.Generic.List<int>(Pending.Keys).ToArray(); }
            catch { dead = new int[0]; }
            foreach (int key in dead)
            {
                TaskCompletionSource<string> tcs;
                if (Pending.TryRemove(key, out tcs))
                    try { tcs.TrySetException(new McpException("MCP server exited (is SolidWorks still running?).")); } catch { }
            }
        }

        private static void DrainLoop(object err)
        {
            try { var r = err as StreamReader; if (r == null) return; while (r.ReadLine() != null) { } }
            catch { }
        }

        public static void Stop()
        {
            lock (Gate) { StopLocked(); }
        }

        private static void StopLocked()
        {
            CachedTools = null; // invalidate: a restarted server may serve a different tool set
            foreach (var kv in Pending)
            {
                TaskCompletionSource<string> tcs;
                if (Pending.TryRemove(kv.Key, out tcs))
                    // Fail with a real error, not TaskCanceledException: waiters report
                    // "server stopped" instead of a bare "a task was canceled".
                    try { tcs.TrySetException(new McpException("MCP server stopped.")); } catch { }
            }
            try { if (Stdin != null) Stdin.Close(); } catch { }
            Stdin = null;
            try
            {
                if (Proc != null)
                {
                    if (!Proc.HasExited) { try { Proc.Kill(); } catch { } }
                    try { Proc.Dispose(); } catch { }
                }
            }
            catch { }
            Proc = null;
        }

        private static Task NotifyAsync(string method, JObject p)
        {
            var msg = new JObject { ["jsonrpc"] = "2.0", ["method"] = method };
            if (p != null) msg["params"] = p;
            lock (Gate)
            {
                if (Stdin == null) throw new McpException("MCP server is not running.");
                Stdin.WriteLine(msg.ToString(Newtonsoft.Json.Formatting.None));
            }
            return Task.FromResult(0);
        }

        private static void ObserveFault(Task t)
        {
            // An abandoned call (timeout/cancel) may still fault via a racing StopLocked: mark it
            // observed so the finalizer never takes the process down with UnobservedTaskException.
            try { t.ContinueWith(x => { var ignored = x.Exception; }, TaskContinuationOptions.OnlyOnFaulted); }
            catch { }
        }

        private static bool IsDeadServer(McpException ex)
        {
            string m = ex != null ? (ex.Message ?? "") : "";
            return m.IndexOf("not running", StringComparison.OrdinalIgnoreCase) >= 0
                || m.IndexOf("exited", StringComparison.OrdinalIgnoreCase) >= 0
                || m.IndexOf("stopped", StringComparison.OrdinalIgnoreCase) >= 0
                || m.IndexOf("write failed", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string Trunc(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length > max ? s.Substring(0, max) + "…(truncated)" : s;
        }

        /// <summary>Raw JSON-RPC call. Returns the `result` object (throws on error member / timeout).</summary>
        public static async Task<JObject> RpcAsync(string method, JObject p, int timeoutMs, CancellationToken ct = default(CancellationToken))
        {
            if (timeoutMs <= 0) timeoutMs = 60000; // Task.Delay throws on 0/negative: bound it instead
            if (string.IsNullOrWhiteSpace(method)) throw new McpException("MCP method must not be empty.");
            int id = Interlocked.Increment(ref NextId);
            var msg = new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
            if (p != null) msg["params"] = p;
            var tcs = new TaskCompletionSource<string>();
            lock (Gate)
            {
                if (Stdin == null || (Proc != null && Proc.HasExited))
                    throw new McpException("MCP server is not running (is SolidWorks still open?).");
                Pending[id] = tcs;
                try { Stdin.WriteLine(msg.ToString(Newtonsoft.Json.Formatting.None)); }
                catch (Exception ex)
                {
                    TaskCompletionSource<string> dropped;
                    Pending.TryRemove(id, out dropped);
                    try { tcs.TrySetException(new McpException("MCP write failed: " + ex.Message)); } catch { }
                    ObserveFault(tcs.Task);
                    throw new McpException("MCP write failed: " + ex.Message);
                }
            }
            // Cancellable delay: a bare Task.Delay(timeoutMs) leaves a Timer queued for the
            // full timeout (300s on tools/call) even when the reply arrives in ms.
            object winner;
            using (var timeoutCts = new CancellationTokenSource())
            {
                var timeoutTask = Task.Delay(timeoutMs, timeoutCts.Token);
                if (ct.CanBeCanceled)
                    winner = await Task.WhenAny(tcs.Task, timeoutTask, Task.Delay(-1, ct)).ConfigureAwait(false);
                else
                    winner = await Task.WhenAny(tcs.Task, timeoutTask).ConfigureAwait(false);
                try { timeoutCts.Cancel(); } catch { }
            }
            if (ct.IsCancellationRequested)
            {
                TaskCompletionSource<string> dropped;
                Pending.TryRemove(id, out dropped);
                try { tcs.TrySetException(new OperationCanceledException(ct)); } catch { }
                ObserveFault(tcs.Task); // a racing StopLocked must not surface as UnobservedTaskException
                throw new OperationCanceledException(ct);
            }
            if (winner != tcs.Task)
            {
                TaskCompletionSource<string> dropped;
                Pending.TryRemove(id, out dropped);
                try { tcs.TrySetException(new McpException("MCP `" + method + "` timed out.")); } catch { }
                ObserveFault(tcs.Task); // late reply / racing death-fail must have no unobserved waiter
                throw new McpException("MCP `" + method + "` timed out after " + (timeoutMs / 1000) + "s.");
            }
            string line = await tcs.Task.ConfigureAwait(false);
            JObject resp;
            try { resp = JObject.Parse(line); }
            catch (Exception jx) { throw new McpException("MCP returned non-JSON text (" + (line != null ? line.Length : 0) + " chars): " + Trunc(line, 200)); }
            var err = resp["error"];
            if (err != null)
            {
                string em = null;
                try { em = (string)err["message"]; } catch { }
                if (string.IsNullOrEmpty(em)) { try { em = err.ToString(); } catch { em = "unknown error"; } }
                throw new McpException("MCP error: " + Trunc(em, 500));
            }
            return resp["result"] as JObject;
        }

        public static async Task<JArray> ListToolsAsync(CancellationToken ct = default(CancellationToken))
        {
            await EnsureStartedAsync(0, ct).ConfigureAwait(false);
            if (CachedTools != null) return CachedTools;
            JObject res = await RpcAsync("tools/list", new JObject(), 60000, ct).ConfigureAwait(false);
            // A null/missing result must not NRE here, and an empty list must not be
            // cached: otherwise one malformed reply poisons every later call.
            var tools = res != null ? res["tools"] as JArray : null;
            if (tools == null || tools.Count == 0) return new JArray();
            CachedTools = tools;
            return tools;
        }

        public class ToolResult
        {
            public bool Ok = true;
            public string Text = "";
        }

        /// <summary>Call one MCP tool. The server reports failures as {ok:false,...} text — surfaced, never thrown. timeoutMs&lt;=0 uses the configured default.</summary>
        public static async Task<ToolResult> CallToolAsync(string name, JObject args, int timeoutMs = 0, CancellationToken ct = default(CancellationToken))
        {
            if (timeoutMs <= 0) { try { timeoutMs = LoadConfig().TimeoutMs; } catch { timeoutMs = 300000; } if (timeoutMs < 30000) timeoutMs = 30000; }
            var p = new JObject { ["name"] = name };
            p["arguments"] = args ?? new JObject();
            JObject res;
            try { res = await RpcAsync("tools/call", p, timeoutMs, ct).ConfigureAwait(false); }
            catch (McpException mex) when (IsDeadServer(mex) && !ct.IsCancellationRequested)
            {
                // Server died mid-run (process killed / SW closed took it down): restart once and
                // retry so the next tool call heals instead of failing every later step until the cap.
                await EnsureStartedAsync(60000, ct).ConfigureAwait(false);
                res = await RpcAsync("tools/call", p, timeoutMs, ct).ConfigureAwait(false);
            }
            // A null result is a broken server reply: fail loudly, not Ok=true with empty text.
            if (res == null) throw new McpException("MCP `tools/call` returned an empty result.");
            var out_ = new ToolResult();
            try
            {
                var content = res["content"] as JArray;
                var sb = new StringBuilder();
                if (content != null)
                    foreach (var c in content)
                    {
                        var t = c as JObject;
                        if (t == null) continue;
                        if ((string)t["type"] == "text") sb.Append((string)t["text"]);
                    }
                out_.Text = sb.ToString();
                bool isErr = false;
                try { isErr = res["isError"] != null && res["isError"].Value<bool>(); } catch { }
                if (isErr) out_.Ok = false;
                // Whitespace-tolerant {ok:false} detection: servers emit {"ok":false}, { "ok" : false }, etc.
                try
                {
                    string compact = out_.Text.Replace(" ", "").Replace("\t", "").Replace("\r", "").Replace("\n", "");
                    if (compact.Contains("\"ok\":false")) out_.Ok = false;
                }
                catch { }
                // Bound memory + downstream history: the agent history re-caps at 2000 chars.
                if (out_.Text != null && out_.Text.Length > 8000) out_.Text = out_.Text.Substring(0, 8000) + "…(truncated)";
            }
            catch { out_.Text = res != null ? Trunc(res.ToString(), 2000) : ""; }
            return out_;
        }
    }
}
