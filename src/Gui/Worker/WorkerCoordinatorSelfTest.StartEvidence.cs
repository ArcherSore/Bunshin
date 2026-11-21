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
        private int _wireStarts;
        internal int WireStarts => Volatile.Read(ref _wireStarts);
        internal EvidenceWriteStream Server { get; private set; } = null!;
        internal RunKey Run => new(Admission.WorkerInstanceId, Attempt.RunId);
        internal Submission NewSubmission() => new(Guid.NewGuid(), Guid.NewGuid(), Run, Attempt.PlanDigest);

        internal EvidenceFixture(AppLogger logger, string directory, string projectDirectory)
        {
            var project = OpenProject(projectDirectory, directory);
            Attempt = project.CreateRunStartAttempt(project.ActiveConfigurationId);
            Admission = new(Guid.NewGuid(), Convert.ToHexString(Guid.NewGuid().ToByteArray()),
                CurrentSession, Environment.ProcessId, project.RuntimeProfileDigest, DateTime.UtcNow);
            var stateDirectory = Path.Combine(directory, $"worker-evidence-{Guid.NewGuid():N}");
            new WorkerAdmissionStore(stateDirectory).SaveRecord(Admission);
            Coordinator = new(logger, stateDirectory, WorkerPath, PipeName, usePipeAcl: false,
                captureProcesses: () => [new WorkerProcessEntry(Environment.ProcessId, Admission.ChildSessionId, Path.GetFileName(WorkerPath))],
                enumerateSessions: () => [Admission.ChildSessionId], requestClock: Clock,
                wrapPipe: pipe => Server = new(pipe, () => Interlocked.Increment(ref _wireStarts)));
            Coordinator.ObservationReceived += (_, batch) => {
                History.Enqueue(batch);
                _observed.Writer.TryWrite(batch);
            };
        }

        internal async Task<ProtocolConnection> ConnectAsync(long revision = 1)
        {
            await Coordinator.WaitForServerReadyAsync(Token);
            var pipe = await OpenConnectionAsync(PipeName, Admission, 0, Token, stateRevision: revision);
            await NextAsync(WorkerEvidenceKind.Snapshot);
            return pipe;
        }

        internal async Task<WireEnvelope> BeginSyncAsync(ProtocolConnection peer)
        {
            await peer.WriteAsync(WireEnvelope.Request(ProtocolOperations.ConnectionOpen, Guid.NewGuid(),
                new ConnectionOpenRequest(Admission.WorkerInstanceId, Admission.LaunchToken, Admission.RuntimeProfileDigest)), Token);
            Require(await peer.ReadAsync(Token) is { Success: true }, "握手应接纳同一 PID。");
            return await ReadRequestAsync(peer, ProtocolOperations.WorkerGetSnapshot, Token);
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

        public async ValueTask DisposeAsync()
        {
            await Coordinator.DisposeAsync();
            _watchdog.Dispose();
        }
    }

    private static async Task VerifyStartEvidenceSyncReconnectAsync(AppLogger logger, string directory, string project)
    {
        await using var f = new EvidenceFixture(logger, directory, project);
        await f.Coordinator.WaitForServerReadyAsync(f.Token);
        await using var peer = await ConnectRawAsync(f.PipeName, f.Token);
        await f.BeginSyncAsync(peer);
        var oldReader = f.Coordinator.ConnectionReader;
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Coordinator.StateChanged += (_, state) => {
            if (state.Fresh && state.WorkerSnapshot?.StateRevision == 2 && entered.TrySetResult()) {
                release.Wait(f.Token);
            }
        };
        var snapshot = CreateSnapshot(f.Admission, 0) with { StateRevision = 2 };
        await peer.WriteAsync(WireEnvelope.Event(ProtocolOperations.WorkerStateChanged,
            new StateChangedEvent(f.Run.WorkerInstanceId, 2, snapshot)), f.Token);
        try {
            await entered.Task.WaitAsync(f.Token);
            Require(!oldReader.IsCompleted, "阻塞的状态回调应属于仍在执行的旧读循环。");
            // Queue a frame while the old reader is still inside the callback.
            var stale = snapshot with { StateRevision = 3 };
            await peer.WriteAsync(WireEnvelope.Event(ProtocolOperations.RunStateChanged,
                new StateChangedEvent(f.Run.WorkerInstanceId, 3, stale)), f.Token);
            Require(SpinWait.SpinUntil(() => f.Clock.PendingTimers == 1, TimeSpan.FromSeconds(5)),
                "等待首次快照的虚拟定时器尚未登记。");
            f.Clock.Advance(TimeSpan.FromSeconds(15));
            var lost = await f.NextAsync(WorkerEvidenceKind.Disconnected);
            Require(lost.Error is TimeoutException && await peer.ReadAsync(f.Token) is null,
                "存活 Worker 必须直接观察到首次同步超时造成的 EOF。");
            await peer.DisposeAsync(); // The Worker closes its client only after the server has disconnected it.
            await using var next = await f.ConnectAsync(4);
            var fresh = f.History.Last(b => b.Kind == WorkerEvidenceKind.Snapshot);
            Require(fresh.Current?.Fresh == true && fresh.ObservedSnapshot?.WorkerPid == f.Admission.WorkerPid
                && fresh.Connection?.WorkerInstanceId == lost.Connection?.WorkerInstanceId
                && fresh.Connection?.Generation > lost.Connection?.Generation, "原 PID 应在旧回调返回前完成重连同步。");
            release.Set();
            await oldReader.WaitAsync(f.Token);
            var query = f.Coordinator.QueryRunAsync(f.Run, new(Guid.NewGuid()), f.Token);
            var frame = await f.ReadAsync(next, ProtocolOperations.WorkerGetSnapshot);
            await next.WriteAsync(WireEnvelope.Response(frame.Operation, frame.RequestId!.Value,
                new GetSnapshotResponse(snapshot with { StateRevision = 4 }, false)), f.Token);
            Require((await query.WaitAsync(f.Token)).Connection == fresh.Connection && f.WireStarts == 0
                && !f.History.Any(b => b.ObservedSnapshot?.StateRevision == 3), "重连应能查询，旧连接的缓存帧不能再发布。");
        } finally {
            release.Set();
        }
    }

    private static async Task VerifyInitialSnapshotBeforeEofAsync(AppLogger logger, string directory, string project)
    {
        await using var f = new EvidenceFixture(logger, directory, project);
        var active = CreateActiveRun(f.Attempt);
        await f.Coordinator.WaitForServerReadyAsync(f.Token);
        await using (var first = await OpenConnectionAsync(f.PipeName, f.Admission, 0, f.Token, activeRun: active)) {
            Require((await f.NextAsync(WorkerEvidenceKind.Snapshot)).Current?.WorkerSnapshot?.ActiveRun?.RunId == active.RunId,
                "断线前应已观察到活动 Run。");
        }
        await f.NextAsync(WorkerEvidenceKind.Disconnected);

        var releaseFlush = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminal = new ConcurrentQueue<Guid>();
        f.Coordinator.StateChanged += (_, state) => {
            if (state.Resynchronising) {
                // The peer can reply before the sender resumes from its completed pipe write.
                f.Server.HoldNextFlush = releaseFlush.Task;
            }
            if (state.Fresh && state.WorkerSnapshot?.LastRun is { State: RunState.Succeeded } last) {
                terminal.Enqueue(last.RunId);
            }
        };
        try {
            await using var peer = await OpenConnectionAsync(f.PipeName, f.Admission, 0, f.Token,
                lastRun: active with { State = RunState.Succeeded }, stateRevision: 2);
            var server = f.Server;
            await peer.DisposeAsync();
            await server.WireClosed.Task.WaitAsync(f.Token);
            Require(terminal.SequenceEqual([active.RunId]), "首次快照应在紧随其后的 EOF 前发布原 Run 的成功终态。");
        } finally {
            releaseFlush.TrySetResult();
        }
        await f.NextAsync(WorkerEvidenceKind.Disconnected);
    }

    private static async Task VerifyInitialLogPageBeforeEofAsync(AppLogger logger, string directory, string project)
    {
        await using var f = new EvidenceFixture(logger, directory, project);
        using var releaseFirst = new ManualResetEventSlim();
        var firstPublished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sequences = new ConcurrentQueue<long>();
        f.Coordinator.LogReceived += (_, entry) => {
            sequences.Enqueue(entry.Sequence);
            if (entry.Sequence == 1) {
                firstPublished.TrySetResult();
                releaseFirst.Wait(f.Token);
            }
        };
        await f.Coordinator.WaitForServerReadyAsync(f.Token);
        await using var peer = await OpenConnectionAsync(f.PipeName, f.Admission, 3, f.Token);
        var request = await ReadRequestAsync(peer, ProtocolOperations.LogGetSince, f.Token);
        var entries = Enumerable.Range(1, 3).Select(i => CreateEntry(i, ProtocolConstants.RunLogSource, $"log {i}", DateTime.UtcNow)).ToArray();
        try {
            await WriteLogPageAsync(peer, request, entries, 3, f.Token);
            await firstPublished.Task.WaitAsync(f.Token);
            var server = f.Server;
            await peer.DisposeAsync();
            await server.WireClosed.Task.WaitAsync(f.Token);
        } finally {
            releaseFirst.Set();
        }
        await f.NextAsync(WorkerEvidenceKind.Disconnected);
        Require(sequences.SequenceEqual([1L, 2L, 3L]), "首次同步已开始发布的日志页应在断线清理前按序发完。");
    }

    private static async Task VerifyLiveLogPageBeforeInitialSyncEofAsync(AppLogger logger, string directory, string project)
    {
        await using var f = new EvidenceFixture(logger, directory, project);
        await f.Coordinator.WaitForServerReadyAsync(f.Token);
        await using var peer = await ConnectRawAsync(f.PipeName, f.Token);
        var initial = await f.BeginSyncAsync(peer);
        Require(SpinWait.SpinUntil(() => f.Clock.PendingTimers == 1, TimeSpan.FromSeconds(5)),
            "首次快照请求应已写完并在等待回包。");
        var server = f.Server;
        var releasePage = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.HoldNextFlush = releasePage.Task;
        var sequences = new ConcurrentQueue<long>();
        f.Coordinator.LogReceived += (_, entry) => sequences.Enqueue(entry.Sequence);
        var entries = Enumerable.Range(1, 3).Select(i => CreateEntry(i, ProtocolConstants.RunLogSource,
            $"log {i}", DateTime.UtcNow)).ToArray();
        await peer.WriteAsync(CreateLogEvent(f.Run.WorkerInstanceId, entries[^1]), f.Token);
        var page = await ReadRequestAsync(peer, ProtocolOperations.LogGetSince, f.Token);

        // Hold the successful snapshot sender until the reader has processed both replies and EOF.
        // The page reply is already on the GUI, but its sender has yet to return from FlushAsync.
        void WaitForEof(object? sender, Gui.Models.LogEntry entry)
        {
            if (entry.Message.Contains("Worker 请求完成") && entry.Message.Contains(initial.Operation)) {
                server.WireClosed.Task.Wait(f.Token);
            }
        }
        logger.EntryWritten += WaitForEof;
        f.Coordinator.FrameworkOptions = () => {
            releasePage.TrySetResult();
            return new();
        };
        var atDisconnect = new TaskCompletionSource<long[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Coordinator.ObservationReceived += (_, batch) => {
            if (batch.Kind == WorkerEvidenceKind.Disconnected) {
                atDisconnect.TrySetResult(sequences.ToArray());
                releasePage.TrySetResult();
            }
        };
        try {
            await WriteLogPageAsync(peer, page, entries, 3, f.Token);
            await peer.WriteAsync(WireEnvelope.Response(initial.Operation, initial.RequestId!.Value,
                new GetSnapshotResponse(CreateSnapshot(f.Admission, 3))), f.Token);
            await peer.DisposeAsync();
            Require(await atDisconnect.Task.WaitAsync(f.Token) is [1L, 2L, 3L],
                "实时日志先启动的补页应在断线通知前按序发完。");
        } finally {
            releasePage.TrySetResult();
            logger.EntryWritten -= WaitForEof;
        }
    }

    private static async Task VerifyStartEvidenceRevisionOrderAsync(AppLogger logger, string directory, string project)
    {
        await using var f = new EvidenceFixture(logger, directory, project);
        var active = CreateActiveRun(f.Attempt);
        await f.Coordinator.WaitForServerReadyAsync(f.Token);
        await using (var first = await OpenConnectionAsync(f.PipeName, f.Admission, 0, f.Token, activeRun: active)) {
            await f.NextAsync(WorkerEvidenceKind.Snapshot);
        }
        await f.NextAsync(WorkerEvidenceKind.Disconnected);
        await using var peer = await ConnectRawAsync(f.PipeName, f.Token);
        var initial = await f.BeginSyncAsync(peer);
        var ready = CreateSnapshot(f.Admission, 0) with { StateRevision = 3 };
        var running = CreateSnapshot(f.Admission, 0, active) with { StateRevision = 2 };
        await peer.WriteAsync(WireEnvelope.Event(ProtocolOperations.WorkerStateChanged,
            new StateChangedEvent(f.Run.WorkerInstanceId, 3, ready)), f.Token);
        var latest = await f.NextAsync(WorkerEvidenceKind.Snapshot);
        Require(latest.Current is { FreshReadyToStart: true, WorkerSnapshot.StateRevision: 3 },
            "重连后较高修订的就绪事件应更新生产快照。");
        var initialStart = StartDuringSync(out var initialStarts);
        await peer.WriteAsync(WireEnvelope.Response(initial.Operation, initial.RequestId!.Value,
            new GetSnapshotResponse(running)), f.Token);
        var older = await f.NextAsync(WorkerEvidenceKind.Snapshot);
        Require(older.ObservedSnapshot?.StateRevision == 2 && older.Current == latest.Current
            && older.Current == f.Coordinator.Snapshot, "较旧的完整回包应保留其证据，当前可启动状态应维持修订 3。");
        await RequireStartAcceptedAsync(initialStart, initialStarts);

        var gap = running with { StateRevision = 5 };
        await peer.WriteAsync(WireEnvelope.Event(ProtocolOperations.RunStateChanged,
            new StateChangedEvent(f.Run.WorkerInstanceId, 5, gap)), f.Token);
        var refresh = await ReadRequestAsync(peer, ProtocolOperations.WorkerGetSnapshot, f.Token);
        Require((await f.NextAsync(WorkerEvidenceKind.Snapshot)).Current?.Resynchronising == true,
            "修订从 3 跳到 5 应触发完整快照补查。");
        var stale = f.NewSubmission();
        var beforeClick = f.WireStarts;
        var refused = f.Coordinator.SendStartAsync(stale, f.Attempt, f.Token);
        Require(refused.IsCompleted && f.History.Count(b => b.Kind == WorkerEvidenceKind.NotSent
            && b.Request?.Submission == stale) == 1 && f.WireStarts == beforeClick,
            "补查期间尚无新鲜 Ready 帧时，本次点击必须立即 NotSent，发送次数为零。");
        await ExpectAsync<InvalidOperationException>(() => refused, "快照已失效应当场拒绝启动。");
        ready = ready with { StateRevision = 6 };
        await peer.WriteAsync(WireEnvelope.Event(ProtocolOperations.WorkerStateChanged,
            new StateChangedEvent(f.Run.WorkerInstanceId, 6, ready)), f.Token);
        Require((await f.NextAsync(WorkerEvidenceKind.Snapshot)).Current is { FreshReadyToStart: true, WorkerSnapshot.StateRevision: 6 },
            "补查回包前的 Ready/Idle 事件应照常更新生产快照。");
        var gapStart = StartDuringSync(out var gapStarts);
        await peer.WriteAsync(WireEnvelope.Response(refresh.Operation, refresh.RequestId!.Value, new GetSnapshotResponse(gap)), f.Token);
        await f.NextAsync(WorkerEvidenceKind.Snapshot);
        await RequireStartAcceptedAsync(gapStart, gapStarts);
        Require(f.WireStarts == beforeClick + 1, "恢复后只有第二次在新鲜 Ready 上的点击可以发出。");

        Task<WorkerObservationBatch> StartDuringSync(out int starts)
        {
            starts = f.WireStarts;
            var send = f.Coordinator.SendStartAsync(f.NewSubmission(), f.Attempt, f.Token);
            Require(!send.IsCompleted && f.WireStarts == starts, "完整快照尚未返回时，本次点击应等待同步。");
            return send;
        }

        async Task RequireStartAcceptedAsync(Task<WorkerObservationBatch> send, int starts)
        {
            var frame = await f.ReadAsync(peer, ProtocolOperations.RunStart);
            await peer.WriteAsync(WireEnvelope.Response(frame.Operation, frame.RequestId!.Value, new RunStartResponse("accepted")), f.Token);
            Require((await send.WaitAsync(f.Token)).StartReply == StartReplyKind.Accepted && f.WireStarts == starts + 1,
                "完整回包到达且生产快照允许启动时，同一次点击应取得接纳应答。");
        }
    }

    private static async Task VerifySnapshotGapTimeoutReconnectAsync(AppLogger logger, string directory, string project)
    {
        await using var f = new EvidenceFixture(logger, directory, project);
        await using var peer = await f.ConnectAsync();
        var idle = CreateSnapshot(f.Admission, 0) with { StateRevision = 3 };
        await peer.WriteAsync(WireEnvelope.Event(ProtocolOperations.WorkerStateChanged,
            new StateChangedEvent(f.Run.WorkerInstanceId, 3, idle)), f.Token);
        var refresh = await ReadRequestAsync(peer, ProtocolOperations.WorkerGetSnapshot, f.Token);
        Require(ProtocolJson.Deserialize<GetSnapshotRequest>(refresh.Data).RunId is null
            && SpinWait.SpinUntil(() => f.Clock.PendingTimers == 1, TimeSpan.FromSeconds(5)), "断档应触发完整补查。");
        var ready = idle with { StateRevision = 4 };
        await peer.WriteAsync(WireEnvelope.Event(ProtocolOperations.WorkerStateChanged,
            new StateChangedEvent(f.Run.WorkerInstanceId, 4, ready)), f.Token);
        await f.NextAsync(WorkerEvidenceKind.Snapshot);
        await f.NextAsync(WorkerEvidenceKind.Snapshot);
        f.Clock.Advance(TimeSpan.FromSeconds(5));
        var submission = f.NewSubmission();
        var waiting = f.Coordinator.SendStartAsync(submission, f.Attempt, f.Token);
        Require(!waiting.IsCompleted, "补查截止前点击开始应等待这条连接。");
        f.Clock.Advance(TimeSpan.FromSeconds(10));
        // Wait until the sender has actually observed the deadline before attempting its late reply.
        Require(SpinWait.SpinUntil(() => f.Server.WireClosed.Task.IsCompleted || f.Clock.PendingTimers > 1,
            TimeSpan.FromSeconds(5)), "补查应已超时。");
        try {
            await peer.WriteAsync(WireEnvelope.Response(refresh.Operation, refresh.RequestId!.Value,
                new GetSnapshotResponse(idle)), f.Token);
            await peer.WriteAsync(WireEnvelope.Event(ProtocolOperations.WorkerStateChanged,
                new StateChangedEvent(f.Run.WorkerInstanceId, 4, idle with { StateRevision = 4 })), f.Token);
        } catch (IOException) { } // The old pipe may already be closed.
        Require(SpinWait.SpinUntil(() => f.History.Any(b => b.Kind == WorkerEvidenceKind.Disconnected
            && b.Error is TimeoutException), TimeSpan.FromSeconds(3)), "完整补查逾期应关闭旧连接，允许 Worker 重连。");
        await ExpectAsync<TimeoutException>(() => waiting.WaitAsync(f.Token), "等待补查的启动应收到原连接超时原因。");
        Require(f.History.Count(b => b.Kind == WorkerEvidenceKind.NotSent && b.Request?.Submission == submission) == 1
            && f.WireStarts == 0, "补查失败应结算原连接上等待的启动，不能在重连后补发。");
        await peer.DisposeAsync();
        await using var next = await f.ConnectAsync(4);
        var send = f.Coordinator.SendStartAsync(f.NewSubmission(), f.Attempt, f.Token);
        var start = await f.ReadAsync(next, ProtocolOperations.RunStart);
        await next.WriteAsync(WireEnvelope.Response(start.Operation, start.RequestId!.Value,
            new RunStartResponse("accepted")), f.Token);
        Require((await send).StartReply == StartReplyKind.Accepted && f.WireStarts == 1, "重连首次同步后应能重新开始任务。");
    }

    private static async Task VerifyQueuedStartSnapshotGapAsync(AppLogger logger, string directory, string project)
    {
        await using var f = new EvidenceFixture(logger, directory, project);
        await using var peer = await f.ConnectAsync();
        f.Server.StopAfterBytes = 0;
        using var releaseWriter = CancellationTokenSource.CreateLinkedTokenSource(f.Token);
        var held = f.Coordinator.StopRunAsync(f.Run.RunId, releaseWriter.Token);
        var submission = f.NewSubmission();
        Task<WorkerObservationBatch> send;
        var ready = CreateSnapshot(f.Admission, 0) with { StateRevision = 3 };
        try {
            await f.Server.Blocked.Task.WaitAsync(f.Token);
            send = f.Coordinator.SendStartAsync(submission, f.Attempt, f.Token);
            Require(!send.IsCompleted && f.Coordinator.EvidenceCounts == (1, 1),
                "启动应通过初检并在管道写锁上排队。");
            f.Clock.Advance(TimeSpan.FromSeconds(10));

            await peer.WriteAsync(WireEnvelope.Event(ProtocolOperations.WorkerStateChanged,
                new StateChangedEvent(f.Run.WorkerInstanceId, 3, ready)), f.Token);
            Require((await f.NextAsync(WorkerEvidenceKind.Snapshot)).Current?.Resynchronising == true,
                "持锁期间读线程应发现修订断档并安排完整快照补查。");
            ready = ready with { StateRevision = 4 };
            await peer.WriteAsync(WireEnvelope.Event(ProtocolOperations.WorkerStateChanged,
                new StateChangedEvent(f.Run.WorkerInstanceId, 4, ready)), f.Token);
            Require((await f.NextAsync(WorkerEvidenceKind.Snapshot)).Current is { FreshReadyToStart: true },
                "释放写锁前，生产快照应已就绪，完整快照补查仍待返回。");
        } finally {
            releaseWriter.Cancel();
        }
        await ExpectAsync<OperationCanceledException>(() => held, "持锁请求应在首字节前取消。");
        var refresh = await ReadRequestAsync(peer, ProtocolOperations.WorkerGetSnapshot, f.Token);
        Require(!send.IsCompleted && f.WireStarts == 0 && f.Server.PartialFrameBytes == 0,
            "排队的启动应释放写锁供补查使用，保留原提交。");
        f.Clock.Advance(TimeSpan.FromSeconds(4));
        await peer.WriteAsync(WireEnvelope.Response(refresh.Operation, refresh.RequestId!.Value,
            new GetSnapshotResponse(ready)), f.Token);
        await f.NextAsync(WorkerEvidenceKind.Snapshot);
        var start = await f.ReadAsync(peer, ProtocolOperations.RunStart);
        f.Clock.Advance(TimeSpan.FromSeconds(1));
        await ExpectAsync<TimeoutException>(() => send.WaitAsync(f.Token), "排队和等待补查用过的时间不能延长启动应答期限。");
        await peer.WriteAsync(WireEnvelope.Response(start.Operation, start.RequestId!.Value, new RunStartResponse("accepted")), f.Token);
        Require((await f.NextAsync(WorkerEvidenceKind.StartReply, submission)).StartReply == StartReplyKind.Accepted
            && f.WireStarts == 1 && f.Coordinator.EvidenceCounts == (0, 0),
            "补查结束后应只发送最初排队的那次启动，迟到应答仍归原提交。");
    }

    private static async Task VerifyStartEvidenceWritesAsync(AppLogger logger, string directory, string project)
    {
        await using var f = new EvidenceFixture(logger, directory, project);
        var c = f.Coordinator;
        await using var zero = await f.ConnectAsync();
        var oversized = f.Attempt with { RedactionValues = [new string('x', ProtocolConstants.MaximumFramePayloadBytes)] };
        var large = f.NewSubmission();
        var sizeError = await ExpectAsync<ProtocolException>(() => c.SendStartAsync(large, oversized, f.Token), "超大帧应该被拒绝。");
        var rejected = await f.NextAsync(WorkerEvidenceKind.NotSent, large);
        Require(ReferenceEquals(rejected.Error, sizeError) && rejected.Request?.WriteSequence is null, "超大帧失败应在正文前保留原错。");

        f.Server.StopAfterBytes = 0;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(f.Token);
        // The ordinary writer has no virtual deadline while it holds the lock.
        var held = c.StopRunAsync(f.Run.RunId, cancel.Token);
        await f.Server.Blocked.Task.WaitAsync(f.Token);
        var queued = f.NewSubmission();
        var queuedTask = c.SendStartAsync(queued, f.Attempt, f.Token);
        await f.ExpireAsync(queuedTask);
        Require(!held.IsCompleted, "排队的提交必须在持锁者仍停于首字节前时超时。");
        // The closing host cancels the token belonging to this submission while the ordinary writer owns the lock.
        using var closing = CancellationTokenSource.CreateLinkedTokenSource(f.Token);
        var closingSubmission = f.NewSubmission();
        var closingWriter = c.SendStartAsync(closingSubmission, f.Attempt, closing.Token);
        await f.NextAsync(WorkerEvidenceKind.BeforeBody, closingSubmission);
        closing.Cancel();
        var cancelledWrite = await ExpectAsync<OperationCanceledException>(() => closingWriter.WaitAsync(f.Token), "等写锁时应收到原取消。");
        await f.NextAsync(WorkerEvidenceKind.NotSent, closingSubmission);
        cancel.Cancel();
        await ExpectAsync<OperationCanceledException>(() => held.WaitAsync(f.Token), "普通请求应保留零字节取消。");
        Require(f.History.Count(b => b.Kind == WorkerEvidenceKind.NotSent && b.Request?.Submission == queued) == 1
            && !f.History.Any(b => b.Kind == WorkerEvidenceKind.BodyStarted)
            && !f.History.Any(b => b.Kind == WorkerEvidenceKind.Disconnected)
            && f.Server.PartialFrameBytes == 0 && c.EvidenceCounts == (0, 0), "零字节和等写锁应保持健康连接。");
        var stop = c.StopRunAsync(f.Run.RunId, f.Token);
        var stopped = await f.ReadAsync(zero, ProtocolOperations.RunStop);
        await zero.WriteAsync(WireEnvelope.Response(stopped.Operation, stopped.RequestId!.Value, new RunStopResponse("accepted")), f.Token);
        await stop.WaitAsync(f.Token);
        Require(f.History.Count(b => b.Kind == WorkerEvidenceKind.NotSent && b.Request?.Submission == closingSubmission
            && b.Error == cancelledWrite) == 1 && !f.History.Any(b => b.Kind == WorkerEvidenceKind.BodyStarted)
            && f.WireStarts == 0, "释放写锁并完成后续请求后，确认退出取消的启动仍须为零次正文、一次 NotSent。");
        await zero.DisposeAsync();
        await f.NextAsync(WorkerEvidenceKind.Disconnected);

        foreach (var count in new[] { 1, sizeof(uint), sizeof(uint) + 1 }) {
            await using var partial = await f.ConnectAsync();
            var server = f.Server;
            server.StopAfterBytes = count;
            if (count == 1) {
                server.ReleasePrefix = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            var sub = f.NewSubmission();
            var sending = c.SendStartAsync(sub, f.Attempt, f.Token);
            await server.Blocked.Task.WaitAsync(f.Token);
            try {
                await f.ExpireAsync(sending);
                await server.WireClosed.Task.WaitAsync(f.Token);
            } finally {
                server.ReleasePrefix?.TrySetResult();
            }
            var disconnect = await f.NextAsync(WorkerEvidenceKind.Disconnected);
            Require(disconnect.Error is IOException && server.PartialFrameBytes == count
                && f.History.Any(b => b.Kind == WorkerEvidenceKind.BeforeBody && b.Request?.Submission == sub)
                && f.History.Any(b => b.Request?.Submission == sub && b.Kind ==
                    (count > sizeof(uint) ? WorkerEvidenceKind.BodyStarted : WorkerEvidenceKind.NotSent))
                && c.EvidenceCounts == (0, 0) && f.WireStarts == 0, "半帧应按正文边界归类，并由 GUI 关闭整条连接。");
            await ExpectAsync<EndOfStreamException>(() => partial.ReadAsync(f.Token), "客户端应在未读满该帧时收到 EOF。");
        }
        await using (var ordinary = await f.ConnectAsync()) {
            f.Server.StopAfterBytes = sizeof(uint);
            using var interrupted = CancellationTokenSource.CreateLinkedTokenSource(f.Token);
            var first = c.StopRunAsync(f.Run.RunId, interrupted.Token);
            await f.Server.Blocked.Task.WaitAsync(f.Token);
            var waiting = c.StopRunAsync(f.Run.RunId, f.Token);
            interrupted.Cancel();
            await ExpectAsync<OperationCanceledException>(() => first, "普通半帧写者应收到原取消。");
            await ExpectAsync<IOException>(() => waiting.WaitAsync(f.Token), "排队的普通请求应收到断管 IOException。");
            await f.NextAsync(WorkerEvidenceKind.Disconnected);
            Require(f.Server.PartialFrameBytes == sizeof(uint), "半帧后不能追加下一请求的任何字节。");
            await ExpectAsync<EndOfStreamException>(() => ordinary.ReadAsync(f.Token), "普通半帧也应由服务端关闭。");
        }
        await using var healthy = await f.ConnectAsync();
        f.Server.StopAfterBytes = 0;
        using var beforeFirstByte = CancellationTokenSource.CreateLinkedTokenSource(f.Token);
        var cancelled = f.NewSubmission();
        var zeroByteStart = c.SendStartAsync(cancelled, f.Attempt, beforeFirstByte.Token);
        await f.Server.Blocked.Task.WaitAsync(f.Token);
        beforeFirstByte.Cancel();
        await ExpectAsync<OperationCanceledException>(() => zeroByteStart, "首字节前取消应保留原调用方取消。");
        var notSent = await f.NextAsync(WorkerEvidenceKind.NotSent, cancelled);
        Require(notSent.Request?.WriteSequence is null && f.Server.PartialFrameBytes == 0
            && !f.History.Any(b => b.Kind == WorkerEvidenceKind.BodyStarted && b.Request?.Submission == cancelled),
            "零字节取消应关联原提交且不进入正文。");
        var submission = f.NewSubmission();
        var completed = c.SendStartAsync(submission, f.Attempt, f.Token);
        await f.ReadAsync(healthy, ProtocolOperations.RunStart);
        await f.ExpireAsync(completed);
        Require(c.EvidenceCounts == (1, 0) && f.WireStarts == 1 && !f.Server.WireClosed.Task.IsCompleted,
            "完整帧超时只保留应答关联，重连后的 Worker 只应收到显式发送的一帧。");
    }

    private static async Task VerifyStartEvidenceRepliesAsync(AppLogger logger, string directory, string project)
    {
        await using var f = new EvidenceFixture(logger, directory, project);
        await using var peer = await f.ConnectAsync();
        var c = f.Coordinator;
        string[] codes = ["accepted", "already_accepted", "invalid_request", "run_id_conflict", "worker_not_ready",
            "worker_faulted", "operation_not_allowed", "worker_busy", "invalid_run_plan", "protocol_version_mismatch", "internal_error"];
        var submissions = new List<(Submission Submission, Guid Id)>();
        for (var i = 0; i <= codes.Length; ++i) {
            var submission = f.NewSubmission();
            var send = c.SendStartAsync(submission, f.Attempt, f.Token);
            var body = await f.NextAsync(WorkerEvidenceKind.BodyStarted, submission);
            var frame = await f.ReadAsync(peer, ProtocolOperations.RunStart);
            Require(body.Request!.RequestId == frame.RequestId && c.EvidenceCounts == (i + 1, 1), "线上请求与正文证据必须对应。");
            submissions.Add((submission, frame.RequestId!.Value));
            await f.ExpireAsync(send);
            Require(c.EvidenceCounts == (i + 1, 0), "连续超时后只保留轻量身份。");
        }
        var wire = f.Server;
        for (var i = codes.Length - 1; i >= 0; --i) {
            var (submission, id) = submissions[i];
            await peer.WriteAsync(i < 2
                ? WireEnvelope.Response(ProtocolOperations.RunStart, id, new RunStartResponse(codes[i]))
                : WireEnvelope.Failure(ProtocolOperations.RunStart, id, codes[i], "scripted"), f.Token);
            var late = await f.NextAsync(WorkerEvidenceKind.StartReply);
            var disposition = i switch {
                0 => StartReplyKind.Accepted, 1 => StartReplyKind.AlreadyAccepted,
                _ when codes[i] == "internal_error" => StartReplyKind.Indeterminate,
                _ => StartReplyKind.RejectedBeforeRegistration
            };
            Require(late.Request?.Submission == submission && late.Request.RequestId == id
                && late.ReplyCode == codes[i] && late.StartReply == disposition, "迟到应答必须带回原提交和真实处置。");
        }
        Require(c.EvidenceCounts == (1, 0), "只有未回包的启动应保留轻量身份。");
        // Do not read the tail run.start on the peer. It must still count on the GUI wire.
        var tail = c.SendStartAsync(f.NewSubmission(), f.Attempt, f.Token);
        Require(SpinWait.SpinUntil(() => f.WireStarts == submissions.Count + 1, TimeSpan.FromSeconds(5)),
            "客户端未读取的完整帧也必须计数。");
        await f.ExpireAsync(tail);
        var last = submissions[^1];
        await peer.WriteAsync(WireEnvelope.Response(ProtocolOperations.RunStart, last.Id,
            new RunStartResponse("accepted")), f.Token);
        await peer.DisposeAsync();
        var eof = await f.NextAsync(WorkerEvidenceKind.Disconnected);
        await wire.WireClosed.Task.WaitAsync(f.Token);
        var ordered = f.History.ToList();
        Require(eof.Error is null && c.EvidenceCounts == (0, 0) && f.WireStarts == submissions.Count + 1,
            "EOF 应清空本连接的所有关联，且没有自动重发。");
        var accepted = ordered.Single(b => b.Kind == WorkerEvidenceKind.StartReply && b.Request?.RequestId == last.Id);
        Require(accepted.Request?.Submission == last.Submission && ordered.IndexOf(accepted) < ordered.IndexOf(eof),
            "应答后紧跟 EOF 时仍应先发布同一连接上已读到的接纳。");
        await using var next = await f.ConnectAsync();
        var lookup = c.QueryRunAsync(f.Run, new(Guid.NewGuid()), f.Token);
        var lookupFrame = await f.ReadAsync(next, ProtocolOperations.WorkerGetSnapshot);
        await next.WriteAsync(WireEnvelope.Response(lookupFrame.Operation, lookupFrame.RequestId!.Value,
            new GetSnapshotResponse(CreateSnapshot(f.Admission, 0), true)), f.Token);
        Require(!(await lookup.WaitAsync(f.Token)).Request!.Covers(accepted.Request!),
            "重连查询不能按写序覆盖旧连接上的启动。");
    }

    private static async Task VerifyStartEvidenceQueryAsync(AppLogger logger, string directory, string project)
    {
        await using var f = new EvidenceFixture(logger, directory, project);
        var snapshot = CreateSnapshot(f.Admission, 0);
        var active = CreateActiveRun(f.Attempt);
        var missingRevision = JsonSerializer.SerializeToNode(snapshot, ProtocolJson.Options)!.AsObject();
        missingRevision.Remove("stateRevision");
        object[] malformed = [
            new { snapshot }, new { snapshot, runRegistered = "false" },
            new { snapshot = missingRevision, runRegistered = false },
            new GetSnapshotResponse(snapshot with { ActiveRun = active, RunState = active.State }, false),
            new GetSnapshotResponse(snapshot with { LastRun = active with { State = RunState.Failed } }, false),
            new GetSnapshotResponse(snapshot with { WorkerInstanceId = Guid.NewGuid() }, true),
            new GetSnapshotResponse(snapshot with { WorkerPid = -1 }, false)
        ];
        for (var i = 0; i < malformed.Length; i++) {
            await using var peer = await f.ConnectAsync();
            var id = new QueryId(Guid.NewGuid());
            var query = f.Coordinator.QueryRunAsync(f.Run, id, f.Token);
            var frame = await f.ReadAsync(peer, ProtocolOperations.WorkerGetSnapshot);
            var late = i == 1;
            if (late) {
                await f.ExpireAsync(query);
            }
            await peer.WriteAsync(WireEnvelope.Response(frame.Operation, frame.RequestId!.Value, malformed[i]), f.Token);
            if (!late) {
                await ExpectAsync<ProtocolException>(() => query.WaitAsync(f.Token), "无效定向回包必须判为协议错误。");
            }
            var fault = await f.NextAsync(WorkerEvidenceKind.Disconnected);
            Require(fault.Error is ProtocolException && await peer.ReadAsync(f.Token) is null
                && !f.History.Any(b => b.Request?.QueryId == id && b.Kind == WorkerEvidenceKind.QueryReply),
                "无效回包不能产生查询证据，Worker 应收到服务端关闭。");
        }
        await using var healthy = await f.ConnectAsync();
        var start = f.NewSubmission();
        var send = f.Coordinator.SendStartAsync(start, f.Attempt, f.Token);
        var body = await f.NextAsync(WorkerEvidenceKind.BodyStarted);
        await f.ReadAsync(healthy, ProtocolOperations.RunStart);
        await f.ExpireAsync(send);
        foreach (var registered in new[] { false, true }) {
            var id = new QueryId(Guid.NewGuid());
            var query = f.Coordinator.QueryRunAsync(f.Run, id, f.Token);
            var frame = await f.ReadAsync(healthy, ProtocolOperations.WorkerGetSnapshot);
            await healthy.WriteAsync(WireEnvelope.Response(frame.Operation, frame.RequestId!.Value,
                new GetSnapshotResponse(snapshot, registered)), f.Token);
            var result = await query.WaitAsync(f.Token);
            Require(result.Request?.QueryId == id && result.Request.RequestId == frame.RequestId
                && result.Request.Covers(body.Request!) && result.RunRegistered == registered && result.Current?.Fresh == true,
                "同连接的有效查询应按写序覆盖原提交，并保留 true/false 原值。");
        }
        f.Coordinator.ChildSessionEnded();
        var removed = await f.NextAsync(WorkerEvidenceKind.AdmissionRemoved);
        Require(removed.Connection == body.Connection && await healthy.ReadAsync(f.Token) is null
            && !f.History.Any(b => b.Kind == WorkerEvidenceKind.ProcessExited) && f.WireStarts == 1,
            "摘除 Admission 应立即关闭管道，不能冒充进程退出。");
    }

    // Only the GUI's outgoing writes are intercepted. Admission, framing, reader, and peer EOF use a real pipe.
    private sealed class EvidenceWriteStream(Stream pipe, Action onWireStart) : Stream
    {
        internal int? StopAfterBytes;
        internal TaskCompletionSource? ReleasePrefix;
        internal Task? HoldNextFlush;
        internal readonly TaskCompletionSource Blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
            if (count == buffer.Length && (remaining != count || _frame.Length != sizeof(uint))) {
                return;
            }
            StopAfterBytes = null;
            Blocked.TrySetResult();
            if (ReleasePrefix is { } release) {
                await release.Task;
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
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
            if (Interlocked.Exchange(ref HoldNextFlush, null) is { } held) {
                await held;
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
