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
            window.AutoRunFailed += (_, message) => failures.Add(message);
            var result = window.TryStartScheduledRunAsync(weekly.Id).GetAwaiter().GetResult();
            Require(result.Disposition == ScheduledRunDisposition.Invalid && project.ActiveConfigurationId == before
                && failures.Single().Contains(weekly.Name), "无效定时配置应通知原因并保留原配置。");
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
            project.AddTask(task);
            project.ActivateConfiguration(original);
            var failures = new List<string>();
            window.AutoRunFailed += (_, message) => failures.Add(message);
            var run = window.TryStartScheduledRunAsync(target);
            PumpUntil(() => run.IsCompleted, "旧分身清理未确认时应结束本轮。");
            Require(run.Result.Disposition == ScheduledRunDisposition.Admitted
                && project.ActiveConfigurationId == target && !prepared
                && wts.LogoffAttempts.SequenceEqual([7u]) && failures.Count == 1,
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
    }

    private static void VerifyScheduledTerminal(AppLogger logger, string directory, RunState terminal,
        bool lostReply = false)
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
                window.AutoRunFailed += (_, message) => failures.Add(message);
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
                void ExpectBusy()
                {
                    logger.EntryWritten += Record;
                    try {
                        var skipped = window.TryStartScheduledRunAsync(target);
                        Require(skipped.IsCompletedSuccessfully
                            && skipped.Result.Disposition == ScheduledRunDisposition.Busy
                            && logs.Any(line => line.Contains(name) && line.Contains(skipped.Result.Reason!)),
                            "遇忙或 Worker 状态不确定应立即跳过并记明原因。");
                    } finally {
                        logger.EntryWritten -= Record;
                    }
                }
                ExpectBusy();
                void Publish(RunSnapshot last, long revision)
                {
                    Task.Run(() => pipe.WriteAsync(StateEvent(snapshot with { StateRevision = revision,
                        LastRun = last }), token)).GetAwaiter().GetResult();
                    PumpUntil(() => coordinator.Snapshot.WorkerSnapshot?.StateRevision >= revision,
                        "必须观察到新的 Worker 快照。");
                    PumpOnboarding();
                }
                var done = own with { State = terminal, EndedAtUtc = DateTime.UtcNow,
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
                    ExpectBusy();
                } else {
                    var accepted = WireEnvelope.Response(request.Result.Operation, request.Result.RequestId!.Value,
                        new RunStartResponse("accepted"));
                    Task.Run(() => pipe.WriteAsync(accepted, token)).GetAwaiter().GetResult();
                    PumpUntil(() => start.IsCompleted, "定时执行应返回此次提交的身份。");
                    ExpectBusy();
                    Require(failures.Count == 0, "普通冲突只能记日志。");
                }
                Require(start.Result is { Disposition: ScheduledRunDisposition.Admitted, RunId: { } runId }
                    && runId == own.RunId && start.Result.ConfigurationId == target, "启动结果必须对应提交给 Worker 的计划。");
                Publish(done, lostReply ? 4 : 3);
                var quit = new QuitAfterRunTracker(DateTime.MinValue);
                var observed = new WorkerCoordinatorSnapshot(WorkerObservation.Connected, true,
                    snapshot with { LastRun = done }, "已结束");
                Require(!quit.Observe(observed, window.LastScheduledRunId) && quit.Observe(observed),
                    "定时 Run 的终态不得触发开机自动执行的退出。");
                if (terminal == RunState.Succeeded) {
                    PumpUntil(() => !session.HasChildSession, "仅本次成功后应结束分身。");
                    Require(wts.LogoffAttempts.SequenceEqual([77u, sessionId]), "应先清理旧分身，成功后只关闭新分身。");
                } else {
                    Require(wts.LogoffAttempts.Count == 0 && session.HasChildSession, "失败或手停应保留现场。");
                    Require(terminal == RunState.Cancelled ? failures.Count == 0
                        : failures.Single().Contains(name) && failures[0].Contains("计划项模拟失败"),
                        "任务失败通知必须包含配置与失败原因。");
                    wts.ReportedId = sessionId + 97;
                    wts.Sessions.Add(wts.ReportedId.Value);
                    ExpectBusy();
                    Require(wts.LogoffAttempts.Count == 0, "已知空闲的 Worker 不属于当前 WTS 分身时不得注销。");
                }
            }, session: session, coordinator: coordinator, environment: CreateEnvironment(logger, session, coordinator),
                prepareEnvironment: async (project, options, show) => {
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
