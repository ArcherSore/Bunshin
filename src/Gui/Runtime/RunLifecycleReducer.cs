using System.Collections.Immutable;
using Gui.Worker;
using static Gui.Runtime.LifecycleEvent;
using static Gui.Runtime.LifecycleEffect;

namespace Gui.Runtime;

internal sealed partial class RunLifecycleReducer(RunLifecycleState initial)
{
    private RunLifecycleState state = initial;
    private readonly ImmutableArray<LifecycleEffect>.Builder effects = ImmutableArray.CreateBuilder<LifecycleEffect>();

    internal static LifecycleTransition Reduce(RunLifecycleState state, LifecycleEvent input, bool dispatchDelivery = true,
        bool deferManualErrors = false)
    {
        if (state.ExitCommitted) return new(state, []);
        var step = new RunLifecycleReducer(state with { Now = input.At > state.Now ? input.At : state.Now });
        step.Apply(input);
        step.DispatchQueries();
        if (dispatchDelivery) step.DispatchDelivery(deferManualErrors);
        step.ReleaseCompletedRuns();
        return new(step.state, step.effects.ToImmutable());
    }

    private void Apply(LifecycleEvent input)
    {
        switch (input) {
            case SubmissionStarted e: Register(e); break;
            case Observed e: Observe(e.Batch, e.ConfigurationName); break;
            case ConfirmNow e when state.Runs.TryGetValue(e.Key, out var pending) && !pending.Settled:
                CancelTimer(pending);
                Save(pending with { NeedQuery = true, Confirmation = pending.Confirmation with { Timer = 0, Due = null } });
                break;
            case RetryDue e when state.Runs.TryGetValue(e.Key, out var run)
                    && run.Confirmation.Timer == e.Token && run.Confirmation.Due is { } due:
                if (state.Now < due) {
                    var token = Next();
                    Save(run with { Confirmation = run.Confirmation with { Timer = token } });
                    effects.Add(new ArmRetry(run.Key, token, due));
                } else Save(run with { Confirmation = run.Confirmation with { Timer = 0, Due = null } });
                break;
            case Delivered e: Acknowledge(e); break;
            case CleanupFailed e: Queue(new PresentCleanupFailure(e.Key, e.Name, e.Detail)); break;
            case DeferQuit e when e.Deferred || state.DeferredQuit == e.Key:
                state = state with { DeferredQuit = e.Deferred ? e.Key : null };
                break;
            case AbandonQuit e:
                state = state with { Quits = state.Quits.Remove(e.Key),
                    DeferredQuit = state.DeferredQuit == e.Key ? null : state.DeferredQuit,
                    Deliveries = state.Deliveries.RemoveAll(d => d.Item is RequestQuit q && q.Key == e.Key) };
                break;
            case ClosingStarted e when state.Closing == null: state = state with { Closing = e.Id }; break;
            case ClosingCancelled e when state.Closing == e.Id:
                state = state with { Closing = null,
                    InFlightDelivery = state.InFlightDelivery?.Item is RequestQuit ? null : state.InFlightDelivery };
                foreach (var (key, stage) in state.Quits) {
                    if (stage == QuitStage.Requested) SetQuit(key, QuitStage.AwaitingRecovery);
                    else if (stage == QuitStage.RecoveredWhileClosing) QueueQuit(key);
                }
                break;
            case EnvironmentRecovered:
                RecoverQuitRequests();
                break;
            case ClosingCommitted e when state.Closing == e.Id:
                foreach (var r in state.Runs.Values) CancelConfirmation(r);
                state = new() { ExitCommitted = true };
                break;
        }
    }

    private void RecoverQuitRequests()
    {
        foreach (var (key, stage) in state.Quits) {
            if (stage == QuitStage.AwaitingRecovery) QueueQuit(key);
            else if (stage == QuitStage.Requested && state.Closing != null) SetQuit(key, QuitStage.RecoveredWhileClosing);
        }
    }

    private void Register(SubmissionStarted e)
    {
        var (intent, key, submission, attempt) = e;
        if (submission.Run != key || submission.IntentId != intent.Id || key.RunId != attempt.RunId
            || submission.PlanDigest != attempt.PlanDigest)
            throw new ArgumentException("提交身份与意图或执行计划不一致");

        state.Runs.TryGetValue(key, out var run);
        if (run != null && (run.Submission == submission || run.PriorSubmissions.Contains(submission)
            || run.Sends.GetValueOrDefault(submission.SubmissionId)?.Identity == submission)) return;
        if (run != null || intent.RetryOf?.RunId == key.RunId) {
            if (intent.RetryOf != key || !ReferenceEquals(state.ReusableAttempt(intent, key.WorkerInstanceId), attempt))
                throw new InvalidOperationException("本次提交不能复用原 Run ID");
        }
        run ??= new(key, attempt.PlanDigest);
        if (intent is { Source: RunSource.Launch, QuitAfterRun: true }) SetQuit(key, QuitStage.AwaitingBody);
        Save(run with { Owner = intent, Attempt = attempt, Submission = submission, Conclusion = null,
            Sends = run.Sends.Add(submission.SubmissionId, new(submission)) });
    }

    private void ReleaseCompletedRuns()
    {
        foreach (var r in state.Runs.Values) {
            if (!r.Settled || r.ReusableAttempt != null || state.InFlightDelivery?.Item.Key == r.Key
                || state.Deliveries.Any(d => d.Item.Key == r.Key)) continue;
            if (r.Retired) {
                state = state with { Runs = state.Runs.Remove(r.Key) };
            } else if (r.Attempt != null || r.Terminal != null) {
                Save(new(r.Key, r.Digest) { Submission = r.Submission, Conclusion = r.Conclusion, TerminalState = r.TerminalState,
                    RejectionCode = r.ReuseBlockingRejection(),
                    PriorSubmissions = r.Sends.Values.Select(s => s.Identity).Where(s => s != r.Submission).ToImmutableHashSet(),
                    Notification = r.Notification, NotificationIntent = r.NotificationIntent });
            }
        }
    }

    private void Save(TrackedRun run) => state = state with { Runs = state.Runs.SetItem(run.Key, run) };

    private TrackedRun Conclude(TrackedRun r, StartConclusion conclusion, Exception? error = null)
    {
        if (r.Conclusion != conclusion && r.Submission is { } submission)
            effects.Add(new SubmissionConclusion(submission, conclusion, error));
        return r with { Conclusion = conclusion };
    }

    private long Next()
    {
        state = state with { Sequence = checked(state.Sequence + 1) };
        return state.Sequence;
    }
}
