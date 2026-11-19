using Gui.Models;
using Gui.Views;
using Gui.Worker;
using Protocol;

namespace Gui.Infrastructure;

internal static partial class SelfTestRunner
{
    private static void VerifyForcedScheduledStart(AppLogger logger, string directory)
    {
        foreach (var scenario in new[] { "disabled", "cancel", "timeout" }) {
            var root = Path.Combine(directory, scenario);
            var settings = CreateIdleSettings(CreateProjectFixture(root), logger);
            settings.ForceScheduledStart.Value = scenario != "disabled";
            var clock = new ScheduleTestClock();
            WithRunningFakeWorker(logger, Path.Combine(root, "old-worker"), async (worker, token) => {
                if (scenario != "timeout") { return; }
                var stop = await WorkerCoordinatorSelfTest.ReadRequestAsync(worker.Pipe, ProtocolOperations.RunStop,
                    token, worker.Coordinator.Snapshot.WorkerSnapshot);
                Require(ProtocolJson.Deserialize<RunStopRequest>(stop.Data).RunId == worker.ActiveRun.RunId,
                    "到期时应停止原先运行中的任务。");
                await worker.Pipe.WriteAsync(WireEnvelope.Response(stop.Operation, stop.RequestId!.Value,
                    new RunStopResponse("stop_requested")), token);
                var done = worker.Coordinator.Snapshot.WorkerSnapshot! with {
                    StateRevision = 2, RunState = RunState.Idle, ActiveRun = null,
                    LastRun = worker.ActiveRun with { State = RunState.Cancelled }
                };
                await worker.Pipe.WriteAsync(StateEvent(done), token);
            }, coordinator => {
                AwaitRunningWorker(coordinator);
                var old = coordinator.Snapshot.WorkerSnapshot!;
                var wts = new SimulatedWts();
                RunState? terminalAtLogoff = null;
                wts.DuringLogoff = _ => terminalAtLogoff = coordinator.Snapshot.WorkerSnapshot?.LastRun?.State;
                using var session = wts.CreateManager(logger);
                var preparations = new List<Guid>();
                WithRuntimeControlWindow(logger, root, window => {
                    wts.ReportedId = old.ChildSessionId;
                    wts.Sessions.Add(old.ChildSessionId);
                    var project = ProjectOf(window);
                    var first = project.ActiveConfigurationId;
                    project.RenameConfiguration(first, "第一班");
                    var task = project.SelectedTaskNames.First();
                    project.AddSchedulePolicy(first, "早班");
                    var second = project.CreateConfiguration();
                    project.RenameConfiguration(second, "第二班");
                    project.AddItem(PlanItemKind.MaaTask, task);
                    project.AddSchedulePolicy(second, "早班");
                    project.ActivateConfiguration(first);
                    var notices = new List<RunTerminalNotification>();
                    window.NotificationRequested += (_, n) => notices.Add(n);
                    var countdowns = new List<ForcedScheduledStartCountdown>();
                    // The unattended case has no countdown UI installed.
                    if (scenario != "timeout") {
                        window.ForcedScheduledStartPending += (_, c) => countdowns.Add(c);
                    }
                    var ended = 0;
                    window.ForcedScheduledStartEnded += (_, _) => ended++;
                    bool Logged(string message) => window.LogLines.OfType<LogEntry>()
                        .Any(e => e.TaskLabel == "定时" && e.Message == message);
                    clock.Advance(TimeSpan.FromSeconds(scenario == "timeout" ? 73 : 60));
                    var check = window.CheckScheduledRunsAsync();
                    if (scenario == "disabled") {
                        PumpUntil(() => check.IsCompleted, "关闭强制后遇忙应立即跳过。");
                        Require(Logged("跳过「第一班」：正在运行其他任务") && countdowns.Count == 0 && notices.Count == 0,
                            "默认忙碌跳过应有记录且无倒计时或系统提示。");
                    } else {
                        PumpUntil(() => notices.Any(n => !n.Failed), "强制启动前应发出系统提示。");
                        Require(notices.Single(n => !n.Failed).Message.Contains("第一班"), "系统提示应指明即将启动的配置。");
                        if (scenario == "cancel") {
                            Require(countdowns is [{ ConfigurationName: "第一班" } countdown]
                                && countdown.Deadline == clock.GetLocalNow().AddSeconds(10),
                                "同刻只为首份获准的配置倒计时，并向界面提供截止时间。");
                            countdowns.Single().Cancel();
                        } else {
                            clock.Advance(TimeSpan.FromSeconds(9));
                            Require(!check.IsCompleted && wts.LogoffAttempts.Count == 0
                                && coordinator.Snapshot.WorkerSnapshot?.ActiveRun?.RunId == old.ActiveRun!.RunId,
                                "跨分钟后仍应保留完整的十秒取消机会。");
                            clock.Advance(TimeSpan.FromSeconds(1));
                        }
                        PumpUntil(() => check.IsCompleted, "取消或到期后应处理完这组机会。");
                        Require(ended == 1 && Logged("跳过「第二班」：与「第一班」同时到点"),
                            "一次倒计时只结束一次，同时命中的其余配置应说明胜出者。");
                    }
                    check.GetAwaiter().GetResult();
                    if (scenario == "timeout") {
                        Require(terminalAtLogoff == RunState.Cancelled && preparations.SequenceEqual([first])
                            && wts.LogoffAttempts.SequenceEqual([old.ChildSessionId]),
                            "无人取消时应先停止旧 Run，再结束旧分身，并从新的环境准备目标配置。");
                    } else {
                        window.CheckScheduledRunsAsync().GetAwaiter().GetResult();
                        Require(preparations.Count == 0 && wts.LogoffAttempts.Count == 0
                            && coordinator.Snapshot.WorkerSnapshot?.ActiveRun?.RunId == old.ActiveRun!.RunId,
                            "跳过或取消后应保留当前任务，本分钟不能再次启动。");
                        Require(scenario != "cancel" || countdowns.Count == 1 && Logged("已取消「第一班」的强制启动，当前任务继续"),
                            "取消强制启动应记录原因且不再倒计时。");
                    }
                }, session: session, coordinator: coordinator, scheduleClock: clock,
                    environment: CreateEnvironment(logger, session, coordinator),
                    prepareEnvironment: (project, _, _) => {
                        preparations.Add(project.ActiveConfigurationId);
                        throw new InvalidOperationException("到达新的准备流程");
                    });
            });
        }
        VerifyIdleForcedSchedule(logger, Path.Combine(directory, "idle"));
    }

    private static void VerifyIdleForcedSchedule(AppLogger logger, string root)
    {
        var settings = CreateIdleSettings(CreateProjectFixture(root), logger);
        settings.ForceScheduledStart.Value = true;
        var clock = new ScheduleTestClock();
        var preparations = new List<Guid>();
        WithRuntimeControlWindow(logger, root, window => {
            var project = ProjectOf(window);
            var target = project.ActiveConfigurationId;
            var policy = project.AddSchedulePolicy(target, "早班");
            project.UpdateSchedulePolicy(target, policy with { Times = [new(8, 0), new(8, 1)] });
            var countdowns = 0;
            window.ForcedScheduledStartPending += (_, _) => countdowns++;
            window.SetExitInProgress(true);
            clock.Advance(TimeSpan.FromMinutes(1));
            var closing = window.CheckScheduledRunsAsync();
            PumpUntil(() => closing.IsCompleted, "退出时到点应跳过本次机会。");
            Require(preparations.Count == 0 && countdowns == 0, "退出时不能执行强制定时启动。");
            window.SetExitInProgress(false);
            clock.Advance(TimeSpan.FromMinutes(1));
            var idle = window.CheckScheduledRunsAsync();
            PumpUntil(() => idle.IsCompleted, "没有 Active Run 时应立即开始准备。");
            Require(preparations.SequenceEqual([target]) && countdowns == 0, "空闲的定时配置不应等待十秒。");
        }, scheduleClock: clock, prepareEnvironment: (project, _, _) => {
            preparations.Add(project.ActiveConfigurationId);
            throw new InvalidOperationException("到达空闲环境的准备流程");
        });
    }
}
