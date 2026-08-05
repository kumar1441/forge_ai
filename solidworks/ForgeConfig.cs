using System;
using System.IO;
using Newtonsoft.Json.Linq;

namespace Forge.SolidWorks
{
    /// <summary>
    /// Resolution order for each setting: environment variable, then %APPDATA%\Forge\config.json, else empty.
    /// No secrets live in the source tree.
    /// </summary>
    public static class ForgeConfig
    {
        private const string ApiKeyEnv = "FORGE_MCP_API_KEY";
        private const string ModelPrimaryEnv = "FORGE_MODEL_PRIMARY";
        private const string ModelLightEnv = "FORGE_MODEL_LIGHT";

        private static JObject _json;
        private static JObject Json
        {
            get
            {
                if (_json != null) return _json;
                JObject j = null;
                try
                {
                    string p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Forge", "config.json");
                    if (File.Exists(p)) j = JObject.Parse(File.ReadAllText(p));
                }
                catch { }
                _json = j ?? new JObject();
                return _json;
            }
        }

        public static string ApiKey => Env(ApiKeyEnv) ?? JsonValue("apiKey") ?? "";
        public static string ModelPrimary => Env(ModelPrimaryEnv) ?? JsonValue("modelPrimary") ?? "";
        public static string ModelLight => Env(ModelLightEnv) ?? JsonValue("modelLight") ?? "";

        private static string Env(string name)
        {
            try { var v = Environment.GetEnvironmentVariable(name); return string.IsNullOrWhiteSpace(v) ? null : v; }
            catch { return null; }
        }

        private static string JsonValue(string key)
        {
            try { var v = Json[key]; return v == null ? null : v.ToString(); }
            catch { return null; }
        }
    }
}
