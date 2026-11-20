using Gui.Worker;
using Protocol;
using static Gui.Runtime.LifecycleEvent;
using static Gui.Runtime.LifecycleEffect;

namespace Gui.Runtime;

internal sealed partial class RunLifecycleReducer
{
    private void Queue(Payload item)
    {
        var delivery = new Delivery(Next(), item);
        state = state with { Deliveries = state.Deliveries.Add(delivery) };
    }

    private void PresentFailure(RunKey? key, RunIntent? intent, NoticeKind kind, Exception? error, string? operation = null)
    {
        var channel = intent is { Source: RunSource.Manual, Foreground: true }
            ? PresentationChannel.ForegroundError : PresentationChannel.Tray;
        Queue(new Present(key, intent, kind, channel, operation ?? intent?.OperationName ?? "任务运行", error));
    }

    private void Settle(TrackedRun r, StartConclusion conclusion, Exception? error)
    {
        CancelTimer(r);
        if (r.Confirmation.InFlight is { } query) effects.Add(new CancelQuery(r.Key, query));
        EndSending(r, error);
        // A retry gets its own foreground operation result even when this Run's proactive slot was used.
        var foreground = r.Owner is { Source: RunSource.Manual, Foreground: true };
        if (r.Notification == NotificationSlot.Open || foreground && r.Owner?.Mode == StartMode.Retry
            && r.NotificationIntent != r.Owner.Id) {
            var kind = conclusion switch {
                StartConclusion.StartUnknown => NoticeKind.StartUnknown,
                StartConclusion.EndUnconfirmed => NoticeKind.EndUnconfirmed,
                _ => NoticeKind.NotStarted
            };
            PresentFailure(r.Key, r.Owner, kind, error);
            if (r.Notification == NotificationSlot.Open)
                r = r with { Notification = NotificationSlot.Claimed, NotificationIntent = r.Owner?.Id };
        }
        Save(r with { Conclusion = conclusion, Confirmation = new(), NeedQuery = false,
            AwaitingReconnectQuery = false, ReconnectEvidence = null,
            Quit = r.Quit == QuitStage.Suspended ? QuitStage.Suspended : QuitStage.None });
    }

    private void Terminal(TrackedRun r, RunSnapshot terminal)
    {
        if (r.Confirmation.InFlight is { } query) effects.Add(new CancelQuery(r.Key, query));
        EndSending(r);
        r = r with { Terminal = terminal, Confirmation = new(), NeedQuery = false };
        if (r.Notification == NotificationSlot.Open) {
            if (terminal.State == RunState.Cancelled) {
                r = r with { Notification = NotificationSlot.Silent };
            } else {
                var kind = terminal.State == RunState.Succeeded ? NoticeKind.Succeeded : NoticeKind.Failed;
                Queue(new Present(r.Key, r.Owner, kind, PresentationChannel.Tray, "任务运行", Terminal: terminal));
                r = r with { Notification = NotificationSlot.Claimed, NotificationIntent = r.Owner?.Id };
            }
        }
        if (r.Owner is { Source: RunSource.Scheduled } scheduled) {
            Queue(new CompleteScheduled(r.Key, scheduled, terminal));
            if (terminal.State == RunState.Succeeded && r.ManualStop == null) {
                r = r with { Cleanup = CleanupStage.Claimed };
                Queue(new Cleanup(r.Key, r.Environment));
            }
        }
        if (r.Quit == QuitStage.Bound) {
            r = r with { Quit = QuitStage.Pending };
            Queue(new RequestQuit(r.Key));
        }
        Save(r);
    }

    private void Dispatch()
    {
        if (state.Closing?.Stage == ClosingStage.Committed) return;
        if (state.Closing is { Stage: ClosingStage.WaitingForOperations } closing && state.Operations.IsEmpty) {
            state = state with { Closing = closing with { Stage = ClosingStage.End } };
            effects.Add(new EndForClosing(state.Closing));
        }
        ReviewCountdowns();
        DispatchQueries();
        RecheckCleanups();
        if (state.Closing == null) {
            DispatchDelivery();
            DrainReplacements();
        }
        foreach (var r in state.Runs.Values.Where(r => r.Retired && r.Quit == QuitStage.None
            && r.Cleanup is not (CleanupStage.Claimed or CleanupStage.WaitingForReconnect)
            && !state.Deliveries.Any(d => d.Payload.Key == r.Key)
            && !state.Operations.Values.Any(o => o.Replacement == r.Key))) {
            state = state with { Runs = state.Runs.Remove(r.Key) };
        }
    }

    private void DispatchDelivery()
    {
        if (state.Deliveries.Any(d => d.InFlight)) return;
        // One receipt releases the next consumer. In particular a synchronous quit callback can
        // enter Closing only after every preceding presentation and completion was acknowledged.
        while (state.Deliveries.Count != 0) {
            var d = state.Deliveries.FirstOrDefault(d => d.Payload is not RequestQuit
                && !(d.Payload is Cleanup claimed && HeldForChangeover(claimed)));
            if (d == null) {
                if (state.Deliveries.Any(d => d.Payload is not RequestQuit)) return;
                d = state.Deliveries[0];
            }
            if (d.Payload is Cleanup cleanup && !CanDispatchCleanup(cleanup)) {
                state = state with { Deliveries = state.Deliveries.Remove(d) };
                continue;
            }
            state = state with { Deliveries = state.Deliveries.Replace(d, d with { InFlight = true, DispatchedOn = state.Connection }) };
            effects.Add(new Deliver(d.Id, d.Payload));
            return;
        }
    }

    // A changeover already owns this lease. A second physical end would make the
    // replacement wait for a consumer waiting on the replacement's own gate.
    private bool HeldForChangeover(Cleanup c) => state.Runs[c.Run].Cleanup == CleanupStage.Claimed
        && state.Operations.Values.Any(op => op.Intent.Source == RunSource.Scheduled && op.Lease?.Expected == c.Expected
            && op.Stage is OperationStage.Commit or OperationStage.Activity or OperationStage.EndOld);

    private void MarkTargetCleanup(EnvironmentTarget target, CleanupStage disposition)
    {
        foreach (var r in state.Runs.Values.Where(r => r.Environment == target
            && r.Cleanup is CleanupStage.Claimed or CleanupStage.WaitingForReconnect)) {
            Save(r with { Cleanup = disposition });
        }
    }

    private bool CanDispatchCleanup(Cleanup c)
    {
        var r = state.Runs[c.Run];
        if (r.Cleanup != CleanupStage.Claimed) return false;
        var view = state.Current;
        var live = view.WorkerSnapshot;
        if (r.Retired || state.Operations.Values.Any(op => op.Stage == OperationStage.DrainOld && op.Lease?.Expected == c.Expected)
            || live != null && (live.WorkerInstanceId != c.Run.WorkerInstanceId
            || live.ChildSessionId != c.Expected.SessionId) || view.Fresh && (live?.ActiveRun != null
            || live?.LastRun?.RunId != c.Run.RunId || live.LastRun.PlanDigest != r.Digest || live.LastRun.State != RunState.Succeeded)) {
            Save(r with { Cleanup = CleanupStage.Superseded });
            return false;
        }
        if (!view.FreshIdleForEnvironmentEnd || state.Runs.Values.Any(run => !run.Settled)) {
            Save(r with { Cleanup = CleanupStage.WaitingForReconnect });
            return false;
        }
        return true;
    }

    private void RecheckCleanups()
    {
        foreach (var r in state.Runs.Values.Where(r => r.Cleanup == CleanupStage.WaitingForReconnect)) {
            if (!r.Retired && (!state.Current.Fresh || state.Connection == r.CleanupDisconnectedOn
                || state.Runs.Values.Any(other => !other.Settled && other.Key.WorkerInstanceId == r.Key.WorkerInstanceId))) continue;
            Save(r with { Cleanup = CleanupStage.Claimed, CleanupDisconnectedOn = null });
            Queue(new Cleanup(r.Key, r.Environment));
        }
    }

    private void Acknowledge(long id)
    {
        if (state.Deliveries.Find(d => d.Id == id && d.InFlight) is not { } d || d.Payload is Cleanup) return;
        state = state with { Deliveries = state.Deliveries.Remove(d) };
        if (d.Payload.Key is { } key && state.Runs.TryGetValue(key, out var r)) {
            if (d.Payload is Present && r.Notification == NotificationSlot.Claimed)
                Save(r with { Notification = NotificationSlot.Delivered });
            else if (d.Payload is CompleteScheduled) Save(r with { CompletionDelivered = true });
        }
    }

    private void CleanupReceipt(CleanupReturned e)
    {
        if (state.Deliveries.Find(d => d.Id == e.Id && d.InFlight) is not { Payload: Cleanup c } d) return;
        state = state with { Deliveries = state.Deliveries.Remove(d) };
        var r = state.Runs[c.Run];
        if (r.Cleanup != CleanupStage.Claimed) return;
        var stage = e.Result switch {
            CleanupResult.Ended => CleanupStage.Ended,
            CleanupResult.Disconnected when !r.Retired => CleanupStage.WaitingForReconnect,
            CleanupResult.Failed => CleanupStage.Failed,
            _ => CleanupStage.Superseded
        };
        Save(r with { Cleanup = stage, CleanupDisconnectedOn = stage == CleanupStage.WaitingForReconnect ? d.DispatchedOn : null });
        if (stage == CleanupStage.Ended) effects.Add(new ScheduleActivity(r.Owner!, "全部完成，已关闭桌面分身"));
        if (stage == CleanupStage.Failed) PresentFailure(r.Key, r.Owner, NoticeKind.CleanupFailed, e.Error, "定时任务收尾失败");
    }

    private void BeginStop(Stop e)
    {
        if (state.Closing != null || state.Stop != null || !state.Runs.TryGetValue(e.Target, out var r)
            || r.Settled || r.ManualStop != null) return;
        Save(r with { ManualStop = e.Id, StopProgress = StopProgress.WaitingForGate });
        state = state with { Stop = new(e.Id, e.Target) };
        effects.Add(new AcquireStopGate(e.Id, e.Target));
    }

    private void StopAcquired(Guid id)
    {
        if (state.Stop is not { Sent: false } stop || stop.Id != id) return;
        if (state.Closing != null || state.Current is not { Fresh: true, WorkerSnapshot: { ActiveRun: { } active } worker }
            || stop.Target != new RunKey(worker.WorkerInstanceId, active.RunId) || !Views.RuntimeControls.IsStoppable(active)) {
            FinishStop(new(id, StopResult.Rejected));
            return;
        }
        state = state with { Stop = stop with { Sent = true } };
        var r = state.Runs[stop.Target];
        Save(r with { StopProgress = StopProgress.AwaitingReply });
        effects.Add(new SendStop(id, stop.Target));
    }

    private void FinishStop(StopReturned e)
    {
        if (state.Stop is not { } stop || stop.Id != e.Id) return;
        if (state.Runs.GetValueOrDefault(stop.Target) is { } r && r.ManualStop == e.Id) {
            if (e.Result == StopResult.Rejected) {
                r = r with { ManualStop = null, StopProgress = null };
                if (r is { Owner.Source: RunSource.Scheduled, Terminal.State: RunState.Succeeded, Cleanup: CleanupStage.None }) {
                    r = r with { Cleanup = CleanupStage.Claimed };
                    Queue(new Cleanup(r.Key, r.Environment));
                }
                Save(r);
            } else if (r.StopProgress != StopProgress.Stopping) {
                Save(r with { StopProgress = e.Result == StopResult.Requested
                    ? StopProgress.Requested : StopProgress.AwaitingConfirmation });
            }
        }
        state = state with { Stop = null };
        effects.Add(new ReleaseStopGate(e.Id));
    }

    private ClosingOperation? ClosingAt(Guid id, ClosingStage stage) =>
        state.Closing is { } c && c.Id == id && c.Stage == stage ? c : null;

    private void Pause(BeginClosing e)
    {
        if (e.QuitRun is { } key && state.Runs.GetValueOrDefault(key)?.Quit != QuitStage.Pending) return;
        if (state.Closing is { } existing) {
            if (e.QuitRun is { } pending && existing.QuitRun == null) {
                if (existing.Kind == ClosingKind.Exit) state = state with { Closing = existing with { QuitRun = pending } };
                else state = state with { Closing = existing with { DeferredQuit = pending } };
            }
            return;
        }
        var closing = new ClosingOperation(e.Id, e.Kind, ClosingStage.Confirm, e.QuitRun);
        state = state with { Closing = closing };
        effects.Add(new SetClosing(true));
        foreach (var op in state.Operations.Values.OrderBy(op => op.Intent.Id)) {
            if (op.Stage is OperationStage.Commit or OperationStage.Activity or OperationStage.EndOld
                or OperationStage.Prepare or OperationStage.Sending) {
                // These effects already own the lease. Their receipts settle the physical work.
                Save(op with { Cancelled = true });
                continue;
            }
            if (op.Stage == OperationStage.Countdown) effects.Add(new HideCountdown(op.Intent.Id));
            effects.Add(new CancelOperation(op.Intent.Id, op.Lease));
            effects.Add(new OperationFinished(op.Intent, op.Admitted, null, new OperationCanceledException("操作已取消")));
            RestoreBinding(op);
            SkipRemainder(op, "应用正在退出");
            state = state with { Operations = state.Operations.Remove(op.Intent.Id) };
        }
        effects.Add(new ConfirmClosing(closing));
    }

    private void ClosingEnded(ClosingOperation c, ClosingEndReturned e)
    {
        if (!e.Ended) {
            if (e.RecoveryTarget != null) {
                foreach (var run in state.Runs.Values.Where(r => r.Environment == e.RecoveryTarget
                    && r.Cleanup is CleanupStage.Claimed or CleanupStage.WaitingForReconnect)) {
                    Save(run with { Cleanup = CleanupStage.Failed });
                    PresentFailure(run.Key, run.Owner, NoticeKind.CleanupFailed, e.Error
                        ?? new InvalidOperationException("旧的桌面分身未能确认结束，请先恢复运行环境。"), "定时任务收尾失败");
                }
            }
            Resume(c);
            return;
        }
        if (e.EndedTarget != null) MarkTargetCleanup(e.EndedTarget, CleanupStage.Superseded);
        if (c.Kind == ClosingKind.Update) {
            state = state with { Closing = c with { Stage = ClosingStage.Handoff } };
            effects.Add(new HandoffUpdate(c.Id));
        } else if (c.Kind == ClosingKind.EndEnvironment) {
            Resume(c);
            RetryQuit();
        } else CommitClosing(c);
    }

    private void Resume(ClosingOperation c)
    {
        state = state with { Closing = null };
        effects.Add(new SetClosing(false));
        if (c.DeferredQuit is { } pending) Queue(new RequestQuit(pending));
        if (c.QuitRun is { } key && state.Runs.TryGetValue(key, out var r)) Save(r with { Quit = QuitStage.AwaitingRecovery });
    }

    private void RetryQuit()
    {
        foreach (var r in state.Runs.Values.Where(r => r.Quit == QuitStage.AwaitingRecovery)) {
            Save(r with { Quit = QuitStage.Pending });
            if (!state.Deliveries.Any(d => !d.InFlight && d.Payload is RequestQuit q && q.Run == r.Key)) Queue(new RequestQuit(r.Key));
        }
    }

    private void CommitClosing(ClosingOperation c)
    {
        state = state with { Closing = c with { Stage = ClosingStage.Committed }, Deliveries = [], Runs = state.Runs.Clear() };
        effects.Add(new CommitExit(c.Id));
    }
}

