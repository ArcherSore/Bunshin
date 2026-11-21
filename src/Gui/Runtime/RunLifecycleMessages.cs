using Gui.Worker;
using ProjectModel;
using Protocol;

namespace Gui.Runtime;

internal abstract record LifecycleEvent
{
    internal TimeSpan At { get; init; }
    internal sealed record SubmissionStarted(RunIntent Intent, RunKey Key, Submission Submission, RunStartAttempt Attempt) : LifecycleEvent;
    internal sealed record Observed(WorkerObservationBatch Batch, string? ConfigurationName = null) : LifecycleEvent;
    internal sealed record RetryDue(RunKey Key, long Token) : LifecycleEvent;
    internal sealed record Delivered(long Id) : LifecycleEvent;
    internal sealed record ClosingStarted(Guid Id) : LifecycleEvent;
    internal sealed record ClosingCancelled(Guid Id) : LifecycleEvent;
    internal sealed record ClosingCommitted(Guid Id) : LifecycleEvent;
}

internal abstract record LifecycleEffect
{
    // Evidence can establish acceptance first, then an unconfirmed end if the Worker exits.
    internal sealed record SubmissionConclusion(Submission Submission, StartConclusion Conclusion, Exception? Error = null) : LifecycleEffect;
    internal sealed record QueryRun(RunKey Key, QueryId Id, WorkerConnectionId Connection) : LifecycleEffect;
    internal sealed record CancelQuery(RunKey Key, QueryId Id) : LifecycleEffect;
    internal sealed record ArmRetry(RunKey Key, long Token, TimeSpan Due) : LifecycleEffect;
    internal sealed record CancelRetry(RunKey Key, long Token) : LifecycleEffect;

    internal abstract record Payload(RunKey Key);
    internal sealed record Present(RunKey Run, RunIntent? Intent, NoticeKind Kind, PresentationChannel Channel,
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
    internal sealed record RequestCleanup(RunKey Run) : Payload(Run);
    internal sealed record RequestQuit(RunKey Run) : Payload(Run);
    internal sealed record Deliver(long Id, Payload Item) : LifecycleEffect;
}
