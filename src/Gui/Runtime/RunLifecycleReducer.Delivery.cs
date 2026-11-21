using Gui.Worker;
using Protocol;
using static Gui.Runtime.LifecycleEffect;

namespace Gui.Runtime;

internal sealed partial class RunLifecycleReducer
{
    private void Queue(Payload item)
    {
        var delivery = new Deliver(Next(), item);
        state = state with { Deliveries = state.Deliveries.Add(delivery) };
    }

    private void SetQuit(RunKey key, QuitStage stage) => state = state with { Quits = state.Quits.SetItem(key, stage) };

    private void QueueQuit(RunKey key)
    {
        state = state with { Quits = state.Quits.Remove(key) };
        Queue(new RequestQuit(key));
    }

    private void Settle(TrackedRun r, StartConclusion conclusion, Exception? error, string? confirmedStartDetail = null)
    {
        CancelConfirmation(r);
        r = Conclude(r, conclusion, error);
        // A new retry can still receive a foreground error after the Run's proactive slot was used.
        if (r.Notification == NotificationSlot.Open
            || r.Owner is { Source: RunSource.Manual } retry && retry.RetryOf == r.Key && r.NotificationIntent != retry.Id) {
            var kind = conclusion switch {
                StartConclusion.StartUnknown => NoticeKind.StartUnknown,
                StartConclusion.EndUnconfirmed => NoticeKind.EndUnconfirmed,
                _ => NoticeKind.NotStarted
            };
            Queue(new Present(r.Key, r.Owner, kind, r.Owner?.OperationName ?? "任务运行", error)
                { ConfirmedStartDetail = confirmedStartDetail });
            if (r.Notification == NotificationSlot.Open)
                r = r with { Notification = NotificationSlot.Claimed, NotificationIntent = r.Owner?.Id };
        }
        state = state with { Quits = state.Quits.Remove(r.Key) };
        Save(r with { Confirmation = new(), NeedQuery = false, AwaitingReconnectQuery = false, ReconnectEvidence = null,
            ReconnectFaultPending = false });
    }

    private void Terminal(TrackedRun r, RunSnapshot terminal)
    {
        CancelConfirmation(r);
        r = r with { Terminal = terminal, TerminalState = terminal.State, Confirmation = new(), NeedQuery = false };
        if (r.Notification == NotificationSlot.Open) {
            if (terminal.State == RunState.Cancelled) {
                r = r with { Notification = NotificationSlot.Silent };
            } else {
                Queue(TerminalNotice(r, terminal));
                r = r with { Notification = NotificationSlot.Claimed, NotificationIntent = r.Owner?.Id };
            }
        }
        if (r.Owner is { Source: RunSource.Scheduled } scheduled) {
            Queue(new CompleteScheduled(r.Key, scheduled, terminal));
            if (terminal.State == RunState.Succeeded) Queue(new RequestCleanup(r.Key));
        }
        if (state.Quits.TryGetValue(r.Key, out var quit) && quit == QuitStage.Bound) QueueQuit(r.Key);
        else state = state with { Quits = state.Quits.Remove(r.Key) };
        Save(r);
    }

    private static Present TerminalNotice(TrackedRun r, RunSnapshot terminal)
    {
        var name = r.Owner?.ConfigurationName ?? r.TakeoverConfigurationName;
        var subject = string.IsNullOrWhiteSpace(name) ? "任务" : $"「{name}」";
        var succeeded = terminal.State == RunState.Succeeded;
        return new(r.Key, r.Owner, succeeded ? NoticeKind.Succeeded : NoticeKind.Failed,
            subject + (succeeded ? "已完成" : "运行失败"), Terminal: terminal);
    }

    private void DispatchDelivery(bool deferManualErrors)
    {
        if (state.Closing != null || state.InFlightDelivery != null) return;
        while (!state.Deliveries.IsEmpty) {
            // A synchronous quit callback may start Closing. Finish the other consumers first.
            var d = state.Deliveries.FirstOrDefault(d => d.Item is not RequestQuit)
                ?? state.Deliveries.FirstOrDefault(d => d.Item.Key != state.DeferredQuit);
            if (d == null) return;
            var queued = d;
            if (d.Item is Present { Kind: NoticeKind.Faulted } fault && state.Runs[fault.Key] is { Retired: true } r) {
                if (FinalNoticeAfterFault(r, fault) is not { } final) {
                    state = state with { Deliveries = state.Deliveries.Remove(queued) };
                    continue;
                }
                d = d with { Item = final };
            }
            if (deferManualErrors && d.Item is Present { Intent.Source: RunSource.Manual,
                Kind: NoticeKind.NotStarted or NoticeKind.StartUnknown or NoticeKind.EndUnconfirmed }) return;
            state = state with { Deliveries = state.Deliveries.Remove(queued) };
            if (d.Item is Present { Intent: { Source: RunSource.Manual, RetryOf: not null } retry } notice
                && state.Runs[notice.Key] is { } run && run.NotificationIntent != retry.Id) {
                if (run.Notification == NotificationSlot.Open)
                    Save(run with { Notification = NotificationSlot.Claimed, NotificationIntent = retry.Id });
                else
                    d = d with { Item = notice with { ForegroundOnly = true } };
            }
            state = state with { InFlightDelivery = d };
            if (d.Item is RequestQuit) SetQuit(d.Item.Key, QuitStage.Requested);
            effects.Add(d);
            return;
        }
    }

    private void Acknowledge(LifecycleEvent.Delivered receipt)
    {
        if (state.InFlightDelivery is not { } d || d.Id != receipt.Id) return;
        state = state with { InFlightDelivery = null };
        if (d.Item is not Present notice || state.Runs[notice.Key] is not { Notification: NotificationSlot.Claimed } r
            || r.NotificationIntent != notice.Intent?.Id) return;

        if (!receipt.Presented && notice.Kind == NoticeKind.Faulted && r.Settled) {
            // Settlement raced the receipt. Dispatch the final outcome which the reserved slot held back.
            if (FinalNoticeAfterFault(r, notice) is { } final) Queue(final);
        } else {
            Save(r with { Notification = receipt.Presented ? NotificationSlot.Delivered : NotificationSlot.Open });
        }
    }

    private Present? FinalNoticeAfterFault(TrackedRun run, Present fault)
    {
        if (run.Terminal is not { } terminal)
            return fault with { Kind = NoticeKind.EndUnconfirmed, Operation = run.Owner?.OperationName ?? "任务运行",
                Error = LastError(run) };
        if (terminal.State != RunState.Cancelled) return TerminalNotice(run, terminal);
        Save(run with { Notification = NotificationSlot.Silent });
        return null;
    }
}
