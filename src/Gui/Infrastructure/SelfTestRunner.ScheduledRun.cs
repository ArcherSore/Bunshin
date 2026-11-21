using System.Diagnostics;
using System.IO;
using Gui.ChildSession;
using Gui.Models;
using Gui.Worker;
using Protocol;
using ProjectModel;
using Gui.Views;

namespace Gui.Infrastructure;

internal static partial class SelfTestRunner
{
    private static void VerifyScheduledRun(AppLogger logger, string directory)
    {
        RunOnboardingScenario(logger, directory, (window, _) => {
            CompleteOnboardingStartup(window);
            var project = ProjectOf(window);
            var before = project.ActiveConfigurationId;
            var weekly = project.Configurations.Single(item => item.Name == "周常");
            var failures = new List<string>();
            window.NotificationRequested += (_, n) => failures.Add(n.Title + " / " + n.Message);
            var result = window.TryStartScheduledRunAsync(weekly.Id).GetAwaiter().GetResult();
            // 迁移至 WPF / 无效定时配置
            Require(result.Disposition == ScheduledRunDisposition.Invalid && project.ActiveConfigurationId == before
                && failures.Single() == $"定时任务未能启动 / 「{weekly.Name}」：执行计划为空", "无效定时配置应通知原因并保留原配置。");
        }, beforeLoad: TwoConfigurations(weeklyHasTask: false));

        var wts = new SimulatedWts { Sessions = [0, 1, 7], LogoffError = 5 };
        using var session = wts.CreateManager(logger);
        var prepared = false;
        WithRuntimeControlWindow(logger, Path.Combine(directory, "cleanup"), window => {
            wts.ReportedId = 7;
            var project = ProjectOf(window);
            var task = project.SelectedTaskNames.First();
            var original = project.ActiveConfigurationId;
            var target = project.CreateConfiguration();
            project.AddItem(PlanItemKind.MaaTask, task);
            project.ActivateConfiguration(original);
            var failures = new List<string>();
            window.NotificationRequested += (_, n) => failures.Add(n.Title + " / " + n.Message);
            window.Hide();
            var targetName = project.Configurations.Single(c => c.Id == target).Name;
            var run = window.TryStartScheduledRunAsync(target);
            // 迁移至 WPF / 换场失败保留激活标签
            PumpUntil(() => run.IsCompleted, "旧分身清理未确认时应结束本轮。");
            Require(run.Result.Disposition == ScheduledRunDisposition.Admitted
                && project.ActiveConfigurationId == target && !prepared
                && wts.LogoffAttempts.SequenceEqual([7u])
                && !window.IsVisible && window.TaskPlanEditor.ConfigurationTabs.SelectedItem
                    is System.Windows.Controls.TabItem { Tag: Guid selected } && selected == target
                && failures.Single() == $"定时任务未能启动 / 「{targetName}」：旧的桌面分身未能关闭",
                "获准后激活目标，旧分身未结束必须报告失败并保留现场。");
        }, session: session, prepareEnvironment: (_, _, _) => {
            prepared = true;
            return Task.CompletedTask;
        });

        foreach (var bootFirst in new[] { true, false }) {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var preparedIds = new List<Guid>();
            RunOnboardingScenario(logger, Path.Combine(directory, $"boot-{bootFirst}"), (window, _) => {
                CompleteOnboardingStartup(window);
                var project = ProjectOf(window);
                var daily = project.Configurations.Single(c => c.Name == "日常").Id;
                var weekly = project.Configurations.Single(c => c.Name == "周常").Id;
                var boot = bootFirst ? window.AutoRunOnLaunchAsync() : null;
                var schedule = window.TryStartScheduledRunAsync(weekly);
                boot ??= window.AutoRunOnLaunchAsync();
                release.SetResult();
                // 迁移至 VerifyScheduleClockAtLogon
                PumpUntil(() => schedule.IsCompleted && boot.IsCompleted, "两种启动都应返回。");
                var winner = bootFirst ? daily : weekly;
                // 迁移至 VerifyScheduleClockAtLogon
                Require(preparedIds.SequenceEqual([winner]) && ProjectOf(window).ActiveConfigurationId == winner
                    && boot.Result == bootFirst && schedule.Result.Disposition == (bootFirst
                        ? ScheduledRunDisposition.Busy : ScheduledRunDisposition.Admitted),
                    "先获准者独占准备，另一来源不能切换配置或排队。");
            }, beforeLoad: TwoConfigurations(), launchCommand: new LaunchCommand(AutoStart: true, Instance: "日常"),
                prepareEnvironment: async (project, _, _) => {
                    preparedIds.Add(project.ActiveConfigurationId);
                    await release.Task;
                    throw new InvalidOperationException("模拟环境准备失败");
                });
        }

        foreach (var state in new[] { RunState.Succeeded, RunState.Failed, RunState.Cancelled }) {
            VerifyScheduledTerminal(logger, Path.Combine(directory, state.ToString()), state);
        }
        VerifyScheduledTerminal(logger, Path.Combine(directory, "lost-reply"), RunState.Succeeded, lostReply: true);
        VerifyScheduledTerminal(logger, Path.Combine(directory, "not-accepted"), RunState.Idle, lostReply: true);
        VerifyScheduledTerminal(logger, Path.Combine(directory, "next-tick"),
            RunState.Failed, tickBeforeDispatch: true);
        VerifyScheduledTerminal(logger, Path.Combine(directory, "cleanup-collision"),
            RunState.Succeeded, tickBeforeDispatch: true);
    }

    private static void VerifyScheduledTerminal(AppLogger logger, string directory, RunState terminal,
        bool lostReply = false, bool tickBeforeDispatch = false, bool bufferExit = false, bool terminalDuringEnumeration = false, bool cleanupDuringExit = false)
    {
        using var process = Process.GetCurrentProcess();
        var sessionId = (uint)process.SessionId;
        var wts = new SimulatedWts();
        using var session = wts.CreateManager(logger);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        var pipeName = $"Bunshin.Schedule.SelfTest.{Guid.NewGuid():N}";
        Task<ProtocolConnection>? connected = null;
        WorkerAdmissionRecord? record = null;
        string digest = "";
        var preparations = 0;
        var exitRequests = 0;
        var probe = new PresentationProbe();
        var cleanupCollision = tickBeforeDispatch && terminal == RunState.Succeeded;
        var clock = new ScheduleTestClock();
        if (cleanupCollision) {
            CreateIdleSettings(CreateProjectFixture(directory), logger).ForceScheduledStart.Value = true;
        }
        var failPreparation = terminal == RunState.Cancelled;
        var coordinator = new WorkerCoordinator(logger, Path.Combine(directory, "state"), process.MainModule!.FileName,
            pipeName, usePipeAcl: false, captureProcesses: () =>
                record == null ? [] : [new(process.Id, sessionId, Path.GetFileName(process.MainModule!.FileName))],
            launchWorker: (id, instance, secret, _, _) => {
                record = new(instance, secret, id, Environment.ProcessId, digest, DateTime.UtcNow);
                connected = Task.Run(() => WorkerCoordinatorSelfTest.OpenConnectionAsync(pipeName, record, 0, token));
                return Task.FromResult(new VerifiedChildSessionProcessLaunch((uint)process.Id, id, null, null));
            });
        var environment = CreateEnvironment(logger, session, coordinator);
        try {
            WithRuntimeControlWindow(logger, directory, window => {
                var project = ProjectOf(window);
                var target = project.ActiveConfigurationId;
                var name = project.Configurations.Single(c => c.Id == target).Name;
                var failures = new List<string>();
                window.NotificationRequested += (_, n) => failures.Add(n.Title + " / " + n.Message);
                if (failPreparation) {
                    var failed = window.TryStartScheduledRunAsync(target);
                    // 迁移至 WPF / 准备失败重试按钮
                    PumpUntil(() => failed.IsCompleted, "模拟准备失败应结束。");
                    Require(failures.Count == 1 && ShownHeaderControls(window).Contains("RetryEnvironmentButton"),
                        "定时准备失败时控制栏应允许重试准备。");
                    failures.Clear();
                }
                if (terminal == RunState.Succeeded && !lostReply) {
                    ClickOnboarding(window, "NewConfigurationButton");
                    // 迁移至 WPF / 恢复窗口的标签和进度
                    Require(window.TaskPlanEditor.EmptyPlanPanel.IsVisible, "启动前应显示另一份空配置。");
                    window.Hide();
                }
                if (terminal == RunState.Succeeded) {
                    wts.ReportedId = 77;
                    wts.Sessions.Add(77);
                }
                var start = window.TryStartScheduledRunAsync(target);
                // 迁移至 WorkerCoordinatorSelfTest / 定时 Worker 接纳
                PumpUntil(() => connected?.IsCompleted == true || start.IsCompleted, "定时执行应接纳新 Worker。");
                var pipe = connected!.GetAwaiter().GetResult();
                var request = Task.Run(() => WorkerCoordinatorSelfTest.ReadRequestAsync(
                    pipe, ProtocolOperations.RunStart, token));
                // 迁移至 VerifyRunLifecycle / 定时计划提交
                PumpUntil(() => request.IsCompleted, "Worker 应收到此定时配置的计划。");
                var payload = ProtocolJson.Deserialize<RunStartRequest>(request.Result.Data);
                var own = WorkerCoordinatorSelfTest.CreateActiveRun(
                    new(payload.RunId, payload.Plan, payload.PlanDigest));
                var snapshot = WorkerCoordinatorSelfTest.CreateSnapshot(record!, 0);
                var logs = new System.Collections.Concurrent.ConcurrentQueue<string>();
                void Record(object? _, LogEntry entry) => logs.Enqueue(entry.Message);
                void ExpectBusy(string? expected = null)
                {
                    logger.EntryWritten += Record;
                    try {
                        var skipped = window.TryStartScheduledRunAsync(target);
                        // 迁移至 VerifyRunLifecycle / 普通候选遇忙
                        Require(skipped.IsCompletedSuccessfully
                            && skipped.Result.Disposition == ScheduledRunDisposition.Busy
                            && (expected is null || window.LogLines.OfType<LogEntry>().First().Message
                                == $"跳过「{name}」：{expected}")
                            && logs.Any(line => line.Contains(name) && line.Contains(skipped.Result.Reason!)),
                            "遇忙或 Worker 状态不确定应立即跳过并记明原因。");
                    } finally {
                        logger.EntryWritten -= Record;
                    }
                }
                if (!cleanupCollision) {
                    // 迁移至 VerifyRunLifecycle / 提交期间遇忙
                    ExpectBusy("正在运行其他任务");
                }
                void Publish(RunSnapshot last, long revision)
                {
                    Task.Run(() => pipe.WriteAsync(StateEvent(snapshot with { StateRevision = revision,
                        LastRun = last }), token)).GetAwaiter().GetResult();
                    // 迁移至 WorkerCoordinatorSelfTest / 快照修订
                    PumpUntil(() => coordinator.Snapshot.WorkerSnapshot?.StateRevision >= revision,
                        "必须观察到新的 Worker 快照。");
                    PumpOnboarding();
                }
                var done = own with { State = terminal, EndedAtUtc = DateTime.UtcNow,
                    Items = terminal == RunState.Failed ? [own.Items.Single() with { State = PlanItemState.Failed }]
                        : own.Items,
                    Error = terminal == RunState.Failed ? new("fixture", "计划项模拟失败") : null };
                Publish(done with { RunId = Guid.NewGuid(), State = RunState.Succeeded }, 2);
                // 迁移至 VerifyRunLifecycle / 非目标 Run 成功
                Require(wts.ReportedId == sessionId, "另一 Run 的成功不得关闭分身。");
                if (lostReply) {
                    pipe.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    // 迁移至 VerifyRunLifecycle / 丢应答后断线
                    PumpUntil(() => start.IsCompleted
                        && coordinator.Snapshot.Observation == WorkerObservation.IpcDisconnected,
                        "丢失回包后应保留现场等待 Worker 重连。");
                    // 迁移至 VerifyRunLifecycle / 断线期间遇忙
                    ExpectBusy();
                    connected = Task.Run(() => WorkerCoordinatorSelfTest.OpenConnectionAsync(
                        pipeName, record!, 0, token, activeRun: terminal == RunState.Idle ? null : own,
                        stateRevision: 3));
                    // 迁移至 WorkerCoordinatorSelfTest / 同一 Worker 重连
                    PumpUntil(() => connected.IsCompleted && coordinator.Snapshot.SnapshotFresh, "模拟 Worker 应重连。");
                    pipe = connected.Result;
                    var restored = snapshot with { ActiveRun = terminal == RunState.Idle ? null : own,
                        RunState = terminal == RunState.Idle ? RunState.Idle : own.State, StateRevision = 3 };
                    if (terminal == RunState.Idle)
                        Require(failures.Count == 0, "普通空闲快照不能证明启动未登记。");
                    var confirmation = Task.Run(() => WorkerCoordinatorSelfTest.AnswerRunQueryAsync(pipe, restored, token));
                    PumpUntil(() => confirmation.IsCompleted, "重连后应查询目标 Run 的登记状态。");
                    confirmation.GetAwaiter().GetResult();
                    if (terminal == RunState.Idle) {
                        // 迁移至 VerifyRunLifecycle / 普通重连空闲快照后仍待确认，此时失败通知应为零（#124）。
                        PumpUntil(() => failures.Count > 0, "重连后确认未接纳应通知启动失败。");
                        PumpOnboarding();
                        // 收到覆盖本次发送的有序查询结果 runRegistered=false 后，才报告一次启动失败并保留分身。
                        Require(failures.Count == 1 && failures[0].Contains(name) && session.HasChildSession,
                            "启动未被接纳时应保留分身且只报告一次。");
                        return;
                    }
                    // 迁移至 VerifyRunLifecycle / 重连后目标仍在运行
                    ExpectBusy();
                } else if (terminal == RunState.Succeeded) {
                    // Keep IPC up and omit only the start reply: the state event is authoritative.
                    var running = snapshot with { StateRevision = 3, RunState = RunState.Running, ActiveRun = own };
                    Task.Run(() => pipe.WriteAsync(StateEvent(running), token)).GetAwaiter().GetResult();
                    if (bufferExit || cleanupDuringExit)
                        Task.Run(() => pipe.WriteAsync(WireEnvelope.Response(request.Result.Operation, request.Result.RequestId!.Value,
                            new RunStartResponse("accepted")), token)).GetAwaiter().GetResult();
                    // The observed Active already proves acceptance even with the start reply missing.
                    PumpUntil(() => start.IsCompleted, "丢失回包后应按快照确认。");
                    // 迁移至 WPF / 托盘隐藏
                    Require(!window.IsVisible, "定时启动和进度更新应保持窗口隐藏。");
                    window.Show();
                    // 迁移至 WPF / 恢复窗口的标签和进度
                    PumpUntil(() => window.RunProgressPanel.IsVisible, "打开窗口后应显示当前运行的进度。");
                    Require(window.TaskPlanEditor.ConfigurationTabs.SelectedItem is System.Windows.Controls.TabItem
                        { Tag: Guid selected } && selected == target && !window.TaskPlanEditor.EmptyPlanPanel.IsVisible,
                        "恢复可见时目标标签、执行计划与正在运行的任务应一致。");
                    // 迁移至 VerifyRunLifecycle / 丢应答但已运行
                    Require(failures.Count == 0 && start.Result.Reason is null, "Worker 已运行时回复超时不得报告启动失败。");
                } else {
                    var accepted = WireEnvelope.Response(request.Result.Operation, request.Result.RequestId!.Value,
                        new RunStartResponse("accepted"));
                    Task.Run(() => pipe.WriteAsync(accepted, token)).GetAwaiter().GetResult();
                    // 迁移至 VerifyRunLifecycle / 定时计划提交
                    PumpUntil(() => start.IsCompleted, "定时执行应返回此次提交的身份。");
                    // 迁移至 VerifyRunLifecycle / 已接纳后遇忙
                    ExpectBusy();
                    // 迁移至 VerifyRunLifecycle / 普通候选遇忙
                    Require(failures.Count == 0, "普通冲突只能记日志。");
                }
                // 迁移至 VerifyRunLifecycle / 定时计划提交
                Require(start.Result is { Disposition: ScheduledRunDisposition.Admitted, RunId: { } runId }
                    && runId == own.RunId, "启动结果必须对应提交给 Worker 的计划。");
                if (terminal == RunState.Cancelled) {
                    // The pending start is resolved by a Running event during the synchronous Windows inspection.
                    wts.DuringEnumeration = () => {
                        wts.DuringEnumeration = null;
                        var active = snapshot with { StateRevision = 3, ActiveRun = own, RunState = RunState.Running };
                        Task.Run(() => pipe.WriteAsync(StateEvent(active), token)).GetAwaiter().GetResult();
                        // 迁移至 VerifyRunLifecycle / own 已获 accepted，另一机会在 WTS 枚举期间观察到同一 Run 进入 Running。
                        AwaitWorker(coordinator, s => s.WorkerSnapshot?.ActiveRun?.RunId == own.RunId,
                            "Worker 应在 WTS 查询期间报告 Active Run。");
                    };
                    // 迁移后仍须断言：从提交待观察转为已在运行，另一机会始终返回 Busy。
                    ExpectBusy();
                    Publish(done, 4);
                } else if (terminalDuringEnumeration) {
                    var running = snapshot with { StateRevision = 3, ActiveRun = own, RunState = RunState.Running };
                    Task.Run(() => pipe.WriteAsync(StateEvent(running), token)).GetAwaiter().GetResult();
                    PumpUntil(() => HeaderShows(window, "StopTaskHeaderButton", enabled: true), "定时入口应已观察到旧 Run 运行中。");
                    wts.DuringEnumeration = () => {
                        wts.DuringEnumeration = null;
                        var ended = snapshot with { StateRevision = 4, LastRun = done };
                        Task.Run(() => pipe.WriteAsync(StateEvent(ended), token)).GetAwaiter().GetResult();
                        AwaitWorker(coordinator, s => s.WorkerSnapshot?.LastRun?.RunId == own.RunId,
                            "本次 WTS 枚举期间应收到旧 Run 的终态。");
                    };
                    var next = window.TryStartScheduledRunAsync(target);
                    PumpUntil(() => next.IsCompleted, "普通定时应继续处理本次机会。");
                    Require(next.Result.Disposition == ScheduledRunDisposition.Admitted && preparations == 2,
                        "枚举分身期间到达的终态应允许下一班执行。");
                    return;
                } else if (tickBeforeDispatch) {
                    // Advance IPC on the pool while the dispatcher is still handling the clock tick.
                    var received = snapshot with { LastRun = done, StateRevision = cleanupCollision ? 4 : 3 };
                    if (cleanupCollision) {
                        project.AddSchedulePolicy(target, "早班");
                        wts.DuringLogoff = _ => throw new IOException("模拟旧分身注销失败");
                    }
                    Task.Run(() => pipe.WriteAsync(StateEvent(received), token)).GetAwaiter().GetResult();
                    // 迁移至 VerifyRunLifecycle / next-tick、cleanup-collision
                    AwaitWorker(coordinator, s => s.WorkerSnapshot?.LastRun?.RunId == own.RunId,
                        "下一分钟已到时 Worker 应已报告此前 Run 的失败。");
                    var completed = new List<Guid>();
                    window.ScheduledRunCompleted += (_, run) => completed.Add(run.RunId);
                    if (cleanupCollision) {
                        clock.Advance(TimeSpan.FromMinutes(1));
                        var check = window.CheckScheduledRunsAsync();
                        // 迁移至 VerifyRunLifecycle / cleanup-collision
                        PumpUntil(() => check.IsCompleted, "旧定时 Run 收尾失败后应结束新的定时机会。");
                        check.GetAwaiter().GetResult();
                        // 迁移至 VerifyRunLifecycle / cleanup-collision
                        Require(preparations == 1 && wts.LogoffAttempts.SequenceEqual([77u, sessionId])
                            && completed.SequenceEqual([own.RunId])
                            && failures.Count(line => line.StartsWith("定时任务未能启动 / ")) == 1,
                            "上次定时成功的收尾与新机会相遇，注销失败只允许尝试一次并报告本次未能启动。");
                        return;
                    }
                    var next = window.TryStartScheduledRunAsync(target);
                    // 迁移至 VerifyRunLifecycle / next-tick
                    PumpUntil(() => next.IsCompleted, "下一次机会应消化上次终态，再在新环境中启动。");
                    Require(next.Result.Disposition == ScheduledRunDisposition.Admitted
                        && completed.SequenceEqual([own.RunId])
                        && failures.Count(line => line.StartsWith($"「{name}」运行失败 / ")) == 1
                        && wts.LogoffAttempts.SequenceEqual([sessionId]), "下次清理不得吞掉上一 Run 的失败提醒。");
                    return;
                } else {
                    var completions = new List<Guid>();
                    window.ScheduledRunCompleted += (_, run) => {
                        completions.Add(run.RunId);
                        if (lostReply && completions.Count == 1) {
                            wts.DuringEnumeration = () => {
                                wts.DuringEnumeration = null;
                                pipe.DisposeAsync().AsTask().GetAwaiter().GetResult();
                                // 迁移至 VerifyRunLifecycle / 收尾时断线重连
                                AwaitWorker(coordinator, s => s.Observation == WorkerObservation.IpcDisconnected,
                                    "成功后检查分身身份期间 Worker 可暂时断线。");
                            };
                        }
                    };
                    if (cleanupDuringExit) {
                        window.Hide();
                        wts.DuringLogoff = _ => {
                            window.Dispatcher.Invoke(() => {
                                environment.Closing = true;
                                window.SetExitInProgress(true);
                            });
                            throw new IOException("模拟退出确认期间注销失败");
                        };
                    }
                    if (bufferExit) window.SetExitInProgress(true);
                    Publish(done, terminal == RunState.Succeeded ? 4 : 3);
                    if (cleanupDuringExit) {
                        PumpUntil(() => window.LogLines.OfType<LogEntry>().Any(e => e.Message.Contains("任务已完成，桌面分身未能关闭")),
                            "退出确认期间应已记录收尾失败。");
                        int CleanupBalloons() => probe.Balloons.Count(n => n.Failed && n.Message.Contains("桌面分身未能关闭"));
                        Require(CleanupBalloons() == 0, "退出期间应暂存收尾失败提醒。");
                        environment.Closing = false;
                        window.SetExitInProgress(false);
                        Publish(done, 5);
                        Require(CleanupBalloons() == 1 && wts.LogoffAttempts.SequenceEqual([77u, sessionId]),
                            "取消退出后应向托盘补发一次收尾失败提醒。");
                        return;
                    }
                    if (bufferExit) {
                        Publish(done, 5);
                        Require(completions.Count == 0 && wts.LogoffAttempts.SequenceEqual([77u]),
                            "退出期间重复终态不应提前完成或收尾。");
                        window.SetExitInProgress(false);
                        PumpUntil(() => completions.Count > 0, "取消退出后应交付已缓冲的终态。");
                    }
                    if (lostReply) {
                        // 迁移至 VerifyRunLifecycle / 收尾时断线重连
                        Require(session.HasChildSession, "未重新确认 Worker 身份前必须保留现场。");
                        connected = Task.Run(() => WorkerCoordinatorSelfTest.OpenConnectionAsync(
                            pipeName, record!, 0, token, stateRevision: 5, lastRun: done));
                        // 迁移至 VerifyRunLifecycle / 收尾时断线重连
                        PumpUntil(() => connected.IsCompleted && !session.HasChildSession,
                            "同一 Worker 重连并确认定时 Run 成功后应继续收尾。");
                    }
                    // 迁移至 VerifyRunLifecycle / 三种定时终态
                    Require(completions.SequenceEqual([own.RunId]), "同一 Run 应只发出一次终态。");
                }
                Require(exitRequests == 0, "定时 Run 的终态不得触发开机自动执行的退出。");
                if (terminal == RunState.Succeeded) {
                    // 迁移至 VerifyRunLifecycle / 成功收尾
                    PumpUntil(() => !session.HasChildSession && window.LogLines.OfType<LogEntry>()
                        .Any(e => e.Message == "全部完成，已关闭桌面分身"), "仅本次成功后应完成分身收尾。");
                    // 迁移至 VerifyRunLifecycle / 换场与成功收尾动态
                    Require(window.LogLines.OfType<LogEntry>().Any(e => e.Message == "已关闭旧的桌面分身")
                        && window.LogLines.OfType<LogEntry>().Count(e => e.Message == "全部完成，已关闭桌面分身") == 1,
                        "成功收尾和旧分身关闭应分别出现在运行动态。");
                    // 迁移至 VerifyRunLifecycle / 成功收尾
                    Require(wts.LogoffAttempts.SequenceEqual([77u, sessionId]), "应先清理旧分身，成功后只关闭新分身。");
                } else {
                    // 迁移至 VerifyRunLifecycle / 失败与手停保留现场
                    Require(wts.LogoffAttempts.Count == 0 && session.HasChildSession, "失败或手停应保留现场。");
                    var label = own.Plan.Items.Single().TaskLabel;
                    // 迁移至 VerifyRunLifecycle / 失败与取消通知
                    Require(terminal == RunState.Cancelled ? failures.Count == 0
                        : failures.Single() == $"「{name}」运行失败 / 失败任务：{label}。打开 Bunshin 查看运行动态。",
                        "任务失败通知必须包含配置与失败原因。");
                    if (terminal == RunState.Cancelled) {
                        // 迁移至 WPF / 准备失败重试按钮
                        Require(!ShownHeaderControls(window).Contains("RetryEnvironmentButton"),
                            "后续准备成功后，手停任务应清除此前准备失败的重试状态。");
                    }
                    wts.ReportedId = sessionId + 97;
                    wts.Sessions.Add(wts.ReportedId.Value);
                    // 迁移至 VerifyRunLifecycle / 换场时分身身份不匹配
                    ExpectBusy("运行环境状态未知");
                    Require(wts.LogoffAttempts.Count == 0, "已知空闲的 Worker 不属于当前 WTS 分身时不得注销。");
                    wts.ReportedId = sessionId;
                    var key = new RunKey(record!.WorkerInstanceId, own.RunId);
                    var retry = coordinator.SendStartAsync(new(Guid.NewGuid(), Guid.NewGuid(), key, payload.PlanDigest),
                        new(own.RunId, payload.Plan, payload.PlanDigest), token);
                    var duplicate = Task.Run(async () => {
                        var request = await WorkerCoordinatorSelfTest.ReadRequestAsync(
                            pipe, ProtocolOperations.RunStart, token, snapshot with { LastRun = done, StateRevision = 4 });
                        await pipe.WriteAsync(WireEnvelope.Response(request.Operation, request.RequestId!.Value,
                            new RunStartResponse("already_accepted")), token);
                    });
                    // 迁移至 WorkerCoordinatorSelfTest / 已接纳的同 ID 重试
                    PumpUntil(() => retry.IsCompleted && duplicate.IsCompleted, "已终结的同 ID 重试应返回已接纳。");
                    Require(retry.Result.StartReply == StartReplyKind.AlreadyAccepted, "应保留 Worker 对同一 Run 的幂等答复。");
                    var next = window.TryStartScheduledRunAsync(target);
                    // 迁移至 VerifyRunLifecycle / 已知终态后的新机会
                    PumpUntil(() => next.IsCompleted, "相同 Run 的终态已知时无需再等一次快照。");
                    Require(next.Result.Disposition == ScheduledRunDisposition.Admitted,
                        "重试已终结的 Run 之后仍应可以运行新的配置。");
                }
            }, session: session, coordinator: coordinator, environment: environment,
                scheduleClock: clock, quitAfterRun: () => exitRequests++, presentation: probe.Presentation,
                prepareEnvironment: async (project, options, show) => {
                    if (failPreparation) {
                        failPreparation = false;
                        throw new InvalidOperationException("模拟首次准备失败");
                    }
                    if (++preparations > 1) {
                        throw new InvalidOperationException("模拟第二次准备结束");
                    }
                    // 迁移至 VerifyPrepareAndRunWiring / 定时换场后隐藏准备
                    Require(!session.HasChildSession && !show, "准备前应已确认没有旧桌面分身。");
                    wts.ReportedId = sessionId;
                    wts.Sessions.Add(sessionId);
                    digest = project.RuntimeProfileDigest;
                    await coordinator.PrepareWorkerAsync(sessionId, project, token, options());
                });
        } finally {
            timeout.Cancel();
            if (connected?.IsCompletedSuccessfully == true) {
                connected.Result.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            Task.Run(async () => await coordinator.DisposeAsync()).GetAwaiter().GetResult();
        }
    }
}
