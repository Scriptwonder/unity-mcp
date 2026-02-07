using System;
using System.Threading.Tasks;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Services.Transport;
using MCPForUnity.Runtime.Transport;
using UnityEditor;

namespace MCPForUnity.Editor.Services
{
    /// <summary>
    /// Handles editor transport disconnect/reconnect around Play Mode transitions.
    /// When entering Play Mode, the editor WebSocket disconnects so the RuntimeBridge
    /// can register as the active session. When exiting Play Mode, the editor reconnects.
    /// </summary>
    [InitializeOnLoad]
    internal static class PlayModeSessionSwapHandler
    {
        private static bool _wasHttpRunning;
        private static bool _wasStdioRunning;

        static PlayModeSessionSwapHandler()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            switch (state)
            {
                case PlayModeStateChange.ExitingEditMode:
                    WriteServerUrlForRuntime();
                    break;
                case PlayModeStateChange.EnteredPlayMode:
                    DisconnectEditorTransports();
                    break;
                case PlayModeStateChange.EnteredEditMode:
                    ReconnectEditorTransports();
                    break;
            }
        }

        private static void WriteServerUrlForRuntime()
        {
            try
            {
                string url = HttpEndpointUtility.GetBaseUrl();
                if (!string.IsNullOrEmpty(url))
                {
                    RuntimeBridge.WriteSharedServerUrl(url);
                }
            }
            catch (Exception ex)
            {
                McpLog.Warn($"[PlayMode] Failed to write server URL for runtime: {ex.Message}");
            }
        }

        private static void DisconnectEditorTransports()
        {
            try
            {
                var transport = MCPServiceLocator.TransportManager;
                _wasHttpRunning = transport.IsRunning(TransportMode.Http);
                _wasStdioRunning = transport.IsRunning(TransportMode.Stdio);

                if (_wasHttpRunning)
                {
                    McpLog.Info("[PlayMode] Disconnecting HTTP transport for runtime session swap");
                    var stopTask = transport.StopAsync(TransportMode.Http);
                    stopTask.ContinueWith(t =>
                    {
                        if (t.IsFaulted)
                            McpLog.Warn($"[PlayMode] Error stopping HTTP transport: {t.Exception?.GetBaseException().Message}");
                    }, TaskScheduler.Default);
                }

                if (_wasStdioRunning)
                {
                    McpLog.Info("[PlayMode] Disconnecting stdio transport for runtime session swap");
                    var stopTask = transport.StopAsync(TransportMode.Stdio);
                    stopTask.ContinueWith(t =>
                    {
                        if (t.IsFaulted)
                            McpLog.Warn($"[PlayMode] Error stopping stdio transport: {t.Exception?.GetBaseException().Message}");
                    }, TaskScheduler.Default);
                }
            }
            catch (Exception ex)
            {
                McpLog.Warn($"[PlayMode] Error during transport disconnect: {ex.Message}");
            }
        }

        private static void ReconnectEditorTransports()
        {
            // Use delayCall to ensure we reconnect after Unity is fully back in Edit Mode
            EditorApplication.delayCall += async () =>
            {
                try
                {
                    var transport = MCPServiceLocator.TransportManager;

                    if (_wasHttpRunning)
                    {
                        McpLog.Info("[PlayMode] Reconnecting HTTP transport after Play Mode");
                        bool started = await transport.StartAsync(TransportMode.Http);
                        if (!started)
                            McpLog.Warn("[PlayMode] Failed to reconnect HTTP transport");
                    }

                    if (_wasStdioRunning)
                    {
                        McpLog.Info("[PlayMode] Reconnecting stdio transport after Play Mode");
                        bool started = await transport.StartAsync(TransportMode.Stdio);
                        if (!started)
                            McpLog.Warn("[PlayMode] Failed to reconnect stdio transport");
                    }
                }
                catch (Exception ex)
                {
                    McpLog.Error($"[PlayMode] Error reconnecting transports: {ex.Message}");
                }
            };
        }
    }
}
