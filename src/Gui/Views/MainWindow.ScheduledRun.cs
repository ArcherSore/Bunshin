using Gui.Worker;
using ProjectModel;
using Protocol;

namespace Gui.Views;

internal enum ScheduledRunDisposition { Invalid, Busy, Failed, Admitted }

internal sealed record ScheduledRunResult(
    Guid ConfigurationId, ScheduledRunDisposition Disposition, Guid? RunId = null, string? Reason = null);

public partial class MainWindow
{
    private sealed record ScheduledSubmission(Guid ConfigurationId, string Name, Guid WorkerId, Guid RunId)
    {
        public bool AwaitingConfirmation { get; set; }
        public bool CompletionReported { get; set; }
    }

    private ScheduledSubmission? _scheduledSubmission;

    internal Guid? LastScheduledRunId { get; private set; }
    internal event EventHandler<Guid>? ScheduledConfigurationActivated;
    internal event EventHandler<(Guid ConfigurationId, RunSnapshot Run)>? ScheduledRunCompleted;

    // Called on the GUI dispatcher. Invalid leaves the opportunity to the next candidate.
    internal async Task<ScheduledRunResult> TryStartScheduledRunAsync(Guid configurationId)
    {
        var project = _projectPlan;
        var name = project?.Configurations.FirstOrDefault(c => c.Id == configurationId)?.Name
            ?? configurationId.ToString();
        ScheduledRunResult? result = null;
        if (!OperationOrExitInProgress) {
            await _environment.TryRunExclusiveAsync(async () =>
                result = await StartScheduledRunInGateAsync(project, configurationId, name));
        }
        result ??= new(configurationId, ScheduledRunDisposition.Busy, Reason: "程序正在退出或运行环境已被其他操作占用");
        if (result.Disposition == ScheduledRunDisposition.Busy) {
            _logger.Info($"定时执行“{name}”已跳过：{result.Reason}。");
        }
        await ObserveScheduledRunAsync();
        return result;
    }

    private async Task<ScheduledRunResult> StartScheduledRunInGateAsync(
        ProjectPlanModule? project, Guid configurationId, string name)
    {
        if (_environment.ScheduledRunBlocker(out var observed) is { } reason) {
            return new(configurationId, ScheduledRunDisposition.Busy, Reason: reason);
        }
        // The clock may run ahead of the dispatcher's snapshot callback. Consume the old Run before replacing it.
        await ObserveScheduledRunAsync(observed, gateHeld: true);
        if (_environment.Closing || _environment.EndUnconfirmed) {
            return new(configurationId, ScheduledRunDisposition.Busy, Reason: "运行环境正在退出或结束未确认");
        }
        if (project is null || project.ConfigurationsUnreadable) {
            const string detail = "无法读取任务配置。";
            ReportUnattendedFailure($"定时执行“{name}”失败", new InvalidOperationException(detail));
            return new(configurationId, ScheduledRunDisposition.Failed, Reason: detail);
        }
        var active = configurationId == project.ActiveConfigurationId;
        if (active) {
            if (SaveEditor() is { } failure) {
                return failure;
            }
            name = ActiveConfigurationName;
        }
        RunStartAttempt attempt;
        try {
            var drafts = _invalidInputDrafts.Where(pair => pair.Key.Item1 == configurationId)
                .ToDictionary(pair => (pair.Key.Item2, pair.Key.Item3), pair => pair.Value.Message);
            attempt = project.CreateRunStartAttempt(configurationId, inputErrors: drafts);
        } catch (Exception exception) {
            ReportUnattendedFailure($"定时执行“{name}”失败", exception);
            return new(configurationId, ScheduledRunDisposition.Invalid, Reason: exception.Message);
        }

        if (!active && SaveEditor() is { } saveFailure) {
            return saveFailure;
        }
        if (_environment.ScheduledRunBlocker(out _) is { } changed) {
            return new(configurationId, ScheduledRunDisposition.Busy, Reason: changed);
        }
        Guid? submitted = null;
        var sending = false;
        SetPendingOperation(PendingOperation.PreparingEnvironment);
        try {
            project.ActivateConfiguration(configurationId);
            _pendingStartAttempt = null;
            ScheduledConfigurationActivated?.Invoke(this, configurationId);
            var ended = await _environment.EndWhileExclusiveAsync();
            if (ended.Outcome != EnvironmentEndOutcome.Ended) {
                throw ended.Error ?? new InvalidOperationException("旧桌面分身未确认结束。");
            }
            if (!_exitInProgress && !_environment.Closing) {
                await PrepareProjectEnvironmentAsync(project, showDesktop: false);
                if (await WaitForRunStartAsync()) {
                    var worker = _workerCoordinator.Snapshot.WorkerSnapshot
                        ?? throw new InvalidOperationException("Worker 快照已失效。");
                    _scheduledSubmission = new(configurationId, name, worker.WorkerInstanceId, attempt.RunId);
                    LastScheduledRunId = submitted = attempt.RunId;
                    await SubmitRunAsync(attempt, name, startupOnly: false, onSending: () => sending = true);
                }
            }
            return new(configurationId, ScheduledRunDisposition.Admitted, submitted);
        } catch (Exception exception) {
            if (submitted.HasValue && _scheduledSubmission is { } own) {
                var status = _workerCoordinator.ObserveStart(own.WorkerId, own.RunId);
                if (status == StartObservation.Accepted) {
                    return new(configurationId, ScheduledRunDisposition.Admitted, submitted);
                }
                if (sending && status == StartObservation.Unknown && exception is not WorkerProtocolErrorException) {
                    own.AwaitingConfirmation = true;
                    var detail = $"开始请求未确认，等待 Worker 快照：{exception.Message}";
                    _logger.Warn($"定时执行“{name}”（Run {submitted}）：{detail}");
                    return new(configurationId, ScheduledRunDisposition.Admitted, submitted, detail);
                }
                _scheduledSubmission = null;
            }
            ReportUnattendedFailure($"定时执行“{name}”失败", exception);
            return new(configurationId, ScheduledRunDisposition.Admitted, submitted, exception.Message);
        } finally {
            SetPendingOperation(PendingOperation.None);
        }

        ScheduledRunResult? SaveEditor()
        {
            try {
                CommitFocusedConfigurationInput(unattended: true);
                return null;
            } catch (Exception error) {
                var operation = $"定时执行“{name}”时无法保存配置“{ActiveConfigurationName}”";
                ReportUnattendedFailure(operation, error);
                return new(configurationId, ScheduledRunDisposition.Failed, Reason: operation);
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
                    ReportUnattendedFailure($"定时执行“{own.Name}”失败",
                        new InvalidOperationException($"Worker 已确认未接纳 Run {own.RunId}。"));
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
            ScheduledRunCompleted?.Invoke(this, (own.ConfigurationId, run));
        }
        if (run.State == RunState.Failed) {
            var item = run.Items.FirstOrDefault(i => i.State == PlanItemState.Failed);
            var detail = run.Error?.Message ?? item?.Error?.Message ?? item?.Reason ?? item?.TaskLabel ?? "任务运行失败";
            ReportUnattendedFailure($"定时执行“{own.Name}”失败", new InvalidOperationException(detail));
        } else if (run.State == RunState.Succeeded) {
            try {
                bool CanClose() => _scheduledSubmission == own && !OperationOrExitInProgress
                    && _environment.CanEndSuccessfulRun(own.WorkerId, own.RunId);
                var ended = gateHeld ? await _environment.EndWhileExclusiveAsync(CanClose)
                    : await _environment.EndAsync(CanClose);
                if (ended.Outcome != EnvironmentEndOutcome.Declined && _scheduledSubmission == own) {
                    _scheduledSubmission = null;
                }
                if (ended.Error is { } error) {
                    throw error;
                }
            } catch (Exception exception) {
                if (_scheduledSubmission == own) {
                    _scheduledSubmission = null;
                }
                ReportUnattendedFailure($"定时执行“{own.Name}”收尾失败", exception);
            }
        }
    }
}
