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
            Require(result.Disposition == ScheduledRunDisposition.Invalid
                && ProjectOf(window).ActiveConfigurationId == before
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
            PumpUntil(() => run.IsCompleted, "旧分身清理未确认时应结束本轮。");
            Require(run.Result.Disposition == ScheduledRunDisposition.Admitted
                && ProjectOf(window).ActiveConfigurationId == target && !prepared
                && wts.LogoffAttempts.SequenceEqual([7u])
                && !window.IsVisible && window.ConfigurationTabs.SelectedItem is System.Windows.Controls.TabItem
                    { Tag: Guid selected } && selected == target
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
                PumpUntil(() => schedule.IsCompleted && boot.IsCompleted, "两种启动都应返回。");
                var winner = bootFirst ? daily : weekly;
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
    }

    private static void VerifyScheduledTerminal(AppLogger logger, string directory, RunState terminal,
        bool lostReply = false, bool tickBeforeDispatch = false)
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
        var failPreparation = terminal == RunState.Cancelled;
        var coordinator = new WorkerCoordinator(logger, Path.Combine(directory, "state"), process.MainModule!.FileName,
            pipeName, usePipeAcl: false, captureProcesses: () =>
                record == null ? [] : [new(process.Id, sessionId, Path.GetFileName(process.MainModule!.FileName))],
            launchWorker: (id, instance, secret, _, _) => {
                record = new(instance, secret, id, Environment.ProcessId, digest, DateTime.UtcNow);
                connected = Task.Run(() => WorkerCoordinatorSelfTest.OpenConnectionAsync(pipeName, record, 0, token));
                return Task.FromResult(new VerifiedChildSessionProcessLaunch((uint)process.Id, id, null, null));
            });
        try {
            WithRuntimeControlWindow(logger, directory, window => {
                var project = ProjectOf(window);
                var target = project.ActiveConfigurationId;
                var name = project.Configurations.Single(c => c.Id == target).Name;
                var failures = new List<string>();
                window.NotificationRequested += (_, n) => failures.Add(n.Title + " / " + n.Message);
                if (failPreparation) {
                    var failed = window.TryStartScheduledRunAsync(target);
                    PumpUntil(() => failed.IsCompleted, "模拟准备失败应结束。");
                    Require(failures.Count == 1 && ShownHeaderControls(window).Contains("RetryEnvironmentButton"),
                        "定时准备失败时控制栏应允许重试准备。");
                    failures.Clear();
                }
                if (terminal == RunState.Succeeded && !lostReply) {
                    ClickOnboarding(window, "NewConfigurationButton");
                    Require(window.EmptyPlanPanel.IsVisible, "启动前应显示另一份空配置。");
                    window.Hide();
                }
                if (terminal == RunState.Succeeded) {
                    wts.ReportedId = 77;
                    wts.Sessions.Add(77);
                }
                var start = window.TryStartScheduledRunAsync(target);
                PumpUntil(() => connected?.IsCompleted == true || start.IsCompleted, "定时执行应接纳新 Worker。");
                var pipe = connected!.GetAwaiter().GetResult();
                var request = Task.Run(() => WorkerCoordinatorSelfTest.ReadRequestAsync(
                    pipe, ProtocolOperations.RunStart, token));
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
                ExpectBusy("正在运行其他任务");
                void Publish(RunSnapshot last, long revision)
                {
                    Task.Run(() => pipe.WriteAsync(StateEvent(snapshot with { StateRevision = revision,
                        LastRun = last }), token)).GetAwaiter().GetResult();
                    PumpUntil(() => coordinator.Snapshot.WorkerSnapshot?.StateRevision >= revision,
                        "必须观察到新的 Worker 快照。");
                    PumpOnboarding();
                }
                var done = own with { State = terminal, EndedAtUtc = DateTime.UtcNow,
                    Items = terminal == RunState.Failed ? [own.Items.Single() with { State = PlanItemState.Failed }]
                        : own.Items,
                    Error = terminal == RunState.Failed ? new("fixture", "计划项模拟失败") : null };
                Publish(done with { RunId = Guid.NewGuid(), State = RunState.Succeeded }, 2);
                Require(wts.ReportedId == sessionId, "另一 Run 的成功不得关闭分身。");
                if (lostReply) {
                    pipe.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    PumpUntil(() => start.IsCompleted
                        && coordinator.Snapshot.Observation == WorkerObservation.IpcDisconnected,
                        "丢失回包后应保留现场等待 Worker 重连。");
                    ExpectBusy();
                    connected = Task.Run(() => WorkerCoordinatorSelfTest.OpenConnectionAsync(
                        pipeName, record!, 0, token, activeRun: terminal == RunState.Idle ? null : own,
                        stateRevision: 3));
                    PumpUntil(() => connected.IsCompleted && coordinator.Snapshot.SnapshotFresh, "模拟 Worker 应重连。");
                    pipe = connected.Result;
                    if (terminal == RunState.Idle) {
                        PumpUntil(() => failures.Count > 0, "重连后确认未接纳应通知启动失败。");
                        PumpOnboarding();
                        Require(failures.Count == 1 && failures[0].Contains(name) && session.HasChildSession,
                            "启动未被接纳时应保留分身且只报告一次。");
                        return;
                    }
                    ExpectBusy();
                } else if (terminal == RunState.Succeeded) {
                    // Keep IPC up and omit only the start reply: the state event is authoritative.
                    var running = snapshot with { StateRevision = 3, RunState = RunState.Running, ActiveRun = own };
                    Task.Run(() => pipe.WriteAsync(StateEvent(running), token)).GetAwaiter().GetResult();
                    var refresh = Task.Run(async () => {
                        var read = await WorkerCoordinatorSelfTest.ReadRequestAsync(
                            pipe, ProtocolOperations.WorkerGetSnapshot, token);
                        await pipe.WriteAsync(WireEnvelope.Response(read.Operation, read.RequestId!.Value,
                            new GetSnapshotResponse(running)), token);
                    });
                    PumpUntil(() => start.IsCompleted && refresh.IsCompletedSuccessfully, "丢失回包后应按快照确认。");
                    Require(!window.IsVisible, "定时启动和进度更新应保持窗口隐藏。");
                    window.Show();
                    PumpUntil(() => window.RunProgressPanel.IsVisible, "打开窗口后应显示当前运行的进度。");
                    Require(window.ConfigurationTabs.SelectedItem is System.Windows.Controls.TabItem
                        { Tag: Guid selected } && selected == target && !window.EmptyPlanPanel.IsVisible,
                        "恢复可见时目标标签、执行计划与正在运行的任务应一致。");
                    Require(failures.Count == 0 && start.Result.Reason is null, "Worker 已运行时回复超时不得报告启动失败。");
                } else {
                    var accepted = WireEnvelope.Response(request.Result.Operation, request.Result.RequestId!.Value,
                        new RunStartResponse("accepted"));
                    Task.Run(() => pipe.WriteAsync(accepted, token)).GetAwaiter().GetResult();
                    PumpUntil(() => start.IsCompleted, "定时执行应返回此次提交的身份。");
                    ExpectBusy();
                    Require(failures.Count == 0, "普通冲突只能记日志。");
                }
                Require(start.Result is { Disposition: ScheduledRunDisposition.Admitted, RunId: { } runId }
                    && runId == own.RunId, "启动结果必须对应提交给 Worker 的计划。");
                if (terminal == RunState.Cancelled) {
                    // The pending start is resolved by a Running event during the synchronous Windows inspection.
                    wts.DuringEnumeration = () => {
                        wts.DuringEnumeration = null;
                        var active = snapshot with { StateRevision = 3, ActiveRun = own, RunState = RunState.Running };
                        Task.Run(() => pipe.WriteAsync(StateEvent(active), token)).GetAwaiter().GetResult();
                        AwaitWorker(coordinator, s => s.WorkerSnapshot?.ActiveRun?.RunId == own.RunId,
                            "Worker 应在 WTS 查询期间报告 Active Run。");
                    };
                    ExpectBusy();
                    Publish(done, 4);
                } else if (tickBeforeDispatch) {
                    // Advance IPC on the pool while the dispatcher is still handling the clock tick.
                    var received = snapshot with { LastRun = done, StateRevision = 3 };
                    Task.Run(() => pipe.WriteAsync(StateEvent(received), token)).GetAwaiter().GetResult();
                    AwaitWorker(coordinator, s => s.WorkerSnapshot?.LastRun?.RunId == own.RunId,
                        "下一分钟已到时 Worker 应已报告此前 Run 的失败。");
                    var completed = new List<Guid>();
                    window.ScheduledRunCompleted += (_, run) => completed.Add(run.RunId);
                    var next = window.TryStartScheduledRunAsync(target);
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
                                AwaitWorker(coordinator, s => s.Observation == WorkerObservation.IpcDisconnected,
                                    "成功后检查分身身份期间 Worker 可暂时断线。");
                            };
                        }
                    };
                    Publish(done, terminal == RunState.Succeeded ? 4 : 3);
                    if (lostReply) {
                        Require(session.HasChildSession, "未重新确认 Worker 身份前必须保留现场。");
                        connected = Task.Run(() => WorkerCoordinatorSelfTest.OpenConnectionAsync(
                            pipeName, record!, 0, token, stateRevision: 5, lastRun: done));
                        PumpUntil(() => connected.IsCompleted && !session.HasChildSession,
                            "同一 Worker 重连并确认定时 Run 成功后应继续收尾。");
                    }
                    Require(completions.SequenceEqual([own.RunId]), "同一 Run 应只发出一次终态。");
                }
                var quit = new QuitAfterRunTracker();
                quit.Await(Guid.NewGuid());
                var observed = new WorkerCoordinatorSnapshot(WorkerObservation.Connected, true,
                    snapshot with { LastRun = done }, "已结束");
                window.ObserveRunNotification(observed);
                Require(!quit.Observe(observed),
                    "定时 Run 的终态不得触发开机自动执行的退出。");
                if (terminal == RunState.Succeeded) {
                    PumpUntil(() => !session.HasChildSession, "仅本次成功后应结束分身。");
                    Require(window.LogLines.OfType<LogEntry>().Any(e => e.Message == "已关闭旧的桌面分身")
                        && window.LogLines.OfType<LogEntry>().Count(e => e.Message == "全部完成，已关闭桌面分身") == 1,
                        "成功收尾和旧分身关闭应分别出现在运行动态。");
                    Require(wts.LogoffAttempts.SequenceEqual([77u, sessionId]), "应先清理旧分身，成功后只关闭新分身。");
                } else {
                    Require(wts.LogoffAttempts.Count == 0 && session.HasChildSession, "失败或手停应保留现场。");
                    var label = own.Plan.Items.Single().TaskLabel;
                    Require(terminal == RunState.Cancelled ? failures.Count == 0
                        : failures.Single() == $"「{name}」运行失败 / 失败任务：{label}。打开 Bunshin 查看运行动态。",
                        "任务失败通知必须包含配置与失败原因。");
                    if (terminal == RunState.Cancelled) {
                        Require(!ShownHeaderControls(window).Contains("RetryEnvironmentButton"),
                            "后续准备成功后，手停任务应清除此前准备失败的重试状态。");
                    }
                    wts.ReportedId = sessionId + 97;
                    wts.Sessions.Add(wts.ReportedId.Value);
                    ExpectBusy("运行环境状态未知");
                    Require(wts.LogoffAttempts.Count == 0, "已知空闲的 Worker 不属于当前 WTS 分身时不得注销。");
                    wts.ReportedId = sessionId;
                    var retry = coordinator.StartRunAsync(new(own.RunId, payload.Plan, payload.PlanDigest));
                    var duplicate = Task.Run(async () => {
                        var request = await WorkerCoordinatorSelfTest.ReadRequestAsync(
                            pipe, ProtocolOperations.RunStart, token);
                        await pipe.WriteAsync(WireEnvelope.Response(request.Operation, request.RequestId!.Value,
                            new RunStartResponse("already_accepted")), token);
                    });
                    PumpUntil(() => retry.IsCompleted && duplicate.IsCompleted, "已终结的同 ID 重试应返回已接纳。");
                    Require(retry.Result.Disposition == "already_accepted", "应保留 Worker 对同一 Run 的幂等答复。");
                    var next = window.TryStartScheduledRunAsync(target);
                    PumpUntil(() => next.IsCompleted, "相同 Run 的终态已知时无需再等一次快照。");
                    Require(next.Result.Disposition == ScheduledRunDisposition.Admitted,
                        "重试已终结的 Run 之后仍应可以运行新的配置。");
                }
            }, session: session, coordinator: coordinator, environment: CreateEnvironment(logger, session, coordinator),
                prepareEnvironment: async (project, options, show) => {
                    if (failPreparation) {
                        failPreparation = false;
                        throw new InvalidOperationException("模拟首次准备失败");
                    }
                    if (++preparations > 1) {
                        throw new InvalidOperationException("模拟第二次准备结束");
                    }
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
