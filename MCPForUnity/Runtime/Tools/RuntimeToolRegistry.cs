using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using MCPForUnity.Runtime.Helpers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MCPForUnity.Runtime.Tools
{
    /// <summary>
    /// Metadata for a registered runtime tool, used for registration with the MCP server.
    /// </summary>
    public class RuntimeToolMetadata
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public bool StructuredOutput { get; set; }
    }

    /// <summary>
    /// Holds information about a registered runtime tool handler.
    /// </summary>
    internal class RuntimeHandlerInfo
    {
        public string CommandName { get; }
        public Func<JObject, object> SyncHandler { get; }
        public Func<JObject, Task<object>> AsyncHandler { get; }
        public bool IsAsync => AsyncHandler != null;

        public RuntimeHandlerInfo(string commandName, Func<JObject, object> syncHandler, Func<JObject, Task<object>> asyncHandler)
        {
            CommandName = commandName;
            SyncHandler = syncHandler;
            AsyncHandler = asyncHandler;
        }
    }

    /// <summary>
    /// Registry for all runtime MCP tool handlers. Discovers classes marked with
    /// [RuntimeMcpTool] and registers their HandleCommand methods.
    /// </summary>
    public static class RuntimeToolRegistry
    {
        private static readonly Dictionary<string, RuntimeHandlerInfo> _handlers = new();
        private static bool _initialized;

        public static void Initialize()
        {
            if (_initialized) return;
            AutoDiscoverTools();
            _initialized = true;
        }

        public static void Reset()
        {
            _handlers.Clear();
            _initialized = false;
        }

        private static void AutoDiscoverTools()
        {
            try
            {
                var allTypes = AppDomain.CurrentDomain.GetAssemblies()
                    .Where(a => !a.IsDynamic)
                    .SelectMany(a =>
                    {
                        try { return a.GetTypes(); }
                        catch { return new Type[0]; }
                    })
                    .ToList();

                var toolTypes = allTypes.Where(t => t.GetCustomAttribute<RuntimeMcpToolAttribute>() != null);
                int count = 0;
                foreach (var type in toolTypes)
                {
                    if (RegisterToolType(type))
                        count++;
                }

                RuntimeLog.Info($"Auto-discovered {count} runtime tools ({_handlers.Count} handlers)", false);
            }
            catch (Exception ex)
            {
                RuntimeLog.Error($"Failed to auto-discover runtime MCP tools: {ex.Message}");
            }
        }

        private static bool RegisterToolType(Type type)
        {
            var attr = type.GetCustomAttribute<RuntimeMcpToolAttribute>();
            string commandName = attr.Name;

            if (string.IsNullOrEmpty(commandName))
            {
                commandName = RuntimeStringCaseUtility.ToSnakeCase(type.Name);
            }

            if (_handlers.ContainsKey(commandName))
            {
                RuntimeLog.Warn($"Duplicate runtime tool name '{commandName}'. {type.Name} will override.");
            }

            var method = type.GetMethod(
                "HandleCommand",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(JObject) },
                null
            );

            if (method == null)
            {
                RuntimeLog.Warn($"Runtime tool {type.Name} has [RuntimeMcpTool] but no public static HandleCommand(JObject) method");
                return false;
            }

            try
            {
                RuntimeHandlerInfo handlerInfo;

                if (typeof(Task).IsAssignableFrom(method.ReturnType))
                {
                    var asyncHandler = CreateAsyncHandlerDelegate(method, commandName);
                    handlerInfo = new RuntimeHandlerInfo(commandName, null, asyncHandler);
                }
                else
                {
                    var handler = (Func<JObject, object>)Delegate.CreateDelegate(
                        typeof(Func<JObject, object>),
                        method
                    );
                    handlerInfo = new RuntimeHandlerInfo(commandName, handler, null);
                }

                _handlers[commandName] = handlerInfo;
                return true;
            }
            catch (Exception ex)
            {
                RuntimeLog.Error($"Failed to register runtime tool {type.Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Execute a command by name. For sync handlers, returns the result directly.
        /// For async handlers, completes the TCS when done and returns null.
        /// </summary>
        public static object ExecuteCommand(string commandName, JObject @params, TaskCompletionSource<string> tcs)
        {
            if (!_handlers.TryGetValue(commandName, out var handlerInfo))
            {
                throw new InvalidOperationException($"Unknown runtime tool: {commandName}");
            }

            if (handlerInfo.IsAsync)
            {
                ExecuteAsyncHandler(handlerInfo, @params, commandName, tcs);
                return null;
            }

            if (handlerInfo.SyncHandler == null)
            {
                throw new InvalidOperationException($"Handler for '{commandName}' has no sync implementation");
            }

            return handlerInfo.SyncHandler(@params);
        }

        /// <summary>
        /// Get metadata for all registered tools (for server registration).
        /// </summary>
        public static List<RuntimeToolMetadata> GetRegisteredTools()
        {
            var tools = new List<RuntimeToolMetadata>();
            foreach (var kvp in _handlers)
            {
                // Look up the attribute to get description
                var allTypes = AppDomain.CurrentDomain.GetAssemblies()
                    .Where(a => !a.IsDynamic)
                    .SelectMany(a =>
                    {
                        try { return a.GetTypes(); }
                        catch { return new Type[0]; }
                    });

                string description = "";
                bool structuredOutput = true;

                foreach (var type in allTypes)
                {
                    var attr = type.GetCustomAttribute<RuntimeMcpToolAttribute>();
                    if (attr == null) continue;
                    string name = attr.Name ?? RuntimeStringCaseUtility.ToSnakeCase(type.Name);
                    if (name == kvp.Key)
                    {
                        description = attr.Description ?? "";
                        structuredOutput = attr.StructuredOutput;
                        break;
                    }
                }

                tools.Add(new RuntimeToolMetadata
                {
                    Name = kvp.Key,
                    Description = description,
                    StructuredOutput = structuredOutput
                });
            }
            return tools;
        }

        public static bool HasHandler(string commandName)
        {
            return _handlers.ContainsKey(commandName);
        }

        private static Func<JObject, Task<object>> CreateAsyncHandlerDelegate(MethodInfo method, string commandName)
        {
            return async (JObject parameters) =>
            {
                object rawResult;
                try
                {
                    rawResult = method.Invoke(null, new object[] { parameters });
                }
                catch (TargetInvocationException ex)
                {
                    throw ex.InnerException ?? ex;
                }

                if (rawResult is not Task task)
                {
                    throw new InvalidOperationException($"Async handler '{commandName}' did not return a Task");
                }

                await task.ConfigureAwait(false);

                var taskType = task.GetType();
                if (taskType.IsGenericType)
                {
                    var resultProperty = taskType.GetProperty("Result");
                    if (resultProperty != null)
                    {
                        return resultProperty.GetValue(task);
                    }
                }

                return null;
            };
        }

        private static void ExecuteAsyncHandler(
            RuntimeHandlerInfo handlerInfo,
            JObject parameters,
            string commandName,
            TaskCompletionSource<string> tcs)
        {
            Task<object> handlerTask;

            try
            {
                handlerTask = handlerInfo.AsyncHandler(parameters);
            }
            catch (Exception ex)
            {
                ReportAsyncFailure(commandName, tcs, ex);
                return;
            }

            if (handlerTask == null)
            {
                CompleteAsyncCommand(commandName, tcs, null);
                return;
            }

            async void AwaitHandler()
            {
                try
                {
                    var finalResult = await handlerTask.ConfigureAwait(false);
                    CompleteAsyncCommand(commandName, tcs, finalResult);
                }
                catch (Exception ex)
                {
                    ReportAsyncFailure(commandName, tcs, ex);
                }
            }

            AwaitHandler();
        }

        private static void CompleteAsyncCommand(string commandName, TaskCompletionSource<string> tcs, object result)
        {
            try
            {
                var response = new { status = "success", result };
                string json = JsonConvert.SerializeObject(response);
                if (!tcs.TrySetResult(json))
                {
                    RuntimeLog.Warn($"TCS for async command '{commandName}' was already completed");
                }
            }
            catch (Exception ex)
            {
                RuntimeLog.Error($"Error completing async command '{commandName}': {ex.Message}");
                ReportAsyncFailure(commandName, tcs, ex);
            }
        }

        private static void ReportAsyncFailure(string commandName, TaskCompletionSource<string> tcs, Exception ex)
        {
            RuntimeLog.Error($"Error in async command '{commandName}': {ex.Message}\n{ex.StackTrace}");

            string json;
            try
            {
                json = JsonConvert.SerializeObject(new
                {
                    status = "error",
                    error = ex.Message,
                    command = commandName,
                    stackTrace = ex.StackTrace
                });
            }
            catch
            {
                json = "{\"status\":\"error\",\"error\":\"Failed to complete command\"}";
            }

            tcs.TrySetResult(json);
        }
    }
}
