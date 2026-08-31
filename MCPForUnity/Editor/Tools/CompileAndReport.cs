using System;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Services;
using Newtonsoft.Json.Linq;

namespace MCPForUnity.Editor.Tools
{
    /// <summary>
    /// Starts an async compile job (CompilationPipeline.RequestScriptCompilation)
    /// and returns a job id immediately. Poll get_compile_job(job_id) for the
    /// report — the job survives the domain reload a successful compile triggers
    /// (SessionState-backed, same pattern as run_tests/get_test_job).
    /// </summary>
    [McpForUnityTool("compile_and_report", AutoRegister = false)]
    public static class CompileAndReport
    {
        public static object HandleCommand(JObject @params)
        {
            try
            {
                // Escape hatch mirroring run_tests: clear a stuck/orphaned job.
                if (ParamCoercion.CoerceBool(@params?["clear_stuck"], false))
                {
                    bool wasCleared = CompileJobManager.ClearStuckJob();
                    return new SuccessResponse(
                        wasCleared ? "Stuck compile job cleared." : "No running compile job to clear.",
                        new { cleared = wasCleared }
                    );
                }

                if (TestRunStatus.IsRunning)
                {
                    return new ErrorResponse("tests_running", new
                    {
                        reason = "tests_running",
                        retry_after_ms = 5000
                    });
                }

                string jobId = CompileJobManager.StartJob(out bool attached);
                return new SuccessResponse(
                    attached ? "Attached to compile job already in progress." : "Compile job started.",
                    new
                    {
                        job_id = jobId,
                        status = "running",
                        attached
                    });
            }
            catch (Exception ex)
            {
                return new ErrorResponse($"Failed to start compile job: {ex.Message}");
            }
        }
    }
}
