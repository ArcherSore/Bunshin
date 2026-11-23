using System.Buffers.Binary;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Threading;
using Gui.ChildSession;
using Gui.Views;
using Gui.Worker;
using ProjectModel;
using Protocol;

namespace Gui.Infrastructure;

internal static partial class SelfTestRunner
{
    private static void VerifyQuitAfterRunSubmission(AppLogger logger, string directory)
    {
        using var stream = new MemoryStream();
        var closed = new ProtocolConnection(stream);
        closed.DisposeAsync().AsTask().GetAwaiter().GetResult();
        var sending = false;
        try {
            var request = WireEnvelope.Request(ProtocolOperations.RunStart, Guid.NewGuid(), new { });
            closed.WriteAsync(request, CancellationToken.None, () => sending = true, null, CancellationToken.None)
                .GetAwaiter().GetResult();
            throw new InvalidOperationException("已经释放的连接不应接受写入。");
        } catch (ObjectDisposedException) {
            Require(!sending && stream.ToArray().Length == 0, "写入前已经释放连接时，不能开始等待 Run 的终态。");
        }

        string[] scenarios = ["accepted", "fast-terminal", "reconnect", "retry-ended", "not-sent", "prefix-only",
            "reject", "prepare-failure"];
        foreach (var scenario in scenarios) {
            VerifyBootSubmissionScenario(logger, Path.Combine(directory, scenario), scenario);
        }
    }

    private static void VerifyQuitAfterRunTerminals(AppLogger logger, string directory)
    {
        foreach (var state in new[] { RunState.Succeeded, RunState.Failed, RunState.Cancelled })
            VerifyBootSubmissionScenario(logger, Path.Combine(directory, state.ToString()), "bound-terminal", state);
    }

    private static void VerifyBootSubmissionScenario(AppLogger logger, string root, string scenario, RunState? terminal = null)
    {
        var projectRoot = CreateProjectFixture(root);
        var project = ProjectPlanModule.Open(projectRoot, Path.Combine(projectRoot, "config", "configurations.json"));
        WithFakeWorker(logger, Path.Combine(root, "worker"), (coordinator, record, name) => {
            var check = Dispatcher.CurrentDispatcher.InvokeAsync(() => ExerciseAutoRunAsync(logger, projectRoot,
                project.ActiveConfigurationId, coordinator, record, name, scenario, terminal)).Task.Unwrap();
            PumpUntil(() => check.IsCompleted, $"自动执行未完成 {scenario}。");
            check.GetAwaiter().GetResult();
        }, workerListed: () => true, runtimeProfileDigest: project.RuntimeProfileDigest);
    }

    private static void ConnectTestDesktop(ChildSessionManager session, uint id) => typeof(ChildSessionManager)
        .GetProperty("Snapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session,
            new ChildSessionSnapshot(ChildSessionState.Connected, id, 1, "ready"));

    private static async Task ExerciseAutoRunAsync(AppLogger logger, string projectRoot, Guid configurationId,
        WorkerCoordinator coordinator, WorkerAdmissionRecord record, string pipeName, string scenario, RunState? terminalState)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = timeout.Token;
        Stream? transport = null;
        await using var initialPipe = await WorkerCoordinatorSelfTest.OpenConnectionAsync(
            pipeName, record, 0, token, connected: stream => transport = stream);
        var pipe = initialPipe;
        AwaitWorker(coordinator, observation => observation.SnapshotFresh, "Worker 未报告就绪。");
        using var session = new SimulatedWts().CreateManager(logger);
        var failNextPreparation = scenario == "prepare-failure";
        if (scenario != "reconnect" && !failNextPreparation) {
            ConnectTestDesktop(session, record.ChildSessionId);
        }
        Task Prepare(ProjectPlanModule _, Func<MaaFrameworkOptions> options, bool showDesktop)
        {
            if (failNextPreparation) {
                failNextPreparation = false;
                throw new InvalidOperationException("scripted preparation failure");
            }
            ConnectTestDesktop(session, record.ChildSessionId);
            return Task.CompletedTask;
        }
        var command = new LaunchCommand(AutoStart: true, QuitAfterRun: true, Instance: configurationId.ToString());
        var exitRequests = 0;
        var window = new MainWindow(logger, session, coordinator, new RuntimeEnvironment(logger, session, coordinator),
            () => Task.CompletedTask, () => Task.CompletedTask, _ => Task.CompletedTask, projectRoot,
            checkForUpdate: OfflineUpdateCheck, prepareEnvironment: Prepare, autostart: InertAutostart(), launchCommand: command, quitAfterRun: () => exitRequests++);
        Task<WireEnvelope> ReadStart() => WorkerCoordinatorSelfTest.ReadRequestAsync(
            pipe, ProtocolOperations.RunStart, token);
        Task Accept(WireEnvelope request) => pipe.WriteAsync(WireEnvelope.Response(request.Operation,
            request.RequestId!.Value, new RunStartResponse("accepted")), token);
        async Task PublishTerminal(RunSnapshot last, long revision)
        {
            var terminal = WorkerCoordinatorSelfTest.CreateSnapshot(record, 0) with {
                StateRevision = revision, LastRun = last
            };
            var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void ObserveTerminal(object? sender, WorkerCoordinatorSnapshot view)
            {
                if (view.SnapshotFresh && view.WorkerSnapshot is { } worker
                    && worker.StateRevision == revision && worker.LastRun?.RunId == last.RunId) {
                    queued.TrySetResult();
                }
            }
            coordinator.StateChanged += ObserveTerminal;
            try {
                await pipe.WriteAsync(StateEvent(terminal), token);
                await queued.Task.WaitAsync(token);
                // Let the production delivery queue process the frame before counting exits.
                await Dispatcher.Yield(DispatcherPriority.Background);
            } finally {
                coordinator.StateChanged -= ObserveTerminal;
            }
        }
        async Task CompleteManualRetry(string button, Guid? rejectedRunId = null, int? originalLength = null)
        {
            Click(window, button);
            var manual = await WorkerCoordinatorSelfTest.ReadRequestAsync(
                pipe, ProtocolOperations.RunStart, token, WorkerCoordinatorSelfTest.CreateSnapshot(record, 0));
            if (originalLength.HasValue) {
                Require(originalLength == JsonSerializer.SerializeToUtf8Bytes(manual, ProtocolJson.Options).Length,
                    "断线前读取的前缀应与重试的 run.start 帧长度一致。");
            }
            var terminal = SucceededRun(ProtocolJson.Deserialize<RunStartRequest>(manual.Data));
            Require(rejectedRunId is null || terminal.RunId == rejectedRunId,
                "重试未改变的计划必须复用已拒绝的 Run ID。");
            await Accept(manual);
            await PublishTerminal(terminal, coordinator.Snapshot.WorkerSnapshot!.StateRevision + 1);
            PumpUntil(() => ConfigurationEditable(window), "手动启动应答后没有释放配置。");
            Require(exitRequests == 0, "自动执行没有开始后，手动重试完成不能导致退出。");
        }
        try {
            InvokeOnboarding(window, "LoadProject");
            if (scenario == "prefix-only") {
                var options = await WorkerCoordinatorSelfTest.ReadRequestAsync(
                    pipe, ProtocolOperations.FrameworkSetOptions, token);
                await pipe.WriteAsync(WireEnvelope.Response(options.Operation, options.RequestId!.Value,
                    new FrameworkSetOptionsResponse()), token);
                WorkerEvidenceKind? boundary = null;
                void RecordBoundary(object? sender, WorkerObservationBatch batch)
                {
                    if (batch.Kind is WorkerEvidenceKind.BodyStarted or WorkerEvidenceKind.NotSent) boundary = batch.Kind;
                }
                var prefix = new byte[sizeof(int)];
                coordinator.ObservationReceived += RecordBoundary;
                try {
                    var automatic = window.AutoRunOnLaunchAsync();
                    await transport!.ReadExactlyAsync(prefix, token);
                    await pipe.DisposeAsync();
                    Require(await automatic, "自动执行应处理只读到长度前缀后的断线。");
                } finally {
                    coordinator.ObservationReceived -= RecordBoundary;
                }
                var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
                AwaitWorker(coordinator, view => view.Observation == WorkerObservation.IpcDisconnected,
                    "GUI 未观察到读取前缀后的断线。");
                await using var restored = await WorkerCoordinatorSelfTest.OpenConnectionAsync(
                    pipeName, record, 0, token);
                pipe = restored;
                // Reading the prefix at the peer can race the writer's body callback. Only a
                // possibly-sent body needs a negative registration query before the manual retry.
                if (boundary == WorkerEvidenceKind.BodyStarted) {
                    await WorkerCoordinatorSelfTest.AnswerRunQueryAsync(restored,
                        WorkerCoordinatorSelfTest.CreateSnapshot(record, 0), token);
                } else {
                    Require(boundary == WorkerEvidenceKind.NotSent, "正文前断线应取得明确的未发送证据。");
                }
                PumpUntil(() => coordinator.Snapshot.SnapshotFresh && ConfigurationEditable(window), "等待手动重试。");
                await CompleteManualRetry("StartTaskHeaderButton", originalLength: length);
                return;
            }
            if (scenario == "not-sent") {
                // Hold the GUI reader at Ready until the sender tries the already closed pipe.
                using var resume = new ManualResetEventSlim();
                var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void PauseReader(object? sender, WorkerCoordinatorSnapshot view)
                {
                    if (view.WorkerSnapshot?.StateRevision == 2 && paused.TrySetResult()) {
                        resume.Wait(token);
                    }
                }
                coordinator.StateChanged += PauseReader;
                try {
                    var ready = WorkerCoordinatorSelfTest.CreateSnapshot(record, 0) with { StateRevision = 2 };
                    await pipe.WriteAsync(StateEvent(ready), token);
                    await paused.Task.WaitAsync(token);
                    await pipe.DisposeAsync();
                    Require(await window.AutoRunOnLaunchAsync(), "自动执行应处理写入前已经断开的管道。");
                } finally {
                    coordinator.StateChanged -= PauseReader;
                    resume.Set();
                }
                AwaitWorker(coordinator, view => view.Observation == WorkerObservation.IpcDisconnected,
                    "GUI 未观察到提交前的断线。");
                await using var restored = await WorkerCoordinatorSelfTest.OpenConnectionAsync(
                    pipeName, record, 0, token, stateRevision: 2);
                pipe = restored;
                PumpUntil(() => coordinator.Snapshot.SnapshotFresh && ConfigurationEditable(window), "等待手动重试。");
                await CompleteManualRetry("StartTaskHeaderButton");
                return;
            }
            if (terminalState.HasValue) {
                var historical = CreateNotificationSnapshot(RunState.Running);
                await PublishTerminal(FinishNotificationRun(historical, RunState.Succeeded).WorkerSnapshot!.LastRun!, 2);
                Require(exitRequests == 0, "提交命令行 -q 任务前的历史终态不得触发退出。");
            }
            var launch = window.AutoRunOnLaunchAsync();
            if (scenario == "prepare-failure") {
                Require(await launch && !failNextPreparation, "自动执行必须处理准备失败。");
                await CompleteManualRetry("RetryEnvironmentButton");
                return;
            }
            var start = await ReadStart();
            var last = SucceededRun(ProtocolJson.Deserialize<RunStartRequest>(start.Data));
            if (terminalState is { } state) {
                var running = WorkerCoordinatorSelfTest.CreateActiveRun(new(last.RunId, last.Plan, last.PlanDigest));
                var snapshot = WorkerCoordinatorSelfTest.CreateSnapshot(record, 0, running) with { StateRevision = 3 };
                await pipe.WriteAsync(StateEvent(snapshot), token);
                await Accept(start);
                Require(await launch && exitRequests == 0, "命令行 -q 任务已提交且正在运行时不得退出。");
                await pipe.WriteAsync(StateEvent(snapshot with { StateRevision = 4, WorkerState = WorkerState.Faulted,
                    LastRun = last with { RunId = Guid.NewGuid() } }), token);
                AwaitWorker(coordinator, v => v.WorkerSnapshot?.StateRevision == 4, "应收到 Faulted 和其他 Run 的终态。");
                await Dispatcher.Yield(DispatcherPriority.Background);
                Require(exitRequests == 0, "Faulted 和其他 Run 的终态不得触发退出。");
                await pipe.DisposeAsync();
                AwaitWorker(coordinator, v => !v.Fresh, "断线应使快照失效。");
                await Dispatcher.Yield(DispatcherPriority.Background);
                Require(exitRequests == 0, "断线后不得推测目标 Run 已结束。");
                last = last with { StartedAtUtc = DateTime.UnixEpoch, EndedAtUtc = DateTime.UnixEpoch.AddMinutes(1), State = state };
                await using var reconnect = await WorkerCoordinatorSelfTest.OpenConnectionAsync(pipeName, record, 0, token,
                    lastRun: last, stateRevision: 5);
                pipe = reconnect;
                PumpUntil(() => exitRequests == 1, $"时钟回拨后，已绑定 Run 的 {state} 终态仍应退出。");
                await PublishTerminal(last, 6);
                Require(exitRequests == 1, "重复终态不得再次请求退出。");
                return;
            }
            if (scenario == "reject") {
                await pipe.WriteAsync(WireEnvelope.Failure(start.Operation, start.RequestId!.Value,
                    "operation_not_allowed", "scripted refusal"), token);
                Require(await launch, "自动执行必须处理 Worker 的明确拒绝。");
                await CompleteManualRetry("StartTaskHeaderButton", last.RunId);
                return;
            }
            if (scenario is "reconnect" or "retry-ended") {
                await pipe.DisposeAsync();
                Require(await launch, "自动执行必须处理应答丢失。");
                AwaitWorker(coordinator, view => view.Observation == WorkerObservation.IpcDisconnected,
                    "GUI 未观察到 IPC 断开。");
                Require(exitRequests == 0, "断线本身不能触发退出。");
                await using var restored = await WorkerCoordinatorSelfTest.OpenConnectionAsync(
                    pipeName, record, 0, token, lastRun: scenario == "reconnect" ? last : null, stateRevision: 2);
                if (scenario == "retry-ended") {
                    var query = await WorkerCoordinatorSelfTest.ReadRequestAsync(restored, ProtocolOperations.WorkerGetSnapshot, token);
                    Require(ProtocolJson.Deserialize<GetSnapshotRequest>(query.Data).RunId == last.RunId, "应复核原命令行 -q 任务。");
                    var starts = 0;
                    var clickedBeforeQuit = false;
                    void StartOnTerminal(object? _, RunTerminalNotification notice)
                    {
                        if (notice.Failed) return;
                        clickedBeforeQuit = window.StartTaskHeaderButton.IsEnabled && exitRequests == 0;
                        Click(window, "StartTaskHeaderButton");
                    }
                    void CountStart(object? _, WorkerObservationBatch batch)
                    {
                        if (batch.Kind == WorkerEvidenceKind.BodyStarted) Interlocked.Increment(ref starts);
                    }
                    coordinator.ObservationReceived += CountStart;
                    window.NotificationRequested += StartOnTerminal;
                    try {
                        var ended = WorkerCoordinatorSelfTest.CreateSnapshot(record, 0) with { LastRun = last, StateRevision = 3 };
                        await restored.WriteAsync(StateEvent(ended), token);
                        PumpUntil(() => exitRequests == 1, "定向查询答复前就应按新鲜 LastRun 触发退出。");
                        Require(clickedBeforeQuit && starts == 0 && window.CurrentOperation == PendingOperation.None,
                            "手动开始不能越过已绑定的退出发出第二次提交。");
                        await restored.WriteAsync(WireEnvelope.Response(query.Operation, query.RequestId!.Value,
                            new GetSnapshotResponse(ended, true)), token);
                    } finally {
                        coordinator.ObservationReceived -= CountStart;
                        window.NotificationRequested -= StartOnTerminal;
                    }
                }
                PumpUntil(() => exitRequests == 1, "重连后新鲜终态应确认目标 Run 并退出。");
                if (scenario == "retry-ended") {
                    PumpUntil(() => coordinator.Snapshot.SnapshotFresh && ConfigurationEditable(window), "等待手动重试。");
                    Click(window, "StartTaskHeaderButton");
                    PumpUntil(() => ConfigurationEditable(window), "Worker 已确认原 Run 完成，手动重试应直接结束。");
                }
                Require(exitRequests == 1, "只允许退出一次。");
                return;
            }
            if (scenario == "accepted") {
                await Accept(start);
                Require(await launch, "GUI 必须完成已接纳的自动提交。");
            }
            await PublishTerminal(last with { RunId = Guid.NewGuid() }, 2);
            Require(exitRequests == 0, "其他 Run 的终态不能触发退出。");
            await PublishTerminal(last, 3);
            Require(exitRequests == 1, "本次自动执行的 Run 终态必须触发退出。");
            if (scenario == "fast-terminal") {
                Require(!launch.IsCompleted, "Worker 应答尚未发出，自动执行提交却已结束。");
                await Accept(start);
                Require(await launch, "自动执行应完成此次提交。");
                await PublishTerminal(last, 4);
                Require(exitRequests == 1, "迟到的启动应答不能再次触发退出。");
            }
        } finally {
            window.AllowClose();
            window.Close();
        }
    }

    private static RunSnapshot SucceededRun(RunStartRequest run)
    {
        var active = WorkerCoordinatorSelfTest.CreateActiveRun(new(run.RunId, run.Plan, run.PlanDigest));
        var ended = DateTime.UtcNow;
        return active with { State = RunState.Succeeded, EndedAtUtc = ended,
            CurrentPlanItemId = null, CurrentPlanItemIndex = null, Items = active.Items.Select(item => item with {
                State = PlanItemState.Succeeded, EndedAtUtc = ended
            }).ToArray() };
    }
}
