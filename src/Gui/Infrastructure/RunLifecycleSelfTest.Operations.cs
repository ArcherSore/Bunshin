using Gui.Runtime;
using Gui.Worker;
using Protocol;
using static Gui.Runtime.LifecycleEffect;
using static Gui.Runtime.LifecycleEvent;

namespace Gui.Infrastructure;

internal static partial class RunLifecycleSelfTest
{
    private static void VerifyRunLifecycleMinuteWinner()
    {
        var a = Intent(RunSource.Scheduled);
        var b = Intent(RunSource.Scheduled, 2) with { Input = Input with { ConfigurationId = Id(201) } };
        foreach (var force in new[] { false, true }) {
            var f = new Scenario();
            f.Connect();
            if (force) f.FrameEvent(f.Frame(active: SnapshotOf(Attempt(9), RunState.Running)));
            f.PreviewSchedule(a, force);
            if (!force) {
                f.Step(new Reserved(a.Id, new(a.Id, Target)));
                f.Step(new CommittedConfiguration(a.Id));
                f.Step(new ActivityAppended(a.Id));
                f.Step(new OldEnvironmentEnded(a.Id, true));
                f.Step(new Prepared(a.Id, null, new InvalidOperationException("准备失败")));
                f.DrainDeliveries();
            }
            f.Now += TimeSpan.FromSeconds(10);
            // The focus recheck has the same wall-clock minute even if its elapsed deadline shifted.
            f.Step(new ScheduleClaimed([b], Epoch.Date.AddHours(8), TimeSpan.FromSeconds(61), force));
            Check(f.Last.OfType<Skip>().SingleOrDefault() is { Intent: var skipped, Reason.Length: > 0 } && skipped == b,
                "同分钟后认领的另一个配置也必须记录跳过");
            if (force) {
                f.Step(new CancelCountdown(a.Id));
                f.Claim([b], true);
                Check(f.Only<Skip>().Intent == b, "取消强制倒计时也消费该分钟");
            }
            Check(f.Count<ShowCountdown>() == (force ? 1 : 0) && f.Count<CommitConfiguration>() == (force ? 0 : 1)
                && f.Count<AppendStart>() == (force ? 0 : 1), "同分钟只能有一次倒计时或配置获准");
            f.Now = TimeSpan.FromMinutes(1);
            f.PreviewSchedule(b, force);
            Check(force ? f.Only<ShowCountdown>().Intent == b : f.Only<Reserve>().Intent == b, "下一分钟可以选择新候选");
            if (force) {
                f.Step(new CancelCountdown(b.Id));
                f.Step(new ScheduleClaimed([Intent(RunSource.Scheduled, 3)], Epoch.Date.AddHours(8),
                    f.Now + TimeSpan.FromMinutes(1), true));
                Check(f.Last.OfType<Skip>().Any(), "本地时钟回拨到已消费分钟也不能选择第三个配置");
            }
        }
    }

    private static void VerifyRunLifecycleExpiredSelection()
    {
        var f = new Scenario { Now = TimeSpan.FromSeconds(59) };
        var a = Intent(RunSource.Scheduled);
        var b = Intent(RunSource.Scheduled, 2);
        f.Claim([a, b]);
        f.Now = TimeSpan.FromMinutes(1);
        f.Step(new Previewed(a.Id, null, new InvalidOperationException("空计划")));
        f.Step(new Previewed(b.Id, b));
        f.Step(new Reserved(b.Id, new(b.Id, null)));
        f.Step(new CommittedConfiguration(b.Id));
        f.DrainDeliveries();
        Check(f.Count<Preview>() == 1 && f.History.OfType<Skip>().Single() is { Intent: var skipped, Reason.Length: > 0 }
            && skipped == b && f.Count<CommitConfiguration>() == 0 && f.Count<AppendStart>() == 0, "过期的 B 只跳过一次");
    }

    private static void VerifyRunLifecycleLogonGate()
    {
        var a = Intent(RunSource.Scheduled);
        var b = Intent(RunSource.Launch, 2);
        foreach (var order in new[] { "invalid-preview", "launch-first", "schedule-first" }) {
            var f = new Scenario();
            f.Connect();
            if (order == "schedule-first") f.Step(new Start(b));
            f.Claim([a]);
            if (order != "schedule-first") f.Step(new Start(b));
            Check(f.History.OfType<Reserve>().Any(r => r.Intent == b) && f.History.OfType<Preview>().Any(p => p.Intent == a),
                "预览与尚未取得闸门的请求均不能淘汰对方");
            var launchWins = order != "schedule-first";
            if (launchWins) f.Step(new Reserved(b.Id, new(b.Id, Target)));
            f.Step(new Previewed(a.Id, order == "invalid-preview" ? null : a));
            if (order != "invalid-preview") {
                f.Step(new Reserved(a.Id, launchWins ? null : new(a.Id, Target), BusyReason: "另一个操作已取得闸门"));
            }
            if (launchWins) {
                f.Step(new ReadyChecked(b.Id, Target, b.Input));
                Check(f.Send.Submission.IntentId == b.Id && f.Count<CommitConfiguration>() == 0, "开机先取闸门即可启动");
            } else {
                f.Step(new Reserved(b.Id, null, BusyReason: "定时已取得闸门"));
                f.Step(new CommittedConfiguration(a.Id));
                Check(f.Only<AppendStart>().Intent == a && f.History.OfType<Skip>().Single().Intent == b
                    && f.Count<SendStart>() == 0, "普通定时先取闸门才占用，开机只跳过一次");
            }
        }
        var delayed = new Scenario();
        delayed.Connect();
        delayed.Claim([a]);
        delayed.Step(new Start(b));
        delayed.Step(new Reserved(b.Id, new(b.Id, Target)));
        delayed.Step(new ReadyChecked(b.Id, null, b.Input, new TimeoutException()));
        delayed.Step(new Previewed(a.Id, a));
        Check(delayed.History.OfType<Skip>().Any(s => s.Intent == a && s.Reason.Length > 0)
            && delayed.History.OfType<Reserve>().All(r => r.Intent != a), "开机先取闸门又失败，未返回的定时预览也不能补跑");
    }

    private static void VerifyRunLifecyclePreparation()
    {
        var f = new Scenario();
        var prepare = Intent(RunSource.Manual) with { Mode = StartMode.PrepareAndStart };
        f.Step(new Start(prepare));
        f.Step(new Reserved(prepare.Id, new(prepare.Id, null)));
        Check(!f.Only<PrepareEnvironment>().ShowDesktop, "准备并运行沿用隐藏策略");
        f.Step(new Start(Intent(RunSource.Launch, 2)));
        f.Expect(typeof(Skip), typeof(OperationFinished));
        var inner = new InvalidOperationException("登录失败");
        var login = new InvalidOperationException("无法连接桌面", inner);
        f.Step(new Prepared(prepare.Id, null, login));
        var notice = f.DeliveredItem<Present>();
        Check(notice.Channel == PresentationChannel.ForegroundError && notice.Operation == "准备运行环境失败"
            && notice.Error == login && notice.Error.InnerException == inner && f.State.PreparationFailure == login
            && f.Count<SendStart>() == 0, "前台准备错误须保留操作名、原异常和内部异常");
        f.Ack();
        f.Step(new Start(prepare));
        f.Expect();
        var only = prepare with { Id = Id(3), Mode = StartMode.PrepareOnly };
        f.Step(new Start(only));
        f.Step(new Reserved(only.Id, new(only.Id, null)));
        Check(f.Only<PrepareEnvironment>().ShowDesktop, "仅准备应显示桌面");
        f.Step(new Prepared(only.Id, Target));
        f.Expect(typeof(Release), typeof(OperationFinished));
        Check(f.State.PreparationFailure == null && f.Count<CheckReady>() == 0 && f.Count<SendStart>() == 0, "仅准备不启动");

        var launch = Intent(RunSource.Launch, 4) with { Mode = StartMode.PrepareAndStart };
        f.Step(new Start(launch));
        f.Step(new Reserved(launch.Id, new(launch.Id, null)));
        f.Step(new Prepared(launch.Id, Target));
        Check(f.Only<CheckReady>().Timeout == TimeSpan.FromSeconds(60), "新鲜 Ready 的 60 秒上限");
        f.Now += TimeSpan.FromSeconds(60);
        f.Step(new ReadyChecked(launch.Id, null, Input, new TimeoutException()));
        Check(f.DeliveredItem<Present>() is { Kind: NoticeKind.NotStarted, Channel: PresentationChannel.Tray }
            && !f.Items<RequestQuit>().Any(), "开机准备失败只用后台渠道");
        f.Ack();
        prepare = prepare with { Id = Id(5) };
        f.Step(new Start(prepare));
        f.Step(new Reserved(prepare.Id, new(prepare.Id, null)));
        f.Step(new Prepared(prepare.Id, Target));
        f.Connect();
        f.Step(new ReadyChecked(prepare.Id, Target, Input with { ProjectVersion = 2 }));
        Check(f.Count<SendStart>() == 0, "Ready 后必须复核项目版本");
    }

    private static void VerifyRunLifecycleScheduleAdmission()
    {
        var a = Intent(RunSource.Scheduled);
        var b = Intent(RunSource.Scheduled, 2) with { Input = Input with { ConfigurationId = Id(201) }, ConfigurationName = "第二班" };
        var f = new Scenario();
        f.Connect();
        f.PreviewSchedule(a, false, b);
        f.Step(new Reserved(a.Id, null, BusyReason: "预留前已变忙"));
        f.Expect(typeof(Skip), typeof(Skip), typeof(OperationFinished));
        Check(f.History.OfType<Skip>().Select(s => (s.Intent, s.Reason)).SequenceEqual(new[] { (a, "预留前已变忙"), (b, "预留前已变忙") })
            && f.Count<CommitConfiguration>() == 0 && f.Count<PublishConfiguration>() == 0
            && f.Count<AppendStart>() == 0 && f.Count<EndReserved>() == 0
            && !f.Items<Present>().Any(), "首个和后续忙候选均应留下具体跳过原因");

        var g = new Scenario();
        g.Connect();
        g.PreviewSchedule(b, false, a);
        g.Step(new Reserved(b.Id, new(b.Id, Target)));
        g.Expect(typeof(CommitConfiguration));
        var write = new UnauthorizedAccessException("最终文件替换失败");
        g.Step(new CommittedConfiguration(b.Id, write));
        Check(g.DeliveredItem<Present>() is { Kind: NoticeKind.NotStarted, Error: var e } && e == write
            && !g.Only<OperationFinished>().Admitted && g.Count<PublishConfiguration>() == 0 && g.Count<EndReserved>() == 0
            && g.Count<AppendStart>() == 0 && g.Only<Skip>().Intent == a, "配置最终提交失败，保留激活项与旧环境");
        g.Step(new ActivityAppended(b.Id));
        g.Expect();

        var h = new Scenario();
        h.Connect();
        h.Claim([a, b]);
        h.Step(new Previewed(a.Id, null, new InvalidOperationException("空计划")));
        Check(h.Only<Preview>().Intent == b, "无效首候选让位下一项");
        h.Step(new Previewed(b.Id, b));
        h.Step(new Reserved(b.Id, new(b.Id, Target)));
        h.Step(new ActivityAppended(b.Id));
        h.Expect();
        h.Step(new CommittedConfiguration(b.Id));
        h.Expect(typeof(PublishConfiguration), typeof(AppendStart));
        Check(h.State.Operations[b.Id].Admitted && h.Count<EndReserved>() == 0, "成功写盘就是获准点，追加活动前不结束环境");
        h.FrameEvent(h.Frame(active: SnapshotOf(Attempt(9), RunState.Running)));
        h.Step(new ActivityAppended(b.Id));
        Check(h.Only<OperationFinished>().Admitted && h.Count<EndReserved>() == 0 && h.Count<PrepareEnvironment>() == 0,
            "活动之后目标已变化只报获准后的换场失败");
        h.Ack();
        Check(h.DeliveredItem<Present>().Intent == b && h.Count<AppendStart>() == 1, "失败必须指向已获准目标");

        var j = new Scenario();
        j.Connect();
        j.PreviewSchedule(a);
        j.Step(new Reserved(a.Id, new(a.Id, Target with { SessionId = 99 })));
        Check(j.Count<EndReserved>() == 0 && j.Only<Skip>().Reason == "运行环境状态未知", "预留目标 Session 与 Worker 不匹配");
    }

    private static void VerifyRunLifecycleScheduledTerminals()
    {
        foreach (var status in new[] { RunState.Succeeded, RunState.Failed, RunState.Cancelled }) {
            var f = new Scenario();
            var scheduled = Intent(RunSource.Scheduled);
            f.Begin(scheduled);
            var sent = f.Body();
            Check(ReferenceEquals(f.Send.Attempt, scheduled.Attempt) && f.Key.RunId == scheduled.Attempt!.RunId,
                "定时提交应使用获准配置的原冻结计划");
            void Busy()
            {
                var before = f.Items<Present>().Count();
                f.Claim([Intent(RunSource.Scheduled, 2)]);
                f.Expect(typeof(Skip), typeof(OperationFinished));
                Check(!string.IsNullOrEmpty(f.Only<Skip>().Reason) && f.Items<Present>().Count() == before, "忙候选只记原因");
            }
            Busy();
            f.FrameEvent(f.Frame(last: SnapshotOf(Attempt(60), RunState.Succeeded)));
            Check(!f.Items<Cleanup>().Any() && !f.Items<Present>().Any(), "非目标 Run 的成功不能收尾");
            if (status == RunState.Succeeded) {
                f.Eof();
                Busy();
                f.Reconnect();
                f.Active();
                Check(f.Run.AwaitingReconnectQuery, "重连后有序定向查询仍在途");
                f.QueryResult(f.QueryWire(), true, f.Frame(active: SnapshotOf(f.Send.Attempt, RunState.Running)));
                f.Timeout(sent);
                Check(!f.Items<Present>().Any() && f.Run.Conclusion == StartConclusion.Accepted, "丢应答但已运行不能报启动失败");
            } else {
                f.Reply(sent, StartReplyKind.Accepted, "accepted");
            }
            Busy();
            f.Terminal(status);
            f.Terminal(status); // duplicate on the still-live worker, before any physical cleanup
            var next = Intent(RunSource.Scheduled, 3);
            if (status == RunState.Failed) {
                f.Now += TimeSpan.FromMinutes(1);
                f.PreviewSchedule(next);
                f.Step(new Reserved(next.Id, new(next.Id, Target)));
                f.Step(new CommittedConfiguration(next.Id));
                f.Step(new ActivityAppended(next.Id));
                Check(f.Only<EndReserved>().Lease.Expected == Target && f.Items<Present>().Single().Intent == scheduled
                    && !f.Items<CompleteScheduled>().Any(), "失败提醒尚未回执时，下一次换场已经开始");
            }
            if (status != RunState.Cancelled) {
                var n = f.Items<Present>().Single();
                Check(n.Intent!.ConfigurationName == "第一班" && n.Terminal!.State == status
                    && (status != RunState.Failed || n.Detail.Contains("失败任务：巡逻", StringComparison.Ordinal)),
                    "交付须冻结配置名与失败项标签");
                f.Ack();
            }
            var completed = f.Items<CompleteScheduled>().Single();
            Check(completed.Run == f.Key && completed.Terminal.State == status, "完成必须对应真实终态");
            f.Ack();
            if (status == RunState.Succeeded) {
                Check(f.DeliveredItem<Cleanup>().Expected == Target, "收尾只针对定时 Run 的目标");
                f.Step(new CleanupReturned(f.Only<Deliver>().Id, CleanupResult.Ended));
                Check(f.Only<ScheduleActivity>().Message == "全部完成，已关闭桌面分身", "成功收尾应留下完成动态");
            }
            if (status == RunState.Failed) {
                f.Step(new OldEnvironmentEnded(next.Id, true));
                f.Expect(typeof(ScheduleActivity), typeof(PrepareEnvironment));
                Check(f.Only<ScheduleActivity>().Message == "已关闭旧的桌面分身", "换场和成功收尾是不同活动");
            }
            Check(f.Run.CompletionDelivered && f.Items<CompleteScheduled>().Count() == 1 && !f.Items<RequestQuit>().Any()
                && f.Items<Present>().Count() == (status == RunState.Cancelled ? 0 : 1)
                && f.Items<Cleanup>().Count() == (status == RunState.Succeeded ? 1 : 0), "三个消费者独立去重");
            Check(f.History.OfType<EndReserved>().Count(e => e.Lease.Expected == Target)
                == (status == RunState.Failed ? 1 : 0), "同一旧环境只允许一次物理结束");
        }
    }

    private static void VerifyRunLifecycleCleanupCollision()
    {
        foreach (var trayFirst in new[] { true, false }) {
            var f = new Scenario();
            f.Begin(Intent(RunSource.Scheduled));
            f.Body();
            f.Active();
            var tray = Id(20);
            if (trayFirst) f.Step(new BeginClosing(tray, ClosingKind.EndEnvironment));
            f.Terminal(RunState.Succeeded);
            Check(f.Run.Cleanup == CleanupStage.Claimed && !f.Items<Cleanup>().Any(), "收到成功就占位，先于等待闸门");
            if (!trayFirst) f.Step(new BeginClosing(tray, ClosingKind.EndEnvironment));
            f.Claim([Intent(RunSource.Scheduled, 3)]);
            f.Expect(typeof(Skip), typeof(OperationFinished));
            f.Step(new ClosingConfirmed(tray, false));
            if (!trayFirst) Check(f.Items<Present>().Count() == 1, "解除暂停不重入已经交付的通知");
            f.Ack();
            f.Ack();
            var cleanup = f.Only<Deliver>().Id;
            f.Terminal(RunState.Succeeded);
            f.Claim([Intent(RunSource.Scheduled, 4)]);
            f.Expect(typeof(Skip), typeof(OperationFinished));
            var failure = new InvalidOperationException("收尾检查抛错");
            f.Step(new CleanupReturned(cleanup, CleanupResult.Failed, failure));
            Check(f.DeliveredItem<Present>() is { Kind: NoticeKind.CleanupFailed, Error: var error } && error == failure,
                "检查异常只报一次收尾失败");
            f.Step(new CleanupReturned(cleanup, CleanupResult.Failed, failure));
            f.Expect();
            f.Ack();
            f.Terminal(RunState.Succeeded);
            f.Claim([Intent(RunSource.Scheduled, 5)]);
            Check(f.Items<CompleteScheduled>().Count() == 1 && f.Items<Cleanup>().Count() == 1
                && f.Items<Present>().Count(n => n.Kind == NoticeKind.CleanupFailed) == 1 && f.Count<EndReserved>() == 1,
                "遇忙立即跳过；收尾失败不能再排注销");
        }
    }

    private static void VerifyRunLifecycleCleanupReconnect()
    {
        foreach (var changed in new[] { true, false }) {
            var f = new Scenario();
            f.Begin(Intent(RunSource.Scheduled));
            f.Body();
            f.Terminal(RunState.Succeeded);
            f.Ack();
            f.Ack();
            var check = f.Only<Deliver>().Id;
            f.Eof();
            f.Step(new CleanupReturned(check, CleanupResult.Disconnected));
            Check(f.Run.Cleanup == CleanupStage.WaitingForReconnect, "断线先保留现场");
            f.Connection = new(changed ? Id(101) : Target.WorkerId!.Value, 2);
            f.FrameEvent(f.Frame(last: SnapshotOf(f.Send.Attempt, RunState.Succeeded)));
            Check(f.Items<Cleanup>().Count() == (changed ? 1 : 2), "只为重新确认的原环境恢复条件收尾");
            if (!changed) {
                var resumed = f.Only<Deliver>().Id;
                f.Step(new CleanupReturned(check, CleanupResult.Failed));
                f.Expect();
                f.Step(new CleanupReturned(resumed, CleanupResult.Ended));
                Check(f.Run.Cleanup == CleanupStage.Ended, "旧收尾回执不能消费新收尾");
            } else Check(f.Run.Cleanup == CleanupStage.Superseded, "换 Worker 取消跟踪");
        }
    }

    private static void VerifyRunLifecycleCountdown()
    {
        var f = new Scenario();
        f.Connect();
        f.FrameEvent(f.Frame(active: SnapshotOf(Attempt(9), RunState.Running)));
        var next = Intent(RunSource.Scheduled, 2);
        f.Now = TimeSpan.FromSeconds(55);
        f.Claim([next], true);
        f.Step(new Previewed(next.Id, next));
        var end = f.Only<ShowCountdown>().Deadline;
        Check(end == TimeSpan.FromSeconds(65) && f.Count<Reserve>() == 0, "跨分钟的完整 10 秒倒计时不占闸门");
        f.Now = end - TimeSpan.FromTicks(1);
        f.Step(new CountdownDue(next.Id));
        f.Expect();
        f.Step(new CancelCountdown(next.Id));
        f.Expect(typeof(HideCountdown), typeof(ScheduleActivity), typeof(OperationFinished));
        f.Now = end;
        f.Step(new CountdownDue(next.Id));
        f.Expect();
        var later = next with { Id = Id(3) };
        f.PreviewSchedule(later, true);
        f.Now = f.Only<ShowCountdown>().Deadline;
        f.Step(new CountdownDue(later.Id));
        f.Expect(typeof(HideCountdown), typeof(Reserve));
        Check(f.Only<Reserve>() is { Wait: true, AllowReplacement: true }, "下一机会可到期后重新取得预留");
        var reloaded = later with { Input = Input with { ProjectVersion = 9 }, Attempt = Attempt(9) };
        f.Step(new Reserved(later.Id, new(later.Id, Target), reloaded));
        Check(f.Only<CommitConfiguration>().Intent.Attempt == reloaded.Attempt, "到期复核使用最新配置");

        var g = new Scenario();
        g.FrameEvent(g.Frame(active: SnapshotOf(Attempt(9), RunState.Running)));
        g.Claim([next], true);
        g.Now = TimeSpan.FromMinutes(1);
        g.Step(new Previewed(next.Id, next));
        Check(g.Count<ShowCountdown>() == 0 && g.Count<EndReserved>() == 0 && g.Only<Skip>().Intent == next,
            "倒计时尚未开始且错过分钟应跳过");
        var h = new Scenario();
        h.Connect();
        h.Eof();
        h.PreviewSchedule(next, true);
        h.Step(new Reserved(next.Id, new(next.Id, Target)));
        Check(h.Count<ShowCountdown>() == 0 && h.Count<EndReserved>() == 0 && h.Only<Skip>().Intent == next,
            "未知风险的存活 Worker 不允许强制换场");
    }
}

