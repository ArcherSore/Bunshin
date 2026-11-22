using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Gui.ChildSession;
using Gui.Infrastructure;
using ProjectModel;
using Protocol;

namespace Gui.Worker;

internal enum WorkerObservation
{
    WorkerNotStarted,
    WorkerStarting,
    Connected,
    IpcDisconnected,
    WorkerExited,
    WorkerRecoveryConflict,
    ChildSessionEnded
}

internal sealed record WorkerCoordinatorSnapshot(
    WorkerObservation Observation, bool SnapshotFresh,
    WorkerSnapshot? WorkerSnapshot, string Detail)
{
    internal bool Fresh => Observation == WorkerObservation.Connected && SnapshotFresh;
    internal bool Resynchronising => Observation == WorkerObservation.Connected && !SnapshotFresh;

    // A Worker may still be running across a disconnect or GUI restart.
    internal bool LiveAdmissionWithoutFreshSnapshot => !NoLiveAdmission && !SnapshotFresh;

    // Editing depends on whether a Run owns the configuration, regardless of Worker readiness.
    internal bool FreshIdle => Fresh && WorkerSnapshot is { ActiveRun: null, RunState: RunState.Idle };

    // Starting a Run also requires the Worker to be ready to execute it.
    internal bool FreshReadyToStart => FreshIdle && WorkerSnapshot is { WorkerState: WorkerState.Ready };

    // Scheduled cleanup needs a settled Worker and can proceed with unavailable dependencies.
    internal bool FreshIdleForEnvironmentEnd => FreshIdle && WorkerSnapshot is {
        WorkerState: WorkerState.Ready or WorkerState.NotReady
    };

    internal bool NoLiveAdmission => Observation is WorkerObservation.WorkerNotStarted
        or WorkerObservation.ChildSessionEnded or WorkerObservation.WorkerExited;

    internal static WorkerCoordinatorSnapshot Empty { get; } = new(
        WorkerObservation.WorkerNotStarted, false, null, "Worker 尚未启动");
}

internal sealed partial class WorkerCoordinator : IAsyncDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SnapshotRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan AdmissionTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan WorkerExitRecheckDelay = TimeSpan.FromSeconds(1);
    private const string EndChildSessionHint = "请从托盘菜单选择“结束桌面分身”后重新准备运行环境。";
    private readonly object _gate = new();
    private readonly AppLogger _logger;
    private readonly RunResultLog _runResults;
    private readonly WorkerStateLog _stateLog;
    private readonly WorkerLogRecovery _logRecovery;
    private readonly WorkerAdmissionStore _store;
    private readonly string _workerExecutablePath;
    private readonly string _workerImageName;
    private readonly string _pipeName;
    private readonly bool _usePipeAcl;
    private readonly Func<IReadOnlyList<WorkerProcessEntry>> _captureProcesses;
    private readonly Func<IReadOnlyCollection<uint>> _enumerateSessions;
    private readonly Func<uint, Guid, string, string, CancellationToken, Task<VerifiedChildSessionProcessLaunch>>
        _launchWorker;
    private readonly CancellationTokenSource _shutdown = new();
    private sealed record PendingRequest(string Operation, TaskCompletionSource<WireEnvelope> Completion)
    {
        internal GetSnapshotResponse? SnapshotResponse;
    }
    private readonly ConcurrentDictionary<Guid, PendingRequest> _pending = new();
    private readonly ConcurrentDictionary<Guid, byte> _abandonedRequests = new();
    private readonly TaskCompletionSource<bool> _serverReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _serverTask;
    private ProtocolConnection? _connection;
    private bool _refreshingSnapshot;
    // A full reply below a revision this connection already saw cannot restore Fresh.
    private long _observedRevision;
    private WorkerAdmissionRecord? _admission;
    private CancellationTokenSource? _exitWatch;
    // An unreadable record may still describe a live Worker, so it blocks Prepare until the session is ended.
    private bool _admissionUnreadable;
    private Guid? _launchingWorkerInstanceId;
    private TaskCompletionSource<WorkerSnapshot>? _awaitedFreshSnapshot;

    internal WorkerCoordinator(AppLogger logger, string stateDirectory, string workerExecutablePath)
        : this(logger, stateDirectory, workerExecutablePath, PipeIdentity.ForCurrentUser(), usePipeAcl: true)
    {
    }

    // The optional delegates are self-test seams; production always inspects and launches for real.
    internal WorkerCoordinator(
        AppLogger logger, string stateDirectory, string workerExecutablePath,
        string pipeName, bool usePipeAcl,
        Func<IReadOnlyList<WorkerProcessEntry>>? captureProcesses = null,
        Func<IReadOnlyCollection<uint>>? enumerateSessions = null,
        Func<uint, Guid, string, string, CancellationToken, Task<VerifiedChildSessionProcessLaunch>>?
            launchWorker = null, TimeProvider? requestClock = null, Func<Stream, Stream>? wrapPipe = null)
    {
        _logger = logger;
        _runResults = new RunResultLog(logger);
        _stateLog = new WorkerStateLog(logger);
        _logRecovery = new WorkerLogRecovery(_gate, logger,
            instanceId => _connection is not null && _admission?.WorkerInstanceId == instanceId,
            (request, token) => SendRequestAsync<LogGetSinceRequest, LogGetSinceResponse>(
                ProtocolOperations.LogGetSince, request, token),
            entry => LogReceived?.Invoke(this, entry));
        _store = new WorkerAdmissionStore(stateDirectory);
        _workerExecutablePath = Path.GetFullPath(workerExecutablePath);
        _workerImageName = Path.GetFileName(_workerExecutablePath);
        _pipeName = pipeName;
        _usePipeAcl = usePipeAcl;
        _requestClock = requestClock ?? TimeProvider.System;
        _wrapPipe = wrapPipe;
        _captureProcesses = captureProcesses ?? WorkerAdmissionInspection.CaptureProcesses;
        _enumerateSessions = enumerateSessions ?? WorkerAdmissionInspection.EnumerateSessionIds;
        var launcher = new ChildSessionWorkerLauncher(logger);
        _launchWorker = launchWorker ?? ((sessionId, instanceId, launchToken, manifestPath, cancellationToken) =>
            launcher.LaunchAsync(
                sessionId, _workerExecutablePath, instanceId, launchToken, manifestPath, cancellationToken));
        // Stale recovery runs before the pipe accepts connections; a kept record still lets its Worker reconnect.
        Snapshot = WorkerCoordinatorSnapshot.Empty;
        LoadAdmission();
        _stateLog.Observe(() => Snapshot);
        _serverTask =Task.Run(() => ServerLoopAsync(_shutdown.Token));
        _ = _serverTask.ContinueWith(
            task => _serverReady.TrySetException(task.Exception!.GetBaseException()),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    internal event EventHandler<WorkerCoordinatorSnapshot>? StateChanged;
    internal event EventHandler<WorkerLogEntry>? LogReceived;

    internal WorkerCoordinatorSnapshot Snapshot { get; private set; }

    // Scheduling can see a terminal before its dispatcher callback. Wait for the pipe reader to
    // enqueue that frame's lifecycle evidence before the gate decides to replace the old Run.
    internal WorkerCoordinatorSnapshot ReadPublishedSnapshot()
    {
        lock (_gate) return Snapshot;
    }

    // The user's current choice, which a Worker connected earlier may no longer hold.
    internal Func<MaaFrameworkOptions>? FrameworkOptions { get; set; }

    // The user's telemetry choice travels with the framework switches; the options a new Worker starts with have
    // already passed the release gate.
    internal Func<bool>? TelemetryEnabled { get; set; }
    internal Func<ProjectPlanModule, TelemetryOptions?>? TelemetryOptions { get; set; }

    internal int? TrackedWorkerPid => TrackedAdmission?.WorkerPid;

    internal WorkerAdmissionRecord? TrackedAdmission
    {
        get
        {
            lock (_gate) {
                return _admission;
            }
        }
    }

    internal Task WaitForServerReadyAsync(CancellationToken cancellationToken) =>
        _serverReady.Task.WaitAsync(cancellationToken);

    internal async Task<WorkerSnapshot> PrepareWorkerAsync(
        uint childSessionId, ProjectPlanModule project, CancellationToken cancellationToken = default,
        MaaFrameworkOptions? frameworkOptions = null)
    {
        await WaitForServerReadyAsync(cancellationToken);
        WorkerAdmissionRecord? existing;
        lock (_gate) {
            if (Snapshot.Fresh
                && Snapshot.WorkerSnapshot?.RuntimeProfileDigest == project.RuntimeProfileDigest) {
                return Snapshot.WorkerSnapshot;
            }
            if (_admissionUnreadable) {
                throw new InvalidOperationException(
                    $"无法读取 Worker Admission Record，为避免重复启动 Worker 已停止准备。{EndChildSessionHint}");
            }
            if (_connection is not null) {
                throw new InvalidOperationException(Snapshot.Fresh
                    ? $"当前 Worker 使用的项目与已加载的项目不一致。{EndChildSessionHint}"
                    : "Worker 正在同步状态，请稍后重试。");
            }
            existing = _admission;
        }
        if (existing is not null) {
            var (disposition, liveness) = ResolveAdmission(existing, AdmissionScenario.BeforeLaunch);
            if (disposition != AdmissionDisposition.Discarded) {
                throw CreateAdmissionRefusal(existing, liveness);
            }
            _logger.Info($"已证明 Worker instance={existing.WorkerInstanceId} 不再存在，已清理其 Admission Record。 ");
        }
        // Predictable failures and an occupied session are rejected before any Admission is written.
        ValidateWorkerExecutable();
        EnsureSessionHasNoWorker(childSessionId);

        var instanceId = Guid.NewGuid();
        var launchToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var manifest = project.CreateLaunchManifest(instanceId) with {
            FrameworkOptions = frameworkOptions ?? new MaaFrameworkOptions(),
            Telemetry = TelemetryOptions?.Invoke(project)
        };
        var manifestPath = _store.GetManifestPath(instanceId);
        var admission = new WorkerAdmissionRecord(
            instanceId, launchToken, childSessionId, null,
            project.RuntimeProfileDigest, DateTime.UtcNow);
        try {
            _store.SaveManifest(manifest);
            _store.SaveRecord(admission);
        } catch {
            _store.DeleteManifest(instanceId);
            throw;
        }

        TaskCompletionSource<WorkerSnapshot> fresh;
        lock (_gate) {
            _admission = admission;
            _launchingWorkerInstanceId = instanceId;
            fresh = new TaskCompletionSource<WorkerSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            _awaitedFreshSnapshot = fresh;
            Snapshot = new WorkerCoordinatorSnapshot(
                WorkerObservation.WorkerStarting,
                false,
                null,
                "Pending Admission 已写入，正在启动 Worker");
        }
        RaiseStateChanged();

        try {
            VerifiedChildSessionProcessLaunch launch;
            try {
                launch = await _launchWorker(childSessionId, instanceId, launchToken, manifestPath, cancellationToken);
            } finally {
                lock (_gate) {
                    _launchingWorkerInstanceId = null;
                }
            }
            RecordVerifiedWorkerProcess(instanceId, launch);
        } catch (Exception exception) {
            WorkerAdmissionRecord? current;
            lock (_gate) {
                current = _admission?.WorkerInstanceId == instanceId ? _admission : null;
            }
            // RunEx may already have been submitted, so a launch exception alone does not prove the Worker absent.
            switch (ResolveAdmission(current, AdmissionScenario.LaunchFailure).Disposition) {
                case AdmissionDisposition.KeptAliveForReconnect:
                    _logger.Warn(
                        "Task Scheduler 进程枚举验证失败，但同一 Worker 已通过 Pipe PID/Session/映像校验；"
                        + "继续等待 fresh Snapshot。",
                        exception);
                    break;
                case AdmissionDisposition.Discarded:
                    _logger.Warn("Worker 启动失败，且已证明没有存活的 Worker；已回滚 worker.json 与 launch manifest。 ");
                    throw;
                case AdmissionDisposition.KeptUnproven:
                    _logger.Warn("Worker 启动未确认，且无法证明 Worker 不会出现；保留 Admission，结束桌面分身前不再启动。 ");
                    throw;
                default:
                    throw;
            }
        }

        try {
            var snapshot = await fresh.Task.WaitAsync(AdmissionTimeout, cancellationToken);
            _store.DeleteManifest(instanceId);
            return snapshot;
        } catch (TimeoutException exception) {
            WorkerAdmissionRecord? timedOutAdmission = null;
            WorkerSnapshot? lateSnapshot = null;
            lock (_gate) {
                if (Snapshot is {
                    Fresh: true,
                    WorkerSnapshot: { WorkerInstanceId: var snapshotInstance } workerSnapshot
                }
                    && snapshotInstance == instanceId) {
                    lateSnapshot = workerSnapshot;
                } else if (_admission?.WorkerInstanceId == instanceId) {
                    timedOutAdmission = _admission;
                    _awaitedFreshSnapshot = null;
                }
            }

            if (lateSnapshot is not null) {
                _store.DeleteManifest(instanceId);
                _logger.Info("Worker fresh Snapshot 在 admission 超时边界完成；按成功处理。 ");
                return lateSnapshot;
            }

            var timeout = $"Worker admission 在 {AdmissionTimeout.TotalSeconds:0} 秒后超时；";
            var disposition = ResolveAdmission(timedOutAdmission, AdmissionScenario.SnapshotTimeout).Disposition;
            string detail;
            if (disposition == AdmissionDisposition.AlreadyCleaned) {
                detail = "Admission 已由 Child Session 生命周期清理。 ";
            } else if (disposition == AdmissionDisposition.Discarded) {
                _logger.Warn($"{timeout}已证明没有存活的 Worker，已回滚 worker.json 与 launch manifest。 ");
                detail = "未发现存活的已验证 Worker，Pending Admission 已自动回滚。 ";
            } else if (disposition == AdmissionDisposition.KeptAliveForReconnect) {
                _logger.Warn(
                    $"{timeout}PID={timedOutAdmission!.WorkerPid}、SessionId={timedOutAdmission.ChildSessionId} 仍存活，"
                    + "保留 Admission 供 Worker 重连。 ");
                detail = "已验证 Worker 仍存活，Admission 已保留供重连。 ";
            } else {
                _logger.Warn($"{timeout}无法确认 Worker 是否仍在运行，保留 Admission。 ");
                detail = $"无法确认 Worker 是否仍在运行，Admission 已保留。{EndChildSessionHint}";
            }
            throw new TimeoutException(
                $"Worker 未在 {AdmissionTimeout.TotalSeconds:0} 秒内完成 admission + fresh Snapshot；"
                + detail,
                exception);
        }
    }

    private enum AdmissionScenario { BeforeLaunch, LaunchFailure, SnapshotTimeout }
    private enum AdmissionDisposition { Discarded, KeptAliveForReconnect, KeptUnproven, AlreadyCleaned }

    private (AdmissionDisposition Disposition, AdmissionLiveness? Liveness) ResolveAdmission(
        WorkerAdmissionRecord? record, AdmissionScenario scenario)
    {
        if (record is null) {
            return (AdmissionDisposition.AlreadyCleaned, null);
        }

        var liveness = ClassifyAdmission(record);
        if (liveness == AdmissionLiveness.Stale) {
            var detail = scenario switch {
                AdmissionScenario.BeforeLaunch => "上次的 Worker 已不存在，Admission Record 已清理",
                AdmissionScenario.LaunchFailure => "Worker 启动失败且已证明没有存活的 Worker；Admission 已回滚",
                AdmissionScenario.SnapshotTimeout => "Worker admission 超时且没有存活的已验证进程；Pending Admission 已回滚",
                _ => throw new ArgumentOutOfRangeException(nameof(scenario))
            };
            if (TryDiscardStaleAdmission(record, detail)) {
                return (AdmissionDisposition.Discarded, liveness);
            }
        }

        if (liveness == AdmissionLiveness.Alive
            && (scenario != AdmissionScenario.LaunchFailure || record.WorkerPid is not null)) {
            if (scenario == AdmissionScenario.SnapshotTimeout) {
                SetDisconnectedObservation(record, WorkerObservation.IpcDisconnected,
                    $"Worker PID {record.WorkerPid} 仍存活，但未按时完成 admission + fresh Snapshot");
            }
            return (AdmissionDisposition.KeptAliveForReconnect, liveness);
        }

        if (scenario != AdmissionScenario.BeforeLaunch) {
            KeepUnprovenAdmission(record, scenario == AdmissionScenario.LaunchFailure
                ? "Worker 启动未确认；为避免重复启动，Admission 已保留"
                : "Worker admission 超时，且无法确认 Worker 是否仍在运行");
        }
        return (AdmissionDisposition.KeptUnproven, liveness);
    }

    private void RecordVerifiedWorkerProcess(Guid workerInstanceId, VerifiedChildSessionProcessLaunch launch)
    {
        var workerPid = checked((int)launch.ProcessId);
        WorkerAdmissionRecord? recordToPersist = null;
        var changed = false;
        lock (_gate) {
            var current = _admission
                          ?? throw new InvalidOperationException("Worker 进程启动后 Admission Record 已不存在。 ");
            if (current.WorkerInstanceId != workerInstanceId) {
                throw new InvalidOperationException("Worker 进程启动后 Admission instance 已发生变化。 ");
            }
            if (current.ChildSessionId != launch.SessionId) {
                throw new InvalidOperationException("Worker 启动验证返回了错误的 Child Session。 ");
            }
            if (current.WorkerPid is int admittedPid && admittedPid != workerPid) {
                throw new InvalidOperationException(
                    $"Task Scheduler 验证 PID={workerPid}，但 Pipe Admission PID={admittedPid}。 ");
            }
            if (current.WorkerPid is null) {
                current = current with { WorkerPid = workerPid };
                _admission = current;
                recordToPersist = current;
                changed = true;
            }
            if (Snapshot.Observation != WorkerObservation.Connected) {
                Snapshot = new WorkerCoordinatorSnapshot(
                    WorkerObservation.WorkerStarting, false, Snapshot.WorkerSnapshot,
                    $"Worker 进程已验证：PID={workerPid}，SessionId={launch.SessionId}；等待 admission + fresh Snapshot");
                changed = true;
            }
        }

        if (recordToPersist is not null) {
            try {
                _store.SaveRecord(recordToPersist);
            } catch (Exception exception) {
                _logger.Warn(
                    $"Worker PID={workerPid} 已在内存中验证，但写回 Admission Record 失败；"
                    + "Pipe admission 仍将继续。 ",
                    exception);
            }
        }
        if (changed) {
            RaiseStateChanged();
        }
    }

    // Admission supplies a fresh snapshot, which may still report Starting during dependency checks.
    // A Run must wait for Ready from that same Worker, never for a fixed delay or a GUI notification.
    internal async Task WaitForReadyAsync(CancellationToken cancellationToken = default)
    {
        Guid? instanceId;
        lock (_gate) {
            instanceId = Snapshot.WorkerSnapshot?.WorkerInstanceId;
        }
        if (instanceId is null) {
            throw new InvalidOperationException("Worker 尚未完成接纳，无法等待就绪。");
        }
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check(object? sender, WorkerCoordinatorSnapshot ignored)
        {
            WorkerCoordinatorSnapshot observed;
            lock (_gate) {
                observed = Snapshot;
            }
            var worker = observed.WorkerSnapshot;
            if (!observed.Fresh || worker?.WorkerInstanceId != instanceId) {
                ready.TrySetException(new InvalidOperationException("等待就绪期间 Worker 连接或身份已改变。"));
            } else if (worker.WorkerState == WorkerState.Ready) {
                ready.TrySetResult();
            } else if (worker.WorkerState != WorkerState.Starting) {
                ready.TrySetException(new InvalidOperationException(
                    $"Worker 未能就绪：{worker.WorkerReason?.Message ?? worker.WorkerState.ToString()}"));
            }
        }
        StateChanged += Check;
        try {
            // Subscribe before reading again so Ready cannot be lost between the read and subscription.
            Check(this, Snapshot);
            using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
            await ready.Task.WaitAsync(AdmissionTimeout, cancelled.Token);
        } catch (TimeoutException exception) {
            throw new TimeoutException("Worker 依赖检查未在 60 秒内完成，任务尚未启动。", exception);
        } finally {
            StateChanged -= Check;
        }
    }

    internal async Task<RunStopResponse> StopRunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        return await SendRequestAsync<RunStopRequest, RunStopResponse>(
            ProtocolOperations.RunStop,
            new RunStopRequest(runId),
            cancellationToken);
    }

    // Without a connection there is nothing to update: every new connection synchronises the current choice.
    internal async Task ApplyFrameworkOptionsAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate) {
            if (_connection is null) {
                return;
            }
        }
        if (FrameworkOptions is { } current) {
            await SendRequestAsync<FrameworkSetOptionsRequest, FrameworkSetOptionsResponse>(
                ProtocolOperations.FrameworkSetOptions,
                new FrameworkSetOptionsRequest(current(), TelemetryEnabled?.Invoke() ?? false), cancellationToken);
        }
    }

    internal void ChildSessionEnded(WorkerAdmissionRecord? confirmedExit = null)
    {
        WorkerAdmissionRecord? record;
        lock (_logRecovery.DispatchGate) {
            lock (_gate) {
                record = _admission;
                CancelExitWatchLocked();
                if (_connection is { } connection) {
                    connection.Close(new IOException("Worker Admission 已摘除。"));
                    FinishEvidenceLocked(connection);
                }
                if (record is not null) {
                    RemoveAdmissionLocked(record, confirmedExit == record);
                }
                _admissionUnreadable = false;
                _connection = null;
                _awaitedFreshSnapshot = null;
                _logRecovery.InvalidateConnectionLocked();
                Snapshot = new WorkerCoordinatorSnapshot(
                    WorkerObservation.ChildSessionEnded, false, Snapshot.WorkerSnapshot,
                    "Child Session 已结束");
            }
        }
        if (record is not null) {
            _store.DeleteManifest(record.WorkerInstanceId);
        }
        _store.DeleteRecord();
        RaiseStateChanged();
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        CancelPendingRequests();
        try {
            await _serverTask;
        } catch (OperationCanceledException) {
        }
        _shutdown.Dispose();
    }

    private async Task ServerLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested) {
            await using var server = CreatePipeServer();
            _serverReady.TrySetResult(true);
            try {
                await server.WaitForConnectionAsync(cancellationToken);
                await ServeConnectionAsync(server, cancellationToken);
            } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                return;
            } catch (Exception exception) when (exception is IOException
                                                     or ProtocolException
                                                     or UnauthorizedAccessException
                                                     or InvalidOperationException) {
                _logger.Warn($"Worker IPC connection 结束：{exception.GetBaseException().Message}");
            } catch (Exception exception) {
                // An admitted Worker's unexpected failure ends its connection, never the long-lived server.
                _logger.Error("Worker IPC connection 异常结束。", exception);
            }
        }
    }

    private NamedPipeServerStream CreatePipeServer()
    {
        if (!_usePipeAcl) {
            return new NamedPipeServerStream(
                _pipeName, PipeDirection.InOut,
                maxNumberOfServerInstances: 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                inBufferSize: 64 * 1024, outBufferSize: 64 * 1024);
        }

        using var identity = WindowsIdentity.GetCurrent();
        var userSid = identity.User ?? throw new InvalidOperationException("无法取得当前 Windows 用户 SID。 ");
        var networkSid = new SecurityIdentifier(WellKnownSidType.NetworkSid, null);
        var security = new PipeSecurity();
        security.SetOwner(userSid);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(networkSid, PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(userSid, PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(
            _pipeName, PipeDirection.InOut, maxNumberOfServerInstances: 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, inBufferSize: 64 * 1024, outBufferSize: 64 * 1024, security);
    }

    private async Task ServeConnectionAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
    {
        await using var connection = new ProtocolConnection(_wrapPipe?.Invoke(server) ?? server);
        if (await AdmitConnectionAsync(server, connection, cancellationToken) is not { } admitted) {
            return;
        }
        var (admission, logRecoveryGeneration) = admitted;
        RaiseStateChanged();

        using var connectionLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = connectionLifetime.Token;
        ConnectionReader = ReadWorkerAsync();
        var initialSync = SynchroniseWorkerAsync();
        try {
            if (await connection.Closed.WaitAsync(cancellationToken) is { } failure) {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
            }
        } finally {
            connection.Close(cancellationToken.IsCancellationRequested ? new OperationCanceledException(cancellationToken) : null);
            connectionLifetime.Cancel();
            // A complete first-sync log page can still be publishing after EOF.
            // Cancel further requests, then let that page finish before invalidating its generation.
            await initialSync;
            WorkerAdmissionRecord? detached = null;
            lock (_logRecovery.DispatchGate) {
                lock (_gate) {
                    if (ReferenceEquals(_connection, connection)) {
                        FinishEvidenceLocked(connection);
                        _connection = null;
                        _logRecovery.InvalidateConnectionLocked();
                        detached = _admission;
                    }
                }
            }
            CancelPendingRequests();
            // A killed Worker ends the pipe with a plain EOF, so every end of an admitted connection is observed here.
            // It runs after detaching so ClassifyAdmission inspects the recorded PID instead of trusting the pipe.
            if (detached is not null && !cancellationToken.IsCancellationRequested) {
                MarkDisconnected(detached);
            }
        }

        async Task ReadWorkerAsync()
        {
            try {
                await ReadLoopAsync(connection, admission, logRecoveryGeneration, token);
                connection.Close();
            } catch (Exception error) {
                connection.Close(error);
            }
        }

        async Task SynchroniseWorkerAsync()
        {
            try {
                var response = await SendRequestAsync<object, GetSnapshotResponse>(
                    ProtocolOperations.WorkerGetSnapshot, new { }, token, connection: connection);
                lock (_gate) {
                    if (_connection == connection) {
                        _initialSnapshotSync!.TrySetResult(true);
                        _initialSnapshotSync = null;
                    }
                }
                _ = SynchroniseFrameworkOptionsAsync(token);
                await _logRecovery.EnsureLogRecoveryAsync(admission.WorkerInstanceId, logRecoveryGeneration,
                    response.Snapshot.LastLogSequence, token);
            } catch (Exception error) {
                connection.Close(error);
            }
        }
    }

    internal Task ConnectionReader { get; private set; } = Task.CompletedTask;

    // A Worker that outlived the GUI still holds the options of its launch. This runs beside log recovery, and its
    // failure ends nothing: debug records are not worth a connection, and SendRequestAsync has logged it.
    private async Task SynchroniseFrameworkOptionsAsync(CancellationToken cancellationToken)
    {
        try {
            await ApplyFrameworkOptionsAsync(cancellationToken);
        } catch (Exception) {
            // Nothing observes this task; the next connection or switch change tries again.
        }
    }

    // Returns null after rejecting the client. Any local process can open the pipe, so a rejection ends only this
    // connection and leaves the Admission and the observed state untouched.
    private async Task<(WorkerAdmissionRecord Admission, int LogRecoveryGeneration)?> AdmitConnectionAsync(
        NamedPipeServerStream server, ProtocolConnection connection, CancellationToken cancellationToken)
    {
        try {
            var open = await connection.ReadAsync(cancellationToken)
                       ?? throw new EndOfStreamException("Worker 未发送 connection.open。 ");
            var requestId = open.RequestId
                            ?? throw new ProtocolException("connection.open 缺少 requestId。 ");
            if (open.ProtocolVersion != ProtocolConstants.ProtocolVersion
                || open.MessageType != ProtocolMessageTypes.Request
                || open.Operation != ProtocolOperations.ConnectionOpen) {
                await connection.WriteAsync(
                    WireEnvelope.Failure(
                        ProtocolOperations.ConnectionOpen, requestId, "protocol_version_mismatch",
                        "connection.open envelope 不兼容。 "),
                    cancellationToken);
                return null;
            }

            ConnectionOpenRequest payload;
            try {
                payload = ProtocolJson.Deserialize<ConnectionOpenRequest>(open.Data);
            } catch (JsonException) {
                await connection.WriteAsync(
                    WireEnvelope.Failure(
                        ProtocolOperations.ConnectionOpen, requestId, "invalid_request", "connection.open 数据无效。 "),
                    cancellationToken);
                throw;
            }
            var clientPid = GetClientPid(server);
            var admission = ValidateAdmission(payload, clientPid);
            // Validation ran outside the gate; a record removed or replaced meanwhile is never acknowledged or revived.
            lock (_gate) {
                _ = RequireCurrentAdmissionLocked(admission, clientPid);
            }
            await connection.WriteAsync(
                WireEnvelope.Response(
                    ProtocolOperations.ConnectionOpen, requestId,
                    new { }),
                cancellationToken);

            lock (_logRecovery.DispatchGate) {
                lock (_gate) {
                    admission = RequireCurrentAdmissionLocked(admission, clientPid);
                    if (admission.WorkerPid != clientPid) {
                        admission = admission with { WorkerPid = clientPid };
                        _admission = admission;
                        _store.SaveRecord(admission);
                    }
                    _connection = connection;
                    _refreshingSnapshot = false;
                    _observedRevision = 0;
                    CancelExitWatchLocked();
                    BeginEvidenceLocked(admission);
                    var logRecoveryGeneration = _logRecovery.ConnectionAdmittedLocked(admission.WorkerInstanceId);
                    Snapshot = new WorkerCoordinatorSnapshot(
                        WorkerObservation.Connected, false, Snapshot.WorkerSnapshot,
                        "Worker 已接纳，正在同步 Snapshot");
                    return (admission, logRecoveryGeneration);
                }
            }
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            throw;
        } catch (Exception exception) {
            _logger.Warn($"已拒绝 Worker 连接：{exception.GetBaseException().Message}");
            return null;
        }
    }

    private async Task ReadLoopAsync(
        ProtocolConnection connection, WorkerAdmissionRecord admission,
        int logRecoveryGeneration, CancellationToken cancellationToken)
    {
        var workerInstanceId = admission.WorkerInstanceId;
        while (!connection.IsClosed && !cancellationToken.IsCancellationRequested) {
            var envelope = await connection.ReadAsync(cancellationToken);
            if (envelope is null) {
                _logger.Info($"Worker IPC 已关闭（EOF）{AppLogger.Field("instance", workerInstanceId)}");
                return;
            }
            if (envelope.MessageType == ProtocolMessageTypes.Response && envelope.RequestId is Guid requestId) {
                bool stateChanged;
                lock (_gate) {
                    if (connection != _connection || connection.IsClosed) {
                        return;
                    }
                    if (!ReceiveEvidenceReplyLocked(requestId, envelope, admission, connection, out stateChanged)) {
                        stateChanged = CompleteRequestLocked(requestId, envelope, admission);
                    }
                }
                if (stateChanged) {
                    RaiseStateChanged();
                }
                continue;
            }
            if (envelope.MessageType != ProtocolMessageTypes.Event) {
                throw new ProtocolException("GUI 收到无法关联的非 Event envelope。 ");
            }
            switch (envelope.Operation) {
                case ProtocolOperations.WorkerStateChanged:
                case ProtocolOperations.RunStateChanged: {
                        var state = ProtocolJson.Deserialize<StateChangedEvent>(envelope.Data);
                        if (state.WorkerInstanceId != workerInstanceId) {
                            throw new ProtocolException("stateChanged workerInstanceId 不匹配。 ");
                        }
                        ApplyStateEvent(state, envelope.ProtocolVersion, connection, cancellationToken);
                        break;
                    }
                case ProtocolOperations.LogEntry: {
                        var log = ProtocolJson.Deserialize<LogEntryEvent>(envelope.Data);
                        if (log.WorkerInstanceId != workerInstanceId) {
                            throw new ProtocolException("log.entry workerInstanceId 不匹配。 ");
                        }
                        _logRecovery.ApplyLiveLog(workerInstanceId, logRecoveryGeneration, log.Entry, cancellationToken);
                        break;
                    }
            }
        }
    }

    private WorkerAdmissionRecord ValidateAdmission(ConnectionOpenRequest request, int clientPid)
    {
        WorkerAdmissionRecord admission;
        lock (_gate) {
            admission = _admission
                        ?? throw new UnauthorizedAccessException("没有 Pending/valid Admission Record。 ");
        }
        byte[] receivedToken;
        try {
            receivedToken = Convert.FromHexString(request.LaunchToken);
        } catch (Exception exception) when (exception is FormatException or ArgumentNullException) {
            throw new UnauthorizedAccessException("Worker Launch Token 格式非法。 ", exception);
        }
        var expectedToken = Convert.FromHexString(admission.LaunchToken);
        if (request.WorkerInstanceId != admission.WorkerInstanceId || !CryptographicOperations.FixedTimeEquals(receivedToken, expectedToken)
            || request.RuntimeProfileDigest != admission.RuntimeProfileDigest) {
            throw new UnauthorizedAccessException("Worker identity/token/runtime profile 不匹配。 ");
        }
        if (admission.WorkerPid is not null && admission.WorkerPid != clientPid) {
            throw new UnauthorizedAccessException("Pipe client PID 与 Admission Record 不匹配。 ");
        }
        uint sessionId;
        string? imagePath;
        try {
            using var process = Process.GetProcessById(clientPid);
            sessionId = (uint)process.SessionId;
            imagePath = process.MainModule?.FileName;
        } catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
            or Win32Exception or NotSupportedException) {
            // A client that exited or cannot be inspected has no verifiable identity.
            throw new UnauthorizedAccessException($"无法核验 Worker 进程身份：PID={clientPid}。 ", exception);
        }
        if (sessionId != admission.ChildSessionId) {
            throw new UnauthorizedAccessException("Worker 真实 SessionId 不匹配。 ");
        }
        if (imagePath is null) {
            throw new UnauthorizedAccessException("无法取得 Worker 映像路径。 ");
        }
        if (!string.Equals(Path.GetFullPath(imagePath), _workerExecutablePath, StringComparison.OrdinalIgnoreCase)) {
            throw new UnauthorizedAccessException($"Worker 映像不来自当前 {AppIdentity.Name} 发布包。 ");
        }
        return admission;
    }

    // Apply a validated full reply on the reader before it can observe the next frame or EOF.
    private bool CompleteRequestLocked(Guid id, WireEnvelope frame, WorkerAdmissionRecord admission)
    {
        if (!_pending.TryRemove(id, out var pending)) {
            if (_abandonedRequests.TryRemove(id, out _)) {
                return false;
            }
            throw new ProtocolException("GUI 收到无法关联的响应。 ");
        }
        try {
            var changed = false;
            if (pending.Operation == ProtocolOperations.WorkerGetSnapshot && frame.Success == true) {
                if (ObservingEvidence && (frame.Operation != pending.Operation || frame.ProtocolVersion != ProtocolConstants.ProtocolVersion)) {
                    throw new ProtocolException("getSnapshot 响应的操作或协议版本不匹配。");
                }
                var response = ProtocolJson.Deserialize<GetSnapshotResponse>(frame.Data);
                changed = ApplyFreshSnapshotLocked(response.Snapshot, admission);
                PublishLocked(SnapshotBatchLocked(response.Snapshot));
                pending.SnapshotResponse = response;
            }
            pending.Completion.TrySetResult(frame);
            return changed;
        } catch (Exception error) {
            pending.Completion.TrySetException(error);
            throw;
        }
    }

    private bool ApplyFreshSnapshotLocked(WorkerSnapshot snapshot, WorkerAdmissionRecord admission)
    {
        if (ObservingEvidence) {
            ValidateEvidenceSnapshot(snapshot);
        }
        ValidateSnapshot(snapshot, admission);
        var older = Snapshot.WorkerSnapshot is { } current && current.WorkerInstanceId == snapshot.WorkerInstanceId
            && current.StateRevision > snapshot.StateRevision || snapshot.StateRevision < _observedRevision;
        if (!older) {
            Snapshot = new WorkerCoordinatorSnapshot(WorkerObservation.Connected, true, snapshot,
                "Worker Snapshot 已同步");
        }
        if (Snapshot.Fresh) {
            _awaitedFreshSnapshot?.TrySetResult(Snapshot.WorkerSnapshot!);
            _awaitedFreshSnapshot = null;
        }
        return !older;
    }

    private void ApplyStateEvent(StateChangedEvent state, int protocolVersion, ProtocolConnection connection,
        CancellationToken connectionToken)
    {
        WorkerAdmissionRecord admission;
        long? revisionGapFrom = null;
        lock (_gate) {
            if (connection != _connection || connection.IsClosed) {
                return;
            }
            admission = _admission
                        ?? throw new ProtocolException("没有 Admission Record 却收到 state event。 ");
            if (ObservingEvidence) {
                if (protocolVersion != ProtocolConstants.ProtocolVersion || state.Snapshot?.StateRevision != state.StateRevision) {
                    throw new ProtocolException("stateChanged 的版本或修订与快照不匹配。");
                }
                ValidateEvidenceSnapshot(state.Snapshot);
                ValidateSnapshot(state.Snapshot, admission);
            }
            var currentRevision = Snapshot.WorkerSnapshot?.StateRevision ?? 0;
            // Same rule as full replies: a frame below the highest revision seen here cannot restore Fresh.
            if (state.StateRevision <= currentRevision || state.StateRevision < _observedRevision) {
                PublishLocked(SnapshotBatchLocked(state.Snapshot));
                return;
            }
            _observedRevision = state.StateRevision;
            if (Snapshot.Fresh && state.StateRevision != currentRevision + 1) {
                Snapshot = Snapshot with {
                    SnapshotFresh = false,
                    Detail = "stateRevision 断档，等待完整 Snapshot"
                };
                if (!_refreshingSnapshot) {
                    _refreshingSnapshot = true;
                    _ = Task.Run(() => RefreshSnapshotAsync(connection, connectionToken));
                }
                revisionGapFrom = currentRevision;
            } else {
                ValidateSnapshot(state.Snapshot, admission);
                Snapshot = new WorkerCoordinatorSnapshot(
                    WorkerObservation.Connected, true, state.Snapshot,
                    "Worker 状态已更新");
            }
            PublishLocked(SnapshotBatchLocked(state.Snapshot));
        }
        if (revisionGapFrom is long from) {
            // The Worker keeps only its newest state event, so a gap is routine and a full Snapshot closes it.
            _logger.Debug($"Worker 状态断档，正在取完整 Snapshot{AppLogger.Field("from", from)}"
                + AppLogger.Field("received", state.StateRevision));
            return;
        }
        RaiseStateChanged();
    }

    private async Task RefreshSnapshotAsync(ProtocolConnection connection, CancellationToken connectionToken)
    {
        try {
            while (!connectionToken.IsCancellationRequested) {
                lock (_gate) {
                    if (_connection != connection || connection.IsClosed) return;
                    if (Snapshot.Fresh) {
                        // Release under the same lock that starts recovery on the next gap.
                        _refreshingSnapshot = false;
                        return;
                    }
                }
                try {
                    await SendRequestAsync<object, GetSnapshotResponse>(
                        ProtocolOperations.WorkerGetSnapshot, new { }, connectionToken, connection);
                } catch (TimeoutException) {
                    await Task.Delay(SnapshotRetryDelay, _requestClock, connectionToken);
                }
            }
        } catch (OperationCanceledException) when (connectionToken.IsCancellationRequested) {
        } catch (Exception exception) {
            connection.Close(exception);
        }
    }

    private Task<TResponse> SendRequestAsync<TRequest, TResponse>(
        string operation, TRequest data, CancellationToken cancellationToken, ProtocolConnection? connection = null) =>
        TraceRequestAsync(operation, () => SendRequestCoreAsync<TRequest, TResponse>(
            operation, data, cancellationToken, connection));

    private async Task<TResponse> TraceRequestAsync<TResponse>(string operation, Func<Task<TResponse>> send)
    {
        var started = Stopwatch.GetTimestamp();
        string Details() => AppLogger.Field("operation", operation)
            + AppLogger.Field("cost", $"{Stopwatch.GetElapsedTime(started).TotalMilliseconds:0}ms");
        try {
            var response = await send().ConfigureAwait(false);
            // Log recovery pages can be frequent, and the logs they carry show their own effect.
            if (operation != ProtocolOperations.LogGetSince) {
                _logger.Debug($"Worker 请求完成{Details()}");
            }
            return response;
        } catch (OperationCanceledException) {
            _logger.Debug($"Worker 请求已取消{Details()}");
            throw;
        } catch (Exception exception) {
            // A timeout wraps the cancellation it came from; its own message is the reason.
            var reason = exception is TimeoutException ? exception.Message : exception.GetBaseException().Message;
            _logger.Warn($"Worker 请求失败{Details()}{AppLogger.Field("error", reason)}");
            throw;
        }
    }

    private async Task<TResponse> SendRequestCoreAsync<TRequest, TResponse>(
        string operation, TRequest data, CancellationToken cancellationToken, ProtocolConnection? connection)
    {
        var requestId = Guid.NewGuid();
        var completion = new TaskCompletionSource<WireEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingRequest(operation, completion);
        lock (_gate) {
            connection ??= _connection
                         ?? throw new InvalidOperationException("Worker IPC 尚未连接。 ");
            if (!_pending.TryAdd(requestId, pending)) {
                throw new InvalidOperationException("无法登记 IPC requestId。 ");
            }
        }
        try {
            await WriteRequestAsync(connection, WireEnvelope.Request(operation, requestId, data), cancellationToken, null);
            var response = await completion.Task.WaitAsync(RequestTimeout, _requestClock, cancellationToken);
            if (response.Success != true) {
                throw new WorkerProtocolErrorException(
                    response.Error?.Code ?? "internal_error", response.Error?.Message ?? "Worker 返回未知错误。 ");
            }
            if (pending.SnapshotResponse is TResponse snapshot) {
                return snapshot;
            }
            return ProtocolJson.Deserialize<TResponse>(response.Data);
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            AbandonPendingRequest(requestId);
            throw;
        } catch (TimeoutException) {
            AbandonPendingRequest(requestId);
            throw;
        } catch {
            _pending.TryRemove(requestId, out _);
            throw;
        }
    }

    private static async Task<bool> WriteRequestAsync(ProtocolConnection connection, WireEnvelope request,
        CancellationToken cancellationToken, Action? onSending, Func<bool>? canWrite = null,
        CancellationToken? frameCancellationToken = null)
    {
        try {
            return await connection.WriteAsync(request, cancellationToken, onSending, canWrite, frameCancellationToken ?? cancellationToken);
        } catch (Exception error) when ((connection.WriteFailure ?? error as IOException) is { } failure) {
            connection.Close(failure);
            throw;
        }
    }

    private void AbandonPendingRequest(Guid requestId)
    {
        _abandonedRequests.TryAdd(requestId, 0);
        if (!_pending.TryRemove(requestId, out _)) {
            _abandonedRequests.TryRemove(requestId, out _);
            return;
        }
        _ = ExpireAbandonedRequestAsync(requestId);
    }

    private async Task ExpireAbandonedRequestAsync(Guid requestId)
    {
        try {
            await Task.Delay(RequestTimeout, _requestClock, _shutdown.Token).ConfigureAwait(false);
        } catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) {
        } finally {
            _abandonedRequests.TryRemove(requestId, out _);
        }
    }

    private void CancelPendingRequests()
    {
        foreach (var pair in _pending) {
            if (_pending.TryRemove(pair.Key, out var pending)) {
                pending.Completion.TrySetCanceled();
            }
        }
        _abandonedRequests.Clear();
    }

    private void ValidateSnapshot(WorkerSnapshot snapshot, WorkerAdmissionRecord admission)
    {
        if (snapshot.SnapshotVersion != ProtocolConstants.SnapshotVersion
            || snapshot.ProtocolVersion != ProtocolConstants.ProtocolVersion
            || snapshot.WorkerInstanceId != admission.WorkerInstanceId || snapshot.WorkerPid != admission.WorkerPid
            || snapshot.ChildSessionId != admission.ChildSessionId || snapshot.RuntimeProfileDigest != admission.RuntimeProfileDigest) {
            throw new ProtocolException("Worker Snapshot identity/schema 与 Admission Record 不一致。 ");
        }
        if ((snapshot.ActiveRun is null) != (snapshot.RunState == RunState.Idle)) {
            throw new ProtocolException("Worker Snapshot activeRun/runState invariant 失败。 ");
        }
        if (snapshot.ActiveRun is not null && snapshot.ActiveRun.State != snapshot.RunState) {
            throw new ProtocolException("Worker Snapshot activeRun.state 与 runState 不一致。 ");
        }
    }

    // Presentation only: a disconnect never deletes the Admission, and it is dropped once the record is replaced.
    private void MarkDisconnected(WorkerAdmissionRecord admission)
    {
        if (admission.WorkerPid is null) {
            SetDisconnectedObservation(admission, WorkerObservation.WorkerStarting, "等待 Worker 连接");
            return;
        }
        var liveness = ClassifyAdmission(admission);
        SetDisconnectedObservation(admission,
            liveness == AdmissionLiveness.Stale ? WorkerObservation.WorkerExited : WorkerObservation.IpcDisconnected,
            liveness switch {
                AdmissionLiveness.Stale => "Worker 进程已退出",
                AdmissionLiveness.Alive => "Worker 仍存活，IPC 已断开",
                _ => "无法确认 Worker 是否仍在运行，IPC 已断开"
            });
        lock (_gate) {
            if (liveness != AdmissionLiveness.Stale && _exitWatch is null
                && ReferenceEquals(_admission, admission) && _connection is null) {
                _exitWatch = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                _ = WatchForWorkerExitAsync(admission, _exitWatch);
            }
        }
    }

    private void CancelExitWatchLocked()
    {
        var watch = _exitWatch;
        _exitWatch = null;
        watch?.Cancel();
    }

    // A killed Worker's pipe can end before Windows removes its process. Repeat the same inspection
    // until the process is gone, the Worker reconnects or the Admission is replaced.
    private async Task WatchForWorkerExitAsync(WorkerAdmissionRecord admission, CancellationTokenSource watch)
    {
        var cancellationToken = watch.Token;
        try {
            while (true) {
                await Task.Delay(WorkerExitRecheckDelay, cancellationToken);
                lock (_gate) {
                    if (!ReferenceEquals(_admission, admission) || _connection is not null) {
                        return;
                    }
                }
                if (ClassifyAdmission(admission) == AdmissionLiveness.Stale) {
                    SetDisconnectedObservation(admission, WorkerObservation.WorkerExited, "Worker 进程已退出", watch);
                    return;
                }
            }
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
        } finally {
            lock (_gate) {
                if (ReferenceEquals(_exitWatch, watch)) {
                    _exitWatch = null;
                }
            }
            watch.Dispose();
        }
    }

    private void LoadAdmission()
    {
        WorkerAdmissionRecord? record;
        try {
            record = _store.Load();
        } catch (Exception exception) {
            _admissionUnreadable = true;
            Snapshot = new WorkerCoordinatorSnapshot(
                WorkerObservation.WorkerRecoveryConflict, false, null,
                $"无法读取 Worker Admission Record：{exception.GetBaseException().Message}");
            _logger.Error("读取 Worker Admission Record 失败；结束桌面分身前不会启动新的 Worker。", exception);
            return;
        }
        if (record is null) {
            return;
        }
        _admission = record;
        var liveness = ClassifyAdmission(record);
        if (liveness == AdmissionLiveness.Stale
            && TryDiscardStaleAdmission(record, "上次的 Worker 已不存在，Admission Record 已清理")) {
            _logger.Info($"启动时已证明 Worker instance={record.WorkerInstanceId} 不再存在，已清理其 Admission Record。 ");
            return;
        }
        if (liveness == AdmissionLiveness.Alive) {
            Snapshot = new WorkerCoordinatorSnapshot(
                record.WorkerPid is null ? WorkerObservation.WorkerStarting : WorkerObservation.IpcDisconnected,
                false, null, "已加载 Worker Admission Record，等待 Worker 连接");
            return;
        }
        Snapshot = new WorkerCoordinatorSnapshot(
            WorkerObservation.WorkerRecoveryConflict, false, null,
            "无法确认上次的 Worker 是否仍在运行；Admission 已保留，等待 Worker 连接或结束桌面分身");
        _logger.Warn($"无法确认 Worker instance={record.WorkerInstanceId} 是否仍在运行；保留 Admission Record。 ");
    }

    private AdmissionLiveness ClassifyAdmission(WorkerAdmissionRecord record)
    {
        lock (_gate) {
            if (_launchingWorkerInstanceId == record.WorkerInstanceId
                || _connection is not null && _admission?.WorkerInstanceId == record.WorkerInstanceId) {
                return AdmissionLiveness.Alive;
            }
        }
        return WorkerAdmissionInspection.Classify(
            record, _workerImageName, DateTime.UtcNow, _captureProcesses, _enumerateSessions);
    }

    // Deletes only the record that was classified, and only while nothing has connected or launched for it since.
    private bool TryDiscardStaleAdmission(WorkerAdmissionRecord record, string detail)
    {
        lock (_gate) {
            if (!ReferenceEquals(_admission, record) || _connection is not null
                || _launchingWorkerInstanceId == record.WorkerInstanceId) {
                return false;
            }
            try {
                _store.DeleteRecord();
            } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
                _logger.Warn("删除失效的 Worker Admission Record 失败；保留该记录。", exception);
                return false;
            }
            CancelExitWatchLocked();
            RemoveAdmissionLocked(record, confirmedExit: true);
            _awaitedFreshSnapshot = null;
            Snapshot = WorkerCoordinatorSnapshot.Empty with { Detail = detail };
            try {
                _store.DeleteManifest(record.WorkerInstanceId);
            } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
                _logger.Warn("删除失效 Worker 的 launch manifest 失败。", exception);
            }
        }
        RaiseStateChanged();
        return true;
    }

    private void KeepUnprovenAdmission(WorkerAdmissionRecord record, string detail)
    {
        lock (_gate) {
            if (ReferenceEquals(_admission, record)) {
                _awaitedFreshSnapshot = null;
            }
        }
        SetDisconnectedObservation(record, WorkerObservation.WorkerRecoveryConflict, detail);
    }

    private void SetDisconnectedObservation(WorkerAdmissionRecord record, WorkerObservation observation, string detail,
        CancellationTokenSource? watch = null)
    {
        lock (_gate) {
            if (!ReferenceEquals(_admission, record) || _connection is not null || watch is not null && watch != _exitWatch) {
                return;
            }
            if (observation == WorkerObservation.WorkerExited && Snapshot.Observation != observation) {
                ObserveAdmissionLocked(record, WorkerEvidenceKind.ProcessExited);
            }
            Snapshot = new WorkerCoordinatorSnapshot(observation, false, Snapshot.WorkerSnapshot, detail);
        }
        RaiseStateChanged();
    }

    private static InvalidOperationException CreateAdmissionRefusal(
        WorkerAdmissionRecord record, AdmissionLiveness? liveness) => new(liveness switch {
            AdmissionLiveness.Alive when record.WorkerPid is int pid =>
                $"桌面分身中的 Worker（PID {pid}）仍在运行，正在等待它重新连接。请稍后重试；如仍无法恢复，{EndChildSessionHint}",
            AdmissionLiveness.Alive => "Worker 正在启动，请稍后重试。",
            AdmissionLiveness.Stale => "上次的 Worker 已不存在，但暂时无法清理其 Admission Record，请稍后重试。",
            _ => $"无法确认上一次启动的 Worker 是否已退出，为避免重复启动 Worker 已停止准备。{EndChildSessionHint}"
        });

    private void ValidateWorkerExecutable()
    {
        if (!File.Exists(_workerExecutablePath)) {
            throw new FileNotFoundException("Worker 尚未随 GUI 发布。请重新运行正式发布脚本。", _workerExecutablePath);
        }
        if (!string.Equals(Path.GetExtension(_workerExecutablePath), ".exe", StringComparison.OrdinalIgnoreCase)) {
            throw new InvalidDataException($"Worker 可执行文件无效：{_workerExecutablePath}。 ");
        }
    }

    // The primary guard against a second live Worker; the Worker's per-session mutex is only a backstop.
    internal void EnsureSessionHasNoWorker(uint childSessionId)
    {
        IReadOnlyList<WorkerProcessEntry> processes;
        try {
            processes = _captureProcesses();
        } catch (Exception exception) {
            throw new InvalidOperationException(
                "无法枚举进程以确认桌面分身中没有 Worker，为避免重复启动 Worker 已停止准备。请稍后重试。", exception);
        }
        var (running, unreadable) = WorkerAdmissionInspection.FindWorkers(processes, childSessionId, _workerImageName);
        if (running.Length > 0) {
            throw new InvalidOperationException(
                $"桌面分身中已有 {_workerImageName} 进程（PID {string.Join("、", running)}），"
                + $"为避免重复启动 Worker 已停止准备。{EndChildSessionHint}");
        }
        if (unreadable.Length > 0) {
            throw new InvalidOperationException(
                $"无法读取进程 {string.Join("、", unreadable)} 的信息，不能确认桌面分身中没有 Worker，"
                + $"为避免重复启动 Worker 已停止准备。请稍后重试；如仍失败，{EndChildSessionHint}");
        }
    }

    private WorkerAdmissionRecord RequireCurrentAdmissionLocked(WorkerAdmissionRecord validated, int clientPid)
    {
        if (_admission is not { } current || current.WorkerInstanceId != validated.WorkerInstanceId) {
            throw new UnauthorizedAccessException("Worker Admission 已被移除或替换。 ");
        }
        if (current.WorkerPid is int pid && pid != clientPid) {
            throw new UnauthorizedAccessException("Pipe client PID 与 Admission Record 不匹配。 ");
        }
        return current;
    }

    private void RaiseStateChanged()
    {
        _stateLog.Observe(() => Snapshot);
        _runResults.Observe(Snapshot.WorkerSnapshot);
        StateChanged?.Invoke(this, Snapshot);
    }

    private static int GetClientPid(NamedPipeServerStream server)
    {
        if (!GetNamedPipeClientProcessId(server.SafePipeHandle, out var processId)) {
            throw new IOException("无法取得 Named Pipe client PID。", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
        }
        return checked((int)processId);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(
        Microsoft.Win32.SafeHandles.SafePipeHandle pipe,
        out uint clientProcessId);
}

internal sealed class WorkerProtocolErrorException : Exception
{
    internal WorkerProtocolErrorException(string code, string message)
        : base($"{code}: {message}")
    {
    }
}
