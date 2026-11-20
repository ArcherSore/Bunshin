using System.Collections.Immutable;
using Gui.Worker;
using ProjectModel;
using Protocol;
using static Gui.Runtime.LifecycleEvent;
using static Gui.Runtime.LifecycleEffect;

namespace Gui.Runtime;

internal sealed partial class RunLifecycleReducer(RunLifecycleState initial)
{
    private RunLifecycleState state = initial;
    private readonly ImmutableArray<LifecycleEffect>.Builder effects = ImmutableArray.CreateBuilder<LifecycleEffect>();

    internal static LifecycleTransition Reduce(RunLifecycleState state, LifecycleEvent input)
    {
        var step = new RunLifecycleReducer(state with { Now = input.At > state.Now ? input.At : state.Now });
        if (state.Closing?.Stage != ClosingStage.Committed) {
            step.Apply(input);
            step.Dispatch();
        }
        return new(step.state, step.effects.ToImmutable());
    }

    private void Apply(LifecycleEvent input)
    {
        switch (input) {
            case Start e: StartRequested(e); break;
            case ScheduleClaimed e when !e.Candidates.IsDefaultOrEmpty:
                SelectCandidate(e.Candidates, e.LocalMinute, e.MinuteEnd, e.Force); break;
            case Previewed e when At(e.Id, OperationStage.Preview) is { } op: PreviewResult(op, e); break;
            case Reserved e: ReservationResult(e); break;
            case CommittedConfiguration e when At(e.Id, OperationStage.Commit) is { } op: ConfigResult(op, e); break;
            case ActivityAppended e when At(e.Id, OperationStage.Activity) is { } op: ChangeEnvironment(op); break;
            case OldEnvironmentEnded e when At(e.Id, OperationStage.EndOld) is { } op: EndResult(op, e); break;
            case Prepared e when At(e.Id, OperationStage.Prepare) is { } op: PrepareResult(op, e); break;
            case ReadyChecked e when At(e.Id, OperationStage.Ready) is { } op: ReadyResult(op, e); break;
            case PlanResolved e when At(e.Id, OperationStage.Resolve) is { } op:
                if (e.Error != null || e.Attempt == null) Fail(op, e.Error ?? new InvalidOperationException("执行计划为空"));
                else AuthorizeStart(op, e.Attempt);
                break;
            case CountdownDue e when At(e.Id, OperationStage.Countdown) is { } op && state.Now >= op.CountdownEnd:
                effects.Add(new HideCountdown(e.Id));
                if ((MissedMinute(op) ?? AdmissionBlocker(op)) is { } reason) SkipOperation(op, reason);
                else AskReservation(op);
                break;
            case CancelCountdown e when At(e.Id, OperationStage.Countdown) is { } op:
                CancelWarning(op);
                Finish(op); break;
            case Observed e: Observe(e.Batch); break;
            case RetryDue e when state.Runs.TryGetValue(e.Key, out var run)
                    && run.Confirmation.Timer == e.Token && state.Now >= run.Confirmation.Due:
                Save(run with { Confirmation = run.Confirmation with { Timer = 0, Due = null } }); break;
            case Stop e: BeginStop(e); break;
            case StopGateAcquired e: StopAcquired(e.Id); break;
            case StopReturned e: FinishStop(e); break;
            case BeginClosing e: Pause(e); break;
            case ClosingConfirmed e when ClosingAt(e.Id, ClosingStage.Confirm) is { } closing:
                if (!e.Proceed) Resume(closing);
                else {
                    foreach (var send in state.Runs.Values.SelectMany(r => r.Sends.Values)
                        .Where(s => s.Boundary == SendBoundary.BeforeBody && At(s.Intent.Id, OperationStage.Sending) != null))
                        effects.Add(new CancelSend(send.Identity));
                    state = state with { Closing = closing with { Stage = ClosingStage.WaitingForOperations } };
                }
                break;
            case ClosingEndReturned e when ClosingAt(e.Id, ClosingStage.End) is { } closing: ClosingEnded(closing, e); break;
            case HandoffReturned e when ClosingAt(e.Id, ClosingStage.Handoff) is { } closing:
                if (e.TakenOver) CommitClosing(closing);
                else Resume(closing);
                break;
            case EnvironmentRecovered:
                foreach (var r in state.Runs.Values.Where(r => r.Cleanup == CleanupStage.Failed))
                    Save(r with { Cleanup = CleanupStage.Superseded });
                RetryQuit(); break;
            case Delivered e: Acknowledge(e.Id); break;
            case CleanupReturned e: CleanupReceipt(e); break;
        }
    }

    private void Save(TrackedRun run) => state = state with { Runs = state.Runs.SetItem(run.Key, run) };
    private void Save(StartOperation op) => state = state with { Operations = state.Operations.SetItem(op.Intent.Id, op) };
    private StartOperation? At(Guid id, OperationStage stage) =>
        state.Operations.GetValueOrDefault(id) is { } op && op.Stage == stage ? op : null;
    private long Next()
    {
        state = state with { Sequence = checked(state.Sequence + 1) };
        return state.Sequence;
    }

    // These IDs are correlations within one GUI lifetime. Intent and Run IDs come from the caller/plan.
    private Guid NextId() => new(0, 0, 0, BitConverter.GetBytes(Next()));

    private string? Blocked(bool force = false)
    {
        if (state.Closing != null || state.QuitPending) return "应用正在退出";
        if (state.Runs.Values.Any(r => r.Cleanup == CleanupStage.Failed && !r.Retired))
            return "旧的桌面分身未能关闭";
        if (state.Runs.Values.Any(r => r.Cleanup is CleanupStage.Claimed or CleanupStage.WaitingForReconnect))
            return "正在收尾";
        if (!force && (state.Runs.Values.Any(r => !r.Settled) || state.Current.WorkerSnapshot?.ActiveRun != null))
            return "正在运行其他任务";
        return null;
    }

    private void StartRequested(Start e)
    {
        if (state.Operations.ContainsKey(e.Intent.Id) || state.FailedLocalIntents.Contains(e.Intent.Id)) return;
        var op = new StartOperation(e.Intent, OperationStage.Reserve);
        if (e.Intent is { Mode: StartMode.Retry, RetryOf: { } retry } && state.Runs.GetValueOrDefault(retry)?.Owner is { } previous)
            op = op with { Intent = op.Intent with { PreviousIntent = previous.Id } };
        var reason = Blocked();
        if (reason != null || state.Operations.Values.Any(o => o.Lease != null)) {
            SkipOperation(op, reason ?? "正在运行其他操作");
        } else if (e.ValidationError != null) {
            Fail(op, e.ValidationError);
        } else {
            AskReservation(op);
        }
    }

    private void AskReservation(StartOperation op)
    {
        Save(op with { Stage = OperationStage.Reserve });
        effects.Add(new Reserve(op.Intent, op.Force, op.CountdownEnd.HasValue));
    }

    private void ReservationResult(Reserved e)
    {
        if (At(e.Id, OperationStage.Reserve) is not { } op) return;
        if (e.Lease != null && e.Lease.Operation != e.Id) return;
        if (e.Lease == null) {
            SkipOperation(op, e.BusyReason ?? "环境正忙");
            return;
        }
        op = op with { Lease = e.Lease };
        if (MissedMinute(op) is { } expired) {
            SkipOperation(op, expired);
            return;
        }
        if (!op.Force && op.Intent.Source is RunSource.Launch or RunSource.Scheduled) {
            var otherSource = op.Intent.Source == RunSource.Launch ? RunSource.Scheduled : RunSource.Launch;
            foreach (var other in state.Operations.Values.Where(o => o.Intent.Source == otherSource && !o.Force
                && o.Stage is OperationStage.Preview or OperationStage.Reserve).OrderBy(o => o.Intent.Id)) {
                if (other.Stage == OperationStage.Reserve) effects.Add(new CancelOperation(other.Intent.Id, null));
                SkipOperation(other, "另一个启动请求已取得环境闸门");
            }
        }
        if (e.Validated is { } validated) {
            if (validated.Id != e.Id || validated.Input.ConfigurationId != op.Intent.Input.ConfigurationId) {
                SkipOperation(op, "目标配置已变化");
                return;
            }
            op = op with { Intent = op.Intent with { Input = validated.Input, Attempt = validated.Attempt,
                ConfigurationName = validated.ConfigurationName } };
        }
        if (AdmissionBlocker(op) is { } reason) {
            SkipOperation(op, reason);
        } else if (op.Intent.Source == RunSource.Scheduled) {
            Save(op with { Stage = OperationStage.Commit });
            effects.Add(new CommitConfiguration(op.Intent, e.Lease));
        } else if (op.Intent.Mode is StartMode.PrepareOnly or StartMode.PrepareAndStart) {
            BeginPrepare(op);
        } else {
            WaitReady(op, e.Lease.Expected);
        }
    }

    private void BeginPrepare(StartOperation op)
    {
        Save(op with { Stage = OperationStage.Prepare });
        effects.Add(new PrepareEnvironment(op.Intent, op.Intent is { Source: RunSource.Manual, Mode: StartMode.PrepareOnly }));
    }

    private void PrepareResult(StartOperation op, Prepared e)
    {
        if (e.Error != null || e.Target == null) {
            var error = e.Error ?? new InvalidOperationException("运行环境尚未准备好");
            state = state with { PreparationFailure = error };
            Fail(op, error, "准备运行环境失败");
            return;
        }
        state = state with { PreparationFailure = null };
        if (op.Cancelled || op.Intent.Mode == StartMode.PrepareOnly) Finish(op);
        else WaitReady(op, e.Target);
    }

    private void WaitReady(StartOperation op, EnvironmentTarget? expected)
    {
        Save(op with { Stage = OperationStage.Ready, Prepared = expected });
        effects.Add(new CheckReady(op.Intent, expected, TimeSpan.FromSeconds(60)));
    }

    private bool CanSend(StartOperation op) => Blocked() == null && op.Prepared is { } expected
        && state.Current is { FreshReadyToStart: true, WorkerSnapshot: { } current }
        && current.ChildSessionId == expected.SessionId && current.WorkerInstanceId == expected.WorkerId
        && state.Connection?.WorkerInstanceId == expected.WorkerId;

    private void ReadyResult(StartOperation op, ReadyChecked e)
    {
        var expected = op.Prepared;
        op = op with { Prepared = e.Target };
        if (e.Error != null || e.Input != op.Intent.Input || expected == null || e.Target?.SessionId != expected.SessionId
            || expected.WorkerId != null && expected.WorkerId != e.Target.WorkerId || !CanSend(op)) {
            Fail(op, e.Error ?? new InvalidOperationException("当前项目或运行环境已变化"));
            return;
        }
        if (op.Intent is { Mode: StartMode.Retry, RetryOf: { } oldKey }
            && state.Runs.TryGetValue(oldKey, out var old) && old.Owner is { } owner
            && oldKey.WorkerInstanceId == e.Target!.WorkerId && owner.Input == e.Input
            && old.Conclusion == StartConclusion.NotStarted && owner.Attempt != null) {
            var denied = old.Sends.Values.FirstOrDefault(s => s.ReplyCode == "run_id_conflict"
                || s.Intent.Id == owner.Id && s.Reply == StartReplyKind.RejectedBeforeRegistration
                && s.ReplyCode is not ("worker_busy" or "worker_not_ready" or "worker_faulted"
                    or "operation_not_allowed" or "protocol_version_mismatch"));
            if (denied != null) {
                Fail(op, denied.ReplyError ?? denied.Error ?? new InvalidOperationException($"Worker 拒绝启动：{denied.ReplyCode}"));
                return;
            }
            AuthorizeStart(op with { Intent = op.Intent with { PreviousIntent = owner.Id } }, owner.Attempt);
        } else if (op.Intent.Attempt is { } attempt && op.Intent.RetryOf?.RunId != attempt.RunId) {
            AuthorizeStart(op, attempt);
        } else {
            Save(op with { Stage = OperationStage.Resolve });
            effects.Add(new ResolvePlan(op.Intent));
        }
    }

    private void AuthorizeStart(StartOperation op, RunStartAttempt attempt)
    {
        if (!CanSend(op) || state.Current.WorkerSnapshot!.RuntimeProfileDigest != attempt.Plan.RuntimeProfileDigest
            || attempt.Plan.Items.Count == 0) {
            Fail(op, new InvalidOperationException("当前 Worker 或执行计划不允许启动"));
            return;
        }
        var key = new RunKey(op.Prepared!.WorkerId!.Value, attempt.RunId);
        var intent = op.Intent with { Attempt = attempt };
        var run = state.Runs.GetValueOrDefault(key);
        if (run == null && intent.RetryOf?.RunId == attempt.RunId
            || run != null && (run.Conclusion != StartConclusion.NotStarted || run.Digest != attempt.PlanDigest
            || intent.PreviousIntent != run.Owner?.Id || intent.Mode != StartMode.Retry
            || !ReferenceEquals(attempt, run.Owner?.Attempt) || run.Sends.Values.Any(s => s.ReplyCode == "run_id_conflict"))) {
            Fail(op, new InvalidOperationException("Run ID 与原执行计划冲突"));
            return;
        }
        run ??= new(key, attempt.PlanDigest, op.Prepared, intent, ImmutableDictionary<Guid, SendRecord>.Empty);
        var submission = new Submission(NextId(), intent.Id, key, attempt.PlanDigest);
        var send = new SendRecord(submission, intent, SendBoundary.BeforeBody);
        Save(run with { Owner = intent, Conclusion = null, NeedQuery = false, Quit = QuitStage.None,
            Confirmation = new(), Sends = run.Sends.Add(submission.SubmissionId, send) });
        Save(op with { Stage = OperationStage.Sending, Intent = intent });
        effects.Add(new RegisterPlan(submission, attempt));
        effects.Add(new SendStart(submission, attempt));
    }

    private void Finish(StartOperation op, RunKey? key = null, Exception? error = null)
    {
        if (key == null && op.Cancelled && op.Intent.Mode == StartMode.PrepareAndStart)
            error ??= new OperationCanceledException("启动已取消");
        state = state with { Operations = state.Operations.Remove(op.Intent.Id) };
        if (op.Lease != null) effects.Add(new Release(op.Lease));
        effects.Add(new OperationFinished(op.Intent, op.Admitted, key, error));
    }

    private void Fail(StartOperation op, Exception error, string? operation = null)
    {
        Finish(op, error: error);
        state = state with { FailedLocalIntents = state.FailedLocalIntents.Add(op.Intent.Id) };
        PresentFailure(null, op.Intent, NoticeKind.NotStarted, error, operation);
        SkipRemainder(op, error.Message);
    }

    private void SkipOperation(StartOperation op, string reason)
    {
        effects.Add(new Skip(op.Intent, reason));
        SkipRemainder(op, reason);
        Finish(op);
    }
}

