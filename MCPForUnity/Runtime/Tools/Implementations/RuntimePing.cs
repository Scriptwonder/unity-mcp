using Newtonsoft.Json.Linq;
using UnityEngine;

namespace MCPForUnity.Runtime.Tools.Implementations
{
    /// <summary>
    /// Simple ping tool to verify the runtime MCP bridge is working.
    /// </summary>
    [RuntimeMcpTool("ping")]
    public static class RuntimePing
    {
        public static object HandleCommand(JObject @params)
        {
            var p = new RuntimeToolParams(@params);
            string message = p.Get("message", "pong");

            return new RuntimeSuccessResponse($"Runtime bridge is alive! Message: {message}", new
            {
                echoMessage = message,
                unityVersion = Application.unityVersion,
                platform = Application.platform.ToString(),
                isPlaying = Application.isPlaying,
                productName = Application.productName,
                time = Time.time
            });
        }
    }
}
