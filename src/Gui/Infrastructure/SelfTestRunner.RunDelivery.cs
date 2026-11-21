using System.Windows;
using System.Windows.Threading;
using Gui.ChildSession;
using Gui.Views;
using Gui.Worker;
using ProjectModel;
using Protocol;

namespace Gui.Infrastructure;

internal static partial class SelfTestRunner
{
    private sealed class PresentationProbe
    {
        internal readonly List<(string Operation, Exception Error, string Message)> Dialogs = [];
        internal readonly List<RunTerminalNotification> Balloons = [];
        internal int HelloReads;
        internal RunPresentation Presentation => new() {
            RequiresWindowsHello = () => { HelloReads++; return true; },
            ShowError = (_, operation, error, message, _) => {
                Dialogs.Add((operation, error, message));
                return MessageBoxResult.No;
            },
            ShowBalloon = Balloons.Add
        };
    }

    private static void VerifyRunDelivery(AppLogger logger, string directory)
    {
        foreach (var scenario in new[] { "hello", "rejected", "unknown", "unconfirmed", "late", "quit", "update-failed" }) {
            var root = CreateProjectFixture(Path.Combine(directory, scenario));
            var project = ProjectPlanModule.Open(root, Path.Combine(root, "config", "configurations.json"));
            var clock = new ScheduleTestClock();
            WithFakeWorker(logger, Path.Combine(root, "worker"), (worker, record, name) => {
                var check = Dispatcher.CurrentDispatcher.InvokeAsync(() => ExerciseRunDeliveryAsync(logger, root,
                    project.ActiveConfigurationId, worker, record, name, clock, scenario)).Task.Unwrap();
                PumpUntil(() => check.IsCompleted, $"接线场景 {scenario} 未完成。");
                check.GetAwaiter().GetResult();
            }, () => true, project.RuntimeProfileDigest, clock);
        }
        VerifyScheduledTerminal(logger, Path.Combine(directory, "buffered-schedule"), RunState.Succeeded, bufferExit: true);
    }

    private static async Task ExerciseRunDeliveryAsync(AppLogger logger, string root, Guid configurationId,
        WorkerCoordinator worker, WorkerAdmissionRecord record, string name, ScheduleTestClock clock, string scenario)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = timeout.Token;
        await using var pipe = await WorkerCoordinatorSelfTest.OpenConnectionAsync(name, record, 0, token);
        AwaitWorker(worker, view => view.Fresh, "模拟 Worker 应就绪。");
        var probe = new PresentationProbe();
        var loginError = new InvalidOperationException("登录操作失败", new ChildSessionConnectionException("原始登录错误"));
        using var session = new SimulatedWts().CreateManager(logger);
        if (scenario != "hello") ConnectTestDesktop(session, record.ChildSessionId);
        var environment = CreateEnvironment(logger, session, worker, (_, _) => Task.CompletedTask);
        var exits = 0;
        var balloonsAtQuit = 0;
        MainWindow? window = null;
        window = new MainWindow(logger, session, worker, environment, () => Task.CompletedTask,
            () => Task.CompletedTask, _ => Task.CompletedTask, root, prepareEnvironment: (_, _, _) => throw loginError,
            launchCommand: new(AutoStart: true, Instance: configurationId.ToString(), QuitAfterRun: true),
            autostart: InertAutostart(), presentation: probe.Presentation, quitAfterRun: () => {
                exits++;
                balloonsAtQuit = probe.Balloons.Count;
                window!.SetExitInProgress(true);
            });
        var rejected = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var starts = 0;
        void Observe(object? sender, WorkerObservationBatch batch)
        {
            if (batch.Kind == WorkerEvidenceKind.BodyStarted) Interlocked.Increment(ref starts);
            if (batch.StartReply == StartReplyKind.RejectedBeforeRegistration) rejected.TrySetResult(batch.Error!);
            if (batch.Kind == WorkerEvidenceKind.RequestFailed && batch.Request?.Submission != null)
                failed.TrySetResult(batch.Error!);
        }
        worker.ObservationReceived += Observe;
        try {
            InvokeOnboarding(window, "LoadProject");
            if (scenario == "hello") {
                Require(await window.AutoRunOnLaunchAsync(), "开机意图应进入准备。");
                Require(probe.Balloons is [{ Operation: "准备运行环境失败" } balloon] && balloon.Error == loginError
                    && balloon.Message.Contains("原始登录错误") && probe.Dialogs.Count == 0 && probe.HelloReads == 0,
                    "后台准备失败只应向托盘交付一次，保留操作名和原异常。");
                Click(window, "RetryEnvironmentButton");
                Require(probe.Dialogs is [{ Operation: "准备运行环境失败" } dialog] && dialog.Error == loginError
                    && dialog.Message.Contains("Windows Hello") && dialog.Message.Contains("关闭该开关")
                    && dialog.Message.Contains("帐户密码登录") && !dialog.Message.Contains("原始登录错误")
                    && probe.HelloReads == 1 && probe.Balloons.Count == 1, "手动准备应以原异常呈现一次 Hello 操作指引。");
                return;
            }
            // Retry the unchanged rejected launch from the Start button. The same frozen Run can
            // acquire a new submission, but never the rejected launch's quit eligibility.
            var launch = window.AutoRunOnLaunchAsync();
            var boot = await WorkerCoordinatorSelfTest.ReadRequestAsync(pipe, ProtocolOperations.RunStart, token);
            var bootPlan = ProtocolJson.Deserialize<RunStartRequest>(boot.Data);
            var snapshot = WorkerCoordinatorSelfTest.CreateSnapshot(record, 0);
            if (scenario == "update-failed") {
                // The terminal is already in the pipe when the update starts closing. No real
                // updater or logoff is needed to exercise the failed handoff after environment end.
                await pipe.WriteAsync(WireEnvelope.Response(boot.Operation, boot.RequestId!.Value,
                    new RunStartResponse("accepted")), token);
                await launch;
                var handoffFailure = new IOException("模拟 Engine 未接管");
                var releaseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var gate = environment.RunExclusiveAsync(() => releaseGate.Task);
                var update = App.InstallAfterEnvironmentEndAsync(environment, window, logger, () => throw handoffFailure,
                    () => throw new InvalidOperationException("交接失败不应关闭 GUI"));
                await pipe.WriteAsync(StateEvent(snapshot with { StateRevision = 2, LastRun = SucceededRun(bootPlan) }), token);
                AwaitWorker(worker, v => v.WorkerSnapshot?.LastRun?.RunId == bootPlan.RunId, "交接期间应收到开机终态。");
                await Dispatcher.Yield(DispatcherPriority.Background);
                Require(probe.Balloons.Count == 0 && exits == 0, "更新等待环境时不应提前交付。");
                releaseGate.SetResult();
                await gate;
                try {
                    await update;
                    throw new InvalidOperationException("必须传播交接失败。");
                } catch (IOException error) when (error == handoffFailure) { }
                Require(!environment.Closing && exits == 1 && balloonsAtQuit == 1 && probe.Balloons.Count == 1,
                    "环境结束后交接失败，应恢复并补发一次提醒和退出请求。");
                return;
            }
            if (scenario == "quit") {
                window.SetExitInProgress(true);
                var terminal = SucceededRun(bootPlan);
                await pipe.WriteAsync(StateEvent(snapshot with { StateRevision = 2,
                    LastRun = terminal with { RunId = Guid.NewGuid() } }), token);
                await pipe.WriteAsync(StateEvent(snapshot with { StateRevision = 3, LastRun = terminal }), token);
                await pipe.WriteAsync(WireEnvelope.Response(boot.Operation, boot.RequestId!.Value, new RunStartResponse("accepted")), token);
                await launch;
                Require(exits == 0 && probe.Balloons.Count == 0, "退出期间应缓冲提醒和 -q。");
                window.SetExitInProgress(false);
                Require(exits == 1 && balloonsAtQuit == 1 && probe.Balloons.Single().Title.Contains("已完成"),
                    "退出取消后应先提醒，再由目标 Run 触发一次同步退出。");
                return;
            }
            async Task Reject(WireEnvelope request) => await pipe.WriteAsync(WireEnvelope.Failure(request.Operation,
                request.RequestId!.Value, "worker_busy", "模拟明确拒绝"), token);
            await Reject(boot);
            await launch;
            Require(probe.Balloons is [{ Operation: "开始任务失败" } notice] && notice.Error == await rejected.Task.WaitAsync(token)
                && probe.Dialogs.Count == 0, "开机拒绝只通过托盘呈现原始异常。");
            rejected = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Click(window, "StartTaskHeaderButton");
            var start = await WorkerCoordinatorSelfTest.ReadRequestAsync(pipe, ProtocolOperations.RunStart, token);
            Require(start.Data.GetRawText() == boot.Data.GetRawText(), "手动重试应提交同一份冻结计划。");
            if (scenario == "rejected") {
                await Reject(start);
                var error = await rejected.Task.WaitAsync(token);
                PumpUntil(() => probe.Dialogs.Count > 0, "手动明确拒绝应在前台可见。");
                Require(probe.Dialogs is [{ Operation: "开始任务失败" } dialog] && dialog.Error == error
                    && dialog.Message.Contains("模拟明确拒绝"), "前台拒绝应只弹一次并保留原异常。");
            } else {
                clock.Advance(TimeSpan.FromSeconds(16));
                var error = await failed.Task.WaitAsync(token);
                var query = await WorkerCoordinatorSelfTest.ReadRequestAsync(pipe, ProtocolOperations.WorkerGetSnapshot, token);
                Require(error is TimeoutException && ProtocolJson.Deserialize<GetSnapshotRequest>(query.Data).RunId == bootPlan.RunId
                    && probe.Dialogs.Count == 0 && starts == 2, "应答超时后应仅查询本 Run，启动次数不得增加。");
                if (scenario is "unconfirmed" or "late") {
                    await pipe.WriteAsync(WireEnvelope.Response(start.Operation, start.RequestId!.Value,
                        new RunStartResponse("accepted")), token);
                    var active = WorkerCoordinatorSelfTest.CreateActiveRun(new(bootPlan.RunId, bootPlan.Plan, bootPlan.PlanDigest));
                    await pipe.WriteAsync(StateEvent(snapshot with { StateRevision = 2, ActiveRun = active,
                        RunState = RunState.Running }), token);
                    AwaitWorker(worker, v => v.WorkerSnapshot?.ActiveRun?.RunId == bootPlan.RunId, "应接纳迟到的启动。");
                }
                if (scenario == "late") {
                    await pipe.WriteAsync(StateEvent(snapshot with { StateRevision = 3, LastRun = SucceededRun(bootPlan) }), token);
                    PumpUntil(() => worker.Snapshot.WorkerSnapshot?.LastRun?.RunId == bootPlan.RunId
                        && ConfigurationEditable(window), "已确认终态应允许下一次开始。");
                    Require(probe.Dialogs.Count == 0, "迟到接纳不应报告启动失败。");
                } else {
                    window.SetExitInProgress(true);
                    worker.ChildSessionEnded(record);
                    await Dispatcher.Yield(DispatcherPriority.Background);
                    Require(probe.Dialogs.Count == 0, "确认退出期间不应提前弹框。");
                    window.SetExitInProgress(false);
                    var detail = scenario == "unknown" ? "启动结果未知" : "运行结果未确认";
                    Require(probe.Dialogs is [{ Operation: "开始任务失败" } dialog] && dialog.Error == error
                        && dialog.Message.Contains(detail), "取消退出应补发一次对应结论，并保留原异常。");
                }
            }
            Require(exits == 0 && starts == 2 && probe.Balloons.Count == 1,
                "手动重试不可再触发开机退出、额外发送或第二份托盘提醒。");
        } finally {
            worker.ObservationReceived -= Observe;
            window.AllowClose();
            window.Close();
        }
    }
}
