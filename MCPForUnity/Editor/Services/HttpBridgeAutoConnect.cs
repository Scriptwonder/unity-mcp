using System;
using System.Threading;
using System.Threading.Tasks;
using MCPForUnity.Editor.Constants;
using MCPForUnity.Editor.Services.Transport;
using MCPForUnity.Editor.Windows;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Services
{
    /// <summary>
    /// Auto-connects Unity's HTTP/WebSocket bridge to a running local server
    /// without requiring the MCP for Unity window.
    /// </summary>
    [InitializeOnLoad]
    internal static class HttpBridgeAutoConnect
    {
        private static readonly double[] ReconnectScheduleSeconds = { 0, 2, 5, 10, 30, 60 };
        private static int _attemptIndex;
        private static double _nextAttemptAt;
        private static int _attemptInFlight;

        static HttpBridgeAutoConnect()
        {
            try { EditorApplication.update -= OnEditorUpdate; } catch { }
            EditorApplication.update += OnEditorUpdate;
        }

        private static bool IsEnabled()
        {
            if (Application.isBatchMode)
            {
                return false;
            }

            return EditorPrefs.GetBool(EditorPrefKeys.AutoConnectHttp, true);
        }

        private static bool IsLocalScope()
        {
            string scope = EditorPrefs.GetString(EditorPrefKeys.HttpTransportScope, "local");
            return string.IsNullOrEmpty(scope) ||
                   string.Equals(scope, "local", StringComparison.OrdinalIgnoreCase);
        }

        private static void OnEditorUpdate()
        {
            if (!IsEnabled())
            {
                ResetState();
                return;
            }

            var transport = MCPServiceLocator.TransportManager;
            if (transport.IsRunning(TransportMode.Http))
            {
                ResetState();
                return;
            }

            double now = EditorApplication.timeSinceStartup;
            if (_nextAttemptAt > now)
            {
                return;
            }

            if (IsLocalScope())
            {
                try
                {
                    if (!MCPServiceLocator.Server.IsLocalHttpServerReachable())
                    {
                        ScheduleNextAttempt(now);
                        return;
                    }
                }
                catch
                {
                    ScheduleNextAttempt(now);
                    return;
                }
            }

            if (Interlocked.CompareExchange(ref _attemptInFlight, 1, 0) != 0)
            {
                return;
            }

            ScheduleNextAttempt(now);

            var startTask = transport.StartAsync(TransportMode.Http);
            startTask.ContinueWith(t =>
            {
                Interlocked.Exchange(ref _attemptInFlight, 0);
                if (t.IsFaulted)
                {
                    return;
                }
                if (!t.Result)
                {
                    return;
                }

                ResetState();
                MCPForUnityEditorWindow.RequestHealthVerification();
            }, TaskScheduler.Default);
        }

        private static void ScheduleNextAttempt(double now)
        {
            int idx = Math.Min(_attemptIndex, ReconnectScheduleSeconds.Length - 1);
            double delay = ReconnectScheduleSeconds[idx];
            _nextAttemptAt = now + delay;
            if (_attemptIndex < ReconnectScheduleSeconds.Length - 1)
            {
                _attemptIndex++;
            }
        }

        private static void ResetState()
        {
            _attemptIndex = 0;
            _nextAttemptAt = 0;
            Interlocked.Exchange(ref _attemptInFlight, 0);
        }
    }
}
