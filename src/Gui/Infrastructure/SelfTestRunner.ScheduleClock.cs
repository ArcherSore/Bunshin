using System.Windows.Threading;
using Gui.Models;
using Gui.Views;

namespace Gui.Infrastructure;

internal static partial class SelfTestRunner
{
    private sealed class ScheduleTestClock(bool deliverAtStartup = true) : TimeProvider, ITimer
    {
        private TimerCallback? _tick;
        private object? _state;
        private DateTimeOffset _now = new(2026, 11, 15, 23, 59, 42, TimeSpan.Zero);
        private DateTimeOffset _next;
        private TimeSpan _period;
        public override TimeZoneInfo LocalTimeZone { get; } =
            TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
        public override DateTimeOffset GetUtcNow() => _now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _tick = callback;
            _state = state;
            Change(dueTime, period);
            if (deliverAtStartup) {
                Advance(TimeSpan.Zero);
            }
            return this;
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            _next = _now + dueTime;
            _period = period;
            return true;
        }

        // Sleeping hosts deliver a tick on wake, with the current time.
        internal void Advance(TimeSpan by)
        {
            _now += by;
            if (_tick is not null && _now >= _next) {
                _next = _now + _period;
                _tick(_state);
            }
        }

        public void Dispose() => _tick = null;
        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private static void VerifyScheduleClock(AppLogger logger, string directory)
    {
        var clock = new ScheduleTestClock();
        var starts = new List<Guid>();
        void Open(Action<MainWindow> verify) =>
            WithRuntimeControlWindow(logger, directory, verify, scheduleClock: clock,
                launchCommand: new(AutoStart: true), prepareEnvironment: (project, _, _) => {
                    starts.Add(project.ActiveConfigurationId);
                    throw new InvalidOperationException("模拟准备失败");
                });

        Guid daily = default;
        Open(window => {
            var project = ProjectOf(window);
            daily = project.ActiveConfigurationId;
            var policy = project.AddSchedulePolicy(daily, "工作日");
            project.UpdateSchedulePolicy(daily, policy with {
                Times = [new(8, 0), new(8, 1), new(8, 2), new(8, 4)]
            });
            project.AddSchedulePolicy(daily, "重叠");
            var disabled = project.AddSchedulePolicy(daily, "禁用");
            project.UpdateSchedulePolicy(daily, disabled with { Times = [new(8, 3)], Enabled = false });
            var deleted = project.AddSchedulePolicy(daily, "删除");
            project.UpdateSchedulePolicy(daily, deleted with { Times = [new(8, 3)] });
            project.DeleteSchedulePolicy(daily, deleted.Id);
        });
        clock.Advance(TimeSpan.FromMinutes(1));
        Open(window => {
            Require(!window.IsVisible && starts.SequenceEqual([daily]),
                "托盘启动到 08:00:42 时，应立即按本地星期和分钟执行一次。");
            window.CheckScheduledRunsAsync().GetAwaiter().GetResult();
            Require(starts.Count == 1, "恢复焦点不能重复执行同一分钟的配置。");
        });
        MainWindow? closed = null;
        Open(window => {
            closed = window;
            Require(starts.Count == 1, "同一分钟正常重开不能重复处理已获准但准备失败的机会。");
            clock.Advance(TimeSpan.FromSeconds(30));
            PumpUntil(() => starts.Count == 2, "托盘中经过 30 秒必须检查当前分钟。");
            clock.Advance(TimeSpan.FromMinutes(2));
            window.CheckScheduledRunsAsync().GetAwaiter().GetResult();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Require(starts.Count == 2, "恢复后不能补跑错过的 08:02，也不能执行已禁用、删除的 08:03 策略。");
        });
        clock.Advance(TimeSpan.FromMinutes(1));
        closed!.CheckScheduledRunsAsync().GetAwaiter().GetResult();
        closed.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
        Require(starts.Count == 2, "完全退出后旧宿主的时钟和恢复信号均应失效。");
        clock.Advance(TimeSpan.FromDays(5));
        Open(_ => Require(starts.Count == 2, "周六的 08:04 不应执行工作日策略。"));
    }

    private static void VerifyScheduleClockSelection(AppLogger logger, string directory)
    {
        var clock = new ScheduleTestClock();
        var preparations = new List<Guid>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        WithRuntimeControlWindow(logger, directory, window => {
            var project = ProjectOf(window);
            var a = project.ActiveConfigurationId;
            var task = project.SelectedTaskNames.First();
            project.RemoveTask(task);
            project.RenameConfiguration(a, "无任务");
            project.AddSchedulePolicy(a, "早班");
            var b = project.CreateConfiguration();
            project.RenameConfiguration(b, "第一班");
            project.AddTask(task);
            project.AddSchedulePolicy(b, "早班");
            var c = project.CreateConfiguration();
            project.RenameConfiguration(c, "第二班");
            project.AddTask(task);
            var policy = project.AddSchedulePolicy(c, "连续两分钟");
            project.UpdateSchedulePolicy(c, policy with { Times = [new(8, 0), new(8, 1)] });
            var d = project.CreateConfiguration();
            project.RenameConfiguration(d, "新启用");
            project.AddTask(task);
            project.ActivateConfiguration(c);
            var failures = new List<string>();
            var logs = new System.Collections.Concurrent.ConcurrentQueue<string>();
            void Record(object? _, LogEntry entry)
            {
                if (entry.Level == LogLevel.Info) {
                    logs.Enqueue(entry.Message);
                }
            }
            window.AutoRunFailed += (_, message) => failures.Add(message);
            logger.EntryWritten += Record;
            try {
                clock.Advance(TimeSpan.FromMinutes(1));
                PumpUntil(() => preparations.Count == 1, "首份无效配置之后应开始准备下一份。");
                Require(preparations.SequenceEqual([b]) && failures is [{ } failure] && failure.Contains("无任务")
                    && project.ActiveConfigurationId == b, "同刻应先报告 A 无效，再按配置列表顺序接纳 B。");
                project.AddSchedulePolicy(d, "刚刚启用");
                Require(window.CheckScheduledRunsAsync().IsCompletedSuccessfully
                    && logs.Any(line => line.Contains("新启用")),
                    "等待准备时新加入的同分钟机会应立即跳过。");
                clock.Advance(TimeSpan.FromSeconds(30));
                PumpUntil(() => logs.Any(line => line.Contains("第二班")), "相邻分钟的任务遇忙应立即跳过。");
                release.SetResult();
                PumpUntil(() => failures.Any(message => message.Contains("准备失败")), "获准后准备失败应报告原因。");
                PumpUntil(() => logs.Count(line => line.Contains("第二班")) >= 2, "获准启动后应记明同刻余项的跳过原因。");
                window.CheckScheduledRunsAsync().GetAwaiter().GetResult();
                Require(preparations.SequenceEqual([b]) && failures.Count == 2
                    && logs.Count(line => line.Contains("第二班")) == 2,
                    "B 准备失败不让位给 C，两分钟分别记录 C 的跳过原因，保持无重试、无额外通知。");
            } finally {
                release.TrySetResult();
                logger.EntryWritten -= Record;
            }
        }, scheduleClock: clock, prepareEnvironment: async (project, _, _) => {
            preparations.Add(project.ActiveConfigurationId);
            await release.Task;
            throw new InvalidOperationException("准备失败");
        });
    }

    private static void VerifyScheduleClockAtLogon(AppLogger logger, string directory)
    {
        foreach (var clockFirst in new[] { true, false }) {
            var host = Path.Combine(directory, clockFirst.ToString());
            var clock = new ScheduleTestClock(deliverAtStartup: clockFirst);
            Guid day = default, boot = default;
            WithRuntimeControlWindow(logger, host, window => {
                var project = ProjectOf(window);
                day = project.ActiveConfigurationId;
                project.RenameConfiguration(day, "定时配置");
                project.AddSchedulePolicy(day, "工作日");
                var task = project.SelectedTaskNames.First();
                boot = project.CreateConfiguration();
                project.RenameConfiguration(boot, "登录配置");
                project.AddTask(task);
            }, scheduleClock: clock);
            clock.Advance(TimeSpan.FromMinutes(1));
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var preparations = new List<Guid>();
            var messages = new System.Collections.Concurrent.ConcurrentQueue<string>();
            void Record(object? _, LogEntry entry)
            {
                if (entry.Level == LogLevel.Info) {
                    messages.Enqueue(entry.Message);
                }
            }
            logger.EntryWritten += Record;
            try {
                WithRuntimeControlWindow(logger, host, window => {
                    var failures = new List<string>();
                    window.AutoRunFailed += (_, message) => failures.Add(message);
                    clock.Advance(TimeSpan.Zero);
                    var skipped = clockFirst ? "登录配置" : "定时配置";
                    PumpUntil(() => messages.Any(m => m.Contains(skipped)),
                        $"托盘登录时遇忙的配置 {skipped} 应记明跳过原因。");
                    Require(preparations.SequenceEqual([clockFirst ? day : boot]) && !window.IsVisible,
                        "托盘登录时先抵达的启动应占用环境。");
                    release.SetResult();
                    PumpUntil(() => failures.Any(f => f.Contains("结束模拟启动")), "获准的启动应报告准备失败。");
                    window.CheckScheduledRunsAsync().GetAwaiter().GetResult();
                    Require(preparations.Count == 1 && failures.Count == 1, "遇忙的机会不得通知失败或重试。");
                }, scheduleClock: clock, launchCommand: new(AutoStart: true, Instance: boot.ToString()),
                    prepareEnvironment: async (project, _, _) => {
                        preparations.Add(project.ActiveConfigurationId);
                        await release.Task;
                        throw new InvalidOperationException("结束模拟启动");
                    });
            } finally {
                release.TrySetResult();
                logger.EntryWritten -= Record;
            }
        }
    }
}
