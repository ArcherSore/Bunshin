using Gui.Views;
using Gui.Worker;
using Protocol;
using static Gui.Runtime.LifecycleEffect;

namespace Gui.Runtime;

internal enum CleanupStage { None, Pending, WaitingForGate, Finished }
internal sealed record ScheduledSettlement(RunKey Key, string Name,
    CleanupStage Cleanup = CleanupStage.None, Exception? Failure = null)
{
    internal bool NeedsCleanup => Cleanup is CleanupStage.Pending or CleanupStage.WaitingForGate;
}

// Dispatcher-owned immutable view. An open presenter remains in flight until its receipt.
internal sealed record RunOrchestration(RunLifecycleState Lifecycle)
{
    internal ScheduledSettlement? Scheduled { get; init; }
    internal PendingOperation WindowOperation { get; init; }
    internal PendingOperation ScheduledOperation { get; init; }
    internal PendingOperation Operation => ScheduledOperation != PendingOperation.None ? ScheduledOperation
        : WindowOperation != PendingOperation.None ? WindowOperation
        : ManualErrorPending ? PendingOperation.StartingRun : PendingOperation.None;
    internal IEnumerable<Deliver> PendingConclusions => Lifecycle.Deliveries.Concat(Lifecycle.InFlight.Values)
        .Where(d => d.Item is Present or PresentCleanupFailure);
    internal bool ManualErrorPending => PendingConclusions.Any(Lifecycle.IsManualError);

    internal bool HasTrackedQuitRun => Lifecycle.Quits.ContainsValue(QuitStage.Bound) || PendingQuitReason != null;
    internal string? PendingQuitReason => Lifecycle.Deliveries.Any(d => d.Item is RequestQuit)
        || Lifecycle.Quits.Values.Any(stage => stage is QuitStage.Requested or QuitStage.AwaitingRecovery or QuitStage.RecoveredWhileClosing)
        ? "命令行任务已结束，正在等待退出" : null;

    internal RunKey? PendingStart(WorkerCoordinatorSnapshot view) =>
        Lifecycle.Runs.Values.FirstOrDefault(r => r.Owner != null && !r.Settled
            && (view.WorkerSnapshot == null || view.WorkerSnapshot.WorkerInstanceId == r.Key.WorkerInstanceId)
            && !(view.Fresh && Matches(view.WorkerSnapshot?.ActiveRun, r)))?.Key;

    private static bool Matches(RunSnapshot? snapshot, TrackedRun run) =>
        snapshot?.RunId == run.Key.RunId && snapshot.PlanDigest == run.Digest;
}
