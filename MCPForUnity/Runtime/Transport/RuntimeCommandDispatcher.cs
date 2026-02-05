using System;
using System.Collections.Concurrent;
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
    /// Command model for deserialization of incoming commands.
    /// </summary>
    internal class RuntimeCommand
    {
        public string type { get; set; }
        public JObject @params { get; set; }
    }

    /// <summary>
    /// Dispatches MCP commands on the Unity main thread at runtime.
    /// Background WebSocket threads enqueue commands; Update() processes them.
    /// </summary>
    public class RuntimeCommandDispatcher : MonoBehaviour
    {
        private static RuntimeCommandDispatcher _instance;

        private sealed class PendingCommand
        {
            public string CommandJson;
            public TaskCompletionSource<string> CompletionSource;
            public CancellationToken CancellationToken;
        }

        private readonly ConcurrentQueue<PendingCommand> _queue = new();
        private bool _registryInitialized;

        public static RuntimeCommandDispatcher Instance => _instance;

        internal static RuntimeCommandDispatcher EnsureInstance()
        {
            if (_instance != null) return _instance;

            var go = new GameObject("[MCP Runtime Dispatcher]");
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideInHierarchy;
            _instance = go.AddComponent<RuntimeCommandDispatcher>();
            return _instance;
        }

        void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            _instance = this;
        }

        void OnDestroy()
        {
            if (_instance == this)
                _instance = null;
        }

        void Update()
        {
            if (!_registryInitialized)
            {
                RuntimeToolRegistry.Initialize();
                _registryInitialized = true;
            }

            int processed = 0;
            while (_queue.TryDequeue(out var pending) && processed < 50)
            {
                processed++;
                ProcessCommand(pending);
            }
        }

        /// <summary>
        /// Schedule a command for execution on the main thread. Thread-safe.
        /// </summary>
        public Task<string> ExecuteCommandJsonAsync(string commandJson, CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            if (cancellationToken.CanBeCanceled)
            {
                cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            }

            _queue.Enqueue(new PendingCommand
            {
                CommandJson = commandJson,
                CompletionSource = tcs,
                CancellationToken = cancellationToken
            });

            return tcs.Task;
        }

        private void ProcessCommand(PendingCommand pending)
        {
            if (pending.CancellationToken.IsCancellationRequested)
            {
                pending.CompletionSource.TrySetCanceled();
                return;
            }

            string commandText = pending.CommandJson?.Trim();
            if (string.IsNullOrEmpty(commandText))
            {
                pending.CompletionSource.TrySetResult(SerializeError("Empty command received"));
                return;
            }

            if (string.Equals(commandText, "ping", StringComparison.OrdinalIgnoreCase))
            {
                pending.CompletionSource.TrySetResult(
                    JsonConvert.SerializeObject(new { status = "success", result = new { message = "pong" } }));
                return;
            }

            if (!IsValidJson(commandText))
            {
                pending.CompletionSource.TrySetResult(SerializeError("Invalid JSON format"));
                return;
            }

            try
            {
                var command = JsonConvert.DeserializeObject<RuntimeCommand>(commandText);
                if (command == null || string.IsNullOrWhiteSpace(command.type))
                {
                    pending.CompletionSource.TrySetResult(SerializeError("Command type cannot be empty"));
                    return;
                }

                if (string.Equals(command.type, "ping", StringComparison.OrdinalIgnoreCase))
                {
                    pending.CompletionSource.TrySetResult(
                        JsonConvert.SerializeObject(new { status = "success", result = new { message = "pong" } }));
                    return;
                }

                var parameters = command.@params ?? new JObject();

                if (!RuntimeToolRegistry.HasHandler(command.type))
                {
                    pending.CompletionSource.TrySetResult(
                        SerializeError($"Unknown runtime tool: {command.type}"));
                    return;
                }

                var result = RuntimeToolRegistry.ExecuteCommand(command.type, parameters, pending.CompletionSource);

                // If result is null, the command is async and will complete the TCS itself
                if (result == null) return;

                var response = new { status = "success", result };
                pending.CompletionSource.TrySetResult(JsonConvert.SerializeObject(response));
            }
            catch (Exception ex)
            {
                RuntimeLog.Error($"Error processing command: {ex.Message}\n{ex.StackTrace}");
                pending.CompletionSource.TrySetResult(SerializeError(ex.Message));
            }
        }

        private static string SerializeError(string message)
        {
            return JsonConvert.SerializeObject(new { status = "error", error = message });
        }

        private static bool IsValidJson(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            text = text.Trim();
            if ((text.StartsWith("{") && text.EndsWith("}")) || (text.StartsWith("[") && text.EndsWith("]")))
            {
                try
                {
                    JToken.Parse(text);
                    return true;
                }
                catch
                {
                    return false;
                }
            }
            return false;
        }
    }
}
