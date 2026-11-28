using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
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
        public const int ServerLogTailChars = 8000;
        private const int CatalogPerServerTimeoutMs = 60000;
        private const int DefaultStdioTimeoutMs = 300000;
        private const int DefaultHttpTimeoutMs = 15000;
        private const int MinConfiguredTimeoutMs = 1000;
        private const int WatchPollMs = 2000;
        private const int EffectiveServersCacheTtlMs = 3000;

        private static string _projectKey = "";
        public static string ProjectKey
        {
            get { return _projectKey ?? ""; }
            set { _projectKey = value ?? ""; InvalidateEffectiveServersCache(); }
        }

        private static readonly object ConfigCacheGate = new object();
        private static List<McpServerConfig> _effectiveServersCache;
        private static string _effectiveServersCacheKey;
        private static DateTime _effectiveServersCacheAt;
        private static readonly Dictionary<HttpClient, int> HttpInFlight = new Dictionary<HttpClient, int>();
        private static readonly HashSet<HttpClient> DetachedHttpClients = new HashSet<HttpClient>();

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, McpConn> Conns =
            new Dictionary<string, McpConn>(StringComparer.OrdinalIgnoreCase);
        private static int NextId; // global: ids stay unique across servers so replies can't cross-resolve
        // Serializes EnsureStarted handshakes: without this two concurrent callers each
        // start a server, the second kills the first's process, and both handshakes fail.
        private static readonly SemaphoreSlim StartGate = new SemaphoreSlim(1, 1);

        public static Action<string> ServerToolsChanged;

        public class McpException : Exception
        {
            public McpException(string message) : base(message) { }
        }

        /// <summary>One MCP server entry. stdio command/args/env/cwd or http url/headers.</summary>
        public class McpServerConfig
        {
            public string Name = "default";
            public string Command = "uvx";
            public string[] Args = new[] { "solidworks-mcp" };
            public int StartTimeoutMs; // 0/<1000 = follow TimeoutMs
            public bool Enabled = true;
            public int TimeoutMs = DefaultStdioTimeoutMs;
            public string Type = "stdio";
            public string Url = "";
            public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> Env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public string Cwd = "";
            public string[] WatchPaths = new string[0];
            public string[] AlwaysAllow = new string[0];
            public string[] DisabledTools = new string[0];
            public McpOAuthConfig OAuth;

            public class McpOAuthConfig
            {
                public string ClientId = "";
                public string AuthorizationUrl = "";
                public string TokenUrl = "";
                public string[] Scope = new string[0];
                public int RedirectPort = 53821;
            }

            public bool HasOAuth
            {
                get
                {
                    return OAuth != null
                        && !string.IsNullOrWhiteSpace(OAuth.ClientId)
                        && !string.IsNullOrWhiteSpace(OAuth.AuthorizationUrl)
                        && !string.IsNullOrWhiteSpace(OAuth.TokenUrl);
                }
            }

            public bool IsHttp
            {
                get { return string.Equals(Type, "http", StringComparison.OrdinalIgnoreCase); }
            }

            public bool IsSse
            {
                get { return string.Equals(Type, "sse", StringComparison.OrdinalIgnoreCase); }
            }

            public bool IsRemote
            {
                get { return IsHttp || IsSse; }
            }

            public int EffectiveStartTimeoutMs
            {
                get
                {
                    if (StartTimeoutMs >= MinConfiguredTimeoutMs) return StartTimeoutMs;
                    if (TimeoutMs >= MinConfiguredTimeoutMs) return TimeoutMs;
                    return DefaultStdioTimeoutMs;
                }
            }
        }

        /// <summary>Winner of a routed tools/list: the tools plus the pinned server name.</summary>
        public class RouteResult
        {
            public JArray Tools = new JArray();
            public string Winner;
            public string Instructions = "";
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
            public JArray CachedResources;
            public JArray CachedResourceTemplates;
            public JArray CachedPrompts;
            public Timer WatchTimer;
            public string[] WatchPaths;
            public readonly Dictionary<string, DateTime> WatchStamps = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            public int WatchBusy;
            public string Instructions = "";
            public bool IsHttp;
            public bool IsSse;
            public string Url;
            public string SsePostUrl;
            public Dictionary<string, string> Headers;
            public HttpClient Http;
            public CancellationTokenSource SseCts;
            public Thread SseThread;
            public HttpResponseMessage SseResponse;
            public HttpClient SseClient;
            public string SessionId;
            public bool HttpReady;
            public bool HttpFailed;
            public readonly StringBuilder StderrTail = new StringBuilder();
        }

        private class ReaderState
        {
            public string Name;
            public McpConn Conn;
            public Process Proc;
        }

        private class DrainState
        {
            public string Name;
            public McpConn Conn;
            public StreamReader Reader;
        }

        private class WatchState
        {
            public string Name;
        }

        private static McpServerConfig DefaultServerConfig()
        {
            return new McpServerConfig
            {
                Name = "default",
                Command = "uvx",
                Args = new[] { "solidworks-mcp" },
                StartTimeoutMs = 0,
                Enabled = true,
                TimeoutMs = DefaultStdioTimeoutMs
            };
        }

        private static string ConfigPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Forge", "mcp.json");
        }

        public static string ProjectKeyForAssembly(string assemblyPath)
        {
            try
            {
                string k = (assemblyPath ?? "").Trim();
                if (k.Length == 0) return "";
                return k.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
            }
            catch { return ""; }
        }

        private static string ProjectConfigPath(string projectKey)
        {
            string key = (projectKey ?? "").Trim().Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
            byte[] hash;
            using (var sha = System.Security.Cryptography.SHA256.Create())
                hash = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
            var name = new StringBuilder();
            for (int i = 0; i < 12; i++) name.Append(hash[i].ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Forge", "mcp-projects", name.ToString() + ".json");
        }

        public static List<McpServerConfig> LoadServersForProject(string projectKey)
        {
            var global = LoadServers();
            if (string.IsNullOrWhiteSpace(projectKey)) return global;
            try
            {
                string p = ProjectConfigPath(projectKey);
                if (!File.Exists(p)) return global;
                JObject j;
                try { j = JObject.Parse(File.ReadAllText(p)); }
                catch { return global; }
                if (j == null) return global;
                var project = ParseServersObject(j);
                return MergeProjectServersOverGlobalByServerNameFieldWise(global, project);
            }
            catch { return global; }
        }

        private static List<McpServerConfig> MergeProjectServersOverGlobalByServerNameFieldWise(List<McpServerConfig> global, List<McpServerConfig> project)
        {
            var merged = new List<McpServerConfig>();
            var gs = global ?? new List<McpServerConfig>();
            var ps = project ?? new List<McpServerConfig>();
            foreach (var g in gs)
            {
                if (g == null) continue;
                McpServerConfig overrideEntry = null;
                foreach (var p in ps)
                {
                    if (p != null && string.Equals(g.Name, p.Name, StringComparison.OrdinalIgnoreCase)) { overrideEntry = p; break; }
                }
                merged.Add(overrideEntry == null ? g : MergeServerFieldsFieldWise(CloneServerConfig(g), overrideEntry));
            }
            foreach (var p in ps)
            {
                if (p == null) continue;
                bool exists = false;
                foreach (var g in gs)
                {
                    if (g != null && string.Equals(g.Name, p.Name, StringComparison.OrdinalIgnoreCase)) { exists = true; break; }
                }
                if (!exists) merged.Add(p);
            }
            return merged;
        }

        private static McpServerConfig CloneServerConfig(McpServerConfig s)
        {
            var c = new McpServerConfig();
            c.Name = s.Name;
            c.Command = s.Command;
            c.Args = s.Args != null ? (string[])s.Args.Clone() : new string[0];
            c.StartTimeoutMs = s.StartTimeoutMs;
            c.Enabled = s.Enabled;
            c.TimeoutMs = s.TimeoutMs;
            c.Type = s.Type;
            c.Url = s.Url;
            c.Headers = s.Headers != null ? new Dictionary<string, string>(s.Headers, StringComparer.OrdinalIgnoreCase) : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            c.Env = s.Env != null ? new Dictionary<string, string>(s.Env, StringComparer.OrdinalIgnoreCase) : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            c.Cwd = s.Cwd;
            c.WatchPaths = s.WatchPaths != null ? (string[])s.WatchPaths.Clone() : new string[0];
            c.AlwaysAllow = s.AlwaysAllow != null ? (string[])s.AlwaysAllow.Clone() : new string[0];
            c.DisabledTools = s.DisabledTools != null ? (string[])s.DisabledTools.Clone() : new string[0];
            if (s.OAuth != null)
            {
                c.OAuth = new McpServerConfig.McpOAuthConfig();
                c.OAuth.ClientId = s.OAuth.ClientId ?? "";
                c.OAuth.AuthorizationUrl = s.OAuth.AuthorizationUrl ?? "";
                c.OAuth.TokenUrl = s.OAuth.TokenUrl ?? "";
                c.OAuth.Scope = s.OAuth.Scope != null ? (string[])s.OAuth.Scope.Clone() : new string[0];
                c.OAuth.RedirectPort = s.OAuth.RedirectPort > 0 ? s.OAuth.RedirectPort : 53821;
            }
            return c;
        }

        private static bool IsDefaultArgs(string[] args)
        {
            return args != null && args.Length == 1 && string.Equals(args[0], "solidworks-mcp", StringComparison.OrdinalIgnoreCase);
        }

        private static McpServerConfig MergeServerFieldsFieldWise(McpServerConfig g, McpServerConfig p)
        {
            if (!string.IsNullOrWhiteSpace(p.Command) && !string.Equals(p.Command.Trim(), "uvx", StringComparison.OrdinalIgnoreCase)) g.Command = p.Command.Trim();
            if (p.Args != null && p.Args.Length > 0 && !IsDefaultArgs(p.Args)) g.Args = (string[])p.Args.Clone();
            if (p.StartTimeoutMs >= MinConfiguredTimeoutMs) g.StartTimeoutMs = p.StartTimeoutMs;
            if (!p.Enabled) g.Enabled = false;
            int projectDefaultTimeout = p.IsRemote ? DefaultHttpTimeoutMs : DefaultStdioTimeoutMs;
            if (p.TimeoutMs >= MinConfiguredTimeoutMs && p.TimeoutMs != projectDefaultTimeout) g.TimeoutMs = p.TimeoutMs;
            if (p.IsRemote)
            {
                g.Type = p.IsSse ? "sse" : "http";
                if (!string.IsNullOrWhiteSpace(p.Url)) g.Url = p.Url.Trim();
                if (p.Headers != null && p.Headers.Count > 0)
                    g.Headers = new Dictionary<string, string>(p.Headers, StringComparer.OrdinalIgnoreCase);
            }
            else
            {
                if (p.Env != null && p.Env.Count > 0)
                    g.Env = new Dictionary<string, string>(p.Env, StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrWhiteSpace(p.Cwd)) g.Cwd = p.Cwd.Trim();
            }
            if (p.WatchPaths != null && p.WatchPaths.Length > 0) g.WatchPaths = (string[])p.WatchPaths.Clone();
            if (p.AlwaysAllow != null && p.AlwaysAllow.Length > 0) g.AlwaysAllow = (string[])p.AlwaysAllow.Clone();
            if (p.DisabledTools != null && p.DisabledTools.Length > 0) g.DisabledTools = (string[])p.DisabledTools.Clone();
            if (p.OAuth != null
                && !string.IsNullOrWhiteSpace(p.OAuth.ClientId)
                && !string.IsNullOrWhiteSpace(p.OAuth.AuthorizationUrl)
                && !string.IsNullOrWhiteSpace(p.OAuth.TokenUrl))
            {
                var oc = new McpServerConfig.McpOAuthConfig();
                oc.ClientId = (p.OAuth.ClientId ?? "").Trim();
                oc.AuthorizationUrl = (p.OAuth.AuthorizationUrl ?? "").Trim();
                oc.TokenUrl = (p.OAuth.TokenUrl ?? "").Trim();
                oc.Scope = p.OAuth.Scope != null ? (string[])p.OAuth.Scope.Clone() : new string[0];
                oc.RedirectPort = p.OAuth.RedirectPort > 0 && p.OAuth.RedirectPort <= 65535 ? p.OAuth.RedirectPort : 53821;
                g.OAuth = oc;
            }
            return g;
        }

        public static void SaveServersForProject(string projectKey, List<McpServerConfig> servers)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(projectKey)) return;
                string p = ProjectConfigPath(projectKey);
                try { Directory.CreateDirectory(Path.GetDirectoryName(p)); } catch { }
                File.WriteAllText(p, SerializeServers(servers).ToString(Newtonsoft.Json.Formatting.None));
                InvalidateEffectiveServersCache();
            }
            catch { }
        }

        public static List<McpServerConfig> LoadEffectiveServers()
        {
            string key = ProjectKey ?? "";
            lock (ConfigCacheGate)
            {
                if (_effectiveServersCache != null &&
                    string.Equals(_effectiveServersCacheKey, key, StringComparison.Ordinal) &&
                    (DateTime.UtcNow - _effectiveServersCacheAt).TotalMilliseconds < EffectiveServersCacheTtlMs)
                    return new List<McpServerConfig>(_effectiveServersCache);
            }
            var list = LoadServersForProject(key);
            lock (ConfigCacheGate)
            {
                _effectiveServersCache = list;
                _effectiveServersCacheKey = key;
                _effectiveServersCacheAt = DateTime.UtcNow;
            }
            return new List<McpServerConfig>(list);
        }

        private static void InvalidateEffectiveServersCache()
        {
            lock (ConfigCacheGate)
            {
                _effectiveServersCache = null;
                _effectiveServersCacheKey = null;
                _effectiveServersCacheAt = DateTime.MinValue;
            }
        }

        public static McpServerConfig ConfigForServer(string serverName)
        {
            string target = ResolveName(serverName);
            var list = LoadEffectiveServers();
            if (list != null)
                foreach (var s in list)
                    if (s != null && string.Equals(s.Name, target, StringComparison.OrdinalIgnoreCase)) return s;
            return null;
        }

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
                return ParseServersObject(j);
            }
            catch { return fallback; }
        }

        private static List<McpServerConfig> ParseServersObject(JObject j)
        {
            var fallback = new List<McpServerConfig> { DefaultServerConfig() };
            try
            {
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
                            if (!seen.Add(name)) continue;
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
                                string astr = (string)o["args"];
                                if (!string.IsNullOrWhiteSpace(astr))
                                {
                                    var parts = SplitArgs(astr);
                                    if (parts.Length > 0) s.Args = parts;
                                }
                            }
                            string type = ((string)o["type"] ?? "").Trim();
                            s.Type = string.Equals(type, "http", StringComparison.OrdinalIgnoreCase) ? "http"
                                : (string.Equals(type, "sse", StringComparison.OrdinalIgnoreCase) ? "sse" : "stdio");
                            if (s.IsRemote)
                            {
                                string url = ((string)o["url"] ?? "").Trim();
                                if (url.Length > 0) s.Url = url;
                                s.Headers = ParseStringMap(o["headers"]);
                            }
                            else
                            {
                                s.Env = ParseStringMap(o["env"]);
                                string cwd = ((string)o["cwd"] ?? "").Trim();
                                if (cwd.Length > 0) s.Cwd = cwd;
                            }
                            if (o["enabled"] != null) { try { s.Enabled = (bool)o["enabled"]; } catch { s.Enabled = true; } }
                            int? tmo = (int?)o["timeoutMs"];
                            s.TimeoutMs = (tmo.HasValue && tmo.Value >= MinConfiguredTimeoutMs)
                                ? tmo.Value
                                : (s.IsRemote ? DefaultHttpTimeoutMs : DefaultStdioTimeoutMs);
                            int stmo = (int?)o["startTimeoutMs"] ?? 0;
                            s.StartTimeoutMs = stmo >= MinConfiguredTimeoutMs ? stmo : 0;
                            s.WatchPaths = ParseStringArray(o["watchPaths"]);
                            s.AlwaysAllow = ParseStringArray(o["alwaysAllow"]);
                            s.DisabledTools = ParseStringArray(o["disabledTools"]);
                            s.OAuth = ParseOAuthConfig(o["oauth"]);
                            list.Add(s);
                        }
                        catch { }
                    }
                    if (list.Count == 0) return fallback;
                    return list;
                }
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
                    int stmo = (int?)j["startTimeoutMs"] ?? 0;
                    if (stmo >= MinConfiguredTimeoutMs) one.StartTimeoutMs = stmo;
                    if (j["enabled"] != null) { try { one.Enabled = (bool)j["enabled"]; } catch { one.Enabled = true; } }
                    int? ptmo = (int?)j["timeoutMs"];
                    one.TimeoutMs = (ptmo.HasValue && ptmo.Value >= MinConfiguredTimeoutMs) ? ptmo.Value : DefaultStdioTimeoutMs;
                    if ((int?)j["startTimeoutMs"] == null) one.StartTimeoutMs = 0;
                }
                catch { }
                try { if ((int?)j["startTimeoutMs"] == null) one.StartTimeoutMs = 0; } catch { }
                one.WatchPaths = ParseStringArray(j["watchPaths"]);
                one.AlwaysAllow = ParseStringArray(j["alwaysAllow"]);
                one.DisabledTools = ParseStringArray(j["disabledTools"]);
                return new List<McpServerConfig> { one };
            }
            catch { return fallback; }
        }

        private static string[] ParseStringArray(JToken tok)
        {
            var list = new List<string>();
            try
            {
                var ja = tok as JArray;
                if (ja != null)
                {
                    foreach (var a in ja) { string s = (string)a; if (!string.IsNullOrWhiteSpace(s)) list.Add(s.Trim()); }
                    return list.ToArray();
                }
                string one = (string)tok;
                if (!string.IsNullOrWhiteSpace(one))
                    foreach (var s in one.Split(new[] { ',', ';', '\n', '\r', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                        if (!string.IsNullOrWhiteSpace(s)) list.Add(s.Trim());
            }
            catch { }
            return list.ToArray();
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

        private static Dictionary<string, string> ParseStringMap(JToken tok)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var o = tok as JObject;
                if (o == null) return map;
                foreach (var p in o.Properties())
                {
                    try
                    {
                        if (string.IsNullOrWhiteSpace(p.Name) || p.Value == null) continue;
                        if (p.Value.Type == JTokenType.Null) continue;
                        string v = p.Value.Type == JTokenType.String ? (string)p.Value : p.Value.ToString(Newtonsoft.Json.Formatting.None);
                        if (!string.IsNullOrEmpty(v)) map[p.Name.Trim()] = v;
                    }
                    catch { }
                }
            }
            catch { }
            return map;
        }

        private static JObject StringMapJson(Dictionary<string, string> map)
        {
            var o = new JObject();
            if (map != null)
                foreach (var kv in map)
                {
                    try { if (!string.IsNullOrEmpty(kv.Key) && kv.Value != null) o[kv.Key] = kv.Value; }
                    catch { }
                }
            return o;
        }

        private static JArray StringArrayJson(string[] values)
        {
            var ja = new JArray();
            if (values != null)
                foreach (var v in values) if (!string.IsNullOrWhiteSpace(v)) ja.Add(v.Trim());
            return ja;
        }

        private static JObject SerializeServers(List<McpServerConfig> servers)
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
                        int tmo = s.TimeoutMs >= MinConfiguredTimeoutMs
                            ? s.TimeoutMs
                            : (s.IsRemote ? DefaultHttpTimeoutMs : DefaultStdioTimeoutMs);
                        var o = new JObject
                        {
                            ["name"] = s.Name.Trim(),
                            ["command"] = cmd,
                            ["args"] = ja,
                            ["enabled"] = s.Enabled,
                            ["timeoutMs"] = tmo
                        };
                        if (s.StartTimeoutMs >= MinConfiguredTimeoutMs && s.StartTimeoutMs != tmo)
                            o["startTimeoutMs"] = s.StartTimeoutMs;
                        if (s.IsRemote)
                        {
                            o["type"] = s.IsSse ? "sse" : "http";
                            o["url"] = s.Url ?? "";
                            if (s.Headers != null && s.Headers.Count > 0) o["headers"] = StringMapJson(s.Headers);
                        }
                        else
                        {
                            if (s.Env != null && s.Env.Count > 0) o["env"] = StringMapJson(s.Env);
                            if (!string.IsNullOrWhiteSpace(s.Cwd)) o["cwd"] = s.Cwd.Trim();
                        }
                        if (s.WatchPaths != null && s.WatchPaths.Length > 0) o["watchPaths"] = StringArrayJson(s.WatchPaths);
                        if (s.AlwaysAllow != null && s.AlwaysAllow.Length > 0) o["alwaysAllow"] = StringArrayJson(s.AlwaysAllow);
                        if (s.DisabledTools != null && s.DisabledTools.Length > 0) o["disabledTools"] = StringArrayJson(s.DisabledTools);
                        if (s.OAuth != null && (!string.IsNullOrWhiteSpace(s.OAuth.ClientId) || !string.IsNullOrWhiteSpace(s.OAuth.AuthorizationUrl) || !string.IsNullOrWhiteSpace(s.OAuth.TokenUrl)))
                        {
                            var oo = new JObject
                            {
                                ["clientId"] = (s.OAuth.ClientId ?? "").Trim(),
                                ["authorizationUrl"] = (s.OAuth.AuthorizationUrl ?? "").Trim(),
                                ["tokenUrl"] = (s.OAuth.TokenUrl ?? "").Trim(),
                                ["redirectPort"] = s.OAuth.RedirectPort > 0 ? s.OAuth.RedirectPort : 53821
                            };
                            if (s.OAuth.Scope != null && s.OAuth.Scope.Length > 0) oo["scope"] = StringArrayJson(s.OAuth.Scope);
                            o["oauth"] = oo;
                        }
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
            return new JObject { ["servers"] = arr };
        }

        public static void SaveServers(List<McpServerConfig> servers)
        {
            try
            {
                string p = ConfigPath();
                try { Directory.CreateDirectory(Path.GetDirectoryName(p)); } catch { }
                File.WriteAllText(p, SerializeServers(servers).ToString(Newtonsoft.Json.Formatting.None));
                InvalidateEffectiveServersCache();
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
                if (timeoutMs.HasValue && timeoutMs.Value >= MinConfiguredTimeoutMs) cur.TimeoutMs = timeoutMs.Value;
                if (!string.IsNullOrWhiteSpace(command)) cur.Command = command.Trim();
                if (args != null && args.Length > 0)
                {
                    var kept = new List<string>();
                    foreach (var a in args) if (!string.IsNullOrWhiteSpace(a)) kept.Add(a);
                    if (kept.Count > 0) cur.Args = kept.ToArray();
                }
                if (startTimeoutMs.HasValue)
                    cur.StartTimeoutMs = startTimeoutMs.Value >= MinConfiguredTimeoutMs ? startTimeoutMs.Value : 0;
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

        private static string ResolveName(string serverName)
        {
            try
            {
                string n = (serverName ?? "").Trim();
                if (n.Length > 0) return n;
                var list = LoadEffectiveServers();
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
                var list = LoadEffectiveServers();
                if (list != null)
                    foreach (var s in list)
                        if (s != null && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
                            return s;
            }
            catch { }
            return null;
        }

        private static List<string> ConfiguredServerNames()
        {
            var names = new List<string>();
            try
            {
                var list = LoadEffectiveServers();
                if (list != null)
                    foreach (var s in list)
                        if (s != null && !string.IsNullOrWhiteSpace(s.Name)) names.Add(s.Name.Trim());
            }
            catch { }
            return names;
        }

        private static bool IsHttpServer(string name)
        {
            var cfg = FindServer(name);
            return cfg != null && cfg.IsHttp;
        }

        private static bool IsSseServer(string name)
        {
            var cfg = FindServer(name);
            return cfg != null && cfg.IsSse;
        }

        public static bool TrySplitQualified(string qualified, IEnumerable<string> serverNames, out string server, out string tool)
        {
            server = null;
            tool = null;
            if (string.IsNullOrWhiteSpace(qualified) || serverNames == null) return false;
            string q = qualified.Trim();
            string best = null;
            foreach (var raw in serverNames)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string s = raw.Trim();
                if (s.Length == 0 || q.Length <= s.Length + 1) continue;
                if (q[s.Length] != '_') continue;
                if (!string.Equals(q.Substring(0, s.Length), s, StringComparison.OrdinalIgnoreCase)) continue;
                if (best == null || s.Length > best.Length) best = s;
            }
            if (best == null) return false;
            string remainder = q.Substring(best.Length + 1);
            if (remainder.Length == 0) return false;
            JArray cached = null;
            try
            {
                lock (Gate)
                {
                    McpConn conn;
                    if (Conns.TryGetValue(best, out conn) && conn != null && conn.CachedTools != null && conn.CachedTools.Count > 0)
                        cached = conn.CachedTools;
                }
            }
            catch { }
            if (cached != null)
            {
                bool found = false;
                foreach (var t in cached)
                {
                    try
                    {
                        string tn = t is JObject ? (string)((JObject)t)["name"] : (string)t;
                        if (string.Equals(tn, remainder, StringComparison.OrdinalIgnoreCase)) { found = true; break; }
                    }
                    catch { }
                }
                if (!found) return false;
            }
            server = best;
            tool = remainder;
            return true;
        }

        public static string ServerForTool(string qualifiedOrRaw, IEnumerable<string> serverNames)
        {
            string s, t;
            return TrySplitQualified(qualifiedOrRaw, serverNames, out s, out t) ? s : null;
        }

        public static string ServerForTool(string qualifiedOrRaw)
        {
            return ServerForTool(qualifiedOrRaw, ConfiguredServerNames());
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

        private static void OnToolsListChanged(string name, McpConn conn)
        {
            string target = ResolveName(name);
            try
            {
                lock (Gate)
                {
                    if (conn != null) { conn.CachedTools = null; conn.CachedResources = null; conn.CachedResourceTemplates = null; conn.CachedPrompts = null; }
                    McpConn cur;
                    if (Conns.TryGetValue(target, out cur) && cur != null) { cur.CachedTools = null; cur.CachedResources = null; cur.CachedResourceTemplates = null; cur.CachedPrompts = null; }
                }
            }
            catch { }
            var hook = ServerToolsChanged;
            if (hook != null)
            {
                try { hook(target); } catch { }
            }
        }

        private static bool HandleNotification(string serverName, JObject o)
        {
            try
            {
                if (o == null) return false;
                var jt = o["id"];
                if (jt != null && jt.Type != JTokenType.Null) return false;
                string m = (string)o["method"];
                if (string.IsNullOrEmpty(m)) return false;
                if (string.Equals(m, "notifications/tools/list_changed", StringComparison.Ordinal) ||
                    string.Equals(m, "notifications/resources/list_changed", StringComparison.Ordinal) ||
                    string.Equals(m, "notifications/resources/updated", StringComparison.Ordinal) ||
                    string.Equals(m, "notifications/prompts/list_changed", StringComparison.Ordinal))
                {
                    string target = ResolveName(serverName);
                    McpConn conn = null;
                    try { lock (Gate) { Conns.TryGetValue(target, out conn); } } catch { }
                    OnToolsListChanged(target, conn);
                    return true;
                }
                return true;
            }
            catch { }
            return false;
        }

        private static void StoreInstructions(string name, JObject initResult)
        {
            string instr = "";
            try
            {
                if (initResult != null)
                {
                    var tok = initResult["instructions"];
                    if (tok != null && tok.Type != JTokenType.Null) instr = (string)tok ?? "";
                }
                if (instr.Length > 4000) instr = instr.Substring(0, 4000);
            }
            catch { instr = ""; }
            try { lock (Gate) { GetConnLocked(name).Instructions = instr; } } catch { }
        }

        private static string GetInstructions(string name)
        {
            try
            {
                lock (Gate)
                {
                    McpConn c;
                    return Conns.TryGetValue(name, out c) && c != null ? (c.Instructions ?? "") : "";
                }
            }
            catch { return ""; }
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
                    if (!Conns.TryGetValue(name, out c) || c == null) return false;
                    if (c.IsHttp || c.IsSse) return c.HttpReady && !c.HttpFailed;
                    if (c.Proc == null) return false;
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
                var list = LoadEffectiveServers();
                if (list == null || list.Count == 0) list = new List<McpServerConfig> { DefaultServerConfig() };
                foreach (var s in list)
                {
                    try
                    {
                        if (s == null) continue;
                        bool running = false;
                        try { running = IsRunningServer(s.Name); } catch { }
                        var jo = new JObject
                        {
                            ["name"] = s.Name ?? "default",
                            ["command"] = s.Command ?? "uvx",
                            ["args"] = s.Args != null ? string.Join(" ", s.Args) : "",
                            ["enabled"] = s.Enabled,
                            ["timeoutMs"] = s.TimeoutMs >= MinConfiguredTimeoutMs ? s.TimeoutMs : (s.IsRemote ? DefaultHttpTimeoutMs : DefaultStdioTimeoutMs),
                            ["startTimeoutMs"] = s.StartTimeoutMs,
                            ["type"] = s.IsSse ? "sse" : (s.IsHttp ? "http" : "stdio"),
                            ["url"] = s.IsRemote ? (s.Url ?? "") : "",
                            ["running"] = running,
                            ["cwd"] = s.Cwd ?? "",
                            ["env"] = StringMapJson(s.Env),
                            ["headers"] = StringMapJson(s.Headers),
                            ["watchPaths"] = StringArrayJson(s.WatchPaths),
                            ["alwaysAllow"] = StringArrayJson(s.AlwaysAllow),
                            ["disabledTools"] = StringArrayJson(s.DisabledTools)
                        };
                        if (s.OAuth != null)
                        {
                            var oo = new JObject
                            {
                                ["clientId"] = (s.OAuth.ClientId ?? "").Trim(),
                                ["authorizationUrl"] = (s.OAuth.AuthorizationUrl ?? "").Trim(),
                                ["tokenUrl"] = (s.OAuth.TokenUrl ?? "").Trim(),
                                ["redirectPort"] = s.OAuth.RedirectPort > 0 ? s.OAuth.RedirectPort : OAuthRedirectPortDefault,
                                ["scope"] = StringArrayJson(s.OAuth.Scope)
                            };
                            jo["oauth"] = oo;
                        }
                        else
                        {
                            jo["oauth"] = JValue.CreateNull();
                        }
                        arr.Add(jo);
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
            command = "uvx";
            args = "solidworks-mcp";
            enabled = true;
            timeoutMs = DefaultStdioTimeoutMs;
            try
            {
                var list = LoadServers();
                var s = (list != null && list.Count > 0) ? list[0] : DefaultServerConfig();
                if (s == null) s = DefaultServerConfig();
                command = string.IsNullOrWhiteSpace(s.Command) ? "uvx" : s.Command;
                args = (s.Args != null && s.Args.Length > 0) ? string.Join(" ", s.Args) : "solidworks-mcp";
                enabled = s.Enabled;
                timeoutMs = s.TimeoutMs >= MinConfiguredTimeoutMs ? s.TimeoutMs : DefaultStdioTimeoutMs;
            }
            catch { }
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
            try { servers = LoadEffectiveServers(); } catch { servers = null; }
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
                        return new RouteResult { Tools = tools, Winner = s.Name, Instructions = GetInstructions(s.Name) };
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { /* next server */ }
            }
            return new RouteResult { Tools = new JArray(), Winner = null };
        }

        public static async Task<RouteResult> RouteAsync(string qualified, CancellationToken ct = default(CancellationToken))
        {
            string server, tool;
            if (!TrySplitQualified(qualified, ConfiguredServerNames(), out server, out tool))
                return await RouteAsync(ct).ConfigureAwait(false);
            var cfg = FindServer(server);
            if (cfg == null || !cfg.Enabled)
                throw new McpException("MCP server " + server + " is disabled/unavailable for tool " + tool + " (disabled).");
            try { await EnsureStartedOnAsync(server, 0, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { throw new McpException("MCP server " + server + " is disabled/unavailable for tool " + tool + " (unavailable)."); }
            JArray tools = await ListToolsAsync(server, ct).ConfigureAwait(false);
            if (tools != null && tools.Count > 0) return new RouteResult { Tools = tools, Winner = server, Instructions = GetInstructions(server) };
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
                int want = timeoutMs > 0 ? timeoutMs : cfg.EffectiveStartTimeoutMs;
                if (want <= 30000) want = 60000;
                initTimeout = want;
                var init = new JObject
                {
                    ["protocolVersion"] = "2024-11-05",
                    ["capabilities"] = new JObject
                    {
                        ["tools"] = new JObject { ["listChanged"] = true },
                        ["resources"] = new JObject { ["subscribe"] = false, ["listChanged"] = true },
                        ["prompts"] = new JObject { ["listChanged"] = true }
                    },
                    ["clientInfo"] = new JObject { ["name"] = "Forge", ["version"] = "1.0" }
                };
                if (cfg.IsHttp)
                {
                    bool alreadyReady;
                    lock (Gate)
                    {
                        var conn = GetConnLocked(name);
                        alreadyReady = conn.HttpReady && !conn.HttpFailed;
                        if (!alreadyReady)
                        {
                            conn.IsHttp = true;
                            conn.Url = cfg.Url;
                            conn.Headers = cfg.Headers;
                            EnsureHttpClientLocked(conn);
                        }
                    }
                    if (alreadyReady) { RegisterWatch(name); return; }
                    // Handshake outside the process lock but inside the start gate (IO).
                    try
                    {
                        JObject res = await RpcOnServerAsync(name, "initialize", init, initTimeout, ct).ConfigureAwait(false);
                        if (res == null) throw new McpException("MCP server '" + name + "' returned an empty initialize response — check the server URL and retry.");
                        StoreInstructions(name, res);
                        await NotifyOnServerAsync(name, "notifications/initialized", new JObject()).ConfigureAwait(false);
                        bool notifyFailed;
                        lock (Gate) { var c = GetConnLocked(name); notifyFailed = c.HttpFailed; if (!notifyFailed) c.HttpReady = true; }
                        if (notifyFailed)
                            throw new McpException("MCP server '" + name + "' failed to confirm initialization — check the URL/headers and the server logs, then retry.");
                        RegisterWatch(name);
                    }
                    catch (OperationCanceledException) { Stop(name); throw; }
                    catch (Exception ex)
                    {
                        Stop(name);
                        if (ex is McpException) throw;
                        throw new McpException("MCP server '" + name + "' handshake failed: " + ex.Message + " — check the MCP server URL and retry.");
                    }
                }
                else if (cfg.IsSse)
                {
                    bool alreadyReady;
                    lock (Gate)
                    {
                        var conn = GetConnLocked(name);
                        alreadyReady = (conn.IsSse && conn.HttpReady && !conn.HttpFailed && !string.IsNullOrEmpty(conn.SsePostUrl));
                        if (!alreadyReady)
                        {
                            conn.IsHttp = false;
                            conn.IsSse = true;
                            conn.Url = cfg.Url;
                            conn.Headers = cfg.Headers;
                            EnsureHttpClientLocked(conn);
                        }
                    }
                    if (alreadyReady) { RegisterWatch(name); return; }
                    try
                    {
                        JObject res = await RpcOnServerAsync(name, "initialize", init, initTimeout, ct).ConfigureAwait(false);
                        if (res == null) throw new McpException("MCP server '" + name + "' returned an empty initialize response — check the server SSE URL and retry.");
                        StoreInstructions(name, res);
                        await NotifyOnServerAsync(name, "notifications/initialized", new JObject()).ConfigureAwait(false);
                        bool notifyFailed;
                        lock (Gate) { var c = GetConnLocked(name); notifyFailed = c.HttpFailed; if (!notifyFailed) c.HttpReady = true; }
                        if (notifyFailed)
                            throw new McpException("MCP server '" + name + "' failed to confirm initialization — check the SSE URL/headers and the server logs, then retry.");
                        RegisterWatch(name);
                    }
                    catch (OperationCanceledException) { Stop(name); throw; }
                    catch (Exception ex)
                    {
                        Stop(name);
                        if (ex is McpException) throw;
                        throw new McpException("MCP server '" + name + "' handshake failed: " + ex.Message + " — check the MCP server SSE URL and retry.");
                    }
                }
                else
                {
                    bool alreadyRunning = false;
                    lock (Gate)
                    {
                        var conn = GetConnLocked(name);
                        if (conn.Proc != null) { try { if (!conn.Proc.HasExited) alreadyRunning = true; } catch { } }
                        if (!alreadyRunning) StartLocked(name, conn, cfg);
                    }
                    if (alreadyRunning) { RegisterWatch(name); return; }
                    // Handshake outside the process lock but inside the start gate (IO).
                    try
                    {
                        JObject res = await RpcOnServerAsync(name, "initialize", init, initTimeout, ct).ConfigureAwait(false);
                        if (res == null) throw new McpException("MCP server '" + name + "' returned an empty initialize response — run `uvx solidworks-mcp --selftest` with SolidWorks open and retry.");
                        StoreInstructions(name, res);
                        await NotifyOnServerAsync(name, "notifications/initialized", new JObject()).ConfigureAwait(false);
                        RegisterWatch(name);
                    }
                    catch (OperationCanceledException) { Stop(name); throw; } // stop wins: never rewrap cancel as a handshake error
                    catch (Exception ex)
                    {
                        Stop(name);
                        if (ex is McpException) throw;
                        throw new McpException("MCP server '" + name + "' handshake failed: " + ex.Message + " — run `uvx solidworks-mcp --selftest` with SolidWorks open and retry.");
                    }
                }
            }
            finally { if (acquired) { try { StartGate.Release(); } catch { } } }
        }

        private static void RegisterWatch(string name)
        {
            try
            {
                var cfg = FindServer(name);
                lock (Gate) { StartWatchLocked(name, GetConnLocked(name), cfg); }
            }
            catch { }
        }

        private static void StartWatchLocked(string name, McpConn conn, McpServerConfig cfg)
        {
            try { if (conn.WatchTimer != null) conn.WatchTimer.Dispose(); } catch { }
            conn.WatchTimer = null;
            conn.WatchPaths = null;
            try { conn.WatchStamps.Clear(); } catch { }
            conn.WatchBusy = 0;
            if (cfg == null || !cfg.Enabled || cfg.WatchPaths == null || cfg.WatchPaths.Length == 0) return;
            var paths = new List<string>();
            foreach (var raw in cfg.WatchPaths)
            {
                try
                {
                    string p = (raw ?? "").Trim();
                    if (p.Length == 0) continue;
                    p = Environment.ExpandEnvironmentVariables(p);
                    if (!Path.IsPathRooted(p)) p = Path.GetFullPath(p);
                    paths.Add(p);
                }
                catch { }
            }
            if (paths.Count == 0) return;
            conn.WatchPaths = paths.ToArray();
            RefreshWatchStampsLocked(conn);
            conn.WatchTimer = new Timer(WatchTick, new WatchState { Name = name }, WatchPollMs, WatchPollMs);
        }

        private static void RefreshWatchStampsLocked(McpConn conn)
        {
            try
            {
                try { conn.WatchStamps.Clear(); } catch { }
                foreach (string p in conn.WatchPaths ?? new string[0])
                {
                    try { conn.WatchStamps[p] = WatchStampFor(p); }
                    catch { try { conn.WatchStamps[p] = DateTime.MinValue; } catch { } }
                }
            }
            catch { }
        }

        private static DateTime WatchStampFor(string path)
        {
            try
            {
                if (File.Exists(path)) return File.GetLastWriteTimeUtc(path);
                if (Directory.Exists(path)) return Directory.GetLastWriteTimeUtc(path);
            }
            catch { }
            return DateTime.MinValue;
        }

        private static void WatchTick(object state)
        {
            var ws = state as WatchState;
            if (ws == null) return;
            string name = ws.Name;
            McpConn conn;
            string[] paths;
            var stamps = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            try
            {
                lock (Gate)
                {
                    if (!Conns.TryGetValue(name, out conn) || conn == null || conn.WatchTimer == null ||
                        conn.WatchPaths == null || conn.WatchPaths.Length == 0 || conn.WatchBusy != 0) return;
                    paths = conn.WatchPaths;
                    foreach (var kv in conn.WatchStamps) stamps[kv.Key] = kv.Value;
                }
            }
            catch { return; }
            bool changed = false;
            foreach (string p in paths)
            {
                try
                {
                    DateTime now = WatchStampFor(p);
                    DateTime prev;
                    if (!stamps.TryGetValue(p, out prev) || prev != now) changed = true;
                    stamps[p] = now;
                }
                catch { }
            }
            if (!changed) return;
            try
            {
                lock (Gate)
                {
                    if (!Conns.TryGetValue(name, out conn) || conn == null || conn.WatchTimer == null) return;
                    if (System.Threading.Interlocked.CompareExchange(ref conn.WatchBusy, 1, 0) != 0) return;
                    foreach (var kv in stamps) conn.WatchStamps[kv.Key] = kv.Value;
                }
            }
            catch { return; }
            var task = RestartWatchedAsync(name);
            ObserveFault(task);
        }

        private static async Task RestartWatchedAsync(string name)
        {
            McpConn conn = null;
            try
            {
                bool wasRunning = IsRunningServer(name);
                try { Stop(name); } catch { }
                if (!wasRunning) return;
                var cfg = FindServer(name);
                if (cfg == null || !cfg.Enabled) return;
                await EnsureStartedAsync(name, 0, CancellationToken.None).ConfigureAwait(false);
            }
            catch { }
            finally
            {
                bool needRegister = false;
                try
                {
                    lock (Gate)
                    {
                        if (Conns.TryGetValue(name, out conn) && conn != null)
                        {
                            conn.WatchBusy = 0;
                            RefreshWatchStampsLocked(conn);
                            needRegister = conn.WatchTimer == null;
                        }
                    }
                }
                catch { }
                if (needRegister) { try { RegisterWatch(name); } catch { } }
            }
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
            if (cfg.Env != null)
                foreach (var kv in cfg.Env)
                    if (!string.IsNullOrEmpty(kv.Key) && !string.IsNullOrEmpty(kv.Value))
                        psi.EnvironmentVariables[kv.Key] = kv.Value;
            if (!string.IsNullOrWhiteSpace(cfg.Cwd) && Directory.Exists(cfg.Cwd))
                psi.WorkingDirectory = cfg.Cwd;
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
            conn.DrainThread.Start(new DrainState { Name = name, Conn = conn, Reader = conn.Proc.StandardError });
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
                        if (jt == null || jt.Type == JTokenType.Null)
                        {
                            try
                            {
                                string m = (string)msg["method"];
                                    if (string.Equals(m, "notifications/tools/list_changed", StringComparison.Ordinal) ||
                                        string.Equals(m, "notifications/resources/list_changed", StringComparison.Ordinal) ||
                                        string.Equals(m, "notifications/resources/updated", StringComparison.Ordinal) ||
                                        string.Equals(m, "notifications/prompts/list_changed", StringComparison.Ordinal))
                                        OnToolsListChanged(rs.Name, conn);
                            }
                            catch { }
                            continue;
                        }
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
            AppendServerLog(rs.Name, "[forge] MCP server process exited");
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

        private static void AppendServerLog(string name, string line)
        {
            try
            {
                if (line == null) return;
                string target = ResolveName(name);
                lock (Gate)
                {
                    var c = GetConnLocked(target);
                    if (line.Length > 4000) line = line.Substring(0, 4000) + "…";
                    if (c.StderrTail.Length > 0) c.StderrTail.Append('\n');
                    c.StderrTail.Append(line);
                    if (c.StderrTail.Length > ServerLogTailChars)
                    {
                        string s = c.StderrTail.ToString();
                        c.StderrTail.Clear();
                        c.StderrTail.Append(s.Substring(s.Length - ServerLogTailChars));
                    }
                }
            }
            catch { }
        }

        public static string ServerLog(string serverName = null)
        {
            try
            {
                string name = ResolveName(serverName);
                lock (Gate)
                {
                    McpConn c;
                    return Conns.TryGetValue(name, out c) && c != null ? c.StderrTail.ToString() : "";
                }
            }
            catch { return ""; }
        }

        private static void DrainLoop(object state)
        {
            var ds = state as DrainState;
            if (ds == null || ds.Reader == null) return;
            try
            {
                string line;
                while ((line = ds.Reader.ReadLine()) != null) AppendServerLog(ds.Name, line);
            }
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
            try { if (conn.WatchTimer != null) conn.WatchTimer.Dispose(); } catch { }
            conn.WatchTimer = null;
            conn.WatchPaths = null;
            try { conn.WatchStamps.Clear(); } catch { }
            conn.WatchBusy = 0;
            conn.CachedTools = null;
            conn.CachedResources = null;
            conn.CachedResourceTemplates = null;
            conn.CachedPrompts = null;
            conn.Instructions = "";
            try { if (conn.SseCts != null) conn.SseCts.Cancel(); } catch { }
            conn.SseCts = null;
            conn.SseThread = null;
            try { if (conn.SseResponse != null) conn.SseResponse.Dispose(); } catch { }
            conn.SseResponse = null;
            try { if (conn.SseClient != null) conn.SseClient.Dispose(); } catch { }
            conn.SseClient = null;
            conn.SsePostUrl = null;
            conn.IsSse = false;
            conn.HttpReady = false;
            conn.HttpFailed = false;
            conn.SessionId = null;
            conn.IsHttp = false;
            conn.Url = null;
            conn.Headers = null;
            var oldHttp = conn.Http;
            conn.Http = null;
            if (oldHttp != null)
            {
                bool inFlight;
                lock (Gate)
                {
                    int n;
                    inFlight = HttpInFlight.TryGetValue(oldHttp, out n) && n > 0;
                    if (inFlight) DetachedHttpClients.Add(oldHttp);
                }
                if (!inFlight) { try { oldHttp.Dispose(); } catch { } }
            }
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
            if (IsHttpServer(name)) return HttpNotifyAsync(name, method, p);
            if (IsSseServer(name)) return SseNotifyAsync(name, method, p);
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
                || m.IndexOf("unavailable", StringComparison.OrdinalIgnoreCase) >= 0
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

        public static async Task<bool> PingAsync(string name, CancellationToken ct = default(CancellationToken))
        {
            try
            {
                string target = ResolveName(name);
                var cfg = FindServer(target);
                if (cfg == null || !cfg.Enabled) return false;
                if (ct.IsCancellationRequested) return false;
                int tmo = cfg.TimeoutMs >= MinConfiguredTimeoutMs ? cfg.TimeoutMs : 60000;
                if (tmo > 10000) tmo = 10000;
                await RpcOnServerAsync(target, "ping", null, tmo, ct).ConfigureAwait(false);
                return true;
            }
            catch { return false; }
        }

        /// <summary>Raw JSON-RPC call on one server. Returns the `result` object (throws on error member / timeout).</summary>
        public static async Task<JObject> RpcOnServerAsync(string serverName, string method, JObject p, int timeoutMs, CancellationToken ct = default(CancellationToken))
        {
            string name = ResolveName(serverName);
            if (timeoutMs <= 0) timeoutMs = 60000; // Task.Delay throws on 0/negative: bound it instead
            if (string.IsNullOrWhiteSpace(method)) throw new McpException("MCP method must not be empty.");
            bool http;
            bool sse;
            lock (Gate)
            {
                McpConn existing;
                http = Conns.TryGetValue(name, out existing) && existing != null && existing.IsHttp;
                sse = !http && Conns.TryGetValue(name, out existing) && existing != null && existing.IsSse;
            }
            if (!http && !sse)
            {
                var cfgs = FindServer(name);
                http = cfgs != null && cfgs.IsHttp;
                sse = !http && cfgs != null && cfgs.IsSse;
            }
            if (http) return await HttpRpcAsync(name, method, p, timeoutMs, ct).ConfigureAwait(false);
            if (sse) return await SseRpcAsync(name, method, p, timeoutMs, ct).ConfigureAwait(false);
            int id = Interlocked.Increment(ref NextId);
            var msg = new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
            if (p != null) msg["params"] = p;
            var tcs = new TaskCompletionSource<string>();
            lock (Gate)
            {
                var conn = GetConnLocked(name);
                if (conn.Stdin == null || (conn.Proc != null && conn.Proc.HasExited))
                    throw new McpException("MCP server '" + name + "' is not running (is SolidWorks still open?) — start it in Settings → MCP servers, then retry.");
                conn.Pending[id] = tcs;
                try { conn.Stdin.WriteLine(msg.ToString(Newtonsoft.Json.Formatting.None)); }
                catch (Exception ex)
                {
                    TaskCompletionSource<string> dropped;
                    conn.Pending.TryRemove(id, out dropped);
                    try { tcs.TrySetException(new McpException("MCP write failed on server '" + name + "': " + ex.Message)); } catch { }
                    ObserveFault(tcs.Task);
                    throw new McpException("MCP write failed on server '" + name + "': " + ex.Message + " — restart the server and retry.");
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
                try { tcs.TrySetException(new McpException("MCP server '" + name + "' timed out on `" + method + "`.")); } catch { }
                ObserveFault(tcs.Task); // late reply / racing death-fail must have no unobserved waiter
                throw new McpException("MCP server '" + name + "' timed out on `" + method + "` after " + (timeoutMs / 1000) + "s — check the server process/logs and retry.");
            }
            string line = await tcs.Task.ConfigureAwait(false);
            JObject resp;
            try { resp = JObject.Parse(line); }
            catch { throw new McpException("MCP server '" + name + "' returned non-JSON text for `" + method + "` (" + (line != null ? line.Length : 0) + " chars): " + Trunc(line, 200) + " — check the server logs and retry."); }
            var err = resp["error"];
            if (err != null)
            {
                string em = null;
                try { em = (string)err["message"]; } catch { }
                if (string.IsNullOrEmpty(em)) { try { em = err.ToString(); } catch { em = "unknown error"; } }
                throw new McpException("MCP server '" + name + "' rejected `" + method + "`: " + Trunc(em, 500) + " — check the tool name/arguments and the server logs, then retry.");
            }
            return resp["result"] as JObject;
        }

        private static void EnsureHttpClientLocked(McpConn conn)
        {
            if (conn.Http == null)
            {
                conn.Http = new HttpClient();
                try { conn.Http.Timeout = Timeout.InfiniteTimeSpan; } catch { }
            }
        }

        private static void MarkHttpFailed(McpConn conn)
        {
            try { lock (Gate) { conn.HttpFailed = true; conn.SessionId = null; } } catch { }
        }

        private static string ExpandEnvToken(string name, string original)
        {
            try
            {
                string v = Environment.GetEnvironmentVariable(name);
                if (!string.IsNullOrEmpty(v)) return v;
            }
            catch { }
            return original;
        }

        private static string ExpandHeaderValue(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            string s = value;
            try { s = Regex.Replace(s, @"\{env:([A-Za-z_][A-Za-z0-9_]*)\}", m => ExpandEnvToken(m.Groups[1].Value, m.Value)); } catch { }
            try { s = Regex.Replace(s, @"\$\{([A-Za-z_][A-Za-z0-9_]*)\}", m => ExpandEnvToken(m.Groups[1].Value, m.Value)); } catch { }
            try { s = Regex.Replace(s, @"%([A-Za-z_][A-Za-z0-9_]*)%", m => ExpandEnvToken(m.Groups[1].Value, m.Value)); } catch { }
            return s;
        }

        private static JObject TryParseRpcData(string data, string serverName)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(data) || data.Trim() == "[DONE]") return null;
                JObject o = JObject.Parse(data.Trim());
                if (o != null)
                {
                    if (HandleNotification(serverName, o)) return null;
                    if (o["result"] != null || o["error"] != null || o["jsonrpc"] != null) return o;
                }
            }
            catch { }
            return null;
        }

        private static JObject ParseRpcResponse(string text, string serverName)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(text)) return null;
                string s = text.Trim();
                if (s.StartsWith("{"))
                {
                    JObject direct = null;
                    try { direct = JObject.Parse(s); } catch { }
                    if (direct != null)
                    {
                        if (HandleNotification(serverName, direct)) return null;
                        if (direct["result"] != null || direct["error"] != null || direct["jsonrpc"] != null) return direct;
                    }
                }
                JObject found = null;
                string data = null;
                using (var sr = new StringReader(s))
                {
                    string line;
                    while ((line = sr.ReadLine()) != null)
                    {
                        if (line.StartsWith("data:", StringComparison.Ordinal))
                        {
                            string d = line.Substring(5);
                            if (d.StartsWith(" ")) d = d.Substring(1);
                            data = data == null ? d : data + "\n" + d;
                        }
                        else if (line.Length == 0)
                        {
                            found = TryParseRpcData(data, serverName);
                            data = null;
                            if (found != null) return found;
                        }
                    }
                }
                return found != null ? found : TryParseRpcData(data, serverName);
            }
            catch { return null; }
        }

        private static async Task<JObject> HttpPostAsync(string name, string method, string body, int timeoutMs, CancellationToken ct)
        {
            McpConn conn;
            HttpClient http;
            string url;
            string sid;
            Dictionary<string, string> hdrs;
            lock (Gate)
            {
                conn = GetConnLocked(name);
                EnsureHttpClientLocked(conn);
                http = conn.Http;
                url = !string.IsNullOrEmpty(conn.SsePostUrl) ? conn.SsePostUrl : conn.Url;
                sid = conn.SessionId;
                hdrs = conn.Headers;
                int n;
                HttpInFlight.TryGetValue(http, out n);
                HttpInFlight[http] = n + 1;
            }
            string oauthBearer = null;
            try { oauthBearer = await GetOAuthBearerAsync(name, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch { oauthBearer = null; }
            try
            {
                if (string.IsNullOrWhiteSpace(url)) throw new McpException("MCP server '" + name + "' has no URL configured — add a URL in Settings → MCP servers, then retry.");
                bool isNotification = method != null && method.StartsWith("notifications/", StringComparison.Ordinal);
                using (var timeoutCts = new CancellationTokenSource(timeoutMs))
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token))
                {
                    HttpResponseMessage respMsg = null;
                    try
                    {
                        using (var req = new HttpRequestMessage(HttpMethod.Post, url))
                        {
                            req.Content = new StringContent(body, Encoding.UTF8);
                            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                            bool hasAuthHeader = false;
                            if (hdrs != null)
                                foreach (var kv in hdrs)
                                {
                                    if (string.IsNullOrEmpty(kv.Key) || kv.Value == null) continue;
                                    string xv = ExpandHeaderValue(kv.Value);
                                    if (!req.Headers.TryAddWithoutValidation(kv.Key, xv))
                                        req.Content.Headers.TryAddWithoutValidation(kv.Key, xv);
                                    if (string.Equals(kv.Key, "Authorization", StringComparison.OrdinalIgnoreCase)) hasAuthHeader = true;
                                }
                            if (!hasAuthHeader && !string.IsNullOrEmpty(oauthBearer)) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + oauthBearer);
                            if (!string.IsNullOrEmpty(sid)) req.Headers.TryAddWithoutValidation("Mcp-Session-Id", sid);
                            respMsg = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                        MarkHttpFailed(conn);
                        throw new McpException("MCP server '" + name + "' timed out on `" + method + "` after " + (timeoutMs / 1000) + "s — check its URL/logs, then retry.");
                    }
                    catch (Exception ex)
                    {
                        MarkHttpFailed(conn);
                        throw new McpException("MCP server '" + name + "' unavailable (" + ex.Message + ") — check its URL/command and retry.");
                    }
                    using (respMsg)
                    {
                        try
                        {
                            IEnumerable<string> vals;
                            if (respMsg.Headers.TryGetValues("Mcp-Session-Id", out vals))
                            {
                                var got = vals.FirstOrDefault();
                                if (!string.IsNullOrEmpty(got)) lock (Gate) { conn.SessionId = got; }
                            }
                        }
                        catch { }
                        string text;
                        try
                        {
                            using (var stream = await respMsg.Content.ReadAsStreamAsync().ConfigureAwait(false))
                            using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                            {
                                var sb = new StringBuilder();
                                char[] buf = new char[8192];
                                int n;
                                while ((n = await reader.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false)) > 0)
                                {
                                    linked.Token.ThrowIfCancellationRequested();
                                    if (sb.Length + n > 16 * 1024 * 1024) break;
                                    sb.Append(buf, 0, n);
                                }
                                text = sb.ToString();
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                            MarkHttpFailed(conn);
                            throw new McpException("MCP server '" + name + "' timed out on `" + method + "` after " + (timeoutMs / 1000) + "s — check its URL/logs, then retry.");
                        }
                        catch (Exception ex)
                        {
                            MarkHttpFailed(conn);
                            throw new McpException("MCP server '" + name + "' unavailable (" + ex.Message + ") — check its URL/command and retry.");
                        }
                        if (!respMsg.IsSuccessStatusCode)
                        {
                            MarkHttpFailed(conn);
                            int httpCode = (int)respMsg.StatusCode;
                            string why = respMsg.ReasonPhrase ?? "";
                            if ((httpCode == 401 || LooksLikeNeedsAuth(text)) && ServerHasOAuth(name))
                            {
                                MarkOAuthNeedsAuth(name);
                                throw new McpException("MCP server '" + name + "' requires OAuth sign-in (HTTP " + httpCode + (string.IsNullOrEmpty(why) ? "" : " " + why) + " for `" + method + "`) — run OAuth sign-in for this server, then retry. [needs_auth]");
                            }
                            throw new McpException("MCP server '" + name + "' returned HTTP " + httpCode + (string.IsNullOrEmpty(why) ? "" : " " + why) + " for `" + method + "`" + (string.IsNullOrEmpty(text) ? "" : ": " + Trunc(text, 200)) + " — check the URL/headers and the server logs, then retry.");
                        }
                        JObject parsed = ParseRpcResponse(text, name);
                        if (parsed == null)
                        {
                            if (isNotification) { lock (Gate) { conn.HttpFailed = false; } return new JObject(); }
                            MarkHttpFailed(conn);
                            throw new McpException("MCP server '" + name + "' returned an unreadable response to `" + method + "` (" + (text != null ? text.Length : 0) + " chars): " + Trunc(text, 200) + " — check the server logs and retry.");
                        }
                        lock (Gate) { conn.HttpFailed = false; }
                        try { ClearOAuthNeedsAuth(name); } catch { }
                        return parsed;
                    }
                }
            }
            finally
            {
                bool dispose = false;
                lock (Gate)
                {
                    int n;
                    if (HttpInFlight.TryGetValue(http, out n))
                    {
                        if (n <= 1) HttpInFlight.Remove(http);
                        else HttpInFlight[http] = n - 1;
                    }
                    if (!HttpInFlight.ContainsKey(http) && DetachedHttpClients.Remove(http)) dispose = true;
                }
                if (dispose) { try { http.Dispose(); } catch { } }
            }
        }

        private static async Task<JObject> HttpRpcAsync(string name, string method, JObject p, int timeoutMs, CancellationToken ct)
        {
            if (timeoutMs <= 0) timeoutMs = 60000;
            int id = Interlocked.Increment(ref NextId);
            var msg = new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
            if (p != null) msg["params"] = p;
            JObject resp = await HttpPostAsync(name, method, msg.ToString(Newtonsoft.Json.Formatting.None), timeoutMs, ct).ConfigureAwait(false);
            var err = resp["error"];
            if (err != null) throw RpcError(name, method, err);
            JToken rid = resp["id"];
            if (rid != null && rid.Type != JTokenType.Null)
            {
                string got;
                try { got = rid.Type == JTokenType.String ? (string)rid : rid.ToString(Newtonsoft.Json.Formatting.None); }
                catch { got = ""; }
                if (!string.Equals(got.Trim('"'), id.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal))
                {
                    lock (Gate) { MarkHttpFailed(GetConnLocked(name)); }
                    throw new McpException("MCP server '" + name + "' returned a reply for id " + Trunc(got, 40) + " while waiting on " + id + " (concurrent calls on one server) — the connection was reset; retry the call.");
                }
            }
            return resp["result"] as JObject;
        }

        private static async Task HttpNotifyAsync(string name, string method, JObject p)
        {
            try
            {
                var msg = new JObject { ["jsonrpc"] = "2.0", ["method"] = method };
                if (p != null) msg["params"] = p;
                await HttpPostAsync(name, method, msg.ToString(Newtonsoft.Json.Formatting.None), 60000, CancellationToken.None).ConfigureAwait(false);
            }
            catch { }
        }

        private class SseReaderState
        {
            public string Name;
            public McpConn Conn;
            public HttpClient Client;
            public HttpResponseMessage Response;
            public Stream Stream;
            public CancellationTokenSource Cts;
            public TaskCompletionSource<string> EndpointTcs;
        }

        private static void TryAdoptSseEndpoint(string name, McpConn conn, SseReaderState rs, string endpoint)
        {
            try
            {
                string ep = (endpoint ?? "").Trim().Trim('"');
                if (ep.Length == 0) return;
                string baseUrl;
                lock (Gate) { baseUrl = conn.Url; if (!string.IsNullOrEmpty(conn.SsePostUrl)) return; }
                if (string.IsNullOrWhiteSpace(baseUrl)) return;
                string post;
                try
                {
                    if (ep.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || ep.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) post = ep;
                    else post = new Uri(new Uri(baseUrl), ep).ToString();
                }
                catch { return; }
                lock (Gate) { if (string.IsNullOrEmpty(conn.SsePostUrl)) conn.SsePostUrl = post; }
                try { if (rs.EndpointTcs != null) rs.EndpointTcs.TrySetResult(post); } catch { }
            }
            catch { }
        }

        private static void DispatchSseFrame(string name, McpConn conn, SseReaderState rs, string evt, string payload)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(payload) || payload.Trim() == "[DONE]") return;
                string t = payload.Trim();
                if (!t.StartsWith("{"))
                {
                    if (string.Equals(evt, "endpoint", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(evt))
                        TryAdoptSseEndpoint(name, conn, rs, t);
                    return;
                }
                JObject o = null;
                try { o = JObject.Parse(t); } catch { return; }
                if (o == null) return;
                var jt = o["id"];
                if (jt == null || jt.Type == JTokenType.Null)
                {
                    HandleNotification(name, o);
                    return;
                }
                int id = -1;
                try
                {
                    if (jt.Type == JTokenType.Integer) id = jt.Value<int>();
                    else if (jt.Type == JTokenType.String) { if (!int.TryParse((string)jt, out id)) return; }
                    else { try { id = Convert.ToInt32(((JValue)jt).Value); } catch { return; } }
                }
                catch { return; }
                TaskCompletionSource<string> tcs;
                if (conn.Pending.TryRemove(id, out tcs))
                    try { tcs.TrySetResult(t); } catch { }
            }
            catch { }
        }

        private static void FailSsePending(McpConn conn, string message)
        {
            try
            {
                foreach (var kv in conn.Pending)
                {
                    TaskCompletionSource<string> tcs;
                    if (conn.Pending.TryRemove(kv.Key, out tcs))
                        try { tcs.TrySetException(new McpException(message)); } catch { }
                }
            }
            catch { }
        }

        private static void SseReaderLoop(object state)
        {
            var rs = state as SseReaderState;
            if (rs == null || rs.Conn == null) return;
            var conn = rs.Conn;
            string name = rs.Name;
            try
            {
                using (var reader = new StreamReader(rs.Stream, Encoding.UTF8, true))
                {
                    string evt = null;
                    var data = new StringBuilder();
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        bool cancel = false;
                        try { cancel = rs.Cts.IsCancellationRequested; } catch { cancel = true; }
                        if (cancel) break;
                        if (line.StartsWith(":")) continue;
                        if (line.Length == 0)
                        {
                            string payload = data.Length > 0 ? data.ToString() : null;
                            string ev = evt;
                            evt = null;
                            data.Length = 0;
                            if (payload != null) DispatchSseFrame(name, conn, rs, ev, payload);
                            continue;
                        }
                        if (line.StartsWith("event:"))
                        {
                            string e = line.Substring(6).Trim();
                            evt = e;
                            continue;
                        }
                        if (line.StartsWith("data:"))
                        {
                            string d = line.Substring(5);
                            if (d.StartsWith(" ")) d = d.Substring(1);
                            if (data.Length > 0) data.Append('\n');
                            if (data.Length < 4 * 1024 * 1024) data.Append(d);
                        }
                    }
                }
            }
            catch { }
            bool expected = true;
            try { expected = rs.Cts.IsCancellationRequested; } catch { expected = true; }
            if (!expected)
            {
                AppendServerLog(name, "[forge] MCP SSE stream closed");
                MarkHttpFailed(conn);
                FailSsePending(conn, "MCP server '" + name + "' SSE stream closed — retry the call.");
            }
            try
            {
                lock (Gate)
                {
                    McpConn cur;
                    if (Conns.TryGetValue(ResolveName(name), out cur) && object.ReferenceEquals(cur, conn))
                        if (object.ReferenceEquals(conn.SseThread, Thread.CurrentThread)) conn.SseThread = null;
                }
            }
            catch { }
            try { if (rs.Response != null) rs.Response.Dispose(); } catch { }
            try { if (rs.Client != null) rs.Client.Dispose(); } catch { }
        }

        private static async Task<string> SseConnectAsync(string name, int timeoutMs, CancellationToken ct)
        {
            McpConn conn;
            string url;
            Dictionary<string, string> hdrs;
            lock (Gate)
            {
                conn = GetConnLocked(name);
                if (!string.IsNullOrEmpty(conn.SsePostUrl) && !conn.HttpFailed && conn.SseThread != null && conn.SseThread.IsAlive)
                    return conn.SsePostUrl;
                try { if (conn.SseCts != null) conn.SseCts.Cancel(); } catch { }
                conn.SseCts = null;
                conn.SseThread = null;
                try { if (conn.SseResponse != null) conn.SseResponse.Dispose(); } catch { }
                conn.SseResponse = null;
                try { if (conn.SseClient != null) conn.SseClient.Dispose(); } catch { }
                conn.SseClient = null;
                conn.SsePostUrl = null;
                url = conn.Url;
                hdrs = conn.Headers;
            }
            if (string.IsNullOrWhiteSpace(url)) throw new McpException("MCP server '" + name + "' has no URL configured — add an SSE URL in Settings → MCP servers, then retry.");
            string oauthBearer = null;
            try { oauthBearer = await GetOAuthBearerAsync(name, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch { oauthBearer = null; }
            if (timeoutMs <= 0) timeoutMs = 60000;
            var client = new HttpClient();
            try { client.Timeout = Timeout.InfiniteTimeSpan; } catch { }
            bool handedOff = false;
            HttpResponseMessage resp = null;
            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                    bool hasAuthHeader = false;
                    if (hdrs != null)
                        foreach (var kv in hdrs)
                        {
                            if (string.IsNullOrEmpty(kv.Key) || kv.Value == null) continue;
                            req.Headers.TryAddWithoutValidation(kv.Key, ExpandHeaderValue(kv.Value));
                            if (string.Equals(kv.Key, "Authorization", StringComparison.OrdinalIgnoreCase)) hasAuthHeader = true;
                        }
                    if (!hasAuthHeader && !string.IsNullOrEmpty(oauthBearer)) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + oauthBearer);
                    try { resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw new OperationCanceledException(ct); }
                    catch (Exception ex) { throw new McpException("MCP server '" + name + "' unavailable (" + ex.Message + ") — check its SSE URL and retry."); }
                }
                if (!resp.IsSuccessStatusCode)
                {
                    string why = resp.ReasonPhrase ?? "";
                    int code = (int)resp.StatusCode;
                    try { resp.Dispose(); } catch { }
                    try { client.Dispose(); } catch { }
                    MarkHttpFailed(conn);
                    if (code == 401 && ServerHasOAuth(name))
                    {
                        MarkOAuthNeedsAuth(name);
                        throw new McpException("MCP server '" + name + "' requires OAuth sign-in (HTTP 401 for the SSE stream) — run OAuth sign-in for this server, then retry. [needs_auth]");
                    }
                    throw new McpException("MCP server '" + name + "' returned HTTP " + code + (string.IsNullOrEmpty(why) ? "" : " " + why) + " for the SSE stream — check the SSE URL/headers and the server logs, then retry.");
                }
                Stream stream = null;
                try { stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false); }
                catch (Exception ex)
                {
                    try { resp.Dispose(); } catch { }
                    try { client.Dispose(); } catch { }
                    MarkHttpFailed(conn);
                    throw new McpException("MCP server '" + name + "' unavailable (" + ex.Message + ") — check its SSE URL and retry.");
                }
                var rs = new SseReaderState();
                rs.Name = name;
                rs.Conn = conn;
                rs.Client = client;
                rs.Response = resp;
                rs.Stream = stream;
                rs.Cts = new CancellationTokenSource();
                rs.EndpointTcs = new TaskCompletionSource<string>();
                lock (Gate)
                {
                    McpConn cur = GetConnLocked(name);
                    if (!object.ReferenceEquals(cur, conn))
                    {
                        try { resp.Dispose(); } catch { }
                        try { client.Dispose(); } catch { }
                        throw new McpException("MCP server '" + name + "' was replaced during connect — retry the call.");
                    }
                    conn.SseCts = rs.Cts;
                    conn.SseResponse = resp;
                    conn.SseClient = client;
                    conn.SseThread = new Thread(SseReaderLoop) { IsBackground = true, Name = "forge-mcp-sse" };
                    conn.SseThread.Start(rs);
                }
                handedOff = true;
                object winner;
                using (var timeoutCts = new CancellationTokenSource())
                {
                    var timeoutTask = Task.Delay(timeoutMs, timeoutCts.Token);
                    if (ct.CanBeCanceled)
                        winner = await Task.WhenAny(rs.EndpointTcs.Task, timeoutTask, Task.Delay(-1, ct)).ConfigureAwait(false);
                    else
                        winner = await Task.WhenAny(rs.EndpointTcs.Task, timeoutTask).ConfigureAwait(false);
                    try { timeoutCts.Cancel(); } catch { }
                }
                if (ct.IsCancellationRequested)
                {
                    try { rs.Cts.Cancel(); } catch { }
                    throw new OperationCanceledException(ct);
                }
                if (winner == rs.EndpointTcs.Task)
                    return await rs.EndpointTcs.Task.ConfigureAwait(false);
                try { rs.Cts.Cancel(); } catch { }
                MarkHttpFailed(conn);
                throw new McpException("MCP server '" + name + "' did not send an SSE endpoint after " + (timeoutMs / 1000) + "s — check its SSE URL/logs, then retry.");
            }
            catch (OperationCanceledException) { throw; }
            catch (McpException) { throw; }
            catch (Exception ex)
            {
                MarkHttpFailed(conn);
                throw new McpException("MCP server '" + name + "' unavailable (" + ex.Message + ") — check its SSE URL and retry.");
            }
            finally
            {
                if (!handedOff)
                {
                    try { if (resp != null) resp.Dispose(); } catch { }
                    try { client.Dispose(); } catch { }
                }
            }
        }

        private static async Task<JObject> SseRpcAsync(string name, string method, JObject p, int timeoutMs, CancellationToken ct)
        {
            if (timeoutMs <= 0) timeoutMs = 60000;
            int start = Environment.TickCount;
            await SseConnectAsync(name, timeoutMs, ct).ConfigureAwait(false);
            int id = Interlocked.Increment(ref NextId);
            var msg = new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
            if (p != null) msg["params"] = p;
            var tcs = new TaskCompletionSource<string>();
            McpConn conn;
            lock (Gate) { conn = GetConnLocked(name); conn.Pending[id] = tcs; }
            string body = msg.ToString(Newtonsoft.Json.Formatting.None);
            JObject resp = null;
            bool posted = false;
            try { resp = await HttpPostAsync(name, method, body, timeoutMs, ct).ConfigureAwait(false); posted = true; }
            catch { posted = false; }
            if (posted && resp != null)
            {
                bool mine = false;
                try
                {
                    var rid = resp["id"];
                    if (rid != null && rid.Type != JTokenType.Null)
                    {
                        string got = rid.Type == JTokenType.String ? (string)rid : rid.ToString(Newtonsoft.Json.Formatting.None);
                        mine = string.Equals(got.Trim('"'), id.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
                    }
                    else mine = resp["result"] != null || resp["error"] != null;
                }
                catch { mine = false; }
                if (mine)
                {
                    TaskCompletionSource<string> dropped;
                    conn.Pending.TryRemove(id, out dropped);
                    var err = resp["error"];
                    if (err != null) throw RpcError(name, method, err);
                    return resp["result"] as JObject;
                }
            }
            int elapsed = unchecked(Environment.TickCount - start);
            int rest = timeoutMs - elapsed;
            if (rest < 1000) rest = 1000;
            object winner;
            using (var timeoutCts = new CancellationTokenSource())
            {
                var timeoutTask = Task.Delay(rest, timeoutCts.Token);
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
                ObserveFault(tcs.Task);
                throw new OperationCanceledException(ct);
            }
            if (winner != tcs.Task)
            {
                TaskCompletionSource<string> dropped;
                wconn.Pending.TryRemove(id, out dropped);
                try { tcs.TrySetException(new McpException("MCP server '" + name + "' timed out on `" + method + "`.")); } catch { }
                ObserveFault(tcs.Task);
                throw new McpException("MCP server '" + name + "' timed out on `" + method + "` after " + (timeoutMs / 1000) + "s — check its SSE URL/logs, then retry.");
            }
            string line = await tcs.Task.ConfigureAwait(false);
            JObject parsed;
            try { parsed = JObject.Parse(line); }
            catch { throw new McpException("MCP server '" + name + "' returned non-JSON text for `" + method + "` (" + (line != null ? line.Length : 0) + " chars): " + Trunc(line, 200) + " — check the server logs and retry."); }
            var streamErr = parsed["error"];
            if (streamErr != null) throw RpcError(name, method, streamErr);
            return parsed["result"] as JObject;
        }

        private static async Task SseNotifyAsync(string name, string method, JObject p)
        {
            try
            {
                await SseConnectAsync(name, 60000, CancellationToken.None).ConfigureAwait(false);
                var msg = new JObject { ["jsonrpc"] = "2.0", ["method"] = method };
                if (p != null) msg["params"] = p;
                await HttpPostAsync(name, method, msg.ToString(Newtonsoft.Json.Formatting.None), 60000, CancellationToken.None).ConfigureAwait(false);
            }
            catch { }
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
                try { servers = LoadEffectiveServers(); } catch { servers = null; }
                if (servers == null || servers.Count == 0) return out_;
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var s in servers)
                {
                    try
                    {
                        if (s == null || !s.Enabled) continue;
                        if (ct.IsCancellationRequested) break;
                        int budget = CatalogPerServerTimeoutMs;
                        try
                        {
                            int configured = s.TimeoutMs >= MinConfiguredTimeoutMs ? s.TimeoutMs : int.MaxValue;
                            budget = Math.Min(budget, Math.Min(configured, s.EffectiveStartTimeoutMs));
                            if (budget < 5000) budget = 5000;
                        }
                        catch { budget = CatalogPerServerTimeoutMs; }
                        JArray tools = null;
                        using (var perServer = CancellationTokenSource.CreateLinkedTokenSource(ct))
                        {
                            try { perServer.CancelAfter(budget); } catch { }
                            try { tools = await ListToolsAsync(s.Name, perServer.Token).ConfigureAwait(false); }
                            catch (OperationCanceledException)
                            {
                                if (ct.IsCancellationRequested) break;
                                continue;
                            }
                            catch { continue; }
                        }
                        if (tools == null) continue;
                        foreach (var t in tools)
                        {
                            try
                            {
                                var o = t as JObject;
                                string n = o != null ? (string)o["name"] : (string)t;
                                if (string.IsNullOrWhiteSpace(n)) continue;
                                n = n.Trim();
                                string qualified = (s.Name ?? "default").Trim() + "_" + n;
                                if (!seen.Add(qualified)) continue;
                                if (o != null)
                                {
                                    var clone = (JObject)o.DeepClone();
                                    clone["name"] = qualified;
                                    out_.Add(clone);
                                }
                                else out_.Add(new JValue(qualified));
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

        public static void RefreshResourceCache(string serverName = null)
        {
            try
            {
                string target = ResolveName(serverName);
                lock (Gate)
                {
                    McpConn c;
                    if (Conns.TryGetValue(target, out c) && c != null)
                    {
                        c.CachedResources = null;
                        c.CachedResourceTemplates = null;
                    }
                }
            }
            catch { }
        }

        public static Task<JArray> ListResourcesAsync(CancellationToken ct = default(CancellationToken), string serverName = null)
        {
            return ListResourcesOnAsync(ResolveName(serverName), ct);
        }

        public static Task<JArray> ListResourcesAsync(string serverName, CancellationToken ct)
        {
            return ListResourcesOnAsync(ResolveName(serverName), ct);
        }

        private static async Task<JArray> ListResourcesOnAsync(string name, CancellationToken ct)
        {
            await EnsureStartedOnAsync(name, 0, ct).ConfigureAwait(false);
            McpConn conn;
            lock (Gate) { conn = GetConnLocked(name); if (conn.CachedResources != null) return conn.CachedResources; }
            JObject res = await RpcOnServerAsync(name, "resources/list", new JObject(), 60000, ct).ConfigureAwait(false);
            var resources = res != null ? (res["resources"] as JArray ?? res["items"] as JArray) : null;
            if (resources == null || resources.Count == 0) return new JArray();
            lock (Gate) { conn.CachedResources = resources; }
            return resources;
        }

        public static Task<JArray> ListResourceTemplatesAsync(CancellationToken ct = default(CancellationToken), string serverName = null)
        {
            return ListResourceTemplatesOnAsync(ResolveName(serverName), ct);
        }

        public static Task<JArray> ListResourceTemplatesAsync(string serverName, CancellationToken ct)
        {
            return ListResourceTemplatesOnAsync(ResolveName(serverName), ct);
        }

        private static async Task<JArray> ListResourceTemplatesOnAsync(string name, CancellationToken ct)
        {
            await EnsureStartedOnAsync(name, 0, ct).ConfigureAwait(false);
            McpConn conn;
            lock (Gate) { conn = GetConnLocked(name); if (conn.CachedResourceTemplates != null) return conn.CachedResourceTemplates; }
            JObject res = await RpcOnServerAsync(name, "resources/templates/list", new JObject(), 60000, ct).ConfigureAwait(false);
            var templates = res != null ? (res["resourceTemplates"] as JArray ?? res["templates"] as JArray) : null;
            if (templates == null || templates.Count == 0) return new JArray();
            lock (Gate) { conn.CachedResourceTemplates = templates; }
            return templates;
        }

        public static Task<string> ReadResourceAsync(string uri, CancellationToken ct = default(CancellationToken), string serverName = null)
        {
            return ReadResourceOnAsync(ResolveName(serverName), uri, ct);
        }

        public static Task<string> ReadResourceAsync(string serverName, string uri, CancellationToken ct)
        {
            return ReadResourceOnAsync(ResolveName(serverName), uri, ct);
        }

        private static async Task<string> ReadResourceOnAsync(string name, string uri, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(uri)) throw new McpException("A resource uri is required — pass the uri returned by list_mcp_resources.");
            await EnsureStartedOnAsync(name, 0, ct).ConfigureAwait(false);
            int tmo;
            try { var cfg = FindServer(name); tmo = cfg != null && cfg.TimeoutMs >= MinConfiguredTimeoutMs ? cfg.TimeoutMs : DefaultStdioTimeoutMs; }
            catch { tmo = DefaultStdioTimeoutMs; }
            var p = new JObject { ["uri"] = uri };
            JObject res = await RpcOnServerAsync(name, "resources/read", p, tmo, ct).ConfigureAwait(false);
            var contents = res != null ? res["contents"] as JArray : null;
            var sb = new StringBuilder();
            if (contents != null)
                foreach (var c in contents)
                {
                    try
                    {
                        var o = c as JObject;
                        if (o == null) continue;
                        string text = (string)o["text"];
                        if (string.IsNullOrEmpty(text)) text = (string)o["blob"];
                        if (string.IsNullOrEmpty(text)) text = (string)o["uri"] ?? o.ToString(Newtonsoft.Json.Formatting.None);
                        if (sb.Length > 0) sb.Append('\n');
                        sb.Append(text);
                    }
                    catch { }
                }
            string s = sb.ToString();
            return s.Length > 8000 ? s.Substring(0, 8000) + "…(truncated)" : s;
        }

        public static Task<JArray> ListPromptsAsync(CancellationToken ct = default(CancellationToken), string serverName = null)
        {
            return ListPromptsOnAsync(ResolveName(serverName), ct);
        }

        public static Task<JArray> ListPromptsAsync(string serverName, CancellationToken ct)
        {
            return ListPromptsOnAsync(ResolveName(serverName), ct);
        }

        private static async Task<JArray> ListPromptsOnAsync(string name, CancellationToken ct)
        {
            await EnsureStartedOnAsync(name, 0, ct).ConfigureAwait(false);
            McpConn conn;
            lock (Gate) { conn = GetConnLocked(name); if (conn.CachedPrompts != null) return conn.CachedPrompts; }
            JObject res = await RpcOnServerAsync(name, "prompts/list", new JObject(), 60000, ct).ConfigureAwait(false);
            var prompts = res != null ? res["prompts"] as JArray : null;
            if (prompts == null || prompts.Count == 0) return new JArray();
            var capped = new JArray();
            int used = 2;
            foreach (var pr in prompts)
            {
                string js;
                try { js = pr != null ? pr.ToString(Newtonsoft.Json.Formatting.None) : ""; } catch { continue; }
                if (string.IsNullOrEmpty(js)) continue;
                if (used + js.Length > 8000) break;
                try { capped.Add(pr.DeepClone()); } catch { continue; }
                used += js.Length + 1;
            }
            lock (Gate) { conn.CachedPrompts = capped; }
            return capped;
        }

        public static Task<string> GetPromptAsync(string promptName, JObject arguments, CancellationToken ct = default(CancellationToken), string serverName = null)
        {
            return GetPromptOnAsync(ResolveName(serverName), promptName, arguments, ct);
        }

        public static Task<string> GetPromptAsync(string serverName, string promptName, JObject arguments, CancellationToken ct)
        {
            return GetPromptOnAsync(ResolveName(serverName), promptName, arguments, ct);
        }

        private static void AppendPromptContent(StringBuilder sb, JToken content)
        {
            if (sb == null || content == null) return;
            try
            {
                if (content.Type == JTokenType.String)
                {
                    string t = (string)content;
                    if (!string.IsNullOrEmpty(t)) { if (sb.Length > 0) sb.Append('\n'); sb.Append(t); }
                    return;
                }
                var arr = content as JArray;
                if (arr != null)
                {
                    foreach (var c in arr) AppendPromptContent(sb, c);
                    return;
                }
                var o = content as JObject;
                if (o != null)
                {
                    string type = null;
                    try { type = (string)o["type"]; } catch { type = null; }
                    string text = null;
                    try { text = (string)o["text"]; } catch { text = null; }
                    if (!string.IsNullOrEmpty(text) && (string.IsNullOrEmpty(type) || string.Equals(type, "text", StringComparison.OrdinalIgnoreCase)))
                    { if (sb.Length > 0) sb.Append('\n'); sb.Append(text); }
                }
            }
            catch { }
        }

        private static async Task<string> GetPromptOnAsync(string name, string promptName, JObject arguments, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(promptName)) throw new McpException("A prompt name is required — pass a name returned by list_mcp_prompts.");
            await EnsureStartedOnAsync(name, 0, ct).ConfigureAwait(false);
            int tmo;
            try { var cfg = FindServer(name); tmo = cfg != null && cfg.TimeoutMs >= MinConfiguredTimeoutMs ? cfg.TimeoutMs : DefaultStdioTimeoutMs; }
            catch { tmo = DefaultStdioTimeoutMs; }
            var p = new JObject { ["name"] = promptName.Trim() };
            if (arguments != null) p["arguments"] = arguments;
            JObject res = await RpcOnServerAsync(name, "prompts/get", p, tmo, ct).ConfigureAwait(false);
            var messages = res != null ? res["messages"] as JArray : null;
            var sb = new StringBuilder();
            if (messages != null)
                foreach (var m in messages)
                {
                    try
                    {
                        var o = m as JObject;
                        if (o == null) continue;
                        AppendPromptContent(sb, o["content"]);
                    }
                    catch { }
                }
            string s = sb.ToString();
            return s.Length > 8000 ? s.Substring(0, 8000) + "…(truncated)" : s;
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
            bool qualified = false;
            string rawTool = tool;
            try
            {
                string srv, tl;
                if (TrySplitQualified(tool, ConfiguredServerNames(), out srv, out tl))
                {
                    qualified = true;
                    name = srv;
                    rawTool = tl;
                }
            }
            catch { }
            if (qualified)
            {
                var target = FindServer(name);
                if (target == null || !target.Enabled)
                    throw new McpException("MCP server " + name + " is disabled/unavailable for tool " + rawTool + " (disabled).");
                try { await EnsureStartedOnAsync(name, 0, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { throw new McpException("MCP server " + name + " is disabled/unavailable for tool " + rawTool + " (unavailable)."); }
            }
            if (timeoutMs <= 0)
            {
                try
                {
                    var scfg = FindServer(name);
                    timeoutMs = scfg != null ? scfg.TimeoutMs : DefaultStdioTimeoutMs;
                }
                catch { timeoutMs = DefaultStdioTimeoutMs; }
                if (timeoutMs < MinConfiguredTimeoutMs) timeoutMs = MinConfiguredTimeoutMs;
            }
            var p = new JObject { ["name"] = rawTool };
            p["arguments"] = args ?? new JObject();
            JObject res;
            try { res = await RpcOnServerAsync(name, "tools/call", p, timeoutMs, ct).ConfigureAwait(false); }
            catch (McpException mex) when (IsDeadServer(mex) && !qualified && !ct.IsCancellationRequested)
            {
                // Server died mid-run (process killed / SW closed took it down): restart once and
                // retry so the next tool call heals instead of failing every later step until the cap.
                await EnsureStartedOnAsync(name, 60000, ct).ConfigureAwait(false);
                res = await RpcOnServerAsync(name, "tools/call", p, timeoutMs, ct).ConfigureAwait(false);
            }
            // A null result is a broken server reply: fail loudly, not Ok=true with empty text.
            if (res == null) throw new McpException("MCP server '" + name + "' returned an empty result for tool '" + rawTool + "' — check the server logs and retry the step.");
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

        private static readonly object OAuthGate = new object();
        private static readonly HashSet<string> OAuthNeedsAuth = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private const int OAuthRedirectPortDefault = 53821;
        private const int OAuthCallbackTimeoutMs = 300000;
        private const int OAuthExpirySkewSeconds = 60;

        private class OAuthToken
        {
            public string AccessToken = "";
            public string RefreshToken = "";
            public DateTime ExpiresAtUtc = DateTime.MinValue;
            public string Scope = "";
        }

        private static string FirstStr(JObject o, params string[] keys)
        {
            try
            {
                if (o == null || keys == null) return "";
                foreach (var k in keys)
                {
                    try
                    {
                        string v = (string)o[k];
                        if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
                    }
                    catch { }
                }
            }
            catch { }
            return "";
        }

        private static McpServerConfig.McpOAuthConfig ParseOAuthConfig(JToken tok)
        {
            try
            {
                var o = tok as JObject;
                if (o == null) return null;
                var c = new McpServerConfig.McpOAuthConfig();
                c.ClientId = FirstStr(o, "clientId", "client_id", "ClientId");
                c.AuthorizationUrl = FirstStr(o, "authorizationUrl", "authorization_url", "AuthorizationUrl");
                c.TokenUrl = FirstStr(o, "tokenUrl", "token_url", "TokenUrl");
                var scopes = new List<string>();
                var ja = o["scope"] as JArray;
                if (ja != null)
                {
                    foreach (var t in ja)
                    {
                        string s = null;
                        try { s = (string)t; } catch { s = null; }
                        if (!string.IsNullOrWhiteSpace(s)) scopes.Add(s.Trim());
                    }
                }
                else
                {
                    string one = FirstStr(o, "scope", "Scope");
                    if (!string.IsNullOrWhiteSpace(one))
                        foreach (var s in one.Split(new[] { ' ', ',', ';', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                            if (!string.IsNullOrWhiteSpace(s)) scopes.Add(s.Trim());
                }
                c.Scope = scopes.ToArray();
                int port = 0;
                try
                {
                    JToken pt = o["redirectPort"];
                    if (pt == null) pt = o["redirect_port"];
                    if (pt == null) pt = o["RedirectPort"];
                    if (pt != null) port = pt.Value<int>();
                }
                catch { port = 0; }
                c.RedirectPort = port > 0 && port <= 65535 ? port : OAuthRedirectPortDefault;
                if (c.ClientId.Length == 0 && c.AuthorizationUrl.Length == 0 && c.TokenUrl.Length == 0) return null;
                return c;
            }
            catch { return null; }
        }

        private static string OAuthTokenDir()
        {
            try { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Forge", "mcp-oauth"); }
            catch { return ""; }
        }

        private static string OAuthTokenPath(string serverName)
        {
            try
            {
                string key = ((serverName ?? "").Trim().ToLowerInvariant());
                byte[] hash;
                using (var sha = System.Security.Cryptography.SHA256.Create())
                    hash = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
                var sb = new StringBuilder();
                for (int i = 0; i < 16; i++) sb.Append(hash[i].ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
                string dir = OAuthTokenDir();
                if (string.IsNullOrEmpty(dir)) return "";
                return Path.Combine(dir, sb.ToString() + ".token.json");
            }
            catch { return ""; }
        }

        private static bool ServerHasOAuth(string name)
        {
            try
            {
                var cfg = FindServer(name);
                return cfg != null && cfg.HasOAuth;
            }
            catch { return false; }
        }

        private static void MarkOAuthNeedsAuth(string name)
        {
            try
            {
                string n = (name ?? "").Trim();
                if (n.Length == 0) return;
                lock (OAuthGate) { OAuthNeedsAuth.Add(n); }
            }
            catch { }
        }

        private static void ClearOAuthNeedsAuth(string name)
        {
            try
            {
                string n = (name ?? "").Trim();
                if (n.Length == 0) return;
                lock (OAuthGate) { OAuthNeedsAuth.Remove(n); }
            }
            catch { }
        }

        private static bool HasOAuthNeedsAuthFlag(string name)
        {
            try
            {
                string n = (name ?? "").Trim();
                if (n.Length == 0) return false;
                lock (OAuthGate) { return OAuthNeedsAuth.Contains(n); }
            }
            catch { return false; }
        }

        private static bool LooksLikeNeedsAuth(string text)
        {
            try
            {
                if (string.IsNullOrEmpty(text)) return false;
                return text.IndexOf("needs_auth", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        private static McpException RpcError(string name, string method, JToken err)
        {
            string em = null;
            try { em = (string)err["message"]; } catch { }
            if (string.IsNullOrEmpty(em)) { try { em = err.ToString(); } catch { em = "unknown error"; } }
            string marked = OAuthAuthMarker(name, em);
            if (marked != null) return new McpException(marked);
            return new McpException("MCP server '" + name + "' rejected `" + method + "`: " + Trunc(em, 500) + " — check the tool name/arguments and the server logs, then retry.");
        }

        private static string OAuthAuthMarker(string name, string detail)
        {
            try
            {
                if (string.IsNullOrEmpty(detail)) return null;
                if (detail.IndexOf("needs_auth", StringComparison.OrdinalIgnoreCase) < 0) return null;
                if (!ServerHasOAuth(name)) return null;
                MarkOAuthNeedsAuth(name);
                return "MCP server '" + name + "' requires OAuth sign-in (" + Trunc(detail, 200) + ") — run OAuth sign-in for this server, then retry. [needs_auth]";
            }
            catch { return null; }
        }

        private static OAuthToken LoadOAuthToken(string serverName)
        {
            try
            {
                string path = OAuthTokenPath(serverName);
                if (string.IsNullOrEmpty(path)) return null;
                string b64;
                lock (OAuthGate)
                {
                    if (!File.Exists(path)) return null;
                    b64 = File.ReadAllText(path);
                }
                if (string.IsNullOrWhiteSpace(b64)) return null;
                byte[] prot = Convert.FromBase64String(b64.Trim());
                byte[] raw = System.Security.Cryptography.ProtectedData.Unprotect(prot, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
                if (raw == null || raw.Length == 0) return null;
                JObject o = JObject.Parse(Encoding.UTF8.GetString(raw));
                if (o == null) return null;
                var t = new OAuthToken();
                t.AccessToken = (string)o["accessToken"] ?? "";
                t.RefreshToken = (string)o["refreshToken"] ?? "";
                string exp = (string)o["expiresAtUtc"];
                DateTime dt;
                if (!string.IsNullOrWhiteSpace(exp) && DateTime.TryParse(exp, null, System.Globalization.DateTimeStyles.RoundtripKind, out dt))
                    t.ExpiresAtUtc = dt.ToUniversalTime();
                else
                    t.ExpiresAtUtc = DateTime.MinValue;
                t.Scope = (string)o["scope"] ?? "";
                if (t.AccessToken.Length == 0) return null;
                return t;
            }
            catch { return null; }
        }

        private static bool SaveOAuthToken(string serverName, OAuthToken token)
        {
            try
            {
                if (token == null || string.IsNullOrEmpty(token.AccessToken)) return false;
                string path = OAuthTokenPath(serverName);
                if (string.IsNullOrEmpty(path)) return false;
                var o = new JObject
                {
                    ["accessToken"] = token.AccessToken,
                    ["refreshToken"] = token.RefreshToken ?? "",
                    ["expiresAtUtc"] = token.ExpiresAtUtc.ToUniversalTime().ToString("o"),
                    ["scope"] = token.Scope ?? ""
                };
                byte[] prot = System.Security.Cryptography.ProtectedData.Protect(Encoding.UTF8.GetBytes(o.ToString(Newtonsoft.Json.Formatting.None)), null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
                string b64 = Convert.ToBase64String(prot);
                lock (OAuthGate)
                {
                    try
                    {
                        try { Directory.CreateDirectory(Path.GetDirectoryName(path)); } catch { }
                        File.WriteAllText(path, b64);
                    }
                    catch { return false; }
                }
                try { RestrictOAuthTokenFile(path); } catch { }
                return true;
            }
            catch { return false; }
        }

        private static void RestrictOAuthTokenFile(string path)
        {
            try
            {
                try { File.SetAttributes(path, FileAttributes.Hidden); } catch { }
                try
                {
                    var acl = File.GetAccessControl(path);
                    acl.SetAccessRuleProtection(true, false);
                    var user = System.Security.Principal.WindowsIdentity.GetCurrent().User;
                    if (user != null)
                        acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(user, System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
                    File.SetAccessControl(path, acl);
                }
                catch { }
            }
            catch { }
        }

        public static void ClearOAuthToken(string serverName)
        {
            try
            {
                string name = ResolveName(serverName);
                ClearOAuthNeedsAuth(name);
                string path = OAuthTokenPath(name);
                if (string.IsNullOrEmpty(path)) return;
                lock (OAuthGate)
                {
                    try { if (File.Exists(path)) File.Delete(path); } catch { }
                }
            }
            catch { }
        }

        private static byte[] OAuthRandomBytes(int count)
        {
            try
            {
                if (count <= 0) count = 32;
                if (count > 256) count = 256;
                byte[] buf = new byte[count];
                using (var rng = new System.Security.Cryptography.RNGCryptoServiceProvider())
                    rng.GetBytes(buf);
                return buf;
            }
            catch
            {
                byte[] fallback = new byte[count > 0 ? count : 32];
                try
                {
                    var rnd = new Random();
                    rnd.NextBytes(fallback);
                }
                catch { }
                return fallback;
            }
        }

        private static string OAuthBase64Url(byte[] data)
        {
            try
            {
                if (data == null || data.Length == 0) return "";
                return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            }
            catch { return ""; }
        }

        private static async Task<string> GetOAuthBearerAsync(string serverName, CancellationToken ct)
        {
            string name;
            try { name = ResolveName(serverName); } catch { return null; }
            McpServerConfig cfg;
            try
            {
                cfg = FindServer(name);
                if (cfg == null || !cfg.HasOAuth) return null;
            }
            catch { return null; }
            OAuthToken tok;
            try { tok = LoadOAuthToken(name); } catch { return null; }
            if (tok == null || string.IsNullOrEmpty(tok.AccessToken)) return null;
            bool fresh;
            try { fresh = (tok.ExpiresAtUtc - DateTime.UtcNow).TotalSeconds > OAuthExpirySkewSeconds; }
            catch { fresh = false; }
            if (fresh) return tok.AccessToken;
            if (string.IsNullOrEmpty(tok.RefreshToken)) return tok.AccessToken;
            string renewed = null;
            try { renewed = await RefreshOAuthTokenAsync(cfg, tok, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch { renewed = null; }
            return !string.IsNullOrEmpty(renewed) ? renewed : tok.AccessToken;
        }

        private static async Task<string> RefreshOAuthTokenAsync(McpServerConfig cfg, OAuthToken current, CancellationToken ct)
        {
            try
            {
                if (cfg == null || cfg.OAuth == null || string.IsNullOrWhiteSpace(cfg.OAuth.TokenUrl)) return null;
                if (current == null || string.IsNullOrEmpty(current.RefreshToken)) return null;
                var fields = new List<KeyValuePair<string, string>>();
                fields.Add(new KeyValuePair<string, string>("grant_type", "refresh_token"));
                fields.Add(new KeyValuePair<string, string>("refresh_token", current.RefreshToken));
                fields.Add(new KeyValuePair<string, string>("client_id", cfg.OAuth.ClientId ?? ""));
                if (cfg.OAuth.Scope != null && cfg.OAuth.Scope.Length > 0)
                    fields.Add(new KeyValuePair<string, string>("scope", string.Join(" ", cfg.OAuth.Scope)));
                OAuthToken next = await ExchangeOAuthTokenAsync(cfg.OAuth.TokenUrl, fields, ct).ConfigureAwait(false);
                if (next == null || string.IsNullOrEmpty(next.AccessToken)) return null;
                if (string.IsNullOrEmpty(next.RefreshToken)) next.RefreshToken = current.RefreshToken;
                if (string.IsNullOrEmpty(next.Scope)) next.Scope = current.Scope;
                try { SaveOAuthToken(cfg.Name, next); } catch { }
                ClearOAuthNeedsAuth(cfg.Name);
                return next.AccessToken;
            }
            catch { return null; }
        }

        private static async Task<OAuthToken> ExchangeOAuthTokenAsync(string tokenUrl, List<KeyValuePair<string, string>> fields, CancellationToken ct)
        {
            using (var http = new HttpClient())
            {
                try { http.Timeout = TimeSpan.FromSeconds(30); } catch { }
                CancellationToken useCt = CancellationToken.None;
                try { if (ct.CanBeCanceled) ct.ThrowIfCancellationRequested(); useCt = ct; } catch (OperationCanceledException) { throw; } catch { useCt = CancellationToken.None; }
                using (var content = new FormUrlEncodedContent(fields))
                using (var resp = await http.PostAsync(tokenUrl, content, useCt).ConfigureAwait(false))
                {
                    string text = "";
                    try { text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false); } catch { text = ""; }
                    if (!resp.IsSuccessStatusCode)
                    {
                        string why = "";
                        try { why = ((int)resp.StatusCode) + " " + (resp.ReasonPhrase ?? ""); } catch { why = ""; }
                        throw new McpException("OAuth token endpoint returned " + why.Trim() + " — check the MCP server OAuth configuration and retry.");
                    }
                    JObject o = null;
                    try { o = JObject.Parse(text ?? ""); } catch { o = null; }
                    if (o == null) throw new McpException("OAuth token endpoint returned an unreadable response — check the MCP server OAuth configuration and retry.");
                    string access = (string)o["access_token"];
                    if (string.IsNullOrEmpty(access)) access = (string)o["accessToken"];
                    if (string.IsNullOrEmpty(access))
                    {
                        string desc = (string)o["error_description"];
                        if (string.IsNullOrEmpty(desc)) desc = (string)o["error"];
                        throw new McpException("OAuth token request was rejected" + (string.IsNullOrEmpty(desc) ? "" : ": " + Trunc(desc, 200)) + " — run OAuth sign-in again, then retry.");
                    }
                    var t = new OAuthToken();
                    t.AccessToken = access;
                    t.RefreshToken = (string)o["refresh_token"];
                    if (string.IsNullOrEmpty(t.RefreshToken)) t.RefreshToken = (string)o["refreshToken"];
                    if (t.RefreshToken == null) t.RefreshToken = "";
                    int expiresIn = 0;
                    try
                    {
                        JToken e = o["expires_in"];
                        if (e == null) e = o["expiresIn"];
                        if (e != null) expiresIn = e.Value<int>();
                    }
                    catch { expiresIn = 0; }
                    if (expiresIn <= 0) expiresIn = 3600;
                    try { t.ExpiresAtUtc = DateTime.UtcNow.AddSeconds(expiresIn); } catch { t.ExpiresAtUtc = DateTime.UtcNow.AddHours(1); }
                    t.Scope = (string)o["scope"];
                    if (t.Scope == null) t.Scope = "";
                    return t;
                }
            }
        }

        private static async Task<HttpListenerContext> WaitOAuthCallbackAsync(HttpListener listener, int timeoutMs, CancellationToken ct)
        {
            Task<HttpListenerContext> get = null;
            try
            {
                if (listener == null || !listener.IsListening) return null;
                try { get = listener.GetContextAsync(); }
                catch { return null; }
                using (var timeoutCts = new CancellationTokenSource())
                {
                    var timeoutTask = Task.Delay(timeoutMs <= 0 ? OAuthCallbackTimeoutMs : timeoutMs, timeoutCts.Token);
                    Task winner;
                    if (ct.CanBeCanceled)
                        winner = await Task.WhenAny(get, timeoutTask, Task.Delay(-1, ct)).ConfigureAwait(false);
                    else
                        winner = await Task.WhenAny(get, timeoutTask).ConfigureAwait(false);
                    try { timeoutCts.Cancel(); } catch { }
                    if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                    if (winner == get)
                    {
                        try { return await get.ConfigureAwait(false); }
                        catch { return null; }
                    }
                    try { ObserveFault(get); } catch { }
                    return null;
                }
            }
            catch (OperationCanceledException) { try { if (get != null) ObserveFault(get); } catch { } throw; }
            catch { try { if (get != null) ObserveFault(get); } catch { } return null; }
        }

        private static void ReplyOAuthCallback(HttpListenerContext ctx, bool ok)
        {
            try
            {
                if (ctx == null) return;
                string title = ok ? "Sign-in complete" : "Sign-in failed";
                string msg = ok ? "OAuth sign-in completed. Return to the application." : "OAuth sign-in failed. Return to the application and retry.";
                string html = "<html><head><title>" + title + "</title></head><body><h3>" + title + "</h3><p>" + msg + "</p></body></html>";
                byte[] bytes = Encoding.UTF8.GetBytes(html);
                try
                {
                    ctx.Response.StatusCode = ok ? 200 : 400;
                    ctx.Response.ContentType = "text/html; charset=utf-8";
                    ctx.Response.ContentLength64 = bytes.Length;
                    ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                }
                catch { }
                try { ctx.Response.OutputStream.Close(); } catch { }
                try { ctx.Response.Close(); } catch { }
            }
            catch { }
        }

        public static async Task<bool> StartOAuthAsync(string serverName, CancellationToken ct)
        {
            string name = ResolveName(serverName);
            var cfg = FindServer(name);
            if (cfg == null || !cfg.HasOAuth)
                throw new McpException("MCP server '" + name + "' has no OAuth configuration — add an `oauth` block (clientId, authorizationUrl, tokenUrl) to its mcp.json entry, then retry.");
            string verifier = OAuthBase64Url(OAuthRandomBytes(32));
            string challenge;
            using (var sha = System.Security.Cryptography.SHA256.Create())
                challenge = OAuthBase64Url(sha.ComputeHash(Encoding.UTF8.GetBytes(verifier)));
            string state = OAuthBase64Url(OAuthRandomBytes(32));
            int port = cfg.OAuth.RedirectPort > 0 && cfg.OAuth.RedirectPort <= 65535 ? cfg.OAuth.RedirectPort : OAuthRedirectPortDefault;
            string redirectUri = "http://127.0.0.1:" + port + "/callback";
            var listener = new HttpListener();
            try { listener.Prefixes.Add("http://127.0.0.1:" + port + "/"); } catch { }
            try { listener.Start(); }
            catch (Exception ex) { throw new McpException("OAuth sign-in for server '" + name + "' could not listen on 127.0.0.1:" + port + " (" + ex.Message + ") — free the port or set a different oauth redirectPort, then retry."); }
            try
            {
                string scope = cfg.OAuth.Scope != null ? string.Join(" ", cfg.OAuth.Scope) : "";
                string sep = cfg.OAuth.AuthorizationUrl.Contains("?") ? "&" : "?";
                string authUrl = cfg.OAuth.AuthorizationUrl.Trim() + sep
                    + "response_type=code"
                    + "&client_id=" + Uri.EscapeDataString(cfg.OAuth.ClientId ?? "")
                    + "&redirect_uri=" + Uri.EscapeDataString(redirectUri)
                    + "&code_challenge=" + Uri.EscapeDataString(challenge)
                    + "&code_challenge_method=S256"
                    + (string.IsNullOrWhiteSpace(scope) ? "" : "&scope=" + Uri.EscapeDataString(scope))
                    + "&state=" + Uri.EscapeDataString(state);
                try { Process.Start(authUrl); }
                catch (Exception ex) { throw new McpException("OAuth sign-in for server '" + name + "' could not open the system browser (" + ex.Message + ") — open the authorization URL manually from the server OAuth settings."); }
                HttpListenerContext ctx = await WaitOAuthCallbackAsync(listener, OAuthCallbackTimeoutMs, ct).ConfigureAwait(false);
                if (ctx == null)
                    throw new McpException("OAuth sign-in for server '" + name + "' timed out after 5 minutes — run OAuth sign-in again and complete the browser approval.");
                string code = null;
                string gotState = null;
                try
                {
                    var q = ctx.Request.QueryString;
                    if (q != null)
                    {
                        string qerr = q["error"];
                        if (!string.IsNullOrEmpty(qerr))
                        {
                            string desc = q["error_description"];
                            try { ReplyOAuthCallback(ctx, false); } catch { }
                            throw new McpException("OAuth sign-in for server '" + name + "' was rejected by the provider" + (string.IsNullOrEmpty(desc) ? "" : ": " + Trunc(desc, 200)) + " — retry the sign-in.");
                        }
                        code = q["code"];
                        gotState = q["state"];
                    }
                }
                catch (McpException) { throw; }
                catch { code = null; }
                bool ok = !string.IsNullOrEmpty(code) && string.Equals(gotState, state, StringComparison.Ordinal);
                try { ReplyOAuthCallback(ctx, ok); } catch { }
                if (string.IsNullOrEmpty(code))
                    throw new McpException("OAuth sign-in for server '" + name + "' returned no authorization code — retry the sign-in and approve the browser request.");
                if (!string.Equals(gotState, state, StringComparison.Ordinal))
                    throw new McpException("OAuth sign-in for server '" + name + "' returned a mismatched state — retry the sign-in (a second browser tab may have interfered).");
                var fields = new List<KeyValuePair<string, string>>();
                fields.Add(new KeyValuePair<string, string>("grant_type", "authorization_code"));
                fields.Add(new KeyValuePair<string, string>("code", code));
                fields.Add(new KeyValuePair<string, string>("redirect_uri", redirectUri));
                fields.Add(new KeyValuePair<string, string>("client_id", cfg.OAuth.ClientId ?? ""));
                fields.Add(new KeyValuePair<string, string>("code_verifier", verifier));
                OAuthToken token = await ExchangeOAuthTokenAsync(cfg.OAuth.TokenUrl, fields, ct).ConfigureAwait(false);
                if (token == null || string.IsNullOrEmpty(token.AccessToken))
                    throw new McpException("OAuth sign-in for server '" + name + "' did not return an access token — check the OAuth configuration and retry.");
                if (string.IsNullOrEmpty(token.Scope)) token.Scope = scope ?? "";
                if (!SaveOAuthToken(name, token))
                    throw new McpException("OAuth sign-in for server '" + name + "' succeeded but the token could not be stored — check write access to %APPDATA%\\Forge\\mcp-oauth and retry.");
                ClearOAuthNeedsAuth(name);
                return true;
            }
            finally { try { listener.Stop(); } catch { } try { listener.Close(); } catch { } }
        }

        public static string OAuthStatus(string serverName)
        {
            try
            {
                string name = ResolveName(serverName);
                var cfg = FindServer(name);
                if (cfg == null || !cfg.HasOAuth) return "none";
                if (HasOAuthNeedsAuthFlag(name)) return "needs_auth";
                OAuthToken tok = null;
                try { tok = LoadOAuthToken(name); } catch { tok = null; }
                if (tok == null || string.IsNullOrEmpty(tok.AccessToken)) return "configured";
                bool live = false;
                try { live = (tok.ExpiresAtUtc - DateTime.UtcNow).TotalSeconds > OAuthExpirySkewSeconds; } catch { live = false; }
                return live ? "authenticated" : "expired";
            }
            catch { return "none"; }
        }

        public static bool IsOAuthRequired(string serverName)
        {
            try
            {
                string name = ResolveName(serverName);
                var cfg = FindServer(name);
                if (cfg == null || !cfg.HasOAuth) return false;
                if (HasOAuthNeedsAuthFlag(name)) return true;
                OAuthToken tok = null;
                try { tok = LoadOAuthToken(name); } catch { tok = null; }
                if (tok == null || string.IsNullOrEmpty(tok.AccessToken)) return true;
                try { return (tok.ExpiresAtUtc - DateTime.UtcNow).TotalSeconds <= OAuthExpirySkewSeconds && string.IsNullOrEmpty(tok.RefreshToken); }
                catch { return true; }
            }
            catch { return false; }
        }
    }
}
