using Newtonsoft.Json;

namespace MCPForUnity.Runtime.Tools
{
    public interface IRuntimeMcpResponse
    {
        [JsonProperty("success")]
        bool Success { get; }
    }

    public sealed class RuntimeSuccessResponse : IRuntimeMcpResponse
    {
        [JsonProperty("success")]
        public bool Success => true;

        [JsonProperty("message")]
        public string Message { get; }

        [JsonProperty("data", NullValueHandling = NullValueHandling.Ignore)]
        public object Data { get; }

        public RuntimeSuccessResponse(string message, object data = null)
        {
            Message = message;
            Data = data;
        }
    }

    public sealed class RuntimeErrorResponse : IRuntimeMcpResponse
    {
        [JsonProperty("success")]
        public bool Success => false;

        [JsonProperty("error")]
        public string Error { get; }

        [JsonProperty("data", NullValueHandling = NullValueHandling.Ignore)]
        public object Data { get; }

        public RuntimeErrorResponse(string error, object data = null)
        {
            Error = error;
            Data = data;
        }
    }
}
