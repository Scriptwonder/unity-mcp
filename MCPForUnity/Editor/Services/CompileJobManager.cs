using System;
using System.Collections.Generic;
using System.Linq;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.Compilation;

namespace MCPForUnity.Editor.Services
{
    internal enum CompileJobStatus
    {
        Running,
        Succeeded,
        Failed
    }

    internal sealed class CompileJobError
    {
        public string File { get; set; }
        public int Line { get; set; }
        public string Message { get; set; }
    }

    internal sealed class CompileJob
    {
        public string JobId { get; set; }
        public CompileJobStatus Status { get; set; }
        /// <summary>One of the CompileJobManager.Phase* constants.</summary>
        public string Phase { get; set; }
        public long StartedUnixMs { get; set; }
        public long? CompileStartedUnixMs { get; set; }
        public long? FinishedUnixMs { get; set; }
        public long LastUpdateUnixMs { get; set; }
        public int RequestAttempts { get; set; }
        public List<CompileJobError> Errors { get; set; }
        public int ErrorsTotal { get; set; }
        public int WarningsCount { get; set; }
        public string Error { get; set; }
        /// <summary>"event" (compilationFinished handler) or "poll_recovery" (GetJob after lost events).</summary>
        public string FinalizedBy { get; set; }
    }

    /// <summary>
    /// Tracks async compile jobs started via the compile_and_report tool.
    /// Mirrors TestJobManager: jobs persist to SessionState so they survive the
    /// domain reload a successful compilation triggers, and GetJob performs
    /// poll-driven recovery for events lost across that reload.
    /// </summary>
    [InitializeOnLoad]
    internal static class CompileJobManager
    {
        internal const string PhaseWaitingForCurrent = "waiting_for_current_compile";
        internal const string PhaseCompileRequested = "compile_requested";
        internal const string PhaseCompiling = "compiling";
        internal const string PhaseFinished = "finished";

        // Keep payloads small during 1 s polling; count everything, cap the list.
        private const int ErrorCap = 100;
        private const int MaxJobsToKeep = 5;
        private const long MinPersistIntervalMs = 1000;

        // Poll-driven recovery budgets (see GetJob).
        private const int MaxRequestAttempts = 3;
        private const long RequestRetryMs = 3_000;
        private const long CompileStartTimeoutMs = 30_000;
        private const long FinishGraceMs = 10_000;
        private const long StaleJobCutoffMs = 5 * 60 * 1000;

        // SessionState survives domain reloads within the same Unity Editor session.
        private const string SessionKeyJobs = "MCPForUnity.CompileJobsV1";

        private static readonly object LockObj = new();
        private static readonly Dictionary<string, CompileJob> Jobs = new();
        private static string _currentJobId;
        private static long _lastPersistUnixMs;

        /// <summary>
        /// Test seam: EditMode tests override this so the job state machine can be
        /// exercised without triggering a real script compilation (which would
        /// domain-reload the test run). Production leaves it null.
        /// </summary>
        internal static Action RequestCompilationOverride;

        /// <summary>
        /// Test seam: overrides the "is the editor compiling right now" probe.
        /// Production leaves it null (EditorStateCache.GetActualIsCompiling).
        /// </summary>
        internal static Func<bool> IsCompilingOverride;

        static CompileJobManager()
        {
            // Restore after domain reloads (a successful compile always reloads).
            TryRestoreFromSessionState();

            // Statics reset on domain reload; this [InitializeOnLoad] ctor
            // re-subscribes per-domain (same pattern as EditorStateCache).
            CompilationPipeline.compilationStarted += _ => HandleCompilationStarted();
            CompilationPipeline.assemblyCompilationFinished +=
                (assembly, messages) => HandleAssemblyCompilationFinished(assembly, messages);
            CompilationPipeline.compilationFinished += _ => HandleCompilationFinished();
        }

        public static bool HasRunningJob
        {
            get
            {
                lock (LockObj)
                {
                    return !string.IsNullOrEmpty(_currentJobId);
                }
            }
        }

        private static void RequestCompilation()
        {
            var over = RequestCompilationOverride;
            if (over != null)
            {
                over();
                return;
            }
            CompilationPipeline.RequestScriptCompilation();
        }

        private static bool IsCompilingNow()
        {
            var over = IsCompilingOverride;
            if (over != null)
            {
                return over();
            }
            return EditorStateCache.GetActualIsCompiling();
        }

        /// <summary>
        /// Starts (or attaches to) a compile job. When the editor is already
        /// compiling on entry the job waits for the current compilation to finish
        /// and then requests a fresh one. When a compile job is already running,
        /// the caller attaches to it instead of double-compiling.
        /// </summary>
        internal static string StartJob(out bool attachedToExisting)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            bool requestNow = false;
            string jobId;

            lock (LockObj)
            {
                if (!string.IsNullOrEmpty(_currentJobId)
                    && Jobs.TryGetValue(_currentJobId, out var running)
                    && running.Status == CompileJobStatus.Running)
                {
                    if (now - running.LastUpdateUnixMs <= StaleJobCutoffMs)
                    {
                        attachedToExisting = true;
                        return _currentJobId;
                    }

                    // Orphaned by a long-past domain reload nobody polled through —
                    // fail it and start fresh instead of attaching to a zombie.
                    McpLog.Warn($"[CompileJobManager] Clearing stale job {_currentJobId} (last update {(now - running.LastUpdateUnixMs) / 1000}s ago)");
                    running.Status = CompileJobStatus.Failed;
                    running.Error = "Compile job orphaned after domain reload";
                    running.Phase = PhaseFinished;
                    running.FinishedUnixMs = now;
                    running.LastUpdateUnixMs = now;
                    _currentJobId = null;
                }

                jobId = Guid.NewGuid().ToString("N");
                var job = new CompileJob
                {
                    JobId = jobId,
                    Status = CompileJobStatus.Running,
                    StartedUnixMs = now,
                    LastUpdateUnixMs = now,
                    Errors = new List<CompileJobError>(),
                };

                if (IsCompilingNow())
                {
                    // Wait for the in-flight compilation; HandleCompilationFinished
                    // requests the fresh one.
                    job.Phase = PhaseWaitingForCurrent;
                }
                else
                {
                    job.Phase = PhaseCompileRequested;
                    job.RequestAttempts = 1;
                    requestNow = true;
                }

                Jobs[jobId] = job;
                _currentJobId = jobId;
            }

            PersistToSessionState(force: true);

            if (requestNow)
            {
                RequestCompilation();
            }

            attachedToExisting = false;
            return jobId;
        }

        /// <summary>
        /// Force-clears a stuck or orphaned compile job (mirrors TestJobManager.ClearStuckJob).
        /// </summary>
        internal static bool ClearStuckJob()
        {
            bool cleared = false;
            lock (LockObj)
            {
                if (string.IsNullOrEmpty(_currentJobId))
                {
                    return false;
                }

                if (Jobs.TryGetValue(_currentJobId, out var job) && job.Status == CompileJobStatus.Running)
                {
                    long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    job.Status = CompileJobStatus.Failed;
                    job.Error = "Compile job cleared manually (stuck or orphaned)";
                    job.Phase = PhaseFinished;
                    job.FinishedUnixMs = now;
                    job.LastUpdateUnixMs = now;
                    McpLog.Warn($"[CompileJobManager] Manually cleared stuck job {_currentJobId}");
                    cleared = true;
                }

                _currentJobId = null;
            }
            PersistToSessionState(force: true);
            return cleared;
        }

        // ── Compilation pipeline event handlers ─────────────────────────────

        internal static void HandleCompilationStarted()
        {
            lock (LockObj)
            {
                var job = CurrentRunningJobLocked();
                if (job == null || job.Phase != PhaseCompileRequested)
                {
                    // waiting_for_current: this is the pre-existing compilation, not ours.
                    return;
                }

                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                job.Phase = PhaseCompiling;
                job.CompileStartedUnixMs = now;
                job.LastUpdateUnixMs = now;
                job.Errors?.Clear();
                job.ErrorsTotal = 0;
                job.WarningsCount = 0;
            }
            PersistToSessionState(force: true);
        }

        internal static void HandleAssemblyCompilationFinished(string assemblyPath, CompilerMessage[] messages)
        {
            if (messages == null || messages.Length == 0)
            {
                return;
            }

            lock (LockObj)
            {
                var job = CurrentRunningJobLocked();
                if (job == null)
                {
                    return;
                }

                // Defensive: if compilationStarted was missed (late subscribe),
                // promote the job so per-assembly results are still captured.
                if (job.Phase == PhaseCompileRequested)
                {
                    job.Phase = PhaseCompiling;
                    job.CompileStartedUnixMs ??= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                }

                if (job.Phase != PhaseCompiling)
                {
                    return;
                }

                foreach (var message in messages)
                {
                    if (message.type == CompilerMessageType.Error)
                    {
                        job.ErrorsTotal++;
                        job.Errors ??= new List<CompileJobError>();
                        if (job.Errors.Count < ErrorCap)
                        {
                            job.Errors.Add(new CompileJobError
                            {
                                File = message.file,
                                Line = message.line,
                                Message = message.message,
                            });
                        }
                    }
                    else if (message.type == CompilerMessageType.Warning)
                    {
                        job.WarningsCount++;
                    }
                }

                job.LastUpdateUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }
            PersistToSessionState();
        }

        internal static void HandleCompilationFinished()
        {
            bool requestFresh = false;
            lock (LockObj)
            {
                var job = CurrentRunningJobLocked();
                if (job == null)
                {
                    return;
                }

                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                if (job.Phase == PhaseWaitingForCurrent)
                {
                    // The pre-existing compilation ended — now request ours.
                    job.Phase = PhaseCompileRequested;
                    job.RequestAttempts++;
                    job.LastUpdateUnixMs = now;
                    requestFresh = true;
                }
                else if (job.Phase == PhaseCompiling)
                {
                    FinalizeLocked(job, now, "event");
                }
                // compile_requested: a finish without a matching start we saw —
                // leave it; GetJob recovery re-requests or finalizes.
            }

            // Persist BEFORE the domain reload a successful compile triggers.
            PersistToSessionState(force: true);

            if (requestFresh)
            {
                RequestCompilation();
            }
        }

        private static CompileJob CurrentRunningJobLocked()
        {
            if (string.IsNullOrEmpty(_currentJobId) || !Jobs.TryGetValue(_currentJobId, out var job))
            {
                return null;
            }
            return job.Status == CompileJobStatus.Running ? job : null;
        }

        private static void FinalizeLocked(CompileJob job, long now, string finalizedBy)
        {
            job.Status = job.ErrorsTotal > 0 ? CompileJobStatus.Failed : CompileJobStatus.Succeeded;
            job.Error = job.ErrorsTotal > 0
                ? $"Compilation failed with {job.ErrorsTotal} error(s)"
                : null;
            job.Phase = PhaseFinished;
            job.FinishedUnixMs = now;
            job.LastUpdateUnixMs = now;
            job.FinalizedBy = finalizedBy;
            if (_currentJobId == job.JobId)
            {
                _currentJobId = null;
            }
        }

        // ── Poll surface ─────────────────────────────────────────────────────

        /// <summary>
        /// Fetch a job by id, self-healing stalls first: compilation events can be
        /// lost to the domain reload, so the poll re-requests a compile that never
        /// started and finalizes a compile whose finish event was swallowed.
        /// </summary>
        internal static CompileJob GetJob(string jobId)
        {
            if (string.IsNullOrWhiteSpace(jobId))
            {
                return null;
            }

            CompileJob jobToReturn;
            bool shouldPersist = false;
            bool reRequest = false;

            lock (LockObj)
            {
                if (!Jobs.TryGetValue(jobId, out var job))
                {
                    return null;
                }

                if (job.Status == CompileJobStatus.Running && _currentJobId == jobId)
                {
                    long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    bool compiling = IsCompilingNow();

                    if (compiling && job.Phase == PhaseCompileRequested)
                    {
                        // compilationStarted was missed — promote so finish handling works.
                        job.Phase = PhaseCompiling;
                        job.CompileStartedUnixMs ??= now;
                        job.LastUpdateUnixMs = now;
                        shouldPersist = true;
                    }
                    else if (!compiling)
                    {
                        switch (job.Phase)
                        {
                            case PhaseWaitingForCurrent:
                                // The compile we were waiting on ended (its finish event
                                // may have been lost to the reload) — request ours now.
                                job.Phase = PhaseCompileRequested;
                                job.RequestAttempts++;
                                job.LastUpdateUnixMs = now;
                                reRequest = true;
                                shouldPersist = true;
                                break;

                            case PhaseCompileRequested:
                                if (now - job.LastUpdateUnixMs > RequestRetryMs)
                                {
                                    if (job.RequestAttempts < MaxRequestAttempts)
                                    {
                                        // The request was likely swallowed by a reload — retry.
                                        job.RequestAttempts++;
                                        job.LastUpdateUnixMs = now;
                                        reRequest = true;
                                        shouldPersist = true;
                                    }
                                    else if (now - job.StartedUnixMs > CompileStartTimeoutMs)
                                    {
                                        McpLog.Warn($"[CompileJobManager] Job {jobId}: compilation never started, auto-failing");
                                        job.Error = "compile_did_not_start: compilation was requested but never began "
                                            + "(is the editor in Play mode with deferred recompilation?)";
                                        job.Status = CompileJobStatus.Failed;
                                        job.Phase = PhaseFinished;
                                        job.FinishedUnixMs = now;
                                        job.LastUpdateUnixMs = now;
                                        job.FinalizedBy = "poll_recovery";
                                        _currentJobId = null;
                                        shouldPersist = true;
                                    }
                                }
                                break;

                            case PhaseCompiling:
                                if (now - job.LastUpdateUnixMs > FinishGraceMs)
                                {
                                    // Compilation ended but the finish event was lost
                                    // (reload race). Finalize from captured state: errors
                                    // prevent the reload, so a clean capture means success.
                                    FinalizeLocked(job, now, "poll_recovery");
                                    shouldPersist = true;
                                }
                                break;
                        }
                    }
                }

                jobToReturn = job;
            }

            if (shouldPersist)
            {
                PersistToSessionState(force: true);
            }
            if (reRequest)
            {
                RequestCompilation();
            }
            return jobToReturn;
        }

        internal static object ToSerializable(CompileJob job)
        {
            if (job == null)
            {
                return null;
            }

            var errors = (job.Errors ?? new List<CompileJobError>())
                .Select(e => new { file = e?.File, line = e?.Line ?? 0, message = e?.Message })
                .ToArray();

            return new
            {
                job_id = job.JobId,
                status = job.Status.ToString().ToLowerInvariant(),
                phase = job.Phase,
                started_unix_ms = job.StartedUnixMs,
                compile_started_unix_ms = job.CompileStartedUnixMs,
                finished_unix_ms = job.FinishedUnixMs,
                last_update_unix_ms = job.LastUpdateUnixMs,
                duration_ms = job.FinishedUnixMs.HasValue
                    ? job.FinishedUnixMs.Value - job.StartedUnixMs
                    : (long?)null,
                errors,
                errors_total = job.ErrorsTotal,
                errors_capped = job.ErrorsTotal > errors.Length,
                warnings_count = job.WarningsCount,
                error = job.Error,
                finalized_by = job.FinalizedBy,
            };
        }

        // ── SessionState persistence ─────────────────────────────────────────

        private sealed class PersistedState
        {
            public string current_job_id { get; set; }
            public List<PersistedJob> jobs { get; set; }
        }

        private sealed class PersistedJob
        {
            public string job_id { get; set; }
            public string status { get; set; }
            public string phase { get; set; }
            public long started_unix_ms { get; set; }
            public long? compile_started_unix_ms { get; set; }
            public long? finished_unix_ms { get; set; }
            public long last_update_unix_ms { get; set; }
            public int request_attempts { get; set; }
            public List<CompileJobError> errors { get; set; }
            public int errors_total { get; set; }
            public int warnings_count { get; set; }
            public string error { get; set; }
            public string finalized_by { get; set; }
        }

        private static CompileJobStatus ParseStatus(string status)
        {
            if (string.IsNullOrWhiteSpace(status))
            {
                return CompileJobStatus.Running;
            }

            string s = status.Trim().ToLowerInvariant();
            return s switch
            {
                "succeeded" => CompileJobStatus.Succeeded,
                "failed" => CompileJobStatus.Failed,
                _ => CompileJobStatus.Running
            };
        }

        internal static void TryRestoreFromSessionState()
        {
            try
            {
                string json = SessionState.GetString(SessionKeyJobs, string.Empty);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return;
                }

                var state = JsonConvert.DeserializeObject<PersistedState>(json);
                if (state?.jobs == null)
                {
                    return;
                }

                lock (LockObj)
                {
                    Jobs.Clear();
                    foreach (var pj in state.jobs)
                    {
                        if (pj == null || string.IsNullOrWhiteSpace(pj.job_id))
                        {
                            continue;
                        }

                        Jobs[pj.job_id] = new CompileJob
                        {
                            JobId = pj.job_id,
                            Status = ParseStatus(pj.status),
                            Phase = pj.phase,
                            StartedUnixMs = pj.started_unix_ms,
                            CompileStartedUnixMs = pj.compile_started_unix_ms,
                            FinishedUnixMs = pj.finished_unix_ms,
                            LastUpdateUnixMs = pj.last_update_unix_ms,
                            RequestAttempts = pj.request_attempts,
                            Errors = pj.errors ?? new List<CompileJobError>(),
                            ErrorsTotal = pj.errors_total,
                            WarningsCount = pj.warnings_count,
                            Error = pj.error,
                            FinalizedBy = pj.finalized_by,
                        };
                    }

                    _currentJobId = string.IsNullOrWhiteSpace(state.current_job_id) ? null : state.current_job_id;
                    if (!string.IsNullOrEmpty(_currentJobId) && !Jobs.ContainsKey(_currentJobId))
                    {
                        _currentJobId = null;
                    }
                }
            }
            catch (Exception ex)
            {
                // Restoration is best-effort; never block editor load.
                McpLog.Warn($"[CompileJobManager] Failed to restore SessionState: {ex.Message}");
            }
        }

        internal static void PersistToSessionState(bool force = false)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // Throttle non-critical updates (per-assembly events during large compiles).
            if (!force && (now - _lastPersistUnixMs) < MinPersistIntervalMs)
            {
                return;
            }

            try
            {
                PersistedState snapshot;
                lock (LockObj)
                {
                    var jobs = Jobs.Values
                        .OrderByDescending(j => j.LastUpdateUnixMs)
                        .Take(MaxJobsToKeep)
                        .Select(j => new PersistedJob
                        {
                            job_id = j.JobId,
                            status = j.Status.ToString().ToLowerInvariant(),
                            phase = j.Phase,
                            started_unix_ms = j.StartedUnixMs,
                            compile_started_unix_ms = j.CompileStartedUnixMs,
                            finished_unix_ms = j.FinishedUnixMs,
                            last_update_unix_ms = j.LastUpdateUnixMs,
                            request_attempts = j.RequestAttempts,
                            errors = (j.Errors ?? new List<CompileJobError>()).Take(ErrorCap).ToList(),
                            errors_total = j.ErrorsTotal,
                            warnings_count = j.WarningsCount,
                            error = j.Error,
                            finalized_by = j.FinalizedBy,
                        })
                        .ToList();

                    snapshot = new PersistedState
                    {
                        current_job_id = _currentJobId,
                        jobs = jobs
                    };
                }

                SessionState.SetString(SessionKeyJobs, JsonConvert.SerializeObject(snapshot));
                _lastPersistUnixMs = now;
            }
            catch (Exception ex)
            {
                McpLog.Warn($"[CompileJobManager] Failed to persist SessionState: {ex.Message}");
            }
        }
    }
}
