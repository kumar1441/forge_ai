using System;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Forge.SolidWorks
{
    /// <summary>
    /// SafeJsonExtractor — fail-closed parsing helpers for free-tier LLM output.
    /// LLMs (Grok, OpenRouter free tiers) wrap JSON in markdown fences, pad it with chatter,
    /// emit trailing commas, unquoted keys and control characters. These helpers recover the
    /// first balanced JSON block from raw text and only ever return null when nothing is recoverable.
    /// </summary>
    public static class SafeJsonExtractor
    {
        private static readonly char[] Whitespace = { ' ', '\t', '\r', '\n', '\uFEFF' };

        /// <summary>
        /// Recover the first balanced JSON block (object or array) from raw LLM text.
        /// Strips leading/trailing whitespace and markdown code fences, locates the first balanced
        /// top-level '{...}' or '[...]', then parses it. If the top level is an array it is wrapped
        /// under the key "_rawArray" so the caller always gets a JObject; when the array contains an
        /// object, that first object is returned instead. Returns null (with error set) only when no
        /// JSON can be recovered at all.
        /// </summary>
        public static JObject ExtractResponseEnvelope(string rawResponse, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(rawResponse)) { error = "empty response"; return null; }

            string cleaned = StripFences(rawResponse);
            string block = FindFirstBalancedJson(cleaned);
            if (block == null) { error = "no balanced JSON block found"; return null; }

            try
            {
                var token = JToken.Parse(block);
                if (token is JObject obj) return obj;

                if (token is JArray arr)
                {
                    foreach (var item in arr)
                        if (item is JObject first) return first;
                    return new JObject { ["_rawArray"] = arr };
                }

                error = "recovered token is neither object nor array";
                return null;
            }
            catch (Exception ex)
            {
                error = "parse failed: " + ex.Message;
                return null;
            }
        }

        /// <summary>
        /// Recover a tool-arguments JSON object from raw LLM text (the "arguments" field of a tool call).
        /// Strips markdown fences and chatter, finds the first balanced '{...}', then parses. On parse
        /// failure it applies safe repairs in order: strip BOM/control characters outside strings, convert
        /// unquoted keys to quoted keys, and remove trailing commas before '}' or ']'. After a successful
        /// parse it guarantees the "intent" key exists as a string (defaulting to "" when missing or null).
        /// Returns null with error set only when no usable object can be recovered.
        /// </summary>
        public static JObject ExtractArguments(string rawArguments, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(rawArguments)) { error = "empty arguments"; return null; }

            string cleaned = StripFences(rawArguments);
            string block = FindFirstBalancedJson(cleaned);
            if (block == null) { error = "no balanced object found"; return null; }

            JObject result = ParseObjectOrRepair(block, out error);
            if (result == null) return null;

            var intent = result["intent"];
            if (intent == null || intent.Type != JTokenType.String)
                result["intent"] = "";
            else if (intent.Type == JTokenType.Null)
                result["intent"] = "";

            return result;
        }

        /// <summary>
        /// Safely read the "intent" string from a parsed arguments object.
        /// Returns defaultIntent when the key is missing, null, empty or not a string.
        /// </summary>
        public static string ExtractIntent(JObject arguments, string defaultIntent = "")
        {
            if (arguments == null) return defaultIntent;
            var token = arguments["intent"];
            if (token == null || token.Type != JTokenType.String) return defaultIntent;
            string value = (string)token;
            return string.IsNullOrEmpty(value) ? defaultIntent : value;
        }

        private static string StripFences(string raw)
        {
            if (raw == null) return string.Empty;
            string s = raw.Trim(Whitespace);
            int idx = s.IndexOf("```", StringComparison.Ordinal);
            if (idx < 0) return s;

            s = s.Substring(idx + 3);
            int eol = s.IndexOfAny(new[] { '\r', '\n' });
            if (eol >= 0) s = s.Substring(eol);
            int close = s.LastIndexOf("```", StringComparison.Ordinal);
            if (close >= 0) s = s.Substring(0, close);
            return s.Trim(Whitespace);
        }

        private static string FindFirstBalancedJson(string text)
        {
            int start = -1, depth = 0;
            bool inString = false, escaped = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') inString = false;
                    continue;
                }

                if (c == '"') { inString = true; continue; }

                if (c == '{' || c == '[')
                {
                    if (depth == 0) start = i;
                    depth++;
                }
                else if (c == '}' || c == ']')
                {
                    depth--;
                    if (depth == 0 && start >= 0)
                        return text.Substring(start, i - start + 1);
                }
            }
            return null;
        }

        private static JObject ParseObjectOrRepair(string block, out string error)
        {
            error = null;
            string candidate = block;

            try
            {
                var token = JToken.Parse(candidate);
                if (token is JObject obj) return obj;
                error = "block is not an object";
                return null;
            }
            catch
            {
                // fall through to repairs
            }

            candidate = StripControlCharacters(candidate);
            try
            {
                var token = JToken.Parse(candidate);
                if (token is JObject obj) return obj;
            }
            catch { }

            candidate = QuoteUnquotedKeys(candidate);
            candidate = RemoveTrailingCommas(candidate);
            try
            {
                var token = JToken.Parse(candidate);
                if (token is JObject obj) return obj;
            }
            catch (Exception ex)
            {
                error = "parse failed after repair: " + ex.Message;
                return null;
            }

            error = "block is not an object";
            return null;
        }

        private static string StripControlCharacters(string s)
        {
            var sb = new StringBuilder(s.Length);
            bool inString = false, escaped = false;
            foreach (char c in s)
            {
                if (inString)
                {
                    sb.Append(c);
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') { inString = true; sb.Append(c); continue; }
                if (c < 0x20) continue;
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static string QuoteUnquotedKeys(string s)
        {
            var sb = new StringBuilder(s.Length);
            bool inString = false, escaped = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (inString)
                {
                    sb.Append(c);
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') { inString = true; sb.Append(c); continue; }

                if ((c == '{' || c == ',') && i + 1 < s.Length && (char.IsLetter(s[i + 1]) || s[i + 1] == '_'))
                {
                    sb.Append(c);
                    int k = i + 1;
                    while (k < s.Length && (char.IsLetterOrDigit(s[k]) || s[k] == '_' || s[k] == '-'))
                        k++;
                    if (k < s.Length && s[k] == ':')
                    {
                        sb.Append('"').Append(s, i + 1, k - i - 1).Append('"');
                        i = k - 1;
                        continue;
                    }
                }
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static string RemoveTrailingCommas(string s)
        {
            var sb = new StringBuilder(s.Length);
            bool inString = false, escaped = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (inString)
                {
                    sb.Append(c);
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') { inString = true; sb.Append(c); continue; }

                if (c == ',')
                {
                    int j = i + 1;
                    while (j < s.Length && (s[j] == ' ' || s[j] == '\t' || s[j] == '\r' || s[j] == '\n'))
                        j++;
                    if (j < s.Length && (s[j] == '}' || s[j] == ']'))
                    {
                        i = j - 1;
                        continue;
                    }
                }
                sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
