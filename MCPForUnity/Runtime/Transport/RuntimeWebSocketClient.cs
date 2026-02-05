using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MCPForUnity.Runtime.Helpers;
using MCPForUnity.Runtime.Tools;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace MCPForUnity.Runtime.Transport
{
    /// <summary>
    /// WebSocket client for the runtime MCP bridge.
    /// Connects to the MCP Python server, registers as a runtime session,
    /// receives commands, and dispatches them via RuntimeCommandDispatcher.
    /// </summary>
    public class RuntimeWebSocketClient : IDisposable
    {
        private static readonly TimeSpan[] ReconnectSchedule =
        {
            TimeSpan.Zero,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30)
        };

        private static readonly TimeSpan DefaultKeepAliveInterval = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(30);

        private ClientWebSocket _socket;
        private CancellationTokenSource _lifecycleCts;
        private CancellationTokenSource _connectionCts;
        private Task _receiveTask;
        private Task _keepAliveTask;
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        private Uri _endpointUri;
        private string _sessionId;
        private string _projectName;
        private string _projectHash;
        private string _unityVersion;
        private TimeSpan _keepAliveInterval = DefaultKeepAliveInterval;
        private volatile bool _isConnected;
        private int _isReconnectingFlag;
        private bool _disposed;

        private RuntimeCommandDispatcher _dispatcher;

        public bool IsConnected => _isConnected;
        public string SessionId => _sessionId;

        public RuntimeWebSocketClient(RuntimeCommandDispatcher dispatcher)
        {
            _dispatcher = dispatcher;
        }

        public async Task<bool> ConnectAsync(string serverUrl)
        {
            _projectName = Application.productName;
            _projectHash = ComputeProjectHash();
            _unityVersion = Application.unityVersion;

            await DisconnectAsync();

            _endpointUri = BuildWebSocketUri(serverUrl);
            _lifecycleCts = new CancellationTokenSource();
            _sessionId = null;

            if (!await EstablishConnectionAsync(_lifecycleCts.Token))
            {
                await DisconnectAsync();
                return false;
            }

            _isConnected = true;
            return true;
        }

        public async Task DisconnectAsync()
        {
            if (_lifecycleCts == null) return;

            try { _lifecycleCts.Cancel(); } catch { }

            await StopConnectionLoopsAsync();

            if (_socket != null)
            {
                try
                {
                    if (_socket.State == WebSocketState.Open || _socket.State == WebSocketState.CloseReceived)
                    {
                        await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Shutdown", CancellationToken.None);
                    }
                }
                catch { }
                finally
                {
                    _socket.Dispose();
                    _socket = null;
                }
            }

            _isConnected = false;
            _lifecycleCts.Dispose();
            _lifecycleCts = null;
        }

        public void Dispose()
        {
            if (_disposed) return;

            try
            {
                DisconnectAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn($"[WebSocket] Dispose error: {ex.Message}");
            }

            _sendLock?.Dispose();
            _socket?.Dispose();
            _lifecycleCts?.Dispose();
            _disposed = true;
        }

        private async Task<bool> EstablishConnectionAsync(CancellationToken token)
        {
            await StopConnectionLoopsAsync();

            _connectionCts?.Dispose();
            _connectionCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var connectionToken = _connectionCts.Token;

            _socket?.Dispose();
            _socket = new ClientWebSocket();
            _socket.Options.KeepAliveInterval = _keepAliveInterval;

            try
            {
                RuntimeLog.Info($"[WebSocket] Connecting to {_endpointUri}...");
                await _socket.ConnectAsync(_endpointUri, connectionToken);
            }
            catch (Exception ex)
            {
                RuntimeLog.Error($"[WebSocket] Connection failed: {ex.Message}");
                return false;
            }

            StartBackgroundLoops(connectionToken);

            try
            {
                await SendRegisterAsync(connectionToken);
            }
            catch (Exception ex)
            {
                RuntimeLog.Error($"[WebSocket] Registration failed: {ex.Message}");
                return false;
            }

            return true;
        }

        private async Task StopConnectionLoopsAsync()
        {
            if (_connectionCts != null && !_connectionCts.IsCancellationRequested)
            {
                try { _connectionCts.Cancel(); } catch { }
            }

            if (_receiveTask != null)
            {
                try { await _receiveTask; } catch { }
                _receiveTask = null;
            }

            if (_keepAliveTask != null)
            {
                try { await _keepAliveTask; } catch { }
                _keepAliveTask = null;
            }

            if (_connectionCts != null)
            {
                _connectionCts.Dispose();
                _connectionCts = null;
            }
        }

        private void StartBackgroundLoops(CancellationToken token)
        {
            if ((_receiveTask != null && !_receiveTask.IsCompleted) ||
                (_keepAliveTask != null && !_keepAliveTask.IsCompleted))
            {
                return;
            }

            _receiveTask = Task.Run(() => ReceiveLoopAsync(token), CancellationToken.None);
            _keepAliveTask = Task.Run(() => KeepAliveLoopAsync(token), CancellationToken.None);
        }

        private async Task ReceiveLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    string message = await ReceiveMessageAsync(token);
                    if (message == null) continue;
                    await HandleMessageAsync(message, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (WebSocketException wse)
                {
                    RuntimeLog.Warn($"[WebSocket] Receive error: {wse.Message}");
                    await HandleSocketClosureAsync(wse.Message);
                    break;
                }
                catch (Exception ex)
                {
                    RuntimeLog.Warn($"[WebSocket] Unexpected receive error: {ex.Message}");
                    await HandleSocketClosureAsync(ex.Message);
                    break;
                }
            }
        }

        private async Task<string> ReceiveMessageAsync(CancellationToken token)
        {
            if (_socket == null) return null;

            byte[] rentedBuffer = System.Buffers.ArrayPool<byte>.Shared.Rent(8192);
            var buffer = new ArraySegment<byte>(rentedBuffer);
            using var ms = new MemoryStream(8192);

            try
            {
                while (!token.IsCancellationRequested)
                {
                    var result = await _socket.ReceiveAsync(buffer, token);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await HandleSocketClosureAsync(result.CloseStatusDescription ?? "Server closed");
                        return null;
                    }

                    if (result.Count > 0)
                    {
                        ms.Write(buffer.Array, buffer.Offset, result.Count);
                    }

                    if (result.EndOfMessage) break;
                }

                if (ms.Length == 0) return null;
                return Encoding.UTF8.GetString(ms.ToArray());
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(rentedBuffer);
            }
        }

        private async Task HandleMessageAsync(string message, CancellationToken token)
        {
            JObject payload;
            try
            {
                payload = JObject.Parse(message);
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn($"[WebSocket] Invalid JSON: {ex.Message}");
                return;
            }

            string messageType = payload.Value<string>("type") ?? string.Empty;

            switch (messageType)
            {
                case "welcome":
                    ApplyWelcome(payload);
                    break;
                case "registered":
                    await HandleRegisteredAsync(payload, token);
                    break;
                case "execute":
                    await HandleExecuteAsync(payload, token);
                    break;
                case "ping":
                    await SendPongAsync(token);
                    break;
            }
        }

        private void ApplyWelcome(JObject payload)
        {
            int? keepAliveSeconds = payload.Value<int?>("keepAliveInterval");
            if (keepAliveSeconds.HasValue && keepAliveSeconds.Value > 0)
            {
                _keepAliveInterval = TimeSpan.FromSeconds(keepAliveSeconds.Value);
            }
        }

        private async Task HandleRegisteredAsync(JObject payload, CancellationToken token)
        {
            string newSessionId = payload.Value<string>("session_id");
            if (!string.IsNullOrEmpty(newSessionId))
            {
                _sessionId = newSessionId;
                RuntimeLog.Info($"[WebSocket] Registered with session ID: {_sessionId}");
                await SendRegisterToolsAsync(token);
            }
        }

        private async Task SendRegisterToolsAsync(CancellationToken token)
        {
            RuntimeToolRegistry.Initialize();
            var tools = RuntimeToolRegistry.GetRegisteredTools();

            RuntimeLog.Info($"[WebSocket] Registering {tools.Count} runtime tool(s)...", false);

            var toolsArray = new JArray();
            foreach (var tool in tools)
            {
                toolsArray.Add(new JObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["structured_output"] = tool.StructuredOutput,
                    ["requires_polling"] = false,
                    ["poll_action"] = "status",
                    ["parameters"] = new JArray()
                });
            }

            var payload = new JObject
            {
                ["type"] = "register_tools",
                ["tools"] = toolsArray
            };

            await SendJsonAsync(payload, token);
            RuntimeLog.Info($"[WebSocket] Sent {tools.Count} tools registration", false);
        }

        private async Task HandleExecuteAsync(JObject payload, CancellationToken token)
        {
            string commandId = payload.Value<string>("id");
            string commandName = payload.Value<string>("name");
            JObject parameters = payload.Value<JObject>("params") ?? new JObject();
            int timeoutSeconds = payload.Value<int?>("timeout") ?? (int)DefaultCommandTimeout.TotalSeconds;

            if (string.IsNullOrEmpty(commandId) || string.IsNullOrEmpty(commandName))
            {
                RuntimeLog.Warn("[WebSocket] Invalid execute payload (missing id or name)");
                return;
            }

            var commandEnvelope = new JObject
            {
                ["type"] = commandName,
                ["params"] = parameters
            };

            string responseJson;
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));
                responseJson = await _dispatcher.ExecuteCommandJsonAsync(
                    commandEnvelope.ToString(Formatting.None), timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                responseJson = JsonConvert.SerializeObject(new
                {
                    status = "error",
                    error = $"Command '{commandName}' timed out after {timeoutSeconds}s"
                });
            }
            catch (Exception ex)
            {
                responseJson = JsonConvert.SerializeObject(new
                {
                    status = "error",
                    error = ex.Message
                });
            }

            JToken resultToken;
            try
            {
                resultToken = JToken.Parse(responseJson);
            }
            catch
            {
                resultToken = new JObject
                {
                    ["status"] = "error",
                    ["error"] = "Invalid response payload"
                };
            }

            var responsePayload = new JObject
            {
                ["type"] = "command_result",
                ["id"] = commandId,
                ["result"] = resultToken
            };

            await SendJsonAsync(responsePayload, token);
        }

        private async Task KeepAliveLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_keepAliveInterval, token);
                    if (_socket == null || _socket.State != WebSocketState.Open) break;
                    await SendPongAsync(token);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    RuntimeLog.Warn($"[WebSocket] Keep-alive failed: {ex.Message}");
                    await HandleSocketClosureAsync(ex.Message);
                    break;
                }
            }
        }

        private async Task SendRegisterAsync(CancellationToken token)
        {
            var registerPayload = new JObject
            {
                ["type"] = "register",
                ["project_name"] = _projectName,
                ["project_hash"] = _projectHash,
                ["unity_version"] = _unityVersion,
                ["session_type"] = "runtime"
            };

            await SendJsonAsync(registerPayload, token);
        }

        private Task SendPongAsync(CancellationToken token)
        {
            var payload = new JObject
            {
                ["type"] = "pong",
                ["session_id"] = _sessionId
            };
            return SendJsonAsync(payload, token);
        }

        private async Task SendJsonAsync(JObject payload, CancellationToken token)
        {
            if (_socket == null)
                throw new InvalidOperationException("WebSocket not initialized");

            string json = payload.ToString(Formatting.None);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            var buffer = new ArraySegment<byte>(bytes);

            await _sendLock.WaitAsync(token);
            try
            {
                if (_socket.State != WebSocketState.Open)
                    throw new InvalidOperationException("WebSocket not open");

                await _socket.SendAsync(buffer, WebSocketMessageType.Text, true, token);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private async Task HandleSocketClosureAsync(string reason)
        {
            if (_lifecycleCts == null || _lifecycleCts.IsCancellationRequested) return;

            if (Interlocked.CompareExchange(ref _isReconnectingFlag, 1, 0) != 0) return;

            _isConnected = false;
            RuntimeLog.Warn($"[WebSocket] Connection closed: {reason}");

            await StopConnectionLoopsAsync();

            _ = Task.Run(() => AttemptReconnectAsync(_lifecycleCts.Token), CancellationToken.None);
        }

        private async Task AttemptReconnectAsync(CancellationToken token)
        {
            try
            {
                await StopConnectionLoopsAsync();

                foreach (var delay in ReconnectSchedule)
                {
                    if (token.IsCancellationRequested) return;

                    if (delay > TimeSpan.Zero)
                    {
                        try { await Task.Delay(delay, token); }
                        catch (OperationCanceledException) { return; }
                    }

                    if (await EstablishConnectionAsync(token))
                    {
                        _isConnected = true;
                        RuntimeLog.Info("[WebSocket] Reconnected to MCP server");
                        return;
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _isReconnectingFlag, 0);
            }

            RuntimeLog.Error("[WebSocket] Failed to reconnect after all attempts");
        }

        private static string ComputeProjectHash()
        {
            string path = Application.dataPath;
            using var sha = System.Security.Cryptography.SHA256.Create();
            byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(path));
            return BitConverter.ToString(hash).Replace("-", "").Substring(0, 12).ToLowerInvariant();
        }

        private static Uri BuildWebSocketUri(string baseUrl)
        {
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var httpUri))
                throw new InvalidOperationException($"Invalid MCP server URL: {baseUrl}");

            string host = httpUri.Host;
            if (host == "0.0.0.0" || host == "::")
                host = "localhost";

            var builder = new UriBuilder(httpUri)
            {
                Scheme = httpUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws",
                Host = host,
                Path = httpUri.AbsolutePath.TrimEnd('/') + "/hub/plugin"
            };

            return builder.Uri;
        }
    }
}
