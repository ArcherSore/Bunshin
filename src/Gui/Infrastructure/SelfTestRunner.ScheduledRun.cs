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

        VerifyScheduledTerminal(logger, Path.Combine(directory, "failed"), RunState.Failed);
        VerifyScheduledTerminal(logger, Path.Combine(directory, "cancelled"), RunState.Cancelled);
        VerifyScheduledTerminal(logger, Path.Combine(directory, "lost-reply"), RunState.Succeeded, lostReply: true);
        VerifyScheduledTerminal(logger, Path.Combine(directory, "not-accepted"), RunState.Idle);
    }

    private enum SettlementOrder { Normal, Reconnect, Preview, Tray, Gap, Update, ManualEndAfterUpdate }

    private static void VerifyScheduledTerminal(AppLogger logger, string directory, RunState terminal,
        bool lostReply = false, bool tickBeforeDispatch = false, bool bufferExit = false, bool terminalDuringEnumeration = false,
        bool cleanupDuringExit = false, bool forceCollision = true, bool failCleanup = true,
        SettlementOrder order = SettlementOrder.Normal)
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
        var requestClock = new ScheduleTestClock();
        if (cleanupCollision && forceCollision) {
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
            }, requestClock: bufferExit || terminal == RunState.Idle ? requestClock : null);
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
                    PumpUntil(() => failed.IsCompleted, "模拟准备失败应结束。");
                    Require(failures.Count == 1 && ShownHeaderControls(window).Contains("RetryEnvironmentButton"),
                        "定时准备失败时控制栏应允许重试准备。");
                    failures.Clear();
                }
                if (terminal == RunState.Succeeded && !lostReply) {
                    ClickOnboarding(window, "NewConfigurationButton");
                    Require(window.TaskPlanEditor.EmptyPlanPanel.IsVisible, "启动前应显示另一份空配置。");
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
                if (!cleanupCollision) {
                    ExpectBusy("正在运行其他任务");
                }
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
                        pipeName, record!, 0, token, activeRun: own, stateRevision: 3));
                    PumpUntil(() => connected.IsCompleted && coordinator.Snapshot.SnapshotFresh, "模拟 Worker 应重连。");
                    pipe = connected.Result;
                    var restored = snapshot with { ActiveRun = own, RunState = own.State, StateRevision = 3 };
                    var confirmation = Task.Run(() => WorkerCoordinatorSelfTest.AnswerRunQueryAsync(pipe, restored, token));
                    PumpUntil(() => confirmation.IsCompleted, "重连后应查询目标 Run 的登记状态。");
                    confirmation.GetAwaiter().GetResult();
                    ExpectBusy();
                } else if (terminal == RunState.Idle) {
                    requestClock.Advance(TimeSpan.FromSeconds(16));
                    PumpUntil(() => start.IsCompleted, "应答超时后应返回本次定时提交。");
                    Require(failures.Count == 0, "仅凭普通空闲快照和应答超时不能报告启动失败。");
                    var confirmation = Task.Run(() => WorkerCoordinatorSelfTest.AnswerRunQueryAsync(
                        pipe, snapshot with { StateRevision = 3 }, token));
                    PumpUntil(() => confirmation.IsCompleted && failures.Count > 0, "确认未登记应通知启动失败。");
                    confirmation.GetAwaiter().GetResult();
                    Require(failures.Count == 1 && failures[0].Contains(name) && session.HasChildSession,
                        "启动未被接纳时应保留分身且只报告一次。");
                    return;
                } else if (terminal == RunState.Succeeded) {
                    var running = snapshot with { StateRevision = 3, RunState = RunState.Running, ActiveRun = own };
                    Task.Run(() => pipe.WriteAsync(StateEvent(running), token)).GetAwaiter().GetResult();
                    if (bufferExit) {
                        AwaitWorker(coordinator, view => view.WorkerSnapshot?.ActiveRun?.RunId == own.RunId,
                            "应在开始应答超时前观察到目标 Run 已运行。");
                        requestClock.Advance(TimeSpan.FromSeconds(16));
                    } else {
                        Task.Run(() => pipe.WriteAsync(WireEnvelope.Response(request.Result.Operation, request.Result.RequestId!.Value,
                            new RunStartResponse("accepted")), token)).GetAwaiter().GetResult();
                    }
                    PumpUntil(() => start.IsCompleted, "观察到运行中的任务后应完成定时提交。");
                    Require(!window.IsVisible, "定时启动和进度更新应保持窗口隐藏。");
                    window.Show();
                    PumpUntil(() => window.RunProgressPanel.IsVisible, "打开窗口后应显示当前运行的进度。");
                    Require(window.TaskPlanEditor.ConfigurationTabs.SelectedItem is System.Windows.Controls.TabItem
                        { Tag: Guid selected } && selected == target && !window.TaskPlanEditor.EmptyPlanPanel.IsVisible,
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
                    var task = project.SelectedTaskNames.First();
                    var nextTarget = project.CreateConfiguration();
                    project.RenameConfiguration(nextTarget, "下一班");
                    project.AddItem(PlanItemKind.MaaTask, task);
                    project.AddSchedulePolicy(nextTarget, "早班");
                    project.ActivateConfiguration(target);
                    if (cleanupCollision && failCleanup) wts.DuringLogoff = _ => throw new IOException("模拟旧分身注销失败");
                    if (order == SettlementOrder.Reconnect) {
                        received = received with { StateRevision = 6 };
                        pipe.DisposeAsync().AsTask().GetAwaiter().GetResult();
                        AwaitWorker(coordinator, s => s.Observation == WorkerObservation.IpcDisconnected, "A 运行期间应已断线。");
                        var targeted = new TaskCompletionSource<Guid?>(TaskCreationOptions.RunContinuationsAsynchronously);
                        var sync = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        connected = Task.Run(() => WorkerCoordinatorSelfTest.OpenConnectionAsync(
                            pipeName, record!, 0, token, lastRun: done, stateRevision: received.StateRevision,
                            beforeSnapshotReply: async peer => {
                                await peer.WriteAsync(StateEvent(snapshot with { StateRevision = 4, ActiveRun = own,
                                    RunState = RunState.Running }), token);
                                var q = await WorkerCoordinatorSelfTest.ReadRequestAsync(peer, ProtocolOperations.WorkerGetSnapshot, token);
                                await peer.WriteAsync(StateEvent(received), token);
                                targeted.SetResult(ProtocolJson.Deserialize<GetSnapshotRequest>(q.Data).RunId);
                                await sync.Task.WaitAsync(token);
                            }));
                        PumpUntil(() => targeted.Task.IsCompleted, "首次同步前应定向复核已断线的 A。");
                        AwaitWorker(coordinator, s => s.Resynchronising, "重连后跳号的终态应使视图失效。");
                        Require(targeted.Task.Result == own.RunId && failures.Count == 0 && session.HasChildSession,
                            "首次同步前失效的 LastRun 应暂存，不能提前提醒或收尾。");
                        // Keep the targeted query unanswered and the dispatcher on B's tick until the full reply.
                        using var synced = new ManualResetEventSlim();
                        void OnFullReply(object? _, WorkerObservationBatch batch)
                        {
                            if (batch is { Kind: WorkerEvidenceKind.Snapshot, Current.Fresh: true }
                                && batch.ObservedSnapshot?.LastRun?.RunId == own.RunId) synced.Set();
                        }
                        coordinator.ObservationReceived += OnFullReply;
                        try {
                            sync.SetResult();
                            pipe = connected.GetAwaiter().GetResult();
                            synced.Wait(token);
                        } finally {
                            coordinator.ObservationReceived -= OnFullReply;
                        }
                    } else if (order == SettlementOrder.Gap) {
                        var running = snapshot with { StateRevision = 3, ActiveRun = own, RunState = RunState.Running };
                        var gap = running with { StateRevision = 5 };
                        received = snapshot with { StateRevision = 6, LastRun = done };
                        Task.Run(async () => {
                            await pipe.WriteAsync(StateEvent(running), token);
                            await pipe.WriteAsync(StateEvent(gap), token);
                            // accepted may already have sent a targeted query. Leave both reads unanswered.
                            WireEnvelope query;
                            do {
                                query = await WorkerCoordinatorSelfTest.ReadRequestAsync(pipe, ProtocolOperations.WorkerGetSnapshot, token);
                            } while (ProtocolJson.Deserialize<GetSnapshotRequest>(query.Data).RunId.HasValue);
                            await pipe.WriteAsync(StateEvent(received), token);
                        }).GetAwaiter().GetResult();
                        AwaitWorker(coordinator, s => s.FreshReadyToStart && s.WorkerSnapshot?.LastRun?.RunId == own.RunId,
                            "完整补查未返回时，协调器应已发布 A 的新鲜终态。");
                    } else {
                        Task.Run(() => pipe.WriteAsync(StateEvent(received), token)).GetAwaiter().GetResult();
                    }
                    AwaitWorker(coordinator, s => s.WorkerSnapshot?.LastRun?.RunId == own.RunId,
                        "下一分钟到来时 Worker 应已报告此前 Run 的终态。");
                    var completed = new List<Guid>();
                    window.ScheduledRunCompleted += (_, run) => completed.Add(run.RunId);
                    if (order == SettlementOrder.Gap) {
                        var firstPeer = connected;
                        var failedBeforeSwitch = false;
                        window.NotificationRequested += (_, n) => {
                            if (n.Title == $"「{name}」运行失败")
                                failedBeforeSwitch = project.ActiveConfigurationId == target && completed.SequenceEqual([own.RunId])
                                    && session.HasChildSession && wts.LogoffAttempts.Count == 0 && exitRequests == 0;
                        };
                        clock.Advance(TimeSpan.FromMinutes(1));
                        var tick = window.CheckScheduledRunsAsync();
                        PumpUntil(() => tick.IsCompleted || connected != firstPeer && connected!.IsCompleted,
                            "普通定时 B 应在补查回包前获准启动。");
                        var label = own.Plan.Items.Single().TaskLabel;
                        Require(failedBeforeSwitch && preparations == 2 && project.ActiveConfigurationId == nextTarget
                            && completed.SequenceEqual([own.RunId]) && wts.LogoffAttempts.SequenceEqual([sessionId])
                            && failures.SequenceEqual([$"「{name}」运行失败 / 失败任务：{label}。打开 Bunshin 查看运行动态。"]),
                            "普通定时 B 到点应先处理 A 的失败，再启动 B。");
                        var nextPeer = connected!.GetAwaiter().GetResult();
                        var nextRequest = Task.Run(() => WorkerCoordinatorSelfTest.ReadRequestAsync(nextPeer, ProtocolOperations.RunStart, token));
                        PumpUntil(() => nextRequest.IsCompleted, "B 的新 Worker 应收到开始请求。");
                        Require(ProtocolJson.Deserialize<RunStartRequest>(nextRequest.Result.Data).RunId != own.RunId,
                            "B 应提交新的 Run。");
                        Task.Run(() => nextPeer.WriteAsync(WireEnvelope.Response(ProtocolOperations.RunStart,
                            nextRequest.Result.RequestId!.Value, new RunStartResponse("accepted")), token)).GetAwaiter().GetResult();
                        PumpUntil(() => tick.IsCompleted, "B 应收到接纳应答。");
                        tick.GetAwaiter().GetResult();
                        pipe.DisposeAsync().AsTask().GetAwaiter().GetResult();
                        return;
                    }
                    clock.Advance(TimeSpan.FromMinutes(1));
                    var check = window.CheckScheduledRunsAsync();
                    PumpUntil(() => check.IsCompleted, "旧定时 Run 收尾失败后应结束新的定时机会。");
                    check.GetAwaiter().GetResult();
                    var activity = window.LogLines.OfType<LogEntry>().Where(e => e.TaskLabel == "定时")
                        .Reverse().Select(e => e.Message).ToList();
                    var cleanup = activity.IndexOf(failCleanup ? "任务已完成，桌面分身未能关闭" : "全部完成，已关闭桌面分身");
                    var nextAction = activity.FindIndex(line => line.Contains("下一班") && (line.Contains("跳过")
                        || line.Contains("未能启动") || line.StartsWith("启动")));
                    Require(completed.SequenceEqual([own.RunId]) && wts.LogoffAttempts.SequenceEqual([77u, sessionId])
                        && cleanup >= 0 && nextAction > cleanup && preparations == (failCleanup ? 1 : 2)
                        && project.ActiveConfigurationId == (failCleanup ? target : nextTarget),
                        "下一班到点时必须先完成原 Run 的收尾动态，再决定是否激活新配置。");
                    var expected = new List<string> { $"「{name}」已完成" };
                    if (failCleanup) expected.Add($"「{name}」收尾失败");
                    if (!failCleanup || forceCollision) expected.Add("定时任务未能启动");
                    Require(failures.Select(line => line.Split(" / ")[0]).SequenceEqual(expected)
                        && (!failCleanup || forceCollision || activity[nextAction].Contains("运行环境状态未知")),
                        "应按原任务成功、收尾结果、下一班处置的顺序交付；普通定时在注销失败后按环境状态未知跳过。");
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
                    if (order == SettlementOrder.Preview) {
                        var preview = new RdpPreviewWindow { Owner = window };
                        typeof(ChildSessionManager).GetField("_previewWindow",
                            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(session, preview);
                        preview.Show();
                        preview.Activate();
                        PumpUntilFocus(() => preview.IsActive, "成功前用户应正在查看分身预览。");
                        int? balloonsAtClose = null;
                        preview.Closed += (_, _) => balloonsAtClose = probe.Balloons.Count(n => n.Title == $"「{name}」已完成");
                        Publish(done, 4);
                        PumpUntil(() => balloonsAtClose.HasValue, "成功收尾应关闭预览。");
                        Require(balloonsAtClose == 1, "定时成功提醒必须在收尾关闭预览之前发出一次。");
                    } else if (order == SettlementOrder.Tray) {
                        var tray = environment.RequestEnd(() => {
                            Publish(done, 4);
                            PumpUntil(() => !session.HasChildSession && AdmissionDiscarded(coordinator),
                                "托盘确认框打开期间，定时收尾应能拿到闸门并结束分身。");
                            return true;
                        })!;
                        PumpUntil(() => tray.IsCompleted, "确认后托盘应重新核验已结束的环境。");
                        Require(tray.GetAwaiter().GetResult().Outcome == EnvironmentEndOutcome.Ended, "托盘结束分身应成功。");
                    } else if (order is SettlementOrder.Update or SettlementOrder.ManualEndAfterUpdate) {
                        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        var gate = environment.RunExclusiveAsync(() => release.Task);
                        var failure = new IOException("模拟 Engine 交接失败");
                        if (order == SettlementOrder.ManualEndAfterUpdate)
                            wts.DuringLogoff = _ => throw new IOException("模拟更新首次注销失败");
                        var update = App.InstallAfterEnvironmentEndAsync(environment, window, logger, () => throw failure,
                            () => throw new InvalidOperationException("交接失败不应关闭 GUI。"));
                        Publish(done, 4);
                        Require(completions.Count == 0 && session.HasChildSession, "交接期间应缓冲 A 的完成和收尾。");
                        release.SetResult();
                        PumpUntil(() => update.IsCompleted, "更新退出失败后应恢复界面。");
                        gate.GetAwaiter().GetResult();
                        if (order == SettlementOrder.ManualEndAfterUpdate) {
                            PumpOnboarding();
                            Require(update.IsFaulted && environment.EndUnconfirmed && !environment.Closing
                                && completions.SequenceEqual([own.RunId])
                                && !window.LogLines.OfType<LogEntry>().Any(e => e.Message == "全部完成，已关闭桌面分身"),
                                "首次注销失败后应保留待收尾的定时任务。");
                            wts.DuringLogoff = null;
                            var manual = environment.EndAsync(() => true);
                            PumpUntil(() => manual.IsCompleted, "用户重新结束分身应成功。");
                            Require(manual.GetAwaiter().GetResult().Outcome == EnvironmentEndOutcome.Ended
                                && AdmissionDiscarded(coordinator), "手动结束后应确认旧环境已结束。");
                            PumpOnboarding();
                            Require(window.LogLines.OfType<LogEntry>().Count(e => e.Message == "全部完成，已关闭桌面分身") == 1
                                && completions.SequenceEqual([own.RunId]) && wts.LogoffAttempts.SequenceEqual([77u, sessionId, sessionId]),
                                "手动结束成功后应立即记一次收尾，无需等下一班或再次注销。");
                            return;
                        }
                        Require(update.Exception?.GetBaseException() == failure && !environment.Closing
                            && AdmissionDiscarded(coordinator), "GUI 应在环境已确认结束后恢复。");
                        Require(window.LogLines.OfType<LogEntry>().Count(e => e.Message == "全部完成，已关闭桌面分身") == 1,
                            "交接失败恢复时应立即记一次定时收尾，不等下一班。");
                    } else {
                        if (terminal == RunState.Failed) window.Hide();
                        Publish(done, terminal == RunState.Succeeded ? 4 : 3);
                    }
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
                        Require(session.HasChildSession, "未重新确认 Worker 身份前必须保留现场。");
                        connected = Task.Run(() => WorkerCoordinatorSelfTest.OpenConnectionAsync(
                            pipeName, record!, 0, token, stateRevision: 5, lastRun: done));
                        PumpUntil(() => connected.IsCompleted && !session.HasChildSession,
                            "同一 Worker 重连并确认定时 Run 成功后应继续收尾。");
                    }
                    Require(completions.SequenceEqual([own.RunId]), "同一 Run 应只发出一次终态。");
                }
                Require(exitRequests == 0, "定时 Run 的终态不得触发开机自动执行的退出。");
                if (terminal == RunState.Succeeded) {
                    PumpUntil(() => !session.HasChildSession && window.LogLines.OfType<LogEntry>()
                        .Any(e => e.Message == "全部完成，已关闭桌面分身"), "仅本次成功后应完成分身收尾。");
                    Require(window.LogLines.OfType<LogEntry>().Any(e => e.Message == "已关闭旧的桌面分身")
                        && window.LogLines.OfType<LogEntry>().Count(e => e.Message == "全部完成，已关闭桌面分身") == 1,
                        "成功收尾和旧分身关闭应分别出现在运行动态。");
                    Require(wts.LogoffAttempts.SequenceEqual([77u, sessionId]), "应先清理旧分身，成功后只关闭新分身。");
                } else if (terminal == RunState.Failed) {
                    Require(probe.Balloons is [{ Failed: true } notice] && notice.Title == $"「{name}」运行失败",
                        "本次定时任务的失败提醒应已交付托盘一次。");
                    Require(session.HasChildSession && wts.LogoffAttempts.Count == 0,
                        "没有下一班时，交付失败提醒后应保留分身，注销次数应为零。");
                } else {
                    Require(wts.LogoffAttempts.Count == 0 && session.HasChildSession, "手停应保留现场。");
                    Require(failures.Count == 0, "手停不应通知运行失败。");
                    Require(!ShownHeaderControls(window).Contains("RetryEnvironmentButton"),
                        "后续准备成功后，手停任务应清除此前准备失败的重试状态。");
                    wts.ReportedId = sessionId + 97;
                    wts.Sessions.Add(wts.ReportedId.Value);
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
                    PumpUntil(() => retry.IsCompleted && duplicate.IsCompleted, "已终结的同 ID 重试应返回已接纳。");
                    Require(retry.Result.StartReply == StartReplyKind.AlreadyAccepted, "应保留 Worker 对同一 Run 的幂等答复。");
                    var next = window.TryStartScheduledRunAsync(target);
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
                    if (++preparations > 1 && order != SettlementOrder.Gap) {
                        throw new InvalidOperationException("模拟第二次准备结束");
                    }
                    if (preparations == 2) record = null; // B starts after the simulated logoff of A's Worker.
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
