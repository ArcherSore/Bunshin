using System.Buffers.Binary;
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
    // The server seals each old write side before admitting the next. The same Worker
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

        // The opaque wire ID keeps the connection prefix, Run slot and per-Run query number.
        // Late replies need only one validation record per Run, even across repeated barriers.
        private readonly byte[] _queryPrefix = Guid.NewGuid().ToByteArray();
        private readonly List<(Guid RunId, uint Issued)> _queryRuns = [];

        internal Guid IssueQueryId(Guid runId)
        {
            var slot = _queryRuns.FindIndex(run => run.RunId == runId);
            if (slot < 0) {
                slot = _queryRuns.Count;
                _queryRuns.Add((runId, 0));
            }
            var issued = checked(_queryRuns[slot].Issued + 1);
            _queryRuns[slot] = (runId, issued);
            BinaryPrimitives.WriteInt32LittleEndian(_queryPrefix.AsSpan(8), slot);
            BinaryPrimitives.WriteUInt32LittleEndian(_queryPrefix.AsSpan(12), issued);
            return new Guid(_queryPrefix);
        }

        internal Guid? QueryTarget(Guid id)
        {
            Span<byte> bytes = stackalloc byte[16];
            id.TryWriteBytes(bytes);
            var slot = BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]);
            var number = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]);
            return bytes[..8].SequenceEqual(_queryPrefix.AsSpan(0, 8)) && (uint)slot < _queryRuns.Count
                && number > 0 && number <= _queryRuns[slot].Issued ? _queryRuns[slot].RunId : null;
        }

        internal void ClearRequests()
        {
            Requests.Clear();
            _queryRuns.Clear();
        }
    }

    private sealed class EvidencePending(WorkerRequestEvidence request)
    {
        internal WorkerRequestEvidence Request = request;
        internal bool BodyStarted;
        internal bool FailureReported;
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

    internal int EvidenceRequestCount
    {
        get {
            lock (_gate) {
                return _evidenceConnection?.Requests.Count ?? 0;
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
            var requestId = queryId.HasValue && id.HasValue ? source!.IssueQueryId(run.RunId) : Guid.NewGuid();
            entry = new(new(requestId, run, submission, queryId, id));
            if (submission is not null) {
                PublishLocked(new(id, WorkerEvidenceKind.BeforeBody, entry.Request));
            }
        }
        var waiter = entry.Waiter!;
        Task<Exception?>? write = null;
        var deadline = timeout.Token;
        try {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(deadline, cancellationToken, _shutdown.Token);
            var token = lifetime.Token;

            lock (_gate) {
                CheckEvidenceTargetLocked(source, run);
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
            write = WriteEvidenceFrameAsync(source, entry, frame, token, deadline, cancellationToken);
            if (await write.WaitAsync(token).ConfigureAwait(false) is { } writeError) {
                ExceptionDispatchInfo.Throw(writeError);
            }
            token.ThrowIfCancellationRequested();
            var reply = await waiter.Task.WaitAsync(token).ConfigureAwait(false);
            if (reply.Kind is WorkerEvidenceKind.ProtocolFault or WorkerEvidenceKind.ReaderDrained) {
                ExceptionDispatchInfo.Throw(reply.Error ?? new EndOfStreamException("Worker 读端已排空。"));
            }
            return reply;
        } catch (Exception error) {
            var failure = RequestFailure(error, deadline, cancellationToken);
            lock (_gate) {
                entry.Waiter = null;
                if (!entry.BodyStarted) {
                    // The cancelled token is rechecked at the body boundary under this gate.
                    source?.Requests.Remove(entry.Request.RequestId);
                }
                // A stuck prefix writer owns its eventual NotSent. A deadline only ends the call.
                if (write is null || entry.BodyStarted) {
                    ReportFailureLocked(source, entry, failure);
                }
            }
            if (failure != error) {
                throw failure;
            }
            throw;
        }
    }

    private void CheckEvidenceTargetLocked(EvidenceConnection? source, RunKey run)
    {
        if (source is null || source.Pipe != _connection || source.ReadEnded
            || source.Id.WorkerInstanceId != run.WorkerInstanceId) {
            throw new InvalidOperationException("目标 Worker IPC 已失效。");
        }
    }

    private static Exception RequestFailure(Exception error, CancellationToken deadline, CancellationToken caller) =>
        error is OperationCanceledException && deadline.IsCancellationRequested && !caller.IsCancellationRequested
            ? new TimeoutException("Worker 请求在 15 秒后超时。", error) : error;

    // The writer can outlive its caller. It keeps no reply waiter or continuation back to that call.
    private async Task<Exception?> WriteEvidenceFrameAsync(EvidenceConnection source, EvidencePending entry,
        WireEnvelope frame, CancellationToken token, CancellationToken deadline, CancellationToken caller)
    {
        try {
            await source.Pipe.WriteFrameAsync(frame, token, sequence => {
                lock (_gate) {
                    CheckEvidenceTargetLocked(source, entry.Request.Run);
                    entry.Request = entry.Request with { WriteSequence = sequence };
                }
            }, () => {
                lock (_gate) {
                    token.ThrowIfCancellationRequested();
                    CheckEvidenceTargetLocked(source, entry.Request.Run);
                    entry.BodyStarted = true;
                    if (entry.Request.Submission is not null) {
                        PublishLocked(new(source.Id, WorkerEvidenceKind.BodyStarted, entry.Request));
                    }
                }
            }).ConfigureAwait(false);
            lock (_gate) {
                PublishLocked(new(source.Id, WorkerEvidenceKind.WriteCompleted, entry.Request));
            }
            return null;
        } catch (Exception error) {
            var failure = RequestFailure(error, deadline, caller);
            lock (_gate) {
                ReportFailureLocked(source, entry, failure);
            }
            return failure;
        }
    }

    private void ReportFailureLocked(EvidenceConnection? source, EvidencePending entry, Exception failure)
    {
        if (entry.FailureReported) {
            return;
        }
        entry.FailureReported = true;
        if (!entry.BodyStarted) {
            source?.Requests.Remove(entry.Request.RequestId);
        }
        var kind = entry.Request.Submission is not null && !entry.BodyStarted
            ? WorkerEvidenceKind.NotSent : WorkerEvidenceKind.RequestFailed;
        PublishLocked(new(entry.Request.Connection, kind, entry.Request, Error: failure));
    }

    private bool ReceiveEvidenceReply(EvidenceConnection source, Guid id, WireEnvelope frame)
    {
        lock (_gate) {
            source.Requests.Remove(id, out var entry);
            var request = entry?.Request;
            var target = request?.Run.RunId ?? source.QueryTarget(id);
            if (target is null) {
                return false;
            }
            try {
                var start = request?.Submission is not null;
                if (entry is { BodyStarted: false } || frame.ProtocolVersion != ProtocolConstants.ProtocolVersion
                    || frame.Operation != (start ? ProtocolOperations.RunStart : ProtocolOperations.WorkerGetSnapshot)
                    || frame.Success is null || (frame.Error is null) != frame.Success) {
                    throw new ProtocolException("Worker 响应的版本、操作或结果与请求不匹配。");
                }
                WorkerObservationBatch batch;
                if (start) {
                    batch = ReadStartReply(source.Id, request!, frame);
                } else if (frame.Success == false) {
                    if (request is null) {
                        return true; // An already superseded failed query carries no snapshot.
                    }
                    batch = new(source.Id, WorkerEvidenceKind.QueryReply, request, ReplyCode: frame.Error!.Code,
                        Error: ResponseError(frame));
                } else {
                    batch = ReadQuerySnapshotLocked(source, request, target.Value, frame);
                }
                PublishLocked(batch);
                entry?.Waiter?.TrySetResult(batch);
            } catch (Exception error) when (error is JsonException or ProtocolException) {
                var fault = error as ProtocolException ?? new ProtocolException("Worker 响应的数据格式无效。", error);
                source.ReadEnded = true;
                var batch = new WorkerObservationBatch(source.Id, WorkerEvidenceKind.ProtocolFault, request, Error: fault);
                PublishLocked(batch);
                entry?.Waiter?.TrySetResult(batch);
                throw fault;
            }
            return true;
        }
    }

    private WorkerObservationBatch ReadQuerySnapshotLocked(EvidenceConnection source, WorkerRequestEvidence? request,
        Guid targetRunId, WireEnvelope frame)
    {
        var response = ProtocolJson.Deserialize<GetSnapshotResponse>(frame.Data);
        if (response.RunRegistered is not bool registered || response.Snapshot is null
            || !registered && (response.Snapshot.ActiveRun?.RunId == targetRunId
                || response.Snapshot.LastRun?.RunId == targetRunId)) {
            throw new ProtocolException("定向 getSnapshot 缺少有效的 runRegistered 或与快照冲突。");
        }
        var batch = SnapshotBatchLocked(source, response.Snapshot, full: true, historicalOnly: request is null);
        if (request is null) {
            return batch;
        }
        // Only queries for this Run are superseded. A start still owns its own late disposition.
        foreach (var prior in source.Requests.Values) {
            if (prior.Waiter is null && prior.Request.QueryId.HasValue && prior.Request.Run == request.Run
                && prior.Request.WriteSequence < request.WriteSequence) {
                source.Requests.Remove(prior.Request.RequestId);
            }
        }
        return batch with { Kind = WorkerEvidenceKind.QueryReply, Request = request, RunRegistered = registered };
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

    private WorkerObservationBatch SnapshotBatchLocked(EvidenceConnection source, WorkerSnapshot snapshot, bool full,
        bool historicalOnly = false)
    {
        if (snapshot is null || snapshot.StateRevision < 0
            || snapshot.LastRun is { State: not (RunState.Succeeded or RunState.Failed or RunState.Cancelled) }
            || snapshot.ActiveRun is { State: not (RunState.Starting or RunState.Running or RunState.Stopping) }) {
            throw new ProtocolException("Worker Snapshot 状态或修订无效。");
        }
        ValidateSnapshot(snapshot, source.Admission);
        WorkerCoordinatorSnapshot? current = null;
        if (!historicalOnly && source.Pipe == _connection && AdvanceProjectionLocked(source, snapshot, full)) {
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
                source.ClearRequests();
                if (_evidenceConnection == source) {
                    _evidenceConnection = null;
                }
                PublishLocked(drained);
            }
            lifetime.Cancel();
        }
    }
}
