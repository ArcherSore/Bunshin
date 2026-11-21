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
        internal Action? DuringDialog;
        internal int HelloReads;
        internal RunPresentation Presentation => new() {
            RequiresWindowsHello = () => { HelloReads++; return true; },
            ShowError = (_, operation, error, message, _) => {
                Dialogs.Add((operation, error, message));
                DuringDialog?.Invoke();
                return MessageBoxResult.No;
            },
            ShowBalloon = notice => { Balloons.Add(notice); return true; }
        };
    }

    private static void VerifyRunDelivery(AppLogger logger, string directory)
    {
        foreach (var scenario in new[] { "hello", "rejected", "unknown", "unconfirmed", "late", "quit", "update-failed" }) {
            VerifyRunDeliveryScenario(logger, Path.Combine(directory, scenario), scenario);
        }
        VerifyScheduledTerminal(logger, Path.Combine(directory, "buffered-schedule"), RunState.Succeeded, bufferExit: true);
    }

    private static void VerifyRunDeliveryScenario(AppLogger logger, string directory, string scenario, bool forceScheduledStart = false)
    {
        var root = CreateProjectFixture(directory);
        if (forceScheduledStart) CreateIdleSettings(root, logger).ForceScheduledStart.Value = true;
        var project = ProjectPlanModule.Open(root, Path.Combine(root, "config", "configurations.json"));
        var clock = new ScheduleTestClock();
        var listed = true;
        WithFakeWorker(logger, Path.Combine(root, "worker"), (worker, record, name) => {
            var check = Dispatcher.CurrentDispatcher.InvokeAsync(() => ExerciseRunDeliveryAsync(logger, root,
                project.ActiveConfigurationId, worker, record, name, clock, scenario, () => Volatile.Write(ref listed, false))).Task.Unwrap();
            PumpUntil(() => check.IsCompleted, $"接线场景 {scenario} 未完成。");
            check.GetAwaiter().GetResult();
        }, () => Volatile.Read(ref listed), project.RuntimeProfileDigest, clock);
    }

    private enum ManualSettlement { ForegroundFault, HiddenFailure, ForcedQuery, NotRegistered, DialogGate, OrdinaryDialog }

    private static void VerifyManualSettlement(AppLogger logger, string directory, ManualSettlement scenario)
    {
        var root = CreateProjectFixture(directory);
        var dialogScenario = scenario is ManualSettlement.DialogGate or ManualSettlement.OrdinaryDialog;
        if (dialogScenario) TwoConfigurations()(root);
        var project = ProjectPlanModule.Open(root, Path.Combine(root, "config", "configurations.json"));
        if (scenario is ManualSettlement.ForcedQuery or ManualSettlement.DialogGate)
            CreateIdleSettings(root, logger).ForceScheduledStart.Value = true;
        var clock = new ScheduleTestClock();
        var probe = new PresentationProbe();
        var preparations = 0;
        var listed = true;
        var wts = new SimulatedWts();
        using var session = wts.CreateManager(logger);
        var finishScheduled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePeer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? peer = null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = deadline.Token;
        WithFakeWorker(logger, Path.Combine(directory, "worker"), (worker, record, pipeName) => {
            WithRuntimeControlWindow(logger, directory, window => {
                var check = window.Dispatcher.InvokeAsync(Exercise).Task.Unwrap();
                PumpUntil(() => check.IsCompleted, "手动 Run 的结算应完成。");
                check.GetAwaiter().GetResult();

                async Task Exercise()
                {
                    await using var pipe = await WorkerCoordinatorSelfTest.OpenConnectionAsync(pipeName, record, 0, token);
                    AwaitWorker(worker, v => v.Fresh, "模拟 Worker 应就绪。");
                    ConnectTestDesktop(session, record.ChildSessionId);
                    SetOnboardingField(window, "_sessionSnapshot", session.Snapshot);
                    await Dispatcher.Yield(DispatcherPriority.Background);
                    if (dialogScenario) {
                        var weekly = project.Configurations.Single(c => c.Name == "周常").Id;
                        var completed = new List<Guid>();
                        window.ScheduledRunCompleted += (_, run) => completed.Add(run.RunId);
                        Task<ScheduledRunResult>? duringDialog = null;
                        var notices = new List<RunTerminalNotification>();
                        window.NotificationRequested += (_, notice) => notices.Add(notice);
                        var cleaned = false;
                        probe.DuringDialog = () => {
                            duringDialog = window.TryStartScheduledRunAsync(weekly);
                            PumpUntil(() => duringDialog.IsCompleted, "手动错误框打开期间定时准入应返回。");
                            if (scenario == ManualSettlement.DialogGate && duringDialog.Result.RunId.HasValue) {
                                finishScheduled.SetResult();
                                PumpUntil(() => window.LogLines.OfType<Gui.Models.LogEntry>()
                                    .Any(e => e.Message == "全部完成，已关闭桌面分身"), "新 Run 成功后应在旧错误框内完成收尾。");
                                cleaned = !session.HasChildSession && completed.SequenceEqual([duringDialog.Result.RunId.Value])
                                    && notices.Count(n => n.Title == "「周常」已完成") == 1;
                            }
                        };
                        // The button still shows Ready, but the sender has already lost the Worker.
                        Volatile.Write(ref listed, false);
                        Task.Run(() => worker.ChildSessionEnded(record)).GetAwaiter().GetResult();
                        Click(window, "StartTaskHeaderButton");
                        PumpUntil(() => duringDialog != null, "正文前失败应打开手动启动错误框。");
                        var result = await duringDialog!.WaitAsync(token);
                        if (peer?.IsFaulted == true) await peer;
                        if (scenario == ManualSettlement.OrdinaryDialog) {
                            Require(result.Disposition == ScheduledRunDisposition.Busy && preparations == 0
                                && ProjectOf(window).ActiveConfigurationId == project.ActiveConfigurationId
                                && wts.LogoffAttempts.Count == 0 && probe.Dialogs.Count == 1,
                                "普通定时应遇忙跳过，保留原配置和环境。");
                        } else {
                            Require(cleaned && preparations == 1 && wts.LogoffAttempts.SequenceEqual([record.ChildSessionId])
                                && probe.Dialogs.Count == 1, $"旧错误框未关闭时强制定时应完成一次收尾：{result}，准备 {preparations}，完成 {completed.Count}，注销 {wts.LogoffAttempts.Count}，{worker.Snapshot.Detail}。");
                        }
                        return;
                    }
                    Click(window, "StartTaskHeaderButton");
                    var start = await WorkerCoordinatorSelfTest.ReadRequestAsync(pipe, ProtocolOperations.RunStart, token);
                    var request = ProtocolJson.Deserialize<RunStartRequest>(start.Data);
                    var snapshot = WorkerCoordinatorSelfTest.CreateSnapshot(record, 0);
                    if (scenario == ManualSettlement.ForegroundFault) {
                        var running = WorkerCoordinatorSelfTest.CreateActiveRun(new(request.RunId, request.Plan, request.PlanDigest));
                        snapshot = snapshot with { ActiveRun = running, RunState = running.State, StateRevision = 2 };
                        await pipe.WriteAsync(StateEvent(snapshot), token);
                        await pipe.WriteAsync(WireEnvelope.Response(start.Operation, start.RequestId!.Value,
                            new RunStartResponse("accepted")), token);
                        PumpUntil(() => HeaderShows(window, "StopTaskHeaderButton", enabled: true), "应先显示运行中的手动 Run。");
                        window.Activate();
                        PumpUntil(() => window.IsActive, "模拟前台窗口应已激活。");
                        await pipe.WriteAsync(StateEvent(snapshot with { StateRevision = 3, WorkerState = WorkerState.Faulted }), token);
                        AwaitWorker(worker, v => v.WorkerSnapshot?.WorkerState == WorkerState.Faulted, "应先观察到 Faulted。");
                        await Dispatcher.Yield(DispatcherPriority.Background);
                        PumpUntil(() => HeaderShows(window, "RecoverEnvironmentButton", enabled: true), "Faulted 后应显示恢复按钮。");
                        Require(probe.Balloons.Count == 0 && probe.Dialogs.Count == 0, "前台恢复按钮出现时应抑制气泡。");
                        worker.ChildSessionEnded(record);
                        await Dispatcher.Yield(DispatcherPriority.Background);
                        Require(probe.Dialogs is [var dialog] && dialog.Message.Contains("运行结果未确认") && probe.Balloons.Count == 0,
                            "抑制 Faulted 气泡后，结束分身应在前台呈现一次最终结论。");
                        return;
                    }
                    clock.Advance(TimeSpan.FromSeconds(16));
                    var query = await WorkerCoordinatorSelfTest.ReadRequestAsync(pipe, ProtocolOperations.WorkerGetSnapshot, token);
                    if (scenario == ManualSettlement.NotRegistered) {
                        await pipe.WriteAsync(WireEnvelope.Response(query.Operation, query.RequestId!.Value,
                            new GetSnapshotResponse(snapshot, false)), token);
                        PumpUntil(() => probe.Dialogs.Count > 0, "定向查询确认未登记后应呈现前台结论。");
                        Require(probe.Dialogs is [var dialog] && ShowsNotRegistered(dialog.Message),
                            "错误框应先说明未启动、Worker 未登记，并附上原发送超时。");
                        return;
                    }
                    if (scenario == ManualSettlement.HiddenFailure) {
                        window.Hide();
                        worker.ChildSessionEnded(record);
                        await Dispatcher.Yield(DispatcherPriority.Background);
                        Require(probe.Balloons is [{ Operation: "开始任务失败" } balloon] && balloon.Message.Contains("启动结果未知")
                            && probe.Dialogs.Count == 0, "手动启动后隐藏窗口，迟到的失败应只走托盘。");
                        return;
                    }
                    // Fail the background query. The forced opportunity must bypass its backoff.
                    await pipe.WriteAsync(WireEnvelope.Failure(query.Operation, query.RequestId!.Value,
                        "internal_error", "模拟后台查询失败"), token);
                    PumpUntil(() => worker.EvidenceCounts.Waiters == 0, "后台查询应已结束。");
                    await Dispatcher.Yield(DispatcherPriority.Background);
                    window.Hide();
                    var warnings = 0;
                    window.ForcedScheduledStartPending += (_, _) => warnings++;
                    var due = window.TryStartScheduledRunAsync(project.ActiveConfigurationId);
                    Require(warnings == 0, "后台查询退避期间，倒计时必须等到有序复核结束。");
                    var immediate = await WorkerCoordinatorSelfTest.ReadRequestAsync(pipe, ProtocolOperations.WorkerGetSnapshot, token);
                    Require(ProtocolJson.Deserialize<GetSnapshotRequest>(immediate.Data).RunId == request.RunId,
                        "应立即复核旧的手动 Run。");
                    await pipe.WriteAsync(WireEnvelope.Response(immediate.Operation, immediate.RequestId!.Value,
                        new GetSnapshotResponse(snapshot, false)), token);
                    Require((await due.WaitAsync(token)).Disposition == ScheduledRunDisposition.Admitted && preparations == 1 && warnings == 0
                        && probe.Balloons.Any(b => ShowsNotRegistered(b.Message)),
                        "有序复核确认未登记后应直接按空闲处理，托盘应显示结论和原发送超时。");
                }
            }, session: session, coordinator: worker, presentation: probe.Presentation, scheduleClock: clock,
                environment: CreateEnvironment(logger, session, worker, (_, _) => Task.CompletedTask),
                prepareEnvironment: async (plan, options, _) => {
                    preparations++;
                    if (scenario != ManualSettlement.DialogGate) throw new InvalidOperationException("模拟新环境准备结束");
                    wts.ReportedId = record.ChildSessionId;
                    wts.Sessions.Add(record.ChildSessionId);
                    await worker.PrepareWorkerAsync(record.ChildSessionId, plan, token, options());
                });
        }, () => Volatile.Read(ref listed), project.RuntimeProfileDigest, clock, launchPeer: (next, pipeName) => {
            Volatile.Write(ref listed, true);
            peer = Task.Run(async () => {
                await using var pipe = await WorkerCoordinatorSelfTest.OpenConnectionAsync(pipeName, next, 0, token);
                var start = await WorkerCoordinatorSelfTest.ReadRequestAsync(pipe, ProtocolOperations.RunStart, token);
                var request = ProtocolJson.Deserialize<RunStartRequest>(start.Data);
                var active = WorkerCoordinatorSelfTest.CreateActiveRun(new(request.RunId, request.Plan, request.PlanDigest));
                var idle = WorkerCoordinatorSelfTest.CreateSnapshot(next, 0);
                await pipe.WriteAsync(StateEvent(idle with { StateRevision = 2, RunState = active.State, ActiveRun = active }), token);
                await pipe.WriteAsync(WireEnvelope.Response(start.Operation, start.RequestId!.Value, new RunStartResponse("accepted")), token);
                await finishScheduled.Task.WaitAsync(token);
                await pipe.WriteAsync(StateEvent(idle with { StateRevision = 3, LastRun = SucceededRun(request) }), token);
                await releasePeer.Task.WaitAsync(token);
            });
        });
        releasePeer.SetResult();
        peer?.GetAwaiter().GetResult();
    }

    private static bool ShowsNotRegistered(string message) =>
        message.Contains("任务未能启动") && message.Contains("Worker 未登记本次启动")
        && message.IndexOf("超时", StringComparison.Ordinal) > message.IndexOf("未登记", StringComparison.Ordinal);

    private static async Task ExerciseRunDeliveryAsync(AppLogger logger, string root, Guid configurationId,
        WorkerCoordinator worker, WorkerAdmissionRecord record, string name, ScheduleTestClock clock, string scenario, Action workerExited)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = timeout.Token;
        await using var pipe = await WorkerCoordinatorSelfTest.OpenConnectionAsync(name, record, 0, token);
        AwaitWorker(worker, view => view.Fresh, "模拟 Worker 应就绪。");
        var probe = new PresentationProbe();
        var loginError = new InvalidOperationException("登录操作失败", new ChildSessionConnectionException("原始登录错误"));
        var wts = new SimulatedWts();
        if (scenario == "quit-recovery") {
            wts.ReportedId = record.ChildSessionId;
            wts.Sessions.Add(record.ChildSessionId);
            wts.DuringLogoff = _ => throw new IOException("模拟旧环境注销失败");
        }
        using var session = wts.CreateManager(logger);
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
            Task launch;
            if (scenario is "rejected" or "unknown" or "unconfirmed" or "late" or "hidden-retry") {
                // Let Loaded make the original unattended submission. Show the same window for the retry.
                window.Loaded += (_, _) => {
                    ConnectTestDesktop(session, record.ChildSessionId);
                    SetOnboardingField(window, "_sessionSnapshot", session.Snapshot);
                };
                window.StartInTray();
                launch = Task.CompletedTask;
            } else {
                launch = window.AutoRunOnLaunchAsync();
            }
            var boot = await WorkerCoordinatorSelfTest.ReadRequestAsync(pipe, ProtocolOperations.RunStart, token);
            var bootPlan = ProtocolJson.Deserialize<RunStartRequest>(boot.Data);
            var snapshot = WorkerCoordinatorSelfTest.CreateSnapshot(record, 0);
            if (scenario == "quit-recovery") {
                await pipe.WriteAsync(WireEnvelope.Response(boot.Operation, boot.RequestId!.Value, new RunStartResponse("accepted")), token);
                await pipe.WriteAsync(StateEvent(snapshot with { StateRevision = 2, LastRun = SucceededRun(bootPlan) }), token);
                await launch;
                PumpUntil(() => exits == 1, "开机 Run 结束应请求退出。");
                Require((await environment.EndAsync()).Outcome == EnvironmentEndOutcome.Unconfirmed, "首次退出应因旧环境注销失败而取消。");
                window.SetExitInProgress(false);
                wts.DuringLogoff = null;
                workerExited();
                pipe.DisposeAsync().AsTask().GetAwaiter().GetResult();
                AwaitWorker(worker, v => v.Observation == WorkerObservation.WorkerExited, "旧 Worker 应已确认退出。");
                var due = await window.TryStartScheduledRunAsync(configurationId);
                Require(exits == 1 && due.Disposition == ScheduledRunDisposition.Busy && due.Reason?.Contains("退出") == true
                    && wts.LogoffAttempts.Count == 1, "旧退出请求待恢复时，定时必须跳过并说明原因。");
                string? skip = null;
                void RecordSkip(object? _, Gui.Models.LogEntry entry)
                {
                    if (entry.Message.Contains("自动执行已跳过")) skip = entry.Message;
                }
                logger.EntryWritten += RecordSkip;
                try {
                    Require(!await window.AutoRunOnLaunchAsync() && skip?.Contains("退出") == true,
                        "开机启动也应跳过并记录旧退出请求的原因。");
                } finally {
                    logger.EntryWritten -= RecordSkip;
                }
                await Dispatcher.Yield(DispatcherPriority.Background);
                Require(exits == 2 && starts == 1, "Worker 确认退出后，原退出请求应恰好重试一次。");
                return;
            }
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
                await Dispatcher.Yield(DispatcherPriority.Background);
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
                await Dispatcher.Yield(DispatcherPriority.Background);
                Require(exits == 1 && balloonsAtQuit == 1 && probe.Balloons.Single().Title.Contains("已完成"),
                    "退出取消后应先提醒，再由目标 Run 触发一次同步退出。");
                return;
            }
            async Task Reject(WireEnvelope request) => await pipe.WriteAsync(WireEnvelope.Failure(request.Operation,
                request.RequestId!.Value, "worker_busy", "模拟明确拒绝"), token);
            await Reject(boot);
            PumpUntil(() => probe.Balloons.Count > 0, "开机拒绝应交付给托盘。");
            await Dispatcher.Yield(DispatcherPriority.Background);
            Require(probe.Balloons is [{ Operation: "开始任务失败" } notice] && notice.Error == await rejected.Task.WaitAsync(token)
                && probe.Dialogs.Count == 0, "开机拒绝只通过托盘呈现原始异常。");
            rejected = new(TaskCreationOptions.RunContinuationsAsynchronously);
            window.Show();
            Click(window, "StartTaskHeaderButton");
            var start = await WorkerCoordinatorSelfTest.ReadRequestAsync(pipe, ProtocolOperations.RunStart, token);
            Require(start.Data.GetRawText() == boot.Data.GetRawText(), "手动重试应提交同一份冻结计划。");
            if (scenario == "hidden-retry") {
                window.Hide();
                worker.ChildSessionEnded(record);
                await Dispatcher.Yield(DispatcherPriority.Background);
                Require(probe.Dialogs.Count == 0 && probe.Balloons.Count == 1,
                    "同 ID 手动重试后隐藏窗口，Worker 退出时不能再次发送托盘气泡。");
            } else if (scenario == "rejected") {
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
                    await Dispatcher.Yield(DispatcherPriority.Background);
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
