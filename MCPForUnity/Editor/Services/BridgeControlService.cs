
using System;
using System.Threading.Tasks;
using MCPForUnity.Editor.Constants;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Services.Transport;
using UnityEditor;

namespace MCPForUnity.Editor.Services
{
    /// <summary>
    /// Bridges the editor UI to the HTTP transport (WebSocket push).
    /// </summary>
    public class BridgeControlService : IBridgeControlService
    {
        private readonly TransportManager _transportManager;

        public BridgeControlService()
        {
            _transportManager = MCPServiceLocator.TransportManager;
        }

        private static TransportMode ResolvePreferredMode()
        {
            return TransportMode.Http;
        }

        private static BridgeVerificationResult BuildVerificationResult(TransportState state, TransportMode mode, bool pingSucceeded, string messageOverride = null, bool? handshakeOverride = null)
        {
            bool handshakeValid = handshakeOverride ?? true;
            string transportLabel = string.IsNullOrWhiteSpace(state.TransportName)
                ? mode.ToString().ToLowerInvariant()
                : state.TransportName;
            string detailSuffix = string.IsNullOrWhiteSpace(state.Details) ? string.Empty : $" [{state.Details}]";
            string message = messageOverride
                ?? state.Error
                ?? (state.IsConnected ? $"Transport '{transportLabel}' connected{detailSuffix}" : $"Transport '{transportLabel}' disconnected{detailSuffix}");

            return new BridgeVerificationResult
            {
                Success = pingSucceeded && handshakeValid,
                HandshakeValid = handshakeValid,
                PingSucceeded = pingSucceeded,
                Message = message
            };
        }

        public bool IsRunning
        {
            get
            {
                return _transportManager.IsRunning(TransportMode.Http);
            }
        }
        public bool IsAutoConnectMode => EditorPrefs.GetBool(EditorPrefKeys.AutoConnectHttp, true);
        public TransportMode? ActiveMode => TransportMode.Http;

        public async Task<bool> StartAsync()
        {
            try
            {
                bool started = await _transportManager.StartAsync(TransportMode.Http);
                if (!started)
                {
                    McpLog.Warn("Failed to start HTTP transport");
                }
                return started;
            }
            catch (Exception ex)
            {
                McpLog.Error($"Error starting HTTP transport: {ex.Message}");
                return false;
            }
        }

        public async Task StopAsync()
        {
            try
            {
                await _transportManager.StopAsync(TransportMode.Http);
            }
            catch (Exception ex)
            {
                McpLog.Warn($"Error stopping HTTP transport: {ex.Message}");
            }
        }

        public async Task<BridgeVerificationResult> VerifyAsync()
        {
            bool pingSucceeded = await _transportManager.VerifyAsync(TransportMode.Http);
            var state = _transportManager.GetState(TransportMode.Http);
            return BuildVerificationResult(state, TransportMode.Http, pingSucceeded);
        }

    }
}
