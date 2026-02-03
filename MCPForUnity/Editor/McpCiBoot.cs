using System;
using MCPForUnity.Editor.Constants;
using MCPForUnity.Editor.Services;
using MCPForUnity.Editor.Services.Transport;
using UnityEditor;

namespace MCPForUnity.Editor
{
    public static class McpCiBoot
    {
        public static void StartHttpForCi()
        {
            try
            {
                EditorPrefs.SetString(EditorPrefKeys.HttpTransportScope, "local");
                EditorPrefs.SetBool(EditorPrefKeys.AutoConnectHttp, true);
            }
            catch { /* ignore */ }

            _ = MCPServiceLocator.TransportManager.StartAsync(TransportMode.Http);
        }

        [Obsolete("Legacy name; starts HTTP CLI bridge for CI.")]
        public static void StartStdioForCi()
        {
            StartHttpForCi();
        }
    }
}
