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
            Check(f.History.OfType<Reserve>().All(r => r.Intent != next) && f.Count<CommitConfiguration>() == 0,
                "到期先等待开机归属，不能授权换场");
            if (outcome == "terminal-exit") f.ExitWorker();
            else f.QueryResult(query, outcome != "idle-negative",
                f.Frame(active: outcome == "active" ? SnapshotOf(f.Send.Attempt, RunState.Running) : null));
            f.DrainDeliveries();
            if (last != null) {
                Check(f.Items<Present>().Single().Terminal == last && f.Items<RequestQuit>().Single().Run == f.Key
                    && f.Count<EndReserved>() == 0 && f.Count<PrepareEnvironment>() == 0
                    && f.History.OfType<Skip>().Single().Intent == next, "先到的成功须触发开机退出，消费替换机会");
            } else {
                Check(f.History.OfType<Reserve>().Count(r => r.Intent == next) == 1, "查询消除归属疑问后继续复核");
                f.Step(new Reserved(next.Id, new(next.Id, Target)));
                f.Step(new CommittedConfiguration(next.Id));
                f.Step(new ActivityAppended(next.Id));
                Check(f.Only<EndReserved>().Replace == (outcome == "active" ? f.Key : null)
                    && !f.Items<RequestQuit>().Any(), "只顶替查询确认仍在运行的原目标");
            }
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
        scheduled.QueryResult(scheduled.QueryWire(), true, scheduled.Frame(last: SnapshotOf(scheduled.Send.Attempt, RunState.Succeeded)));
        scheduled.DrainDeliveries();
        Check(scheduled.History.OfType<Reserve>().All(r => r.Intent != replacement)
            && scheduled.Items<CompleteScheduled>().Single().Run == scheduled.Key && scheduled.Items<Cleanup>().Count() == 1,
            "已收到的定时终态也要在到期复核中先结算");
    }

    private static void VerifyRunLifecycleClosingReceipts()
    {
        foreach (var stage in new[] { "commit", "activity", "end", "prepare" })
        foreach (var decision in new[] { "resume-first", "confirm-first", "receipt-first" })
        foreach (var success in new[] { true, false }) {
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
                && (success || stage == "activity" || result.Error == error)
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
        Check(g.DeliveredItem<CompleteScheduled>().Run == g.Key, "先交付旧定时结果");
        g.Ack();
        Check(g.Run.Cleanup == CleanupStage.Claimed && !g.Items<Cleanup>().Any(), "换场持有预留时不能再次等待同一闸门");
        g.Step(new OldEnvironmentEnded(replacement.Id, true));
        g.Expect(typeof(ScheduleActivity), typeof(PrepareEnvironment));
        Check(g.Only<PrepareEnvironment>().Intent == replacement && !g.Items<Cleanup>().Any(), "结束已确认，消化旧收尾再准备");

        foreach (var stopped in new[] { true, false }) {
            var f = new Scenario();
            f.Begin(Intent(RunSource.Launch));
            f.Body();
            f.Active();
            var next = Intent(RunSource.Scheduled, 2);
            f.PreviewSchedule(next, true);
            f.Now = f.Only<ShowCountdown>().Deadline;
            if (stopped) {
                var stop = Id(8);
                f.Step(new Stop(stop, f.Key));
                f.Step(new StopGateAcquired(stop));
                f.Step(new StopReturned(stop, StopResult.TimedOut));
            } else f.Terminal(RunState.Succeeded);
            f.Step(new CountdownDue(next.Id));
            f.Step(new Reserved(next.Id, new(next.Id, Target)));
            Check(f.Count<EndReserved>() == 0 && f.Run.Quit == (stopped ? QuitStage.Bound : QuitStage.Pending),
                "倒计时期间自然结束或手停待确认，优先原 Run 的 -q");
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
        f.Ack();
        f.Ack();
        Check(f.Items<CompleteScheduled>().Single().Terminal.State == RunState.Succeeded && !f.Items<Cleanup>().Any(),
            "手停针对原 Run，即使抵达真实成功也保留现场");
    }
}

