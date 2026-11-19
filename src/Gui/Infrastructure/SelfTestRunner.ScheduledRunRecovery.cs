namespace Gui.Infrastructure;

internal static partial class SelfTestRunner
{
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
                ++attempts < 3 ? Task.FromException(new IOException("无法确认旧进程退出")) : exited.Task,
                TimeSpan.FromSeconds(10));
            WithRuntimeControlWindow(logger, directory, window => {
                var project = ProjectOf(window);
                var target = project.ActiveConfigurationId;
                var policy = project.AddSchedulePolicy(target, "早班");
                project.UpdateSchedulePolicy(target, policy with { Times = [new(8, 0), new(8, 1), new(8, 2)] });
                wts.ReportedId = record.ChildSessionId;
                wts.Sessions.Add(record.ChildSessionId);
                var notices = new List<RunTerminalNotification>();
                window.NotificationRequested += (_, n) => notices.Add(n);
                for (var minute = 1; minute <= 2; minute++) {
                    clock.Advance(TimeSpan.FromMinutes(1));
                    var due = window.CheckScheduledRunsAsync();
                    PumpUntil(() => due.IsCompleted, "旧 Worker 未确认退出时，本次机会应以失败结束。");
                    due.GetAwaiter().GetResult();
                    window.CheckScheduledRunsAsync().GetAwaiter().GetResult();
                    Require(attempts == minute && prepared.Count == 0 && notices.Count == minute
                        && notices.All(n => n.Failed && n.Title == "定时任务未能启动" && n.Message.Contains("旧的桌面分身未能关闭")),
                        "旧分身未确认结束只能报告一次失败，重复检查不能重试或准备新的环境。");
                }
                clock.Advance(TimeSpan.FromMinutes(1));
                var recovery = window.CheckScheduledRunsAsync();
                PumpUntil(() => attempts == 3, "下次定时应能再次确认旧 Worker 退出。");
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
