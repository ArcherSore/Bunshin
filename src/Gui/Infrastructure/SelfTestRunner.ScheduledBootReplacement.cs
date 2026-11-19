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

    private enum BootReplacement { AdjacentMinutes, PendingStart, ManualStopping, ManualStopReply }

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
        var quit = new QuitAfterRunTracker();
        var exitRequested = false;
        using var process = Process.GetCurrentProcess();
        var sessionId = (uint)process.SessionId;
        var path = process.MainModule!.FileName;
        var listed = false;
        var wts = new SimulatedWts { DuringLogoff = _ => listed = false };
        using var session = wts.CreateManager(logger);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        var pipeName = $"Bunshin.Schedule.Boot.{Guid.NewGuid():N}";
        var workers = new List<Task>();
        var bootAccepted = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopReply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishManualStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var preparingReplacement = false;
        var resumePreparation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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
                coordinator.StateChanged += (_, snapshot) =>
                    window.ObserveApplicationRun(snapshot, () => exitRequested = true);
                window.Show();
                PumpUntil(() => bootAccepted.Task.IsCompleted, "Worker 应已接纳开机 Run。");
                if (scenario == BootReplacement.PendingStart) {
                    Require(coordinator.Snapshot.WorkerSnapshot?.ActiveRun is null, "开始应答可以先于运行快照。");
                } else {
                    PumpUntil(() => HeaderShows(window, "StopTaskHeaderButton", enabled: true), "开机 Run 应可停止。");
                }
                var warnings = new List<ForcedScheduledStartCountdown>();
                window.ForcedScheduledStartPending += (_, e) => warnings.Add(e);
                clock.Advance(TimeSpan.FromMinutes(1));
                var scheduled = window.CheckScheduledRunsAsync();
                PumpUntil(() => warnings.Count == 1, "顶替开机 Run 前应有倒计时。");
                Require(wts.LogoffAttempts.Count == 0 && !stopReply.Task.IsCompleted, "倒计时前不能停止任务或关闭分身。");
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
                PumpUntil(() => coordinator.Snapshot.WorkerSnapshot?.ActiveRun is { } run
                    && run.RunId != bootAccepted.Task.Result,
                    "旧 Worker 结束后，新 Worker 应已接纳新的定时 Run。");
                if (scenario == BootReplacement.AdjacentMinutes) {
                    PumpUntil(() => warnings.Count == 2, "前一分钟准备结束后，当前分钟应独立判断是否顶替。");
                    Require(warnings[1].ConfigurationName == "日常", "新 Run 应有自己的取消机会。");
                    warnings[1].Cancel();
                }
                Require(!exitRequested && workers.Count == 2 && wts.LogoffAttempts.SequenceEqual([sessionId])
                    && window.ConfigurationTabs.SelectedItem is TabItem { Tag: Guid shown } && shown == target,
                    "顶替开机 Run 后应继续运行所选定时配置。");
            }, session: session, coordinator: coordinator, environment: CreateEnvironment(logger, session, coordinator),
                launchCommand: new(AutoStart: true, Instance: "日常", QuitAfterRun: true), quitAfterRun: quit,
                scheduleClock: clock, prepareEnvironment: async (plan, options, _) => {
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
            await release.Task;
        }
    }
}
