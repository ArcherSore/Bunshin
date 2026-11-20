using Gui.Views;
using Gui.Worker;

namespace Gui.Infrastructure;

internal static partial class SelfTestRunner
{
    private static void VerifyScheduledRunBeforeWorkerReconnect(AppLogger logger, string directory)
    {
        CreateIdleSettings(CreateProjectFixture(directory), logger).ForceScheduledStart.Value = true;
        var clock = new ScheduleTestClock();
        WithFakeWorker(logger, Path.Combine(directory, "old-worker"), (coordinator, record, pipeName) => {
            var wts = new SimulatedWts { Sessions = [record.ChildSessionId] };
            using var session = wts.CreateManager(logger);
            using var timeout = new CancellationTokenSource(OnPoolLimit);
            var preparations = 0;
            WithRuntimeControlWindow(logger, directory, window => {
                // Only the scheduling decision needs the old session. Reporting it at Loaded would start RDP restore.
                wts.ReportedId = record.ChildSessionId;
                var project = ProjectOf(window);
                var target = project.ActiveConfigurationId;
                var policy = project.AddSchedulePolicy(target, "早班");
                project.UpdateSchedulePolicy(target, policy with { Times = [new(8, 0), new(8, 1)] });
                var countdowns = new List<ForcedScheduledStartCountdown>();
                window.ForcedScheduledStartPending += (_, countdown) => countdowns.Add(countdown);

                clock.Advance(TimeSpan.FromMinutes(1));
                var due = window.CheckScheduledRunsAsync();
                PumpUntil(() => due.IsCompleted, "等待重连时应跳过本次强制定时机会。");
                due.GetAwaiter().GetResult();
                Require(wts.LogoffAttempts.Count == 0 && preparations == 0 && countdowns.Count == 0,
                    "旧 Worker 仍存活但尚未报告任务状态时，定时不能直接注销它所在的分身。");

                var active = WorkerCoordinatorSelfTest.CreateActiveRun(project.CreateRunStartAttempt(target));
                var pipe = OnPool(() => WorkerCoordinatorSelfTest.OpenConnectionAsync(
                    pipeName, record, 0, timeout.Token, active));
                try {
                    AwaitRunningWorker(coordinator);
                    var sameMinute = window.CheckScheduledRunsAsync();
                    PumpUntil(() => sameMinute.IsCompleted, "同一分钟内重连成功也不能补跑已跳过的机会。");
                    sameMinute.GetAwaiter().GetResult();
                    Require(countdowns.Count == 0, "同一分钟内重连成功也不能补跑已跳过的机会。");
                    clock.Advance(TimeSpan.FromMinutes(1));
                    var next = window.CheckScheduledRunsAsync();
                    PumpUntil(() => countdowns.Count == 1, "新鲜快照确认 Run 仍在运行后，下次定时应先倒计时。");
                    countdowns.Single().Cancel();
                    PumpUntil(() => next.IsCompleted, "取消后应处理完本次定时机会。");
                    next.GetAwaiter().GetResult();
                    Require(wts.LogoffAttempts.Count == 0 && preparations == 0,
                        "重连后取消强制启动应保留原运行环境。");
                } finally {
                    pipe.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
            }, session: session, coordinator: coordinator, scheduleClock: clock,
                environment: CreateEnvironment(logger, session, coordinator), prepareEnvironment: (_, _, _) => {
                    preparations++;
                    return Task.FromException(new InvalidOperationException("模拟新环境准备失败"));
                });
        }, workerListed: () => true);
    }

    private static void VerifyScheduledRunRecovery(AppLogger logger, string directory)
    {
        CreateIdleSettings(CreateProjectFixture(directory), logger).ForceScheduledStart.Value = true;
        var wts = new SimulatedWts();
        using var session = wts.CreateManager(logger);
        var clock = new ScheduleTestClock();
        WithFakeWorker(logger, Path.Combine(directory, "old-worker"), (coordinator, record, _) => {
            var attempts = 0;
            var prepared = new List<Guid>();
            var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var environment = CreateEnvironment(logger, session, coordinator, (_, _) =>
                ++attempts < 4 ? Task.FromException(new IOException("无法确认旧进程退出")) : exited.Task,
                TimeSpan.FromSeconds(10));
            WithRuntimeControlWindow(logger, directory, window => {
                var project = ProjectOf(window);
                var target = project.ActiveConfigurationId;
                var policy = project.AddSchedulePolicy(target, "早班");
                project.UpdateSchedulePolicy(target, policy with { Times = [new(8, 0), new(8, 1), new(8, 2)] });
                wts.ReportedId = record.ChildSessionId;
                wts.Sessions.Add(record.ChildSessionId);
                var end = environment.EndAsync();
                PumpUntil(() => end.IsCompleted, "先注销旧分身，模拟旧 Worker 尚未确认退出。");
                Require(end.GetAwaiter().GetResult().Outcome == EnvironmentEndOutcome.Unconfirmed
                    && wts.LogoffAttempts.SequenceEqual([record.ChildSessionId]),
                    "已注销分身的 Worker 尚未确认退出时，应保留 Admission 供下次定时恢复。");
                var notices = new List<RunTerminalNotification>();
                window.NotificationRequested += (_, n) => notices.Add(n);
                for (var minute = 1; minute <= 2; minute++) {
                    clock.Advance(TimeSpan.FromMinutes(1));
                    var due = window.CheckScheduledRunsAsync();
                    PumpUntil(() => due.IsCompleted, "旧 Worker 未确认退出时，本次机会应以失败结束。");
                    due.GetAwaiter().GetResult();
                    window.CheckScheduledRunsAsync().GetAwaiter().GetResult();
                    Require(attempts == minute + 1 && prepared.Count == 0 && notices.Count == minute
                        && notices.All(n => n.Failed && n.Title == "定时任务未能启动" && n.Message.Contains("旧的桌面分身未能关闭")),
                        "旧分身未确认结束只能报告一次失败，重复检查不能重试或准备新的环境。");
                }
                clock.Advance(TimeSpan.FromMinutes(1));
                var recovery = window.CheckScheduledRunsAsync();
                PumpUntil(() => attempts == 4, "下次定时应能再次确认旧 Worker 退出。");
                Require(!recovery.IsCompleted && prepared.Count == 0, "旧 Worker 的退出尚未确认时不能开始准备。");
                exited.SetResult();
                PumpUntil(() => recovery.IsCompleted, "确认旧 Worker 退出后应能恢复定时启动。");
                recovery.GetAwaiter().GetResult();
                Require(prepared.SequenceEqual([target]) && wts.LogoffAttempts.SequenceEqual([record.ChildSessionId]),
                    "应复用已确认的分身注销结果，在 Worker 退出后准备目标配置。");
            }, session: session, coordinator: coordinator, environment: environment, scheduleClock: clock,
                prepareEnvironment: (project, _, _) => {
                    prepared.Add(project.ActiveConfigurationId);
                    throw new InvalidOperationException("模拟新环境准备失败");
                });
        }, workerListed: () => true);
    }
}
