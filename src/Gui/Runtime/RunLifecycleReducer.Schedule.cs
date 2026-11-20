using System.Collections.Immutable;
using Gui.Worker;
using static Gui.Runtime.LifecycleEvent;
using static Gui.Runtime.LifecycleEffect;

namespace Gui.Runtime;

internal sealed partial class RunLifecycleReducer
{
    private void SelectCandidate(ImmutableArray<RunIntent> candidates, DateTime minute, TimeSpan minuteEnd, bool force)
    {
        var op = new StartOperation(candidates[0], OperationStage.Preview, Remaining: candidates.RemoveAt(0),
            Force: force, LocalMinute: minute, MinuteEnd: minuteEnd);
        if (state.Operations.ContainsKey(op.Intent.Id) || state.FailedLocalIntents.Contains(op.Intent.Id)) return;
        SelectCandidate(op);
    }

    private void SelectCandidate(StartOperation op)
    {
        var waitForPrevious = op.Force && state.Operations.Values.Any(o => o.LocalMinute != op.LocalMinute && SelectingSchedule(o));
        if ((!waitForPrevious || state.Closing != null) && Blocked(op.Force) is { } reason) {
            SkipOperation(op, reason);
        } else if (state.Now >= op.MinuteEnd) {
            SkipOperation(op, "当前时间已离开本次定时的分钟");
        } else if (state.MinuteWinners.TryGetValue(op.LocalMinute, out var winner) && state.Now - winner.AwardedAt < WinnerWindow) {
            SkipOperation(op, $"与「{winner.Name}」同时到点");
        } else if (state.Operations.Values.Any(o => (o.Intent.Id != op.Intent.Id && o.Intent.Source == RunSource.Scheduled
            && o.LocalMinute == op.LocalMinute) || (!op.Force && o.Lease != null))) {
            SkipOperation(op, "正在运行其他操作");
        } else if (waitForPrevious) {
            if (op.Stage != OperationStage.WaitingForPrevious) Save(op with { Stage = OperationStage.WaitingForPrevious });
        } else {
            Save(op with { Stage = OperationStage.Preview });
            effects.Add(new Preview(op.Intent));
        }
    }

    private bool SelectingSchedule(StartOperation op)
    {
        if (op.Intent.Source != RunSource.Scheduled || op.Stage == OperationStage.WaitingForPrevious) return false;
        // Once its own send is at risk, this Run can receive a warning even before the send receipt.
        return op.Stage != OperationStage.Sending || AtRisk() is not { } risk || state.Runs.GetValueOrDefault(risk)?.Owner?.Id != op.Intent.Id;
    }

    private void SkipRemainder(StartOperation op, string reason)
    {
        if (!op.Remaining.IsDefaultOrEmpty)
            foreach (var other in op.Remaining) effects.Add(new Skip(other, reason));
    }

    private void PreviewResult(StartOperation op, Previewed e)
    {
        if (e.Error != null || e.Candidate == null) {
            var remaining = op.Remaining;
            var error = e.Error ?? new InvalidOperationException("无效的定时配置");
            Fail(e.BatchFailure ? op : op with { Remaining = [] }, error);
            if (!e.BatchFailure && !remaining.IsDefaultOrEmpty) SelectCandidate(remaining, op.LocalMinute, op.MinuteEnd, op.Force);
            return;
        }
        if (e.Candidate.Id != op.Intent.Id || e.Candidate.Input.ConfigurationId != op.Intent.Input.ConfigurationId) return;
        op = op with { Intent = op.Intent with { Input = e.Candidate.Input, ConfigurationName = e.Candidate.ConfigurationName,
            Attempt = e.Candidate.Attempt } };
        if (op.Force && state.Now >= op.MinuteEnd) SkipOperation(op, "当前时间已离开本次定时的分钟");
        else if (op.Force && AtRisk() is { } risk) BeginCountdown(op, risk);
        else AskReservation(op);
    }

    private RunKey? AtRisk()
    {
        if (state.Current.WorkerSnapshot is { ActiveRun: { } active } live)
            return new(live.WorkerInstanceId, active.RunId);
        return state.Runs.Values.FirstOrDefault(r => !r.Settled && (r.Conclusion == StartConclusion.Accepted
            || r.Sends.Values.Any(s => s.Boundary == SendBoundary.PossiblySent)))?.Key;
    }

    private void BeginCountdown(StartOperation op, RunKey target)
    {
        if (state.Now >= op.MinuteEnd) {
            SkipOperation(op, "当前时间已离开本次定时的分钟");
            return;
        }
        if (op.Lease != null) effects.Add(new Release(op.Lease));
        RecordMinuteWinner(op);
        var deadline = state.Now + TimeSpan.FromSeconds(10);
        SkipRemainder(op, $"与「{op.Intent.ConfigurationName}」同时到点");
        Save(op with { Lease = null, Stage = OperationStage.Countdown, Replacement = target, CountdownEnd = deadline, Remaining = [] });
        effects.Add(new ShowCountdown(op.Intent, deadline));
    }

    // Two elapsed hours cover a one-hour DST rollback and the repeated hour, including midnight.
    private static readonly TimeSpan WinnerWindow = TimeSpan.FromHours(2);

    private void RecordMinuteWinner(StartOperation op)
    {
        var winners = state.MinuteWinners;
        state = state with { MinuteWinners = winners.RemoveRange(winners.Where(w => state.Now - w.Value.AwardedAt >= WinnerWindow)
            .Select(w => w.Key)).SetItem(op.LocalMinute, (op.Intent.ConfigurationName, state.Now)) };
    }

    private TrackedRun? PendingReplacementEvidence() => state.Runs.Values.FirstOrDefault(r => !r.Settled
        && (r.ReconnectEvidence?.LastRun is { } last && last.RunId == r.Key.RunId && last.PlanDigest == r.Digest
            || r.Quit == QuitStage.Bound && (r.AwaitingReconnectQuery || r.Conclusion != StartConclusion.Accepted || r.NeedQuery)));

    private void ReviewCountdowns()
    {
        foreach (var op in state.Operations.Values.Where(o => o.Stage == OperationStage.ReviewCountdown)) {
            if (Blocked(force: true) is { } reason) SkipOperation(op, reason);
            else if (state.Runs.Values.Any(r => r is { Quit: QuitStage.Bound, ManualStop: not null }
                && state.Connection?.WorkerInstanceId != r.Key.WorkerInstanceId))
                SkipOperation(op, "正在等待开机任务停止并退出");
            else if (PendingReplacementEvidence() is { } run) Save(run with { NeedQuery = true });
            else AskReservation(op);
        }
    }

    private void ReserveScheduled(StartOperation op)
    {
        if (op.CountdownEnd.HasValue && PendingReplacementEvidence() != null) {
            effects.Add(new Release(op.Lease!));
            Save(op with { Stage = OperationStage.ReviewCountdown, Lease = null });
            return;
        }
        var risk = AtRisk();
        if (op.Force && risk is { } targetAtRisk && targetAtRisk != op.Replacement) {
            BeginCountdown(op, targetAtRisk);
            return;
        }
        var expected = op.Lease!.Expected;
        if (risk != null && risk.Value.WorkerInstanceId != expected?.WorkerId
            || !state.Current.NoLiveAdmission && state.Current.WorkerSnapshot is { } live && live.WorkerInstanceId != expected?.WorkerId
            || expected?.WorkerId is { } worker && (state.Current.WorkerSnapshot?.WorkerInstanceId != worker
            || state.Current.WorkerSnapshot.ChildSessionId != expected.SessionId
            || !op.CountdownEnd.HasValue && !state.Current.FreshIdleForEnvironmentEnd)) {
            SkipOperation(op, "运行环境状态未知");
            return;
        }
        if (risk is { } target && state.Runs.GetValueOrDefault(target) is { ManualStop: not null, Quit: QuitStage.Bound }
            && state.Current.WorkerSnapshot?.WorkerState != Protocol.WorkerState.Faulted) {
            SkipOperation(op, "正在等待开机任务停止并退出");
            return;
        }
        Save(op with { Stage = OperationStage.Commit, Replacement = op.Force ? risk : null });
        effects.Add(new CommitConfiguration(op.Intent, op.Lease));
    }

    private void ConfigResult(StartOperation op, CommittedConfiguration e)
    {
        if (e.Error != null) {
            Fail(op, e.Error, "保存定时配置失败");
            return;
        }
        Save(op with { Stage = OperationStage.Activity, Admitted = true, Remaining = [] });
        if (!op.CountdownEnd.HasValue) RecordMinuteWinner(op);
        effects.Add(new PublishConfiguration(op.Intent));
        effects.Add(new AppendStart(op.Intent));
        SkipRemainder(op, $"与「{op.Intent.ConfigurationName}」同时到点");
    }

    private void ChangeEnvironment(StartOperation op)
    {
        if (op.Cancelled) {
            Finish(op);
            return;
        }
        var current = state.Current;
        var expected = op.Lease!.Expected;
        var risk = AtRisk();
        if (state.QuitPending || op.CountdownEnd.HasValue && PendingReplacementEvidence() != null || risk != null && risk != op.Replacement
            || !current.NoLiveAdmission && current.WorkerSnapshot is { } observed && observed.WorkerInstanceId != expected?.WorkerId
            || expected?.WorkerId is { } worker && (current.WorkerSnapshot?.WorkerInstanceId != worker
                || current.WorkerSnapshot.ChildSessionId != expected.SessionId
                || op.Replacement == null && !current.FreshIdleForEnvironmentEnd)) {
            Fail(op, new InvalidOperationException("换场目标已变化"));
            return;
        }
        if (op.Replacement is { } key && state.Runs.GetValueOrDefault(key) is { Quit: QuitStage.Bound } old) {
            if (old.ManualStop != null && current.WorkerSnapshot?.WorkerState != Protocol.WorkerState.Faulted) {
                Fail(op, new InvalidOperationException("正在等待开机任务停止并退出"));
                return;
            }
            Save(old with { Quit = QuitStage.Suspended });
        }
        Save(op with { Stage = OperationStage.EndOld });
        effects.Add(new EndReserved(op.Lease, op.Replacement));
    }

    private void EndResult(StartOperation op, OldEnvironmentEnded e)
    {
        if (op.Lease!.Expected is { } expected)
            MarkTargetCleanup(expected, e.Ended ? CleanupStage.Superseded : CleanupStage.Failed);
        if (!e.Ended) {
            RestoreBinding(op);
            Fail(op, e.Error ?? new InvalidOperationException("旧的桌面分身未能关闭"), "结束旧运行环境失败");
            return;
        }
        if (op.Lease.Expected != null) effects.Add(new ScheduleActivity(op.Intent, "已关闭旧的桌面分身"));
        if (op.Replacement is { } key && state.Runs.TryGetValue(key, out var old)) {
            if (old.Quit == QuitStage.Suspended) Save(old with { Quit = QuitStage.None });
            if (op.Cancelled) Finish(op);
            else Save(op with { Stage = OperationStage.DrainOld });
        } else if (op.Cancelled) {
            Finish(op);
        } else {
            BeginPrepare(op);
        }
    }

    private void RestoreBinding(StartOperation op)
    {
        if (op.Replacement is not { } key || state.Runs.GetValueOrDefault(key) is not { Quit: QuitStage.Suspended } old) return;
        Save(old with { Quit = old.Terminal != null ? QuitStage.Pending : old.Settled ? QuitStage.None : QuitStage.Bound });
        if (old.Terminal != null) Queue(new RequestQuit(key));
    }

    private void DrainReplacements()
    {
        foreach (var op in state.Operations.Values.Where(o => o.Stage == OperationStage.DrainOld)) {
            var key = op.Replacement!.Value;
            if (state.Runs.GetValueOrDefault(key) is { Settled: false }
                || state.Deliveries.Any(d => d.Payload.Key == key)) continue;
            BeginPrepare(op);
        }
    }
}

