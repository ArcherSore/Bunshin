using System.Collections.Immutable;
using Gui.Worker;
using static Gui.Runtime.LifecycleEvent;
using static Gui.Runtime.LifecycleEffect;

namespace Gui.Runtime;

internal sealed partial class RunLifecycleReducer(RunLifecycleState initial)
{
    private RunLifecycleState state = initial;
    private readonly ImmutableArray<LifecycleEffect>.Builder effects = ImmutableArray.CreateBuilder<LifecycleEffect>();

    internal static LifecycleTransition Reduce(RunLifecycleState state, LifecycleEvent input)
    {
        if (state.ExitCommitted) return new(state, []);
        var step = new RunLifecycleReducer(state with { Now = input.At > state.Now ? input.At : state.Now });
        step.Apply(input);
        step.DispatchQueries();
        step.DispatchDelivery();
        foreach (var run in step.state.Runs.Values.Where(r => r.Retired && step.state.InFlightDelivery?.Item.Key != r.Key
            && !step.state.Deliveries.Any(d => d.Item.Key == r.Key))) {
            step.state = step.state with { Runs = step.state.Runs.Remove(run.Key) };
        }
        return new(step.state, step.effects.ToImmutable());
    }

    private void Apply(LifecycleEvent input)
    {
        switch (input) {
            case SubmissionStarted e: Register(e); break;
            case Observed e: Observe(e.Batch, e.ConfigurationName); break;
            case RetryDue e when state.Runs.TryGetValue(e.Key, out var run)
                    && run.Confirmation.Timer == e.Token && state.Now >= run.Confirmation.Due:
                Save(run with { Confirmation = run.Confirmation with { Timer = 0, Due = null } }); break;
            case Delivered e: Acknowledge(e.Id); break;
            case ClosingStarted e when state.Closing == null: state = state with { Closing = e.Id }; break;
            case ClosingCancelled e when state.Closing == e.Id: state = state with { Closing = null }; break;
            case ClosingCommitted e when state.Closing == e.Id:
                foreach (var r in state.Runs.Values) CancelConfirmation(r);
                state = new() { ExitCommitted = true };
                break;
        }
    }

    private void Register(SubmissionStarted e)
    {
        var (intent, key, submission, attempt) = e;
        if (submission.Run != key || submission.IntentId != intent.Id || key.RunId != attempt.RunId
            || submission.PlanDigest != attempt.PlanDigest)
            throw new ArgumentException("提交身份与意图或执行计划不一致");

        state.Runs.TryGetValue(key, out var run);
        if (run?.Sends.GetValueOrDefault(submission.SubmissionId)?.Identity == submission) return;
        if (run != null || intent.RetryOf?.RunId == key.RunId) {
            if (intent.RetryOf != key || !ReferenceEquals(state.ReusableAttempt(intent, key.WorkerInstanceId), attempt))
                throw new InvalidOperationException("本次提交不能复用原 Run ID");
        }
        run ??= new(key, attempt.PlanDigest);
        Save(run with { Owner = intent, Attempt = attempt, Submission = submission, Conclusion = null,
            QuitBound = false, Sends = run.Sends.Add(submission.SubmissionId, new(submission)) });
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
