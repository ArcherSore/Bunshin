using System.Text.Json;
using System.Windows.Threading;

namespace Gui.Views;

public partial class MainWindow
{
    private readonly TimeProvider _scheduleClock;
    private readonly DispatcherTimer _scheduleTimer;
    private DateTime _lastCheckedMinute;
    private readonly HashSet<Guid> _checkedConfigurations = [];
    private bool _selectingSchedule;

    private void StartScheduleClock()
    {
        if (_scheduleTimer.IsEnabled || _allowClose) {
            return;
        }
        _scheduleTimer.Tick += (_, _) => _ = CheckScheduledRunsAsync();
        _scheduleTimer.Start();
        _ = Dispatcher.InvokeAsync(CheckScheduledRunsAsync);
    }

    private DateTime CurrentScheduleMinute()
    {
        var now = _scheduleClock.GetLocalNow();
        return new(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0);
    }

    // The host calls this on the GUI dispatcher when the window becomes visible or gains focus.
    internal async Task CheckScheduledRunsAsync()
    {
        if (!_scheduleTimer.IsEnabled || _projectPlan is not { } project) {
            return;
        }
        var minute = CurrentScheduleMinute();
        var time = TimeOnly.FromDateTime(minute);
        if (_lastCheckedMinute != minute) {
            _lastCheckedMinute = minute;
            _checkedConfigurations.Clear();
        }
        var due = project.Configurations.Select(c => (c.Id, c.Name,
                Policy: c.SchedulePolicies.FirstOrDefault(p => p.FiresAt(minute.DayOfWeek, time))))
            .Where(c => c.Policy is not null && _checkedConfigurations.Add(c.Id)).ToArray();
        if (due.Length == 0) {
            return;
        }
        HashSet<Guid> claimed;
        try {
            // Claim the whole batch before yielding to start, a focus event or the next timer tick.
            claimed = ClaimScheduleOpportunities(minute, due.Select(c => c.Id));
        } catch (IOException exception) when ((exception.HResult & 0xffff) == 32) { // ERROR_SHARING_VIOLATION
            foreach (var configuration in due) {
                ReportScheduledSkip(configuration.Name, "定时执行记录正被占用");
            }
            return;
        } catch (Exception exception) {
            var names = string.Join("、", due.Select(c => c.Name));
            ReportScheduledStartFailure(names, "无法保存定时执行记录", exception);
            return;
        }
        var ownsSelection = !_selectingSchedule;
        _selectingSchedule = true;
        string? skip = ownsSelection ? null : "正在运行其他任务";
        try {
            foreach (var configuration in due.Where(c => claimed.Contains(c.Id))) {
                if (!_scheduleTimer.IsEnabled) {
                    return;
                }
                if (skip is null && CurrentScheduleMinute() != minute) {
                    skip = "当前时间已离开本次定时的分钟";
                }
                if (skip is not null) {
                    ReportScheduledSkip(configuration.Name, skip);
                    continue;
                }
                try {
                    var policyTime = $"{configuration.Policy!.Name} {minute:HH:mm}";
                    var result = await TryStartScheduledRunAsync(configuration.Id, policyTime);
                    skip = result.Disposition switch {
                        ScheduledRunDisposition.Admitted => $"与「{configuration.Name}」同时到点",
                        ScheduledRunDisposition.Failed => result.Reason,
                        ScheduledRunDisposition.Busy => result.Reason ?? "运行环境已被占用",
                        _ => null
                    };
                } catch (Exception exception) {
                    ReportScheduledStartFailure(configuration.Name, "运行环境状态未知", exception);
                    skip = $"配置“{configuration.Name}”的启动结果未确认";
                }
            }
        } finally {
            if (ownsSelection) {
                _selectingSchedule = false;
            }
        }
    }

    private HashSet<Guid> ClaimScheduleOpportunities(DateTime minute, IEnumerable<Guid> candidates)
    {
        var directory = Path.Combine(_applicationDirectory, "state");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "schedule-opportunities.json");
        // Other Windows sessions may use the same installation. Hold the lock only for the local state transaction.
        using var fileLock = File.Open(path + ".lock", FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        var records = File.Exists(path)
            ? JsonSerializer.Deserialize<Dictionary<DateTime, HashSet<Guid>>>(File.ReadAllText(path))
                ?? throw new InvalidDataException("定时执行记录无效。")
            : [];
        if (!records.TryGetValue(minute, out var seen)) {
            records[minute] = seen = [];
        }
        var claimed = candidates.Where(seen.Add).ToHashSet();
        if (claimed.Count != 0) {
            // Retain local wall-clock minutes across a daylight-saving rollback, with bounded disk usage.
            foreach (var old in records.Keys.Where(key => key.Date != minute.Date).ToArray()) {
                records.Remove(old);
            }
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(records));
            File.Move(temp, path, overwrite: true);
        }
        return claimed;
    }
}
