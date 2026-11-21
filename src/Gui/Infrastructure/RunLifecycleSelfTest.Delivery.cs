using Gui.Runtime;
using Gui.Worker;
using Protocol;
using static Gui.Runtime.LifecycleEffect;
using static Gui.Runtime.LifecycleEvent;

namespace Gui.Infrastructure;

internal static partial class RunLifecycleSelfTest
{
    private static void VerifyRunLifecycleRetryChannels()
    {
        foreach (var conclusion in new[] { StartConclusion.NotStarted, StartConclusion.StartUnknown, StartConclusion.EndUnconfirmed }) {
            var f = new Scenario();
            f.Begin(Intent(RunSource.Launch));
            f.Reply(f.Body(), StartReplyKind.RejectedBeforeRegistration, "worker_busy");
            var retry = Intent(RunSource.Manual, 2) with { RetryOf = f.Key, OperationName = "重试启动" };
            f.Begin(retry, f.Plan);
            var send = f.Body();
            var error = new InvalidOperationException("本次提交被明确拒绝");
            if (conclusion == StartConclusion.NotStarted) f.Reply(send, StartReplyKind.RejectedBeforeRegistration, "worker_busy", error);
            else {
                if (conclusion == StartConclusion.EndUnconfirmed) f.Active();
                f.ExitWorker();
            }
            f.Reply(send, StartReplyKind.RejectedBeforeRegistration, "worker_busy", error);
            f.Finish(typeof(Present), typeof(Present));
            var notices = f.Items<Present>().ToArray();
            var kind = conclusion switch {
                StartConclusion.NotStarted => NoticeKind.NotStarted,
                StartConclusion.StartUnknown => NoticeKind.StartUnknown,
                _ => NoticeKind.EndUnconfirmed
            };
            var p = notices[1];
            Check(notices[0].Intent?.Source == RunSource.Launch
                && p.Kind == kind && p.Intent == retry && p.Operation == retry.OperationName
                && (kind != NoticeKind.NotStarted || p.Error == error)
                && f.History.OfType<SubmissionConclusion>().Last() is { } c && c.Submission == send.Submission && c.Conclusion == conclusion,
                "已用过主动通知槽的手动重试仍需向本次前台入口交付一次错误");
        }
    }

    private static void VerifyRunLifecycleBootAttribution()
    {
        foreach (var rejected in new[] { true, false }) {
            var f = new Scenario();
            f.Begin(Intent(RunSource.Launch));
            var boot = f.Body();
            if (rejected) f.Reply(boot, StartReplyKind.RejectedBeforeRegistration, "worker_busy");
            else {
                f.Eof();
                f.Reconnect();
                f.QueryResult(f.QueryWire(), false);
            }
            var retry = Intent(RunSource.Manual, 2) with { RetryOf = f.Key };
            f.Begin(retry, f.Plan);
            f.Body();
            f.Eof();
            f.Reconnect();
            f.QueryResult(f.QueryWire(), true, f.Frame(last: SnapshotOf(f.Plan, RunState.Succeeded)));
            f.Finish(typeof(Present));
            f.Conclusions(StartConclusion.NotStarted, StartConclusion.Accepted);
            Check(f.Items<Present>().Single().Intent?.Id == boot.Submission!.IntentId
                && f.History.OfType<SubmissionConclusion>().Select(c => c.Submission).SequenceEqual([boot.Submission, f.Start.Submission])
                && f.State.Runs[f.Key].TerminalState == RunState.Succeeded,
                "开机被排除后手动同 ID 首次登记并丢 ACK，终态不继承 -q 或重开通知槽");
        }

        var g = new Scenario();
        g.Begin(Intent(RunSource.Launch) with { QuitAfterRun = false });
        g.Body();
        g.Terminal(RunState.Succeeded);
        g.Finish(typeof(Present));
    }

    private static void VerifyRunLifecycleScheduledTerminals()
    {
        foreach (var status in new[] { RunState.Succeeded, RunState.Failed, RunState.Cancelled }) {
            var f = new Scenario();
            var intent = Intent(RunSource.Scheduled);
            f.Begin(intent);
            f.Body();
            var active = f.Frame(active: SnapshotOf(f.Plan, RunState.Running));
            f.FrameEvent(active);
            var terminal = SnapshotOf(f.Plan, status);
            var gap = f.Frame(last: terminal) with { StateRevision = active.StateRevision + 2 };
            var stale = f.State.Current with { SnapshotFresh = false };
            f.FrameEvent(gap, stale);
            f.FrameEvent(gap, stale);
            if (status == RunState.Succeeded) {
                f.Ack(f.History.OfType<Deliver>().Single(d => d.Item is Present).Id);
                var complete = f.History.OfType<Deliver>().Single(d => d.Item is CompleteScheduled);
                Check(complete.Item == new CompleteScheduled(f.Key, intent, terminal)
                    && !f.Items<RequestCleanup>().Any(), "跳号帧应交付原 Run 的定时完成，回执前不能请求收尾");
                f.Ack(complete.Id);
                Check(f.Items<RequestCleanup>().Single().Run == f.Key, "定时完成回执后应恰好请求一次原 Run 的收尾");
            }
            f.Finish(status switch {
                RunState.Succeeded => [typeof(Present), typeof(CompleteScheduled), typeof(RequestCleanup)],
                RunState.Failed => [typeof(Present), typeof(CompleteScheduled)],
                _ => [typeof(CompleteScheduled)]
            });
            Check(f.Items<CompleteScheduled>().Single() == new CompleteScheduled(f.Key, intent, terminal)
                && f.Items<RequestCleanup>().All(item => item.Run == f.Key)
                && f.Items<Present>().All(p => p.Terminal == terminal && p.Operation.Contains(intent.ConfigurationName)),
                "定时完成传递原终态，成功才交付对应 Run 的收尾请求");
        }
    }

    private static void VerifyRunLifecycleAbandonQuit()
    {
        var queued = new Scenario();
        queued.Begin(Intent(RunSource.Launch));
        queued.Body();
        queued.Terminal(RunState.Succeeded);
        var present = queued.History.OfType<Deliver>().Single();
        Check(present.Item is Present { Kind: NoticeKind.Succeeded } && present.Item.Key == queued.Key
            && queued.State.Deliveries.Single().Item == new RequestQuit(queued.Key), "成功通知等待回执时，原 Run 的退出请求应已入队");
        queued.Step(new AbandonQuit(queued.Key));
        queued.Ack(present.Id);
        queued.Finish(typeof(Present));

        foreach (var status in new[] { RunState.Cancelled, RunState.Failed }) {
            var f = new Scenario();
            f.Begin(Intent(RunSource.Launch));
            var first = f.Start;
            var wire = f.Body();
            f.Step(new AbandonQuit(first.Key));
            f.Terminal(status);
            f.Reply(wire, StartReplyKind.Accepted, "accepted");
            f.Begin(Intent(RunSource.Launch, 2));
            f.Body();
            f.Step(new AbandonQuit(first.Key));
            f.Terminal(RunState.Succeeded);
            f.Finish(status == RunState.Failed ? [typeof(Present), typeof(Present), typeof(RequestQuit)]
                : [typeof(Present), typeof(RequestQuit)]);
            f.Conclusions(StartConclusion.Accepted, StartConclusion.Accepted);
            Check(f.Items<RequestQuit>().Single().Key == f.Key
                && f.Items<Present>().Select(p => p.Key).SequenceEqual(status == RunState.Failed ? [first.Key, f.Key] : [f.Key])
                && f.History.OfType<SubmissionConclusion>().Select(c => c.Submission).SequenceEqual([first.Submission, f.Start.Submission]),
                "只撤销指定开机 Run 的退出资格，保留原提交结论与终态提醒");
        }
    }

    private static void VerifyRunLifecycleQuitRecovery()
    {
        foreach (var recoveredFirst in new[] { false, true }) {
            var idle = new Scenario();
            idle.Begin(Intent(RunSource.Launch));
            idle.Body();
            idle.Terminal(RunState.Cancelled);
            idle.Finish(typeof(RequestQuit));
            idle.Step(new ClosingStarted(Id(69)));
            if (recoveredFirst) idle.Step(new EnvironmentRecovered());
            idle.Finish(typeof(RequestQuit));
            idle.Step(new ClosingCancelled(Id(69)));
            if (!recoveredFirst) {
                idle.Terminal(RunState.Cancelled);
                idle.Finish(typeof(RequestQuit));
                idle.Step(new EnvironmentRecovered());
            }
            idle.Finish(typeof(RequestQuit), typeof(RequestQuit));
            idle.Step(new EnvironmentRecovered());
            idle.Finish(typeof(RequestQuit), typeof(RequestQuit));
        }

        foreach (var ackFirst in new[] { true, false })
        foreach (var commit in new[] { false, true }) {
            var f = new Scenario();
            f.Begin(Intent(RunSource.Launch));
            var boot = f.Key;
            f.Body();
            f.Terminal(RunState.Succeeded);
            f.Ack(f.History.OfType<Deliver>().Single().Id);
            var quit = f.History.OfType<Deliver>().Single(d => d.Item is RequestQuit);
            if (ackFirst) f.Ack(quit.Id);
            f.Step(new ClosingStarted(Id(70)));
            f.Begin(Intent(RunSource.Scheduled, 2));
            f.Body();
            f.Terminal(RunState.Succeeded);
            f.ExitWorker();
            f.Step(commit ? new ClosingCommitted(Id(70)) : new ClosingCancelled(Id(70)));
            Check(f.Items<RequestQuit>().Count() == 1, "取消退出不能立即循环请求 -q");
            f.Step(new EnvironmentRecovered());
            f.Step(new EnvironmentRecovered());
            if (!commit) {
                foreach (var kind in new[] { typeof(Present), typeof(CompleteScheduled), typeof(RequestCleanup) }) {
                    var delivery = f.History.OfType<Deliver>().Single(d => d.Item.Key == f.Key && d.Item.GetType() == kind);
                    Check(f.Items<RequestQuit>().Count() == 1, "恢复后应先排空所有非退出交付");
                    f.Ack(delivery.Id);
                }
                Check(f.Items<RequestQuit>().Count() == 2, "恢复后应再次派发一次退出请求");
                var retry = f.History.OfType<Deliver>().Last();
                f.Ack(quit.Id);
                Check(f.State.InFlightDelivery == retry, "旧退出回执不能确认重新派发的请求");
                f.Ack(retry.Id);
            }
            f.Step(new EnvironmentRecovered());
            f.Finish(commit ? [typeof(Present), typeof(RequestQuit)]
                : [typeof(Present), typeof(RequestQuit), typeof(Present), typeof(CompleteScheduled), typeof(RequestCleanup), typeof(RequestQuit)]);
            Check(f.Items<RequestQuit>().All(q => q.Key == boot) && f.State.ExitCommitted == commit,
                "恢复只重发原 Run 的退出，提交退出后清除资格");
        }
    }

    private static void VerifyRunLifecycleCompletedPlans()
    {
        var f = new Scenario();
        var expected = new List<Type>();
        var submissions = new List<Submission>();
        for (int n = 1; n <= 12; ++n) {
            f.Begin(Intent(RunSource.Manual, n));
            submissions.Add(f.Start.Submission);
            var wire = f.Body();
            f.Terminal(RunState.Succeeded);
            expected.Add(typeof(Present));
            f.Finish(expected.ToArray());
            f.Reply(wire, StartReplyKind.Accepted, "accepted");
            f.Step(f.Start);
            f.Terminal(RunState.Succeeded);
        }
        f.Finish(expected.ToArray());
        Check(f.History.OfType<SubmissionConclusion>().Select(c => c.Submission).SequenceEqual(submissions)
            && f.Items<Present>().Select(p => p.Key).SequenceEqual(submissions.Select(s => s.Run))
            && f.State.Runs.Count == submissions.Count && f.State.Runs.Values.All(r => r.Settled
                && r.TerminalState == RunState.Succeeded && r.Notification == NotificationSlot.Delivered
                && r.Attempt == null && r.Sends.IsEmpty && r.Terminal == null),
            "同一 Worker 的已完成 Run 只保留轻量身份、结论与通知状态，不累计计划或发送记录");
    }

    private static void VerifyRunLifecycleRejectedPlans()
    {
        var f = new Scenario();
        var submissions = new List<Submission>();
        for (int n = 1; n <= 12; ++n) {
            f.Begin(Intent(RunSource.Manual, n));
            submissions.Add(f.Start.Submission);
            var wire = f.Body();
            var error = new InvalidOperationException("计划超过快照大小预算");
            f.Reply(wire, StartReplyKind.RejectedBeforeRegistration, "invalid_run_plan", error);
            var notice = f.History.OfType<Deliver>().Last();
            Check(notice.Item is Present { Kind: NoticeKind.NotStarted } p && p.Key == f.Key && p.Error == error,
                "每份无效计划都应向原提交交付拒绝原因");
            f.Ack(notice.Id);
            f.Step(f.Start);
            f.Reply(wire, StartReplyKind.RejectedBeforeRegistration, "invalid_run_plan", error);
        }
        f.Finish(Enumerable.Repeat(typeof(Present), submissions.Count).ToArray());
        Check(f.History.OfType<SubmissionConclusion>().Select(c => c.Submission).SequenceEqual(submissions)
            && f.State.Runs.Count == submissions.Count
            && submissions.All(s => f.State.Runs[s.Run] is { Conclusion: StartConclusion.NotStarted, Owner: null,
                Attempt: null, Sends.IsEmpty: true, RejectionCode: "invalid_run_plan" } r && r.Submission == s),
            "同一 Worker 连续拒绝无效计划后应保留提交身份，累计保留的完整计划、发送记录和异常均应为零");
    }

    private static void VerifyRunLifecycleObservedRuns()
    {
        foreach (var initial in new string?[] { "周常", null, "", " " })
        foreach (var status in new[] { RunState.Failed, RunState.Succeeded }) {
            var f = new Scenario { ConfigurationName = "历史记录" };
            f.FrameEvent(f.Frame(last: SnapshotOf(Attempt(3), status)));
            var external = Attempt(8);
            var active = f.Frame(SnapshotOf(external, RunState.Running));
            f.ConfigurationName = initial;
            f.FrameEvent(active);
            f.ConfigurationName = "周常";
            f.FrameEvent(active);
            f.ConfigurationName = "已改名";
            f.FrameEvent(active);
            var terminal = f.Frame(last: SnapshotOf(external, status));
            f.FrameEvent(terminal);
            f.FrameEvent(terminal);
            f.Finish(typeof(Present));
            f.Conclusions();
            var notice = f.Items<Present>().Single();
            Check(notice.Intent == null && notice.Terminal == terminal.LastRun
                && notice.Operation == (status == RunState.Succeeded ? "「周常」已完成" : "「周常」运行失败"),
                "外部 Active 冻结观察期间首次非空配置名，历史 Last 不补发");
        }
    }

    private static void VerifyRunLifecycleFaultedSlot()
    {
        var f = new Scenario();
        f.Begin(Intent(RunSource.Launch));
        f.Body();
        f.Active();
        f.Active(WorkerState.Faulted);
        f.Active(WorkerState.Faulted);
        f.Ack(f.History.OfType<Deliver>().Single().Id);
        f.ExitWorker();
        f.Finish(typeof(Present));
        f.Conclusions(StartConclusion.Accepted, StartConclusion.EndUnconfirmed);
        Check(f.Items<Present>().Single() is { Kind: NoticeKind.Faulted, Terminal: null } p && p.Detail.Contains("结束桌面分身"),
            "已观察 Active 才提示恢复，进程退出不能再占用同一个通知槽");

        foreach (var status in new RunState?[] { null, RunState.Succeeded, RunState.Cancelled }) {
            var race = new Scenario();
            race.Begin(Intent(RunSource.Manual));
            race.Body();
            race.Active();
            race.Active(WorkerState.Faulted);
            var fault = race.History.OfType<Deliver>().Single();
            if (status.HasValue) race.Terminal(status.Value);
            else race.ExitWorker();
            race.Ack(fault.Id, presented: false);
            race.Finish(status == RunState.Cancelled ? [typeof(Present)] : [typeof(Present), typeof(Present)]);
            var final = race.Items<Present>().Skip(1).SingleOrDefault();
            Check(final?.Kind == (status switch {
                RunState.Succeeded => NoticeKind.Succeeded, RunState.Cancelled => null, _ => NoticeKind.EndUnconfirmed
            }), "未呈现的 Faulted 回执晚于结算时，仍应交付最终结论，取消保持静默");
        }

        var g = new Scenario();
        var external = Attempt(8);
        var frame = g.Frame(SnapshotOf(external, RunState.Running), worker: WorkerState.Faulted);
        g.FrameEvent(frame);
        g.FrameEvent(frame);
        g.FrameEvent(g.Frame(last: SnapshotOf(external, RunState.Failed)));
        g.Finish(typeof(Present));
        Check(g.Items<Present>().Single().Kind == NoticeKind.Failed, "接管时已 Faulted 不补发故障提醒");
    }

    private static void VerifyRunLifecycleReconnectFault()
    {
        foreach (var outcome in new[] { "still-faulted", "terminal", "process-exit", "terminal-exit", "faulted-first" }) {
            var f = new Scenario();
            f.Begin(Intent(RunSource.Launch));
            f.Body();
            f.Eof();
            var active = SnapshotOf(f.Plan, RunState.Running);
            var fault = f.Frame(active, worker: WorkerState.Faulted);
            f.Reconnect(outcome == "faulted-first" ? fault : f.Frame(active));
            f.FrameEvent(fault);
            f.FrameEvent(fault);
            var terminal = outcome is "terminal" or "terminal-exit";
            if (terminal) f.Terminal(RunState.Succeeded);
            var beforeConfirmation = f.History.Count;
            if (outcome is "process-exit" or "terminal-exit") f.ExitWorker();
            else f.QueryResult(f.QueryWire(), true, terminal ? f.Frame(last: SnapshotOf(f.Plan, RunState.Succeeded)) : fault);
            f.Finish(outcome == "faulted-first" ? [] : terminal ? [typeof(Present), typeof(RequestQuit)] : [typeof(Present)]);
            var expected = outcome switch {
                "process-exit" => NoticeKind.EndUnconfirmed, "terminal-exit" => NoticeKind.Succeeded, _ => NoticeKind.Faulted
            };
            Check(!f.History.Take(beforeConfirmation).OfType<Deliver>().Any() && f.History.OfType<QueryRun>().Count() == 1
                && f.Items<Present>().All(p => p.Kind == expected && p.Run == f.Key), "重连后的故障转变与真实终态分别结算");
        }
    }

    private static void VerifyRunLifecycleBufferedFaultExit()
    {
        var manual = new Scenario();
        manual.Begin(Intent(RunSource.Manual));
        var timeout = new TimeoutException("原始应答超时");
        manual.Evidence(new(manual.Connection, WorkerEvidenceKind.RequestFailed, manual.Body(), Error: timeout));
        manual.Active();
        manual.Step(new ClosingStarted(Id(18)));
        manual.Active(WorkerState.Faulted);
        manual.ExitWorker();
        manual.Step(new ClosingCancelled(Id(18)));
        manual.Finish(typeof(Present));
        Check(manual.Items<Present>().Single() is { Kind: NoticeKind.EndUnconfirmed, Intent.Source: RunSource.Manual } notice && notice.Error == timeout,
            "退出期间由 Faulted 转为运行未确认的手动任务应补发原异常到前台");

        foreach (var status in new RunState?[] { null, RunState.Succeeded, RunState.Cancelled }) {
            var f = new Scenario();
            f.Begin(Intent(RunSource.Launch));
            f.Body();
            f.Active();
            f.Step(new ClosingStarted(Id(19)));
            f.Active(WorkerState.Faulted);
            if (status.HasValue) f.Terminal(status.Value);
            f.ExitWorker();
            var beforeResume = f.History.Count;
            f.Step(new ClosingCancelled(Id(19)));
            f.Finish(status switch {
                RunState.Cancelled => [typeof(RequestQuit)], RunState.Succeeded => [typeof(Present), typeof(RequestQuit)],
                _ => [typeof(Present)]
            });
            Check(!f.History.Take(beforeResume).OfType<Deliver>().Any() && f.Items<Present>().All(p =>
                p.Kind == (status.HasValue ? NoticeKind.Succeeded : NoticeKind.EndUnconfirmed)),
                "恢复交付时已退出的 Worker 不应再提示结束分身");
        }
    }

    private static void VerifyRunLifecycleExitBuffer()
    {
        foreach (var commit in new[] { false, true })
        foreach (var terminalFirst in new[] { false, true }) {
            var f = new Scenario();
            var closing = Id(77);
            f.Begin(Intent(RunSource.Scheduled));
            f.Body();
            if (terminalFirst) f.Terminal(RunState.Succeeded);
            f.Step(new ClosingStarted(closing));
            if (terminalFirst) f.Ack(f.History.OfType<Deliver>().Single().Id);
            else f.Terminal(RunState.Succeeded);
            f.Step(new ClosingCommitted(Id(78)));
            f.Step(new ClosingCancelled(Id(78)));
            var paused = f.History.Count;
            f.Step(commit ? new ClosingCommitted(closing) : new ClosingCancelled(closing));
            f.Step(new ClosingCancelled(closing));
            f.Terminal(RunState.Succeeded);
            f.Finish(commit ? (terminalFirst ? [typeof(Present)] : []) : [typeof(Present), typeof(CompleteScheduled), typeof(RequestCleanup)]);
            Check(f.History.Take(paused).OfType<Deliver>().Count() == (terminalFirst ? 1 : 0)
                && f.State.ExitCommitted == commit, "退出前已派发的回执不阻塞，旧代回执不解冻当前交付");
        }

        // Dispatch a quit only after preceding consumers acknowledge; the callback may close synchronously.
        var g = new Scenario();
        g.Begin(Intent(RunSource.Launch));
        g.Body();
        g.Terminal(RunState.Failed);
        var first = g.History.OfType<Deliver>().Single();
        g.Ack(first.Id + 1);
        var beforeReceipt = g.History.Count;
        g.Ack(first.Id);
        g.Step(new ClosingStarted(Id(7)));
        g.Ack(first.Id);
        g.Ack(g.History.OfType<Deliver>().Last().Id);
        g.Step(new ClosingCommitted(Id(7)));
        g.Finish(typeof(Present), typeof(RequestQuit));
        Check(first.Item is Present { Kind: NoticeKind.Failed } && g.Items<RequestQuit>().Single().Run == g.Key
            && !g.History.Take(beforeReceipt).OfType<Deliver>().Any(d => d.Item is RequestQuit),
            "同步 -q 回调前已交付原 Run 通知，重复回执不重复退出");

        foreach (var commit in new[] { false, true }) {
            var h = new Scenario();
            h.Begin(Intent(RunSource.Launch));
            h.Body();
            h.Step(new ClosingStarted(Id(8)));
            h.Terminal(RunState.Cancelled);
            var paused = h.History.Count;
            h.Step(commit ? new ClosingCommitted(Id(8)) : new ClosingCancelled(Id(8)));
            h.Finish(commit ? [] : [typeof(RequestQuit)]);
            Check(!h.History.Take(paused).OfType<Deliver>().Any(), "取消的开机 Run 也应缓冲 -q");
        }
    }
}
