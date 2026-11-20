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
            closed.WriteAsync(request, CancellationToken.None, () => sending = true).GetAwaiter().GetResult();
            throw new InvalidOperationException("已经释放的连接不应接受写入。");
        } catch (ObjectDisposedException) {
            Require(!sending && stream.ToArray().Length == 0, "写入前已经释放连接时，不能开始等待 Run 的终态。");
        }

        string[] scenarios = ["accepted", "fast-terminal", "reconnect", "retry-ended", "not-sent", "prefix-only",
            "reject", "prepare-failure"];
        foreach (var scenario in scenarios) {
            var root = Path.Combine(directory, scenario);
            var projectRoot = CreateProjectFixture(root);
            var config = Path.Combine(projectRoot, "config", "configurations.json");
            var project = ProjectPlanModule.Open(projectRoot, config);
            WithFakeWorker(logger, Path.Combine(root, "worker"), (coordinator, record, name) => {
                var check = Dispatcher.CurrentDispatcher.InvokeAsync(() => ExerciseAutoRunAsync(logger, projectRoot,
                    project.ActiveConfigurationId, coordinator, record, name, scenario)).Task.Unwrap();
                PumpUntil(() => check.IsCompleted, $"自动执行未完成 {scenario}。");
                check.GetAwaiter().GetResult();
            }, workerListed: () => true, runtimeProfileDigest: project.RuntimeProfileDigest);
        }
    }

    private static void ConnectTestDesktop(ChildSessionManager session, uint id) => typeof(ChildSessionManager)
        .GetProperty("Snapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session,
            new ChildSessionSnapshot(ChildSessionState.Connected, id, 1, "ready"));

    private static async Task ExerciseAutoRunAsync(AppLogger logger, string projectRoot, Guid configurationId,
        WorkerCoordinator coordinator, WorkerAdmissionRecord record, string pipeName, string scenario)
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
        var quit = new QuitAfterRunTracker();
        var window = new MainWindow(logger, session, coordinator, new RuntimeEnvironment(logger, session, coordinator),
            () => Task.CompletedTask, () => Task.CompletedTask, _ => Task.CompletedTask, projectRoot,
            prepareEnvironment: Prepare, autostart: InertAutostart(), launchCommand: command, quitAfterRun: quit);
        var exitRequests = 0;
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
                    window.ObserveApplicationRun(view, () => exitRequests++);
                    queued.TrySetResult();
                }
            }
            coordinator.StateChanged += ObserveTerminal;
            try {
                await pipe.WriteAsync(StateEvent(terminal), token);
                await queued.Task.WaitAsync(token);
                // ObserveApplicationRun posts at Normal. Count only after the queued delivery has run.
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
                var automatic = window.AutoRunOnLaunchAsync();
                var prefix = new byte[sizeof(int)];
                await transport!.ReadExactlyAsync(prefix, token);
                var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
                await pipe.DisposeAsync();
                Require(await automatic, "自动执行应处理只读到长度前缀后的断线。");
                AwaitWorker(coordinator, view => view.Observation == WorkerObservation.IpcDisconnected,
                    "GUI 未观察到读取前缀后的断线。");
                await using var restored = await WorkerCoordinatorSelfTest.OpenConnectionAsync(
                    pipeName, record, 0, token);
                pipe = restored;
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
            var launch = window.AutoRunOnLaunchAsync();
            if (scenario == "prepare-failure") {
                Require(await launch && !failNextPreparation, "自动执行必须处理准备失败。");
                await CompleteManualRetry("RetryEnvironmentButton");
                return;
            }
            var start = await ReadStart();
            var last = SucceededRun(ProtocolJson.Deserialize<RunStartRequest>(start.Data));
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
                Require(!quit.Observe(coordinator.Snapshot), "断线本身不能触发退出。");
                await using var restored = await WorkerCoordinatorSelfTest.OpenConnectionAsync(
                    pipeName, record, 0, token, lastRun: last, stateRevision: 2);
                if (scenario == "retry-ended") {
                    PumpUntil(() => coordinator.Snapshot.SnapshotFresh && ConfigurationEditable(window), "等待手动重试。");
                    Click(window, "StartTaskHeaderButton");
                    PumpUntil(() => ConfigurationEditable(window), "Worker 已确认原 Run 完成，手动重试应直接结束。");
                }
                AwaitWorker(coordinator, quit.Observe, "重连后的完整快照必须能确认目标 Run 已结束。");
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
