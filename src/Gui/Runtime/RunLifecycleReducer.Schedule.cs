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
        } else if (MissedMinute(op) is { } expired) {
            SkipOperation(op, expired);
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
        if (op.Force && MissedMinute(op) is { } expired) SkipOperation(op, expired);
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
        RecordMinuteWinner(op);
        var deadline = state.Now + TimeSpan.FromSeconds(10);
        SkipRemainder(op, $"与「{op.Intent.ConfigurationName}」同时到点");
        Save(op with { Stage = OperationStage.Countdown, Replacement = target, CountdownEnd = deadline, Remaining = [] });
        effects.Add(new ShowCountdown(op.Intent, deadline));
    }

    private void CancelWarning(StartOperation op)
    {
        effects.Add(new HideCountdown(op.Intent.Id));
        effects.Add(new ScheduleActivity(op.Intent, $"已取消「{op.Intent.ConfigurationName}」的强制启动，当前任务继续"));
    }

    // Two elapsed hours cover a one-hour DST rollback and the repeated hour, including midnight.
    private static readonly TimeSpan WinnerWindow = TimeSpan.FromHours(2);

    private void RecordMinuteWinner(StartOperation op)
    {
        var winners = state.MinuteWinners;
        state = state with { MinuteWinners = winners.RemoveRange(winners.Where(w => state.Now - w.Value.AwardedAt >= WinnerWindow)
            .Select(w => w.Key)).SetItem(op.LocalMinute, (op.Intent.ConfigurationName, state.Now)) };
    }

    private string? MissedMinute(StartOperation op) => op.Intent.Source == RunSource.Scheduled && state.Now >= op.MinuteEnd
        ? "当前时间已离开本次定时的分钟" : null;

    private string? AdmissionBlocker(StartOperation op) => Blocked(op.Force)
        ?? (op.Intent.Source == RunSource.Scheduled ? ChangeoverBlocker(op) : null);

    private string? ChangeoverBlocker(StartOperation op)
    {
        var current = state.Current;
        if (op.Force && state.Runs.Values.Any(r => !r.Settled && r.Owner?.Source == RunSource.Launch && r.ManualStop != null))
            return "正在等待开机任务停止并退出";
        if (!current.NoLiveAdmission && (state.AwaitingFullSnapshot || !current.Fresh || state.Connection is not { } live
            || live.WorkerInstanceId != current.WorkerSnapshot?.WorkerInstanceId))
            return "Worker 连接已断开或状态尚未同步";
        if (op.Force) {
            foreach (var r in state.Runs.Values.Where(r => !r.Settled && (r.Key == op.Replacement || r.Owner?.Source == RunSource.Launch))) {
                if (r.AwaitingReconnectQuery || r.NeedQuery || r.Confirmation.InFlight != null || r.Conclusion != StartConclusion.Accepted)
                    return r.Owner?.Source == RunSource.Launch ? "开机任务的运行证据尚未确认" : "原任务的运行证据尚未确认";
            }
        }
        if (AtRisk() != op.Replacement || op.Replacement is { } key
            && (state.Runs.GetValueOrDefault(key) is not { Settled: false } target || !CurrentShowsActive(target)))
            return op.Force ? "强制启动的目标任务已变化" : "正在运行其他任务";
        if (op.Lease is not { } lease) return null;
        var expected = lease.Expected;
        if (!current.NoLiveAdmission && current.WorkerSnapshot?.WorkerInstanceId != expected?.WorkerId
            || expected?.WorkerId is { } worker && (current.WorkerSnapshot?.WorkerInstanceId != worker
                || current.WorkerSnapshot.ChildSessionId != expected.SessionId || op.Replacement == null && !current.FreshIdleForEnvironmentEnd))
            return "运行环境状态未知";
        return null;
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
        if ((state.QuitPending ? "应用正在退出" : ChangeoverBlocker(op)) is { } reason) {
            Fail(op, new InvalidOperationException(reason));
            return;
        }
        if (op.Replacement is { } key && state.Runs.GetValueOrDefault(key) is { Quit: QuitStage.Bound } old)
            Save(old with { Quit = QuitStage.Suspended });
        Save(op with { Stage = OperationStage.EndOld });
        effects.Add(new EndReserved(op.Lease!, op.Replacement));
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
            else if (!old.Settled) Fail(op, new InvalidOperationException("旧任务的运行结果尚未确认"));
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
            var dialog = state.Deliveries.Find(d => d.InFlight && d.Payload is Present { Channel: PresentationChannel.ForegroundError });
            if (!state.Deliveries.Any(d => d.Payload.Key == key && d.Id != dialog?.Id)) {
                BeginPrepare(op);
            } else if (dialog != null) {
                // A queued old result cannot be dispatched until the open dialog returns. Give up this lease.
                Fail(op, new InvalidOperationException("旧任务的结果仍在等待交付"));
            }
        }
    }
}

