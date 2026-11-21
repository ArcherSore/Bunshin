using System.Text.Json;
using ProjectModel;
using Protocol;

namespace Gui.Worker;

internal readonly record struct RunKey(Guid WorkerInstanceId, Guid RunId);
internal sealed record Submission(Guid SubmissionId, Guid IntentId, RunKey Run, string PlanDigest);
internal readonly record struct QueryId(Guid Value);
internal readonly record struct WorkerConnectionId(Guid WorkerInstanceId, long Generation);

internal sealed record WorkerRequestEvidence(Guid RequestId, RunKey Run, Submission? Submission, QueryId? QueryId,
    WorkerConnectionId? Connection, long? WriteSequence = null)
{
    internal bool Covers(WorkerRequestEvidence send) =>
        QueryId.HasValue && send.Submission is not null && Connection == send.Connection && Run == send.Run
        && send.WriteSequence < WriteSequence;
}

internal enum WorkerEvidenceKind
{
    BeforeBody, BodyStarted, NotSent, StartReply, QueryReply, RequestFailed,
    Snapshot, Disconnected, ProcessExited, AdmissionRemoved
}

internal enum StartReplyKind { Accepted, AlreadyAccepted, RejectedBeforeRegistration, Indeterminate }

internal sealed record WorkerObservationBatch(WorkerConnectionId? Connection, WorkerEvidenceKind Kind,
    WorkerRequestEvidence? Request = null, WorkerCoordinatorSnapshot? Current = null,
    WorkerSnapshot? ObservedSnapshot = null, StartReplyKind? StartReply = null, string? ReplyCode = null,
    bool? RunRegistered = null, Exception? Error = null);

internal sealed partial class WorkerCoordinator
{
    // A timed-out request keeps its wire identity only. Never store a plan, token or writer here.
    private sealed class EvidenceRequest(WorkerRequestEvidence identity)
    {
        internal WorkerRequestEvidence Identity = identity;
        internal bool DiscardReply;
        internal TaskCompletionSource<WorkerObservationBatch>? Waiter =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly TimeProvider _requestClock;
    private readonly Func<Stream, Stream>? _wrapPipe;
    private readonly Dictionary<Guid, EvidenceRequest> _evidenceRequests = [];
    private WorkerConnectionId _connectionId;
    private long _writeSequence;
    // Reserved before scheduling each full read. Only validated replies release starts on this connection.
    private int _pendingSnapshotSyncs;
    private TaskCompletionSource<bool>? _snapshotSync;
    private bool _evidenceRequested;

    // Subscribers enqueue batches synchronously. Frame validation, publication and waiter
    // completion share the gate with detachment and with the write-side body boundary.
    internal event EventHandler<WorkerObservationBatch>? ObservationReceived;

    internal void SubscribeStartEvidence(EventHandler<WorkerObservationBatch> observe)
    {
        lock (_gate) {
            ObservationReceived += observe;
            if (Snapshot is { Fresh: true, WorkerSnapshot: { } snapshot })
                observe(this, SnapshotBatchLocked(snapshot));
        }
    }

    internal (int Requests, int Waiters) EvidenceCounts
    {
        get {
            lock (_gate) {
                return (_evidenceRequests.Count, _evidenceRequests.Values.Count(entry => entry.Waiter is not null));
            }
        }
    }

    private void PublishLocked(WorkerObservationBatch batch) => ObservationReceived?.Invoke(this, batch);

    private void BeginEvidenceLocked(WorkerAdmissionRecord admission)
    {
        _connectionId = new(admission.WorkerInstanceId, _connectionId.Generation + 1);
        _writeSequence = 0;
        ReserveSnapshotSyncLocked();
    }

    private void ReserveSnapshotSyncLocked()
    {
        _pendingSnapshotSyncs++;
        _snapshotSync ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private void FinishEvidenceLocked(ProtocolConnection connection)
    {
        // A clean peer EOF has no exception. All other closures carry their original cause.
        var batch = new WorkerObservationBatch(_connectionId, WorkerEvidenceKind.Disconnected,
            Error: connection.Closed.GetAwaiter().GetResult());
        _pendingSnapshotSyncs = 0;
        _snapshotSync?.TrySetResult(false);
        _snapshotSync = null;
        PublishLocked(batch);
        foreach (var request in _evidenceRequests.Values) {
            request.Waiter?.TrySetResult(batch);
            request.Waiter = null;
        }
        _evidenceRequests.Clear();
    }

    private void ObserveAdmissionLocked(WorkerAdmissionRecord record, WorkerEvidenceKind kind)
    {
        var id = _connectionId.WorkerInstanceId == record.WorkerInstanceId
            ? _connectionId : new WorkerConnectionId(record.WorkerInstanceId, 0);
        PublishLocked(new(id, kind));
    }

    private void RemoveAdmissionLocked(WorkerAdmissionRecord record, bool confirmedExit)
    {
        if (confirmedExit && record.WorkerPid.HasValue && Snapshot.Observation != WorkerObservation.WorkerExited) {
            ObserveAdmissionLocked(record, WorkerEvidenceKind.ProcessExited);
        }
        _admission = null;
        ObserveAdmissionLocked(record, WorkerEvidenceKind.AdmissionRemoved);
    }

    internal Task<WorkerObservationBatch> SendStartAsync(Submission submission, RunStartAttempt attempt,
        CancellationToken cancellationToken = default) =>
        TraceRequestAsync(ProtocolOperations.RunStart,
            () => SendEvidenceRequestAsync(submission.Run, submission, null, attempt, cancellationToken));

    internal Task<WorkerObservationBatch> QueryRunAsync(RunKey run, QueryId queryId,
        CancellationToken cancellationToken = default) =>
        SendEvidenceRequestAsync(run, null, queryId, null, cancellationToken);

    private async Task<WorkerObservationBatch> SendEvidenceRequestAsync(RunKey run, Submission? submission,
        QueryId? queryId, RunStartAttempt? attempt, CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(RequestTimeout, _requestClock);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, cancellationToken, _shutdown.Token);
        var token = lifetime.Token;
        bool WorkerReadyForPlan() => Snapshot is { FreshReadyToStart: true, WorkerSnapshot: { } worker }
            && attempt!.Plan.RuntimeProfileDigest == worker.RuntimeProfileDigest;
        ProtocolConnection? connection;
        EvidenceRequest entry;
        bool readyAtEntry;
        lock (_gate) {
            _evidenceRequested = true;
            connection = _connection;
            WorkerConnectionId? id = connection is { IsClosed: false } && _connectionId.WorkerInstanceId == run.WorkerInstanceId
                ? _connectionId : null;
            entry = new(new(Guid.NewGuid(), run, submission, queryId, id));
            readyAtEntry = attempt == null || WorkerReadyForPlan();
            if (submission is not null) {
                PublishLocked(new(id, WorkerEvidenceKind.BeforeBody, entry.Identity));
            }
        }
        // Only the transport deadline and shutdown can interrupt a query after the prefix starts.
        using var queryWriter = queryId.HasValue
            ? CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, _shutdown.Token) : null;
        using var discard = queryId.HasValue ? cancellationToken.Register(() => {
            lock (_gate) entry.DiscardReply = true;
        }) : default;
        var frameToken = queryWriter?.Token ?? token;
        try {
            Task<WorkerObservationBatch> reply;
            lock (_gate) {
                if (connection is null || connection != _connection || connection.IsClosed || entry.Identity.Connection is null) {
                    throw new InvalidOperationException("目标 Worker IPC 已失效。");
                }
                if (attempt is not null && (attempt.RunId != run.RunId || attempt.PlanDigest != submission!.PlanDigest
                    || !readyAtEntry)) {
                    throw new InvalidOperationException("当前 Worker、计划或 Run 状态不允许 Start。");
                }
                reply = entry.Waiter!.Task;
                _evidenceRequests.Add(entry.Identity.RequestId, entry);
            }
            token.ThrowIfCancellationRequested();
            var frame = attempt is null
                ? WireEnvelope.Request(ProtocolOperations.WorkerGetSnapshot, entry.Identity.RequestId, new GetSnapshotRequest(run.RunId))
                : WireEnvelope.Request(ProtocolOperations.RunStart, entry.Identity.RequestId,
                    new RunStartRequest(run.RunId, attempt.PlanDigest, attempt.Plan) { RedactionValues = attempt.RedactionValues });
            Task<bool>? sync = null;
            bool CanWriteStart()
            {
                lock (_gate) {
                    token.ThrowIfCancellationRequested();
                    if (connection != _connection || connection.IsClosed)
                        throw new IOException("目标 Worker IPC 已断开。");
                    sync = _snapshotSync?.Task;
                    return sync == null && WorkerReadyForPlan();
                }
            }
            while (!await WriteRequestAsync(connection, frame, token, () => BeginBody(connection, entry, frameToken),
                attempt is null ? null : CanWriteStart, frameToken).WaitAsync(frameToken).ConfigureAwait(false)) {
                if (sync == null) {
                    lock (_gate) {
                        _evidenceRequests.Remove(entry.Identity.RequestId);
                        return PublishStartNotSentLocked(entry, "当前 Worker 或计划状态不允许 Start。");
                    }
                }
                // The pipe write lock is free for the snapshot request. Recheck under that lock after every wake.
                if (!await sync.WaitAsync(token).ConfigureAwait(false))
                    throw await connection.Closed.ConfigureAwait(false) ?? new EndOfStreamException("Worker IPC 已关闭。");
            }
            token.ThrowIfCancellationRequested();
            var batch = await reply.WaitAsync(token).ConfigureAwait(false);
            if (batch.Kind == WorkerEvidenceKind.Disconnected) {
                throw batch.Error ?? new EndOfStreamException("Worker IPC 已关闭。");
            }
            return batch;
        } catch (Exception error) {
            var failure = error is OperationCanceledException && deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested
                ? new TimeoutException("Worker 请求已超时。", error) : error;
            lock (_gate) {
                entry.Waiter = null;
                var beforeBody = !entry.Identity.WriteSequence.HasValue;
                if (beforeBody) {
                    _evidenceRequests.Remove(entry.Identity.RequestId);
                }
                PublishLocked(new(entry.Identity.Connection, beforeBody && submission is not null
                    ? WorkerEvidenceKind.NotSent : WorkerEvidenceKind.RequestFailed, entry.Identity, Error: failure));
            }
            if (failure == error) {
                throw;
            }
            throw failure;
        }
    }

    private WorkerObservationBatch PublishStartNotSentLocked(EvidenceRequest entry, string reason)
    {
        var batch = new WorkerObservationBatch(entry.Identity.Connection, WorkerEvidenceKind.NotSent, entry.Identity,
            Error: new InvalidOperationException(reason));
        PublishLocked(batch);
        return batch;
    }

    private void BeginBody(ProtocolConnection connection, EvidenceRequest entry, CancellationToken token)
    {
        lock (_gate) {
            token.ThrowIfCancellationRequested();
            if (connection != _connection || connection.IsClosed) {
                throw new IOException("目标 Worker IPC 已断开。");
            }
            // Both starts and targeted queries pass this point under the same pipe write lock.
            entry.Identity = entry.Identity with { WriteSequence = ++_writeSequence };
            if (entry.Identity.Submission is not null) {
                PublishLocked(new(_connectionId, WorkerEvidenceKind.BodyStarted, entry.Identity));
            }
        }
    }

    private bool ReceiveEvidenceReplyLocked(Guid id, WireEnvelope frame, WorkerAdmissionRecord admission,
        ProtocolConnection connection, out bool stateChanged)
    {
        stateChanged = false;
        if (!_evidenceRequests.Remove(id, out var entry)) {
            return false;
        }
        if (entry.DiscardReply) return true;
        var request = entry.Identity;
        try {
            var start = request.Submission is not null;
            if (!request.WriteSequence.HasValue || frame.ProtocolVersion != ProtocolConstants.ProtocolVersion
                || frame.Operation != (start ? ProtocolOperations.RunStart : ProtocolOperations.WorkerGetSnapshot)
                || frame.Success is null || (frame.Error is null) != frame.Success) {
                throw new ProtocolException("Worker 响应的版本、操作或结果与请求不匹配。");
            }
            WorkerObservationBatch batch;
            if (start) {
                batch = ReadStartReply(_connectionId, request, frame);
            } else if (frame.Success == false) {
                batch = new(_connectionId, WorkerEvidenceKind.QueryReply, request,
                    ReplyCode: frame.Error!.Code, Error: ResponseError(frame));
            } else {
                var response = ProtocolJson.Deserialize<GetSnapshotResponse>(frame.Data);
                if (response.RunRegistered is not bool registered || response.Snapshot is null
                    || !registered && (response.Snapshot.ActiveRun?.RunId == request.Run.RunId
                        || response.Snapshot.LastRun?.RunId == request.Run.RunId)) {
                    throw new ProtocolException("定向 getSnapshot 缺少有效的 runRegistered 或与快照冲突。");
                }
                if (!frame.Data.GetProperty("snapshot").TryGetProperty("stateRevision", out _)) {
                    throw new ProtocolException("定向 getSnapshot 缺少 snapshot.stateRevision。");
                }
                stateChanged = ApplyFreshSnapshotLocked(response.Snapshot, admission);
                batch = SnapshotBatchLocked(response.Snapshot) with {
                    Kind = WorkerEvidenceKind.QueryReply, Request = request, RunRegistered = registered
                };
            }
            PublishLocked(batch);
            entry.Waiter?.TrySetResult(batch);
        } catch (Exception error) when (error is JsonException or ProtocolException) {
            var fault = error as ProtocolException ?? new ProtocolException("Worker 响应的数据格式无效。", error);
            connection.Close(fault);
            entry.Waiter?.TrySetException(fault);
            throw fault;
        }
        return true;
    }

    private static WorkerObservationBatch ReadStartReply(WorkerConnectionId source, WorkerRequestEvidence request,
        WireEnvelope frame)
    {
        var code = frame.Success == true ? ProtocolJson.Deserialize<RunStartResponse>(frame.Data).Disposition : frame.Error!.Code;
        var kind = (frame.Success, code) switch {
            (true, "accepted") => StartReplyKind.Accepted,
            (true, "already_accepted") => StartReplyKind.AlreadyAccepted,
            (false, "protocol_version_mismatch" or "invalid_request" or "run_id_conflict" or "worker_not_ready"
                or "worker_faulted" or "operation_not_allowed" or "worker_busy" or "invalid_run_plan")
                => StartReplyKind.RejectedBeforeRegistration,
            _ => StartReplyKind.Indeterminate
        };
        return new(source, WorkerEvidenceKind.StartReply, request, StartReply: kind, ReplyCode: code, Error: ResponseError(frame));
    }

    private static Exception? ResponseError(WireEnvelope frame) => frame.Error is { } error
        ? new WorkerProtocolErrorException(error.Code, error.Message) : null;

    private bool ObservingEvidence => _evidenceRequested || ObservationReceived is not null;

    private WorkerObservationBatch SnapshotBatchLocked(WorkerSnapshot observed) =>
        new(_connectionId, WorkerEvidenceKind.Snapshot, Current: Snapshot, ObservedSnapshot: observed);

    private static void ValidateEvidenceSnapshot(WorkerSnapshot snapshot)
    {
        if (snapshot is null || snapshot.StateRevision < 0
            || snapshot.LastRun is { State: not (RunState.Succeeded or RunState.Failed or RunState.Cancelled) }
            || snapshot.ActiveRun is { State: not (RunState.Starting or RunState.Running or RunState.Stopping) }) {
            throw new ProtocolException("Worker Snapshot 状态或修订无效。");
        }
    }
}
