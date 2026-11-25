using System.Windows.Threading;
using Gui.Infrastructure;
using Gui.Runtime;
using Protocol;

namespace Gui.Worker;

internal static partial class WorkerCoordinatorSelfTest
{
    internal static async Task VerifySettledRunSnapshotRetry(AppLogger logger, string directory, string project)
    {
        await using var f = new EvidenceFixture(logger, directory, project);
        await using var peer = await f.ConnectAsync();
        var notices = new List<LifecycleEffect.Payload>();
        var completed = new List<Guid>();
        var cleaned = new List<RunKey>();
        var cleanupDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = Dispatcher.CurrentDispatcher;
        using var host = new RunLifecycleHost(f.Coordinator, dispatcher, () => "A",
            notice => { notices.Add(notice); return Task.FromResult(true); }, () => { }, logger);
        host.ScheduledCompleted += run => completed.Add(run.RunId);
        host.CleanupPending += own => {
            cleaned.Add(own.Key);
            host.SetCleanup(own.Key, CleanupStage.Finished);
            cleanupDelivered.TrySetResult();
            return Task.CompletedTask;
        };
        var intent = new RunIntent(Guid.NewGuid(), RunSource.Scheduled, "定时", new(Guid.NewGuid(), 1, 1), "A");
        var submitting = host.SubmitAsync(intent, f.Run, f.Attempt);
        var start = await f.ReadAsync(peer, ProtocolOperations.RunStart);
        var active = CreateActiveRun(f.Attempt);
        await SendRevisionAsync(peer, CreateSnapshot(f.Admission, 0, active) with { StateRevision = 2 }, f.Token);
        await peer.WriteAsync(WireEnvelope.Response(start.Operation, start.RequestId!.Value, new RunStartResponse("accepted")), f.Token);
        await submitting.WaitAsync(f.Token);
        var terminal = CreateSnapshot(f.Admission, 0) with { StateRevision = 4,
            LastRun = active with { State = RunState.Succeeded, EndedAtUtc = DateTime.UtcNow } };
        await SendRevisionAsync(peer, terminal, f.Token);
        var refresh = await ReadFullRefreshAsync(peer, f.Token);
        await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        Require(completed.SequenceEqual([f.Run.RunId]) && host.Orchestration.Scheduled?.Cleanup == CleanupStage.Pending
            && cleaned.Count == 0 && f.Coordinator.Snapshot.Resynchronising, "跨号终态应结算一次并等待新鲜快照收尾。");

        // The now idle peer sends no more events. Even a second timeout must leave a retry armed.
        for (int attempt = 0; attempt < 2; attempt++) {
            ExpireFullRefresh(f);
            f.Clock.Advance(TimeSpan.FromSeconds(15));
            refresh = await ReadFullRefreshAsync(peer, f.Token);
        }
        await peer.WriteAsync(WireEnvelope.Response(refresh.Operation, refresh.RequestId!.Value,
            new GetSnapshotResponse(terminal)), f.Token);
        await cleanupDelivered.Task.WaitAsync(f.Token);
        await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        Require(f.Coordinator.Snapshot.FreshReadyToStart && f.WireStarts == 1
            && completed.SequenceEqual([f.Run.RunId]) && cleaned.SequenceEqual([f.Run])
            && notices is [LifecycleEffect.Present { Kind: NoticeKind.Succeeded } notice] && notice.Key == f.Run,
            "没有后续事件时，重试应恢复定时可启动状态，并只交付一次成功提醒和收尾。");
        // The probe is the next request on the wire only if no full recheck followed the fresh reply.
        Require((await ProbeTargetedAsync(f, peer, terminal)).Current?.FreshReadyToStart == true,
            "恢复新鲜后不应再发出完整补查。");
    }

    private static async Task VerifyStaleRefreshAfterGapAsync(AppLogger logger, string directory, string project)
    {
        await using var f = new EvidenceFixture(logger, directory, project);
        await using var peer = await f.ConnectAsync();
        var active = CreateActiveRun(f.Attempt);
        var running = CreateSnapshot(f.Admission, 0, active) with { StateRevision = 4 };
        var terminal = CreateSnapshot(f.Admission, 0) with { StateRevision = 6,
            LastRun = active with { State = RunState.Succeeded, EndedAtUtc = DateTime.UtcNow } };
        await SendRevisionAsync(peer, running with { StateRevision = 3 }, f.Token);
        var first = await ReadFullRefreshAsync(peer, f.Token);
        // Revision 4 (Running) restores Fresh while the recheck is out; terminal revision 6 then gaps and reuses it.
        await SendRevisionAsync(peer, running, f.Token);
        await SendRevisionAsync(peer, terminal, f.Token);
        await f.NextAsync(WorkerEvidenceKind.Snapshot);
        Require((await f.NextAsync(WorkerEvidenceKind.Snapshot)).Current?.Fresh == true
            && (await f.NextAsync(WorkerEvidenceKind.Snapshot)).Current?.Resynchronising == true,
            "补查在途时应依次发布恢复和再次断档。");
        // Neither a reply below the cache (2) nor one equal to it (4) may re-publish the running view as Fresh.
        var refresh = first;
        foreach (var stale in new[] { running with { StateRevision = 2 }, running }) {
            await peer.WriteAsync(WireEnvelope.Response(refresh.Operation, refresh.RequestId!.Value,
                new GetSnapshotResponse(stale)), f.Token);
            Require((await f.NextAsync(WorkerEvidenceKind.Snapshot)).Current is { Resynchronising: true, WorkerSnapshot.StateRevision: 4 },
                $"修订 {stale.StateRevision} 的完整回包低于已见修订，不能恢复新鲜度。");
            refresh = await ReadFullRefreshAsync(peer, f.Token);
        }
        await peer.WriteAsync(WireEnvelope.Response(refresh.Operation, refresh.RequestId!.Value,
            new GetSnapshotResponse(terminal)), f.Token);
        Require((await f.NextAsync(WorkerEvidenceKind.Snapshot)).Current is { FreshReadyToStart: true, WorkerSnapshot.StateRevision: 6 },
            "补查应重发，直到回包追上已见修订。");
        await ProbeTargetedAsync(f, peer, terminal);
    }

    private static async Task VerifyLateEventDuringBackoffAsync(AppLogger logger, string directory, string project)
    {
        await using var f = new EvidenceFixture(logger, directory, project);
        await using var peer = await f.ConnectAsync();
        var active = CreateActiveRun(f.Attempt);
        var running = CreateSnapshot(f.Admission, 0, active) with { StateRevision = 2 };
        var terminal = CreateSnapshot(f.Admission, 0) with { StateRevision = 4,
            LastRun = active with { State = RunState.Succeeded, EndedAtUtc = DateTime.UtcNow } };
        await SendRevisionAsync(peer, running, f.Token);
        Require((await f.NextAsync(WorkerEvidenceKind.Snapshot)).Current?.Fresh == true, "相邻修订应保持新鲜。");
        await SendRevisionAsync(peer, terminal, f.Token);
        Require((await f.NextAsync(WorkerEvidenceKind.Snapshot)).Current?.Resynchronising == true, "跨号终态应进入补查。");
        await ReadFullRefreshAsync(peer, f.Token);
        ExpireFullRefresh(f);
        // A late Stopping event below the terminal revision lands while the recheck waits in backoff.
        var stopping = active with { State = RunState.Stopping };
        await SendRevisionAsync(peer, CreateSnapshot(f.Admission, 0, stopping) with { StateRevision = 3 }, f.Token);
        Require((await f.NextAsync(WorkerEvidenceKind.Snapshot)).Current is { Resynchronising: true, WorkerSnapshot.StateRevision: 2 },
            "低于已见修订的迟到事件不能恢复新鲜度，也不能替换缓存。");
        f.Clock.Advance(TimeSpan.FromSeconds(15));
        var refresh = await ReadFullRefreshAsync(peer, f.Token);
        await peer.WriteAsync(WireEnvelope.Response(refresh.Operation, refresh.RequestId!.Value,
            new GetSnapshotResponse(terminal)), f.Token);
        Require((await f.NextAsync(WorkerEvidenceKind.Snapshot)).Current is { FreshReadyToStart: true, WorkerSnapshot.StateRevision: 4 },
            "退避结束后补查应继续，直到已见修订的回包恢复可启动。");
        await ProbeTargetedAsync(f, peer, terminal);
    }

    private static async Task VerifySnapshotRetryDisconnectAsync(AppLogger logger, string directory, string project)
    {
        await using var f = new EvidenceFixture(logger, directory, project);
        var idle = CreateSnapshot(f.Admission, 0) with { StateRevision = 3 };
        await using (var peer = await f.ConnectAsync()) {
            await SendRevisionAsync(peer, idle, f.Token);
            await ReadFullRefreshAsync(peer, f.Token);
            ExpireFullRefresh(f);
            // A fresh event followed by another gap shares the outstanding backoff.
            await SendRevisionAsync(peer, idle with { StateRevision = 4 }, f.Token);
            await SendRevisionAsync(peer, idle with { StateRevision = 6 }, f.Token);
            await f.NextAsync(WorkerEvidenceKind.Snapshot);
            Require((await f.NextAsync(WorkerEvidenceKind.Snapshot)).Current?.Fresh == true
                && (await f.NextAsync(WorkerEvidenceKind.Snapshot)).Current?.Resynchronising == true,
                "退避期间应依次发布恢复和再次断档。");
            Require((await ProbeTargetedAsync(f, peer, idle)).Current?.Resynchronising == true,
                "退避期间不能重复发出完整补查，较旧的定向回包也不能恢复新鲜度。");
            // Only the shared backoff may send the next full recheck; a duplicate would sit ahead of the probe.
            f.Clock.Advance(TimeSpan.FromSeconds(15));
            await ReadFullRefreshAsync(peer, f.Token);
            ExpireFullRefresh(f);
            await ProbeTargetedAsync(f, peer, idle);
        }
        await f.NextAsync(WorkerEvidenceKind.Disconnected);
        Require(f.Clock.NextTimerDelay is not { } pending || pending >= TimeSpan.FromSeconds(15),
            "EOF 应取消退避计时。");
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        await using var next = await f.ConnectAsync(6);
        await SendRevisionAsync(next, idle with { StateRevision = 8 }, f.Token);
        var refresh = await ReadFullRefreshAsync(next, f.Token);
        await next.WriteAsync(WireEnvelope.Response(refresh.Operation, refresh.RequestId!.Value,
            new GetSnapshotResponse(idle with { StateRevision = 8 })), f.Token);
        await f.NextAsync(WorkerEvidenceKind.Snapshot);
        Require((await f.NextAsync(WorkerEvidenceKind.Snapshot)).Current is { FreshReadyToStart: true, WorkerSnapshot.StateRevision: 8 },
            "重连后新断档应由新连接独立补查。");
    }

    private static Task SendRevisionAsync(ProtocolConnection peer, WorkerSnapshot snapshot, CancellationToken token) =>
        peer.WriteAsync(WireEnvelope.Event(ProtocolOperations.RunStateChanged,
            new StateChangedEvent(snapshot.WorkerInstanceId, snapshot.StateRevision, snapshot)), token);

    private static async Task<WireEnvelope> ReadFullRefreshAsync(ProtocolConnection peer, CancellationToken token)
    {
        var frame = await ReadRequestAsync(peer, ProtocolOperations.WorkerGetSnapshot, token);
        Require(ProtocolJson.Deserialize<GetSnapshotRequest>(frame.Data).RunId is null, "连接补查必须请求完整快照。");
        return frame;
    }

    // A full recheck queued before this targeted query would be read first and fail the probe.
    private static async Task<WorkerObservationBatch> ProbeTargetedAsync(EvidenceFixture f, ProtocolConnection peer,
        WorkerSnapshot reply)
    {
        var run = new RunKey(f.Admission.WorkerInstanceId, Guid.NewGuid());
        var probe = f.Coordinator.QueryRunAsync(run, new(Guid.NewGuid()), f.Token);
        var frame = await ReadRequestAsync(peer, ProtocolOperations.WorkerGetSnapshot, f.Token);
        Require(ProtocolJson.Deserialize<GetSnapshotRequest>(frame.Data).RunId == run.RunId,
            "定向查询之前不应排有多余的完整补查。");
        await peer.WriteAsync(WireEnvelope.Response(frame.Operation, frame.RequestId!.Value,
            new GetSnapshotResponse(reply, false)), f.Token);
        return await probe;
    }

    private static void ExpireFullRefresh(EvidenceFixture f)
    {
        var timeout = TimeSpan.FromSeconds(15);
        Require(SpinWait.SpinUntil(() => f.Clock.NextTimerDelay == timeout, TimeSpan.FromSeconds(5)),
            "线上补查应开始等待应答。");
        f.Clock.Advance(timeout);
        Require(SpinWait.SpinUntil(() => f.Clock.NextTimerDelay is { } delay && delay > TimeSpan.Zero && delay < timeout,
            TimeSpan.FromSeconds(5)), "补查超时应进入退避。");
    }
}
