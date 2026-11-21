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
        VerifyRunLifecycleSourceConclusions, VerifyRunLifecycleBeforeBodyExit, VerifyRunLifecycleConfirmationClock,
        VerifyRunLifecycleLatePositive, VerifyRunLifecycleQueryBeforeWriter, VerifyRunLifecycleProtocolRecovery,
        VerifyRunLifecycleReconnectNegative, VerifyRunLifecycleReconnectBarrier, VerifyRunLifecycleRetirementOrder,
        VerifyRunLifecycleRetryIdentity, VerifyRunLifecycleRejectedRetry, VerifyRunLifecycleRetryChannels,
        VerifyRunLifecycleBootAttribution, VerifyRunLifecycleStaleActiveBackoff, VerifyRunLifecycleRegisteredQuery,
        VerifyRunLifecycleScheduledTerminals, VerifyRunLifecycleObservedRuns, VerifyRunLifecycleFaultedSlot,
        VerifyRunLifecycleReconnectFault, VerifyRunLifecycleBufferedFaultExit, VerifyRunLifecycleExitBuffer,
        VerifyRunLifecycleAbandonQuit, VerifyRunLifecycleQuitRecovery, VerifyRunLifecycleCompletedPlans
    ];

    private static Guid Id(int value) => new(value, 1, 2, new byte[8]);
    private static readonly DateTime Epoch = DateTime.UnixEpoch;
    private static readonly InputVersion Input = new(Id(200), 1, 1);
    private static readonly JsonElement Empty = JsonSerializer.SerializeToElement(new { });
    private static RunStartAttempt Attempt(int n)
    {
        var plan = new RunPlan(1, Epoch.AddSeconds(n), new("project", "1", 1, "project"), "profile", Empty,
            [new(Id(400 + n), "task", "巡逻", "entry", Empty, Empty)]);
        return new(Id(300 + n), plan, $"digest-{n}") { RedactionValues = ["secret"] };
    }

    private static RunIntent Intent(RunSource source, int n = 1) => new(Id(n), source, "开始任务", Input, "第一班",
        Foreground: source == RunSource.Manual, QuitAfterRun: source == RunSource.Launch);

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
        internal WorkerConnectionId Connection = new(Id(100), 1);
        internal SubmissionStarted Start = null!;
        internal RunKey Key => Start.Key;
        internal RunStartAttempt Plan => Start.Attempt;
        internal string? ConfigurationName;
        private long revision;
        private int sequence;
        private int sends;
        private readonly HashSet<long> acknowledged = [];

        internal void Step(LifecycleEvent input)
        {
            var step = RunLifecycleReducer.Reduce(State, input with { At = Now });
            State = step.State;
            History.AddRange(step.Effects);
        }

        internal void Begin(RunIntent intent, RunStartAttempt? attempt = null)
        {
            attempt ??= Attempt(sends + 1);
            var key = new RunKey(Connection.WorkerInstanceId, attempt.RunId);
            var submission = new Submission(Id(500 + ++sends), intent.Id, key, attempt.PlanDigest);
            Start = new(intent, key, submission, attempt);
            Step(Start);
        }

        internal void Ack(long id)
        {
            if (History.OfType<Deliver>().Any(d => d.Id == id)) acknowledged.Add(id);
            Step(new Delivered(id));
        }

        // Drive exactly the actions the consumer actually received. A missing dispatch or a
        // duplicate action after its receipt must still be visible to the final assertions.
        internal void Finish(params Type[] expected)
        {
            while (History.OfType<Deliver>().FirstOrDefault(d => !acknowledged.Contains(d.Id)) is { } pending) {
                Check(acknowledged.Count < expected.Length, "多余交付：" + pending.Item);
                Ack(pending.Id);
            }
            var sent = History.OfType<Deliver>().ToArray();
            var actual = sent.Select(d => d.Item.GetType()).ToArray();
            Check(State.InFlightDelivery == null && State.Deliveries.IsEmpty && acknowledged.Count == sent.Length
                && actual.SequenceEqual(expected), $"交付历史 [{string.Join(", ", actual.Select(t => t.Name))}]，"
                + $"期望 [{string.Join(", ", expected.Select(t => t.Name))}]");
        }

        internal IEnumerable<T> Items<T>() where T : Payload => History.OfType<Deliver>().Select(d => d.Item).OfType<T>();
        internal void Conclusions(params StartConclusion[] expected) =>
            Check(History.OfType<SubmissionConclusion>().Select(e => e.Conclusion).SequenceEqual(expected), "提交结论时序不符");

        internal WorkerSnapshot Frame(RunSnapshot? active = null, RunSnapshot? last = null, WorkerState worker = WorkerState.Ready)
        {
            var dependency = new DependencyCheck(true, null, null);
            return new(1, Epoch, ++revision, Connection.WorkerInstanceId, 123, 7, "1",
                ProtocolConstants.ProtocolVersion, "profile", new("project", "1", 1, "project"), worker, null,
                new(Epoch, "1", "1", dependency, dependency, dependency), active?.State ?? RunState.Idle,
                active, last, 0, 0);
        }

        internal void Evidence(WorkerObservationBatch b) => Step(new Observed(b, ConfigurationName));
        internal void FrameEvent(WorkerSnapshot observed, WorkerCoordinatorSnapshot? current = null) =>
            Evidence(new(Connection, WorkerEvidenceKind.Snapshot,
                Current: current ?? new(WorkerObservation.Connected, true, observed, ""), ObservedSnapshot: observed));

        internal void Active(WorkerState worker = WorkerState.Ready) => FrameEvent(Frame(SnapshotOf(Plan, RunState.Running), worker: worker));
        internal void Terminal(RunState status) => FrameEvent(Frame(last: SnapshotOf(Plan, status)));
        internal void Eof(Exception? error = null) => Evidence(new(Connection, WorkerEvidenceKind.Disconnected, Error: error));
        internal void ExitWorker() => Evidence(new(Connection, WorkerEvidenceKind.ProcessExited));
        internal void Reconnect(WorkerSnapshot? frame = null)
        {
            Connection = Connection with { Generation = Connection.Generation + 1 };
            sequence = 0;
            FrameEvent(frame ?? Frame());
        }

        internal WorkerRequestEvidence Writer() => new(Id(1000 + sends), Key, Start.Submission, null, Connection);
        internal WorkerRequestEvidence Body()
        {
            var wire = Writer();
            Evidence(new(Connection, WorkerEvidenceKind.BeforeBody, wire));
            wire = wire with { WriteSequence = ++sequence };
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
            return new(q.Id.Value, q.Key, null, q.Id, q.Connection, ++sequence);
        }
        internal void QueryResult(WorkerRequestEvidence q, bool registered, WorkerSnapshot? frame = null)
        {
            frame ??= Frame();
            Evidence(new(q.Connection, WorkerEvidenceKind.QueryReply, q, new(WorkerObservation.Connected, true, frame, ""),
                frame, RunRegistered: registered));
        }
    }
}
