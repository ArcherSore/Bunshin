using System.Diagnostics;
using System.Windows.Controls;
using Gui.ChildSession;
using Gui.Views;
using Gui.Worker;
using ProjectModel;
using Protocol;

namespace Gui.Infrastructure;

internal static partial class SelfTestRunner
{
    private static void VerifyScheduledBootReplacement(AppLogger logger, string directory)
    {
        foreach (var scenario in Enum.GetValues<BootReplacement>()) {
            CheckBootStop(logger, Path.Combine(directory, scenario.ToString()), scenario);
        }
    }

    private enum BootReplacement { AdjacentMinutes, PendingStart, ManualStopping, ManualStopReply, EndFailed, Disconnected }

    private static void CheckBootStop(AppLogger logger, string directory, BootReplacement scenario)
    {
        var root = CreateProjectFixture(directory);
        TwoConfigurations()(root);
        var project = ProjectPlanModule.Open(root, Path.Combine(root, "config", "configurations.json"));
        var target = project.Configurations.Single(item => item.Name == "周常").Id;
        project.AddSchedulePolicy(target, "早班");
        if (scenario == BootReplacement.AdjacentMinutes) {
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
            if (scenario == BootReplacement.EndFailed) throw new IOException("模拟旧环境未能结束");
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
        var preparingReplacement = false;
        var resumePreparation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unconfirmedNotices = 0;
        var noticesAtPreparation = -1;
        var manual = scenario is BootReplacement.ManualStopping or BootReplacement.ManualStopReply;
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
        try {
            WithRuntimeControlWindow(logger, directory, window => {
                window.Show();
                PumpUntil(() => bootAccepted.Task.IsCompleted, "Worker 应已接纳开机 Run。");
                if (scenario == BootReplacement.PendingStart) {
                    Require(coordinator.Snapshot.WorkerSnapshot?.ActiveRun is null, "开始应答可以先于运行快照。");
                } else {
                    PumpUntil(() => HeaderShows(window, "StopTaskHeaderButton", enabled: true), "开机 Run 应可停止。");
                }
                window.NotificationRequested += (_, notice) => {
                    if (notice.Message.Contains("运行结果未确认")) unconfirmedNotices++;
                };
                var warnings = new List<ForcedScheduledStartCountdown>();
                window.ForcedScheduledStartPending += (_, e) => {
                    if (scenario == BootReplacement.PendingStart)
                        Require(coordinator.Snapshot.WorkerSnapshot?.ActiveRun?.RunId == bootAccepted.Task.Result,
                            "宣布倒计时时应已确认旧 Run 正在运行。");
                    warnings.Add(e);
                };
                clock.Advance(TimeSpan.FromMinutes(1));
                var scheduled = window.CheckScheduledRunsAsync();
                if (scenario == BootReplacement.PendingStart) {
                    PumpUntil(() => pendingQuery.Task.IsCompleted, "定时入口应先等开机 Run 的有序复核。");
                    Require(warnings.Count == 0, "有序复核应答前不能宣布倒计时。");
                    publishPendingRun.SetResult();
                }
                PumpUntil(() => warnings.Count == 1, "确认开机 Run 仍在运行后应有倒计时。");
                Require(wts.LogoffAttempts.Count == 0 && !stopReply.Task.IsCompleted, "倒计时前不能停止任务或关闭分身。");
                if (scenario == BootReplacement.Disconnected) {
                    disconnect.SetResult();
                    AwaitWorker(coordinator, s => s.Observation == WorkerObservation.IpcDisconnected, "倒计时内旧 Worker 应已断线。");
                    Require(unconfirmedNotices == 0, "仅断线不能报告最终结论。");
                }
                if (manual) {
                    Click(window, "StopTaskHeaderButton");
                    PumpUntil(() => stopReply.Task.IsCompleted, "倒计时内应允许手动停止。");
                    if (scenario == BootReplacement.ManualStopping) {
                        PumpUntil(() => coordinator.Snapshot.WorkerSnapshot?.ActiveRun?.State == RunState.Stopping,
                            "终态到达前应能观察到停止中。");
                    }
                }
                clock.Advance(TimeSpan.FromSeconds(10));
                if (manual) {
                    PumpUntil(() => scheduled.IsCompleted, "手动停止正等待退出时不应继续顶替。");
                    Require(workers.Count == 1 && wts.LogoffAttempts.Count == 0 && !exitRequested,
                        "请求手动停止后应保留环境，等 Worker 终态确认退出。");
                    finishManualStop.SetResult();
                    PumpUntil(() => exitRequested, "手动停止带 -q 的开机 Run 应触发退出。");
                    return;
                }
                if (scenario == BootReplacement.AdjacentMinutes) {
                    PumpUntil(() => preparingReplacement, "周常应已获得运行环境并开始准备。");
                    clock.Advance(TimeSpan.FromMinutes(1));
                    _ = window.CheckScheduledRunsAsync();
                    Require(warnings.Count == 1, "等待周常准备时不能提前警告日常将要顶替的 Run。");
                    resumePreparation.SetResult();
                }
                PumpUntil(() => scheduled.IsCompleted, "顶替开机 Run 的定时执行应完成提交。");
                scheduled.GetAwaiter().GetResult();
                if (scenario == BootReplacement.EndFailed) {
                    PumpUntil(() => exitRequested, "顶替未能结束旧环境时应恢复原开机终态的退出请求。");
                    Require(workers.Count == 1 && wts.LogoffAttempts.SequenceEqual([sessionId]),
                        "旧环境未确认结束不能放弃开机绑定或开始准备替代 Worker。");
                    return;
                }
                PumpUntil(() => coordinator.Snapshot.WorkerSnapshot?.ActiveRun is { } run
                    && run.RunId != bootAccepted.Task.Result,
                    "旧 Worker 结束后，新 Worker 应已接纳新的定时 Run。");
                if (scenario == BootReplacement.AdjacentMinutes) {
                    PumpUntil(() => warnings.Count == 2, "前一分钟准备结束后，当前分钟应独立判断是否顶替。");
                    Require(warnings[1].ConfigurationName == "日常", "新 Run 应有自己的取消机会。");
                    warnings[1].Cancel();
                }
                if (scenario == BootReplacement.Disconnected)
                    Require(noticesAtPreparation == 1 && unconfirmedNotices == 1, "确认旧 Worker 退出后应先发出唯一结论，再进入新环境准备。");
                Require(!exitRequested && workers.Count == 2 && wts.LogoffAttempts.SequenceEqual([sessionId])
                    && window.TaskPlanEditor.ConfigurationTabs.SelectedItem is TabItem { Tag: Guid shown }
                    && shown == target,
                    "顶替开机 Run 后应继续运行所选定时配置。");
            }, session: session, coordinator: coordinator,
                environment: CreateEnvironment(logger, session, coordinator, (_, _) => workers[0]),
                launchCommand: new(AutoStart: true, Instance: "日常", QuitAfterRun: true), quitAfterRun: () => exitRequested = true,
                scheduleClock: clock, prepareEnvironment: async (plan, options, _) => {
                    if (workers.Count == 1) noticesAtPreparation = unconfirmedNotices;
                    if (workers.Count == 1 && scenario == BootReplacement.AdjacentMinutes) {
                        preparingReplacement = true;
                        await resumePreparation.Task.WaitAsync(token);
                    }
                    wts.ReportedId = sessionId;
                    wts.Sessions.Add(sessionId);
                    ConnectTestDesktop(session, sessionId);
                    await coordinator.PrepareWorkerAsync(sessionId, plan, token, options());
                });
        } finally {
            release.TrySetResult();
            timeout.Cancel();
            Task.Run(async () => await coordinator.DisposeAsync()).GetAwaiter().GetResult();
        }
        Task.WhenAll(workers).GetAwaiter().GetResult();

        async Task RunWorker(WorkerAdmissionRecord record, bool boot)
        {
            await using var pipe = await WorkerCoordinatorSelfTest.OpenConnectionAsync(pipeName, record, 0, token);
            var start = await WorkerCoordinatorSelfTest.ReadRequestAsync(pipe, ProtocolOperations.RunStart, token);
            var request = ProtocolJson.Deserialize<RunStartRequest>(start.Data);
            var attempt = new RunStartAttempt(request.RunId, request.Plan, request.PlanDigest);
            var active = WorkerCoordinatorSelfTest.CreateActiveRun(attempt);
            var running = WorkerCoordinatorSelfTest.CreateSnapshot(record, 0, active) with { StateRevision = 2 };
            if (!boot || scenario != BootReplacement.PendingStart) {
                await pipe.WriteAsync(StateEvent(running), token);
            }
            await pipe.WriteAsync(WireEnvelope.Response(start.Operation, start.RequestId!.Value,
                new RunStartResponse("accepted")), token);
            if (boot) {
                bootAccepted.SetResult(active.RunId);
                if (scenario == BootReplacement.Disconnected) {
                    await disconnect.Task.WaitAsync(token);
                    return;
                }
                if (scenario == BootReplacement.PendingStart) {
                    var query = await WorkerCoordinatorSelfTest.ReadRequestAsync(pipe, ProtocolOperations.WorkerGetSnapshot, token);
                    Require(ProtocolJson.Deserialize<GetSnapshotRequest>(query.Data).RunId == active.RunId, "应复核原开机 Run。");
                    pendingQuery.SetResult();
                    await publishPendingRun.Task.WaitAsync(token);
                    await pipe.WriteAsync(WireEnvelope.Response(query.Operation, query.RequestId!.Value,
                        new GetSnapshotResponse(running, true)), token);
                }
                var stop = await WorkerCoordinatorSelfTest.ReadRequestAsync(pipe, ProtocolOperations.RunStop,
                    token, running);
                Require(ProtocolJson.Deserialize<RunStopRequest>(stop.Data).RunId == active.RunId,
                    "停止请求必须指向原开机任务。");
                await pipe.WriteAsync(WireEnvelope.Response(stop.Operation, stop.RequestId!.Value,
                    new RunStopResponse("stop_requested")), token);
                if (scenario == BootReplacement.ManualStopping) {
                    running = running with { StateRevision = 3, RunState = RunState.Stopping,
                        ActiveRun = active with { State = RunState.Stopping } };
                    await pipe.WriteAsync(StateEvent(running), token);
                }
                stopReply.SetResult();
                if (manual) {
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
