using Gui.Runtime;
using Gui.Worker;
using Protocol;
using static Gui.Runtime.LifecycleEffect;
using static Gui.Runtime.LifecycleEvent;

namespace Gui.Infrastructure;

internal static partial class RunLifecycleSelfTest
{
    private static void VerifyRunLifecycleSourceConclusions()
    {
        foreach (var source in Enum.GetValues<RunSource>())
        foreach (var conclusion in Enum.GetValues<StartConclusion>()) {
            var f = new Scenario();
            f.Begin(Intent(source));
            if (conclusion == StartConclusion.NotStarted) {
                f.Evidence(new(null, WorkerEvidenceKind.NotSent, f.Writer() with { Connection = null },
                    Error: new InvalidOperationException("未能写出正文")));
            } else {
                f.Body();
                if (conclusion != StartConclusion.StartUnknown) f.Active();
                if (conclusion == StartConclusion.Accepted) f.Terminal(RunState.Succeeded);
                else {
                    f.Eof();
                    f.ExitWorker();
                }
            }
            var expected = new List<Type> { typeof(Present) };
            if (conclusion == StartConclusion.Accepted && source == RunSource.Launch) expected.Add(typeof(RequestQuit));
            Check(f.History.OfType<CompleteScheduled>().Count() == (conclusion == StartConclusion.Accepted && source == RunSource.Scheduled ? 1 : 0),
                "只有已接纳并取得终态的定时提交应完成记账");
            f.Finish(expected.ToArray());
            f.Conclusions(conclusion == StartConclusion.EndUnconfirmed ? [StartConclusion.Accepted, conclusion] : [conclusion]);
            var notice = f.Items<Present>().Single();
            var kind = conclusion switch {
                StartConclusion.Accepted => NoticeKind.Succeeded, StartConclusion.NotStarted => NoticeKind.NotStarted,
                StartConclusion.StartUnknown => NoticeKind.StartUnknown, _ => NoticeKind.EndUnconfirmed
            };
            Check(notice.Run == f.Key && notice.Intent == f.Start.Intent && notice.Kind == kind
                && f.History.OfType<SubmissionConclusion>().All(c => c.Submission == f.Start.Submission),
                $"{source} / {conclusion} 应交付给原提交");
        }
    }

    private static void VerifyRunLifecycleBeforeBodyExit()
    {
        foreach (var cause in new[] { "序列化", "大小越界", "断开", "只写出前缀" }) {
            var f = new Scenario();
            var intent = Intent(RunSource.Manual) with { OperationName = "准备运行环境失败" };
            var error = new InvalidOperationException(cause, new System.IO.IOException(cause));
            f.Begin(intent);
            var writer = f.Writer();
            f.Evidence(new(f.Connection, WorkerEvidenceKind.BeforeBody, writer));
            f.Eof();
            f.Evidence(new(writer.Connection, WorkerEvidenceKind.NotSent, writer, Error: error));
            f.ExitWorker();
            f.Finish(typeof(Present));
            f.Conclusions(StartConclusion.NotStarted);
            Check(f.Items<Present>().Single() == new Present(f.Key, intent, NoticeKind.NotStarted,
                intent.OperationName, error)
                && f.History.OfType<SubmissionConclusion>().Single().Error == error && f.State.Runs.IsEmpty,
                "保留调用方的操作名、原始异常链；回执后回收退休 Run");
        }
        var g = new Scenario();
        g.Begin(Intent(RunSource.Launch));
        g.ExitWorker();
        g.Finish(typeof(Present));
        g.Conclusions(StartConclusion.NotStarted);
    }

    private static void VerifyRunLifecycleConfirmationClock()
    {
        var f = new Scenario();
        f.Begin(Intent(RunSource.Scheduled));
        var send = f.Body();
        f.Timeout(send);
        WorkerRequestEvidence? prior = null;
        int[] delays = [1, 2, 4, 8, 16, 30, 30, 30];
        var expected = new List<LifecycleEffect> { f.History.OfType<QueryRun>().Single() };
        foreach (var delay in delays) {
            var query = f.QueryWire();
            if (prior != null) f.Timeout(prior);
            f.Timeout(send);
            f.Timeout(query);
            var timer = f.History.OfType<ArmRetry>().Last();
            expected.Add(new ArmRetry(f.Key, timer.Token, f.Now + TimeSpan.FromSeconds(delay)));
            f.Now = timer.Due - TimeSpan.FromTicks(1);
            var beforeEarly = f.History.Count;
            f.Step(new RetryDue(f.Key, timer.Token));
            Check(f.History.Skip(beforeEarly).ToArray() is [ArmRetry], "单次计时器提前触发后必须重新设定，不能提前查询");
            var rearmed = f.History.OfType<ArmRetry>().Last();
            expected.Add(new ArmRetry(f.Key, rearmed.Token, timer.Due));
            var beforeDue = f.History.Count;
            f.Now = rearmed.Due;
            f.Step(new RetryDue(f.Key, rearmed.Token));
            var next = f.History.Skip(beforeDue).OfType<QueryRun>().Single();
            expected.Add(new QueryRun(f.Key, next.Id, f.Connection));
            prior = query;
        }
        var inFlight = f.QueryWire();
        f.QueryResult(prior!, false);
        f.QueryResult(inFlight, false);
        f.Finish(typeof(Present));
        f.Conclusions(StartConclusion.NotStarted);
        expected.Add(new CancelQuery(f.Key, inFlight.QueryId!.Value));
        Check(f.History.Where(e => e is not (SubmissionConclusion or Deliver)).SequenceEqual(expected)
            && f.History.OfType<QueryRun>().Select(q => q.Id).Distinct().Count() == delays.Length + 1,
            "每次失败仅启动一个退避计时器；到期仅查询一次，旧回包可结算并取消新查询");
    }

    private static void VerifyRunLifecycleLatePositive()
    {
        var launch = new Scenario();
        launch.Begin(Intent(RunSource.Launch));
        var wire = launch.Body();
        launch.Timeout(wire);
        launch.Conclusions();
        launch.Reply(wire, StartReplyKind.Accepted, "accepted");
        launch.Conclusions(StartConclusion.Accepted);
        Check(launch.State.Runs[launch.Key].Conclusion == StartConclusion.Accepted && !launch.Items<Present>().Any(),
            "同一活连接的迟到 ACK 应在收到快照前确认接纳原 Run");
        launch.ExitWorker();
        launch.Finish(typeof(Present));
        launch.Conclusions(StartConclusion.Accepted, StartConclusion.EndUnconfirmed);
        Check(launch.Items<Present>().Single() is { Kind: NoticeKind.EndUnconfirmed } noticeAfterExit && noticeAfterExit.Key == launch.Key
            && !launch.Items<RequestQuit>().Any(), "已接纳的 Run 未见快照即退出时，只通知一次结果未确认");

        foreach (var source in new[] { RunSource.Launch, RunSource.Scheduled }) {
            var f = new Scenario();
            f.Begin(Intent(source));
            var send = f.Body();
            f.Reply(send, StartReplyKind.Indeterminate, "internal_error");
            f.FrameEvent(f.Frame());
            var beforePositive = f.History.Count;
            f.Terminal(RunState.Failed);
            f.Terminal(RunState.Failed);
            f.Finish(source == RunSource.Launch ? [typeof(Present), typeof(RequestQuit)] : [typeof(Present)]);
            Check(f.History.OfType<CompleteScheduled>().Count() == (source == RunSource.Scheduled ? 1 : 0), "定时终态应完成一次");
            f.Conclusions(StartConclusion.Accepted);
            var notice = f.Items<Present>().Single();
            Check(f.History.Take(beforePositive).Single() is QueryRun && notice.Kind == NoticeKind.Failed
                && notice.Detail.Contains("巡逻") && f.History.OfType<Deliver>().All(d => d.Item.Key == send.Run),
                "普通空闲快照后，迟到的真实终态仍只交付一次");
        }
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
        var beforeFailure = f.History.Count;
        var error = new InvalidOperationException("旧写端未写正文");
        f.Evidence(new(writer.Connection, WorkerEvidenceKind.NotSent, writer, Error: error));
        f.Finish(typeof(Present));
        f.Conclusions(StartConclusion.NotStarted);
        Check(f.History.Take(beforeFailure).ToArray() is [QueryRun, ArmRetry]
            && f.History.OfType<CancelRetry>().Single().Token == f.History.OfType<ArmRetry>().Single().Token
            && f.Items<Present>().Single().Error == error, "定向 false 不能排除尚未定性的 writer");
    }

    private static void VerifyRunLifecycleProtocolRecovery()
    {
        foreach (var reason in new[] { "缺少 runRegistered", "runRegistered 与快照冲突" }) {
            var f = new Scenario();
            f.Begin(Intent(RunSource.Manual));
            f.Timeout(f.Body());
            var old = f.QueryWire();
            // #131 closes the pipe on malformed v9 data. No negative query result is published.
            f.Eof(new ProtocolException(reason));
            f.Timeout(old);
            f.QueryResult(old, false);
            f.Reconnect();
            var live = f.QueryWire();
            f.QueryResult(live, true, f.Frame(last: SnapshotOf(f.Plan, RunState.Succeeded)));
            f.Finish(typeof(Present));
            f.Conclusions(StartConclusion.Accepted);
            Check(f.Items<Present>().Single().Kind == NoticeKind.Succeeded && old.Connection != live.Connection
                && f.History.OfType<QueryRun>().Count() == 2 && f.History.OfType<CancelQuery>().Single().Id == old.QueryId,
                "判坏的连接不再提供帧，重连后定向恢复");
        }
    }

    private static void VerifyRunLifecycleReconnectNegative()
    {
        var f = new Scenario();
        f.Begin(Intent(RunSource.Scheduled));
        var send = f.Body();
        f.Eof();
        f.Evidence(new(f.Connection, WorkerEvidenceKind.AdmissionRemoved));
        f.Reconnect();
        var q = f.QueryWire();
        var before = f.History.Count;
        f.QueryResult(q, false);
        f.QueryResult(q, false);
        f.Finish(typeof(Present));
        f.Conclusions(StartConclusion.NotStarted);
        Check(f.History.Take(before).Single() is QueryRun && q.WriteSequence == send.WriteSequence && !q.Covers(send)
            && f.Items<Present>().Single().Kind == NoticeKind.NotStarted,
            "Admission 摘除和重连首帧均不结算，只有新连接的定向 false 能排除旧写入");

        var g = new Scenario();
        g.Begin(Intent(RunSource.Manual));
        g.Timeout(g.Body());
        g.Timeout(g.QueryWire());
        var timer = g.History.OfType<ArmRetry>().Single();
        g.Eof();
        g.Reconnect();
        g.Now = timer.Due;
        g.Step(new RetryDue(g.Key, timer.Token));
        g.QueryResult(g.QueryWire(), false);
        g.Finish(typeof(Present));
        Check(g.History.OfType<CancelRetry>().Single() == new CancelRetry(g.Key, timer.Token)
            && g.History.OfType<QueryRun>().Select(e => e.Connection.Generation).SequenceEqual([1L, 2L]),
            "重连立即确认，旧连接的退避到期不能增加查询");
    }

    private static void VerifyRunLifecycleReconnectBarrier()
    {
        foreach (var exits in new[] { true, false }) {
            var f = new Scenario();
            f.Begin(Intent(RunSource.Launch));
            f.Timeout(f.Body());
            f.Eof();
            var terminal = f.Frame(last: SnapshotOf(f.Plan, RunState.Succeeded));
            f.Reconnect(terminal, new(WorkerObservation.Connected, false, terminal, ""));
            var before = f.History.Count;
            if (exits) f.ExitWorker();
            else f.QueryResult(f.QueryWire(), true, f.Frame(last: SnapshotOf(Attempt(8), RunState.Failed)));
            f.Finish(typeof(Present), typeof(RequestQuit));
            f.Conclusions(StartConclusion.Accepted);
            Check(f.History.Take(before).All(e => e is QueryRun or CancelQuery) && f.History.OfType<QueryRun>().Count() == 2
                && f.Items<Present>().Single().Terminal == terminal.LastRun, "定向结果或进程退出释放重连期间收到的真实终态");
        }
        var g = new Scenario();
        g.Begin(Intent(RunSource.Scheduled));
        g.Body();
        g.Eof();
        var older = g.Frame();
        var last = g.Frame(last: SnapshotOf(g.Plan, RunState.Succeeded));
        g.Reconnect(last, new(WorkerObservation.Connected, false, last, ""));
        Check(g.History.OfType<CompleteScheduled>().Count() == 0 && new RunOrchestration(g.State).PendingStart(g.State.Current) == g.Key,
            "首次同步前的终态不能放行下一次启动。");
        g.FrameEvent(older, g.State.Current with { SnapshotFresh = true });
        Check(g.History.OfType<CompleteScheduled>().Single().Terminal == last.LastRun
            && new RunOrchestration(g.State).PendingStart(g.State.Current) == null,
            "补齐同步后，最新匹配终态与解除待启动必须在同一次转换生效。");
    }

    private static void VerifyRunLifecycleRetirementOrder()
    {
        foreach (var terminalFirst in new[] { false, true }) {
            var f = new Scenario();
            f.Begin(Intent(RunSource.Launch));
            f.Reply(f.Body(), StartReplyKind.Accepted, "accepted");
            var last = f.Frame(last: SnapshotOf(f.Plan, RunState.Succeeded));
            if (terminalFirst) f.FrameEvent(last);
            f.Eof();
            f.ExitWorker();
            f.FrameEvent(last);
            f.Finish(terminalFirst ? [typeof(Present), typeof(RequestQuit)] : [typeof(Present)]);
            f.Conclusions(terminalFirst ? [StartConclusion.Accepted] : [StartConclusion.Accepted, StartConclusion.EndUnconfirmed]);
            Check(f.State.Runs.IsEmpty && f.Items<Present>().Single().Kind ==
                (terminalFirst ? NoticeKind.Succeeded : NoticeKind.EndUnconfirmed), "确认退出之后不能再修订分类");
        }
        var g = new Scenario();
        g.Begin(Intent(RunSource.Launch));
        g.Timeout(g.Body());
        var old = g.Connection;
        g.Eof();
        g.Connection = new(Id(101), 2);
        g.Terminal(RunState.Succeeded);
        g.Evidence(new(old, WorkerEvidenceKind.ProcessExited));
        g.Finish(typeof(Present));
        g.Conclusions(StartConclusion.StartUnknown);
        Check(g.State.Connection == g.Connection && g.Items<Present>().Single().Key == g.Key,
            "另一 Worker 的同名终态不能结算旧 Run，旧进程退出不清新连接");
    }

    private static void VerifyRunLifecycleRetryIdentity()
    {
        var f = new Scenario();
        f.Begin(Intent(RunSource.Manual));
        var first = f.Start;
        var send = f.Body();
        f.Timeout(send);
        var oldQuery = f.QueryWire();
        f.FrameEvent(f.Frame());
        var retry = Intent(RunSource.Manual, 2) with { RetryOf = f.Key };
        Check(f.State.ReusableAttempt(retry, f.Connection.WorkerInstanceId) == null, "未排除原提交时不能复用");
        f.QueryResult(oldQuery, false);
        var reuse = f.State.ReusableAttempt(retry, f.Connection.WorkerInstanceId);
        Check(ReferenceEquals(reuse, first.Attempt), "应取回原计划、项 ID、时间及脱敏值");
        f.Begin(retry, reuse);
        var second = f.Body();
        f.QueryResult(oldQuery, false);
        f.Reply(second, StartReplyKind.Accepted, "accepted");
        f.Reply(send, StartReplyKind.RejectedBeforeRegistration, "invalid_request");
        var oldRejection = f.State.Runs[f.Key].Sends[first.Submission.SubmissionId].RejectionCode;
        f.Terminal(RunState.Failed);
        f.Finish(typeof(Present));
        f.Step(first);
        f.Step(f.Start);
        f.Finish(typeof(Present));
        f.Conclusions(StartConclusion.NotStarted, StartConclusion.Accepted);
        Check(f.Start.Submission.SubmissionId != first.Submission.SubmissionId && f.Key == first.Key
            && f.History.OfType<SubmissionConclusion>().Select(c => c.Submission).SequenceEqual([first.Submission, f.Start.Submission])
            && oldRejection == "invalid_request"
            && f.State.ReusableAttempt(retry with { Id = Id(3) }, f.Connection.WorkerInstanceId) == null,
            "旧查询和旧拒绝只归属第一次提交，终态后的新开始必须另建尝试");
    }

    private static void VerifyRunLifecycleRejectedRetry()
    {
        foreach (var code in new[] { "invalid_run_plan", "invalid_request", "run_id_conflict", "worker_busy",
            "worker_not_ready", "operation_not_allowed", "worker_faulted", "protocol_version_mismatch" }) {
            var f = new Scenario();
            f.Begin(Intent(RunSource.Manual));
            var send = f.Body();
            var error = new InvalidOperationException(code);
            f.Reply(send, StartReplyKind.RejectedBeforeRegistration, code, error);
            f.Reply(send, StartReplyKind.RejectedBeforeRegistration, code, error);
            f.Finish(typeof(Present));
            f.Conclusions(StartConclusion.NotStarted);
            var retry = Intent(RunSource.Manual, 2) with { RetryOf = f.Key };
            var worker = f.Key.WorkerInstanceId;
            var reusable = f.State.ReusableAttempt(retry, worker);
            Check(f.Items<Present>().Single() == new Present(f.Key, f.Start.Intent, NoticeKind.NotStarted,
                    f.Start.Intent.OperationName, error)
                && (code is "invalid_run_plan" or "invalid_request" or "run_id_conflict" ? reusable == null : ReferenceEquals(reusable, f.Plan))
                && f.State.ReusableAttempt(retry, Id(101)) == null, "明确拒绝只交付一次原错，复用须符合拒绝码和 Worker 身份");
            foreach (var changed in new[] { Input with { ConfigurationId = Id(9) }, Input with { ProjectVersion = 2 },
                Input with { EditVersion = 2 } })
                Check(f.State.ReusableAttempt(retry with { Input = changed }, worker) == null, "输入变化后不能复用旧尝试");
        }
    }

    private static void VerifyRunLifecycleStaleActiveBackoff()
    {
        var f = new Scenario();
        f.Begin(Intent(RunSource.Manual));
        f.Body();
        var active = f.Frame(SnapshotOf(f.Plan, RunState.Running));
        f.FrameEvent(active);
        f.FrameEvent(f.Frame(SnapshotOf(Attempt(8), RunState.Running)));
        var expectedDue = new List<TimeSpan>();
        foreach (var delay in new[] { 1, 2, 4 }) {
            f.Timeout(f.QueryWire());
            var timer = f.History.OfType<ArmRetry>().Last();
            expectedDue.Add(f.Now + TimeSpan.FromSeconds(delay));
            f.FrameEvent(active, f.State.Current);
            f.Now = timer.Due;
            f.Step(new RetryDue(f.Key, timer.Token));
        }
        var pending = f.QueryWire();
        f.FrameEvent(f.Frame(last: SnapshotOf(f.Plan, RunState.Failed)), f.State.Current with { SnapshotFresh = false });
        Check(!f.Items<Present>().Any(), "失效快照中的终态应等待定向确认。");
        f.QueryResult(pending, true);
        f.Finish(typeof(Present));
        f.Conclusions(StartConclusion.Accepted);
        Check(f.History.OfType<ArmRetry>().Select(a => a.Due).SequenceEqual(expectedDue)
            && f.History.OfType<QueryRun>().Count(q => q.Key == f.Key) == 4
            && !f.History.OfType<CancelRetry>().Any() && f.Items<Present>().Single().Kind == NoticeKind.Failed,
            "低修订 Active 不重置退避，修订断档中的历史 Last 仍可结算目标 Run");
    }

    private static void VerifyRunLifecycleRegisteredQuery()
    {
        foreach (var conflict in new[] { false, true }) {
            var f = new Scenario();
            f.Begin(Intent(RunSource.Launch));
            f.Timeout(f.Body());
            var other = Attempt(8);
            var q = f.QueryWire();
            if (conflict) other = other with { RunId = f.Key.RunId };
            var frame = f.Frame(last: SnapshotOf(other, RunState.Succeeded));
            f.QueryResult(q, true, frame);
            f.Finish(typeof(Present));
            var released = f.State.Runs[f.Key];
            Check(released.Attempt == null && released.Sends.IsEmpty && released.Submission == f.Start.Submission,
                "通知回执后应释放原计划、发送记录和超时异常，并保留提交身份");
            f.Step(f.Start);
            Check(f.State.Runs[f.Key] == released, "重投同一提交不能重新登记已结算的 Run");
            f.QueryResult(q, true, frame);
            f.Finish(typeof(Present));
            f.Conclusions(conflict ? StartConclusion.NotStarted : StartConclusion.EndUnconfirmed);
            var notice = f.Items<Present>().Single();
            Check(notice.Key == f.Key && notice.Terminal == null
                && notice.Kind == (conflict ? NoticeKind.NotStarted : NoticeKind.EndUnconfirmed)
                && f.State.ReusableAttempt(Intent(RunSource.Manual, 2) with { RetryOf = f.Key }, f.Key.WorkerInstanceId) == null,
                "摘要冲突或后继覆盖均不能借用其他 Run 的终态");
        }
    }
}
