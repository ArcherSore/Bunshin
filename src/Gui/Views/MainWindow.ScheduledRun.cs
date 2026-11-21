using Gui.Worker;
using Gui.Runtime;
using Gui.Models;
using ProjectModel;
using Protocol;

namespace Gui.Views;

internal enum ScheduledRunDisposition { Invalid, Busy, Failed, CountdownRequired, Admitted }

internal sealed record ScheduledRunResult(
    ScheduledRunDisposition Disposition, Guid? RunId = null, string? Reason = null);

internal sealed record ForcedScheduledStartCountdown(string ConfigurationName, DateTimeOffset Deadline, Action Cancel);

public partial class MainWindow
{
    private sealed record ScheduledSubmission(string Name, RunKey Key)
    {
        public bool CleanupRequested { get; set; }
        public bool CleanupQueued { get; set; }
        public Exception? CleanupFailure { get; set; }
    }

    private ScheduledSubmission? _scheduledSubmission;
    private TaskCompletionSource<bool>? _forcedStartCountdown;
    private int _exitGeneration;
    private Guid? _manuallyStoppedRun;
    internal event EventHandler<ForcedScheduledStartCountdown>? ForcedScheduledStartPending;
    internal event EventHandler? ForcedScheduledStartEnded;

    private void AddScheduleActivity(string message, LogLevel level = LogLevel.Info)
    {
        AddActivityEntry(new(_scheduleClock.GetLocalNow(), level, message, TaskLabel: "定时"));
    }

    private void ReportScheduledStartFailure(string name, string reason, Exception? exception = null)
    {
        var message = $"「{name}」未能启动：{reason}";
        _logger.Error(message, exception);
        AddScheduleActivity(message, LogLevel.Error);
        Notify(new("定时任务未能启动", $"「{name}」：{reason}", true, "开始任务失败", exception));
    }

    private void ReportScheduledSkip(string name, string reason)
    {
        var message = $"跳过「{name}」：{reason}";
        _logger.Info(message);
        AddScheduleActivity(message);
    }

    internal event EventHandler<RunSnapshot>? ScheduledRunCompleted;

    private void RenderScheduledActivation(Guid configurationId)
    {
        TaskPlanEditor.RenderActivatedConfiguration(configurationId);
        if (ScheduleView.IsVisible) {
            ScheduleView.Refresh();
        }
    }

    // Called on the GUI dispatcher. Invalid leaves the opportunity to the next candidate.
    internal async Task<ScheduledRunResult> TryStartScheduledRunAsync(Guid configurationId, string? policyTime = null)
    {
        var name = ConfigurationName(configurationId) ?? configurationId.ToString();
        ScheduledRunResult? result = null;
        var force = _settings.ForceScheduledStart.Value;
        var previous = _scheduledSubmission;
        _lifecycle.Drain();
        var exitGeneration = _exitGeneration;
        var dueMinute = CurrentScheduleMinute();
        async Task StartInGateAsync(bool countdownElapsed)
        {
            result = await StartScheduledRunInGateAsync(configurationId, name, policyTime, force, countdownElapsed,
                previous, exitGeneration);
        }
        if (force && !ScheduledStartClosing(exitGeneration)) {
            try {
                if (_workerCoordinator.Snapshot is { NoLiveAdmission: false, WorkerSnapshot.ActiveRun: not null }) {
                    result = ValidateScheduledStart(_projectPlan, configurationId, name, out _)
                        ?? new(ScheduledRunDisposition.CountdownRequired);
                } else {
                    await _environment.RunExclusiveAsync(() => StartInGateAsync(false));
                }
                if (result?.Disposition == ScheduledRunDisposition.CountdownRequired) {
                    if (CurrentScheduleMinute() != dueMinute) {
                        result = new(ScheduledRunDisposition.Busy, Reason: "当前时间已离开本次定时的分钟");
                    } else if (!ScheduledStartClosing(exitGeneration)) {
                        name = ConfigurationName(configurationId) ?? name;
                        if (await WaitForForcedScheduledStartAsync(name, exitGeneration)) {
                            await _environment.RunExclusiveAsync(() => StartInGateAsync(true));
                            if (result is { Disposition: ScheduledRunDisposition.Busy, Reason: { } reason }) {
                                ReportScheduledSkip(name, reason);
                            }
                        }
                        // Once announced, even a cancelled or subsequently invalid configuration owns the minute.
                        result = new(ScheduledRunDisposition.Admitted, result.RunId, result.Reason);
                    } else {
                        result = new(ScheduledRunDisposition.Busy, Reason: "应用正在退出");
                    }
                }
            } catch (OperationCanceledException) when (ScheduledStartClosing(exitGeneration)) {
                result = new(ScheduledRunDisposition.Busy, Reason: "应用正在退出");
            }
        } else if (!force && !OperationOrExitInProgress) {
            await _environment.TryRunExclusiveAsync(() => StartInGateAsync(false));
        }
        result ??= new(ScheduledRunDisposition.Busy, Reason: ScheduledStartClosing(exitGeneration)
            ? "应用正在退出" : "正在运行其他任务");
        if (result.Disposition == ScheduledRunDisposition.Busy) {
            ReportScheduledSkip(name, result.Reason!);
        }
        ResumeScheduledCleanup();
        return result;
    }

    private async Task<bool> WaitForForcedScheduledStartAsync(string name, int exitGeneration)
    {
        var duration = TimeSpan.FromSeconds(10);
        var elapsed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _forcedStartCountdown = elapsed;
        var deadline = _scheduleClock.GetLocalNow() + duration;
        using var timer = _scheduleClock.CreateTimer(_ => elapsed.TrySetResult(true), null, duration,
            Timeout.InfiniteTimeSpan);
        try {
            AddScheduleActivity($"「{name}」到点，强制停止当前任务");
            Notify(new("强制定时启动", $"10 秒后将停止当前任务，并启动「{name}」。点击打开窗口可取消", false));
            ForcedScheduledStartPending?.Invoke(this, new(name, deadline, () => elapsed.TrySetResult(false)));
            if (await elapsed.Task) {
                return !ScheduledStartClosing(exitGeneration);
            }
            AddScheduleActivity($"已取消「{name}」的强制启动，当前任务继续");
            return false;
        } finally {
            _forcedStartCountdown = null;
            ForcedScheduledStartEnded?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool ScheduledStartClosing(int generation) =>
        _exitInProgress || _allowClose || _environment.Closing || generation != _exitGeneration;

    private ScheduledRunResult? ValidateScheduledStart(ProjectPlanModule? project, Guid configurationId, string name,
        out RunStartAttempt? attempt)
    {
        attempt = null;
        if (project is null || project.ConfigurationsUnreadable) {
            const string detail = "无法读取任务配置。";
            ReportScheduledStartFailure(name, detail);
            return new(ScheduledRunDisposition.Failed, Reason: detail);
        }
        var active = configurationId == project.ActiveConfigurationId;
        if (active) {
            if (SaveEditor() is { } failure) {
                return failure;
            }
            name = ActiveConfigurationName;
        }
        try {
            attempt = CreateConfigurationAttempt(project, configurationId);
        } catch (Exception exception) {
            var reason = exception.Message.TrimEnd().TrimEnd('。')
                .Replace("请先勾选要执行的任务或前置任务", "执行计划为空").Replace("Run Plan", "执行计划");
            ReportScheduledStartFailure(name, reason, exception);
            return new(ScheduledRunDisposition.Invalid, Reason: exception.Message);
        }

        return active ? null : SaveEditor();

        ScheduledRunResult? SaveEditor()
        {
            try {
                TaskPlanEditor.CommitFocusedConfigurationInput(unattended: true);
                return null;
            } catch (Exception error) {
                var reason = $"无法保存配置「{ActiveConfigurationName}」";
                ReportScheduledStartFailure(name, reason, error);
                return new(ScheduledRunDisposition.Failed, Reason: reason);
            }
        }
    }

    private async Task<ScheduledRunResult> StartScheduledRunInGateAsync(Guid configurationId, string name,
        string? policyTime, bool force, bool countdownElapsed, ScheduledSubmission? previous, int exitGeneration)
    {
        if (ScheduledStartClosing(exitGeneration)) {
            return new(ScheduledRunDisposition.Busy, Reason: "应用正在退出");
        }
        if (force) {
            await _workerCoordinator.ReconcilePendingStartAsync();
        } else if (_environment.ScheduledRunBlocker(out _, out _) is { } blocker) {
            return new(ScheduledRunDisposition.Busy, Reason: blocker);
        }
        // The clock may run ahead of the dispatcher's snapshot callback. Consume the old Run before replacing it.
        previous ??= _scheduledSubmission;
        _lifecycle.Drain();
        if (previous is { CleanupRequested: true }) await EndScheduledRunAsync(previous, gateHeld: true);
        if (ScheduledStartClosing(exitGeneration) || !force && _environment.EndUnconfirmed) {
            return new(ScheduledRunDisposition.Busy, Reason: "运行环境状态未知");
        }
        if (force && _environment.EndUnconfirmed && previous?.CleanupFailure is { } cleanupFailure) {
            const string reason = "旧的桌面分身未能关闭";
            ReportScheduledStartFailure(name, reason, cleanupFailure);
            return new(ScheduledRunDisposition.Admitted, Reason: reason);
        }
        // Manual preparation can reload the project during the countdown or while we wait for admission.
        var project = _projectPlan;
        if (ValidateScheduledStart(project, configurationId, name, out var attempt) is { } failure) {
            return failure;
        }
        name = ConfigurationName(configurationId) ?? name;
        var hadSession = false;
        if (!force && _environment.ScheduledRunBlocker(out _, out hadSession) is { } changed) {
            return new(ScheduledRunDisposition.Busy, Reason: changed);
        }
        var (current, pendingStart) = _environment.ReadRunObservation();
        var active = current.NoLiveAdmission ? null : current.WorkerSnapshot?.ActiveRun;
        // An ordered read can fail on disconnect. The outstanding start may still be running.
        var atRisk = active?.RunId ?? pendingStart;
        if (force) {
            if (!countdownElapsed && atRisk.HasValue) {
                return new(ScheduledRunDisposition.CountdownRequired);
            }
            hadSession = _sessionManager.HasChildSession;
            if (!countdownElapsed && hadSession && current.LiveAdmissionWithoutFreshSnapshot) {
                return new(ScheduledRunDisposition.Busy, Reason: "尚未确认当前任务状态");
            }
        }
        RunKey? replacing = countdownElapsed && current.WorkerSnapshot is { } currentWorker && atRisk is Guid id
            && _lifecycle.WaitingForQuit(new(currentWorker.WorkerInstanceId, id))
            ? new(currentWorker.WorkerInstanceId, id) : null;
        if (current is { Fresh: true, WorkerSnapshot.LastRun: { } last }
            && _lifecycle.WaitingForQuit(new(current.WorkerSnapshot.WorkerInstanceId, last.RunId))) {
            return new(ScheduledRunDisposition.Busy, Reason: "开机任务已结束，正在退出");
        }
        if (replacing.HasValue && _manuallyStoppedRun == atRisk
            && current.WorkerSnapshot?.WorkerState != WorkerState.Faulted) {
            return new(ScheduledRunDisposition.Busy, Reason: "正在等待开机任务停止并退出");
        }
        Guid? submitted = null;
        var endingOldSession = false;
        SetPendingOperation(PendingOperation.PreparingEnvironment);
        try {
            project!.ActivateConfiguration(configurationId);
            RenderScheduledActivation(configurationId);
            AddScheduleActivity(policyTime is null ? $"启动「{name}」" : $"启动「{name}」（{policyTime}）");
            endingOldSession = true;
            if (replacing is { } boot) _lifecycle.Apply(new LifecycleEvent.DeferQuit(boot, true));
            var ended = await _environment.EndWhileExclusiveAsync();
            if (ended.Outcome != EnvironmentEndOutcome.Ended) {
                throw ended.Error ?? new InvalidOperationException("旧桌面分身未确认结束。");
            }
            if (replacing is { } endedBoot) _lifecycle.Apply(new LifecycleEvent.AbandonQuit(endedBoot));
            _lifecycle.Drain();
            endingOldSession = false;
            if (hadSession) {
                AddScheduleActivity("已关闭旧的桌面分身");
            }
            if (!ScheduledStartClosing(exitGeneration)) {
                await PrepareProjectEnvironmentAsync(project, showDesktop: false);
                if (!ScheduledStartClosing(exitGeneration) && await WaitForRunStartAsync()
                    && !ScheduledStartClosing(exitGeneration)) {
                    var worker = _workerCoordinator.Snapshot.WorkerSnapshot
                        ?? throw new InvalidOperationException("Worker 快照已失效。");
                    var key = new RunKey(worker.WorkerInstanceId, attempt!.RunId);
                    _scheduledSubmission = new(name, key);
                    submitted = attempt.RunId;
                    await SubmitRunAsync(CreateRunIntent(RunSource.Scheduled, configurationId, name), key, attempt);
                }
            }
            return new(ScheduledRunDisposition.Admitted, submitted);
        } catch (OperationCanceledException) when (ScheduledStartClosing(exitGeneration) && !submitted.HasValue) {
            return new(ScheduledRunDisposition.Admitted, Reason: "应用正在退出");
        } catch (Exception exception) {
            var detail = exception switch {
                TimeoutException => "等待运行环境响应超时",
                UnauthorizedAccessException => "没有访问运行环境的权限",
                _ when submitted.HasValue => "任务提交未成功",
                _ when exception.Message.Contains("Worker") => "运行组件未能就绪",
                _ => exception.Message.TrimEnd().TrimEnd('。')
            };
            var reason = endingOldSession ? "旧的桌面分身未能关闭" : $"桌面分身启动失败（{detail}）";
            ReportScheduledStartFailure(name, reason, exception);
            return new(ScheduledRunDisposition.Admitted, submitted, exception.Message);
        } finally {
            if (endingOldSession && replacing is { } original) _lifecycle.Apply(new LifecycleEvent.DeferQuit(original, false));
            SetPendingOperation(PendingOperation.None);
        }
    }

    // A single delivered request can wait for the same Worker to reconnect. RuntimeEnvironment
    // checks the live session and Run again after acquiring its gate.
    private void ResumeScheduledCleanup()
    {
        if (!OperationOrExitInProgress && !_environment.Closing
            && _scheduledSubmission is { CleanupRequested: true, CleanupQueued: false } own
            && _workerCoordinator.Snapshot is { Fresh: true, WorkerSnapshot: { } worker }
            && worker.WorkerInstanceId == own.Key.WorkerInstanceId) {
            _ = EndScheduledRunAsync(own, gateHeld: false);
        }
    }

    private async Task EndScheduledRunAsync(ScheduledSubmission own, bool gateHeld)
    {
        async Task EndInGateAsync()
        {
            // A tick can service the request while its queued consumer is waiting on this same gate.
            if (!own.CleanupRequested || _scheduledSubmission != own || OperationOrExitInProgress || _environment.Closing) return;
            var authorized = false;
            bool CanClose() => authorized = _environment.CanEndSuccessfulRun(own.Key.WorkerInstanceId, own.Key.RunId);
            EnvironmentEndResult ended;
            try {
                ended = await _environment.EndWhileExclusiveAsync(CanClose);
            } catch (Exception error) {
                ended = EnvironmentEndResult.Unconfirmed(error);
            }
            if (ended.Outcome == EnvironmentEndOutcome.Declined) return;
            own.CleanupRequested = false;
            if (_scheduledSubmission == own) _scheduledSubmission = null;
            if (ended.Error is { } failure) {
                own.CleanupFailure = failure;
                _logger.Error($"定时执行「{own.Name}」未能关闭桌面分身。", failure);
                const string reason = "任务已完成，桌面分身未能关闭";
                AddScheduleActivity(reason, LogLevel.Error);
                Notify(new($"「{own.Name}」收尾失败", reason, true));
            } else if (authorized) {
                AddScheduleActivity("全部完成，已关闭桌面分身");
            }
        }

        if (gateHeld) {
            await EndInGateAsync();
            return;
        }
        var exitGeneration = _exitGeneration;
        own.CleanupQueued = true;
        try {
            await _environment.RunExclusiveAsync(EndInGateAsync);
        } catch (OperationCanceledException) when (_environment.Closing || exitGeneration != _exitGeneration) {
            // ClosingCancelled resumes this request if the original environment still exists.
        } finally {
            own.CleanupQueued = false;
            if (exitGeneration != _exitGeneration) ResumeScheduledCleanup();
        }
    }
}
