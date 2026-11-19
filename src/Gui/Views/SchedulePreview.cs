using ProjectModel;

namespace Gui.Views;

internal enum ScheduleOutcome { Run, Skip, Invalid }

internal sealed record ScheduleEntry(TaskConfiguration Configuration, IReadOnlyList<SchedulePolicy> Policies);

internal sealed record ScheduleOccurrence(
    DateTime Time, IReadOnlyList<ScheduleEntry> Entries, TaskConfiguration? Winner);

// Predicts what the scheduler will do from the saved policies, in the same order it decides: per local minute, the
// first configuration in list order that can run wins and the rest are skipped. Busy runtime states are not predicted.
internal sealed class SchedulePreview
{
    internal static readonly DayOfWeek[] WeekOrder = [
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday,
        DayOfWeek.Saturday, DayOfWeek.Sunday];
    private static readonly string[] DayNames = ["日", "一", "二", "三", "四", "五", "六"];

    private sealed record ScheduleConflict(TaskConfiguration Other, bool OtherFirst, IReadOnlyList<DayOfWeek> Days,
        IReadOnlyList<TimeOnly> Times);

    private readonly IReadOnlyList<TaskConfiguration> _configurations;
    private readonly Func<Guid, string?> _invalidReason;
    private readonly Dictionary<Guid, string?> _reasons = [];

    internal SchedulePreview(IReadOnlyList<TaskConfiguration> configurations, Func<Guid, string?> invalidReason,
        DateTime now, int days = 8)
    {
        _configurations = configurations;
        _invalidReason = invalidReason;
        Now = now;
        Occurrences = Enumerate(days);
    }

    internal DateTime Now { get; }
    internal IReadOnlyList<ScheduleOccurrence> Occurrences { get; }
    internal ScheduleOccurrence? NextRun => Occurrences.FirstOrDefault(o => o.Winner is not null);

    internal string? InvalidReason(TaskConfiguration configuration)
    {
        if (!_reasons.TryGetValue(configuration.Id, out var reason)) {
            _reasons[configuration.Id] = reason = _invalidReason(configuration.Id);
        }
        return reason;
    }

    internal (ScheduleOccurrence Occurrence, ScheduleOutcome Outcome)? NextFor(TaskConfiguration configuration,
        SchedulePolicy? policy = null)
    {
        var occurrence = Occurrences.FirstOrDefault(o => o.Entries.Any(e => e.Configuration == configuration
            && (policy is null || e.Policies.Contains(policy))));
        if (occurrence is null) {
            return null;
        }
        // A configuration that cannot run needs fixing whatever else is due, so that outranks being skipped.
        return (occurrence, InvalidReason(configuration) is not null ? ScheduleOutcome.Invalid
            : occurrence.Winner == configuration ? ScheduleOutcome.Run : ScheduleOutcome.Skip);
    }

    // Enabled policies of other configurations that share a day and a time with this one.
    private List<ScheduleConflict> Conflicts(TaskConfiguration configuration, SchedulePolicy policy)
    {
        if (!policy.Enabled) {
            return [];
        }
        var index = IndexOf(configuration);
        var conflicts = new List<ScheduleConflict>();
        foreach (var other in _configurations.Where(c => c.Id != configuration.Id)) {
            var days = new HashSet<DayOfWeek>();
            var times = new SortedSet<TimeOnly>();
            foreach (var candidate in other.SchedulePolicies.Where(p => p.Enabled)) {
                var sharedDays = policy.Days.Intersect(candidate.Days).ToArray();
                var sharedTimes = policy.Times.Intersect(candidate.Times).ToArray();
                if (sharedDays.Length != 0 && sharedTimes.Length != 0) {
                    days.UnionWith(sharedDays);
                    times.UnionWith(sharedTimes);
                }
            }
            if (days.Count != 0) {
                conflicts.Add(new(other, IndexOf(other) < index, WeekOrder.Where(days.Contains).ToArray(), [.. times]));
            }
        }
        return conflicts;
    }

    internal IReadOnlyList<(bool Error, string Text)> Issues(TaskConfiguration configuration, SchedulePolicy policy)
    {
        var issues = new List<(bool, string)>();
        if (policy.Days.Count == 0) {
            issues.Add((false, "未选星期，不会运行。"));
        } else if (policy.Times.Count == 0) {
            issues.Add((false, "未设时间，不会运行。"));
        }
        if (policy.Enabled && InvalidReason(configuration) is { } reason) {
            issues.Add((true, $"{reason.TrimEnd('。')}，到点时无法运行。"));
        }
        foreach (var conflict in Conflicts(configuration, policy)) {
            var when = $"{DaysText(conflict.Days)} {string.Join("、", conflict.Times.Select(TimeText))}";
            issues.Add((false, conflict.OtherFirst
                ? $"{when} 与「{conflict.Other.Name}」同时到点，本配置将跳过。"
                : $"{when} 与「{conflict.Other.Name}」同时到点，「{conflict.Other.Name}」将跳过。"));
        }
        return issues;
    }

    // The configuration list shows one short state; the reason is in the editor.
    internal (ScheduleOutcome? Outcome, string Text) ListState(TaskConfiguration configuration)
    {
        if (NextFor(configuration) is not { } next) {
            return (null, "未安排");
        }
        var (occurrence, outcome) = next;
        var when = WhenText(occurrence.Time, Now);
        return (outcome, outcome switch {
            ScheduleOutcome.Skip => $"{when} · 跳过",
            ScheduleOutcome.Invalid => $"{when} · 无法运行",
            _ => when
        });
    }

    internal static string TimeText(TimeOnly time) => time.ToString("HH\\:mm");

    internal static string DayName(DayOfWeek day) => "周" + DayNames[(int)day];

    internal static string DaysText(IEnumerable<DayOfWeek> days)
    {
        var ordered = WeekOrder.Where(days.Contains).ToArray();
        if (ordered.Length == 0) {
            return "未选星期";
        }
        if (ordered.Length == 7) {
            return "每天";
        }
        if (ordered.SequenceEqual(WeekOrder[..5])) {
            return "工作日";
        }
        if (ordered.SequenceEqual(WeekOrder[5..])) {
            return "周末";
        }
        var indexes = ordered.Select(d => Array.IndexOf(WeekOrder, d)).ToArray();
        if (ordered.Length >= 3 && indexes.Zip(indexes.Skip(1)).All(pair => pair.Second == pair.First + 1)) {
            return $"{DayName(ordered[0])}至{DayName(ordered[^1])}";
        }
        return "周" + string.Join("、", ordered.Select(d => DayNames[(int)d]));
    }

    internal static string Summary(SchedulePolicy policy) => $"{DaysText(policy.Days)} " +
        (policy.Times.Count == 0 ? "未设时间" : string.Join("、", policy.Times.Select(TimeText)));

    // Like MXU's collapsed schedule: up to three times are listed, more become a count; the tooltip has them all.
    internal static string ShortSummary(SchedulePolicy policy) => policy.Times.Count <= 3 ? Summary(policy)
        : $"{DaysText(policy.Days)} · {policy.Times.Count} 个时间";

    internal static string WhenText(DateTime time, DateTime now) => (time.Date - now.Date).Days switch {
        0 => "今天",
        1 => "明天",
        _ => DayName(time.DayOfWeek)
    } + " " + time.ToString("HH\\:mm");

    internal static string InText(DateTime time, DateTime now)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling((time - now).TotalMinutes));
        return minutes switch {
            < 60 => $"{minutes} 分钟后",
            < 1440 => $"{minutes / 60} 小时 {minutes % 60} 分后",
            _ => $"{minutes / 1440} 天 {minutes % 1440 / 60} 小时后"
        };
    }

    // Accepts 8, 8:05, 805, 0805 and the full-width colon.
    internal static TimeOnly? ParseTime(string text)
    {
        var value = text.Trim().Replace('：', ':');
        int hour, minute;
        if (value.Contains(':')) {
            var parts = value.Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[0], out hour) || !int.TryParse(parts[1], out minute)) {
                return null;
            }
        } else if (value.Length is >= 1 and <= 4 && int.TryParse(value, out var number)) {
            (hour, minute) = value.Length <= 2 ? (number, 0) : (number / 100, number % 100);
        } else {
            return null;
        }
        return hour is >= 0 and < 24 && minute is >= 0 and < 60 ? new TimeOnly(hour, minute) : null;
    }

    private int IndexOf(TaskConfiguration configuration) =>
        _configurations.ToList().FindIndex(c => c.Id == configuration.Id);

    private IReadOnlyList<ScheduleOccurrence> Enumerate(int days)
    {
        var occurrences = new List<ScheduleOccurrence>();
        for (var offset = 0; offset < days; offset++) {
            var date = Now.Date.AddDays(offset);
            var groups = new SortedDictionary<DateTime, List<ScheduleEntry>>();
            foreach (var configuration in _configurations) {
                foreach (var policy in configuration.SchedulePolicies) {
                    if (!policy.Enabled || !policy.Days.Contains(date.DayOfWeek)) {
                        continue;
                    }
                    foreach (var time in policy.Times) {
                        var at = date.Add(time.ToTimeSpan());
                        // The scheduler only matches the current minute, so a minute already under way is past.
                        if (at <= Now) {
                            continue;
                        }
                        if (!groups.TryGetValue(at, out var entries)) {
                            groups[at] = entries = [];
                        }
                        var index = entries.FindIndex(e => e.Configuration == configuration);
                        if (index < 0) {
                            entries.Add(new(configuration, [policy]));
                        } else if (!entries[index].Policies.Contains(policy)) {
                            entries[index] = entries[index] with { Policies = [.. entries[index].Policies, policy] };
                        }
                    }
                }
            }
            foreach (var (at, entries) in groups) {
                occurrences.Add(new(at, entries, entries.Select(e => e.Configuration)
                    .FirstOrDefault(c => InvalidReason(c) is null)));
            }
        }
        return occurrences;
    }
}
