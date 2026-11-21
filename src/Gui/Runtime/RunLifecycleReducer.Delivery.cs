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

    private void Settle(TrackedRun r, StartConclusion conclusion, Exception? error)
    {
        CancelConfirmation(r);
        r = Conclude(r, conclusion, error);
        var foreground = r.Owner is { Source: RunSource.Manual, Foreground: true };
        // Each retrying click needs its own error, even if the Run's proactive slot was used.
        if (r.Notification == NotificationSlot.Open || foreground && r.Owner?.RetryOf == r.Key && r.NotificationIntent != r.Owner.Id) {
            var kind = conclusion switch {
                StartConclusion.StartUnknown => NoticeKind.StartUnknown,
                StartConclusion.EndUnconfirmed => NoticeKind.EndUnconfirmed,
                _ => NoticeKind.NotStarted
            };
            Queue(new Present(r.Key, r.Owner, kind, foreground ? PresentationChannel.ForegroundError : PresentationChannel.Tray,
                r.Owner?.OperationName ?? "任务运行", error));
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
            PresentationChannel.Tray, subject + (succeeded ? "已完成" : "运行失败"), Terminal: terminal);
    }

    private void DispatchDelivery()
    {
        if (state.Closing != null || state.InFlightDelivery != null) return;
        while (!state.Deliveries.IsEmpty) {
            // A synchronous quit callback may start Closing. Finish the other consumers first.
            var d = state.Deliveries.FirstOrDefault(d => d.Item is not RequestQuit)
                ?? state.Deliveries.FirstOrDefault(d => d.Item.Key != state.DeferredQuit);
            if (d == null) return;
            state = state with { Deliveries = state.Deliveries.Remove(d) };
            if (d.Item is Present { Kind: NoticeKind.Faulted } fault && state.Runs[fault.Key] is { Retired: true } r) {
                if (r.Terminal?.State == RunState.Cancelled) {
                    Save(r with { Notification = NotificationSlot.Silent });
                    continue;
                }
                d = d with { Item = r.Terminal is { } terminal ? TerminalNotice(r, terminal)
                    : fault with { Kind = NoticeKind.EndUnconfirmed, Operation = r.Owner?.OperationName ?? "任务运行",
                        Error = LastError(r), Channel = r.Owner is { Source: RunSource.Manual, Foreground: true }
                            ? PresentationChannel.ForegroundError : PresentationChannel.Tray } };
            }
            state = state with { InFlightDelivery = d };
            if (d.Item is RequestQuit) SetQuit(d.Item.Key, QuitStage.Requested);
            effects.Add(d);
            return;
        }
    }

    private void Acknowledge(long id)
    {
        if (state.InFlightDelivery is not { } d || d.Id != id) return;
        state = state with { InFlightDelivery = null };
        if (d.Item is Present && state.Runs[d.Item.Key] is { Notification: NotificationSlot.Claimed } r)
            Save(r with { Notification = NotificationSlot.Delivered });
    }
}
