using System.Reflection;
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
            ShowError = (operation, error, message) => {
                Dialogs.Add((operation, error, message));
                DuringDialog?.Invoke();
            },
            ShowBalloon = notice => { Balloons.Add(notice); return true; }
        };
    }

    private static void VerifyWindowPrompts(AppLogger logger, string directory)
    {
        var balloons = new List<RunTerminalNotification>();
        WithRuntimeControlWindow(logger, directory, window => {
            var dialog = window.PromptDialog;
            var title = Named<System.Windows.Controls.TextBlock>(dialog, "TitleText");
            Task<bool> Error(string message) => window.ShowErrorPromptAsync(message, message,
                new IOException(message), offerLogs: true);

            var first = Error("第一次启动失败");
            var second = Error("第二次启动失败");
            Require(title.Text == "第二次启动失败" && !first.IsCompleted,
                "第一次错误未关闭时应立即显示新错误，保留原错误等待查看。");
            ClickConfigurationButton(Named<System.Windows.Controls.Button>(dialog, "ActionButton"));
            PumpUntil(() => second.IsCompleted && title.Text == "第一次启动失败", "关掉新错误后应恢复原错误。");
            ClickConfigurationButton(Named<System.Windows.Controls.Button>(dialog, "ActionButton"));
            PumpUntil(() => first.IsCompleted && !dialog.IsVisible, "错误均应完成交付。");

            var confirmation = window.ConfirmExitAsync();
            var pending = Error("等待交付的错误");
            window.SetExitInProgress(true);
            Require(confirmation.IsCompletedSuccessfully && !confirmation.Result && !dialog.IsVisible
                && !pending.IsCompleted, "自动退出开始后应结束旧确认，暂存尚未打开的错误。");
            var exitFailure = window.ShowErrorPromptAsync("退出失败", "无法结束桌面分身",
                new IOException("模拟注销失败"), offerLogs: true, showWhileClosing: true);
            Require(dialog.IsVisible && title.Text == "退出失败", "退出失败应立即显示在旧确认的位置。");
            ClickConfigurationButton(Named<System.Windows.Controls.Button>(dialog, "ActionButton"));
            PumpUntil(() => exitFailure.IsCompleted && !dialog.IsVisible, "确认错误后应关闭退出失败提示。");
            window.SetExitInProgress(false);
            PumpUntil(() => dialog.IsVisible && title.Text == "等待交付的错误", "取消退出后应恢复待交付的错误。");
            ClickConfigurationButton(Named<System.Windows.Controls.Button>(dialog, "ActionButton"));
            PumpUntil(() => pending.IsCompleted && !dialog.IsVisible, "恢复的错误应允许确认。");

            _ = window.ConfirmExitAsync();
            var discarded = Error("成功退出时仍在等待的错误");
            window.SetExitInProgress(true);
            window.Hide();
            window.CommitExit();
            Require(discarded.IsCompletedSuccessfully && !discarded.Result && balloons.Count == 0,
                "确认退出后应丢弃待交付错误，不发送托盘气泡。");
        }, presentation: new RunPresentation { ShowBalloon = notice => { balloons.Add(notice); return true; } });
    }

    private static void VerifyRunDeliveryScenarios(AppLogger logger, string directory)
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
        var nextWorker = new TaskCompletionSource<WorkerAdmissionRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        WithFakeWorker(logger, Path.Combine(root, "worker"), (worker, record, name) => {
            var check = Dispatcher.CurrentDispatcher.InvokeAsync(() => ExerciseRunDeliveryAsync(logger, root,
                project.ActiveConfigurationId, worker, record, name, clock, scenario,
                () => Volatile.Write(ref listed, false), nextWorker.Task)).Task.Unwrap();
            PumpUntil(() => check.IsCompleted, $"接线场景 {scenario} 未完成。");
            check.GetAwaiter().GetResult();
        }, () => Volatile.Read(ref listed), project.RuntimeProfileDigest, clock, launchPeer: (next, _) => {
            Volatile.Write(ref listed, true);
            nextWorker.SetResult(next);
        });
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
                        pipe.Close();
                        Task.Run(() => worker.ChildSessionEndedAsync(record)).GetAwaiter().GetResult();
                        Click(window, "StartPlanButton");
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
                    Click(window, "StartPlanButton");
                    var start = await WorkerCoordinatorSelfTest.ReadRequestAsync(pipe, ProtocolOperations.RunStart, token);
                    var request = ProtocolJson.Deserialize<RunStartRequest>(start.Data);
                    var snapshot = WorkerCoordinatorSelfTest.CreateSnapshot(record, 0);
                    if (scenario == ManualSettlement.ForegroundFault) {
                        var running = WorkerCoordinatorSelfTest.CreateActiveRun(new(request.RunId, request.Plan, request.PlanDigest));
                        snapshot = snapshot with { ActiveRun = running, RunState = running.State, StateRevision = 2 };
                        await pipe.WriteAsync(StateEvent(snapshot), token);
                        await pipe.WriteAsync(WireEnvelope.Response(start.Operation, start.RequestId!.Value,
                            new RunStartResponse("accepted")), token);
                        PumpUntil(() => HeaderShows(window, "StopPlanButton", enabled: true), "应先显示运行中的手动 Run。");
                        window.Activate();
                        PumpUntilFocus(() => window.IsActive, "模拟前台窗口应已激活。");
                        await pipe.WriteAsync(StateEvent(snapshot with { StateRevision = 3, WorkerState = WorkerState.Faulted }), token);
                        AwaitWorker(worker, v => v.WorkerSnapshot?.WorkerState == WorkerState.Faulted, "应先观察到 Faulted。");
                        await Dispatcher.Yield(DispatcherPriority.Background);
                        PumpUntil(() => HeaderShows(window, "RecoverEnvironmentButton", enabled: true), "Faulted 后应显示恢复按钮。");
                        Require(probe.Balloons.Count == 0 && probe.Dialogs.Count == 0, "前台恢复按钮出现时应抑制气泡。");
                        pipe.Close();
                        await worker.ChildSessionEndedAsync(record);
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
                        pipe.Close();
                        await worker.ChildSessionEndedAsync(record);
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
        WorkerCoordinator worker, WorkerAdmissionRecord record, string name, ScheduleTestClock clock, string scenario,
        Action workerExited, Task<WorkerAdmissionRecord> nextWorker)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = timeout.Token;
        await using var pipe = await WorkerCoordinatorSelfTest.OpenConnectionAsync(name, record, 0, token);
        AwaitWorker(worker, view => view.Fresh, "模拟 Worker 应就绪。");
        var probe = new PresentationProbe();
        var loginError = new InvalidOperationException("登录操作失败", new ChildSessionConnectionException("原始登录错误"));
        var logoffClock = new ScheduleTestClock();
        var wts = new SimulatedWts { SessionSurvivesLogoff = scenario == "quit-recovery" };
        if (scenario == "quit-recovery") {
            wts.ReportedId = record.ChildSessionId;
            wts.Sessions.Add(record.ChildSessionId);
        }
        using var session = wts.CreateManager(logger, clock: logoffClock);
        if (scenario != "hello") ConnectTestDesktop(session, record.ChildSessionId);
        var environment = CreateEnvironment(logger, session, worker, (_, _) => Task.CompletedTask);
        var exits = 0;
        var balloonsAtQuit = 0;
        Task? exit = null;
        var closed = false;
        var shutdowns = 0;
        var exitErrors = new List<Exception>();
        MainWindow? window = null;
        window = new MainWindow(logger, session, worker, environment, () => Task.CompletedTask,
            () => Task.CompletedTask, _ => Task.CompletedTask, root, checkForUpdate: OfflineUpdateCheck,
            prepareEnvironment: (plan, options, _) => {
                if (scenario != "quit-recovery") throw loginError;
                wts.ReportedId = record.ChildSessionId;
                wts.Sessions.Add(record.ChildSessionId);
                return worker.PrepareWorkerAsync(record.ChildSessionId, plan, token, options());
            },
            launchCommand: new(AutoStart: true, Instance: configurationId.ToString(), QuitAfterRun: true),
            autostart: InertAutostart(), presentation: probe.Presentation, quitAfterRun: () => {
                exits++;
                balloonsAtQuit = probe.Balloons.Count;
                if (scenario == "quit-recovery") {
                    var app = (App)System.Windows.Application.Current;
                    exit = app.ExitAsync(null, environment, window, exitErrors.Add, () => shutdowns++);
                } else {
                    window!.SetExitInProgress(true);
                }
            });
        window.Closed += (_, _) => closed = true;
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
                var loaded = typeof(MainWindow).GetMethod("MainWindow_Loaded", BindingFlags.Instance | BindingFlags.NonPublic)!;
                window.Loaded -= (RoutedEventHandler)loaded.CreateDelegate(typeof(RoutedEventHandler), window);
                window.ShowInTaskbar = false;
                window.ShowActivated = false;
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = window.Top = -32000;
                window.Show();
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
                PumpUntil(() => logoffClock.PendingTimers >= 2, "命令行 -q 任务结束应请求退出并等待注销。");
                logoffClock.Advance(TimeSpan.FromSeconds(60));
                await exit!.WaitAsync(token);
                Require(exits == 1 && exitErrors.Count == 1 && !closed && shutdowns == 0 && !environment.Closing
                    && environment.EndUnconfirmed && worker.TrackedAdmission == record,
                    "首次 -q 退出应因注销超时而取消，窗口和 Admission 均应保留。");
                wts.ReportedId = null;
                wts.Sessions.Remove(record.ChildSessionId);
                workerExited();
                pipe.DisposeAsync().AsTask().GetAwaiter().GetResult();
                AwaitWorker(worker, v => v.Observation == WorkerObservation.WorkerExited, "旧 Worker 应已确认退出。");
                var due = await window.TryStartScheduledRunAsync(configurationId);
                Require(exits == 1 && due.Disposition == ScheduledRunDisposition.Busy && due.Reason == "运行环境状态未知"
                    && wts.LogoffAttempts.Count == 1, "Worker 已退出但环境结束仍未确认时，定时应因环境状态未知而跳过。");
                string? skip = null;
                void RecordSkip(object? _, Gui.Models.LogEntry entry)
                {
                    if (entry.Message.Contains("自动执行已跳过")) skip = entry.Message;
                }
                logger.EntryWritten += RecordSkip;
                try {
                    Require(!await window.AutoRunOnLaunchAsync() && skip?.Contains("运行环境状态未知") == true && starts == 1,
                        "开机启动应因环境状态未知而跳过，不能提交新的 Run。");
                } finally {
                    logger.EntryWritten -= RecordSkip;
                }
                var ended = await environment.RequestEnd(() => true)!;
                await Dispatcher.Yield(DispatcherPriority.Background);
                Require(ended == EnvironmentEndResult.Ended && exits == 1 && !closed && shutdowns == 0 && exitErrors.Count == 1
                    && !environment.EndUnconfirmed && AdmissionDiscarded(worker),
                    "手动确认结束后应清除旧 Admission，保留 GUI 和首次取消提示。");
                var scheduled = window.TryStartScheduledRunAsync(configurationId);
                await Task.WhenAny(nextWorker, scheduled).WaitAsync(token);
                Require(nextWorker.IsCompletedSuccessfully, "确认结束后定时应准备新的 Worker。");
                var next = await nextWorker;
                await using var nextPipe = await WorkerCoordinatorSelfTest.OpenConnectionAsync(name, next, 0, token);
                var scheduledStart = await WorkerCoordinatorSelfTest.ReadRequestAsync(nextPipe, ProtocolOperations.RunStart, token);
                var run = ProtocolJson.Deserialize<RunStartRequest>(scheduledStart.Data);
                var activeRun = WorkerCoordinatorSelfTest.CreateActiveRun(new(run.RunId, run.Plan, run.PlanDigest));
                await nextPipe.WriteAsync(StateEvent(WorkerCoordinatorSelfTest.CreateSnapshot(next, 0, activeRun)
                    with { StateRevision = 2 }), token);
                await nextPipe.WriteAsync(WireEnvelope.Response(scheduledStart.Operation, scheduledStart.RequestId!.Value,
                    new RunStartResponse("accepted")), token);
                var result = await scheduled.WaitAsync(token);
                await Dispatcher.Yield(DispatcherPriority.Background);
                Require(result.Disposition == ScheduledRunDisposition.Admitted && result.RunId == run.RunId && result.Reason == null
                    && run.RunId != bootPlan.RunId && starts == 2 && exits == 1 && !closed && shutdowns == 0,
                    "确认结束后，定时应向新 Worker 启动一次新的 Run，GUI 保持打开。");
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
            Click(window, "StartPlanButton");
            var start = await WorkerCoordinatorSelfTest.ReadRequestAsync(pipe, ProtocolOperations.RunStart, token);
            Require(start.Data.GetRawText() == boot.Data.GetRawText(), "手动重试应提交同一份冻结计划。");
            if (scenario == "hidden-retry") {
                window.Hide();
                pipe.Close();
                await worker.ChildSessionEndedAsync(record);
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
                    pipe.Close();
                    await worker.ChildSessionEndedAsync(record);
                    await Dispatcher.Yield(DispatcherPriority.Background);
                    Require(probe.Dialogs.Count == 0, "确认退出期间不应提前弹框。");
                    window.SetExitInProgress(false);
                    await Dispatcher.Yield(DispatcherPriority.Background);
                    var (title, detail) = scenario == "unknown"
                        ? ("启动结果未知", "启动结果未知：任务可能已经开始执行。") : ("运行结果未确认", "任务已被接纳，运行结果未确认。");
                    Require(probe.Dialogs is [var dialog] && dialog.Operation == title && dialog.Error == error
                        && dialog.Message.StartsWith($"{detail}\n\n请稍后查看任务状态，确认没有在运行后再开始。", StringComparison.Ordinal)
                        && !dialog.Message.Contains(error.GetBaseException().Message, StringComparison.Ordinal),
                        $"取消退出应补发一次对应结论，标题不称失败、正文同托盘且不夹带原始异常：{string.Join("；", probe.Dialogs)}");
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
