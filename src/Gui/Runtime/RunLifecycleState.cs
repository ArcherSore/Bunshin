using System.Collections.Immutable;
using Gui.Worker;
using ProjectModel;
using Protocol;

namespace Gui.Runtime;

internal enum RunSource { Manual, Launch, Scheduled }
internal enum StartMode { PrepareOnly, PrepareAndStart, Start, Retry }
internal enum StartConclusion { Accepted, NotStarted, StartUnknown, EndUnconfirmed }
internal enum NotificationSlot { Open, Claimed, Delivered, Silent }
internal enum SendBoundary { BeforeBody, PossiblySent, Excluded, Accepted }
internal enum CleanupStage { None, Claimed, WaitingForReconnect, Ended, Superseded, Failed }
internal enum QuitStage { None, Bound, Suspended, Pending, AwaitingRecovery }
internal enum OperationStage { WaitingForPrevious, Preview, Reserve, Countdown, ReviewCountdown, Commit, Activity, EndOld, DrainOld, Prepare, Ready, Resolve, Sending }
internal enum ClosingKind { Exit, Update, EndEnvironment }
internal enum ClosingStage { Confirm, WaitingForOperations, End, Handoff, Committed }
internal enum StopResult { Requested, TimedOut, Rejected }
internal enum StopProgress { WaitingForGate, AwaitingReply, AwaitingConfirmation, Requested, Stopping }
internal enum CleanupResult { Ended, Disconnected, TargetChanged, Failed }
internal enum PresentationChannel { ForegroundError, Tray }
internal enum NoticeKind { NotStarted, StartUnknown, EndUnconfirmed, Faulted, Succeeded, Failed, CleanupFailed }

internal sealed record InputVersion(Guid ConfigurationId, long ProjectVersion, long EditVersion);
internal sealed record RunIntent(Guid Id, RunSource Source, StartMode Mode, string Entry, string OperationName,
    InputVersion Input, string ConfigurationName, bool Foreground = false, bool QuitAfterRun = false,
    RunStartAttempt? Attempt = null, RunKey? RetryOf = null, Guid? PreviousIntent = null, string? Opportunity = null);
internal sealed record EnvironmentTarget(uint SessionId, Guid? WorkerId);
internal sealed record Reservation(Guid Operation, EnvironmentTarget? Expected);
internal sealed record StartOperation(RunIntent Intent, OperationStage Stage, Reservation? Lease = null,
    ImmutableArray<RunIntent> Remaining = default, bool Force = false, DateTime LocalMinute = default, TimeSpan MinuteEnd = default,
    TimeSpan? CountdownEnd = null, bool Admitted = false, EnvironmentTarget? Prepared = null,
    RunKey? Replacement = null, bool Cancelled = false);
internal sealed record SendRecord(Submission Identity, RunIntent Intent, SendBoundary Boundary,
    WorkerRequestEvidence? Wire = null, StartReplyKind? Reply = null, string? ReplyCode = null,
    Exception? Error = null, Exception? ReplyError = null);
internal sealed record Confirmation(QueryId? InFlight = null, int Failures = 0, long Timer = 0, TimeSpan? Due = null);
internal sealed record TrackedRun(RunKey Key, string Digest, EnvironmentTarget Environment, RunIntent? Owner,
    ImmutableDictionary<Guid, SendRecord> Sends)
{
    internal StartConclusion? Conclusion { get; init; }
    internal bool Settled => Terminal != null || Conclusion is StartConclusion.NotStarted or StartConclusion.StartUnknown
        or StartConclusion.EndUnconfirmed;
    internal bool SeenActive { get; init; }
    internal bool SeenFaulted { get; init; }
    internal bool NeedQuery { get; init; }
    internal bool AwaitingReconnectQuery { get; init; }
    internal WorkerSnapshot? ReconnectEvidence { get; init; }
    // Running -> Faulted -> Last before the ordered query: replay the fault notice before the terminal consumers.
    internal bool ReconnectFaultPending { get; init; }
    internal Confirmation Confirmation { get; init; } = new();
    internal RunSnapshot? Terminal { get; init; }
    internal NotificationSlot Notification { get; init; }
    internal Guid? NotificationIntent { get; init; }
    internal CleanupStage Cleanup { get; init; }
    internal WorkerConnectionId? CleanupDisconnectedOn { get; init; }
    internal bool CompletionDelivered { get; init; }
    internal QuitStage Quit { get; init; }
    internal Guid? ManualStop { get; init; }
    internal StopProgress? StopProgress { get; init; }
    internal bool Retired { get; init; }
}
internal sealed record ClosingOperation(Guid Id, ClosingKind Kind, ClosingStage Stage, RunKey? QuitRun = null,
    RunKey? DeferredQuit = null);
internal sealed record StopOperation(Guid Id, RunKey Target, bool Sent = false);
internal sealed record Delivery(long Id, LifecycleEffect.Payload Payload, bool InFlight = false,
    WorkerConnectionId? DispatchedOn = null);

internal sealed record RunLifecycleState
{
    internal ImmutableDictionary<RunKey, TrackedRun> Runs { get; init; } = ImmutableDictionary<RunKey, TrackedRun>.Empty;
    internal ImmutableDictionary<Guid, StartOperation> Operations { get; init; } = ImmutableDictionary<Guid, StartOperation>.Empty;
    internal ImmutableList<Delivery> Deliveries { get; init; } = [];
    // Failures before a RunKey exists need only a lightweight presentation tombstone.
    internal ImmutableHashSet<Guid> FailedLocalIntents { get; init; } = [];
    internal WorkerCoordinatorSnapshot Current { get; init; } = WorkerCoordinatorSnapshot.Empty;
    internal WorkerConnectionId? Connection { get; init; }
    internal long LastGeneration { get; init; }
    internal long Sequence { get; init; }
    internal TimeSpan Now { get; init; }
    // Elapsed award time keeps 23:59 across midnight and subsequent local-clock rollback.
    internal ImmutableDictionary<DateTime, (string Name, TimeSpan AwardedAt)> MinuteWinners { get; init; }
        = ImmutableDictionary<DateTime, (string, TimeSpan)>.Empty;
    internal ClosingOperation? Closing { get; init; }
    internal StopOperation? Stop { get; init; }
    internal Exception? PreparationFailure { get; init; }
    internal bool QuitPending => Runs.Values.Any(r => r.Quit is QuitStage.Pending or QuitStage.AwaitingRecovery);
}

internal sealed record LifecycleTransition(RunLifecycleState State, ImmutableArray<LifecycleEffect> Effects);

