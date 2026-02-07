using System;
using System.Globalization;
using MCPForUnity.Runtime.Helpers;
using Newtonsoft.Json.Linq;

namespace MCPForUnity.Runtime.Tools
{
    /// <summary>
    /// Parameter extraction wrapper for runtime MCP tools.
    /// Supports both snake_case and camelCase parameter names.
    /// </summary>
    public class RuntimeToolParams
    {
        private readonly JObject _params;

        public RuntimeToolParams(JObject @params)
        {
            _params = @params ?? throw new ArgumentNullException(nameof(@params));
        }

        public string RequireString(string key, string errorMessage = null)
        {
            var value = GetString(key);
            if (string.IsNullOrEmpty(value))
                throw new System.ArgumentException(errorMessage ?? $"Required parameter '{key}' is missing or empty");
            return value;
        }

        public string Get(string key, string defaultValue = null)
        {
            return GetString(key) ?? defaultValue;
        }

        public int? GetInt(string key, int? defaultValue = null)
        {
            var token = GetToken(key);
            if (token == null || token.Type == JTokenType.Null) return defaultValue;
            if (token.Type == JTokenType.Integer) return token.Value<int>();
            var s = token.ToString().Trim();
            if (s.Length == 0) return defaultValue;
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : defaultValue;
        }

        public float? GetFloat(string key, float? defaultValue = null)
        {
            var token = GetToken(key);
            if (token == null || token.Type == JTokenType.Null) return defaultValue;
            if (token.Type == JTokenType.Float || token.Type == JTokenType.Integer) return token.Value<float>();
            var s = token.ToString().Trim();
            if (s.Length == 0) return defaultValue;
            return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : defaultValue;
        }

        public bool GetBool(string key, bool defaultValue = false)
        {
            var token = GetToken(key);
            if (token == null || token.Type == JTokenType.Null) return defaultValue;
            if (token.Type == JTokenType.Boolean) return token.Value<bool>();
            var s = token.ToString().Trim().ToLowerInvariant();
            if (s.Length == 0) return defaultValue;
            if (bool.TryParse(s, out var b)) return b;
            if (s == "1" || s == "yes" || s == "on") return true;
            if (s == "0" || s == "no" || s == "off") return false;
            return defaultValue;
        }

        public bool Has(string key)
        {
            return GetToken(key) != null;
        }

        public JToken GetRaw(string key)
        {
            return GetToken(key);
        }

        public string[] GetStringArray(string key)
        {
            var token = GetToken(key);
            if (token == null || token.Type == JTokenType.Null) return null;

            if (token is JArray arr)
            {
                var result = new string[arr.Count];
                for (int i = 0; i < arr.Count; i++)
                    result[i] = arr[i]?.ToString();
                return result;
            }

            // Single string -> array of one
            if (token.Type == JTokenType.String)
                return new[] { token.Value<string>() };

            return null;
        }

        public JObject GetJObject(string key)
        {
            var token = GetToken(key);
            return token as JObject;
        }

        public JObject Raw => _params;

        private JToken GetToken(string key)
        {
            var token = _params[key];
            if (token != null) return token;

            var snakeKey = RuntimeStringCaseUtility.ToSnakeCase(key);
            if (snakeKey != key)
            {
                token = _params[snakeKey];
                if (token != null) return token;
            }

            var camelKey = RuntimeStringCaseUtility.ToCamelCase(key);
            if (camelKey != key)
            {
                token = _params[camelKey];
            }

            return token;
        }

        private string GetString(string key)
        {
            var value = _params[key]?.ToString();
            if (value != null) return value;

            var snakeKey = RuntimeStringCaseUtility.ToSnakeCase(key);
            if (snakeKey != key)
            {
                value = _params[snakeKey]?.ToString();
                if (value != null) return value;
            }

            var camelKey = RuntimeStringCaseUtility.ToCamelCase(key);
            if (camelKey != key)
            {
                value = _params[camelKey]?.ToString();
            }

            return value;
        }
    }
}
