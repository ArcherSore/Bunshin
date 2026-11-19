using System.Diagnostics;
using System.Windows.Threading;
using Gui.ChildSession;
using Gui.Worker;
using ProjectModel;
using Protocol;

namespace Gui.Infrastructure;

internal static partial class SelfTestRunner
{
    private static void VerifyScheduledBootReplacement(AppLogger logger, string directory)
    {
        foreach (var manual in new[] { false, true }) {
            CheckBootStop(logger, Path.Combine(directory, manual ? "manual" : "scheduled"), manual);
        }
    }

    private static void CheckBootStop(AppLogger logger, string directory, bool manual)
    {
        var root = CreateProjectFixture(directory);
        TwoConfigurations()(root);
        var project = ProjectPlanModule.Open(root, Path.Combine(root, "config", "configurations.json"));
        var target = project.Configurations.Single(item => item.Name == "周常").Id;
        project.AddSchedulePolicy(target, "早班");
        CreateIdleSettings(root, logger).ForceScheduledStart.Value = true;

        var clock = new ScheduleTestClock();
        var quit = new QuitAfterRunTracker();
        var dispatcher = Dispatcher.CurrentDispatcher;
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
        coordinator.StateChanged += (_, snapshot) =>
            dispatcher.BeginInvoke(() => { exitRequested |= quit.Observe(snapshot); });
        try {
            WithRuntimeControlWindow(logger, directory, window => {
                window.Show();
                PumpUntil(() => HeaderShows(window, "StopTaskHeaderButton", enabled: true), "开机 Run 应已开始。");
                var bootId = coordinator.Snapshot.WorkerSnapshot!.ActiveRun!.RunId;
                if (manual) {
                    Click(window, "StopTaskHeaderButton");
                    PumpUntil(() => exitRequested, "手动停止带 -q 的开机 Run 应触发退出。");
                    return;
                }
                var warned = false;
                window.NotificationRequested += (_, notification) => warned |= !notification.Failed;
                clock.Advance(TimeSpan.FromMinutes(1));
                var scheduled = window.CheckScheduledRunsAsync();
                PumpUntil(() => warned, "顶替开机 Run 前应有通知。");
                clock.Advance(TimeSpan.FromSeconds(10));
                PumpUntil(() => scheduled.IsCompleted, "顶替开机 Run 的定时执行应完成提交。");
                scheduled.GetAwaiter().GetResult();
                PumpUntil(() => coordinator.Snapshot.WorkerSnapshot?.ActiveRun is { } run && run.RunId != bootId,
                    "旧 Worker 结束后，新 Worker 应已接纳新的定时 Run。");
                Require(!exitRequested && workers.Count == 2 && wts.LogoffAttempts.SequenceEqual([sessionId])
                    && ProjectOf(window).ActiveConfigurationId == target, "顶替开机 Run 后应继续运行定时配置。");
            }, session: session, coordinator: coordinator, environment: CreateEnvironment(logger, session, coordinator),
                launchCommand: new(AutoStart: true, Instance: "日常", QuitAfterRun: true), quitAfterRun: quit,
                scheduleClock: clock, prepareEnvironment: (plan, options, _) => {
                    wts.ReportedId = sessionId;
                    wts.Sessions.Add(sessionId);
                    ConnectTestDesktop(session, sessionId);
                    return coordinator.PrepareWorkerAsync(sessionId, plan, token, options());
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
            await pipe.WriteAsync(WireEnvelope.Response(start.Operation, start.RequestId!.Value,
                new RunStartResponse("accepted")), token);
            await pipe.WriteAsync(StateEvent(running), token);
            if (boot) {
                var stop = await WorkerCoordinatorSelfTest.ReadRequestAsync(pipe, ProtocolOperations.RunStop, token);
                Require(ProtocolJson.Deserialize<RunStopRequest>(stop.Data).RunId == active.RunId,
                    "停止请求必须指向原开机任务。");
                await pipe.WriteAsync(WireEnvelope.Response(stop.Operation, stop.RequestId!.Value,
                    new RunStopResponse("stop_requested")), token);
                await pipe.WriteAsync(StateEvent(running with { StateRevision = 3, RunState = RunState.Idle,
                    ActiveRun = null, LastRun = active with { State = RunState.Cancelled } }), token);
            }
            await release.Task;
        }
    }
}
