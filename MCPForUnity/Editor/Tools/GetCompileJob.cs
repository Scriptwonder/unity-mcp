using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Services;
using Newtonsoft.Json.Linq;

namespace MCPForUnity.Editor.Tools
{
    /// <summary>
    /// Poll a previously started async compile job by job_id.
    /// </summary>
    [McpForUnityTool("get_compile_job", AutoRegister = false)]
    public static class GetCompileJob
    {
        public static object HandleCommand(JObject @params)
        {
            string jobId = @params?["job_id"]?.ToString() ?? @params?["jobId"]?.ToString();
            if (string.IsNullOrWhiteSpace(jobId))
            {
                return new ErrorResponse("Missing required parameter 'job_id'.");
            }

            var job = CompileJobManager.GetJob(jobId);
            if (job == null)
            {
                return new ErrorResponse("Unknown job_id.");
            }

            var payload = CompileJobManager.ToSerializable(job);
            return new SuccessResponse("Compile job status retrieved.", payload);
        }
    }
}
