using Gui.Worker;
using Gui.Models;
using ProjectModel;
using Protocol;

namespace Gui.Views;

internal enum ScheduledRunDisposition { Invalid, Busy, Failed, Admitted }

internal sealed record ScheduledRunResult(
    ScheduledRunDisposition Disposition, Guid? RunId = null, string? Reason = null);

public partial class MainWindow
{
    private sealed record ScheduledSubmission(string Name, Guid WorkerId, Guid RunId)
    {
        public bool AwaitingConfirmation { get; set; }
        public bool CompletionReported { get; set; }
    }

    private ScheduledSubmission? _scheduledSubmission;

    private void AddScheduleActivity(string message, LogLevel level = LogLevel.Info)
    {
        AddActivityEntry(new(_scheduleClock.GetLocalNow(), level, message, TaskLabel: "定时"));
    }

    private void ReportScheduledStartFailure(string name, string reason, Exception? exception = null)
    {
        var message = $"「{name}」未能启动：{reason}";
        _logger.Error(message, exception);
        AddScheduleActivity(message, LogLevel.Error);
        NotificationRequested?.Invoke(this, new("定时任务未能启动", $"「{name}」：{reason}", true));
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
        RenderConfigurationChange(
            ConfigurationTabs.SelectedItem is not System.Windows.Controls.TabItem { Tag: Guid shown }
            || shown != configurationId);
        if (ScheduleView.IsVisible) {
            ScheduleView.Refresh();
        }
    }

    // Called on the GUI dispatcher. Invalid leaves the opportunity to the next candidate.
    internal async Task<ScheduledRunResult> TryStartScheduledRunAsync(Guid configurationId, string? policyTime = null)
    {
        var project = _projectPlan;
        var name = project?.Configurations.FirstOrDefault(c => c.Id == configurationId)?.Name
            ?? configurationId.ToString();
        ScheduledRunResult? result = null;
        if (!OperationOrExitInProgress) {
            await _environment.TryRunExclusiveAsync(async () =>
                result = await StartScheduledRunInGateAsync(configurationId, name, policyTime));
        }
        result ??= new(ScheduledRunDisposition.Busy, Reason: "正在运行其他任务");
        if (result.Disposition == ScheduledRunDisposition.Busy) {
            ReportScheduledSkip(name, result.Reason!);
        }
        await ObserveScheduledRunAsync();
        return result;
    }

    private async Task<ScheduledRunResult> StartScheduledRunInGateAsync(
        Guid configurationId, string name, string? policyTime)
    {
        if (_environment.ScheduledRunBlocker(out var observed, out _) is { } blocker) {
            return new(ScheduledRunDisposition.Busy, Reason: blocker);
        }
        // The clock may run ahead of the dispatcher's snapshot callback. Consume the old Run before replacing it.
        await ObserveScheduledRunAsync(observed, gateHeld: true);
        if (_environment.Closing || _environment.EndUnconfirmed) {
            return new(ScheduledRunDisposition.Busy, Reason: "运行环境状态未知");
        }
        if (SaveEditor() is { } failure) {
            return failure;
        }
        try {
            LoadProject();
        } catch (Exception exception) {
            var detail = ProjectEmptyStateDetailText.Text;
            ReportScheduledStartFailure(name, detail, exception);
            return new(ScheduledRunDisposition.Failed, Reason: detail);
        }
        var project = RequireProject();
        if (project.ConfigurationsUnreadable) {
            const string detail = "无法读取任务配置。";
            ReportScheduledStartFailure(name, detail);
            return new(ScheduledRunDisposition.Failed, Reason: detail);
        }
        name = project.Configurations.FirstOrDefault(item => item.Id == configurationId)?.Name ?? name;
        RunStartAttempt attempt;
        try {
            attempt = CreateConfigurationAttempt(project, configurationId);
        } catch (Exception exception) {
            var reason = exception.Message.TrimEnd().TrimEnd('。')
                .Replace("请先勾选要执行的任务或前置任务", "执行计划为空").Replace("Run Plan", "执行计划");
            ReportScheduledStartFailure(name, reason, exception);
            return new(ScheduledRunDisposition.Invalid, Reason: exception.Message);
        }

        if (_environment.ScheduledRunBlocker(out _, out var hadSession) is { } changed) {
            return new(ScheduledRunDisposition.Busy, Reason: changed);
        }
        Guid? submitted = null;
        var sending = false;
        var endingOldSession = false;
        SetPendingOperation(PendingOperation.PreparingEnvironment);
        try {
            project.ActivateConfiguration(configurationId);
            RenderScheduledActivation(configurationId);
            AddScheduleActivity(policyTime is null ? $"启动「{name}」" : $"启动「{name}」（{policyTime}）");
            endingOldSession = true;
            var ended = await _environment.EndWhileExclusiveAsync();
            if (ended.Outcome != EnvironmentEndOutcome.Ended) {
                throw ended.Error ?? new InvalidOperationException("旧桌面分身未确认结束。");
            }
            endingOldSession = false;
            if (hadSession) {
                AddScheduleActivity("已关闭旧的桌面分身");
            }
            if (!_exitInProgress && !_environment.Closing) {
                await PrepareCurrentEnvironmentAsync();
                if (await WaitForRunStartAsync()) {
                    var worker = _workerCoordinator.Snapshot.WorkerSnapshot
                        ?? throw new InvalidOperationException("Worker 快照已失效。");
                    _scheduledSubmission = new(name, worker.WorkerInstanceId, attempt.RunId);
                    submitted = attempt.RunId;
                    await SubmitRunAsync(attempt, name, onSending: () => sending = true);
                }
            }
            return new(ScheduledRunDisposition.Admitted, submitted);
        } catch (Exception exception) {
            if (submitted.HasValue && _scheduledSubmission is { } own) {
                var status = _workerCoordinator.ObserveStart(own.WorkerId, own.RunId);
                if (status == StartObservation.Accepted) {
                    return new(ScheduledRunDisposition.Admitted, submitted);
                }
                if (sending && status == StartObservation.Unknown && exception is not WorkerProtocolErrorException) {
                    own.AwaitingConfirmation = true;
                    var pending = $"开始请求未确认，等待 Worker 快照：{exception.Message}";
                    _logger.Warn($"定时执行“{name}”（Run {submitted}）：{pending}");
                    return new(ScheduledRunDisposition.Admitted, submitted, pending);
                }
                _scheduledSubmission = null;
            }
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
            SetPendingOperation(PendingOperation.None);
        }

        ScheduledRunResult? SaveEditor()
        {
            try {
                CommitFocusedConfigurationInput(deferDraftCheck: true, unattended: true);
                return null;
            } catch (Exception error) {
                var reason = $"无法保存配置「{ActiveConfigurationName}」";
                ReportScheduledStartFailure(name, reason, error);
                return new(ScheduledRunDisposition.Failed, Reason: reason);
            }
        }
    }

    // Snapshot delivery and the start reply may arrive in either order. Only this exact Run earns cleanup.
    private async Task ObserveScheduledRunAsync(WorkerCoordinatorSnapshot? observed = null, bool gateHeld = false)
    {
        if (_scheduledSubmission is not { } own || OperationOrExitInProgress || _environment.Closing) {
            return;
        }
        if (own.AwaitingConfirmation) {
            switch (_workerCoordinator.ObserveStart(own.WorkerId, own.RunId)) {
                case StartObservation.Unknown:
                    return;
                case StartObservation.NotAccepted:
                    _scheduledSubmission = null;
                    ReportScheduledStartFailure(own.Name, "桌面分身启动失败（任务未被接纳）");
                    return;
                case StartObservation.Accepted:
                    own.AwaitingConfirmation = false;
                    break;
            }
        }
        observed ??= _workerCoordinator.Snapshot;
        if (observed is not { Observation: WorkerObservation.Connected, SnapshotFresh: true,
                WorkerSnapshot: { LastRun: { } run } worker }
            || worker.WorkerInstanceId != own.WorkerId || run.RunId != own.RunId
            || run.State is not (RunState.Succeeded or RunState.Failed or RunState.Cancelled)) {
            return;
        }
        if (run.State != RunState.Succeeded) {
            _scheduledSubmission = null;
        }
        if (!own.CompletionReported) {
            own.CompletionReported = true;
            ScheduledRunCompleted?.Invoke(this, run);
        }
        ObserveRunNotification(observed);
        if (run.State == RunState.Succeeded) {
            try {
                bool CanClose() => _scheduledSubmission == own && !OperationOrExitInProgress
                    && _environment.CanEndSuccessfulRun(own.WorkerId, own.RunId);
                var ended = gateHeld ? await _environment.EndWhileExclusiveAsync(CanClose)
                    : await _environment.EndAsync(CanClose);
                if (ended.Outcome != EnvironmentEndOutcome.Declined && _scheduledSubmission == own) {
                    _scheduledSubmission = null;
                    if (ended.Outcome == EnvironmentEndOutcome.Ended) {
                        AddScheduleActivity("全部完成，已关闭桌面分身");
                    }
                }
                if (ended.Error is { } error) {
                    throw error;
                }
            } catch (Exception exception) {
                if (_scheduledSubmission == own) {
                    _scheduledSubmission = null;
                }
                _logger.Error($"定时执行「{own.Name}」未能关闭桌面分身。", exception);
                const string reason = "任务已完成，桌面分身未能关闭";
                AddScheduleActivity(reason, LogLevel.Error);
                NotificationRequested?.Invoke(this, new($"「{own.Name}」收尾失败", reason, true));
            }
        }
    }
}
