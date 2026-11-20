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

        internal Task HoldStateCallback(long revision, ManualResetEventSlim release)
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Coordinator.StateChanged += (_, state) => {
                if (state.Fresh && state.WorkerSnapshot?.StateRevision == revision && entered.TrySetResult()) {
                    release.Wait(Token);
                }
            };
            return entered.Task;
        }

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
        await InitialSyncTimeoutAsync(logger, directory, project, removeAdmission: false);
        await InitialSyncTimeoutAsync(logger, directory, project, removeAdmission: true);
    }

    private static async Task InitialSyncTimeoutAsync(AppLogger logger, string directory, string project, bool removeAdmission)
    {
        await using var f = new EvidenceFixture(logger, directory, project, observe: removeAdmission);
        var coordinator = f.Coordinator;
        if (removeAdmission) {
            await using var original = await f.ConnectAsync();
            await original.DisposeAsync();
            await f.NextAsync(WorkerEvidenceKind.ReaderDrained);
        }
        using var release = new ManualResetEventSlim();
        var ready = CreateSnapshot(f.Admission, 0) with { StateRevision = 2 };
        var entered = f.HoldStateCallback(ready.StateRevision, release);
        await coordinator.WaitForServerReadyAsync(f.Token);
        await using var peer = await ConnectRawAsync(f.PipeName, f.Token);
        await peer.WriteAsync(WireEnvelope.Request(ProtocolOperations.ConnectionOpen, Guid.NewGuid(),
            new ConnectionOpenRequest(f.Admission.WorkerInstanceId, f.Admission.LaunchToken, f.Admission.RuntimeProfileDigest)), f.Token);
        Require(await peer.ReadAsync(f.Token) is { Success: true }, "首次连接未获接纳。");
        await ReadRequestAsync(peer, ProtocolOperations.WorkerGetSnapshot, f.Token);
        await peer.WriteAsync(WireEnvelope.Event(ProtocolOperations.WorkerStateChanged,
            new StateChangedEvent(ready.WorkerInstanceId, ready.StateRevision, ready)), f.Token);
        try {
            await entered.WaitAsync(f.Token);
            Require(SpinWait.SpinUntil(() => f.Clock.PendingTimers == 1, TimeSpan.FromSeconds(5)),
                "首次同步的应答期限尚未登记。");
            f.Clock.Advance(TimeSpan.FromSeconds(5));
            var pending = coordinator.StopRunAsync(f.Run.RunId, f.Token);
            var stop = await ReadRequestAsync(peer, ProtocolOperations.RunStop, f.Token);
            await peer.WriteAsync(WireEnvelope.Response(stop.Operation, stop.RequestId!.Value,
                new RunStopResponse("accepted")), f.Token);
            var terminal = ready with { StateRevision = 3, LastRun = CreateActiveRun(f.Attempt) with { State = RunState.Failed } };
            await peer.WriteAsync(WireEnvelope.Event(ProtocolOperations.RunStateChanged,
                new StateChangedEvent(terminal.WorkerInstanceId, terminal.StateRevision, terminal)), f.Token);
            await peer.DisposeAsync();
            if (removeAdmission) {
                f.ProcessAlive = false;
                coordinator.ChildSessionEnded(f.Admission);
                var exited = await f.NextAsync(WorkerEvidenceKind.ProcessExited);
                var removed = await f.NextAsync(WorkerEvidenceKind.AdmissionRemoved);
                Require(exited.Connection == removed.Connection && removed.Connection?.Generation == 2,
                    "确认退出并摘除的必须是仍有末帧待处理的重连代次。");
            }
            f.Clock.Advance(TimeSpan.FromSeconds(10));
            await ExpectAsync<OperationCanceledException>(() => pending.WaitAsync(f.Token),
                "同步超时必须立即取消尚未到期的普通请求。");
            await WaitForObservationAsync(coordinator, removeAdmission ? WorkerObservation.ChildSessionEnded
                : WorkerObservation.IpcDisconnected, f.Token);
            await ExpectAsync<InvalidOperationException>(() => coordinator.StartRunAsync(f.Attempt, f.Token),
                "旧回调未返回时不能再次开始。");
            if (!removeAdmission) {
                f.Subscribe();
            }
            release.Set();
            var last = await f.NextAsync(WorkerEvidenceKind.Snapshot);
            var eof = await f.NextAsync(WorkerEvidenceKind.Eof);
            var drained = await f.NextAsync(WorkerEvidenceKind.ReaderDrained);
            Require(last.ObservedSnapshot?.LastRun?.RunId == f.Run.RunId && last.Current is null
                && last.Connection == eof.Connection && eof.Connection == drained.Connection && drained.Error is null,
                "超时前已入管的终态必须归属旧代，先于 EOF 与排空发布。");
            if (removeAdmission) {
                return;
            }
            await using var next = await f.ConnectAsync();
            var connected = f.History.Single(batch => batch.Kind == WorkerEvidenceKind.Connected);
            Require(drained.Connection?.Generation + 1 == connected.Connection?.Generation, "应接纳同一 Worker 重连。");
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
        var otherRun = f.Run with { RunId = Guid.NewGuid() };
        var terminal = snapshot with { LastRun = CreateActiveRun(f.Attempt) with { State = RunState.Failed } };
        for (var round = 0; round < 3; round++) {
            WireEnvelope? abandoned = null;
            for (var i = 1; i <= 8; i++) {
                var lost = coordinator.QueryRunAsync(f.Run, new(Guid.NewGuid()), f.Token);
                abandoned = await f.ReadAsync(reconnected, ProtocolOperations.WorkerGetSnapshot);
                await f.ExpireAfterWriteAsync(lost, abandoned);
                Require(coordinator.EvidenceRequestCount == i && coordinator.EvidenceWaiterCount == 0,
                    "未过屏障时必须仍能识别迟到查询，且不持有等待器。");
            }
            var other = coordinator.QueryRunAsync(otherRun, new(Guid.NewGuid()), f.Token);
            var otherFrame = await ReadRequestAsync(reconnected, ProtocolOperations.WorkerGetSnapshot, f.Token);
            await reconnected.WriteAsync(WireEnvelope.Response(otherFrame.Operation, otherFrame.RequestId!.Value,
                new GetSnapshotResponse(terminal, false)), f.Token);
            await other.WaitAsync(f.Token);
            Require(coordinator.EvidenceRequestCount == 8, "另一 Run 的有序查询不能回收本 Run 的超时关联。");
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
        var tailFrame = await f.ReadAsync(reconnected, ProtocolOperations.RunStart);
        for (var i = 1; i < 127; i++) {
            var lost = coordinator.QueryRunAsync(f.Run, new(Guid.NewGuid()), f.Token);
            var frame = await f.ReadAsync(reconnected, ProtocolOperations.WorkerGetSnapshot);
            await f.ExpireAfterWriteAsync(lost, frame);
        }
        Require(coordinator.EvidenceRequestCount == 127, "扣住读线程前应有 127 项未获应答。");
        using var release = new ManualResetEventSlim();
        var entered = f.HoldStateCallback(2, release);
        await reconnected.WriteAsync(WireEnvelope.Event(ProtocolOperations.WorkerStateChanged,
            new StateChangedEvent(f.Run.WorkerInstanceId, 2, snapshot with { StateRevision = 2 })), f.Token);
        try {
            await entered.WaitAsync(f.Token);
            await reconnected.WriteAsync(WireEnvelope.Response(tailFrame.Operation, tailFrame.RequestId!.Value,
                new RunStartResponse("accepted")), f.Token);
            var lost = coordinator.QueryRunAsync(f.Run, new(Guid.NewGuid()), f.Token);
            await f.ExpireAfterWriteAsync(lost, await f.ReadAsync(reconnected, ProtocolOperations.WorkerGetSnapshot));
            Require(coordinator.EvidenceRequestCount == 128, "接纳回包尚在管道中，第 128 项应独立超时。");
        } finally {
            release.Set();
        }
        var late = await f.NextAsync(WorkerEvidenceKind.StartReply, tail);
        Require(late.StartReply == StartReplyKind.Accepted, "第 128 项超时后仍须发布已经到达的 accepted。");
        var finalQuery = coordinator.QueryRunAsync(f.Run, new(Guid.NewGuid()), f.Token);
        var finalFrame = await f.ReadAsync(reconnected, ProtocolOperations.WorkerGetSnapshot);
        await reconnected.WriteAsync(WireEnvelope.Response(finalFrame.Operation, finalFrame.RequestId!.Value,
            new GetSnapshotResponse(snapshot, true)), f.Token);
        await finalQuery.WaitAsync(f.Token);
        var wireEnd = f.Server.WireClosed.Task;
        await reconnected.DisposeAsync();
        var end = await f.NextAsync(WorkerEvidenceKind.ReaderDrained);
        await wireEnd.WaitAsync(f.Token);
        Require(end.Connection == late.Connection && coordinator.EvidenceRequestCount == 0 && f.WireStarts == count + 1,
            "旧代应收齐接纳、完成后续查询并排空，全程仅显式提交写出了 run.start。");
    }

    private static async Task VerifyAbandonedLogRetriesAsync(AppLogger logger, string directory, string project)
    {
        await using var f = new EvidenceFixture(logger, directory, project);
        await using var worker = await f.ConnectAsync();
        var received = new ConcurrentQueue<WorkerLogEntry>();
        f.Coordinator.LogReceived += (_, entry) => received.Enqueue(entry);
        var first = CreateEntry(1, ProtocolConstants.RunLogSource, "first", DateTime.UtcNow);
        var second = first with { Sequence = 2, Message = "second" };
        await worker.WriteAsync(CreateLogEvent(f.Run.WorkerInstanceId, second), f.Token);
        for (var i = 0; i < 3; i++) {
            await ReadRequestAsync(worker, ProtocolOperations.LogGetSince, f.Token);
            var expectedTimers = i == 0 ? 1 : 2; // Current reply deadline, plus the previous route's expiry.
            Require(SpinWait.SpinUntil(() => f.Clock.PendingTimers == expectedTimers, TimeSpan.FromSeconds(5)),
                "日志补取重试的应答期限应已登记。");
            f.Clock.Advance(TimeSpan.FromSeconds(15));
            Require(SpinWait.SpinUntil(() => f.Coordinator.AbandonedRequestCount == 1 && f.Clock.PendingTimers == 1,
                TimeSpan.FromSeconds(5)), "证据订阅期间只应保留最近一次超时的普通请求。");
        }
        var retry = await ReadRequestAsync(worker, ProtocolOperations.LogGetSince, f.Token);
        await WriteLogPageAsync(worker, retry, [first, second], 2, f.Token);
        await WaitForCountAsync(received, 2, f.Token);
        f.Clock.Advance(TimeSpan.FromSeconds(15));
        Require(SpinWait.SpinUntil(() => f.Coordinator.AbandonedRequestCount == 0, TimeSpan.FromSeconds(5))
            && f.History.Count(b => b.Kind == WorkerEvidenceKind.Connected) == 1,
            "日志恢复后，最后一个普通超时关联也应在同一条活连接上过期。");
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
        f.Server.StopAfterBytes = 1;
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
        async Task InvalidQueryAsync(ProtocolConnection worker, object payload, bool late = false, bool supersede = false)
        {
            var queryId = new QueryId(Guid.NewGuid());
            var query = coordinator.QueryRunAsync(f.Run, queryId, f.Token);
            var request = await f.ReadAsync(worker, ProtocolOperations.WorkerGetSnapshot);
            if (late) {
                await f.ExpireAfterWriteAsync(query, request);
            }
            if (supersede) {
                var newer = coordinator.QueryRunAsync(f.Run, new(Guid.NewGuid()), f.Token);
                var frame = await f.ReadAsync(worker, ProtocolOperations.WorkerGetSnapshot);
                await worker.WriteAsync(WireEnvelope.Response(frame.Operation, frame.RequestId!.Value,
                    new GetSnapshotResponse(snapshot, true)), f.Token);
                await newer.WaitAsync(f.Token);
                Require(coordinator.EvidenceRequestCount == 0, "矛盾回包到达前 Q2 必须已回收 Q1。");
            }
            await worker.WriteAsync(WireEnvelope.Response(request.Operation, request.RequestId!.Value, payload), f.Token);
            if (!late) {
                await ExpectAsync<ProtocolException>(() => query.WaitAsync(f.Token), "无效的定向查询响应应触发协议错误。");
            }
            var fault = await f.NextAsync(WorkerEvidenceKind.ProtocolFault);
            var drained = await f.NextAsync(WorkerEvidenceKind.ReaderDrained);
            Require((supersede ? fault.Request is null : fault.Request?.QueryId == queryId)
                && fault.RunRegistered is null && fault.Connection == drained.Connection
                && !f.History.Any(b => b.Request?.QueryId == queryId && b.Kind == WorkerEvidenceKind.QueryReply
                    || b.Connection == fault.Connection && b.ObservedSnapshot?.StateRevision == 10),
                "协议故障不能发布未登记证据，故障连接必须排空。");
        }
        await InvalidQueryAsync(first, new { snapshot });
        await using var wrongType = await f.ConnectAsync();
        await InvalidQueryAsync(wrongType, new { snapshot, runRegistered = "false" }, late: true);
        await using var wrongIdentity = await f.ConnectAsync();
        await InvalidQueryAsync(wrongIdentity, new { snapshot = snapshot with { WorkerInstanceId = Guid.NewGuid() }, runRegistered = false });
        var active = CreateActiveRun(f.Attempt);
        foreach (var conflicting in new[] {
            snapshot with { StateRevision = 10, ActiveRun = active, RunState = active.State },
            snapshot with { StateRevision = 10, LastRun = active with { State = RunState.Failed } }
        }) {
            await using var conflict = await f.ConnectAsync();
            await InvalidQueryAsync(conflict, new GetSnapshotResponse(conflicting, false), late: true, supersede: true);
        }

        // Persist one prefix byte and hold the writer before the body boundary.
        await using var prefix = await f.ConnectAsync();
        var server = f.Server;
        server.StopAfterBytes = 1;
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
        await ExpectAsync<IOException>(() => coordinator.StopRunAsync(f.Run.RunId, f.Token), "半个前缀必须封住写端。");
        Require(server.PartialFrameBytes == 1, "半帧后不能追加字节。");
        await prefix.DisposeAsync();
        await f.NextAsync(WorkerEvidenceKind.ReaderDrained);

        // Both writes use the ordinary request path with no write-boundary callbacks.
        foreach (var written in new[] { 0, sizeof(uint) }) {
            await using var ordinary = await f.ConnectAsync();
            f.Server.StopAfterBytes = written;
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(f.Token);
            var stopped = coordinator.StopRunAsync(f.Run.RunId, cancel.Token);
            await f.Server.Blocked.Task.WaitAsync(f.Token);
            Require(f.Server.PartialFrameBytes == written, "应在指定的前缀字节数上取消持锁写者。");
            var following = coordinator.StopRunAsync(f.Run.RunId, f.Token);
            cancel.Cancel();
            await ExpectAsync<OperationCanceledException>(() => stopped, "普通写者应保留调用方取消。");
            if (written == 0) {
                var frame = await f.ReadAsync(ordinary, ProtocolOperations.RunStop);
                await ordinary.WriteAsync(WireEnvelope.Response(frame.Operation, frame.RequestId!.Value,
                    new RunStopResponse("accepted")), f.Token);
                Require((await following.WaitAsync(f.Token)).Disposition == "accepted", "零字节取消后排队请求应成功。");
            } else {
                await ExpectAsync<IOException>(() => following, "半帧之后排队的普通写者应失败。");
                Require(f.Server.PartialFrameBytes == written, "排队的普通写者不能追加任何字节。");
                await ordinary.WriteAsync(WireEnvelope.Event(ProtocolOperations.RunStateChanged,
                    new StateChangedEvent(f.Run.WorkerInstanceId, snapshot.StateRevision,
                        snapshot with { LastRun = active with { State = RunState.Failed } })), f.Token);
                var inbound = await f.NextAsync(WorkerEvidenceKind.Snapshot);
                Require(inbound.ObservedSnapshot?.LastRun?.RunId == f.Run.RunId, "写端半帧不能阻断读端。");
            }
            await ordinary.DisposeAsync();
            await f.NextAsync(WorkerEvidenceKind.ReaderDrained);
        }

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
        await ExpectAsync<IOException>(() => coordinator.StopRunAsync(f.Run.RunId, f.Token), "正文半帧后必须封写。");
        await half.DisposeAsync();
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
        server.BlockFlush = false;
        var afterFlush = coordinator.StopRunAsync(f.Run.RunId, f.Token);
        var stopFrame = await f.ReadAsync(flush, ProtocolOperations.RunStop);
        await flush.WriteAsync(WireEnvelope.Response(stopFrame.Operation, stopFrame.RequestId!.Value,
            new RunStopResponse("accepted")), f.Token);
        await afterFlush.WaitAsync(f.Token);
        await flush.DisposeAsync();
        await f.NextAsync(WorkerEvidenceKind.ReaderDrained);

        // The old reader can drain and a new query can finish while an old prefix writer unwinds.
        await using var delayed = await f.ConnectAsync();
        var oldStream = f.Server;
        oldStream.StopAfterBytes = 1;
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
        internal long PartialFrameBytes => _frame.Length;

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
            StopAfterBytes = remaining - count;
            if (count > 0) {
                await ForwardAsync(buffer[..count], token);
            }
            if (count == buffer.Length) {
                return;
            }
            StopAfterBytes = null;
            Blocked.TrySetResult();
            try {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            } finally {
                if (HoldCancelledWrite) {
                    await Unwind.Task;
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
