using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Gui.Infrastructure;
using ProjectModel;
using Protocol;

namespace Gui.Worker;

internal static partial class WorkerCoordinatorSelfTest
{
    private sealed class EvidenceFixture : IAsyncDisposable
    {
        internal readonly string PipeName = $"Gui.Evidence.{Guid.NewGuid():N}";
        private readonly Channel<WorkerObservationBatch> _observed = Channel.CreateUnbounded<WorkerObservationBatch>();
        internal readonly ConcurrentQueue<WorkerObservationBatch> History = new();
        internal readonly SelfTestRunner.ScheduleTestClock Clock = new();
        // A watchdog bounds a broken test. Only Advance drives the request deadlines.
        private readonly CancellationTokenSource _watchdog = new(TimeSpan.FromSeconds(30));
        internal CancellationToken Token => _watchdog.Token;
        internal readonly WorkerAdmissionRecord Admission;
        internal readonly WorkerCoordinator Coordinator;
        internal readonly RunStartAttempt Attempt;
        internal volatile bool ProcessAlive = true;
        private int _wireStarts;
        internal int WireStarts => Volatile.Read(ref _wireStarts);
        internal EvidenceWriteStream Server { get; private set; } = null!;
        internal RunKey Run => new(Admission.WorkerInstanceId, Attempt.RunId);
        internal Submission NewSubmission() => new(Guid.NewGuid(), Guid.NewGuid(), Run, Attempt.PlanDigest);

        internal EvidenceFixture(AppLogger logger, string directory, string projectDirectory, bool observe = true)
        {
            var project = OpenProject(projectDirectory, directory);
            Attempt = project.CreateRunStartAttempt(project.ActiveConfigurationId);
            Admission = new(Guid.NewGuid(), Convert.ToHexString(Guid.NewGuid().ToByteArray()),
                CurrentSession, Environment.ProcessId, project.RuntimeProfileDigest, DateTime.UtcNow);
            var stateDirectory = Path.Combine(directory, $"worker-evidence-{Guid.NewGuid():N}");
            new WorkerAdmissionStore(stateDirectory).SaveRecord(Admission);
            Coordinator = new(logger, stateDirectory, WorkerPath, PipeName, usePipeAcl: false,
                captureProcesses: () => ProcessAlive
                    ? [new WorkerProcessEntry(Environment.ProcessId, Admission.ChildSessionId, Path.GetFileName(WorkerPath))]
                    : [], enumerateSessions: () => [Admission.ChildSessionId], requestClock: Clock,
                wrapPipe: pipe => Server = new(pipe, () => Interlocked.Increment(ref _wireStarts)));
            if (observe) {
                Subscribe();
            }
        }

        internal void Subscribe() =>
            Coordinator.ObservationReceived += (_, batch) => {
                History.Enqueue(batch);
                _observed.Writer.TryWrite(batch);
            };

        internal async Task<ProtocolConnection> ConnectAsync(long revision = 1,
            Func<ProtocolConnection, Task>? beforeSnapshotReply = null)
        {
            await Coordinator.WaitForServerReadyAsync(Token);
            var pipe = await OpenConnectionAsync(PipeName, Admission, 0, Token, stateRevision: revision,
                beforeSnapshotReply: beforeSnapshotReply);
            await NextAsync(WorkerEvidenceKind.Snapshot);
            return pipe;
        }

        internal async Task<WorkerObservationBatch> NextAsync(WorkerEvidenceKind kind, Submission? submission = null)
        {
            while (true) {
                var batch = await _observed.Reader.ReadAsync(Token);
                if (batch.Kind == kind && (submission is null || batch.Request?.Submission == submission)) {
                    return batch;
                }
            }
        }

        internal async Task<WireEnvelope> ReadAsync(ProtocolConnection pipe, string operation)
        {
            var frame = await pipe.ReadAsync(Token) ?? throw new EndOfStreamException();
            Require(frame.MessageType == ProtocolMessageTypes.Request && frame.Operation == operation
                && frame.RequestId.HasValue, $"预期 {operation}，实际收到 {frame.Operation}。");
            if (operation == ProtocolOperations.WorkerGetSnapshot) {
                Require(ProtocolJson.Deserialize<GetSnapshotRequest>(frame.Data).RunId == Run.RunId,
                    "证据查询必须在线上带目标 runId。");
            }
            return frame;
        }

        internal async Task ExpireAsync(Task task)
        {
            Clock.Advance(TimeSpan.FromSeconds(15));
            await ExpectAsync<TimeoutException>(() => task.WaitAsync(Token), "虚拟期限到达后请求必须超时。");
        }

        internal async Task ExpireAfterWriteAsync(Task task, WireEnvelope frame)
        {
            var written = await NextAsync(WorkerEvidenceKind.WriteCompleted);
            Require(written.Request?.RequestId == frame.RequestId, "应等待对应写操作完成后再推进应答期限。");
            await ExpireAsync(task);
        }

        public async ValueTask DisposeAsync()
        {
            await Coordinator.DisposeAsync();
            _watchdog.Dispose();
        }
    }

    private static async Task VerifyInitialSyncTimeoutAsync(AppLogger logger, string directory, string project)
    {
        await using var f = new EvidenceFixture(logger, directory, project, observe: false);
        var coordinator = f.Coordinator;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        coordinator.StateChanged += (_, state) => {
            if (state.Fresh && entered.TrySetResult()) {
                release.Wait(f.Token);
            }
        };
        await coordinator.WaitForServerReadyAsync(f.Token);
        await using var peer = await ConnectRawAsync(f.PipeName, f.Token);
        await peer.WriteAsync(WireEnvelope.Request(ProtocolOperations.ConnectionOpen, Guid.NewGuid(),
            new ConnectionOpenRequest(f.Admission.WorkerInstanceId, f.Admission.LaunchToken, f.Admission.RuntimeProfileDigest)), f.Token);
        Require(await peer.ReadAsync(f.Token) is { Success: true }, "首次连接未获接纳。");
        await ReadRequestAsync(peer, ProtocolOperations.WorkerGetSnapshot, f.Token);
        var ready = CreateSnapshot(f.Admission, 0);
        await peer.WriteAsync(WireEnvelope.Event(ProtocolOperations.WorkerStateChanged,
            new StateChangedEvent(ready.WorkerInstanceId, ready.StateRevision, ready)), f.Token);
        try {
            await entered.Task.WaitAsync(f.Token);
            Require(SpinWait.SpinUntil(() => f.Clock.PendingTimers == 1, TimeSpan.FromSeconds(5)),
                "首次同步的应答期限尚未登记。");
            f.Clock.Advance(TimeSpan.FromSeconds(5));
            var pending = coordinator.StopRunAsync(f.Run.RunId, f.Token);
            await ReadRequestAsync(peer, ProtocolOperations.RunStop, f.Token);
            f.Clock.Advance(TimeSpan.FromSeconds(10));
            await ExpectAsync<OperationCanceledException>(() => pending.WaitAsync(f.Token),
                "同步超时必须立即取消尚未到期的普通请求。");
            await WaitForObservationAsync(coordinator, WorkerObservation.IpcDisconnected, f.Token);
            await ExpectAsync<InvalidOperationException>(() => coordinator.StartRunAsync(f.Attempt, f.Token),
                "旧回调未返回时不能再次开始。");
            await peer.DisposeAsync();
            f.Subscribe();
            await using var next = await f.ConnectAsync();
            var connected = f.History.Single(batch => batch.Kind == WorkerEvidenceKind.Connected);
            release.Set();
            var drained = await f.NextAsync(WorkerEvidenceKind.ReaderDrained);
            Require(drained.Connection?.Generation + 1 == connected.Connection?.Generation,
                "旧回调返回后必须独立报告原代次读端已排空。");
            var query = coordinator.QueryRunAsync(f.Run, new(Guid.NewGuid()), f.Token);
            var request = await f.ReadAsync(next, ProtocolOperations.WorkerGetSnapshot);
            await next.WriteAsync(WireEnvelope.Response(request.Operation, request.RequestId!.Value,
                new GetSnapshotResponse(ready, false)), f.Token);
            Require((await query.WaitAsync(f.Token)).Connection == connected.Connection, "旧代排空不能摘除新连接。");
        } finally {
            release.Set();
        }
    }

    private static async Task VerifyStartEvidenceRepliesAsync(AppLogger logger, string directory, string project)
    {
        await using var f = new EvidenceFixture(logger, directory, project);
        await using var worker = await f.ConnectAsync();
        var wire = f.Server;
        var coordinator = f.Coordinator;
        var first = f.NewSubmission();
        var starting = coordinator.SendStartAsync(first, f.Attempt, f.Token);
        var start = await f.ReadAsync(worker, ProtocolOperations.RunStart);
        var firstBody = await f.NextAsync(WorkerEvidenceKind.BodyStarted);
        Require(firstBody.Request?.Submission == first && firstBody.Request.RequestId == start.RequestId
            && firstBody.Connection?.WorkerInstanceId == f.Run.WorkerInstanceId
            && coordinator.EvidenceWaiterCount == 1, "正文边界必须对应原提交、连接与实际线上请求。");
        await f.ExpireAfterWriteAsync(starting, start);
        Require(coordinator.EvidenceWaiterCount == 0, "启动超时后仍持有完整等待器。");

        var queries = new List<(QueryId Id, WireEnvelope Frame)>();
        for (var i = 0; i < 32; ++i) {
            var id = new QueryId(Guid.NewGuid());
            var waiting = coordinator.QueryRunAsync(f.Run, id, f.Token);
            queries.Add((id, await f.ReadAsync(worker, ProtocolOperations.WorkerGetSnapshot)));
            Require(coordinator.EvidenceWaiterCount == 1, "查询在途时应只有自己的等待器。");
            await f.ExpireAfterWriteAsync(waiting, queries[^1].Frame);
            Require(coordinator.EvidenceWaiterCount == 0, $"第 {i + 1} 次查询超时后等待器未回落。");
        }
        Require(f.WireStarts == 1, "连续查询和超时不得重发 run.start。");
        var second = f.NewSubmission();
        var retry = coordinator.SendStartAsync(second, f.Attempt, f.Token);
        var retryFrame = await f.ReadAsync(worker, ProtocolOperations.RunStart);
        var secondBody = await f.NextAsync(WorkerEvidenceKind.BodyStarted);
        await f.ExpireAfterWriteAsync(retry, retryFrame);

        var currentId = new QueryId(Guid.NewGuid());
        var current = coordinator.QueryRunAsync(f.Run, currentId, f.Token);
        var currentFrame = await f.ReadAsync(worker, ProtocolOperations.WorkerGetSnapshot);
        var snapshot = CreateSnapshot(f.Admission, 0);
        // The old query predates the retry. Its late reply cannot resolve the current waiter.
        var old = queries[0];
        await worker.WriteAsync(WireEnvelope.Response(old.Frame.Operation, old.Frame.RequestId!.Value,
            new GetSnapshotResponse(snapshot, false)), f.Token);
        var barrier = await f.NextAsync(WorkerEvidenceKind.QueryReply);
        Require(barrier is { RunRegistered: false, Current.Fresh: true } && barrier.Request?.QueryId == old.Id
            && barrier.Request.RequestId == old.Frame.RequestId && barrier.Request.Covers(firstBody.Request!)
            && !barrier.Request.Covers(secondBody.Request!) && !current.IsCompleted && coordinator.EvidenceWaiterCount == 1,
            "旧查询只能排除其线序覆盖的发送，且不得唤醒新查询。");

        await worker.WriteAsync(WireEnvelope.Response(retryFrame.Operation, retryFrame.RequestId!.Value,
            new RunStartResponse("accepted")), f.Token);
        await worker.WriteAsync(WireEnvelope.Failure(start.Operation, start.RequestId!.Value,
            "run_id_conflict", "different digest"), f.Token);
        var accepted = await f.NextAsync(WorkerEvidenceKind.StartReply);
        var rejected = await f.NextAsync(WorkerEvidenceKind.StartReply);
        Require(accepted.StartReply == StartReplyKind.Accepted && accepted.Request == secondBody.Request
            && rejected.StartReply == StartReplyKind.RejectedBeforeRegistration && rejected.Request == firstBody.Request
            && rejected.ReplyCode == "run_id_conflict" && starting.IsFaulted && retry.IsFaulted,
            "迟到的接纳与拒绝必须独立归属，原调用保留超时。");
        await worker.WriteAsync(WireEnvelope.Response(currentFrame.Operation, currentFrame.RequestId!.Value,
            new GetSnapshotResponse(snapshot, true)), f.Token);
        await worker.DisposeAsync(); // No await on the GUI request between its reply and EOF.
        var result = await current.WaitAsync(f.Token);
        var eof = await f.NextAsync(WorkerEvidenceKind.Eof);
        var drained = await f.NextAsync(WorkerEvidenceKind.ReaderDrained);
        await wire.WireClosed.Task.WaitAsync(f.Token);
        var history = f.History.ToList();
        Require(result.Request?.QueryId == currentId && result.RunRegistered == true
            && result.Request.Covers(secondBody.Request!) && history.IndexOf(result) < history.IndexOf(eof)
            && eof.Connection == result.Connection && drained.Connection == eof.Connection
            && history.IndexOf(eof) < history.IndexOf(drained) && !coordinator.StartAwaitingObservation
            && f.WireStarts == 2, "回包应先于 EOF、读端排空，仅两次显式提交可发送 run.start。");

        await using var reconnected = await f.ConnectAsync();
        for (var round = 0; round < 3; round++) {
            WireEnvelope? abandoned = null;
            for (var i = 1; i <= 8; i++) {
                var lost = coordinator.QueryRunAsync(f.Run, new(Guid.NewGuid()), f.Token);
                abandoned = await f.ReadAsync(reconnected, ProtocolOperations.WorkerGetSnapshot);
                await f.ExpireAfterWriteAsync(lost, abandoned);
                Require(coordinator.EvidenceRequestCount == i && coordinator.EvidenceWaiterCount == 0,
                    "未过屏障时必须仍能识别迟到查询，且不持有等待器。");
            }
            var covering = coordinator.QueryRunAsync(f.Run, new(Guid.NewGuid()), f.Token);
            var frame = await f.ReadAsync(reconnected, ProtocolOperations.WorkerGetSnapshot);
            await reconnected.WriteAsync(WireEnvelope.Response(frame.Operation, frame.RequestId!.Value,
                new GetSnapshotResponse(snapshot, true)), f.Token);
            await covering.WaitAsync(f.Token);
            Require(coordinator.EvidenceRequestCount == 0, "同一连接上的有序查询应回收此前全部已超时查询。");
            await reconnected.WriteAsync(WireEnvelope.Response(abandoned!.Operation, abandoned.RequestId!.Value,
                new GetSnapshotResponse(snapshot, true)), f.Token);
            var historical = await f.NextAsync(WorkerEvidenceKind.Snapshot);
            Require(historical.ObservedSnapshot?.WorkerInstanceId == f.Run.WorkerInstanceId
                && historical.Request is null && historical.Current is null,
                "已回收查询的迟到帧可提供快照，不能再结算其他查询。");
        }
        foreach (var code in new[] { "protocol_version_mismatch", "invalid_request", "run_id_conflict",
            "worker_not_ready", "worker_faulted", "operation_not_allowed", "worker_busy", "invalid_run_plan",
            "internal_error", "unrecognised_error", "unrecognised_disposition" }) {
            var submission = f.NewSubmission();
            var call = coordinator.SendStartAsync(submission, f.Attempt, f.Token);
            var request = await f.ReadAsync(reconnected, ProtocolOperations.RunStart);
            var id = request.RequestId!.Value;
            await reconnected.WriteAsync(code == "unrecognised_disposition"
                ? WireEnvelope.Response(request.Operation, id, new RunStartResponse(code))
                : WireEnvelope.Failure(request.Operation, id, code, "scripted reply"), f.Token);
            var batch = await call.WaitAsync(f.Token);
            var expected = code is "internal_error" or "unrecognised_error" or "unrecognised_disposition"
                ? StartReplyKind.Indeterminate : StartReplyKind.RejectedBeforeRegistration;
            Require(batch.Request?.Submission == submission && batch.ReplyCode == code && batch.StartReply == expected,
                $"run.start 回复 {code} 的证据分类错误。");
        }
        var count = f.WireStarts;
        var tail = f.NewSubmission();
        var unread = coordinator.SendStartAsync(tail, f.Attempt, f.Token);
        await f.NextAsync(WorkerEvidenceKind.WriteCompleted, tail);
        Require(f.WireStarts == count + 1,
            "最后一次脚本读取之后写出的完整 run.start 也必须计数。");
        await f.ExpireAsync(unread);
        // This peer never supplies another barrier. Consume its already counted tail so that
        // subsequent reads check the query frames in order.
        await f.ReadAsync(reconnected, ProtocolOperations.RunStart);
        for (var i = 1; i < WorkerCoordinator.MaxAbandonedEvidenceRequests; i++) {
            Require(coordinator.EvidenceRequestCount == i, "无应答上限应计入所有开始和查询关联。");
            var lost = coordinator.QueryRunAsync(f.Run, new(Guid.NewGuid()), f.Token);
            var frame = await f.ReadAsync(reconnected, ProtocolOperations.WorkerGetSnapshot);
            await f.ExpireAfterWriteAsync(lost, frame);
        }
        await f.NextAsync(WorkerEvidenceKind.ReaderDrained);
        Require(coordinator.EvidenceRequestCount == 0 && f.WireStarts == count + 1,
            "持续丢应答达到上限时应排空旧连接，期间不能重发开始。");
        await reconnected.DisposeAsync();
        await using var recovered = await f.ConnectAsync();
    }

    private static async Task VerifyStartEvidenceHistoryAsync(AppLogger logger, string directory, string project)
    {
        await using var f = new EvidenceFixture(logger, directory, project);
        await using var original = await f.ConnectAsync();
        var sending = f.Coordinator.SendStartAsync(f.NewSubmission(), f.Attempt, f.Token);
        var start = await f.ReadAsync(original, ProtocolOperations.RunStart);
        var body = await f.NextAsync(WorkerEvidenceKind.BodyStarted);
        await f.ExpireAfterWriteAsync(sending, start);
        // EOF and confirmed process exit are independently observed: keep the fake process alive for reconnect.
        await original.DisposeAsync();
        await f.NextAsync(WorkerEvidenceKind.ReaderDrained);
        var snapshot = CreateSnapshot(f.Admission, 0) with { StateRevision = 5 };
        await using var worker = await f.ConnectAsync(revision: 4, beforeSnapshotReply: async pipe => {
            await pipe.WriteAsync(WireEnvelope.Event(ProtocolOperations.WorkerStateChanged,
                new StateChangedEvent(f.Run.WorkerInstanceId, snapshot.StateRevision, snapshot)), f.Token);
            var early = await f.NextAsync(WorkerEvidenceKind.Snapshot);
            Require(early.Current is { Fresh: false, WorkerSnapshot.StateRevision: 5 },
                "重连首份完整快照前，先到的新修订只能提供历史证据。");
        });
        var lowFull = f.History.Last(b => b.Kind == WorkerEvidenceKind.Snapshot);
        Require(lowFull.ObservedSnapshot?.StateRevision == 4 && lowFull.Current is null,
            "延迟的低修订完整快照不得倒退或解锁实时视图。");
        var query = f.Coordinator.QueryRunAsync(f.Run, new(Guid.NewGuid()), f.Token);
        var frame = await f.ReadAsync(worker, ProtocolOperations.WorkerGetSnapshot);
        await worker.WriteAsync(WireEnvelope.Response(frame.Operation, frame.RequestId!.Value,
            new GetSnapshotResponse(snapshot, false)), f.Token);
        var result = await query.WaitAsync(f.Token);
        Require(result.Current is { Fresh: true, WorkerSnapshot.StateRevision: 5 }
            && result.Connection!.Value.Generation > body.Connection!.Value.Generation && result.Request!.Covers(body.Request!),
            "同一 Worker 重连的定向查询必须带新代次并覆盖已封住的旧代正文。");

        var terminal = CreateActiveRun(f.Attempt) with { State = RunState.Failed };
        var low = snapshot with { StateRevision = 3, LastRun = terminal };
        Task StateAsync(WorkerSnapshot s) => worker.WriteAsync(WireEnvelope.Event(ProtocolOperations.RunStateChanged,
            new StateChangedEvent(f.Run.WorkerInstanceId, s.StateRevision, s)), f.Token);
        await StateAsync(low);
        var history = await f.NextAsync(WorkerEvidenceKind.Snapshot);
        Require(history.ObservedSnapshot?.LastRun?.RunId == f.Run.RunId && history.Current is null,
            "低修订的终态应保留作历史证据。");
        var gap = low with { StateRevision = 8 };
        await StateAsync(gap);
        var gapBatch = await f.NextAsync(WorkerEvidenceKind.Snapshot);
        Require(gapBatch.ObservedSnapshot?.LastRun?.State == RunState.Failed && gapBatch.Current is { Fresh: false },
            "断档应同时发布历史终态与实时失鲜。");
        // Legacy gap recovery is still on the wire. Retire the Admission while its full reply is held.
        var refresh = await ReadRequestAsync(worker, ProtocolOperations.WorkerGetSnapshot, f.Token);
        f.Server.StopAfterBytes = sizeof(uint);
        var prefixOnly = f.Coordinator.QueryRunAsync(f.Run, new(Guid.NewGuid()), f.Token);
        await f.Server.Blocked.Task.WaitAsync(f.Token);
        f.Coordinator.ChildSessionEnded();
        var removed = await f.NextAsync(WorkerEvidenceKind.AdmissionRemoved);
        // A prefix from a retiring writer cannot be followed by a new request. Its timeout must
        // leave the independent read direction available for the already-issued refresh and terminal.
        await f.ExpireAsync(prefixOnly);
        await worker.WriteAsync(WireEnvelope.Response(refresh.Operation, refresh.RequestId!.Value, new GetSnapshotResponse(gap)), f.Token);
        var oldFull = await f.NextAsync(WorkerEvidenceKind.Snapshot);
        await StateAsync(gap with { StateRevision = 9 });
        var oldEvent = await f.NextAsync(WorkerEvidenceKind.Snapshot);
        Require(oldFull.Connection == removed.Connection && oldEvent.Connection == removed.Connection
            && oldFull.Current is null && oldEvent.Current is null && oldFull.ObservedSnapshot?.LastRun?.RunId == f.Run.RunId
            && oldEvent.ObservedSnapshot?.LastRun?.State == RunState.Failed
            && f.Coordinator.Snapshot.Observation == WorkerObservation.ChildSessionEnded,
            "摘除 Admission 后完整回包和末帧仍应归属旧代，仅作历史证据。");
        await worker.DisposeAsync();
        var drained = await f.NextAsync(WorkerEvidenceKind.ReaderDrained);
        Require(drained.Connection == removed.Connection && f.WireStarts == 1
            && !f.History.Any(b => b.Kind == WorkerEvidenceKind.ProcessExited), "读完旧代不能替代进程退出证明或触发重发。");
    }

    private static async Task VerifyStartEvidenceFailuresAsync(AppLogger logger, string directory, string project)
    {
        await using var f = new EvidenceFixture(logger, directory, project);
        var coordinator = f.Coordinator;
        await using var first = await f.ConnectAsync();
        var oversized = f.Attempt with { RedactionValues = [new string('x', ProtocolConstants.MaximumFramePayloadBytes)] };
        var sizeError = await ExpectAsync<ProtocolException>(
            () => coordinator.SendStartAsync(f.NewSubmission(), oversized, f.Token), "超大帧应在写锁前失败。");
        var unsent = await f.NextAsync(WorkerEvidenceKind.NotSent);
        Require(ReferenceEquals(unsent.Error, sizeError) && unsent.Request?.WriteSequence is null
            && coordinator.EvidenceWaiterCount == 0, "大小检查失败必须保留原异常，且没有线序。");

        var snapshot = CreateSnapshot(f.Admission, 0);
        async Task InvalidQueryAsync(ProtocolConnection worker, object payload, bool late = false)
        {
            var queryId = new QueryId(Guid.NewGuid());
            var query = coordinator.QueryRunAsync(f.Run, queryId, f.Token);
            var request = await f.ReadAsync(worker, ProtocolOperations.WorkerGetSnapshot);
            if (late) {
                await f.ExpireAfterWriteAsync(query, request);
            }
            await worker.WriteAsync(WireEnvelope.Response(request.Operation, request.RequestId!.Value, payload), f.Token);
            if (!late) {
                await ExpectAsync<ProtocolException>(() => query.WaitAsync(f.Token), "无效的定向查询响应应触发协议错误。");
            }
            var fault = await f.NextAsync(WorkerEvidenceKind.ProtocolFault);
            var drained = await f.NextAsync(WorkerEvidenceKind.ReaderDrained);
            Require(fault.Request?.QueryId == queryId && fault.RunRegistered is null && fault.Connection == drained.Connection
                && !f.History.Any(b => b.Request?.QueryId == queryId && b.Kind == WorkerEvidenceKind.QueryReply),
                "协议故障不能发布未登记证据，故障连接必须排空。");
        }
        await InvalidQueryAsync(first, new { snapshot });
        await using var wrongType = await f.ConnectAsync();
        await InvalidQueryAsync(wrongType, new { snapshot, runRegistered = "false" }, late: true);
        await using var wrongIdentity = await f.ConnectAsync();
        await InvalidQueryAsync(wrongIdentity, new { snapshot = snapshot with { WorkerInstanceId = Guid.NewGuid() }, runRegistered = false });

        // Persist the prefix, then fail before returning to the body boundary.
        await using var prefix = await f.ConnectAsync();
        var server = f.Server;
        server.StopAfterBytes = sizeof(uint);
        server.HoldCancelledWrite = true;
        var held = f.NewSubmission();
        var beforeBody = coordinator.SendStartAsync(held, f.Attempt, f.Token);
        await server.Blocked.Task.WaitAsync(f.Token);
        var queued = coordinator.SendStartAsync(f.NewSubmission(), f.Attempt, f.Token);
        f.Clock.Advance(TimeSpan.FromSeconds(15));
        try {
            await ExpectAsync<TimeoutException>(() => queued.WaitAsync(f.Token), "等待写锁应计入期限。");
            await ExpectAsync<TimeoutException>(() => beforeBody.WaitAsync(f.Token), "前缀写者未放行时调用也必须到期。");
            Require(coordinator.EvidenceWaiterCount == 0 && !f.History.Any(b =>
                b.Request?.Submission == held && b.Kind == WorkerEvidenceKind.NotSent),
                "到期应释放两个等待器，旧写者尚未返回前不能声称 NotSent。");
        } finally {
            server.Unwind.TrySetResult();
        }
        await f.NextAsync(WorkerEvidenceKind.NotSent);
        await f.NextAsync(WorkerEvidenceKind.NotSent);
        var beforeFailures = f.History.Where(b => b.Kind == WorkerEvidenceKind.NotSent).ToArray();
        Require(beforeFailures.Length == 3 && beforeFailures.Count(b => b.Request!.WriteSequence.HasValue) == 1
            && !f.History.Any(b => b.Kind == WorkerEvidenceKind.BodyStarted), "仅有前缀和未取得写锁均为 NotSent。");
        await ExpectAsync<EndOfStreamException>(() => prefix.ReadAsync(f.Token), "前缀后必须关闭残帧连接。");
        await f.NextAsync(WorkerEvidenceKind.ReaderDrained);

        // Both writes use the ordinary request path with no write-boundary callbacks.
        await using var ordinary = await f.ConnectAsync();
        f.Server.StopAfterBytes = sizeof(uint);
        using (var cancel = CancellationTokenSource.CreateLinkedTokenSource(f.Token)) {
            var damaged = coordinator.StopRunAsync(f.Run.RunId, cancel.Token);
            await f.Server.Blocked.Task.WaitAsync(f.Token);
            var following = coordinator.StopRunAsync(f.Run.RunId, f.Token);
            cancel.Cancel();
            await ExpectAsync<OperationCanceledException>(() => damaged, "普通前缀写者应响应取消。");
            await ExpectAsync<IOException>(() => following, "排队的普通写者必须收到断管错误。");
        }
        await ExpectAsync<EndOfStreamException>(() => ordinary.ReadAsync(f.Token), "普通写者的残帧后应直接 EOF。");
        await f.NextAsync(WorkerEvidenceKind.ReaderDrained);

        // Persist a prefix and one body byte. No writer can append a request to this damaged frame.
        await using var half = await f.ConnectAsync();
        server = f.Server;
        server.StopAfterBytes = sizeof(uint) + 1;
        var submission = f.NewSubmission();
        var possible = coordinator.SendStartAsync(submission, f.Attempt, f.Token);
        await server.Blocked.Task.WaitAsync(f.Token);
        var boundary = await f.NextAsync(WorkerEvidenceKind.BodyStarted);
        await f.ExpireAsync(possible);
        var failed = f.History.Single(b => b.Kind == WorkerEvidenceKind.RequestFailed && b.Request?.Submission == submission);
        Require(boundary.Request == failed.Request && failed.Error is TimeoutException
            && !f.History.Any(b => b.Kind == WorkerEvidenceKind.NotSent && b.Request?.Submission == submission),
            "正文半帧超时必须保留可能已发送证据。");
        await ExpectAsync<EndOfStreamException>(() => half.ReadAsync(f.Token), "正文残帧后的管道必须关闭。");
        await f.NextAsync(WorkerEvidenceKind.ReaderDrained);

        await using var flush = await f.ConnectAsync();
        server = f.Server;
        server.BlockFlush = true;
        var flushing = coordinator.SendStartAsync(f.NewSubmission(), f.Attempt, f.Token);
        var complete = await f.ReadAsync(flush, ProtocolOperations.RunStart);
        await server.Blocked.Task.WaitAsync(f.Token);
        await flush.WriteAsync(WireEnvelope.Response(complete.Operation, complete.RequestId!.Value,
            new RunStartResponse("already_accepted")), f.Token);
        var replay = await f.NextAsync(WorkerEvidenceKind.StartReply);
        Require(replay.StartReply == StartReplyKind.AlreadyAccepted && !flushing.IsCompleted,
            "flush 未返回时，读线程也应独立发布已接纳证据。");
        await f.ExpireAsync(flushing);
        await f.NextAsync(WorkerEvidenceKind.ReaderDrained);

        // The old reader can drain and a new query can finish while an old prefix writer unwinds.
        await using var delayed = await f.ConnectAsync();
        var oldStream = f.Server;
        oldStream.StopAfterBytes = sizeof(uint);
        oldStream.HoldCancelledWrite = true;
        var oldSubmission = f.NewSubmission();
        var oldWriter = coordinator.SendStartAsync(oldSubmission, f.Attempt, f.Token);
        await oldStream.Blocked.Task.WaitAsync(f.Token);
        var preparing = await f.NextAsync(WorkerEvidenceKind.BeforeBody);
        await delayed.DisposeAsync();
        await f.NextAsync(WorkerEvidenceKind.ReaderDrained);
        await using var healthy = await f.ConnectAsync();
        try {
            var query = coordinator.QueryRunAsync(f.Run, new(Guid.NewGuid()), f.Token);
            var frame = await f.ReadAsync(healthy, ProtocolOperations.WorkerGetSnapshot);
            await healthy.WriteAsync(WireEnvelope.Response(frame.Operation, frame.RequestId!.Value,
                new GetSnapshotResponse(snapshot, false)), f.Token);
            var newer = await query.WaitAsync(f.Token);
            Require(!oldWriter.IsCompleted && newer.RunRegistered == false
                && !newer.Request!.Covers(preparing.Request!) && newer.Connection != preparing.Connection,
                "仍停在正文前的旧写入必须等待自己的报告。");
            f.Clock.Advance(TimeSpan.FromSeconds(15));
            await ExpectAsync<TimeoutException>(() => oldWriter.WaitAsync(f.Token), "旧代写者被扣留时调用应已超时。");
        } finally {
            oldStream.Unwind.TrySetResult();
        }
        var oldFailure = await f.NextAsync(WorkerEvidenceKind.NotSent);
        Require(oldFailure.Request?.Submission == oldSubmission && oldFailure.Connection == preparing.Connection,
            "重连查询之后的 NotSent 仍须归属旧发送。");
        f.ProcessAlive = false;
        await healthy.DisposeAsync();
        var eof = await f.NextAsync(WorkerEvidenceKind.Eof);
        var last = await f.NextAsync(WorkerEvidenceKind.ReaderDrained);
        var exited = await f.NextAsync(WorkerEvidenceKind.ProcessExited);
        Require(eof.Connection == last.Connection && last.Connection == exited.Connection && f.WireStarts == 1,
            "应分别观察 EOF、读端排空和经进程检查确认的退出。");
    }

    // Only the GUI's outgoing writes are intercepted. Admission, framing, reader, and peer EOF use a real pipe.
    private sealed class EvidenceWriteStream(Stream pipe, Action onWireStart) : Stream
    {
        internal int? StopAfterBytes;
        internal bool HoldCancelledWrite;
        internal bool BlockFlush;
        internal readonly TaskCompletionSource Blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Unwind = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource WireClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _wireGate = new();
        private bool _closed;
        private bool _forwarding;
        private readonly MemoryStream _frame = new();

        // Accumulate the bytes actually forwarded under the protocol write lock, including
        // frames the scripted peer never reads and fragments that never become a request.
        private async Task ForwardAsync(ReadOnlyMemory<byte> bytes, CancellationToken token)
        {
            lock (_wireGate) {
                ObjectDisposedException.ThrowIf(_closed, this);
                _forwarding = true;
            }
            try {
                await pipe.WriteAsync(bytes, token);
                _frame.Write(bytes.Span);
                var frame = _frame.GetBuffer().AsSpan(0, (int)_frame.Length);
                if (frame.Length >= sizeof(uint) && frame.Length - sizeof(uint) == BinaryPrimitives.ReadUInt32LittleEndian(frame)) {
                    if (JsonSerializer.Deserialize<WireEnvelope>(frame[sizeof(uint)..], ProtocolJson.Options)
                        is { MessageType: ProtocolMessageTypes.Request, Operation: ProtocolOperations.RunStart }) {
                        onWireStart();
                    }
                    _frame.SetLength(0);
                }
            } finally {
                lock (_wireGate) {
                    _forwarding = false;
                    if (_closed) {
                        WireClosed.TrySetResult();
                    }
                }
            }
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            if (StopAfterBytes is not int remaining) {
                await ForwardAsync(buffer, token);
                return;
            }
            int count = Math.Min(remaining, buffer.Length);
            await ForwardAsync(buffer[..count], token);
            StopAfterBytes = remaining - count;
            if (StopAfterBytes == 0) {
                Blocked.TrySetResult();
                try {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                } finally {
                    if (HoldCancelledWrite) {
                        await Unwind.Task;
                    }
                }
            }
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) =>
            pipe.ReadAsync(buffer, token);
        public override bool CanRead => pipe.CanRead;
        public override bool CanWrite => pipe.CanWrite;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Flush() => pipe.Flush();
        public override async Task FlushAsync(CancellationToken token)
        {
            await pipe.FlushAsync(token);
            if (BlockFlush) {
                Blocked.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
        }
        public override int Read(byte[] buffer, int offset, int count) => pipe.Read(buffer, offset, count);
        public override void Write(byte[] buffer, int offset, int count) => pipe.Write(buffer, offset, count);
        protected override void Dispose(bool disposing)
        {
            if (disposing) {
                pipe.Dispose();
                lock (_wireGate) {
                    _closed = true;
                    if (!_forwarding) {
                        WireClosed.TrySetResult();
                    }
                }
            }
            base.Dispose(disposing);
        }
    }
}
