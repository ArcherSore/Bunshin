using Gui.Runtime;
using Gui.Worker;
using Protocol;
using static Gui.Runtime.LifecycleEffect;
using static Gui.Runtime.LifecycleEvent;

namespace Gui.Infrastructure;

internal static partial class RunLifecycleSelfTest
{
    private static void VerifyRunLifecycleSendFailure()
    {
        foreach (var exits in new[] { false, true }) {
            var f = new Scenario();
            f.Begin(Intent(RunSource.Manual));
            var writer = f.Writer();
            f.Evidence(new(f.Connection, WorkerEvidenceKind.BeforeBody, writer));
            var failure = new System.IO.IOException("正文前写入失败");
            if (exits) f.ExitWorker();
            else {
                f.Eof(new System.IO.IOException("读端断开"));
                f.Evidence(new(writer.Connection, WorkerEvidenceKind.NotSent, writer, Error: failure));
            }
            f.DrainDeliveries();
            var result = f.History.OfType<OperationFinished>().Single();
            Check(result.Run == f.Key && result.Error != null && (exits || result.Error == failure)
                && f.Items<Present>().Single() is { Kind: NoticeKind.NotStarted, Error: var error }
                && error == result.Error && f.Count<Release>() == 1, "未能启动不能回报空错误，写前失败保留原异常");
        }
    }

    private static void VerifyRunLifecycleStaleActiveBackoff()
    {
        var f = new Scenario();
        f.Begin(Intent(RunSource.Manual));
        f.Body();
        var old = f.Frame(active: SnapshotOf(f.Send.Attempt, RunState.Running));
        f.FrameEvent(old);
        f.FrameEvent(f.Frame(active: SnapshotOf(Attempt(8), RunState.Running)));
        foreach (var seconds in new[] { 1, 2, 4 }) {
            var count = f.Count<QueryRun>();
            f.Timeout(f.QueryWire());
            var timer = f.Only<ArmRetry>();
            Check(timer.Due == f.Now + TimeSpan.FromSeconds(seconds), "历史 Active 不能重置退避次数");
            f.Now = timer.Due - TimeSpan.FromTicks(1);
            f.FrameEvent(old, f.State.Current);
            f.FrameEvent(old, f.State.Current);
            Check(f.Count<QueryRun>() == count && f.Count<CancelRetry>() == 0, "低修订 Active 到期前不能再查询");
            f.Now = timer.Due;
            f.Step(new RetryDue(f.Key, timer.Token));
            Check(f.Count<QueryRun>() == count + 1 && f.Count<SendStart>() == 1, "到期只发一次定向查询");
        }
    }

    private static void VerifyRunLifecycleRejectedRetry()
    {
        foreach (var code in new[] { "invalid_run_plan", "invalid_request", "run_id_conflict", "worker_busy",
            "worker_not_ready", "operation_not_allowed", "worker_faulted", "protocol_version_mismatch" }) {
            var f = new Scenario();
            var a = Intent(RunSource.Manual);
            if (code == "invalid_run_plan") {
                var attempt = a.Attempt!;
                var item = attempt.Plan.Items.Single();
                a = a with { Attempt = attempt with { Plan = attempt.Plan with { Items = [item, item] } } };
            }
            f.Begin(a);
            var error = new InvalidOperationException(code);
            f.Reply(f.Body(), StartReplyKind.RejectedBeforeRegistration, code, error);
            f.Ack();
            var retry = Intent(RunSource.Manual, 2) with { Mode = StartMode.Retry, RetryOf = f.Key, Attempt = null };
            f.Step(new Start(retry));
            f.Step(new Reserved(retry.Id, new(retry.Id, Target)));
            f.Step(new ReadyChecked(retry.Id, Target, retry.Input));
            f.DrainDeliveries();
            if (code is "invalid_run_plan" or "invalid_request" or "run_id_conflict") {
                Check(f.Count<SendStart>() == 1 && f.Items<Present>().Last() is { Intent: var intent, Error: var actual }
                    && intent?.Id == retry.Id && actual == error, $"{code} 必须向本次点击显示原错，不重发");
            } else {
                Check(f.Count<SendStart>() == 2 && ReferenceEquals(f.Send.Attempt, a.Attempt)
                    && f.Send.Submission.IntentId == retry.Id, $"{code} 在同 Worker 已 Ready 后允许复用原计划");
            }
        }
    }

    private static void VerifyRunLifecycleFaultedTakeover()
    {
        var f = new Scenario();
        var external = Attempt(8);
        var frame = f.Frame(active: SnapshotOf(external, RunState.Running), worker: WorkerState.Faulted);
        f.FrameEvent(frame);
        f.FrameEvent(frame);
        f.DrainDeliveries();
        Check(!f.Items<Present>().Any(), "重复接管时已故障的外部 Active 不能补故障提醒");
        f.FrameEvent(f.Frame(last: SnapshotOf(external, RunState.Failed)));
        f.DrainDeliveries();
        Check(f.Items<Present>().Single().Kind == NoticeKind.Failed, "仍能通知观察期间收到的真实终态");
    }

    private static void VerifyRunLifecycleReconnectFault()
    {
        foreach (var outcome in new[] { "still-faulted", "terminal", "process-exit", "faulted-first" }) {
            var f = new Scenario();
            f.Begin(Intent(RunSource.Launch));
            f.Body();
            f.Eof();
            f.Connection = f.Connection with { Generation = 2 };
            var active = SnapshotOf(f.Send.Attempt, RunState.Running);
            if (outcome != "faulted-first") f.FrameEvent(f.Frame(active));
            var fault = f.Frame(active, worker: WorkerState.Faulted);
            f.FrameEvent(fault);
            f.FrameEvent(fault);
            if (outcome == "terminal") f.Terminal(RunState.Succeeded);
            var beforeConfirmation = f.History.Count;
            if (outcome == "process-exit") f.ExitWorker();
            else f.QueryResult(f.QueryWire(), true, outcome == "terminal"
                ? f.Frame(last: SnapshotOf(f.Send.Attempt, RunState.Succeeded)) : fault);
            f.DrainDeliveries();
            var notices = f.Items<Present>().ToArray();
            Check((outcome == "faulted-first" ? notices.Length == 0
                    : notices.Length == 1 && notices[0].Kind == NoticeKind.Faulted && notices[0].Run == f.Key)
                && !f.History.Take(beforeConfirmation).OfType<Deliver>().Any()
                && f.Count<OperationFinished>() == 1 && f.Count<Release>() == 1 && f.Count<QueryRun>() == 1
                && f.Items<RequestQuit>().Count() == (outcome == "terminal" ? 1 : 0),
                $"重连 {outcome} 应保留实际观察到的故障转变，并独立结算终态");
        }
    }

    private static void VerifyRunLifecycleSourceConclusions()
    {
        foreach (var source in Enum.GetValues<RunSource>())
        foreach (var conclusion in Enum.GetValues<StartConclusion>()) {
            var f = new Scenario();
            f.Begin(Intent(source));
            if (conclusion == StartConclusion.NotStarted) {
                f.Evidence(new(null, WorkerEvidenceKind.NotSent, f.Writer() with { Connection = null },
                    Error: new InvalidOperationException("正文前失败")));
            } else {
                var wire = f.Body();
                if (conclusion != StartConclusion.StartUnknown) f.Reply(wire, StartReplyKind.Accepted, "accepted");
                if (conclusion != StartConclusion.Accepted) {
                    f.Eof();
                    Check(!f.Items<Present>().Any(), "可恢复断线不产生未知提醒");
                    f.ExitWorker();
                }
            }
            Check(f.Run.Conclusion == conclusion && f.Count<SendStart>() == 1, $"{source} / {conclusion} 的分类或发送次数错误");
            if (conclusion == StartConclusion.Accepted) {
                Check(!f.Items<Present>().Any(), "接纳不能单独弹出通知");
                f.Terminal(RunState.Succeeded);
                f.DrainDeliveries();
                Check(f.Items<Present>().Single().Kind == NoticeKind.Succeeded, "已接纳应跟随真实终态");
                Check(f.Items<RequestQuit>().Count() == (source == RunSource.Launch ? 1 : 0)
                    && f.Items<CompleteScheduled>().Count() == (source == RunSource.Scheduled ? 1 : 0)
                    && f.Items<Cleanup>().Count() == (source == RunSource.Scheduled ? 1 : 0), "成功只交付给本次来源");
            } else {
                var kind = conclusion switch {
                    StartConclusion.NotStarted => NoticeKind.NotStarted,
                    StartConclusion.StartUnknown => NoticeKind.StartUnknown,
                    _ => NoticeKind.EndUnconfirmed
                };
                f.DrainDeliveries();
                var notice = f.Items<Present>().Single();
                Check(notice.Kind == kind && notice.Channel == (source == RunSource.Manual
                    ? PresentationChannel.ForegroundError : PresentationChannel.Tray) && notice.Intent?.ConfigurationName == "第一班"
                    && !f.Items<RequestQuit>().Any() && !f.Items<CompleteScheduled>().Any() && !f.Items<Cleanup>().Any(),
                    "来源、呈现或消费者错误");
            }
        }
    }

    private static void VerifyRunLifecycleConfirmationClock()
    {
        var f = new Scenario();
        f.Begin(Intent(RunSource.Scheduled));
        var send = f.Body();
        f.Timeout(send);
        f.Expect(typeof(Release), typeof(OperationFinished), typeof(QueryRun));
        Check(f.Only<OperationFinished>().Error is TimeoutException && !f.Items<Present>().Any()
            && f.Only<QueryRun>().Connection == f.Connection, "超时返回原异常，同时开始确认，不产生启动失败通知");
        WorkerRequestEvidence? prior = null;
        foreach (var delay in new[] { 1, 2, 4, 8, 16, 30, 30, 30 }) {
            var query = f.QueryWire();
            var count = f.Count<QueryRun>();
            if (prior != null) {
                f.Timeout(prior);
                f.Expect();
                Check(f.Run.Confirmation.InFlight == query.QueryId, "迟到失败不能清掉新查询");
            }
            f.Timeout(send);
            Check(f.Count<QueryRun>() == count, "确认触发必须合并");
            f.Timeout(query);
            var timer = f.Only<ArmRetry>();
            f.Expect(typeof(ArmRetry));
            Check(f.Run.Confirmation.InFlight == null && timer.Due == f.Now + TimeSpan.FromSeconds(delay),
                "从本次失败开始退避，等待器应已释放");
            f.Now = timer.Due - TimeSpan.FromTicks(1);
            f.Step(new RetryDue(f.Key, timer.Token));
            f.Expect();
            f.Now = timer.Due;
            f.Step(new RetryDue(f.Key, timer.Token));
            f.Expect(typeof(QueryRun));
            f.Step(new RetryDue(f.Key, timer.Token));
            f.Expect();
            prior = query;
        }
        // The older query's definitive evidence can settle its send without timing out the newer query.
        f.QueryResult(prior!, false);
        Check(f.Run.Conclusion == StartConclusion.NotStarted && f.Run.Confirmation.InFlight == null
            && f.Items<Present>().Single().Kind == NoticeKind.NotStarted && f.Count<SendStart>() == 1
            && f.Only<CancelQuery>().Id == f.History.OfType<QueryRun>().Last().Id, "连续丢 ACK 只提交一次，结算撤销更新的查询");
        f.QueryResult(f.QueryWire(), false);
        f.Expect();
        f.Ack();
        var next = Intent(RunSource.Scheduled, 2);
        f.PreviewSchedule(next);
        f.Step(new Reserved(next.Id, new(next.Id, Target)));
        f.Step(new CommittedConfiguration(next.Id));
        Check(f.State.Operations[next.Id].Admitted && f.Count<SendStart>() == 1, "下一个普通机会应获准");
    }

    private static void VerifyRunLifecycleAtomicRunning()
    {
        var f = new Scenario();
        f.Begin(Intent(RunSource.Manual));
        var wire = f.Body();
        f.Timeout(wire);
        var b = SnapshotOf(Attempt(2), RunState.Running);
        f.QueryResult(f.QueryWire(), false, f.Frame(active: b));
        Check(f.Run.Conclusion == StartConclusion.NotStarted && f.State.Current.WorkerSnapshot?.ActiveRun == b,
            "清除 A 待确认和发布 B 的 Running 必须是同一转换");
        var only = Intent(RunSource.Scheduled, 3);
        f.Claim([only]);
        f.Expect(typeof(Skip), typeof(OperationFinished));
        Check(f.Only<Skip>() is { Intent: var skipped, Reason: "正在运行其他任务" } && skipped == only
            && f.Count<SendStop>() == 0 && f.Count<EndReserved>() == 0, "唯一候选应记下 B 正在运行的跳过原因");

        var g = new Scenario();
        g.Connect();
        g.PreviewSchedule(only);
        g.FrameEvent(g.Frame(active: b)); // Running arrives during the reserve executor's session inspection.
        g.Step(new Reserved(only.Id, new(only.Id, Target)));
        Check(g.Only<Skip>().Intent == only && g.Count<CommitConfiguration>() == 0 && g.Count<SendStop>() == 0
            && g.Count<EndReserved>() == 0, "预留期间 Running 先到，必须在同一实时视图复核");
    }

    private static void VerifyRunLifecycleLatePositive()
    {
        foreach (var source in new[] { RunSource.Launch, RunSource.Scheduled })
        foreach (var replyFirst in new[] { true, false }) {
            var f = new Scenario();
            f.Begin(Intent(source));
            var send = f.Body();
            f.Timeout(send);
            f.Connect();
            Check(!f.Run.Settled && !f.Items<Present>().Any(), "普通空闲快照不能排除 A");
            if (replyFirst) f.Reply(send, StartReplyKind.AlreadyAccepted, "already_accepted");
            f.Terminal(RunState.Failed);
            f.Reply(send, StartReplyKind.Accepted, "accepted");
            f.Ack();
            Check(f.Items<Present>().Single().Kind == NoticeKind.Failed && f.Count<SendStart>() == 1
                && f.Items<RequestQuit>().Count() == (source == RunSource.Launch ? 1 : 0)
                && f.Items<CompleteScheduled>().Count() == (source == RunSource.Scheduled ? 1 : 0),
                "空闲之后的真实结论须交给原始消费者");
        }
    }

    private static void VerifyRunLifecycleBeforeBodyExit()
    {
        foreach (var cause in new[] { "序列化", "大小越界", "断开", "只写出前缀" }) {
            var f = new Scenario();
            f.Begin(Intent(RunSource.Launch));
            var failure = new InvalidOperationException(cause);
            var writer = f.Writer();
            f.Evidence(new(f.Connection, WorkerEvidenceKind.BeforeBody, writer));
            f.Evidence(new(f.Connection, WorkerEvidenceKind.NotSent, writer, Error: failure));
            f.ExitWorker();
            Check(f.Run.Conclusion == StartConclusion.NotStarted && f.Items<Present>().Single().Error == failure
                && f.Run.Quit == QuitStage.None && !f.Items<RequestQuit>().Any(), cause);
            f.Ack();
            Check(!f.State.Runs.ContainsKey(f.Key), "退休 Worker 的交付已排空，应回收计划及发送身份");
        }
        var g = new Scenario();
        g.Begin(Intent(RunSource.Manual));
        g.ExitWorker();
        Check(g.Run.Conclusion == StartConclusion.NotStarted, "进程退出时正文未发出即可最终分类");
    }

    private static void VerifyRunLifecycleFaultedSlot()
    {
        var f = new Scenario();
        var intent = Intent(RunSource.Launch);
        f.Begin(intent);
        f.Body();
        f.Active();
        f.FrameEvent(f.Frame(active: SnapshotOf(f.Send.Attempt, RunState.Running), worker: WorkerState.Faulted));
        Check(f.Items<Present>().Single().Kind == NoticeKind.Faulted && !f.Run.Settled && !f.State.Current.FreshIdle,
            "Faulted 应提醒恢复，保留 Active");
        f.Ack();
        var end = Id(45);
        f.Step(new BeginClosing(end, ClosingKind.EndEnvironment));
        f.Step(new ClosingConfirmed(end, true));
        f.ExitWorker();
        f.Step(new ClosingEndReturned(end, true, Target));
        Check(f.Items<Present>().Count() == 1 && !f.Items<RequestQuit>().Any() && !f.Items<CompleteScheduled>().Any(),
            "结束分身不能再报同一 Run 未确认或合成终态");
    }

    private static void VerifyRunLifecycleRetryChannels()
    {
        foreach (var kind in new[] { NoticeKind.NotStarted, NoticeKind.StartUnknown, NoticeKind.EndUnconfirmed }) {
            var f = new Scenario();
            f.Begin(Intent(RunSource.Launch));
            f.Reply(f.Body(), StartReplyKind.RejectedBeforeRegistration, "worker_busy");
            f.Ack();
            var retry = Intent(RunSource.Manual, 2) with { Mode = StartMode.Retry, Attempt = null, RetryOf = f.Key };
            f.Begin(retry);
            var wire = f.Body();
            var error = new InvalidOperationException("拒绝重试");
            if (kind == NoticeKind.NotStarted) f.Reply(wire, StartReplyKind.RejectedBeforeRegistration, "worker_busy", error);
            else {
                if (kind == NoticeKind.EndUnconfirmed) f.Reply(wire, StartReplyKind.Accepted, "accepted");
                f.ExitWorker();
            }
            var notice = f.Items<Present>().Last();
            Check(notice.Intent?.Id == retry.Id && notice.Kind == kind && notice.Channel == PresentationChannel.ForegroundError
                && notice.Operation == retry.OperationName && (kind != NoticeKind.NotStarted || notice.Error == error)
                && f.Items<Present>().Count(p => p.Channel == PresentationChannel.Tray) == 1, "手动重试的单一前台错误渠道");
            f.Ack();
            f.Reply(wire, StartReplyKind.RejectedBeforeRegistration, "worker_busy", error);
            Check(f.Items<Present>().Count() == 2, "重复证据不能重复打开前台错误框");
        }
    }

    private static void VerifyRunLifecycleRetryIdentity()
    {
        var f = new Scenario();
        f.Begin(Intent(RunSource.Manual));
        var original = f.Send;
        var wire = f.Body();
        f.Timeout(wire);
        var q = f.QueryWire();
        f.Connect();
        var retry = Intent(RunSource.Manual, 2) with { Mode = StartMode.Retry, RetryOf = f.Key, Attempt = null };
        foreach (var blocked in new[] { retry, retry with { Input = Input with { EditVersion = 2 } }, Intent(RunSource.Launch, 8) }) {
            f.Step(new Start(blocked));
            f.Expect(typeof(Skip), typeof(OperationFinished));
        }
        Check(f.Count<SendStart>() == 1, "未排除旧发送时，编辑或跨来源均不能发送 B");
        f.QueryResult(q, false);
        f.Ack();
        f.Begin(retry);
        Check(ReferenceEquals(original.Attempt, f.Send.Attempt) && original.Submission.Run == f.Key
            && original.Submission.SubmissionId != f.Send.Submission.SubmissionId && f.Run.Owner?.PreviousIntent == Id(1)
            && f.Run.Owner.Id == retry.Id, "复用整个尝试但分配新意图及发送身份");
        var second = f.Body();
        f.QueryResult(q, false);
        Check(!f.Run.Settled && f.Run.Sends[f.Send.Submission.SubmissionId].Boundary == SendBoundary.PossiblySent,
            "旧查询的 false 不能否定之后才写出的手动请求");
        f.Reply(second, StartReplyKind.Accepted, "accepted");
        f.Reply(wire, StartReplyKind.RejectedBeforeRegistration, "invalid_request");
        Check(f.Run.Conclusion == StartConclusion.Accepted && f.Run.Owner?.Id == retry.Id
            && f.Run.Sends[original.Submission.SubmissionId].ReplyCode == "invalid_request", "旧拒绝只补原记录，不能覆盖新接纳");
        f.Terminal(RunState.Failed);
        f.Begin(Intent(RunSource.Manual, 3));
        Check(f.Send.Attempt.RunId != original.Attempt.RunId, "已知终态后下一次点击即可开始新 Run");

        foreach (var change in new[] { "edit", "configuration", "project", "worker", "conflict" }) {
            var g = new Scenario();
            g.Begin(Intent(RunSource.Manual));
            var old = g.Send;
            var denied = new InvalidOperationException("原始拒绝原因");
            var rejected = g.Body();
            var code = change == "conflict" ? "run_id_conflict" : "worker_busy";
            g.Reply(rejected, StartReplyKind.RejectedBeforeRegistration, code, denied);
            Check(g.DeliveredItem<Present>() is { Channel: PresentationChannel.ForegroundError, Error: var actual }
                && actual == denied && g.Items<Present>().Count() == 1, "手动明确拒绝只产生一次原异常的前台呈现");
            g.Reply(rejected, StartReplyKind.RejectedBeforeRegistration, code, denied);
            g.Expect();
            g.Ack();
            if (change == "worker") {
                g.Eof();
                g.Connection = new(Id(101), 2);
                g.Connect();
            }
            var target = Target with { WorkerId = g.Connection.WorkerInstanceId };
            var input = change switch {
                "edit" => Input with { EditVersion = 2 }, "configuration" => Input with { ConfigurationId = Id(201) },
                "project" => Input with { ProjectVersion = 2 }, _ => Input
            };
            var next = retry with { Input = input };
            g.Step(new Start(next));
            g.Step(new Reserved(next.Id, new(next.Id, target)));
            g.Step(new ReadyChecked(next.Id, target, input));
            if (change == "conflict") {
                Check(g.DeliveredItem<Present>().Error == denied && g.Count<ResolvePlan>() == 0 && g.Count<SendStart>() == 1,
                    "冲突的未编辑尝试须呈现原错误，不能用新摘要重发");
                continue;
            }
            g.Expect(typeof(ResolvePlan));
            g.Step(new PlanResolved(next.Id, Attempt(2)));
            Check(g.Send.Attempt.RunId != old.Attempt.RunId, $"{change} 后不可复用旧 Run ID");
        }
    }

    private static void VerifyRunLifecycleReconnectNegative()
    {
        var f = new Scenario();
        f.Begin(Intent(RunSource.Scheduled));
        var sent = f.Body();
        f.Eof();
        f.Claim([Intent(RunSource.Scheduled, 2)]);
        f.Expect(typeof(Skip), typeof(OperationFinished));
        f.Evidence(new(f.Connection, WorkerEvidenceKind.AdmissionRemoved));
        f.Reconnect();
        f.Expect(typeof(QueryRun));
        var q = f.QueryWire();
        Check(q.WriteSequence == sent.WriteSequence && !q.Covers(sent) && !f.Items<Present>().Any(),
            "重连后的普通空闲快照与跨代写序均不能排除发送");
        f.QueryResult(q, false);
        Check(f.DeliveredItem<Present>().Kind == NoticeKind.NotStarted, "有序否定应交付未能启动");
        f.Ack();
        f.QueryResult(q, false);
        var next = Intent(RunSource.Scheduled, 3);
        f.Now += TimeSpan.FromMinutes(1);
        f.PreviewSchedule(next);
        f.Step(new Reserved(next.Id, new(next.Id, Target)));
        f.Step(new CommittedConfiguration(next.Id));
        Check(f.Only<PublishConfiguration>().Intent == next && f.Only<AppendStart>().Intent == next && f.Items<Present>().Count() == 1
            && f.Count<SendStart>() == 1 && !f.Items<Cleanup>().Any(), "仅有序 false 结算，现场保留，下一机会获准");
    }

    private static void VerifyRunLifecycleQueryBeforeWriter()
    {
        var f = new Scenario();
        f.Begin(Intent(RunSource.Manual));
        var writer = f.Writer();
        f.Evidence(new(f.Connection, WorkerEvidenceKind.BeforeBody, writer));
        f.Eof();
        f.Reconnect();
        f.QueryResult(f.QueryWire(), false);
        Check(!f.Run.Settled && !f.Items<Present>().Any(), "查询不能排除尚在等写锁的 writer");
        var failure = new InvalidOperationException("旧写端未写正文");
        f.Evidence(new(writer.Connection, WorkerEvidenceKind.NotSent, writer, Error: failure));
        Check(f.Items<Present>().Single().Error == failure && f.Run.Conclusion == StartConclusion.NotStarted
            && f.Run.Confirmation.Due == null, "最终采用本地 NotSent 和原异常");
    }

    private static void VerifyRunLifecycleProtocolRecovery()
    {
        foreach (var cause in new[] { "定向 getSnapshot 缺少有效的 runRegistered", "runRegistered 与快照冲突" }) {
            var f = new Scenario();
            f.Begin(Intent(RunSource.Manual));
            f.Timeout(f.Body());
            var old = f.QueryWire();
            // #131 validates these wire fields and closes the admitted connection on a protocol fault.
            var fault = new ProtocolException(cause);
            f.Eof(fault);
            f.Timeout(old);
            Check(f.State.Connection == null && !f.Run.Settled && !f.Items<Present>().Any()
                && f.State.Current.Detail == fault.Message, "协议故障保留待确认");
            f.Reconnect();
            f.Expect(typeof(QueryRun));
            f.QueryResult(f.QueryWire(), true, f.Frame(last: SnapshotOf(f.Send.Attempt, RunState.Succeeded)));
            Check(f.Items<Present>().Single().Kind == NoticeKind.Succeeded && f.Count<SendStart>() == 1, "重连后定向恢复");
        }
        // VerifyConnectionOpenRejectionAsync exercises the old-version handshake on the pipe.
        // The reducer receives only the result of its Ready wait.
        var waiting = new Scenario();
        var launch = Intent(RunSource.Launch) with { Mode = StartMode.PrepareAndStart };
        waiting.Step(new Start(launch));
        waiting.Step(new Reserved(launch.Id, new(launch.Id, null)));
        waiting.Step(new Prepared(launch.Id, Target));
        waiting.Now += waiting.Only<CheckReady>().Timeout;
        waiting.Step(new ReadyChecked(launch.Id, null, Input, new TimeoutException()));
        waiting.DrainDeliveries();
        Check(waiting.Count<SendStart>() == 0, "Ready 未达成时不得发送启动请求");
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
            f.Ack();
            var original = f.Send;
            f.Begin(Intent(RunSource.Manual, 2) with { Mode = StartMode.Retry, RetryOf = f.Key, Attempt = null });
            Check(f.Run.Quit == QuitStage.None && ReferenceEquals(f.Send.Attempt, original.Attempt)
                && f.Send.Submission.SubmissionId != original.Submission.SubmissionId, "手动发送前原子解绑");
            f.Body();
            f.Eof();
            f.Reconnect();
            f.QueryResult(f.QueryWire(), true, f.Frame(last: SnapshotOf(f.Send.Attempt, RunState.Succeeded)));
            Check(f.Run.Terminal?.State == RunState.Succeeded && f.Run.Owner!.Source == RunSource.Manual
                && f.Items<Present>().Count() == 1 && !f.Items<RequestQuit>().Any() && f.Count<SendStart>() == 2,
                "手动首次登记且丢 ACK 不继承开机退出，同 Run 不重开通知槽");
        }
    }

    private static void VerifyRunLifecycleRetirementOrder()
    {
        foreach (var terminalFirst in new[] { true, false }) {
            var f = new Scenario();
            f.Begin(Intent(RunSource.Launch));
            f.Reply(f.Body(), StartReplyKind.Accepted, "accepted");
            var late = f.Frame(last: SnapshotOf(f.Send.Attempt, RunState.Succeeded));
            if (terminalFirst) f.FrameEvent(late);
            if (terminalFirst) f.Eof();
            f.ExitWorker();
            f.FrameEvent(late);
            f.Ack();
            Check(f.Items<Present>().Single().Kind == (terminalFirst ? NoticeKind.Succeeded : NoticeKind.EndUnconfirmed)
                && f.Items<RequestQuit>().Count() == (terminalFirst ? 1 : 0) && !f.State.Current.Fresh,
                "退出是最终分类点，判坏的连接不再提供终态");
        }
        var g = new Scenario();
        g.Begin(Intent(RunSource.Scheduled));
        g.Body();
        var historical = g.Frame(last: SnapshotOf(g.Send.Attempt, RunState.Succeeded));
        var other = g.Frame(active: SnapshotOf(Attempt(8), RunState.Running));
        g.FrameEvent(historical, new(WorkerObservation.Connected, false, other, "修订断档"));
        Check(g.Run.Terminal?.State == RunState.Succeeded && !g.State.Current.FreshReadyToStart, "历史证据不开放实时启动");
        g.Ack();
        g.Ack();
        g.FrameEvent(other);
        Check(!g.Items<Cleanup>().Any(), "不能根据低修订终态注销后继环境");
        var h = new Scenario();
        h.Begin(Intent(RunSource.Launch));
        h.Timeout(h.Body());
        var old = h.Connection;
        h.Eof();
        h.Connection = new(Id(101), 2);
        h.Terminal(RunState.Succeeded);
        h.Evidence(new(old, WorkerEvidenceKind.ProcessExited));
        Check(h.Items<Present>().Single().Kind == NoticeKind.StartUnknown && !h.Items<RequestQuit>().Any()
            && h.State.Connection == h.Connection, "另一 Worker 的同名终态不结算旧 Run，旧退出不清新连接");
    }

    private static void VerifyRunLifecycleReconnectBarrier()
    {
        foreach (var exits in new[] { true, false }) {
            var f = new Scenario();
            f.Begin(Intent(RunSource.Launch));
            f.Timeout(f.Body());
            f.Eof();
            f.Connection = f.Connection with { Generation = 2 };
            var terminal = f.Frame(last: SnapshotOf(f.Send.Attempt, RunState.Succeeded));
            f.FrameEvent(terminal);
            f.Expect(typeof(QueryRun));
            Check(f.Run.AwaitingReconnectQuery && !f.Run.Settled && !f.Items<Present>().Any(),
                "重连首帧可以包含证据，疑点由该 Worker 的定向查询解决");
            if (exits) f.ExitWorker(); // the terminal was already received on the new, good connection
            else f.QueryResult(f.QueryWire(), true, f.Frame(last: SnapshotOf(Attempt(8), RunState.Failed)));
            Check(f.Items<Present>().Single().Terminal == terminal.LastRun && f.Count<SendStart>() == 1,
                "有序查询或最终退出依据已收到的真实终态，不能拿后继结果替代");
        }
    }

    private static void VerifyRunLifecycleObservedRuns()
    {
        var f = new Scenario();
        var external = Attempt(8);
        f.FrameEvent(f.Frame(last: SnapshotOf(external, RunState.Succeeded)));
        f.Expect();
        f.FrameEvent(f.Frame(active: SnapshotOf(external, RunState.Running)));
        f.FrameEvent(f.Frame(last: SnapshotOf(external, RunState.Succeeded)));
        f.Ack();
        Check(f.Items<Present>().Single().Intent == null && !f.Items<CompleteScheduled>().Any()
            && !f.Items<RequestQuit>().Any(), "接管后实际观察过 Active 才有通知资格");

        var g = new Scenario();
        g.Begin(Intent(RunSource.Launch));
        g.Reply(g.Body(), StartReplyKind.Accepted, "accepted");
        var successor = g.Frame(last: SnapshotOf(external, RunState.Succeeded));
        g.QueryResult(g.QueryWire(), true, successor);
        Check(g.Run.Conclusion == StartConclusion.EndUnconfirmed && g.DeliveredItem<Present>().Kind == NoticeKind.EndUnconfirmed
            && !g.Items<RequestQuit>().Any(), "定向查询已登记但终态被覆盖，不能用后继的成功触发退出");

        var h = new Scenario();
        h.Begin(Intent(RunSource.Manual));
        h.Timeout(h.Body());
        var conflict = SnapshotOf(external with { RunId = h.Key.RunId }, RunState.Succeeded);
        h.QueryResult(h.QueryWire(), true, h.Frame(last: conflict));
        Check(h.Run.Conclusion == StartConclusion.NotStarted && h.Run.Terminal == null
            && h.Run.Sends.Values.Single().ReplyCode == "run_id_conflict", "同 Run ID 不同摘要不能借用接纳或终态");

        var gap = new Scenario();
        gap.Begin(Intent(RunSource.Manual));
        gap.Body();
        var before = gap.State.Current;
        gap.FrameEvent(gap.Frame(), before with { SnapshotFresh = false, Detail = "状态修订断档" });
        gap.Expect(typeof(QueryRun));
        Check(!gap.State.Current.FreshReadyToStart && gap.Count<SendStart>() == 1, "尚无应答时断档也只触发定向查询");
    }
}

