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
                    && window.Sidebar.ConfigurationList.ActiveConfigurationId == target, "定时启动必须使用并保留手动准备重载后的配置。");
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

    private enum ContendedSchedule { Finished, Stopped, Stopping, Faulted, Exit, Update, Late, ManualEnd }

    private static void VerifyContendedForcedSchedule(AppLogger logger, string root, ContendedSchedule scenario)
    {
        CreateIdleSettings(CreateProjectFixture(root), logger).ForceScheduledStart.Value = true;
        var clock = new ScheduleTestClock();
        var connection = new TaskCompletionSource<FakeWorker>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listening = new CancellationTokenSource();
        var requests = new List<string>();
        WithRunningFakeWorker(logger, Path.Combine(root, "old-worker"), async (worker, token) => {
            connection.SetResult(worker);
            if (scenario == ContendedSchedule.Stopping) {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, listening.Token);
                var stop = await WorkerCoordinatorSelfTest.ReadRequestAsync(worker.Pipe, ProtocolOperations.RunStop,
                    linked.Token);
                requests.Add(stop.Operation);
                await ReplyToFakeWorkerStopAsync(worker, stop, linked.Token, "already_stopping");
            } else {
                await RecordRequestsAsync(worker.Pipe, requests, token, listening.Token);
            }
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
            // Late leaves its stop unanswered, so the end goes on only once the stop wait expires.
            var environment = CreateEnvironment(logger, session, coordinator,
                waitLimit: scenario == ContendedSchedule.Late ? ExpiringWaitLimit : null);
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
                    if (scenario is ContendedSchedule.Stopping or ContendedSchedule.Faulted) {
                        Publish(worker.ActiveRun with { State = RunState.Stopping },
                            faulted: scenario == ContendedSchedule.Faulted);
                    } else if (scenario == ContendedSchedule.Late) {
                        Publish(null, worker.ActiveRun with { State = RunState.Succeeded });
                    }
                    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var holder = environment.RunExclusiveAsync(() => release.Task);
                    try {
                        clock.Advance(TimeSpan.FromSeconds(73)); // 08:00:55
                        var check = window.CheckScheduledRunsAsync();
                        if (scenario == ContendedSchedule.Late) {
                            Require(warnings.Count == 0 && !check.IsCompleted, "空闲时应等待环境操作，无需提前倒计时。");
                            Publish(worker.ActiveRun with { RunId = Guid.NewGuid() });
                            release.SetResult();
                        }
                        PumpUntil(() => warnings.Count == 1, "到点时有 Active Run 应提示，即使环境仍被占用。");
                        clock.Advance(TimeSpan.FromSeconds(10));
                        PumpUntil(() => elapsed, "倒计时应按时结束。");
                        if (scenario != ContendedSchedule.Late) {
                            Require(!check.IsCompleted && wts.LogoffAttempts.Count == 0,
                                "倒计时跨分钟后仍须等待先前操作，不能提前注销。");
                        }
                        Task<EnvironmentEndResult>? manualEnd = null;
                        if (scenario == ContendedSchedule.ManualEnd) {
                            manualEnd = environment.RequestEnd(() => true);
                            Publish(null, worker.ActiveRun with { State = RunState.Cancelled });
                        } else if (scenario is ContendedSchedule.Finished or ContendedSchedule.Stopped) {
                            var state = scenario == ContendedSchedule.Finished
                                ? RunState.Succeeded : RunState.Cancelled;
                            Publish(null, worker.ActiveRun with { State = state });
                        } else if (scenario is ContendedSchedule.Exit or ContendedSchedule.Update) {
                            if (scenario == ContendedSchedule.Update) {
                                environment.Closing = true;
                            } else {
                                window.SetExitInProgress(true);
                                window.SetExitInProgress(false); // The user declines the exit before the gate opens.
                            }
                        }
                        release.TrySetResult();
                        PumpUntil(() => check.IsCompleted && holder.IsCompleted, "释放环境后，本次机会应结束。");
                        check.GetAwaiter().GetResult();
                        if (scenario == ContendedSchedule.ManualEnd) {
                            PumpUntil(() => manualEnd!.IsCompleted, "用户确认的结束请求应完成。");
                            Require(manualEnd!.GetAwaiter().GetResult() == EnvironmentEndResult.Ended
                                && wts.LogoffAttempts.SequenceEqual([initial.ChildSessionId]) && preparations.Count == 0,
                                "已排队的强制顶替应让位给用户确认的结束请求，不应准备新分身。");
                        } else if (scenario is ContendedSchedule.Exit or ContendedSchedule.Update) {
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
        var expectedStops = scenario is ContendedSchedule.Late or ContendedSchedule.Stopping ? 1 : 0;
        Require(requests.Count(operation => operation == ProtocolOperations.RunStop) == expectedStops,
            "仍在运行或停止中的任务交给 Worker 停止。已终结、Faulted 或正在退出的任务无需发送停止请求。");
    }
}
