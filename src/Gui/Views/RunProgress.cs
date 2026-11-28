using Protocol;

namespace Gui.Views;

// What one Plan Item shows. A cancelled item that started was stopped; one that never started did not run.
internal enum PlanItemProgress
{
    Pending, Running, Succeeded, Failed, Stopped, NotRun
}

// Kind and Name say which plan item it was, so a record still matches the plan after the plan has changed.
internal sealed record PlanItemProgressView(
    PlanItemKind Kind, string Name, string TaskLabel, PlanItemProgress State, DateTime? StartedAtUtc,
    DateTime? EndedAtUtc);

// The progress of the active Run, or the 运行记录 of the configuration on screen.
internal sealed record RunProgressView(
    bool Active, RunState State, IReadOnlyList<PlanItemProgressView> Items, int? CurrentIndex, DateTime? StartedAtUtc,
    DateTime? EndedAtUtc)
{
    internal int Total => Items.Count;
    internal int Succeeded => Count(PlanItemProgress.Succeeded);
    internal int Failed => Count(PlanItemProgress.Failed);
    internal int Stopped => Count(PlanItemProgress.Stopped);
    internal int NotRun => Count(PlanItemProgress.NotRun);
    // Only finished items count as processed; a stopped or skipped item never does.
    internal int Processed => Succeeded + Failed;

    private int Count(PlanItemProgress state) => Items.Count(item => item.State == state);
}

// Kept apart from RuntimeControls.Derive: progress is presentation, while that decides what may be done.
internal static class RunProgress
{
    // A running Run is always shown; otherwise the configuration's own record, whatever its plan looks like now.
    internal static RunProgressView? Derive(WorkerSnapshot? worker, RunRecord? record) =>
        worker?.ActiveRun is { } active ? Create(active, true) : record?.View;

    // Where 继续运行 starts in the current plan: its first item the record has and did not finish successfully, with
    // the record's item for it. Items the record does not have never start it.
    internal static (int Index, PlanItemProgressView Item)? ContinueFrom(
        RunProgressView view, IReadOnlyList<(PlanItemKind Kind, string Name)> plan)
    {
        if (view.Active) {
            return null;
        }
        var unsuccessful = view.Items.Where(item => item.State != PlanItemProgress.Succeeded)
            .ToLookup(item => (item.Kind, item.Name));
        for (var index = 0; index < plan.Count; index++) {
            if (unsuccessful[plan[index]].FirstOrDefault() is { } item) {
                return (index, item);
            }
        }
        return null;
    }

    internal static PlanItemProgress Map(PlanItemSnapshot item) => item.State switch {
        PlanItemState.Pending => PlanItemProgress.Pending,
        PlanItemState.Starting or PlanItemState.Running => PlanItemProgress.Running,
        PlanItemState.Succeeded => PlanItemProgress.Succeeded,
        PlanItemState.Failed => PlanItemProgress.Failed,
        _ => item.StartedAtUtc is null ? PlanItemProgress.NotRun : PlanItemProgress.Stopped
    };

    internal static string FormatDuration(TimeSpan duration)
    {
        var whole = TimeSpan.FromSeconds(Math.Max(0, Math.Floor(duration.TotalSeconds)));
        return whole.TotalHours >= 1 ? $"{(int)whole.TotalHours}:{whole:mm\\:ss}" : whole.ToString("m\\:ss");
    }

    // Elapsed time of a span that may still be open; an unstarted span has none.
    internal static TimeSpan? Elapsed(DateTime? startedAtUtc, DateTime? endedAtUtc, DateTime nowUtc) =>
        startedAtUtc is { } start ? (endedAtUtc ?? nowUtc) - start : null;

    internal static RunProgressView Create(RunSnapshot run, bool active)
    {
        var items = run.Items.Select(item => new PlanItemProgressView(
            item.Kind, item.TaskName, item.TaskLabel, Map(item), item.StartedAtUtc, item.EndedAtUtc)).ToArray();
        var current = active && run.CurrentPlanItemIndex is { } index && index >= 0 && index < items.Length
            ? (int?)index : null;
        return new RunProgressView(active, run.State, items, current, run.StartedAtUtc, run.EndedAtUtc);
    }
}
