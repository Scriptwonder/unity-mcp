using System;
using System.Reflection;
using NUnit.Framework;
using MCPForUnity.Editor.Services;
using UnityEditor.Compilation;

namespace MCPForUnityTests.Editor.Services
{
    /// <summary>
    /// Tests for CompileJobManager's job state machine. Real compiles are never
    /// triggered: RequestCompilationOverride / IsCompilingOverride stand in for
    /// CompilationPipeline so the tests can run without domain-reloading the
    /// test run (same reason TestJobManagerInitTimeoutTests avoids StartJob).
    /// Reflection is used only to clean up the private Jobs dictionary.
    /// </summary>
    public class CompileJobManagerTests
    {
        private FieldInfo _jobsField;
        private FieldInfo _currentJobIdField;
        private int _requestCount;
        private bool _isCompiling;
        private string _startedJobId;

        [SetUp]
        public void SetUp()
        {
            var managerType = typeof(CompileJobManager);
            _jobsField = managerType.GetField("Jobs", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(_jobsField, "Could not find Jobs field");
            _currentJobIdField = managerType.GetField("_currentJobId", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(_currentJobIdField, "Could not find _currentJobId field");

            // Ensure no running job leaks into a test (e.g. from a prior failure).
            CompileJobManager.ClearStuckJob();

            _requestCount = 0;
            _isCompiling = false;
            _startedJobId = null;
            CompileJobManager.RequestCompilationOverride = () => _requestCount++;
            CompileJobManager.IsCompilingOverride = () => _isCompiling;
        }

        [TearDown]
        public void TearDown()
        {
            CompileJobManager.RequestCompilationOverride = null;
            CompileJobManager.IsCompilingOverride = null;

            // Remove synthetic jobs and flush so they don't survive domain
            // reloads and pollute later runs (SessionState-backed manager).
            var jobs = _jobsField.GetValue(null) as System.Collections.IDictionary;
            if (_startedJobId != null)
            {
                jobs?.Remove(_startedJobId);
                if ((_currentJobIdField.GetValue(null) as string) == _startedJobId)
                {
                    _currentJobIdField.SetValue(null, null);
                }
            }
            CompileJobManager.PersistToSessionState(force: true);
        }

        private static CompilerMessage Error(string file, int line, string message)
        {
            return new CompilerMessage { file = file, line = line, column = 1, message = message, type = CompilerMessageType.Error };
        }

        private static CompilerMessage Warning(string file, int line, string message)
        {
            return new CompilerMessage { file = file, line = line, column = 1, message = message, type = CompilerMessageType.Warning };
        }

        [Test]
        public void StartJob_WhenIdle_RequestsCompilationImmediately()
        {
            _isCompiling = false;
            _startedJobId = CompileJobManager.StartJob(out bool attached);

            Assert.IsFalse(attached);
            Assert.AreEqual(1, _requestCount, "Should request compilation immediately when idle");

            var job = CompileJobManager.GetJob(_startedJobId);
            Assert.NotNull(job);
            Assert.AreEqual(CompileJobStatus.Running, job.Status);
            Assert.AreEqual(CompileJobManager.PhaseCompileRequested, job.Phase);
        }

        [Test]
        public void StartJob_WhenAlreadyCompiling_WaitsForCurrentThenRecompiles()
        {
            _isCompiling = true;
            _startedJobId = CompileJobManager.StartJob(out bool attached);

            Assert.IsFalse(attached);
            Assert.AreEqual(0, _requestCount, "Must not stack a request on top of the in-flight compile");
            var job = CompileJobManager.GetJob(_startedJobId);
            Assert.AreEqual(CompileJobManager.PhaseWaitingForCurrent, job.Phase);

            // The pre-existing compilation finishes -> ours is requested.
            _isCompiling = false;
            CompileJobManager.HandleCompilationFinished();
            Assert.AreEqual(1, _requestCount, "Should recompile after the current compile finished");
            Assert.AreEqual(CompileJobManager.PhaseCompileRequested, CompileJobManager.GetJob(_startedJobId).Phase);

            // Our compile runs and finishes cleanly.
            _isCompiling = true;
            CompileJobManager.HandleCompilationStarted();
            Assert.AreEqual(CompileJobManager.PhaseCompiling, CompileJobManager.GetJob(_startedJobId).Phase);
            _isCompiling = false;
            CompileJobManager.HandleCompilationFinished();

            job = CompileJobManager.GetJob(_startedJobId);
            Assert.AreEqual(CompileJobStatus.Succeeded, job.Status);
            Assert.AreEqual("event", job.FinalizedBy);
        }

        [Test]
        public void StartJob_AttachesToRunningJob()
        {
            _startedJobId = CompileJobManager.StartJob(out _);
            string secondId = CompileJobManager.StartJob(out bool attached);

            Assert.IsTrue(attached);
            Assert.AreEqual(_startedJobId, secondId);
            Assert.AreEqual(1, _requestCount, "Attaching must not trigger a second compile");
        }

        [Test]
        public void FullLifecycle_WithErrors_FinalizesFailed()
        {
            _startedJobId = CompileJobManager.StartJob(out _);
            _isCompiling = true;
            CompileJobManager.HandleCompilationStarted();

            CompileJobManager.HandleAssemblyCompilationFinished("Library/ScriptAssemblies/Assembly-CSharp.dll", new[]
            {
                Error("Assets/Scripts/GameManager.cs", 12, "error CS1002: ; expected"),
                Warning("Assets/Scripts/Hud.cs", 3, "warning CS0414: unused field"),
                Error("Assets/Scripts/Hud.cs", 7, "error CS0246: type not found"),
            });

            _isCompiling = false;
            CompileJobManager.HandleCompilationFinished();

            var job = CompileJobManager.GetJob(_startedJobId);
            Assert.AreEqual(CompileJobStatus.Failed, job.Status);
            Assert.AreEqual(2, job.ErrorsTotal);
            Assert.AreEqual(1, job.WarningsCount);
            Assert.AreEqual("Assets/Scripts/GameManager.cs", job.Errors[0].File);
            Assert.AreEqual(12, job.Errors[0].Line);
            StringAssert.Contains("CS1002", job.Errors[0].Message);

            // Wire payload shape consumed by the Python server.
            var payload = CompileJobManager.ToSerializable(job);
            string json = Newtonsoft.Json.JsonConvert.SerializeObject(payload);
            StringAssert.Contains("\"status\":\"failed\"", json);
            StringAssert.Contains("\"errors_total\":2", json);
            StringAssert.Contains("\"warnings_count\":1", json);
            StringAssert.Contains("\"duration_ms\":", json);
            StringAssert.Contains("\"file\":\"Assets/Scripts/GameManager.cs\"", json);
        }

        [Test]
        public void Job_SurvivesPersistAndRestore()
        {
            _startedJobId = CompileJobManager.StartJob(out _);
            _isCompiling = true;
            CompileJobManager.HandleCompilationStarted();
            CompileJobManager.HandleAssemblyCompilationFinished("Assembly-CSharp.dll", new[]
            {
                Error("Assets/Scripts/X.cs", 5, "error CS0103: name does not exist"),
                Warning("Assets/Scripts/X.cs", 9, "warning CS0168: unused variable"),
            });

            // Simulate the domain reload: persist, wipe in-memory state, restore.
            CompileJobManager.PersistToSessionState(force: true);
            var jobs = _jobsField.GetValue(null) as System.Collections.IDictionary;
            jobs.Remove(_startedJobId);
            _currentJobIdField.SetValue(null, null);
            CompileJobManager.TryRestoreFromSessionState();

            var restored = CompileJobManager.GetJob(_startedJobId);
            Assert.NotNull(restored, "Job should be restored from SessionState");
            Assert.AreEqual(CompileJobStatus.Running, restored.Status);
            Assert.AreEqual(CompileJobManager.PhaseCompiling, restored.Phase);
            Assert.AreEqual(1, restored.ErrorsTotal);
            Assert.AreEqual(1, restored.WarningsCount);
            Assert.AreEqual("Assets/Scripts/X.cs", restored.Errors[0].File);

            // The restored job is still live: finishing finalizes it as failed.
            _isCompiling = false;
            CompileJobManager.HandleCompilationFinished();
            Assert.AreEqual(CompileJobStatus.Failed, CompileJobManager.GetJob(_startedJobId).Status);
        }

        [Test]
        public void GetJob_ReRequests_WhenCompileRequestSwallowed()
        {
            _startedJobId = CompileJobManager.StartJob(out _);
            var job = CompileJobManager.GetJob(_startedJobId);

            // Simulate a request swallowed by a reload: still compile_requested,
            // idle editor, last update beyond the retry window.
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            job.LastUpdateUnixMs = now - 5_000;

            _isCompiling = false;
            CompileJobManager.GetJob(_startedJobId);

            Assert.AreEqual(2, _requestCount, "Poll should re-request the swallowed compile");
            Assert.AreEqual(CompileJobStatus.Running, CompileJobManager.GetJob(_startedJobId).Status);
        }

        [Test]
        public void GetJob_AutoFails_WhenCompileNeverStarts()
        {
            _startedJobId = CompileJobManager.StartJob(out _);
            var job = CompileJobManager.GetJob(_startedJobId);

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            job.StartedUnixMs = now - 40_000;   // beyond the 30s start budget
            job.LastUpdateUnixMs = now - 10_000;
            job.RequestAttempts = 3;            // retry budget exhausted

            _isCompiling = false;
            var result = CompileJobManager.GetJob(_startedJobId);

            Assert.AreEqual(CompileJobStatus.Failed, result.Status);
            StringAssert.Contains("compile_did_not_start", result.Error);
        }

        [Test]
        public void GetJob_FinalizesFromCapturedState_WhenFinishEventLost()
        {
            _startedJobId = CompileJobManager.StartJob(out _);
            _isCompiling = true;
            CompileJobManager.HandleCompilationStarted();

            // Finish event lost to the reload: editor idle, no update for a while.
            var job = CompileJobManager.GetJob(_startedJobId);
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            job.LastUpdateUnixMs = now - 15_000;

            _isCompiling = false;
            var result = CompileJobManager.GetJob(_startedJobId);

            Assert.AreEqual(CompileJobStatus.Succeeded, result.Status, "No captured errors means the reloadful compile succeeded");
            Assert.AreEqual("poll_recovery", result.FinalizedBy);
        }
    }
}
