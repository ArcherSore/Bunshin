using System.Collections.Immutable;
using Gui.Worker;
using static Gui.Runtime.LifecycleEvent;
using static Gui.Runtime.LifecycleEffect;

namespace Gui.Runtime;

internal sealed partial class RunLifecycleReducer
{
    private void SelectCandidate(ImmutableArray<RunIntent> candidates, TimeSpan minuteEnd, bool force)
    {
        var op = new StartOperation(candidates[0], OperationStage.Preview, Remaining: candidates.RemoveAt(0),
            Force: force, MinuteEnd: minuteEnd);
        if (state.Operations.ContainsKey(op.Intent.Id) || state.FailedLocalIntents.Contains(op.Intent.Id)) return;
        if (Blocked(force) is { } reason) {
            SkipOperation(op, reason);
        } else if (!force && state.Operations.Count != 0) {
            SkipOperation(op, "正在运行其他操作");
        } else {
            Save(op);
            effects.Add(new Preview(op.Intent));
        }
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
            if (!e.BatchFailure && !remaining.IsDefaultOrEmpty) SelectCandidate(remaining, op.MinuteEnd, op.Force);
            return;
        }
        if (e.Candidate.Id != op.Intent.Id || e.Candidate.Input.ConfigurationId != op.Intent.Input.ConfigurationId) return;
        op = op with { Intent = op.Intent with { Input = e.Candidate.Input, ConfigurationName = e.Candidate.ConfigurationName,
            Attempt = e.Candidate.Attempt } };
        if (op.Force && AtRisk() != null) BeginCountdown(op);
        else AskReservation(op);
    }

    private RunKey? AtRisk()
    {
        if (state.Current.WorkerSnapshot is { ActiveRun: { } active } live)
            return new(live.WorkerInstanceId, active.RunId);
        return state.Runs.Values.FirstOrDefault(r => !r.Settled && (r.Conclusion == StartConclusion.Accepted
            || r.Sends.Values.Any(s => s.Boundary == SendBoundary.PossiblySent)))?.Key;
    }

    private void BeginCountdown(StartOperation op)
    {
        if (state.Now >= op.MinuteEnd) {
            SkipOperation(op, "当前时间已离开本次定时的分钟");
            return;
        }
        if (op.Lease != null) effects.Add(new Release(op.Lease));
        var deadline = state.Now + TimeSpan.FromSeconds(10);
        SkipRemainder(op, "已有强制定时机会");
        Save(op with { Lease = null, Stage = OperationStage.Countdown, CountdownEnd = deadline, Remaining = [] });
        effects.Add(new ShowCountdown(op.Intent, deadline));
    }

    private void ReserveScheduled(StartOperation op)
    {
        var risk = AtRisk();
        if (op.Force && risk != null && !op.CountdownEnd.HasValue) {
            BeginCountdown(op);
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
        effects.Add(new PublishConfiguration(op.Intent));
        effects.Add(new AppendStart(op.Intent));
        SkipRemainder(op, "本分钟已有获准的配置");
    }

    private void ChangeEnvironment(StartOperation op)
    {
        var current = state.Current;
        var expected = op.Lease!.Expected;
        var risk = AtRisk();
        if (state.QuitPending || risk != null && risk != op.Replacement
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

