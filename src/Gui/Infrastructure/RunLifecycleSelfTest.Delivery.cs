using Gui.Runtime;
using Gui.Worker;
using Protocol;
using static Gui.Runtime.LifecycleEffect;
using static Gui.Runtime.LifecycleEvent;

namespace Gui.Infrastructure;

internal static partial class RunLifecycleSelfTest
{
    private static void VerifyRunLifecycleCountdownEvidence()
    {
        foreach (var outcome in new[] { "terminal-query", "terminal-exit", "idle-negative", "active" }) {
            var f = new Scenario();
            f.Begin(Intent(RunSource.Launch));
            f.Body();
            f.Eof();
            var next = Intent(RunSource.Scheduled, 2);
            f.PreviewSchedule(next, true);
            var deadline = f.Only<ShowCountdown>().Deadline;
            f.Connection = f.Connection with { Generation = 2 };
            var last = outcome.StartsWith("terminal", StringComparison.Ordinal)
                ? SnapshotOf(f.Send.Attempt, RunState.Succeeded) : null;
            f.FrameEvent(f.Frame(last: last));
            var query = f.QueryWire();
            f.Now = deadline;
            f.Step(new CountdownDue(next.Id));
            f.Step(new CountdownDue(next.Id));
            var beforeAnswer = f.History.Count;
            if (outcome == "terminal-exit") f.ExitWorker();
            else f.QueryResult(query, outcome != "idle-negative",
                f.Frame(active: outcome == "active" ? SnapshotOf(f.Send.Attempt, RunState.Running) : null));
            f.Step(new Reserved(next.Id, new(next.Id, Target)));
            f.Step(new CommittedConfiguration(next.Id));
            f.Step(new ActivityAppended(next.Id));
            if (outcome == "active") f.Terminal(RunState.Succeeded);
            f.DrainDeliveries();
            f.CheckSkipped(next, "开机任务的运行证据尚未确认");
            Check(f.History.Take(beforeAnswer).OfType<OperationFinished>().Any(o => o.Intent == next)
                && f.History.OfType<Reserve>().All(r => r.Intent != next) && f.Count<EndReserved>() == 0
                && f.Count<PrepareEnvironment>() == 0 && (last == null || f.Items<Present>().Single().Terminal == last)
                && f.Items<RequestQuit>().Count(q => q.Run == f.Key) == (outcome == "idle-negative" ? 0 : 1),
                $"开机归属 {outcome}：先结算候选，已收到的终态仍须触发 A 的退出");
        }
        var scheduled = new Scenario();
        scheduled.Begin(Intent(RunSource.Scheduled));
        scheduled.Body();
        scheduled.Eof();
        scheduled.Now += TimeSpan.FromMinutes(1);
        var replacement = Intent(RunSource.Scheduled, 3);
        scheduled.PreviewSchedule(replacement, true);
        var due = scheduled.Only<ShowCountdown>().Deadline;
        scheduled.Connection = scheduled.Connection with { Generation = 2 };
        scheduled.Terminal(RunState.Succeeded);
        scheduled.Now = due;
        scheduled.Step(new CountdownDue(replacement.Id));
        var beforeQuery = scheduled.History.Count;
        scheduled.QueryResult(scheduled.QueryWire(), true, scheduled.Frame(last: SnapshotOf(scheduled.Send.Attempt, RunState.Succeeded)));
        scheduled.DrainDeliveries();
        scheduled.CheckSkipped(replacement, "原任务的运行证据尚未确认");
        Check(scheduled.History.Take(beforeQuery).OfType<OperationFinished>().Any(o => o.Intent == replacement)
            && scheduled.History.OfType<Reserve>().All(r => r.Intent != replacement)
            && scheduled.Items<CompleteScheduled>().Single().Run == scheduled.Key && scheduled.Items<Cleanup>().Count() == 1,
            "已收到的定时终态也要在到期复核中先结算");
    }

    private static void VerifyRunLifecycleClosingReceipts()
    {
        foreach (var stage in new[] { "sending", "commit", "activity", "end", "prepare" })
        foreach (var decision in new[] { "resume-first", "confirm-first", "receipt-first" })
        foreach (var success in new[] { true, false }) {
            if (stage == "sending") {
                VerifyClosingWriter(decision, bodyStarted: success);
                continue;
            }
            var f = new Scenario();
            f.Connect();
            var a = Intent(RunSource.Scheduled);
            f.PreviewSchedule(a);
            f.Step(new Reserved(a.Id, new(a.Id, Target)));
            if (stage != "commit") f.Step(new CommittedConfiguration(a.Id));
            if (stage is "end" or "prepare") f.Step(new ActivityAppended(a.Id));
            if (stage == "prepare") f.Step(new OldEnvironmentEnded(a.Id, true));
            var exit = Id(19);
            f.Step(new BeginClosing(exit, ClosingKind.Exit));
            var proceed = decision == "confirm-first";
            if (decision != "receipt-first") f.Step(new ClosingConfirmed(exit, proceed));
            Check(f.Count<OperationFinished>() == 0 && f.Count<Release>() == 0 && f.Count<EndForClosing>() == 0,
                $"退出决定不能提前结算在途 {stage}");
            var error = success ? null : new InvalidOperationException("物理操作失败");
            if (stage == "commit") f.Step(new CommittedConfiguration(a.Id, error));
            if (stage == "activity" || stage == "commit" && success) f.Step(new ActivityAppended(a.Id));
            if (stage == "end") f.Step(new OldEnvironmentEnded(a.Id, success, error));
            if (stage == "prepare") f.Step(new Prepared(a.Id, success ? Target : null, error));
            var admitted = stage != "commit" || success;
            var result = f.History.OfType<OperationFinished>().Single();
            Check(result.Admitted == admitted && result.Intent == a && f.Count<Release>() == 1
                && (success || stage == "activity" ? result.Error is OperationCanceledException : result.Error == error)
                && f.Count<PublishConfiguration>() == (admitted ? 1 : 0) && f.Count<AppendStart>() == (admitted ? 1 : 0)
                && f.Count<EndReserved>() == (stage is "end" or "prepare" ? 1 : 0)
                && f.Count<ScheduleActivity>() == (stage == "prepare" || stage == "end" && success ? 1 : 0)
                && f.Count<PrepareEnvironment>() == (stage == "prepare" ? 1 : 0) && f.Count<CheckReady>() == 0,
                $"{stage} 按物理结果记账，退出后的撤销也不能继续换场");
            if (proceed) {
                Check(f.Count<EndForClosing>() == 1, "物理操作完成后退出可以取得环境闸门");
                f.Step(new ClosingEndReturned(exit, true));
                f.Expect(typeof(CommitExit));
            } else {
                if (decision == "receipt-first") f.Step(new ClosingConfirmed(exit, false));
                f.DrainDeliveries();
            }
        }
    }

    private static void VerifyClosingWriter(string decision, bool bodyStarted)
    {
        var f = new Scenario();
        var a = Intent(RunSource.Scheduled);
        f.Begin(a);
        var wire = f.Writer();
        f.Evidence(new(f.Connection, WorkerEvidenceKind.BeforeBody, wire));
        var exit = Id(19);
        f.Step(new BeginClosing(exit, ClosingKind.Exit));
        void StartBody()
        {
            wire = wire with { WriteSequence = ++f.Sequence };
            f.Evidence(new(f.Connection, WorkerEvidenceKind.BodyStarted, wire));
        }
        if (bodyStarted) StartBody(); // The writer crossed the boundary while the dialog was open.
        var confirm = decision != "resume-first";
        if (decision != "receipt-first") f.Step(new ClosingConfirmed(exit, confirm));
        var cancelled = f.History.OfType<CancelSend>().Any(c => c.Submission == f.Send.Submission);
        var error = new OperationCanceledException("写锁内确认取消");
        if (cancelled && !bodyStarted) {
            f.Evidence(new(f.Connection, WorkerEvidenceKind.NotSent, wire, Error: error));
        } else {
            if (!bodyStarted) StartBody();
            f.Reply(wire, StartReplyKind.Accepted, "accepted");
            f.Terminal(RunState.Failed);
        }
        if (decision == "receipt-first") f.Step(new ClosingConfirmed(exit, true));
        f.Step(new ClosingConfirmed(exit, confirm)); // duplicate decision
        // Ending failed: deliver everything that had been buffered behind the dialog.
        if (confirm) f.Step(new ClosingEndReturned(exit, false));
        f.DrainDeliveries();
        var prevent = decision == "confirm-first" && !bodyStarted;
        var result = f.History.OfType<OperationFinished>().Single();
        Check(cancelled == prevent && f.Count<CancelSend>() == (prevent ? 1 : 0)
            && result.Run == f.Key && result.Admitted && result.Error == (prevent ? error : null)
            && f.Count<Release>() == 1 && f.Count<SendStart>() == 1 && f.Count<EndForClosing>() == (confirm ? 1 : 0)
            && (!confirm || f.History.IndexOf(result) < f.History.FindIndex(e => e is EndForClosing))
            && f.Items<Present>().Single().Kind == (prevent ? NoticeKind.NotStarted : NoticeKind.Failed)
            && f.Items<CompleteScheduled>().Count() == (prevent ? 0 : 1) && !f.Items<Cleanup>().Any(),
            $"Sending/{decision}/正文已开始={bodyStarted}：只取消确认时仍在正文前的发送，按实际回执释放并交付一次");
    }

    private static void VerifyRunLifecycleCancelledPreparation()
    {
        foreach (var mode in new[] { StartMode.PrepareAndStart, StartMode.PrepareOnly }) {
            var f = new Scenario();
            var a = Intent(RunSource.Manual) with { Mode = mode };
            f.Step(new Start(a));
            f.Step(new Reserved(a.Id, new(a.Id, Target)));
            f.Step(new BeginClosing(Id(19), ClosingKind.Exit));
            f.Step(new ClosingConfirmed(Id(19), false));
            f.Step(new Prepared(a.Id, Target));
            f.Step(new Prepared(a.Id, Target));
            f.DrainDeliveries();
            var result = f.History.OfType<OperationFinished>().Single();
            Check(result.Intent == a && result.Run == null
                && (mode == StartMode.PrepareOnly ? result.Error == null : result.Error is OperationCanceledException)
                && f.Count<Release>() == 1 && f.Count<CheckReady>() == 0 && f.Count<SendStart>() == 0
                && !f.Items<Present>().Any(), $"退出打断 {mode} 后须按请求的操作回报");
        }
    }

    private static void VerifyRunLifecycleCancelledWriterRace()
    {
        var f = new Scenario();
        var a = Intent(RunSource.Scheduled);
        f.Begin(a);
        var wire = f.Writer();
        f.Evidence(new(f.Connection, WorkerEvidenceKind.BeforeBody, wire));
        var exit = Id(19);
        f.Step(new BeginClosing(exit, ClosingKind.Exit));
        f.Step(new ClosingConfirmed(exit, true));
        var cancel = f.Only<CancelSend>();
        wire = wire with { WriteSequence = ++f.Sequence };
        f.Evidence(new(f.Connection, WorkerEvidenceKind.BodyStarted, wire));
        f.Timeout(wire);
        f.ExitWorker();
        f.Step(new ClosingEndReturned(exit, false));
        f.DrainDeliveries();
        Check(cancel.Submission == f.Send.Submission && f.Count<CancelSend>() == 1
            && f.History.OfType<OperationFinished>().Single() is { Run: var run, Error: TimeoutException } && run == f.Key
            && f.Items<Present>().Single() is { Kind: NoticeKind.StartUnknown, Run: var reported } && reported == run
            && f.Count<EndForClosing>() == 1 && f.Count<Release>() == 1 && !f.Items<CompleteScheduled>().Any(),
            "取消发送后 writer 仍可能越过正文边界；进程退出时只能报告一次启动结果未知");
    }

    private static void VerifyRunLifecycleClosingCountdown()
    {
        foreach (var elapsed in new[] { false, true }) {
            var f = new Scenario();
            f.FrameEvent(f.Frame(active: SnapshotOf(Attempt(8), RunState.Running)));
            var b = Intent(RunSource.Scheduled, 2);
            f.PreviewSchedule(b, true);
            if (elapsed) {
                f.Now = f.Only<ShowCountdown>().Deadline;
                f.Step(new CountdownDue(b.Id));
            }
            var exit = Id(19);
            f.Step(new BeginClosing(exit, ClosingKind.Exit));
            f.Step(new ClosingConfirmed(exit, false));
            f.Now = TimeSpan.FromSeconds(10);
            f.Step(new CancelCountdown(b.Id));
            f.Step(new CountdownDue(b.Id));
            f.Step(new Reserved(b.Id, new(b.Id, Target)));
            f.DrainDeliveries();
            Check(f.Count<ShowCountdown>() == 1 && f.History.OfType<HideCountdown>().Single().Id == b.Id
                && f.History.OfType<OperationFinished>().Single() is { Intent: var candidate, Error: OperationCanceledException } && candidate == b
                && f.Count<Reserve>() == (elapsed ? 1 : 0) && f.Count<CommitConfiguration>() == 0
                && (elapsed ? f.History.OfType<Skip>().SingleOrDefault() == new Skip(b, "应用正在退出") && f.Count<ScheduleActivity>() == 0
                    : f.Count<Skip>() == 0 && f.History.OfType<ScheduleActivity>().SingleOrDefault()
                        == new ScheduleActivity(b, $"已取消「{b.ConfigurationName}」的强制启动，当前任务继续")),
                "退出打断倒计时或后续预留时应记录一次原因，撤销退出也不能复活该候选");
        }
    }

    private static void VerifyRunLifecycleClosingPreview()
    {
        var a = Intent(RunSource.Scheduled);
        var b = Intent(RunSource.Scheduled, 2);
        var f = new Scenario();
        f.Claim([a, b]);
        f.Step(new BeginClosing(Id(19), ClosingKind.Exit));
        f.Step(new ClosingConfirmed(Id(19), false));
        f.Step(new Previewed(a.Id, a));
        f.DrainDeliveries();
        Check(f.History.OfType<Skip>().Select(s => (s.Intent, s.Reason)).SequenceEqual([(a, "应用正在退出"), (b, "应用正在退出")])
            && f.History.OfType<OperationFinished>().Single() is { Intent: var first, Error: OperationCanceledException }
            && first == a && f.Count<Reserve>() == 0 && f.Count<AppendStart>() == 0, "退出打断预览，首项与余项均跳过一次");
    }

    private static void VerifyRunLifecycleStoppedOffline()
    {
        foreach (var disconnectBeforeDue in new[] { true, false }) CheckStoppedOffline(disconnectBeforeDue);
    }

    private static void CheckStoppedOffline(bool disconnectBeforeDue)
    {
        var f = new Scenario();
        f.Begin(Intent(RunSource.Launch));
        f.Body();
        f.Active();
        var b = Intent(RunSource.Scheduled, 2);
        f.PreviewSchedule(b, true);
        f.Now += TimeSpan.FromSeconds(10);
        if (!disconnectBeforeDue) f.Step(new CountdownDue(b.Id));
        f.Step(new Stop(Id(8), f.Key));
        f.Step(new StopGateAcquired(Id(8)));
        f.Step(new StopReturned(Id(8), StopResult.TimedOut));
        f.Eof();
        if (disconnectBeforeDue) f.Step(new CountdownDue(b.Id));
        else f.Step(new Reserved(b.Id, new(b.Id, Target)));
        f.Step(new CountdownDue(b.Id));
        var beforeReconnect = f.History.Count;
        f.Reconnect();
        f.QueryResult(f.QueryWire(), true, f.Frame(last: SnapshotOf(f.Send.Attempt, RunState.Cancelled)));
        f.DrainDeliveries();
        var skipped = f.History.OfType<Skip>().SingleOrDefault();
        var finished = f.History.OfType<OperationFinished>().Where(o => o.Intent == b).ToArray();
        Check(skipped == new Skip(b, "正在等待开机任务停止并退出") && f.History.IndexOf(skipped) < beforeReconnect
            && finished.Length == 1 && f.History.IndexOf(finished[0]) < beforeReconnect && f.Count<HideCountdown>() == 1
            && f.History.OfType<Release>().Count(r => r.Lease.Operation == b.Id) == (disconnectBeforeDue ? 0 : 1)
            && f.Items<RequestQuit>().Single().Run == f.Key && f.Count<QueryRun>() == 1
            && f.Count<EndReserved>() == 0 && f.Count<CommitConfiguration>() == 0, "失联手停时 B 到期即跳过，A 独立等待终态后退出");
    }

    private static void VerifyRunLifecycleReplacement()
    {
        foreach (var ended in new[] { false, true }) {
            var f = new Scenario();
            f.Begin(Intent(RunSource.Launch));
            f.Body();
            f.Active();
            var key = f.Key;
            var next = Intent(RunSource.Scheduled, 2);
            f.PreviewSchedule(next, true);
            Check(f.Run.Quit == QuitStage.Bound, "倒计时不解绑");
            f.Now = f.Only<ShowCountdown>().Deadline;
            f.Step(new CountdownDue(next.Id));
            f.Step(new Reserved(next.Id, new(next.Id, Target)));
            f.Step(new CommittedConfiguration(next.Id));
            f.Step(new ActivityAppended(next.Id));
            Check(f.Only<EndReserved>().Replace == key && f.Run.Quit == QuitStage.Suspended, "仅在实际顶替时暂停原绑定");
            f.Terminal(RunState.Succeeded);
            Check(!f.Items<RequestQuit>().Any(), "顶替结果未确认前保留终态");
            f.ExitWorker();
            f.Step(new OldEnvironmentEnded(next.Id, ended));
            Check(f.Count<PrepareEnvironment>() == 0, "旧结果未交付前不得准备新环境");
            f.Ack();
            if (ended) {
                Check(f.Only<PrepareEnvironment>().Intent == next && !f.Items<RequestQuit>().Any(), "顶替确认且旧结论交付后才准备");
            } else {
                Check(f.DeliveredItem<Present>().Kind == NoticeKind.NotStarted, "报告本次换场失败");
                f.Ack();
                Check(f.DeliveredItem<RequestQuit>().Run == key && f.State.QuitPending, "失败恢复原 -q 并处理缓冲终态");
            }
        }

        // The old scheduled Run succeeds while its replacement already owns the reservation.
        var g = new Scenario();
        g.Begin(Intent(RunSource.Scheduled));
        g.Body();
        g.Active();
        var replacement = Intent(RunSource.Scheduled, 3);
        g.Now += TimeSpan.FromMinutes(1);
        g.PreviewSchedule(replacement, true);
        g.Now = g.Only<ShowCountdown>().Deadline;
        g.Step(new CountdownDue(replacement.Id));
        g.Step(new Reserved(replacement.Id, new(replacement.Id, Target)));
        g.Step(new CommittedConfiguration(replacement.Id));
        g.Step(new ActivityAppended(replacement.Id));
        g.Terminal(RunState.Succeeded);
        g.Ack();
        var completion = g.Only<Deliver>();
        g.ExitWorker();
        g.Step(new OldEnvironmentEnded(replacement.Id, true));
        var beforeCompletionReceipt = g.History.Count;
        g.Step(new Delivered(completion.Id));
        g.Step(new Delivered(completion.Id));
        g.Step(new Prepared(replacement.Id, null, new InvalidOperationException("准备失败")));
        g.DrainDeliveries();
        var prepare = new PrepareEnvironment(replacement, false);
        Check(completion.Item is CompleteScheduled result && result.Run == g.Key
            && g.History.IndexOf(prepare) >= beforeCompletionReceipt && g.History.Count(e => e == prepare) == 1
            && g.History.OfType<Release>().Count(r => r.Lease.Operation == replacement.Id) == 1
            && g.Items<CompleteScheduled>().Count() == 1 && !g.Items<Cleanup>().Any(),
            "定时完成仍须等待回执；重复回执不重复准备，已完成的换场消化旧收尾");

        foreach (var stopped in new[] { true, false })
        foreach (var atReservation in new[] { false, true }) {
            var f = new Scenario();
            f.Begin(Intent(RunSource.Launch));
            f.Body();
            f.Active();
            var next = Intent(RunSource.Scheduled, 2);
            f.PreviewSchedule(next, true);
            f.Now = f.Only<ShowCountdown>().Deadline;
            if (atReservation) f.Step(new CountdownDue(next.Id));
            if (stopped) {
                var stop = Id(8);
                f.Step(new Stop(stop, f.Key));
                f.Step(new StopGateAcquired(stop));
                f.Step(new StopReturned(stop, StopResult.TimedOut));
            } else f.Terminal(RunState.Succeeded);
            if (!atReservation) f.Step(new CountdownDue(next.Id));
            f.Step(new Reserved(next.Id, new(next.Id, Target)));
            f.Step(new CommittedConfiguration(next.Id));
            f.Step(new ActivityAppended(next.Id));
            f.DrainDeliveries();
            f.CheckSkipped(next, stopped ? "正在等待开机任务停止并退出" : "应用正在退出");
            Check(f.Count<EndReserved>() == 0 && f.Run.Quit == (stopped ? QuitStage.Bound : QuitStage.Pending)
                && f.History.OfType<Reserve>().Count(r => r.Intent == next) == (atReservation ? 1 : 0)
                && f.History.OfType<Release>().Count(r => r.Lease.Operation == next.Id) == (atReservation ? 1 : 0),
                "倒计时期间自然结束或手停待确认，优先原 Run 的 -q");
            if (stopped) {
                f.Terminal(RunState.Cancelled);
                f.DrainDeliveries();
            }
            Check(f.Items<RequestQuit>().Single().Run == f.Key, "开机终态独立交付一次退出请求");
        }
    }

    private static void VerifyRunLifecycleModalChangeover()
    {
        foreach (var earlierDialog in new[] { false, true }) {
            var f = new Scenario();
            if (earlierDialog)
                f.Step(new Start(Intent(RunSource.Manual, 9), new InvalidOperationException("前一次操作失败")));
            f.Begin(Intent(RunSource.Manual));
            f.Body();
            f.Active();
            var old = f.Key;
            var b = Intent(RunSource.Scheduled, 2);
            var c = Intent(RunSource.Scheduled, 3);
            f.PreviewSchedule(b, true);
            f.Now = TimeSpan.FromSeconds(10);
            f.Step(new CountdownDue(b.Id));
            f.Step(new Reserved(b.Id, new(b.Id, Target)));
            f.Step(new CommittedConfiguration(b.Id));
            f.Step(new ActivityAppended(b.Id));
            f.ExitWorker();
            f.Step(new OldEnvironmentEnded(b.Id, true));
            f.Now = TimeSpan.FromMinutes(1);
            f.Claim([c], true);
            if (f.History.OfType<PrepareEnvironment>().Any(p => p.Intent == b))
                f.Step(new Prepared(b.Id, null, new InvalidOperationException("B 准备失败")));
            f.Step(new Previewed(c.Id, c));
            f.Step(new Reserved(c.Id, new(c.Id, null)));
            f.Step(new CommittedConfiguration(c.Id));
            f.Step(new ActivityAppended(c.Id));
            var beforeDismissal = f.History.ToArray();
            f.CompleteChangeover(c);
            var modal = beforeDismissal.OfType<Deliver>().Single().Item as Present;
            Check(modal?.Channel == PresentationChannel.ForegroundError && (earlierDialog || modal.Kind == NoticeKind.EndUnconfirmed)
                && beforeDismissal.OfType<PrepareEnvironment>().Count(p => p.Intent == b) == (earlierDialog ? 0 : 1)
                && beforeDismissal.OfType<Release>().Count(r => r.Lease.Operation == b.Id) == 1
                && beforeDismissal.OfType<EndReserved>().Select(e => e.Lease.Operation).SequenceEqual([b.Id, c.Id])
                && f.History.OfType<OperationFinished>().Count(o => o.Intent == b && o.Admitted) == 1
                && f.Items<Present>().Count(p => p.Run == old && p.Kind == NoticeKind.EndUnconfirmed) == 1
                && f.Items<CompleteScheduled>().Single().Intent == c && f.Count<Skip>() == 0,
                "旧结论已发出就准备 B；若它排在另一弹框后则释放 B，C 在弹框回执前独立获准");
        }
    }

    private static void VerifyRunLifecycleExitBuffer()
    {
        foreach (var disposition in new[] { "cancel", "failed-before-end", "needs-recovery", "failed-after-end", "takeover", "exit" })
        foreach (var terminalFirst in new[] { false, true }) {
            var f = new Scenario();
            f.Begin(Intent(RunSource.Scheduled));
            f.Body();
            f.Active();
            var exit = Id(8);
            if (terminalFirst) f.Terminal(RunState.Succeeded);
            f.Step(new BeginClosing(exit, disposition == "exit" ? ClosingKind.Exit : ClosingKind.Update));
            f.Expect(typeof(SetClosing), typeof(ConfirmClosing));
            f.Step(new BeginClosing(Id(9), ClosingKind.EndEnvironment));
            f.Expect();
            f.Step(new ClosingConfirmed(Id(9), true));
            f.Expect();
            if (!terminalFirst) f.Terminal(RunState.Succeeded);
            else f.Ack(); // already-dispatched presentation returns during the modal operation
            Check(!f.Items<CompleteScheduled>().Any() && !f.Items<Cleanup>().Any()
                && f.Run.Cleanup == CleanupStage.Claimed, "退出期继续结算，排队的动作要先占位");
            f.Step(new ClosingConfirmed(exit, disposition != "cancel"));
            if (disposition != "cancel") {
                f.Expect(typeof(EndForClosing));
                var ended = disposition is not ("failed-before-end" or "needs-recovery");
                f.Step(new ClosingEndReturned(exit, ended, ended ? Target : null,
                    RecoveryTarget: disposition == "needs-recovery" ? Target : null));
                if (disposition is "failed-after-end" or "takeover") {
                    f.Expect(typeof(HandoffUpdate));
                    f.Step(new HandoffReturned(exit, disposition == "takeover"));
                }
            }
            if (disposition is "takeover" or "exit") {
                f.Expect(typeof(CommitExit));
                f.Step(new Delivered(1));
                f.Step(new Start(Intent(RunSource.Manual, 9)));
                f.Expect();
                Check(f.State.Closing?.Stage == ClosingStage.Committed && f.State.Deliveries.Count == 0
                    && f.Items<Present>().Count() == (terminalFirst ? 1 : 0)
                    && !f.Items<CompleteScheduled>().Any() && !f.Items<Cleanup>().Any(), "真正退出零补发");
                continue;
            }
            Check(f.Last.First() is SetClosing { Value: false }, "恢复视图和环境必须先于补发");
            if (!terminalFirst) {
                Check(f.DeliveredItem<Present>().Kind == NoticeKind.Succeeded, "恢复后先交付通知");
                f.Ack();
            }
            Check(f.DeliveredItem<CompleteScheduled>().Run == f.Key, "补发精确的完成一次");
            f.Ack();
            if (disposition == "needs-recovery") {
                Check(f.DeliveredItem<Present>().Kind == NoticeKind.CleanupFailed && !f.Items<Cleanup>().Any(),
                    "环境结束未确认时只交付一次恢复原因，不能再注销");
                continue;
            }
            Check(f.Items<Cleanup>().Count() == (disposition == "failed-after-end" ? 0 : 1)
                && f.Items<Present>().Count() == 1, "只收尾仍属于该 Run 的原环境");
        }
        var g = new Scenario();
        var i = Intent(RunSource.Manual);
        g.Connect();
        g.Step(new Start(i));
        g.Step(new Reserved(i.Id, new(i.Id, Target)));
        g.Step(new BeginClosing(Id(7), ClosingKind.Exit));
        g.Expect(typeof(SetClosing), typeof(CancelOperation), typeof(OperationFinished), typeof(ConfirmClosing));
        g.Step(new ClosingConfirmed(Id(7), false));
        g.Step(new ReadyChecked(i.Id, Target, i.Input));
        g.Expect();
        Check(g.Count<SendStart>() == 0, "取消退出不能复活旧 Ready 等待");

        var h = new Scenario();
        h.Begin(Intent(RunSource.Scheduled));
        h.Body();
        h.Step(new BeginClosing(Id(7), ClosingKind.Exit));
        h.Step(new ClosingConfirmed(Id(7), false));
        h.Terminal(RunState.Failed);
        h.Ack();
        Check(h.Items<Present>().Count() == 1 && h.Items<CompleteScheduled>().Count() == 1,
            "取消先于终态也必须正常交付");
    }

    private static void VerifyRunLifecycleUnsettledAfterEnd()
    {
        var f = new Scenario();
        f.Begin(Intent(RunSource.Launch));
        f.Body();
        f.Active();
        var a = f.Key;
        var b = Intent(RunSource.Scheduled, 2);
        var c = Intent(RunSource.Scheduled, 3);
        f.PreviewSchedule(b, true);
        f.Now = f.Only<ShowCountdown>().Deadline;
        f.Step(new CountdownDue(b.Id));
        f.Step(new Reserved(b.Id, new(b.Id, Target)));
        f.Step(new CommittedConfiguration(b.Id));
        f.Step(new ActivityAppended(b.Id));
        f.Eof();
        f.Now = TimeSpan.FromMinutes(1);
        f.Claim([c], true);
        f.Step(new OldEnvironmentEnded(b.Id, true));
        f.Step(new Previewed(c.Id, c));
        f.Step(new CancelCountdown(c.Id));
        f.DrainDeliveries();
        var beforeExit = f.History.Count;
        f.ExitWorker();
        f.DrainDeliveries();
        var finished = f.History.OfType<OperationFinished>().Single(o => o.Intent == b);
        Check(finished is { Admitted: true, Error.Message: "旧任务的运行结果尚未确认" }
            && f.History.IndexOf(finished) < beforeExit && f.History.Take(beforeExit).OfType<OperationFinished>().Any(o => o.Intent == c)
            && f.History.OfType<Release>().Count(r => r.Lease.Operation == b.Id) == 1 && f.Count<PrepareEnvironment>() == 0
            && f.Items<Present>().Single(p => p.Run == a).Kind == NoticeKind.EndUnconfirmed && !f.Items<RequestQuit>().Any(),
            "物理结束已返回但旧任务证据未到时应结束 B；证据层仍独立交付 A 的结果");
    }

    private static void VerifyRunLifecycleQuitRecovery()
    {
        foreach (var terminal in new[] { RunState.Succeeded, RunState.Failed, RunState.Cancelled }) {
            var f = new Scenario();
            f.Begin(Intent(RunSource.Launch));
            var wire = f.Body();
            f.Active();
            var window = Id(21);
            f.Step(new BeginClosing(window, ClosingKind.Exit));
            f.Terminal(terminal);
            f.Step(new ClosingConfirmed(window, false));
            if (terminal != RunState.Cancelled) {
                Check(f.DeliveredItem<Present>().Terminal?.State == terminal && !f.Items<RequestQuit>().Any(), "先展示后退出");
                f.Ack();
            }
            var quit = f.Only<Deliver>();
            Check(quit.Item is RequestQuit q && q.Run == f.Key && f.State.QuitPending, "开机三种终态均触发退出");
            f.Step(new Start(Intent(RunSource.Manual, 3)));
            f.Expect(typeof(Skip), typeof(OperationFinished));
            var exit = Id(30);
            f.Step(new BeginClosing(exit, ClosingKind.Exit, f.Key)); // synchronous re-entry from RequestQuit
            f.Step(new Delivered(quit.Id));
            f.Step(new ClosingConfirmed(exit, true));
            f.Step(new ClosingEndReturned(exit, false, Error: new InvalidOperationException("结束失败")));
            f.Reply(wire, StartReplyKind.AlreadyAccepted, "already_accepted");
            Check(f.Run.Quit == QuitStage.AwaitingRecovery && f.Items<RequestQuit>().Count() == 1
                && f.Items<Present>().Count() == (terminal == RunState.Cancelled ? 0 : 1), "退出失败不解绑、不立即循环");
            f.Step(new EnvironmentRecovered());
            Check(f.DeliveredItem<RequestQuit>().Run == f.Key, "随后环境恢复重试 -q");
            f.Step(new EnvironmentRecovered());
            f.Expect();
        }
    }

    private static void VerifyRunLifecycleStopTarget()
    {
        var f = new Scenario();
        f.Begin(Intent(RunSource.Scheduled));
        f.Body();
        f.Active();
        var stop = Id(11);
        f.Step(new Stop(stop, f.Key));
        Check(f.Run.ManualStop == stop && f.Only<AcquireStopGate>().Expected == f.Key, "等闸门前登记目标");
        f.FrameEvent(f.Frame(active: SnapshotOf(Attempt(8), RunState.Running)));
        f.Step(new StopGateAcquired(stop));
        Check(f.Count<SendStop>() == 0 && f.Run.ManualStop == null, "闸门内目标变更不停止后继");
        f.Active();
        f.Step(new Stop(Id(12), f.Key));
        f.Step(new StopGateAcquired(Id(12)));
        Check(f.Only<SendStop>().Expected == f.Key, "发送时再次核对 Run");
        f.Step(new StopReturned(Id(12), StopResult.Rejected));
        Check(f.Run.ManualStop == null, "明确拒绝仅撤销本次停止意图");
        f.Step(new Stop(Id(13), f.Key));
        f.Step(new StopGateAcquired(Id(13)));
        f.Step(new StopReturned(Id(13), StopResult.TimedOut));
        f.FrameEvent(f.Frame(active: SnapshotOf(f.Send.Attempt, RunState.Stopping)));
        Check(f.Run.ManualStop == Id(13) && f.Run.StopProgress == StopProgress.Stopping && !f.Run.Settled,
            "超时和 Stopping 均不能合成终态");
        f.Terminal(RunState.Succeeded);
        f.DrainDeliveries();
        Check(f.Items<CompleteScheduled>().Single().Terminal.State == RunState.Succeeded && !f.Items<Cleanup>().Any(),
            "手停针对原 Run，即使抵达真实成功也保留现场");
        Check(f.History.OfType<SendStop>().Select(s => s.Id).SequenceEqual([Id(12), Id(13)])
            && f.History.OfType<ReleaseStopGate>().Select(s => s.Id).SequenceEqual([stop, Id(12), Id(13)])
            && f.Items<Present>().Select(p => p.Kind).SequenceEqual([NoticeKind.StopFailed, NoticeKind.Succeeded]),
            "换目标和超时均不产生停止失败，只有已发送的拒绝交付一次");
    }

    private static void VerifyRunLifecycleStopRejection()
    {
        foreach (var foreground in new[] { true, false }) {
            var f = new Scenario();
            f.Begin(Intent(RunSource.Scheduled));
            f.Body();
            f.Active();
            var key = f.Key;
            var stop = Id(12);
            f.Step(new Stop(stop, key, Foreground: foreground));
            f.Step(new StopGateAcquired(stop));
            var error = new WorkerProtocolErrorException("worker_faulted", "Worker 已进入故障状态");
            var rejected = new StopReturned(stop, StopResult.Rejected, error);
            // A late rejection still belongs to the stop click, even if its Run has already retired.
            if (!foreground) {
                f.Terminal(RunState.Failed);
                f.ExitWorker();
                f.DrainDeliveries();
            }
            f.Step(rejected);
            if (foreground) f.Terminal(RunState.Failed);
            f.DrainDeliveries();
            f.Step(rejected);
            f.DrainDeliveries();
            var notice = f.Items<Present>().SingleOrDefault(p => p.Kind == NoticeKind.StopFailed);
            Check(notice is { Operation: "停止任务失败" } && notice.Run == key && ReferenceEquals(notice.Error, error)
                && notice.Detail == error.Message && notice.Channel == (foreground ? PresentationChannel.ForegroundError : PresentationChannel.Tray)
                && f.History.OfType<SendStop>().SequenceEqual([new(stop, key)]) && f.Count<ReleaseStopGate>() == 1
                && f.Items<Present>().Count() == 2 && f.Items<CompleteScheduled>().Single().Run == key,
                "Worker 明确拒绝时按本次停止渠道交付原错一次，Run 的终态和定时完成仍各自交付");
        }
    }
}

