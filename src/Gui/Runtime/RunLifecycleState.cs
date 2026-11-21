using System.Collections.Immutable;
using Gui.Worker;
using ProjectModel;
using Protocol;

namespace Gui.Runtime;

internal enum RunSource { Manual, Launch, Scheduled }
internal enum StartConclusion { Accepted, NotStarted, StartUnknown, EndUnconfirmed }
internal enum NotificationSlot { Open, Claimed, Delivered, Silent }
internal enum QuitStage { AwaitingBody, Bound, Requested, AwaitingRecovery }
internal enum SendBoundary { BeforeBody, PossiblySent, Excluded, Accepted }
internal enum PresentationChannel { ForegroundError, Tray }
internal enum NoticeKind { NotStarted, StartUnknown, EndUnconfirmed, Faulted, Succeeded, Failed }

internal sealed record InputVersion(Guid ConfigurationId, long ProjectVersion, long EditVersion);
internal sealed record RunIntent(Guid Id, RunSource Source, string OperationName, InputVersion Input,
    string ConfigurationName, bool Foreground = false, bool QuitAfterRun = false, RunKey? RetryOf = null);
internal sealed record SendRecord(Submission Identity, SendBoundary Boundary = SendBoundary.BeforeBody,
    WorkerRequestEvidence? Wire = null, string? RejectionCode = null, Exception? Error = null);
internal sealed record Confirmation(QueryId? InFlight = null, int Failures = 0, long Timer = 0, TimeSpan? Due = null);
internal sealed record TrackedRun(RunKey Key, string Digest)
{
    internal RunIntent? Owner { get; init; }
    internal RunStartAttempt? Attempt { get; init; }
    internal Submission? Submission { get; init; }
    internal ImmutableDictionary<Guid, SendRecord> Sends { get; init; } = ImmutableDictionary<Guid, SendRecord>.Empty;
    internal ImmutableHashSet<Submission> PriorSubmissions { get; init; } = [];
    internal string? TakeoverConfigurationName { get; init; }
    internal StartConclusion? Conclusion { get; init; }
    internal bool Settled => TerminalState != null || Conclusion is StartConclusion.NotStarted or StartConclusion.StartUnknown
        or StartConclusion.EndUnconfirmed;
    internal bool SeenActive { get; init; }
    internal bool SeenFaulted { get; init; }
    internal bool NeedQuery { get; init; }
    internal bool AwaitingReconnectQuery { get; init; }
    internal WorkerSnapshot? ReconnectEvidence { get; init; }
    internal bool ReconnectFaultPending { get; init; }
    internal Confirmation Confirmation { get; init; } = new();
    internal RunSnapshot? Terminal { get; init; }
    internal RunState? TerminalState { get; init; }
    internal NotificationSlot Notification { get; init; }
    internal Guid? NotificationIntent { get; init; }
    internal bool Retired { get; init; }
}

internal sealed record RunLifecycleState
{
    internal ImmutableDictionary<RunKey, TrackedRun> Runs { get; init; } = ImmutableDictionary<RunKey, TrackedRun>.Empty;
    internal ImmutableList<LifecycleEffect.Deliver> Deliveries { get; init; } = [];
    internal LifecycleEffect.Deliver? InFlightDelivery { get; init; }
    // A quit request can outlive its Worker's retirement and its delivery receipt.
    internal ImmutableDictionary<RunKey, QuitStage> Quits { get; init; } = ImmutableDictionary<RunKey, QuitStage>.Empty;
    internal WorkerCoordinatorSnapshot Current { get; init; } = WorkerCoordinatorSnapshot.Empty;
    internal WorkerConnectionId? Connection { get; init; }
    internal long LastGeneration { get; init; }
    internal long Sequence { get; init; }
    internal TimeSpan Now { get; init; }
    internal Guid? Closing { get; init; }
    internal bool ExitCommitted { get; init; }

    // The caller chooses the entry point and creates a fresh submission ID. Reuse the whole
    // frozen attempt (including its plan item IDs and redactions) only after exclusion.
    internal RunStartAttempt? ReusableAttempt(RunIntent intent, Guid workerId)
    {
        if (intent is not { Source: RunSource.Manual, RetryOf: { } key } || key.WorkerInstanceId != workerId
            || !Runs.TryGetValue(key, out var run) || run is not { Retired: false, Conclusion: StartConclusion.NotStarted,
                Owner: { } owner, Submission: { } last } || owner.Input != intent.Input || owner.Id == intent.Id)
            return null;
        if (run.Sends.Values.Any(s => s.RejectionCode == "run_id_conflict")) return null;
        return run.Sends[last.SubmissionId].RejectionCode switch {
            null or "worker_busy" or "worker_not_ready" or "worker_faulted" or "operation_not_allowed"
                or "protocol_version_mismatch" => run.Attempt,
            _ => null
        };
    }
}

internal sealed record LifecycleTransition(RunLifecycleState State, ImmutableArray<LifecycleEffect> Effects);
