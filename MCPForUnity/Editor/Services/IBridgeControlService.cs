using System.Threading.Tasks;
using MCPForUnity.Editor.Services.Transport;

namespace MCPForUnity.Editor.Services
{
    /// <summary>
    /// Service for controlling the Unity CLI bridge connection
    /// </summary>
    public interface IBridgeControlService
    {
        /// <summary>
        /// Gets whether the bridge is currently running
        /// </summary>
        bool IsRunning { get; }

        /// <summary>
        /// Gets whether the bridge is in auto-connect mode
        /// </summary>
        bool IsAutoConnectMode { get; }

        /// <summary>
        /// Gets the currently active transport mode, if any
        /// </summary>
        TransportMode? ActiveMode { get; }

        /// <summary>
        /// Starts the MCP for Unity Bridge asynchronously
        /// </summary>
        /// <returns>True if the bridge started successfully</returns>
        Task<bool> StartAsync();

        /// <summary>
        /// Stops the MCP for Unity Bridge asynchronously
        /// </summary>
        Task StopAsync();

        /// <summary>
        /// Verifies the connection asynchronously (HTTP transport)
        /// </summary>
        /// <returns>Verification result with detailed status</returns>
        Task<BridgeVerificationResult> VerifyAsync();

    }

    /// <summary>
    /// Result of a bridge verification attempt
    /// </summary>
    public class BridgeVerificationResult
    {
        /// <summary>
        /// Whether the verification was successful
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// Human-readable message about the verification result
        /// </summary>
        public string Message { get; set; }

        /// <summary>
        /// Whether the handshake was valid (FRAMING=1 protocol)
        /// </summary>
        public bool HandshakeValid { get; set; }

        /// <summary>
        /// Whether the ping/pong exchange succeeded
        /// </summary>
        public bool PingSucceeded { get; set; }
    }
}
