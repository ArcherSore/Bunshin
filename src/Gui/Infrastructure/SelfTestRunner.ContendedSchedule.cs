using Gui.Views;
using Gui.Worker;
using ProjectModel;
using Protocol;

namespace Gui.Infrastructure;

internal static partial class SelfTestRunner
{
    private static void VerifyForcedScheduleAfterReload(AppLogger logger, string directory)
    {
        var root = CreateProjectFixture(directory);
        CreateIdleSettings(root, logger).ForceScheduledStart.Value = true;
        var clock = new ScheduleTestClock();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var preparedNames = new List<string>();
        WithRuntimeControlWindow(logger, directory, window => {
            var project = ProjectOf(window);
            var original = project.ActiveConfigurationId;
            var item = project.SelectedTaskNames.First();
            var target = project.CreateConfiguration();
            project.AddItem(PlanItemKind.MaaTask, item);
            project.AddSchedulePolicy(target, "早班");
            project.ActivateConfiguration(original);
            Named<System.Windows.Controls.MenuItem>(window, "StartSessionMenuItem").RaiseEvent(
                new System.Windows.RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
            clock.Advance(TimeSpan.FromMinutes(1));
            var due = window.CheckScheduledRunsAsync();
            try {
                Require(!due.IsCompleted && preparedNames.Count == 0, "手动准备占着环境时，应保留强制定时机会。");
                var path = Path.Combine(root, "config", "configurations.json");
                ProjectPlanModule.Open(root, path).RenameConfiguration(target, "新版周常");
                release.SetResult();
                PumpUntil(() => due.IsCompleted, "手动准备重载项目后，目标应在同一个新项目中启动。");
                due.GetAwaiter().GetResult();
                var loaded = ProjectPlanModule.Open(root, path);
                Require(preparedNames.Count == 2 && preparedNames[1] == "新版周常"
                    && loaded.Configurations.Single(c => c.Id == target).Name == "新版周常"
                    && ProjectOf(window).ActiveConfigurationId == target, "定时启动必须使用并保留手动准备重载后的配置。");
            } finally {
                release.TrySetResult();
            }
        }, scheduleClock: clock, runOperation: async operation => {
            await release.Task;
            await operation();
        }, prepareEnvironment: (project, _, showDesktop) => {
            preparedNames.Add(project.Configurations.Single(c => c.Id == project.ActiveConfigurationId).Name);
            return showDesktop ? Task.CompletedTask : Task.FromException(new InvalidOperationException("结束模拟准备"));
        });
    }

    private static void VerifyContendedForcedSchedule(AppLogger logger, string root, string scenario)
    {
        CreateIdleSettings(CreateProjectFixture(root), logger).ForceScheduledStart.Value = true;
        var clock = new ScheduleTestClock();
        var connection = new TaskCompletionSource<FakeWorker>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listening = new CancellationTokenSource();
        var requests = new List<string>();
        WithRunningFakeWorker(logger, Path.Combine(root, "old-worker"), async (worker, token) => {
            connection.SetResult(worker);
            await RecordRequestsAsync(worker.Pipe, requests, token, listening.Token);
        }, coordinator => {
            AwaitRunningWorker(coordinator);
            var worker = connection.Task.GetAwaiter().GetResult();
            var initial = coordinator.Snapshot.WorkerSnapshot!;
            var revision = initial.StateRevision;
            void Publish(RunSnapshot? active, RunSnapshot? last = null, bool faulted = false)
            {
                var next = initial with {
                    StateRevision = ++revision, ActiveRun = active, LastRun = last,
                    RunState = active?.State ?? RunState.Idle,
                    WorkerState = faulted ? WorkerState.Faulted : initial.WorkerState
                };
                Task.Run(() => worker.Pipe.WriteAsync(StateEvent(next), listening.Token)).GetAwaiter().GetResult();
                AwaitWorker(coordinator, s => s.WorkerSnapshot?.StateRevision == revision, "新快照应已到达。");
            }
            var wts = new SimulatedWts();
            using var session = wts.CreateManager(logger);
            var environment = CreateEnvironment(logger, session, coordinator);
            var preparations = new List<Guid>();
            try {
                WithRuntimeControlWindow(logger, root, window => {
                    wts.ReportedId = initial.ChildSessionId;
                    wts.Sessions.Add(initial.ChildSessionId);
                    var project = ProjectOf(window);
                    var target = project.ActiveConfigurationId;
                    project.AddSchedulePolicy(target, "早班");
                    var warnings = new List<ForcedScheduledStartCountdown>();
                    window.ForcedScheduledStartPending += (_, countdown) => warnings.Add(countdown);
                    var elapsed = false;
                    window.ForcedScheduledStartEnded += (_, _) => elapsed = true;
                    if (scenario is "stopping" or "faulted") {
                        Publish(worker.ActiveRun with { State = RunState.Stopping }, faulted: scenario == "faulted");
                    } else if (scenario == "late") {
                        Publish(null, worker.ActiveRun with { State = RunState.Succeeded });
                    }
                    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var holder = environment.RunExclusiveAsync(() => release.Task);
                    try {
                        clock.Advance(TimeSpan.FromSeconds(73)); // 08:00:55
                        var check = window.CheckScheduledRunsAsync();
                        if (scenario == "late") {
                            Require(warnings.Count == 0 && !check.IsCompleted, "空闲时应等待环境操作，无需提前倒计时。");
                            Publish(worker.ActiveRun with { RunId = Guid.NewGuid() });
                            release.SetResult();
                        }
                        PumpUntil(() => warnings.Count == 1, "到点时有 Active Run 应提示，即使环境仍被占用。");
                        clock.Advance(TimeSpan.FromSeconds(10));
                        PumpUntil(() => elapsed, "倒计时应按时结束。");
                        if (scenario != "late") {
                            Require(!check.IsCompleted && wts.LogoffAttempts.Count == 0,
                                "倒计时跨分钟后仍须等待先前操作，不能提前注销。");
                        }
                        if (scenario is "finished" or "stopped") {
                            var state = scenario == "finished" ? RunState.Succeeded : RunState.Cancelled;
                            Publish(null, worker.ActiveRun with { State = state });
                        } else if (scenario is "exit" or "update") {
                            if (scenario == "update") {
                                environment.Closing = true;
                            } else {
                                window.SetExitInProgress(true);
                                window.SetExitInProgress(false); // The user declines the exit before the gate opens.
                            }
                        }
                        release.TrySetResult();
                        PumpUntil(() => check.IsCompleted && holder.IsCompleted, "释放环境后，本次机会应结束。");
                        check.GetAwaiter().GetResult();
                        if (scenario is "exit" or "update") {
                            Require(wts.LogoffAttempts.Count == 0 && preparations.Count == 0,
                                "退出或交接更新后不能再启动已倒计时的配置。");
                        } else {
                            Require(wts.LogoffAttempts.SequenceEqual([initial.ChildSessionId])
                                && preparations.SequenceEqual([target]), "确认旧环境结束后应准备本次定时配置。");
                        }
                    } finally {
                        release.TrySetResult();
                        environment.Closing = false;
                        window.SetExitInProgress(false);
                    }
                }, session: session, coordinator: coordinator, environment: environment, scheduleClock: clock,
                    prepareEnvironment: (project, _, _) => {
                        preparations.Add(project.ActiveConfigurationId);
                        throw new InvalidOperationException("结束模拟准备");
                    });
            } finally {
                listening.Cancel();
            }
        });
        Require(requests.Count(operation => operation == ProtocolOperations.RunStop) == (scenario == "late" ? 1 : 0),
            "只应请求停止后来出现、仍在运行的任务。已终结、停止中、Faulted 或正在退出均不应重复发停止请求。");
    }
}
