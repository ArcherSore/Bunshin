using System.Collections.Immutable;
using System.Text.Json;
using Gui.Runtime;
using Gui.Worker;
using ProjectModel;
using Protocol;
using static Gui.Runtime.LifecycleEffect;
using static Gui.Runtime.LifecycleEvent;

namespace Gui.Infrastructure;

internal static partial class RunLifecycleSelfTest
{
    internal static readonly Action[] Cases = [
        VerifyRunLifecycleSourceConclusions, VerifyRunLifecycleConfirmationClock, VerifyRunLifecycleAtomicRunning,
        VerifyRunLifecycleLatePositive, VerifyRunLifecycleBeforeBodyExit, VerifyRunLifecycleFaultedSlot,
        VerifyRunLifecycleRetryChannels, VerifyRunLifecycleRetryIdentity, VerifyRunLifecycleReconnectNegative,
        VerifyRunLifecycleQueryBeforeWriter, VerifyRunLifecycleProtocolRecovery, VerifyRunLifecycleBootAttribution,
        VerifyRunLifecycleRetirementOrder, VerifyRunLifecyclePreparation, VerifyRunLifecycleScheduleAdmission,
        VerifyRunLifecycleScheduledTerminals, VerifyRunLifecycleCleanupCollision, VerifyRunLifecycleCleanupReconnect,
        VerifyRunLifecycleCountdown, VerifyRunLifecycleReplacement, VerifyRunLifecycleExitBuffer,
        VerifyRunLifecycleQuitRecovery, VerifyRunLifecycleStopTarget, VerifyRunLifecycleReconnectBarrier,
        VerifyRunLifecycleObservedRuns, VerifyRunLifecycleCountdownEvidence, VerifyRunLifecycleClosingReceipts,
        VerifyRunLifecycleSendFailure, VerifyRunLifecycleMinuteWinner, VerifyRunLifecycleExpiredSelection,
        VerifyRunLifecycleLogonGate, VerifyRunLifecycleStaleActiveBackoff, VerifyRunLifecycleRejectedRetry,
        VerifyRunLifecycleFaultedTakeover, VerifyRunLifecycleAdjacentMinutes, VerifyRunLifecycleCountdownTarget, VerifyRunLifecycleDeferredPreview,
        VerifyRunLifecycleStoppedOffline, VerifyRunLifecycleReconnectFault, VerifyRunLifecycleCancelledPreparation,
        VerifyRunLifecycleClosingPreview, VerifyRunLifecycleSimultaneousNames, VerifyRunLifecycleMidnightRollback
    ];

    private static Guid Id(int value) => new(value, 1, 2, new byte[8]);
    private static readonly DateTime Epoch = DateTime.UnixEpoch;
    private static readonly EnvironmentTarget Target = new(7, Id(100));
    private static readonly InputVersion Input = new(Id(200), 1, 1);
    private static readonly JsonElement Empty = JsonSerializer.SerializeToElement(new { });
    private static RunStartAttempt Attempt(int n)
    {
        var plan = new RunPlan(1, Epoch.AddSeconds(n), new("project", "1", 1, "project"), "profile", Empty,
            [new(Id(400 + n), "task", "巡逻", "entry", Empty, Empty)]);
        return new(Id(300 + n), plan, $"digest-{n}") { RedactionValues = ["secret"] };
    }

    private static RunIntent Intent(RunSource source, int n = 1) => new(Id(n), source,
        source == RunSource.Scheduled ? StartMode.PrepareAndStart : StartMode.Start, source.ToString(),
        "开始任务", Input, "第一班", Foreground: source == RunSource.Manual, QuitAfterRun: source == RunSource.Launch,
        Attempt: Attempt(n), Opportunity: source == RunSource.Scheduled ? "08:00" : null);

    private static RunSnapshot SnapshotOf(RunStartAttempt attempt, RunState status)
    {
        var item = attempt.Plan.Items.Single();
        var part = new PlanItemSnapshot(item.PlanItemId, item.TaskName, item.TaskLabel, item.Entry, Empty, Empty,
            status == RunState.Failed ? PlanItemState.Failed : PlanItemState.Running,
            Epoch, null, null, null, null);
        return new(attempt.RunId, attempt.PlanDigest, status, Epoch, Epoch, null,
            status is RunState.Running or RunState.Stopping ? null : Epoch.AddMinutes(1),
            null, null, attempt.Plan, [part], null, null);
    }

    private static void Check(bool ok, string why)
    {
        if (!ok) throw new InvalidOperationException(why);
    }

    private sealed class Scenario
    {
        internal RunLifecycleState State = new();
        internal TimeSpan Now;
        internal readonly List<LifecycleEffect> History = [];
        internal LifecycleEffect[] Last = [];
        internal WorkerConnectionId Connection = new(Target.WorkerId!.Value, 1);
        internal long Revision;
        internal long Sequence;
        internal SendStart Send => History.OfType<SendStart>().Last();
        internal RunKey Key => Send.Submission.Run;
        internal TrackedRun Run => State.Runs[Key];

        internal void Step(LifecycleEvent input)
        {
            var step = RunLifecycleReducer.Reduce(State, input with { At = Now });
            State = step.State;
            Last = step.Effects.ToArray();
            History.AddRange(Last);
        }

        internal void Expect(params Type[] ordered) =>
            Check(Last.Select(e => e.GetType()).SequenceEqual(ordered),
                $"动作应为 [{string.Join(", ", ordered.Select(t => t.Name))}]，实际 [{string.Join(", ", Last.Select(e => e.GetType().Name))}]");
        internal T Only<T>() where T : LifecycleEffect => Last.OfType<T>().Single();
        internal int Count<T>() where T : LifecycleEffect => History.OfType<T>().Count();
        internal IEnumerable<T> Items<T>() where T : Payload => History.OfType<Deliver>().Select(d => d.Item).OfType<T>();
        internal T DeliveredItem<T>() where T : Payload => (T)Only<Deliver>().Item;
        internal void Ack()
        {
            var next = State.Deliveries.Single(d => d.InFlight);
            Check(next.Payload is not Cleanup, "收尾必须回投物理结果");
            Step(new Delivered(next.Id));
        }

        internal void DrainDeliveries()
        {
            while (State.Deliveries.Count != 0) {
                if (State.Deliveries.Single(d => d.InFlight) is { Payload: Cleanup } cleanup)
                    Step(new CleanupReturned(cleanup.Id, CleanupResult.Ended));
                else Ack();
            }
        }

        internal WorkerSnapshot Frame(RunSnapshot? active = null, RunSnapshot? last = null, WorkerState worker = WorkerState.Ready)
        {
            var dependency = new DependencyCheck(true, null, null);
            return new(1, Epoch, ++Revision, Connection.WorkerInstanceId, 123, Target.SessionId, "1",
                ProtocolConstants.ProtocolVersion, "profile", new("project", "1", 1, "project"), worker, null,
                new(Epoch, "1", "1", dependency, dependency, dependency), active?.State ?? RunState.Idle,
                active, last, 0, 0);
        }

        internal void FrameEvent(WorkerSnapshot observed, WorkerCoordinatorSnapshot? current = null) =>
            Evidence(new(Connection, WorkerEvidenceKind.Snapshot,
                Current: current ?? new(WorkerObservation.Connected, true, observed, ""), ObservedSnapshot: observed));
        internal void Connect() => FrameEvent(Frame());
        internal void Reconnect()
        {
            Connection = Connection with { Generation = Connection.Generation + 1 };
            Sequence = 0;
            Connect();
        }
        internal void Active() => FrameEvent(Frame(active: SnapshotOf(Send.Attempt, RunState.Running)));
        internal void Terminal(RunState status) => FrameEvent(Frame(last: SnapshotOf(Send.Attempt, status)));
        internal void Evidence(WorkerObservationBatch b) => Step(new Observed(b));
        internal void Eof(Exception? error = null) => Evidence(new(Connection, WorkerEvidenceKind.Disconnected, Error: error));
        internal void ExitWorker() => Evidence(new(Connection, WorkerEvidenceKind.ProcessExited));

        internal void Claim(ImmutableArray<RunIntent> candidates, bool force = false)
        {
            var minute = TimeSpan.FromMinutes(Math.Floor(Now.TotalMinutes));
            Step(new ScheduleClaimed(candidates, Epoch.Date.AddHours(8) + minute, minute + TimeSpan.FromMinutes(1), force));
        }

        internal void PreviewSchedule(RunIntent intent, bool force = false, params RunIntent[] more)
        {
            Claim([intent, .. more], force);
            Expect(typeof(Preview));
            Step(new Previewed(intent.Id, intent));
        }

        internal void Begin(RunIntent intent)
        {
            if (intent.Source == RunSource.Scheduled) {
                PreviewSchedule(intent);
                Expect(typeof(Reserve));
                Step(new Reserved(intent.Id, new(intent.Id, null)));
                Expect(typeof(CommitConfiguration));
                Step(new CommittedConfiguration(intent.Id));
                Expect(typeof(PublishConfiguration), typeof(AppendStart));
                Step(new ActivityAppended(intent.Id));
                Expect(typeof(EndReserved));
                Step(new OldEnvironmentEnded(intent.Id, true));
                Expect(typeof(PrepareEnvironment));
                Step(new Prepared(intent.Id, Target));
                Expect(typeof(CheckReady));
                if (State.Connection == null) Connect();
            } else {
                if (State.Connection == null) Connect();
                Step(new Start(intent));
                Expect(typeof(Reserve));
                Step(new Reserved(intent.Id, new(intent.Id, Target)));
                Expect(typeof(CheckReady));
            }
            Step(new ReadyChecked(intent.Id, Target, intent.Input));
            Expect(typeof(RegisterPlan), typeof(SendStart));
            Check(Send.Submission.IntentId == intent.Id && Send.Submission.PlanDigest == Send.Attempt.PlanDigest,
                "必须先登记和脱敏，再发送该意图的冻结计划");
        }

        internal WorkerRequestEvidence Writer(SendStart? start = null)
        {
            var send = (start ?? Send).Submission;
            return new(Id(1000 + send.IntentId.ToByteArray()[0]), send.Run, send, null, Connection);
        }
        internal WorkerRequestEvidence Body()
        {
            var wire = Writer();
            Evidence(new(Connection, WorkerEvidenceKind.BeforeBody, wire));
            wire = wire with { WriteSequence = ++Sequence };
            Evidence(new(Connection, WorkerEvidenceKind.BodyStarted, wire));
            return wire;
        }
        internal void Reply(WorkerRequestEvidence wire, StartReplyKind kind, string code, Exception? error = null) =>
            Evidence(new(wire.Connection, WorkerEvidenceKind.StartReply, wire, StartReply: kind, ReplyCode: code, Error: error));
        internal void Timeout(WorkerRequestEvidence wire) =>
            Evidence(new(wire.Connection, WorkerEvidenceKind.RequestFailed, wire, Error: new TimeoutException("请求超时")));
        internal WorkerRequestEvidence QueryWire()
        {
            var q = History.OfType<QueryRun>().Last();
            return new(Id((int)(2000 + State.Sequence)), q.Key, null, q.Id, Connection, ++Sequence);
        }
        internal void QueryResult(WorkerRequestEvidence q, bool registered, WorkerSnapshot? frame = null)
        {
            frame ??= Frame();
            Evidence(new(q.Connection, WorkerEvidenceKind.QueryReply, q, new(WorkerObservation.Connected, true, frame, ""),
                frame, RunRegistered: registered));
        }
    }
}

