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

        public class McpException : Exception
        {
            public McpException(string message) : base(message) { }
        }

        private class McpConfig
        {
            public string Command = "uvx";
            public string[] Args = new[] { "solidworks-mcp" };
            public int StartTimeoutMs = 300000;
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
            }
            catch { }
            return cfg;
        }

        public static bool IsRunning
        {
            get { lock (Gate) { return Proc != null && !Proc.HasExited; } }
        }

        /// <summary>Start the server + handshake if needed. Throws McpException with a setup hint on failure.</summary>
        public static async Task EnsureStartedAsync(int timeoutMs = 0)
        {
            lock (Gate)
            {
                if (Proc != null && !Proc.HasExited) return;
                StartLocked(timeoutMs > 0 ? timeoutMs : LoadConfig().StartTimeoutMs);
            }
            // Handshake outside the lock (IO).
            try
            {
                var init = new JObject
                {
                    ["protocolVersion"] = "2024-11-05",
                    ["capabilities"] = new JObject(),
                    ["clientInfo"] = new JObject { ["name"] = "Forge", ["version"] = "1.0" }
                };
                JObject res = await RpcAsync("initialize", init, 60000);
                if (res == null) throw new McpException("MCP initialize returned nothing.");
                await NotifyAsync("notifications/initialized", new JObject());
            }
            catch (Exception ex)
            {
                Stop();
                if (ex is McpException) throw;
                throw new McpException("MCP handshake failed: " + ex.Message + " — run `uvx solidworks-mcp --selftest` with SolidWorks open and retry.");
            }
        }

        private static void StartLocked(int startTimeoutMs)
        {
            StopLocked();
            var cfg = LoadConfig();
            var psi = new ProcessStartInfo
            {
                FileName = cfg.Command,
                Arguments = string.Join(" ", cfg.Args),
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
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
            ReaderThread.Start();
            DrainThread = new Thread(DrainLoop) { IsBackground = true, Name = "forge-mcp-stderr" };
            DrainThread.Start(Proc.StandardError);
        }

        private static void ReaderLoop()
        {
            try
            {
                string line;
                var reader = Proc.StandardOutput;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    int id = -1;
                    TaskCompletionSource<string> tcs = null;
                    try
                    {
                        var msg = JObject.Parse(line);
                        var jt = msg["id"];
                        if (jt == null || jt.Type == JTokenType.Null) continue; // notification from server: ignore
                        id = jt.Value<int>();
                        Pending.TryRemove(id, out tcs);
                    }
                    catch { continue; }
                    if (tcs != null) { try { tcs.TrySetResult(line); } catch { } }
                }
            }
            catch { }
            // Server died: fail everything outstanding.
            foreach (var kv in Pending)
            {
                TaskCompletionSource<string> tcs;
                if (Pending.TryRemove(kv.Key, out tcs))
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
            CachedTools = null;
            foreach (var kv in Pending)
            {
                TaskCompletionSource<string> tcs;
                if (Pending.TryRemove(kv.Key, out tcs))
                    try { tcs.TrySetCanceled(); } catch { }
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

        /// <summary>Raw JSON-RPC call. Returns the `result` object (throws on error member / timeout).</summary>
        public static async Task<JObject> RpcAsync(string method, JObject p, int timeoutMs)
        {
            int id = Interlocked.Increment(ref NextId);
            var msg = new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
            if (p != null) msg["params"] = p;
            var tcs = new TaskCompletionSource<string>();
            Pending[id] = tcs;
            lock (Gate)
            {
                if (Stdin == null || (Proc != null && Proc.HasExited))
                {
                    TaskCompletionSource<string> dropped;
                    Pending.TryRemove(id, out dropped);
                    throw new McpException("MCP server is not running (is SolidWorks still open?).");
                }
                try { Stdin.WriteLine(msg.ToString(Newtonsoft.Json.Formatting.None)); }
                catch (Exception ex)
                {
                    TaskCompletionSource<string> dropped;
                    Pending.TryRemove(id, out dropped);
                    throw new McpException("MCP write failed: " + ex.Message);
                }
            }
            var winner = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
            if (winner != tcs.Task)
            {
                TaskCompletionSource<string> dropped;
                Pending.TryRemove(id, out dropped);
                throw new McpException("MCP `" + method + "` timed out after " + (timeoutMs / 1000) + "s.");
            }
            string line = await tcs.Task;
            var resp = JObject.Parse(line);
            var err = resp["error"];
            if (err != null) throw new McpException("MCP error: " + (string)err["message"] ?? err.ToString());
            return resp["result"] as JObject;
        }

        public static async Task<JArray> ListToolsAsync()
        {
            await EnsureStartedAsync();
            if (CachedTools != null) return CachedTools;
            JObject res = await RpcAsync("tools/list", new JObject(), 60000);
            var tools = res["tools"] as JArray ?? new JArray();
            CachedTools = tools;
            return tools;
        }

        public class ToolResult
        {
            public bool Ok = true;
            public string Text = "";
        }

        /// <summary>Call one MCP tool. The server reports failures as {ok:false,...} text — surfaced, never thrown.</summary>
        public static async Task<ToolResult> CallToolAsync(string name, JObject args, int timeoutMs = 300000)
        {
            var p = new JObject { ["name"] = name };
            p["arguments"] = args ?? new JObject();
            JObject res = await RpcAsync("tools/call", p, timeoutMs);
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
                if (res["isError"] != null && res["isError"].Value<bool>()) out_.Ok = false;
                if (out_.Text.Contains("\"ok\": false") || out_.Text.Contains("\"ok\":false")) out_.Ok = false;
            }
            catch { out_.Text = res != null ? res.ToString() : ""; }
            return out_;
        }
    }
}
