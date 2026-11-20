using System.Collections.Immutable;
using Gui.Worker;
using ProjectModel;
using Protocol;

namespace Gui.Runtime;

// The host stamps each event with monotonic elapsed time. Operation IDs identify one invocation,
// including its reservation, so a late receipt can never advance a different click or opportunity.
internal abstract record LifecycleEvent
{
    internal TimeSpan At { get; init; }
    internal sealed record Start(RunIntent Intent, Exception? ValidationError = null) : LifecycleEvent;
    // ScheduleClock has claimed these candidates in configuration order.
    internal sealed record ScheduleClaimed(ImmutableArray<RunIntent> Candidates, DateTime LocalMinute, TimeSpan MinuteEnd,
        bool Force = false) : LifecycleEvent;
    internal sealed record Previewed(Guid Id, RunIntent? Candidate, Exception? Error = null, bool BatchFailure = false) : LifecycleEvent;
    // A successful receipt includes the input revalidated under the lease (also after a countdown).
    internal sealed record Reserved(Guid Id, Reservation? Lease, RunIntent? Validated = null, string? BusyReason = null) : LifecycleEvent;
    internal sealed record CommittedConfiguration(Guid Id, Exception? Error = null) : LifecycleEvent;
    internal sealed record ActivityAppended(Guid Id) : LifecycleEvent;
    internal sealed record OldEnvironmentEnded(Guid Id, bool Ended, Exception? Error = null) : LifecycleEvent;
    internal sealed record Prepared(Guid Id, EnvironmentTarget? Target, Exception? Error = null) : LifecycleEvent;
    internal sealed record ReadyChecked(Guid Id, EnvironmentTarget? Target, InputVersion Input, Exception? Error = null) : LifecycleEvent;
    internal sealed record PlanResolved(Guid Id, RunStartAttempt? Attempt, Exception? Error = null) : LifecycleEvent;
    internal sealed record CountdownDue(Guid Id) : LifecycleEvent;
    internal sealed record CancelCountdown(Guid Id) : LifecycleEvent;
    internal sealed record Observed(WorkerObservationBatch Batch) : LifecycleEvent;
    internal sealed record RetryDue(RunKey Key, long Token) : LifecycleEvent;
    internal sealed record Stop(Guid Id, RunKey Target) : LifecycleEvent;
    internal sealed record StopGateAcquired(Guid Id) : LifecycleEvent;
    internal sealed record StopReturned(Guid Id, StopResult Result) : LifecycleEvent;
    internal sealed record BeginClosing(Guid Id, ClosingKind Kind, RunKey? QuitRun = null) : LifecycleEvent;
    internal sealed record ClosingConfirmed(Guid Id, bool Proceed) : LifecycleEvent;
    internal sealed record ClosingEndReturned(Guid Id, bool Ended, EnvironmentTarget? EndedTarget = null,
        Exception? Error = null, EnvironmentTarget? RecoveryTarget = null) : LifecycleEvent;
    internal sealed record HandoffReturned(Guid Id, bool TakenOver) : LifecycleEvent;
    internal sealed record EnvironmentRecovered : LifecycleEvent;
    internal sealed record Delivered(long Id) : LifecycleEvent;
    internal sealed record CleanupReturned(long Id, CleanupResult Result, Exception? Error = null) : LifecycleEvent;
}

internal abstract record LifecycleEffect
{
    internal sealed record Preview(RunIntent Intent) : LifecycleEffect;
    internal sealed record Reserve(RunIntent Intent, bool Wait, bool AllowReplacement) : LifecycleEffect;
    internal sealed record Release(Reservation Lease) : LifecycleEffect;
    internal sealed record CommitConfiguration(RunIntent Intent, Reservation Lease) : LifecycleEffect;
    internal sealed record PublishConfiguration(RunIntent Intent) : LifecycleEffect;
    internal sealed record AppendStart(RunIntent Intent) : LifecycleEffect;
    internal sealed record EndReserved(Reservation Lease, RunKey? Replace) : LifecycleEffect;
    internal sealed record PrepareEnvironment(RunIntent Intent, bool ShowDesktop) : LifecycleEffect;
    internal sealed record CheckReady(RunIntent Intent, EnvironmentTarget? Expected, TimeSpan Timeout) : LifecycleEffect;
    internal sealed record ResolvePlan(RunIntent Intent) : LifecycleEffect;
    internal sealed record ShowCountdown(RunIntent Intent, TimeSpan Deadline) : LifecycleEffect;
    internal sealed record HideCountdown(Guid Id) : LifecycleEffect;
    internal sealed record Skip(RunIntent Intent, string Reason) : LifecycleEffect;
    internal sealed record ScheduleActivity(RunIntent Intent, string Message) : LifecycleEffect;
    internal sealed record OperationFinished(RunIntent Intent, bool Admitted, RunKey? Run, Exception? Error) : LifecycleEffect;
    // Cancel outstanding waits, including a racing acquisition. The executor returns any lease
    // acquired by this operation after cancellation, once its physical operation has returned.
    internal sealed record CancelOperation(Guid Id, Reservation? Lease) : LifecycleEffect;
    internal sealed record RegisterPlan(Submission Submission, RunStartAttempt Attempt) : LifecycleEffect;
    internal sealed record SendStart(Submission Submission, RunStartAttempt Attempt) : LifecycleEffect;
    internal sealed record QueryRun(RunKey Key, QueryId Id, WorkerConnectionId Connection) : LifecycleEffect;
    internal sealed record CancelQuery(RunKey Key, QueryId Id) : LifecycleEffect;
    internal sealed record ArmRetry(RunKey Key, long Token, TimeSpan Due) : LifecycleEffect;
    internal sealed record CancelRetry(RunKey Key, long Token) : LifecycleEffect;
    internal sealed record AcquireStopGate(Guid Id, RunKey Expected) : LifecycleEffect;
    internal sealed record SendStop(Guid Id, RunKey Expected) : LifecycleEffect;
    internal sealed record ReleaseStopGate(Guid Id) : LifecycleEffect;
    internal sealed record SetClosing(bool Value) : LifecycleEffect;
    internal sealed record ConfirmClosing(ClosingOperation Operation) : LifecycleEffect;
    internal sealed record EndForClosing(ClosingOperation Operation) : LifecycleEffect;
    internal sealed record HandoffUpdate(Guid Id) : LifecycleEffect;
    internal sealed record CommitExit(Guid Id) : LifecycleEffect;

    // A delivery is data with a receipt, including its original presentation context. No callbacks.
    internal abstract record Payload(RunKey? Key);
    internal sealed record Present(RunKey? Run, RunIntent? Intent, NoticeKind Kind, PresentationChannel Channel,
        string Operation, Exception? Error = null, RunSnapshot? Terminal = null) : Payload(Run)
    {
        internal string Detail => Kind switch {
            NoticeKind.StartUnknown => "启动结果未知：任务可能已经开始执行。",
            NoticeKind.EndUnconfirmed => "任务已被接纳，运行结果未确认。",
            NoticeKind.Faulted => $"任务可能仍在运行。打开 {AppIdentity.Name} 结束桌面分身以恢复。",
            NoticeKind.Succeeded => "所有任务已执行完成。",
            NoticeKind.Failed => FailedItemLabel() is { Length: > 0 } label
                ? $"失败任务：{label}。打开 {AppIdentity.Name} 查看运行动态。"
                : $"任务运行失败。打开 {AppIdentity.Name} 查看运行动态。",
            _ => Error?.Message ?? Operation
        };

        private string? FailedItemLabel()
        {
            var id = Terminal?.Items.FirstOrDefault(i => i.State == PlanItemState.Failed)?.PlanItemId;
            return Terminal?.Plan.Items.FirstOrDefault(i => i.PlanItemId == id)?.TaskLabel;
        }
    }
    internal sealed record CompleteScheduled(RunKey Run, RunIntent Intent, RunSnapshot Terminal) : Payload(Run);
    // The executor checks session, Worker, fresh idle, successful Last and unresolved sends inside the gate.
    internal sealed record Cleanup(RunKey Run, EnvironmentTarget Expected) : Payload(Run);
    internal sealed record RequestQuit(RunKey Run) : Payload(Run);
    internal sealed record Deliver(long Id, Payload Item) : LifecycleEffect;
}

