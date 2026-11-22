using System.Diagnostics;
using System.Windows.Controls;
using Gui.ChildSession;
using Gui.Models;
using Gui.Views;
using Gui.Worker;
using ProjectModel;
using Protocol;

namespace Gui.Infrastructure;

internal static partial class SelfTestRunner
{
    private static void VerifyScheduledCommandLineRun(AppLogger logger, string directory)
    {
        foreach (var scenario in Enum.GetValues<ScheduleScenario>()) {
            CheckScheduledCommandLineRun(logger, Path.Combine(directory, scenario.ToString()), scenario);
        }
    }

    private enum ScheduleScenario { Tracked, BoundInGate, ManuallyEnded, AdjacentMinutes, PendingStart, Disconnected }

    private static void CheckScheduledCommandLineRun(AppLogger logger, string directory, ScheduleScenario scenario)
    {
        var root = CreateProjectFixture(directory);
        TwoConfigurations()(root);
        var project = ProjectPlanModule.Open(root, Path.Combine(root, "config", "configurations.json"));
        var target = project.Configurations.Single(item => item.Name == "周常").Id;
        project.AddSchedulePolicy(target, "早班");
        if (scenario == ScheduleScenario.AdjacentMinutes) {
            var daily = project.ActiveConfigurationId;
            var policy = project.AddSchedulePolicy(daily, "下一班");
            project.UpdateSchedulePolicy(daily, policy with { Times = [new(8, 1)] });
        }
        CreateIdleSettings(root, logger).ForceScheduledStart.Value = true;

        var clock = new ScheduleTestClock();
        var exitRequested = false;
        using var process = Process.GetCurrentProcess();
        var sessionId = (uint)process.SessionId;
        var path = process.MainModule!.FileName;
        var listed = false;
        var bootLoggedOff = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wts = new SimulatedWts { DuringLogoff = _ => {
            listed = false;
            bootLoggedOff.TrySetResult();
        } };
        using var session = wts.CreateManager(logger);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        var pipeName = $"Bunshin.Schedule.Boot.{Guid.NewGuid():N}";
        var workers = new List<Task>();
        var bootAccepted = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingQuery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publishPendingRun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopReply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishManualStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishPreviousRun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var preparingReplacement = false;
        var resumePreparation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unconfirmedNotices = 0;
        var noticesAtPreparation = -1;
        var quitAfterRun = scenario is ScheduleScenario.Tracked or ScheduleScenario.BoundInGate;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        WorkerProcessEntry[] worker = [new(process.Id, sessionId, Path.GetFileName(path))];
        var coordinator = new WorkerCoordinator(logger, Path.Combine(directory, "state"), path, pipeName,
            usePipeAcl: false, captureProcesses: () => listed ? worker : [],
            launchWorker: (id, instance, secret, _, _) => {
                listed = true;
                var record = new WorkerAdmissionRecord(instance, secret, id, process.Id,
                    project.RuntimeProfileDigest, DateTime.UtcNow);
                var boot = workers.Count == 0;
                workers.Add(Task.Run(() => RunWorker(record, boot)));
                return Task.FromResult(new VerifiedChildSessionProcessLaunch((uint)process.Id, id, null, null));
            });
        var environment = CreateEnvironment(logger, session, coordinator, (_, _) => workers[0]);
        try {
            WithRuntimeControlWindow(logger, directory, window => {
                window.Show();
                if (scenario == ScheduleScenario.BoundInGate) {
                    AwaitRunningWorker(coordinator);
                } else {
                    PumpUntil(() => bootAccepted.Task.IsCompleted, "Worker 应已接纳命令行任务。");
                    if (scenario == ScheduleScenario.PendingStart) {
                        Require(coordinator.Snapshot.WorkerSnapshot?.ActiveRun is null, "开始应答可以先于运行快照。");
                    } else {
                        PumpUntil(() => HeaderShows(window, "StopTaskHeaderButton", enabled: true), "命令行任务应可停止。");
                    }
                }
                window.NotificationRequested += (_, notice) => {
                    if (notice.Message.Contains("运行结果未确认")) unconfirmedNotices++;
                };
                var warnings = new List<ForcedScheduledStartCountdown>();
                window.ForcedScheduledStartPending += (_, e) => {
                    if (scenario == ScheduleScenario.PendingStart)
                        Require(coordinator.Snapshot.WorkerSnapshot?.ActiveRun?.RunId == bootAccepted.Task.Result,
                            "宣布倒计时时应已确认旧 Run 正在运行。");
                    warnings.Add(e);
                };
                if (scenario == ScheduleScenario.ManuallyEnded) {
                    var ending = environment.RequestEnd(() => true)!;
                    PumpUntil(() => ending.IsCompleted, "手动结束分身应完成。");
                    Require(ending.GetAwaiter().GetResult() == EnvironmentEndResult.Ended, "到点前分身应已结束。");
                }
                clock.Advance(TimeSpan.FromMinutes(1));
                var scheduled = window.CheckScheduledRunsAsync();
                if (quitAfterRun) {
                    var expectedWarnings = scenario == ScheduleScenario.BoundInGate ? 1 : 0;
                    if (scenario == ScheduleScenario.BoundInGate) {
                        Require(warnings.Count == 1, "已有其他任务时应发出倒计时。");
                        clock.Advance(TimeSpan.FromSeconds(10));
                        Require(!scheduled.IsCompleted, "换场应等待仍在准备命令行任务的闸门。");
                        finishPreviousRun.SetResult();
                    }
                    PumpUntil(() => scheduled.IsCompleted || warnings.Count > expectedWarnings,
                        "定时应在命令行任务绑定后跳过。");
                    Require(scheduled.IsCompletedSuccessfully && window.LogLines.OfType<LogEntry>().Any(e =>
                        e.TaskLabel == "定时" && e.Message.Contains("周常") && e.Message.Contains("命令行任务尚未结束")),
                        "强制定时应记录命令行任务尚未结束。");
                    Require(warnings.Count == expectedWarnings && workers.Count == 1
                        && wts.LogoffAttempts.Count == 0 && !stopReply.Task.IsCompleted,
                        "跳过时不得追加倒计时、停止任务或更换分身。");
                    PumpUntil(() => HeaderShows(window, "StopTaskHeaderButton", enabled: true), "命令行任务仍应可停止。");
                    Click(window, "StopTaskHeaderButton");
                    PumpUntil(() => stopReply.Task.IsCompleted, "手动停止应发送给当前命令行任务。");
                    finishManualStop.SetResult();
                    PumpUntil(() => exitRequested, "命令行 -q 任务手动停止后应退出。");
                    window.CheckScheduledRunsAsync().GetAwaiter().GetResult();
                    Require(workers.Count == 1 && wts.LogoffAttempts.Count == 0, "已跳过的机会不得补跑。");
                    return;
                }
                if (scenario == ScheduleScenario.PendingStart) {
                    PumpUntil(() => pendingQuery.Task.IsCompleted, "定时入口应先等命令行任务的有序复核。");
                    Require(warnings.Count == 0, "有序复核应答前不能宣布倒计时。");
                    publishPendingRun.SetResult();
                }
                if (scenario != ScheduleScenario.ManuallyEnded) {
                    PumpUntil(() => warnings.Count == 1, "非 -q 任务运行时应有倒计时。");
                    Require(wts.LogoffAttempts.Count == 0 && !stopReply.Task.IsCompleted, "倒计时前不能停止任务或关闭分身。");
                    if (scenario == ScheduleScenario.Disconnected) {
                        disconnect.SetResult();
                        AwaitWorker(coordinator, s => s.Observation == WorkerObservation.IpcDisconnected, "倒计时内旧 Worker 应已断线。");
                        Require(unconfirmedNotices == 0, "仅断线不能报告最终结论。");
                    }
                    clock.Advance(TimeSpan.FromSeconds(10));
                }
                if (scenario == ScheduleScenario.AdjacentMinutes) {
                    PumpUntil(() => preparingReplacement, "周常应已获得运行环境并开始准备。");
                    clock.Advance(TimeSpan.FromMinutes(1));
                    _ = window.CheckScheduledRunsAsync();
                    Require(warnings.Count == 1, "等待周常准备时不能提前警告日常将要顶替的 Run。");
                    resumePreparation.SetResult();
                }
                PumpUntil(() => scheduled.IsCompleted, "定时任务应完成提交。");
                scheduled.GetAwaiter().GetResult();
                if (scenario == ScheduleScenario.ManuallyEnded)
                    Require(warnings.Count == 0, "分身已手动结束，到点时应直接重新准备。");
                PumpUntil(() => coordinator.Snapshot.WorkerSnapshot?.ActiveRun is { } run
                    && run.RunId != bootAccepted.Task.Result,
                    "旧 Worker 结束后，新 Worker 应已接纳新的定时 Run。");
                if (scenario == ScheduleScenario.AdjacentMinutes) {
                    PumpUntil(() => warnings.Count == 2, "前一分钟准备结束后，当前分钟应独立判断是否顶替。");
                    Require(warnings[1].ConfigurationName == "日常", "新 Run 应有自己的取消机会。");
                    warnings[1].Cancel();
                }
                if (scenario == ScheduleScenario.Disconnected)
                    Require(noticesAtPreparation == 1 && unconfirmedNotices == 1, "确认旧 Worker 退出后应先发出唯一结论，再进入新环境准备。");
                Require(!exitRequested && workers.Count == 2 && wts.LogoffAttempts.SequenceEqual([sessionId])
                    && window.TaskPlanEditor.ConfigurationTabs.SelectedItem is TabItem { Tag: Guid shown }
                    && shown == target,
                    "新分身应运行所选定时配置。");
            }, session: session, coordinator: coordinator,
                environment: environment,
                launchCommand: new(AutoStart: true, Instance: "日常", QuitAfterRun: quitAfterRun), quitAfterRun: () => exitRequested = true,
                scheduleClock: clock, prepareEnvironment: async (plan, options, _) => {
                    if (workers.Count == 1) noticesAtPreparation = unconfirmedNotices;
                    if (workers.Count == 1 && scenario == ScheduleScenario.AdjacentMinutes) {
                        preparingReplacement = true;
                        await resumePreparation.Task.WaitAsync(token);
                    }
                    wts.ReportedId = sessionId;
                    wts.Sessions.Add(sessionId);
                    ConnectTestDesktop(session, sessionId);
                    await coordinator.PrepareWorkerAsync(sessionId, plan, token, options());
                    if (scenario == ScheduleScenario.BoundInGate && workers.Count == 1) {
                        await finishPreviousRun.Task.WaitAsync(token);
                        PumpUntil(() => coordinator.Snapshot.WorkerSnapshot is { ActiveRun: null, LastRun: not null },
                            "命令行提交前，原任务应已结束。");
                    }
                });
        } finally {
            release.TrySetResult();
            timeout.Cancel();
            Task.Run(async () => await coordinator.DisposeAsync()).GetAwaiter().GetResult();
        }
        Task.WhenAll(workers).GetAwaiter().GetResult();

        async Task RunWorker(WorkerAdmissionRecord record, bool boot)
        {
            var previous = boot && scenario == ScheduleScenario.BoundInGate
                ? WorkerCoordinatorSelfTest.CreateActiveRun(project.CreateRunStartAttempt(project.ActiveConfigurationId)) : null;
            await using var pipe = await WorkerCoordinatorSelfTest.OpenConnectionAsync(pipeName, record, 0, token, previous);
            if (previous != null) {
                await finishPreviousRun.Task.WaitAsync(token);
                await pipe.WriteAsync(StateEvent(WorkerCoordinatorSelfTest.CreateSnapshot(record, 0) with {
                    StateRevision = 2, LastRun = previous with { State = RunState.Succeeded }
                }), token);
            }
            var start = await WorkerCoordinatorSelfTest.ReadRequestAsync(pipe, ProtocolOperations.RunStart, token);
            var request = ProtocolJson.Deserialize<RunStartRequest>(start.Data);
            var attempt = new RunStartAttempt(request.RunId, request.Plan, request.PlanDigest);
            var active = WorkerCoordinatorSelfTest.CreateActiveRun(attempt);
            var running = WorkerCoordinatorSelfTest.CreateSnapshot(record, 0, active) with { StateRevision = previous == null ? 2 : 3 };
            if (!boot || scenario != ScheduleScenario.PendingStart) {
                await pipe.WriteAsync(StateEvent(running), token);
            }
            await pipe.WriteAsync(WireEnvelope.Response(start.Operation, start.RequestId!.Value,
                new RunStartResponse("accepted")), token);
            if (boot) {
                bootAccepted.SetResult(active.RunId);
                if (scenario == ScheduleScenario.Disconnected) {
                    await disconnect.Task.WaitAsync(token);
                    return;
                }
                if (scenario == ScheduleScenario.PendingStart) {
                    var query = await WorkerCoordinatorSelfTest.ReadRequestAsync(pipe, ProtocolOperations.WorkerGetSnapshot, token);
                    Require(ProtocolJson.Deserialize<GetSnapshotRequest>(query.Data).RunId == active.RunId, "应复核原命令行任务。");
                    pendingQuery.SetResult();
                    await publishPendingRun.Task.WaitAsync(token);
                    await pipe.WriteAsync(WireEnvelope.Response(query.Operation, query.RequestId!.Value,
                        new GetSnapshotResponse(running, true)), token);
                }
                var stop = await WorkerCoordinatorSelfTest.ReadRequestAsync(pipe, ProtocolOperations.RunStop,
                    token, running);
                Require(ProtocolJson.Deserialize<RunStopRequest>(stop.Data).RunId == active.RunId,
                    "停止请求必须指向原命令行任务。");
                await pipe.WriteAsync(WireEnvelope.Response(stop.Operation, stop.RequestId!.Value,
                    new RunStopResponse("stop_requested")), token);
                stopReply.SetResult();
                if (quitAfterRun) {
                    await finishManualStop.Task.WaitAsync(token);
                }
                await pipe.WriteAsync(StateEvent(running with { StateRevision = running.StateRevision + 1,
                    RunState = RunState.Idle,
                    ActiveRun = null, LastRun = active with { State = RunState.Cancelled } }), token);
            }
            // Logging off ends the old process and closes its pipe before the replacement connects.
            if (boot) {
                await Task.WhenAny(bootLoggedOff.Task, release.Task);
            } else {
                await release.Task;
            }
        }
    }
}
