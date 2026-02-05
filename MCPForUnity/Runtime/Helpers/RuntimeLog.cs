using UnityEngine;

namespace MCPForUnity.Runtime.Helpers
{
    internal static class RuntimeLog
    {
        private const string InfoPrefix = "<b><color=#2EA3FF>MCP-RUNTIME</color></b>:";
        private const string DebugPrefix = "<b><color=#6AA84F>MCP-RUNTIME</color></b>:";
        private const string WarnPrefix = "<b><color=#cc7a00>MCP-RUNTIME</color></b>:";
        private const string ErrorPrefix = "<b><color=#cc3333>MCP-RUNTIME</color></b>:";

        private static volatile bool _debugEnabled;

        public static bool DebugEnabled
        {
            get => _debugEnabled;
            set => _debugEnabled = value;
        }

        public static void Debug(string message)
        {
            if (!_debugEnabled) return;
            UnityEngine.Debug.Log($"{DebugPrefix} {message}");
        }

        public static void Info(string message, bool always = true)
        {
            if (!always && !_debugEnabled) return;
            UnityEngine.Debug.Log($"{InfoPrefix} {message}");
        }

        public static void Warn(string message)
        {
            UnityEngine.Debug.LogWarning($"{WarnPrefix} {message}");
        }

        public static void Error(string message)
        {
            UnityEngine.Debug.LogError($"{ErrorPrefix} {message}");
        }
    }
}
