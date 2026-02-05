using System;
using MCPForUnity.Runtime.Helpers;
using UnityEngine;

namespace MCPForUnity.Runtime.Transport
{
    /// <summary>
    /// Entry point for the runtime MCP bridge. Add this MonoBehaviour to your scene
    /// to enable MCP tool execution at runtime (Play Mode or standalone builds).
    ///
    /// Connects to the MCP Python server via WebSocket and receives/executes tool commands.
    /// Persists across scene loads via DontDestroyOnLoad.
    /// </summary>
    public class RuntimeBridge : MonoBehaviour
    {
        private static RuntimeBridge _instance;

        [Header("Server Connection")]
        [Tooltip("The MCP server URL to connect to (e.g., http://localhost:8090)")]
        [SerializeField] private string _serverUrl = "http://localhost:8090";

        [Tooltip("Automatically connect when the component starts")]
        [SerializeField] private bool _autoConnect = true;

        [Header("Debug")]
        [Tooltip("Enable verbose debug logging")]
        [SerializeField] private bool _debugLogging;

        private RuntimeWebSocketClient _wsClient;
        private RuntimeCommandDispatcher _dispatcher;

        /// <summary>
        /// Singleton instance of the RuntimeBridge.
        /// </summary>
        public static RuntimeBridge Instance => _instance;

        /// <summary>
        /// Whether the bridge is currently connected to the MCP server.
        /// </summary>
        public bool IsConnected => _wsClient?.IsConnected ?? false;

        /// <summary>
        /// The current session ID assigned by the server.
        /// </summary>
        public string SessionId => _wsClient?.SessionId;

        /// <summary>
        /// The configured server URL.
        /// </summary>
        public string ServerUrl
        {
            get => _serverUrl;
            set => _serverUrl = value;
        }

        void Awake()
        {
            if (_instance != null && _instance != this)
            {
                RuntimeLog.Warn("Duplicate RuntimeBridge detected. Destroying this instance.");
                Destroy(gameObject);
                return;
            }

            _instance = this;
            DontDestroyOnLoad(gameObject);

            RuntimeLog.DebugEnabled = _debugLogging;
        }

        async void Start()
        {
            _dispatcher = RuntimeCommandDispatcher.EnsureInstance();
            _wsClient = new RuntimeWebSocketClient(_dispatcher);

            if (_autoConnect)
            {
                await ConnectAsync();
            }
        }

        void OnValidate()
        {
            RuntimeLog.DebugEnabled = _debugLogging;
        }

        async void OnDestroy()
        {
            if (_instance == this)
            {
                _instance = null;
            }

            if (_wsClient != null)
            {
                try
                {
                    await _wsClient.DisconnectAsync();
                }
                catch (Exception ex)
                {
                    RuntimeLog.Warn($"Error disconnecting: {ex.Message}");
                }
                _wsClient.Dispose();
                _wsClient = null;
            }
        }

        /// <summary>
        /// Connect to the MCP server. Can be called manually if autoConnect is false.
        /// </summary>
        public async void ConnectAsync()
        {
            if (_wsClient == null)
            {
                RuntimeLog.Error("WebSocket client not initialized. Wait for Start() to complete.");
                return;
            }

            if (string.IsNullOrEmpty(_serverUrl))
            {
                RuntimeLog.Error("Server URL is not configured.");
                return;
            }

            RuntimeLog.Info($"Connecting to MCP server at {_serverUrl}...");

            bool connected = await _wsClient.ConnectAsync(_serverUrl);
            if (connected)
            {
                RuntimeLog.Info("Connected to MCP server.");
            }
            else
            {
                RuntimeLog.Error("Failed to connect to MCP server.");
            }
        }

        /// <summary>
        /// Disconnect from the MCP server.
        /// </summary>
        public async void Disconnect()
        {
            if (_wsClient != null)
            {
                await _wsClient.DisconnectAsync();
                RuntimeLog.Info("Disconnected from MCP server.");
            }
        }
    }
}
