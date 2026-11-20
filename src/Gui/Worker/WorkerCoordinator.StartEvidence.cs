using System.Runtime.ExceptionServices;
using System.Text.Json;
using ProjectModel;
using Protocol;

namespace Gui.Worker;

internal readonly record struct RunKey(Guid WorkerInstanceId, Guid RunId);
internal sealed record Submission(Guid SubmissionId, Guid IntentId, RunKey Run, string PlanDigest);
internal readonly record struct QueryId(Guid Value);
internal readonly record struct WorkerConnectionId(Guid WorkerInstanceId, long Generation);

// Sequence is assigned at the pipe write lock. A queued writer has no position on the wire yet.
internal sealed record WorkerRequestEvidence(Guid RequestId, RunKey Run, Submission? Submission, QueryId? QueryId,
    WorkerConnectionId? Connection, long? WriteSequence = null)
{
    // The server drains and seals each admitted pipe before admitting the next. The same Worker
    // services requests serially, including across its reconnect. Only compare body-started sends.
    internal bool Covers(WorkerRequestEvidence send) =>
        QueryId.HasValue && send.Submission is not null && Run == send.Run
        && Connection is { } query && send.Connection is { } start
        && WriteSequence is long querySequence && send.WriteSequence is long startSequence
        && (start.Generation < query.Generation || start == query && startSequence < querySequence);
}

internal enum WorkerEvidenceKind
{
    Connected, BeforeBody, BodyStarted, WriteCompleted, NotSent, StartReply, QueryReply, RequestFailed,
    Snapshot, ProtocolFault, Eof, ReaderDrained, ProcessExited, AdmissionRemoved
}
internal enum StartReplyKind { Accepted, AlreadyAccepted, RejectedBeforeRegistration, Indeterminate }

// Current is the projection for live decisions. ObservedSnapshot is the validated frame, including
// older revisions and retired connections. A targeted reply delivers both together with its barrier.
internal sealed record WorkerObservationBatch(WorkerConnectionId? Connection, WorkerEvidenceKind Kind,
    WorkerRequestEvidence? Request = null, WorkerCoordinatorSnapshot? Current = null,
    WorkerSnapshot? ObservedSnapshot = null, StartReplyKind? StartReply = null, string? ReplyCode = null,
    bool? RunRegistered = null, Exception? Error = null);

internal sealed partial class WorkerCoordinator
{
    // Only the read loop owns drain. Removing an Admission invalidates writes but leaves this
    // reader's verified identity and request associations intact until its last frame is consumed.
    private sealed class EvidenceConnection(ProtocolConnection pipe, WorkerAdmissionRecord admission, long generation)
    {
        internal ProtocolConnection Pipe { get; } = pipe;
        internal WorkerAdmissionRecord Admission { get; } = admission;
        internal WorkerConnectionId Id { get; } = new(admission.WorkerInstanceId, generation);
        internal Dictionary<Guid, EvidencePending> Requests { get; } = [];
        internal bool Fresh;
        internal bool ReadEnded;
    }

    private sealed class EvidencePending(WorkerRequestEvidence request)
    {
        internal WorkerRequestEvidence Request = request;
        internal bool BodyStarted;
        internal TaskCompletionSource<WorkerObservationBatch>? Waiter =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly TimeProvider _requestClock;
    private readonly Func<Stream, Stream>? _wrapPipe;
    private EvidenceConnection? _evidenceConnection;
    private WorkerConnectionId _lastConnectionId;
    private WorkerSnapshot? _latestEvidenceSnapshot;
    private volatile bool _evidenceRequested;
    private bool ObservingEvidence => ObservationReceived is not null || _evidenceRequested;

    // Subscribers enqueue the immutable batch synchronously. The coordinator gate orders frame,
    // write-boundary, and retirement notifications before waking a request's asynchronous continuation.
    internal event EventHandler<WorkerObservationBatch>? ObservationReceived;

    internal int EvidenceWaiterCount
    {
        get {
            lock (_gate) {
                return _evidenceConnection?.Requests.Values.Count(entry => entry.Waiter is not null) ?? 0;
            }
        }
    }

    private EvidenceConnection BeginEvidenceConnectionLocked(ProtocolConnection pipe, WorkerAdmissionRecord admission)
    {
        var source = new EvidenceConnection(pipe, admission, _lastConnectionId.Generation + 1);
        CancelExitWatchLocked();
        _evidenceConnection = source;
        _lastConnectionId = source.Id;
        if (_latestEvidenceSnapshot?.WorkerInstanceId != admission.WorkerInstanceId) {
            _latestEvidenceSnapshot = null;
        }
        PublishLocked(new(source.Id, WorkerEvidenceKind.Connected));
        return source;
    }

    private void ObserveAdmissionLocked(WorkerAdmissionRecord record, WorkerEvidenceKind kind)
    {
        var id = _lastConnectionId.WorkerInstanceId == record.WorkerInstanceId
            ? _lastConnectionId : new WorkerConnectionId(record.WorkerInstanceId, 0);
        PublishLocked(new(id, kind));
    }

    private void PublishLocked(WorkerObservationBatch batch) => ObservationReceived?.Invoke(this, batch);

    internal Task<WorkerObservationBatch> SendStartAsync(Submission submission, RunStartAttempt attempt,
        CancellationToken cancellationToken = default) =>
        SendEvidenceRequestAsync(submission.Run, submission, null, attempt, cancellationToken);

    internal Task<WorkerObservationBatch> QueryRunAsync(RunKey run, QueryId queryId,
        CancellationToken cancellationToken = default) =>
        SendEvidenceRequestAsync(run, null, queryId, null, cancellationToken);

    private async Task<WorkerObservationBatch> SendEvidenceRequestAsync(RunKey run, Submission? submission,
        QueryId? queryId, RunStartAttempt? attempt, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(RequestTimeout, _requestClock);
        EvidenceConnection? source;
        EvidencePending entry;
        lock (_gate) {
            _evidenceRequested = true;
            source = _evidenceConnection;
            var id = source?.Id.WorkerInstanceId == run.WorkerInstanceId ? source.Id : (WorkerConnectionId?)null;
            entry = new(new(Guid.NewGuid(), run, submission, queryId, id));
            if (submission is not null) {
                PublishLocked(new(id, WorkerEvidenceKind.BeforeBody, entry.Request));
            }
        }
        var waiter = entry.Waiter!;
        try {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken, _shutdown.Token);
            var token = lifetime.Token;

            void CheckTargetLocked()
            {
                if (source is null || source.Pipe != _connection || source.ReadEnded
                    || source.Id.WorkerInstanceId != run.WorkerInstanceId) {
                    throw new InvalidOperationException("目标 Worker IPC 已失效。");
                }
            }

            lock (_gate) {
                CheckTargetLocked();
                if (attempt is not null && (attempt.RunId != run.RunId || attempt.PlanDigest != submission!.PlanDigest
                    || attempt.Plan.RuntimeProfileDigest != source!.Admission.RuntimeProfileDigest
                    || !source.Fresh || _latestEvidenceSnapshot is not { WorkerState: WorkerState.Ready, RunState: RunState.Idle })) {
                    throw new InvalidOperationException("当前 Worker、计划或 Run 状态不允许 Start。");
                }
                source!.Requests.Add(entry.Request.RequestId, entry);
            }
            token.ThrowIfCancellationRequested();
            var requestId = entry.Request.RequestId;
            var frame = attempt is null
                ? WireEnvelope.Request(ProtocolOperations.WorkerGetSnapshot, requestId, new GetSnapshotRequest(run.RunId))
                : WireEnvelope.Request(ProtocolOperations.RunStart, requestId,
                    new RunStartRequest(run.RunId, attempt.PlanDigest, attempt.Plan) { RedactionValues = attempt.RedactionValues });
            await source.Pipe.WriteFrameAsync(frame, token, sequence => {
                lock (_gate) {
                    CheckTargetLocked();
                    entry.Request = entry.Request with { WriteSequence = sequence };
                }
            }, () => {
                lock (_gate) {
                    CheckTargetLocked();
                    entry.BodyStarted = true;
                    if (submission is not null) {
                        PublishLocked(new(source.Id, WorkerEvidenceKind.BodyStarted, entry.Request));
                    }
                }
            }).ConfigureAwait(false);
            lock (_gate) {
                PublishLocked(new(source.Id, WorkerEvidenceKind.WriteCompleted, entry.Request));
            }
            token.ThrowIfCancellationRequested();
            var reply = await waiter.Task.WaitAsync(token).ConfigureAwait(false);
            if (reply.Kind is WorkerEvidenceKind.ProtocolFault or WorkerEvidenceKind.ReaderDrained) {
                ExceptionDispatchInfo.Throw(reply.Error ?? new EndOfStreamException("Worker 读端已排空。"));
            }
            return reply;
        } catch (Exception error) {
            var failure = error is OperationCanceledException && timeout.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested ? new TimeoutException("Worker 请求在 15 秒后超时。", error) : error;
            lock (_gate) {
                // The reader removes answered entries. Here a possibly written request keeps only
                // its IDs and position, so an arbitrarily late frame can still be interpreted.
                entry.Waiter = null;
                if (!entry.BodyStarted) {
                    source?.Requests.Remove(entry.Request.RequestId);
                }
                PublishLocked(new(entry.Request.Connection,
                    submission is not null && !entry.BodyStarted ? WorkerEvidenceKind.NotSent : WorkerEvidenceKind.RequestFailed,
                    entry.Request, Error: failure));
            }
            if (failure != error) {
                throw failure;
            }
            throw;
        }
    }

    private bool ReceiveEvidenceReply(EvidenceConnection source, Guid id, WireEnvelope frame)
    {
        lock (_gate) {
            if (!source.Requests.Remove(id, out var entry)) {
                return false;
            }
            var request = entry.Request;
            try {
                var start = request.Submission is not null;
                if (!entry.BodyStarted || frame.ProtocolVersion != ProtocolConstants.ProtocolVersion
                    || frame.Operation != (start ? ProtocolOperations.RunStart : ProtocolOperations.WorkerGetSnapshot)
                    || frame.Success is null || (frame.Error is null) != frame.Success) {
                    throw new ProtocolException("Worker 响应的版本、操作或结果与请求不匹配。");
                }
                WorkerObservationBatch batch;
                if (start) {
                    batch = ReadStartReply(source.Id, request, frame);
                } else if (frame.Success == false) {
                    batch = new(source.Id, WorkerEvidenceKind.QueryReply, request, ReplyCode: frame.Error!.Code,
                        Error: ResponseError(frame));
                } else {
                    var response = ProtocolJson.Deserialize<GetSnapshotResponse>(frame.Data);
                    if (response.RunRegistered is not bool registered || response.Snapshot is null
                        || !registered && (response.Snapshot.ActiveRun?.RunId == request.Run.RunId
                            || response.Snapshot.LastRun?.RunId == request.Run.RunId)) {
                        throw new ProtocolException("定向 getSnapshot 缺少有效的 runRegistered 或与快照冲突。");
                    }
                    batch = SnapshotBatchLocked(source, response.Snapshot, full: true) with {
                        Kind = WorkerEvidenceKind.QueryReply, Request = request, RunRegistered = registered
                    };
                }
                PublishLocked(batch);
                entry.Waiter?.TrySetResult(batch);
            } catch (Exception error) when (error is JsonException or ProtocolException) {
                var fault = error as ProtocolException ?? new ProtocolException("Worker 响应的数据格式无效。", error);
                source.ReadEnded = true;
                var batch = new WorkerObservationBatch(source.Id, WorkerEvidenceKind.ProtocolFault, request, Error: fault);
                PublishLocked(batch);
                entry.Waiter?.TrySetResult(batch);
                throw fault;
            }
            return true;
        }
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

    private void ObserveSnapshotReply(EvidenceConnection source, RequestRoute route, WireEnvelope frame)
    {
        if (!ObservingEvidence || route.Operation != ProtocolOperations.WorkerGetSnapshot || frame.Success != true) {
            return;
        }
        if (route.Connection != source.Pipe || route.Operation != frame.Operation
            || frame.ProtocolVersion != ProtocolConstants.ProtocolVersion) {
            throw new ProtocolException("getSnapshot 响应的连接、操作或协议版本不匹配。");
        }
        var response = ProtocolJson.Deserialize<GetSnapshotResponse>(frame.Data);
        lock (_gate) {
            PublishLocked(SnapshotBatchLocked(source, response.Snapshot, full: true));
        }
    }

    private void ObserveStateFrame(EvidenceConnection source, WireEnvelope frame, StateChangedEvent state)
    {
        lock (_gate) {
            try {
                if (frame.ProtocolVersion != ProtocolConstants.ProtocolVersion || state.Snapshot?.StateRevision != state.StateRevision) {
                    throw new ProtocolException("stateChanged 的版本或修订与快照不匹配。");
                }
                PublishLocked(SnapshotBatchLocked(source, state.Snapshot, full: false));
            } catch (ProtocolException) when (!ObservingEvidence) {
                // A future first caller must re-sync. Let the existing GUI path handle this frame
                // exactly as before while it is the sole consumer.
                source.Fresh = false;
            }
        }
    }

    private WorkerObservationBatch SnapshotBatchLocked(EvidenceConnection source, WorkerSnapshot snapshot, bool full)
    {
        if (snapshot is null || snapshot.StateRevision < 0
            || snapshot.LastRun is { State: not (RunState.Succeeded or RunState.Failed or RunState.Cancelled) }
            || snapshot.ActiveRun is { State: not (RunState.Starting or RunState.Running or RunState.Stopping) }) {
            throw new ProtocolException("Worker Snapshot 状态或修订无效。");
        }
        ValidateSnapshot(snapshot, source.Admission);
        WorkerCoordinatorSnapshot? current = null;
        if (source.Pipe == _connection && AdvanceProjectionLocked(source, snapshot, full)) {
            current = new(WorkerObservation.Connected, source.Fresh, snapshot,
                source.Fresh ? "Worker 状态已同步" : "等待完整 Snapshot");
        }
        return new(source.Id, WorkerEvidenceKind.Snapshot, Current: current, ObservedSnapshot: snapshot);
    }

    private bool AdvanceProjectionLocked(EvidenceConnection source, WorkerSnapshot snapshot, bool full)
    {
        var previous = _latestEvidenceSnapshot?.StateRevision;
        if (snapshot.StateRevision < previous || !full && snapshot.StateRevision == previous) {
            return false;
        }
        source.Fresh = full || source.Fresh && snapshot.StateRevision == previous + 1;
        _latestEvidenceSnapshot = snapshot;
        return true;
    }

    private async Task ReadAndDrainAsync(EvidenceConnection source, int logGeneration, CancellationTokenSource lifetime)
    {
        Exception? failure = null;
        try {
            await ReadLoopAsync(source, logGeneration, lifetime.Token);
        } catch (Exception error) {
            failure = error;
            throw;
        } finally {
            lock (_gate) {
                source.ReadEnded = true;
                var drained = new WorkerObservationBatch(source.Id, WorkerEvidenceKind.ReaderDrained, Error: failure);
                foreach (var entry in source.Requests.Values) {
                    entry.Waiter?.TrySetResult(drained);
                }
                source.Requests.Clear();
                _evidenceConnection = null;
                PublishLocked(drained);
            }
            lifetime.Cancel();
        }
    }
}
