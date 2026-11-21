using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Forge.SolidWorks
{
    /// <summary>
    /// McpBridge — a minimal MCP (Model Context Protocol) client over stdio for .NET Framework 4.8.
    /// Launches SolidWorks-MCP servers (default: `uvx solidworks-mcp`) as child processes and speaks
    /// newline-delimited JSON-RPC 2.0: initialize -> notifications/initialized -> tools/list -> tools/call.
    /// Multi-server: mcp.json holds an ordered `servers` array; routing is ordered failover with NO
    /// namespacing — the first enabled server whose EnsureStarted + tools/list succeeds wins for the run.
    /// Each server owns a per-name McpConn (process + reader + pending + cached tools) keyed under Gate.
    /// </summary>
    public static class McpBridge
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, McpConn> Conns =
            new Dictionary<string, McpConn>(StringComparer.OrdinalIgnoreCase);
        private static int NextId; // global: ids stay unique across servers so replies can't cross-resolve
        // Serializes EnsureStarted handshakes: without this two concurrent callers each
        // start a server, the second kills the first's process, and both handshakes fail.
        private static readonly SemaphoreSlim StartGate = new SemaphoreSlim(1, 1);

        public class McpException : Exception
        {
            public McpException(string message) : base(message) { }
        }

        /// <summary>One MCP server entry. No url/header/oauth fields — stdio command/args only.</summary>
        public class McpServerConfig
        {
            public string Name = "default";
            public string Command = "uvx";
            public string[] Args = new[] { "solidworks-mcp" };
            public int StartTimeoutMs; // 0/<30s = follow TimeoutMs
            public bool Enabled = true;
            public int TimeoutMs = 300000;

            public int EffectiveStartTimeoutMs
            {
                get
                {
                    if (StartTimeoutMs >= 30000) return StartTimeoutMs;
                    if (TimeoutMs >= 30000) return TimeoutMs;
                    return 300000;
                }
            }
        }

        /// <summary>Winner of a routed tools/list: the tools plus the pinned server name.</summary>
        public class RouteResult
        {
            public JArray Tools = new JArray();
            public string Winner;
        }

        private class McpConn
        {
            public Process Proc;
            public StreamWriter Stdin;
            public Thread ReaderThread;
            public Thread DrainThread;
            public readonly ConcurrentDictionary<int, TaskCompletionSource<string>> Pending =
                new ConcurrentDictionary<int, TaskCompletionSource<string>>();
            public JArray CachedTools;
        }

        private class ReaderState
        {
            public string Name;
            public McpConn Conn;
            public Process Proc;
        }

        private class McpConfig
        {
            public string Command = "uvx";
            public string[] Args = new[] { "solidworks-mcp" };
            public int StartTimeoutMs = 300000;
            public bool Enabled = true;
            public int TimeoutMs = 300000;
        }

        private static McpServerConfig DefaultServerConfig()
        {
            return new McpServerConfig
            {
                Name = "default",
                Command = "uvx",
                Args = new[] { "solidworks-mcp" },
                StartTimeoutMs = 300000,
                Enabled = true,
                TimeoutMs = 300000
            };
        }

        private static string ConfigPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Forge", "mcp.json");
        }

        /// <summary>
        /// Read all servers. NEW form: {"servers":[{name,command,args,enabled,timeoutMs,startTimeoutMs?}]}.
        /// LEGACY form (no `servers`, flat command/args/enabled/timeoutMs/startTimeoutMs) migrates
        /// in-memory to servers[0]={name:"default",...}. Never writes back here — the `servers`
        /// form is written only on the next SaveServers/SaveConfig/SaveServer call. Missing/corrupt
        /// files fall back to a single default server; never throws.
        /// </summary>
        public static List<McpServerConfig> LoadServers()
        {
            var fallback = new List<McpServerConfig> { DefaultServerConfig() };
            try
            {
                string p = ConfigPath();
                if (!File.Exists(p)) return fallback;
                JObject j;
                try { j = JObject.Parse(File.ReadAllText(p)); }
                catch { return fallback; }
                if (j == null) return fallback;
                var arr = j["servers"] as JArray;
                if (arr != null)
                {
                    var list = new List<McpServerConfig>();
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var t in arr)
                    {
                        try
                        {
                            var o = t as JObject;
                            if (o == null) continue;
                            string name = ((string)o["name"] ?? "").Trim();
                            if (name.Length == 0) name = "default";
                            if (!seen.Add(name)) continue; // first wins on duplicates
                            var s = DefaultServerConfig();
                            s.Name = name;
                            string cmd = ((string)o["command"] ?? "").Trim();
                            if (!string.IsNullOrWhiteSpace(cmd)) s.Command = cmd;
                            var ja = o["args"] as JArray;
                            if (ja != null && ja.Count > 0)
                            {
                                var alist = new List<string>();
                                foreach (var a in ja) { string as_ = (string)a; if (!string.IsNullOrWhiteSpace(as_)) alist.Add(as_); }
                                if (alist.Count > 0) s.Args = alist.ToArray();
                            }
                            else
                            {
                                // Tolerate a single space-joined string (panel form may send this).
                                string astr = (string)o["args"];
                                if (!string.IsNullOrWhiteSpace(astr))
                                {
                                    var parts = SplitArgs(astr);
                                    if (parts.Length > 0) s.Args = parts;
                                }
                            }
                            if (o["enabled"] != null) { try { s.Enabled = (bool)o["enabled"]; } catch { s.Enabled = true; } }
                            int tmo = (int?)o["timeoutMs"] ?? 0;
                            s.TimeoutMs = tmo >= 30000 ? tmo : 300000;
                            int stmo = (int?)o["startTimeoutMs"] ?? 0;
                            // Per-server startTimeoutMs honored if present, else = timeoutMs.
                            s.StartTimeoutMs = stmo >= 30000 ? stmo : 0;
                            list.Add(s);
                        }
                        catch { }
                    }
                    if (list.Count == 0) return fallback;
                    return list;
                }
                // LEGACY form: flat keys, no `servers`.
                var one = DefaultServerConfig();
                try
                {
                    if (!string.IsNullOrWhiteSpace((string)j["command"])) one.Command = ((string)j["command"]).Trim();
                    var a = j["args"] as JArray;
                    if (a != null && a.Count > 0)
                    {
                        var list = new List<string>();
                        foreach (var t in a) { string s = (string)t; if (!string.IsNullOrWhiteSpace(s)) list.Add(s); }
                        if (list.Count > 0) one.Args = list.ToArray();
                    }
                    int tmo = (int?)j["startTimeoutMs"] ?? 0;
                    if (tmo >= 30000) one.StartTimeoutMs = tmo;
                    if (j["enabled"] != null) { try { one.Enabled = (bool)j["enabled"]; } catch { one.Enabled = true; } }
                    int ptmo = (int?)j["timeoutMs"] ?? 0;
                    if (ptmo >= 30000) one.TimeoutMs = ptmo;
                    if (one.StartTimeoutMs < 30000) one.StartTimeoutMs = 0; // legacy without startTimeoutMs follows timeoutMs
                    else if ((int?)j["startTimeoutMs"] == null) one.StartTimeoutMs = 0;
                }
                catch { }
                // Legacy files that carried no startTimeoutMs must follow timeoutMs.
                try { if ((int?)j["startTimeoutMs"] == null) one.StartTimeoutMs = 0; } catch { }
                return new List<McpServerConfig> { one };
            }
            catch { return fallback; }
        }

        // Quote-aware splitter for a space-joined args string ("a \"b c\" d" -> 3 parts).
        private static string[] SplitArgs(string s)
        {
            try
            {
                var parts = new List<string>();
                var cur = new StringBuilder();
                bool inQ = false;
                foreach (char c in (s ?? ""))
                {
                    if (c == '"') { inQ = !inQ; continue; }
                    if (!inQ && char.IsWhiteSpace(c)) { if (cur.Length > 0) { parts.Add(cur.ToString()); cur.Length = 0; } continue; }
                    cur.Append(c);
                }
                if (cur.Length > 0) parts.Add(cur.ToString());
                return parts.ToArray();
            }
            catch { return new string[0]; }
        }

        /// <summary>Persist the full server list in NEW `servers` form (legacy flat keys are dropped).</summary>
        public static void SaveServers(List<McpServerConfig> servers)
        {
            try
            {
                var arr = new JArray();
                if (servers != null)
                    foreach (var s in servers)
                    {
                        try
                        {
                            if (s == null || string.IsNullOrWhiteSpace(s.Name)) continue;
                            string cmd = string.IsNullOrWhiteSpace(s.Command) ? "uvx" : s.Command.Trim();
                            var ja = new JArray();
                            if (s.Args != null)
                                foreach (var a in s.Args) if (!string.IsNullOrWhiteSpace(a)) ja.Add(a);
                            if (ja.Count == 0) ja.Add("solidworks-mcp");
                            int tmo = s.TimeoutMs >= 30000 ? s.TimeoutMs : 300000;
                            var o = new JObject
                            {
                                ["name"] = s.Name.Trim(),
                                ["command"] = cmd,
                                ["args"] = ja,
                                ["enabled"] = s.Enabled,
                                ["timeoutMs"] = tmo
                            };
                            if (s.StartTimeoutMs >= 30000 && s.StartTimeoutMs != tmo)
                                o["startTimeoutMs"] = s.StartTimeoutMs;
                            arr.Add(o);
                        }
                        catch { }
                    }
                if (arr.Count == 0)
                {
                    var d = DefaultServerConfig();
                    arr.Add(new JObject
                    {
                        ["name"] = d.Name,
                        ["command"] = d.Command,
                        ["args"] = new JArray(d.Args),
                        ["enabled"] = d.Enabled,
                        ["timeoutMs"] = d.TimeoutMs
                    });
                }
                string p = ConfigPath();
                try { Directory.CreateDirectory(Path.GetDirectoryName(p)); } catch { }
                File.WriteAllText(p, new JObject { ["servers"] = arr }.ToString(Newtonsoft.Json.Formatting.None));
            }
            catch { }
        }

        /// <summary>
        /// Upsert one server by name (case-insensitive); missing fields keep their current values,
        /// a missing server is created from defaults. timeoutMs/startTimeoutMs &lt;30s (and null) = no change.
        /// </summary>
        public static void SaveServer(string name, bool? enabled, int? timeoutMs, string command, string[] args, int? startTimeoutMs = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(name)) return;
                var list = LoadServers();
                McpServerConfig cur = null;
                foreach (var s in list)
                    if (string.Equals(s.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)) { cur = s; break; }
                if (cur == null) { cur = DefaultServerConfig(); cur.Name = name.Trim(); cur.StartTimeoutMs = 0; list.Add(cur); }
                if (enabled.HasValue) cur.Enabled = enabled.Value;
                if (timeoutMs.HasValue && timeoutMs.Value >= 30000) cur.TimeoutMs = timeoutMs.Value;
                if (!string.IsNullOrWhiteSpace(command)) cur.Command = command.Trim();
                if (args != null && args.Length > 0)
                {
                    var kept = new List<string>();
                    foreach (var a in args) if (!string.IsNullOrWhiteSpace(a)) kept.Add(a);
                    if (kept.Count > 0) cur.Args = kept.ToArray();
                }
                if (startTimeoutMs.HasValue)
                    cur.StartTimeoutMs = startTimeoutMs.Value >= 30000 ? startTimeoutMs.Value : 0;
                SaveServers(list);
            }
            catch { }
        }

        /// <summary>Remove one server by name (case-insensitive); also stops its process. Never throws.</summary>
        public static void RemoveServer(string name)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(name)) return;
                try { Stop(name); } catch { }
                var list = LoadServers();
                list.RemoveAll(s => s != null && string.Equals(s.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
                SaveServers(list);
            }
            catch { }
        }

        /// <summary>Legacy single-config read — thin shim over servers[0] (or default when empty).</summary>
        private static McpConfig LoadConfig()
        {
            var cfg = new McpConfig();
            try
            {
                var list = LoadServers();
                var s = (list != null && list.Count > 0) ? list[0] : DefaultServerConfig();
                if (s == null) s = DefaultServerConfig();
                cfg.Command = string.IsNullOrWhiteSpace(s.Command) ? "uvx" : s.Command;
                cfg.Args = (s.Args != null && s.Args.Length > 0) ? s.Args : new[] { "solidworks-mcp" };
                cfg.StartTimeoutMs = s.EffectiveStartTimeoutMs;
                cfg.Enabled = s.Enabled;
                cfg.TimeoutMs = s.TimeoutMs >= 30000 ? s.TimeoutMs : 300000;
            }
            catch { }
            return cfg;
        }

        /// <summary>Legacy persist — thin shim: merges enabled/timeoutMs into servers[0] (writes `servers` form).</summary>
        public static void SaveConfig(bool? enabled, int timeoutMs)
        {
            try
            {
                var list = LoadServers();
                if (list == null || list.Count == 0) list = new List<McpServerConfig> { DefaultServerConfig() };
                var first = list[0];
                if (enabled.HasValue) first.Enabled = enabled.Value;
                if (timeoutMs >= 30000) first.TimeoutMs = timeoutMs;
                SaveServers(list);
            }
            catch { }
        }

        private static string ResolveName(string serverName)
        {
            try
            {
                string n = (serverName ?? "").Trim();
                if (n.Length > 0) return n;
                var list = LoadServers();
                if (list != null && list.Count > 0 && list[0] != null && !string.IsNullOrWhiteSpace(list[0].Name))
                    return list[0].Name.Trim();
            }
            catch { }
            return "default";
        }

        private static McpServerConfig FindServer(string name)
        {
            try
            {
                var list = LoadServers();
                if (list != null)
                    foreach (var s in list)
                        if (s != null && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
                            return s;
            }
            catch { }
            return null;
        }

        private static McpConn GetConnLocked(string name)
        {
            McpConn c;
            if (!Conns.TryGetValue(name, out c) || c == null)
            {
                c = new McpConn();
                Conns[name] = c;
            }
            return c;
        }

        public static bool IsRunning
        {
            get
            {
                try { return IsRunningServer(null); }
                catch { return false; }
            }
        }

        /// <summary>Per-server running check (null/empty = servers[0]). Never throws.</summary>
        public static bool IsRunningServer(string serverName = null)
        {
            try
            {
                string name = ResolveName(serverName);
                lock (Gate)
                {
                    McpConn c;
                    if (!Conns.TryGetValue(name, out c) || c == null || c.Proc == null) return false;
                    try { return !c.Proc.HasExited; } catch { return false; }
                }
            }
            catch { return false; }
        }

        /// <summary>Read-only snapshot for every server: [{name,command,args,enabled,timeoutMs,running}]. Never throws.</summary>
        public static JArray DescribeAll()
        {
            var arr = new JArray();
            try
            {
                var list = LoadServers();
                if (list == null || list.Count == 0) list = new List<McpServerConfig> { DefaultServerConfig() };
                foreach (var s in list)
                {
                    try
                    {
                        if (s == null) continue;
                        bool running = false;
                        try { running = IsRunningServer(s.Name); } catch { }
                        arr.Add(new JObject
                        {
                            ["name"] = s.Name ?? "default",
                            ["command"] = s.Command ?? "uvx",
                            ["args"] = s.Args != null ? string.Join(" ", s.Args) : "",
                            ["enabled"] = s.Enabled,
                            ["timeoutMs"] = s.TimeoutMs >= 30000 ? s.TimeoutMs : 300000,
                            ["running"] = running
                        });
                    }
                    catch { }
                }
            }
            catch { }
            return arr;
        }

        /// <summary>Launch configuration status display (read-only snapshot of servers[0]).</summary>
        public static void Describe(out string command, out string args)
        {
            bool enabledDummy;
            int timeoutDummy;
            DescribeFull(out command, out args, out enabledDummy, out timeoutDummy);
        }

        /// <summary>Legacy full describe — thin shim over servers[0] (or default when empty).</summary>
        public static void DescribeFull(out string command, out string args, out bool enabled, out int timeoutMs)
        {
            var cfg = LoadConfig();
            command = cfg.Command ?? "";
            args = cfg.Args != null ? string.Join(" ", cfg.Args) : "";
            enabled = cfg.Enabled;
            timeoutMs = cfg.TimeoutMs;
        }

        /// <summary>
        /// Ordered failover, NO namespacing: the first enabled server whose EnsureStarted +
        /// tools/list succeeds (non-empty) wins for the run. Tool names are unchanged.
        /// Throws McpException("...disabled...") when no server is enabled so callers keep
        /// their disabled hint; returns an empty result when every enabled server failed.
        /// </summary>
        public static async Task<RouteResult> RouteAsync(CancellationToken ct = default(CancellationToken))
        {
            List<McpServerConfig> servers = null;
            try { servers = LoadServers(); } catch { servers = null; }
            if (servers == null || servers.Count == 0)
                servers = new List<McpServerConfig> { DefaultServerConfig() };
            var enabled = new List<McpServerConfig>();
            foreach (var s in servers) if (s != null && s.Enabled) enabled.Add(s);
            if (enabled.Count == 0)
                throw new McpException("MCP server is disabled — turn it on in Settings → MCP server, then retry.");
            foreach (var s in enabled)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await EnsureStartedAsync(s.Name, 0, ct).ConfigureAwait(false);
                    JArray tools = await ListToolsAsync(s.Name, ct).ConfigureAwait(false);
                    if (tools != null && tools.Count > 0)
                        return new RouteResult { Tools = tools, Winner = s.Name };
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { /* next server */ }
            }
            return new RouteResult { Tools = new JArray(), Winner = null };
        }

        /// <summary>Alias of RouteAsync (C# async methods cannot use out params for the winner).</summary>
        public static Task<RouteResult> ListToolsRoutedAsync(CancellationToken ct = default(CancellationToken))
        {
            return RouteAsync(ct);
        }

        /// <summary>Start the server + handshake if needed. Throws McpException with a setup hint on failure.</summary>
        public static Task EnsureStartedAsync(int timeoutMs = 0, CancellationToken ct = default(CancellationToken), string serverName = null)
        {
            return EnsureStartedOnAsync(ResolveName(serverName), timeoutMs, ct);
        }

        /// <summary>Server-first overload: EnsureStartedAsync(name, timeoutMs, ct).</summary>
        public static Task EnsureStartedAsync(string serverName, int timeoutMs, CancellationToken ct)
        {
            return EnsureStartedOnAsync(ResolveName(serverName), timeoutMs, ct);
        }

        private static async Task EnsureStartedOnAsync(string name, int timeoutMs, CancellationToken ct)
        {
            var cfg = FindServer(name) ?? DefaultServerConfig();
            cfg.Name = name;
            if (!cfg.Enabled)
                throw new McpException("MCP server '" + name + "' is disabled — turn it on in Settings → MCP server, then retry.");
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
                    var conn = GetConnLocked(name);
                    if (conn.Proc != null) { try { if (!conn.Proc.HasExited) return; } catch { } }
                    int want = timeoutMs > 0 ? timeoutMs : cfg.EffectiveStartTimeoutMs;
                    if (want < 30000) want = 60000; // StartLocked ignores <30s; handshake must still bound itself
                    initTimeout = want;
                    StartLocked(name, conn, cfg);
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
                    JObject res = await RpcOnServerAsync(name, "initialize", init, initTimeout, ct).ConfigureAwait(false);
                    if (res == null) throw new McpException("MCP initialize returned nothing.");
                    await NotifyOnServerAsync(name, "notifications/initialized", new JObject());
                }
                catch (OperationCanceledException) { Stop(name); throw; } // stop wins: never rewrap cancel as a handshake error
                catch (Exception ex)
                {
                    Stop(name);
                    if (ex is McpException) throw;
                    throw new McpException("MCP handshake failed: " + ex.Message + " — run `uvx solidworks-mcp --selftest` with SolidWorks open and retry.");
                }
            }
            finally { if (acquired) { try { StartGate.Release(); } catch { } } }
        }

        private static void StartLocked(string name, McpConn conn, McpServerConfig cfg)
        {
            StopLocked(conn);
            var psi = new ProcessStartInfo
            {
                FileName = cfg.Command,
                // Quote args containing spaces so installs under e.g. "C:\Program Files" launch.
                Arguments = string.Join(" ", Array.ConvertAll(cfg.Args ?? new[] { "solidworks-mcp" }, a => a.Contains(" ") && !a.StartsWith("\"") ? "\"" + a + "\"" : a)),
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
                conn.Proc = Process.Start(psi);
                if (conn.Proc == null) throw new McpException("Could not launch `" + cfg.Command + " " + string.Join(" ", cfg.Args ?? new string[0]) + "` — install uv (astral.sh/uv) and retry.");
                conn.Stdin = new StreamWriter(conn.Proc.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = true };
            }
            catch (McpException) { StopLocked(conn); throw; }
            catch (Exception ex)
            {
                StopLocked(conn);
                throw new McpException("Could not launch `" + cfg.Command + "` (" + ex.GetType().Name + ": " + ex.Message + ") — install uv from astral.sh/uv, then `uvx solidworks-mcp --selftest` with SolidWorks open.");
            }

            conn.ReaderThread = new Thread(ReaderLoop) { IsBackground = true, Name = "forge-mcp-reader" };
            conn.ReaderThread.Start(new ReaderState { Name = name, Conn = conn, Proc = conn.Proc });
            conn.DrainThread = new Thread(DrainLoop) { IsBackground = true, Name = "forge-mcp-stderr" };
            conn.DrainThread.Start(conn.Proc.StandardError);
        }

        private static void ReaderLoop(object state)
        {
            var rs = state as ReaderState;
            if (rs == null || rs.Conn == null) return;
            var conn = rs.Conn;
            try
            {
                string line;
                var reader = rs.Proc != null ? rs.Proc.StandardOutput : conn.Proc.StandardOutput;
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
                        conn.Pending.TryRemove(id, out tcs);
                    }
                    catch { continue; }
                    if (tcs != null) { try { tcs.TrySetResult(line); } catch { } }
                }
            }
            catch { }
            // Server died: fail this conn's outstanding calls only. If a restart already swapped
            // in a new proc, the new reader owns the current ids — don't steal them.
            lock (Gate)
            {
                McpConn cur;
                if (Conns.TryGetValue(rs.Name, out cur) && !object.ReferenceEquals(cur, conn)) return;
                if (!object.ReferenceEquals(conn.Proc, rs.Proc)) return;
            }
            int[] dead;
            try { dead = new System.Collections.Generic.List<int>(conn.Pending.Keys).ToArray(); }
            catch { dead = new int[0]; }
            foreach (int key in dead)
            {
                TaskCompletionSource<string> tcs;
                if (conn.Pending.TryRemove(key, out tcs))
                    try { tcs.TrySetException(new McpException("MCP server exited (is SolidWorks still running?).")); } catch { }
            }
        }

        private static void DrainLoop(object err)
        {
            try { var r = err as StreamReader; if (r == null) return; while (r.ReadLine() != null) { } }
            catch { }
        }

        /// <summary>Stop one server (name) or ALL servers (null/empty). Never throws.</summary>
        public static void Stop(string serverName = null)
        {
            try
            {
                lock (Gate)
                {
                    if (string.IsNullOrWhiteSpace(serverName))
                    {
                        foreach (var kv in Conns) { try { StopLocked(kv.Value); } catch { } }
                        return;
                    }
                    McpConn c;
                    if (Conns.TryGetValue(serverName.Trim(), out c) && c != null)
                    { try { StopLocked(c); } catch { } }
                }
            }
            catch { }
        }

        private static void StopLocked(McpConn conn)
        {
            if (conn == null) return;
            conn.CachedTools = null; // invalidate: a restarted server may serve a different tool set
            foreach (var kv in conn.Pending)
            {
                TaskCompletionSource<string> tcs;
                if (conn.Pending.TryRemove(kv.Key, out tcs))
                    // Fail with a real error, not TaskCanceledException: waiters report
                    // "server stopped" instead of a bare "a task was canceled".
                    try { tcs.TrySetException(new McpException("MCP server stopped.")); } catch { }
            }
            try { if (conn.Stdin != null) conn.Stdin.Close(); } catch { }
            conn.Stdin = null;
            try
            {
                if (conn.Proc != null)
                {
                    if (!conn.Proc.HasExited) { try { conn.Proc.Kill(); } catch { } }
                    try { conn.Proc.Dispose(); } catch { }
                }
            }
            catch { }
            conn.Proc = null;
        }

        private static Task NotifyAsync(string method, JObject p)
        {
            return NotifyOnServerAsync(null, method, p);
        }

        private static Task NotifyOnServerAsync(string serverName, string method, JObject p)
        {
            string name = ResolveName(serverName);
            var msg = new JObject { ["jsonrpc"] = "2.0", ["method"] = method };
            if (p != null) msg["params"] = p;
            lock (Gate)
            {
                var conn = GetConnLocked(name);
                if (conn.Stdin == null) throw new McpException("MCP server is not running.");
                conn.Stdin.WriteLine(msg.ToString(Newtonsoft.Json.Formatting.None));
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

        /// <summary>Raw JSON-RPC call on the default server. Returns the `result` object (throws on error member / timeout).</summary>
        public static Task<JObject> RpcAsync(string method, JObject p, int timeoutMs, CancellationToken ct = default(CancellationToken))
        {
            return RpcOnServerAsync(null, method, p, timeoutMs, ct);
        }

        /// <summary>Raw JSON-RPC call on one server. Returns the `result` object (throws on error member / timeout).</summary>
        public static async Task<JObject> RpcOnServerAsync(string serverName, string method, JObject p, int timeoutMs, CancellationToken ct = default(CancellationToken))
        {
            string name = ResolveName(serverName);
            if (timeoutMs <= 0) timeoutMs = 60000; // Task.Delay throws on 0/negative: bound it instead
            if (string.IsNullOrWhiteSpace(method)) throw new McpException("MCP method must not be empty.");
            int id = Interlocked.Increment(ref NextId);
            var msg = new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
            if (p != null) msg["params"] = p;
            var tcs = new TaskCompletionSource<string>();
            lock (Gate)
            {
                var conn = GetConnLocked(name);
                if (conn.Stdin == null || (conn.Proc != null && conn.Proc.HasExited))
                    throw new McpException("MCP server is not running (is SolidWorks still open?).");
                conn.Pending[id] = tcs;
                try { conn.Stdin.WriteLine(msg.ToString(Newtonsoft.Json.Formatting.None)); }
                catch (Exception ex)
                {
                    TaskCompletionSource<string> dropped;
                    conn.Pending.TryRemove(id, out dropped);
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
            McpConn wconn;
            lock (Gate) { wconn = GetConnLocked(name); }
            if (ct.IsCancellationRequested)
            {
                TaskCompletionSource<string> dropped;
                wconn.Pending.TryRemove(id, out dropped);
                try { tcs.TrySetException(new OperationCanceledException(ct)); } catch { }
                ObserveFault(tcs.Task); // a racing StopLocked must not surface as UnobservedTaskException
                throw new OperationCanceledException(ct);
            }
            if (winner != tcs.Task)
            {
                TaskCompletionSource<string> dropped;
                wconn.Pending.TryRemove(id, out dropped);
                try { tcs.TrySetException(new McpException("MCP `" + method + "` timed out.")); } catch { }
                ObserveFault(tcs.Task); // late reply / racing death-fail must have no unobserved waiter
                throw new McpException("MCP `" + method + "` timed out after " + (timeoutMs / 1000) + "s.");
            }
            string line = await tcs.Task.ConfigureAwait(false);
            JObject resp;
            try { resp = JObject.Parse(line); }
            catch { throw new McpException("MCP returned non-JSON text (" + (line != null ? line.Length : 0) + " chars): " + Trunc(line, 200)); }
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

        public static Task<JArray> ListToolsAsync(CancellationToken ct = default(CancellationToken), string serverName = null)
        {
            return ListToolsOnAsync(ResolveName(serverName), ct);
        }

        /// <summary>Server-first overload: ListToolsAsync(name, ct).</summary>
        public static Task<JArray> ListToolsAsync(string serverName, CancellationToken ct)
        {
            return ListToolsOnAsync(ResolveName(serverName), ct);
        }

        private static async Task<JArray> ListToolsOnAsync(string name, CancellationToken ct)
        {
            await EnsureStartedOnAsync(name, 0, ct).ConfigureAwait(false);
            McpConn conn;
            lock (Gate) { conn = GetConnLocked(name); if (conn.CachedTools != null) return conn.CachedTools; }
            JObject res = await RpcOnServerAsync(name, "tools/list", new JObject(), 60000, ct).ConfigureAwait(false);
            // A null/missing result must not NRE here, and an empty list must not be
            // cached: otherwise one malformed reply poisons every later call.
            var tools = res != null ? res["tools"] as JArray : null;
            if (tools == null || tools.Count == 0) return new JArray();
            lock (Gate) { conn.CachedTools = tools; }
            return tools;
        }

        /// <summary>
        /// Union of tools/list across ENABLED servers (ordered by LoadServers/DescribeAll).
        /// Per-server failures are skipped; missing/corrupt config yields an empty list.
        /// Never throws to SW (returns partial/empty on cancel/failure).
        /// </summary>
        public static async Task<JArray> ListAllToolsAsync(CancellationToken ct = default(CancellationToken))
        {
            var out_ = new JArray();
            try
            {
                List<McpServerConfig> servers = null;
                try { servers = LoadServers(); } catch { servers = null; }
                if (servers == null || servers.Count == 0) return out_;
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var s in servers)
                {
                    try
                    {
                        if (s == null || !s.Enabled) continue;
                        if (ct.IsCancellationRequested) break;
                        JArray tools = null;
                        try { tools = await ListToolsAsync(s.Name, ct).ConfigureAwait(false); }
                        catch (OperationCanceledException) { break; }
                        catch { continue; }
                        if (tools == null) continue;
                        foreach (var t in tools)
                        {
                            try
                            {
                                var o = t as JObject;
                                string n = o != null ? (string)o["name"] : (string)t;
                                if (string.IsNullOrWhiteSpace(n)) continue;
                                n = n.Trim();
                                if (!seen.Add(n)) continue;
                                out_.Add(o != null ? (JToken)o.DeepClone() : new JValue(n));
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return out_;
        }

        public class ToolResult
        {
            public bool Ok = true;
            public string Text = "";
        }

        /// <summary>Call one MCP tool on the default server. The server reports failures as {ok:false,...} text — surfaced, never thrown. timeoutMs&lt;=0 uses the configured default.</summary>
        public static Task<ToolResult> CallToolAsync(string tool, JObject args, int timeoutMs = 0, CancellationToken ct = default(CancellationToken), string serverName = null)
        {
            return CallToolOnServerAsync(ResolveName(serverName), tool, args, timeoutMs, ct);
        }

        /// <summary>Server-first overload: CallToolAsync(serverName, tool, args, timeoutMs, ct).</summary>
        public static Task<ToolResult> CallToolAsync(string serverName, string tool, JObject args, int timeoutMs, CancellationToken ct)
        {
            return CallToolOnServerAsync(ResolveName(serverName), tool, args, timeoutMs, ct);
        }

        private static async Task<ToolResult> CallToolOnServerAsync(string name, string tool, JObject args, int timeoutMs, CancellationToken ct)
        {
            if (timeoutMs <= 0)
            {
                try
                {
                    var scfg = FindServer(name);
                    timeoutMs = scfg != null ? scfg.TimeoutMs : 300000;
                }
                catch { timeoutMs = 300000; }
                if (timeoutMs < 30000) timeoutMs = 30000;
            }
            var p = new JObject { ["name"] = tool };
            p["arguments"] = args ?? new JObject();
            JObject res;
            try { res = await RpcOnServerAsync(name, "tools/call", p, timeoutMs, ct).ConfigureAwait(false); }
            catch (McpException mex) when (IsDeadServer(mex) && !ct.IsCancellationRequested)
            {
                // Server died mid-run (process killed / SW closed took it down): restart once and
                // retry so the next tool call heals instead of failing every later step until the cap.
                await EnsureStartedOnAsync(name, 60000, ct).ConfigureAwait(false);
                res = await RpcOnServerAsync(name, "tools/call", p, timeoutMs, ct).ConfigureAwait(false);
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
