using Protocol;

namespace Gui.Views;

// What one Plan Item shows. A cancelled item that started was stopped; one that never started did not run.
internal enum PlanItemProgress
{
    Pending, Running, Succeeded, Failed, Stopped, NotRun
}

internal sealed record PlanItemProgressView(
    Guid PlanItemId, string TaskLabel, PlanItemProgress State, DateTime? StartedAtUtc, DateTime? EndedAtUtc);

// The progress of the active Run, or of the last Run while it still describes the plan on screen. PlanOffset is where
// the last Run's items begin in that plan.
internal sealed record RunProgressView(
    bool Active, RunState State, IReadOnlyList<PlanItemProgressView> Items, int? CurrentIndex, DateTime? StartedAtUtc,
    DateTime? EndedAtUtc, int PlanOffset = 0)
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
    internal static RunProgressView? Derive(
        WorkerSnapshot? worker, IReadOnlyList<(PlanItemKind Kind, string Name)> activeItems)
    {
        if (worker?.ActiveRun is { } active) {
            return Create(active, true, 0);
        }
        // A finished record stays while it is the end of the plan, which is what a continued Run covers.
        if (worker?.LastRun is not { } last) {
            return null;
        }
        var offset = activeItems.Count - last.Plan.Items.Count;
        return offset >= 0 && last.Plan.Items.Select(item => (item.Kind, item.TaskName))
            .SequenceEqual(activeItems.Skip(offset)) ? Create(last, false, offset) : null;
    }

    // Where 继续运行 starts in the current plan: the record's first unsuccessful item. At the plan's first item it
    // would be the same as 运行, so there is none.
    internal static int? ContinueFrom(RunProgressView view)
    {
        if (view.Active) {
            return null;
        }
        var index = view.Items.ToList().FindIndex(item => item.State != PlanItemProgress.Succeeded);
        return index >= 0 && view.PlanOffset + index > 0 ? view.PlanOffset + index : null;
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

    private static RunProgressView Create(RunSnapshot run, bool active, int planOffset)
    {
        var items = run.Items.Select(item => new PlanItemProgressView(
            item.PlanItemId, item.TaskLabel, Map(item), item.StartedAtUtc, item.EndedAtUtc)).ToArray();
        var current = active && run.CurrentPlanItemIndex is { } index && index >= 0 && index < items.Length
            ? (int?)index : null;
        return new RunProgressView(active, run.State, items, current, run.StartedAtUtc, run.EndedAtUtc, planOffset);
    }
}
