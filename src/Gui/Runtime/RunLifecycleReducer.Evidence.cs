using Gui.Worker;
using Protocol;
using static Gui.Runtime.LifecycleEffect;

namespace Gui.Runtime;

internal sealed partial class RunLifecycleReducer
{
    private void Observe(WorkerObservationBatch b, string? configurationName)
    {
        if (b.Kind == WorkerEvidenceKind.ProcessExited && b.Connection is { } exited) {
            Disconnect(exited, exited: true);
            foreach (var old in state.Runs.Values.Where(r => r.Key.WorkerInstanceId == exited.WorkerInstanceId)) {
                ApplyReconnectEvidence(old with { ReconnectFaultPending = false });
                var retired = state.Runs[old.Key] with { Retired = true };
                Save(retired);
                if (!retired.Settled) {
                    var conclusion = retired.Conclusion == StartConclusion.Accepted ? StartConclusion.EndUnconfirmed
                        : retired.Sends.Values.Any(s => s.Boundary == SendBoundary.PossiblySent) ? StartConclusion.StartUnknown
                        : StartConclusion.NotStarted;
                    Settle(retired, conclusion, LastError(retired) ?? new InvalidOperationException(conclusion switch {
                        StartConclusion.NotStarted => "Worker 在发送启动正文前已退出",
                        StartConclusion.StartUnknown => "Worker 已退出，启动结果未知",
                        _ => "Worker 已退出，运行结果未确认"
                    }));
                }
            }
            return;
        }
        if (b.Kind is WorkerEvidenceKind.Disconnected or WorkerEvidenceKind.AdmissionRemoved && b.Connection is { } closed) {
            Disconnect(closed, exited: false, b.Error);
            return;
        }
        // The caller may start observing after the initial full snapshot. A writer on a new
        // generation is also positive evidence of a live connection.
        if (b.Kind is WorkerEvidenceKind.BeforeBody or WorkerEvidenceKind.BodyStarted && b.Connection is { } writer
            && writer.Generation > state.LastGeneration) Connect(writer);
        // Local write boundaries and failures can arrive without a connection (or after detachment).
        if (b.Request?.Submission is { } submission && state.Runs.TryGetValue(submission.Run, out var owner)
            && owner.Sends.TryGetValue(submission.SubmissionId, out var send) && send.Identity == submission) {
            if (b.Kind != WorkerEvidenceKind.StartReply || b.Connection != null && b.Connection == state.Connection)
                StartEvidence(owner, send, b);
            return;
        }
        if (b.Kind == WorkerEvidenceKind.RequestFailed && b.Request is { QueryId: { } failed } request) {
            if (state.Runs.TryGetValue(request.Run, out var r)) QueryFailed(r, failed);
            return;
        }
        if (b.Kind is not (WorkerEvidenceKind.Snapshot or WorkerEvidenceKind.QueryReply) || b.Connection is not { } id) return;
        if (id != state.Connection) {
            if (id.Generation <= state.LastGeneration || b.Current == null) return;
            Connect(id);
        }

        // Current is the coordinator's monotonic projection. A low revision or a gap on this live
        // connection can still carry a genuine terminal, but cannot make a stale environment ready.
        if (b.Current is { } view && view.WorkerSnapshot?.WorkerInstanceId == id.WorkerInstanceId) {
            state = state with { Current = view };
        }
        var query = b.Kind == WorkerEvidenceKind.QueryReply ? b.Request : null;
        var answered = false;
        if (query?.QueryId is { } queryId && query.Connection == id && state.Runs.TryGetValue(query.Run, out var waiting)) {
            answered = waiting.Confirmation.InFlight == queryId;
            if (answered) Save(waiting with { Confirmation = waiting.Confirmation with { InFlight = null } });
            if (b.RunRegistered.HasValue) ApplyReconnectEvidence(state.Runs[query.Run]);
        }
        if (b.ObservedSnapshot is { } frame && frame.WorkerInstanceId == id.WorkerInstanceId) {
            if (frame.LastRun != null) ObserveRun(frame, frame.LastRun, false);
            if (frame.ActiveRun != null) ObserveRun(frame, frame.ActiveRun, true, configurationName);
            foreach (var r in state.Runs.Values.Where(r => r.Key.WorkerInstanceId == id.WorkerInstanceId && !r.Settled)) {
                if (!state.Current.Fresh || r.Conclusion == StartConclusion.Accepted && !CurrentShowsActive(r))
                    Save(r with { NeedQuery = true });
            }
        }
        if (query?.QueryId != null && query.Connection == id && state.Runs.TryGetValue(query.Run, out var run)) {
            QueryReply(run, query, b, answered);
        }
    }

    private void Connect(WorkerConnectionId id)
    {
        if (state.Connection is { } previous) Disconnect(previous, exited: false);
        state = state with { Connection = id, LastGeneration = id.Generation, Current = WorkerCoordinatorSnapshot.Empty };
    }

    private void Disconnect(WorkerConnectionId id, bool exited, Exception? error = null)
    {
        var current = state.Connection;
        if (current == id || exited && (current?.WorkerInstanceId ?? state.Current.WorkerSnapshot?.WorkerInstanceId) == id.WorkerInstanceId) {
            state = state with { Connection = null, Current = exited
                ? new(WorkerObservation.WorkerExited, false, null, "Worker 已退出")
                : state.Current with { Observation = WorkerObservation.IpcDisconnected, SnapshotFresh = false,
                    Detail = error?.Message ?? "Worker IPC 已断开" } };
        }
        if (id.Generation > state.LastGeneration) state = state with { LastGeneration = id.Generation };
        if (!exited && current != null && current != id) return;
        foreach (var r in state.Runs.Values.Where(r => r.Key.WorkerInstanceId == id.WorkerInstanceId && !r.Settled)) {
            CancelConfirmation(r);
            Save(r with { NeedQuery = true, Confirmation = new(), AwaitingReconnectQuery = true });
        }
    }

    private static Exception? LastError(TrackedRun r) =>
        r.Submission is { } send ? r.Sends[send.SubmissionId].Error : null;

    private bool CurrentShowsActive(TrackedRun r) =>
        state.Current is { Fresh: true, WorkerSnapshot: { ActiveRun: { } active } worker }
        && worker.WorkerInstanceId == r.Key.WorkerInstanceId && active.RunId == r.Key.RunId && active.PlanDigest == r.Digest;

    private void StartEvidence(TrackedRun r, SendRecord s, WorkerObservationBatch b)
    {
        if (r.Retired || b.Request == null) return;
        if (s.Wire != null && s.Wire.RequestId != b.Request.RequestId) return;
        var next = s with { Wire = b.Request, Error = b.Error ?? s.Error };
        if (b.StartReply == StartReplyKind.RejectedBeforeRegistration) next = next with { RejectionCode = b.ReplyCode };
        if (r.Settled || r.Submission != s.Identity) {
            // A late live reply belongs to its original send, even after an explicit retry.
            // It cannot reopen the Run, remove a different sender, or transfer quit eligibility.
            Save(r with { Sends = r.Sends.SetItem(s.Identity.SubmissionId, next) });
            return;
        }
        switch (b.Kind) {
            case WorkerEvidenceKind.BeforeBody when s.Boundary == SendBoundary.BeforeBody: break;
            case WorkerEvidenceKind.BodyStarted when s.Boundary == SendBoundary.BeforeBody:
                next = next with { Boundary = SendBoundary.PossiblySent };
                if (state.Quits.TryGetValue(r.Key, out var quit) && quit == QuitStage.AwaitingBody)
                    SetQuit(r.Key, QuitStage.Bound);
                break;
            case WorkerEvidenceKind.NotSent:
            case WorkerEvidenceKind.StartReply when b.StartReply == StartReplyKind.RejectedBeforeRegistration:
                next = next with { Boundary = SendBoundary.Excluded };
                break;
            case WorkerEvidenceKind.StartReply when b.StartReply is StartReplyKind.Accepted or StartReplyKind.AlreadyAccepted:
                next = next with { Boundary = SendBoundary.Accepted };
                r = Conclude(r, StartConclusion.Accepted) with { NeedQuery = r.AwaitingReconnectQuery || !CurrentShowsActive(r) };
                break;
            case WorkerEvidenceKind.StartReply:
            case WorkerEvidenceKind.RequestFailed:
                r = r with { NeedQuery = r.AwaitingReconnectQuery || r.Conclusion != StartConclusion.Accepted || !CurrentShowsActive(r) };
                break;
            default: return;
        }
        r = r with { Sends = r.Sends.SetItem(s.Identity.SubmissionId, next) };
        Save(r);
        if (r.Conclusion != StartConclusion.Accepted && r.Sends.Values.All(item => item.Boundary == SendBoundary.Excluded)) {
            Settle(r, StartConclusion.NotStarted, b.Error ?? next.Error);
        }
    }

    private void ObserveRun(WorkerSnapshot frame, RunSnapshot snapshot, bool active, string? configurationName = null)
    {
        var key = new RunKey(frame.WorkerInstanceId, snapshot.RunId);
        var r = state.Runs.GetValueOrDefault(key);
        if (r == null) {
            // A historical Last observed on takeover never acquires notification eligibility.
            r = new(key, snapshot.PlanDigest);
            if (!active || !CurrentShowsActive(r)) return;
        }
        if (r.Settled || r.Digest != snapshot.PlanDigest) return;
        if (r.AwaitingReconnectQuery && r.ReconnectEvidence?.LastRun is { } terminal
            && terminal.RunId == key.RunId && terminal.PlanDigest == r.Digest) return;
        if (active && CurrentShowsActive(r)) {
            if (r.Owner == null && r.TakeoverConfigurationName == null && !string.IsNullOrWhiteSpace(configurationName))
                r = r with { TakeoverConfigurationName = configurationName };
            var faulted = state.Current.WorkerSnapshot!.WorkerState == WorkerState.Faulted;
            if (r.SeenActive && faulted && !r.SeenFaulted && r.Notification == NotificationSlot.Open)
                r = r.AwaitingReconnectQuery ? r with { ReconnectFaultPending = true } : NotifyFault(r);
            r = r with { SeenActive = true, SeenFaulted = r.SeenFaulted || faulted };
        }
        if (r.AwaitingReconnectQuery) {
            // On the new live connection keep genuine positive evidence, but let the ordered
            // targeted read resolve the disconnect. An ordinary idle frame is never a negative.
            Save(r with { ReconnectEvidence = frame });
            return;
        }
        r = Conclude(r, StartConclusion.Accepted);
        if (!active) {
            Terminal(r, snapshot);
            return;
        }
        if (CurrentShowsActive(r)) {
            CancelTimer(r);
            r = r with { NeedQuery = false, Confirmation = r.Confirmation with { Failures = 0, Due = null, Timer = 0 } };
        }
        Save(r);
    }

    private TrackedRun NotifyFault(TrackedRun r)
    {
        Queue(new Present(r.Key, r.Owner, NoticeKind.Faulted, "任务未能确认结束"));
        return r with { Notification = NotificationSlot.Claimed, NotificationIntent = r.Owner?.Id };
    }

    private void ApplyReconnectEvidence(TrackedRun r)
    {
        if (!r.AwaitingReconnectQuery) return;
        if (r.ReconnectFaultPending) r = NotifyFault(r);
        Save(r with { AwaitingReconnectQuery = false, ReconnectEvidence = null, ReconnectFaultPending = false });
        if (r.ReconnectEvidence is not { } frame) return;
        if (frame.LastRun is { } last && last.RunId == r.Key.RunId && last.PlanDigest == r.Digest) ObserveRun(frame, last, false);
        else if (frame.ActiveRun is { } active && active.RunId == r.Key.RunId) ObserveRun(frame, active, true);
    }

    private void QueryReply(TrackedRun r, WorkerRequestEvidence q, WorkerObservationBatch b, bool answered)
    {
        if (r.Settled) return;
        if (b.RunRegistered is not bool registered || b.ObservedSnapshot is not { } snapshot) {
            if (answered && r.NeedQuery) Backoff(r);
            return;
        }
        var otherDigest = (snapshot.ActiveRun?.RunId == r.Key.RunId && snapshot.ActiveRun.PlanDigest != r.Digest)
            || (snapshot.LastRun?.RunId == r.Key.RunId && snapshot.LastRun.PlanDigest != r.Digest);
        if (registered && !otherDigest) {
            if (snapshot.ActiveRun?.RunId == r.Key.RunId || snapshot.LastRun?.RunId == r.Key.RunId) {
                if (answered && r.NeedQuery) Backoff(r);
                return;
            }
            // The registry and both slots were read under the same Worker lock. A registered Run
            // absent from both slots has been superseded, if the snapshot contains a successor.
            if (snapshot.ActiveRun != null || snapshot.LastRun != null) {
                Settle(r, StartConclusion.EndUnconfirmed, LastError(r));
                return;
            }
            r = Conclude(r, StartConclusion.Accepted) with { NeedQuery = true };
        } else if (r.Conclusion != StartConclusion.Accepted) {
            var reason = new InvalidOperationException(otherDigest ? "Run ID 与已登记的计划冲突" : "Worker 未登记本次启动", LastError(r));
            foreach (var s in r.Sends.Values) {
                if (s.Boundary != SendBoundary.PossiblySent || s.Wire is not { Connection: { } source } wire) continue;
                // A new connection on this same Worker follows the closed writer. Never compare
                // write sequences across connections. An as-yet unclassified writer is not covered.
                if (!q.Covers(wire) && !(source.WorkerInstanceId == q.Run.WorkerInstanceId
                    && source.Generation < q.Connection!.Value.Generation)) continue;
                r = r with { Sends = r.Sends.SetItem(s.Identity.SubmissionId, s with { Boundary = SendBoundary.Excluded,
                    RejectionCode = otherDigest ? "run_id_conflict" : s.RejectionCode }) };
            }
            if (r.Sends.Values.All(s => s.Boundary == SendBoundary.Excluded)) {
                Settle(r, StartConclusion.NotStarted, reason);
                return;
            }
        }
        Save(r);
        if (answered && r.NeedQuery) Backoff(r);
    }

    private void QueryFailed(TrackedRun r, QueryId id)
    {
        if (r.Confirmation.InFlight != id || r.Settled) return;
        r = r with { Confirmation = r.Confirmation with { InFlight = null } };
        Save(r);
        if (r.NeedQuery) Backoff(r);
    }

    private void Backoff(TrackedRun r)
    {
        int[] seconds = [1, 2, 4, 8, 16, 30];
        var index = Math.Min(r.Confirmation.Failures, seconds.Length - 1);
        var due = state.Now + TimeSpan.FromSeconds(seconds[index]);
        var token = Next();
        Save(r with { Confirmation = new(Failures: Math.Min(index + 1, seconds.Length - 1), Timer: token, Due: due) });
        effects.Add(new ArmRetry(r.Key, token, due));
    }

    private void CancelConfirmation(TrackedRun r)
    {
        CancelTimer(r);
        if (r.Confirmation.InFlight is { } query) effects.Add(new CancelQuery(r.Key, query));
    }

    private void CancelTimer(TrackedRun r)
    {
        if (r.Confirmation.Timer != 0) effects.Add(new CancelRetry(r.Key, r.Confirmation.Timer));
    }

    private void DispatchQueries()
    {
        if (state.Connection is not { } live) return;
        foreach (var r in state.Runs.Values.Where(r => r.Key.WorkerInstanceId == live.WorkerInstanceId && !r.Settled
            && r.NeedQuery && r.Confirmation is { InFlight: null, Due: null }).OrderBy(r => r.Key.RunId)) {
            var id = new QueryId(new Guid(0, 0, 0, BitConverter.GetBytes(Next())));
            Save(r with { Confirmation = r.Confirmation with { InFlight = id } });
            effects.Add(new QueryRun(r.Key, id, live));
        }
    }
}
