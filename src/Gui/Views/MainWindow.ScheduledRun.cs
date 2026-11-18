using Gui.Worker;
using ProjectModel;
using Protocol;

namespace Gui.Views;

internal enum ScheduledRunDisposition { Invalid, Busy, Admitted }

internal sealed record ScheduledRunResult(
    Guid ConfigurationId, ScheduledRunDisposition Disposition, Guid? RunId = null, string? Reason = null);

public partial class MainWindow
{
    private sealed record ScheduledSubmission(Guid ConfigurationId, string Name, Guid WorkerId, Guid RunId);
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
        if (_environment.ScheduledRunBlocker() is { } reason) {
            return new(configurationId, ScheduledRunDisposition.Busy, Reason: reason);
        }
        RunStartAttempt attempt;
        try {
            if (project is null || project.ConfigurationsUnreadable) {
                throw new InvalidOperationException("无法读取任务配置。");
            }
            // Commit an active editor before resolving its saved values.
            if (configurationId == project.ActiveConfigurationId && !CommitFocusedConfigurationInput()
                || _invalidInputDrafts.Keys.Any(key => key.Item1 == configurationId)) {
                throw new InvalidOperationException("配置中有尚未通过校验的输入。");
            }
            attempt = project.CreateRunStartAttempt(configurationId);
        } catch (Exception exception) {
            ReportUnattendedFailure($"定时执行“{name}”失败", exception);
            return new(configurationId, ScheduledRunDisposition.Invalid, Reason: exception.Message);
        }

        Guid? submitted = null;
        SetPendingOperation(PendingOperation.PreparingEnvironment);
        try {
            project.ActivateConfiguration(configurationId);
            _pendingStartAttempt = null;
            ScheduledConfigurationActivated?.Invoke(this, configurationId);
            if (_environment.ScheduledRunBlocker() is { } changed) {
                throw new InvalidOperationException(changed);
            }
            var ended = await _environment.EndWhileExclusiveAsync();
            if (ended.Outcome != EnvironmentEndOutcome.Ended) {
                throw ended.Error ?? new InvalidOperationException("旧桌面分身未确认结束。");
            }
            if (!_exitInProgress && !_environment.Closing) {
                await _prepareEnvironment(project, _settings.CreateFrameworkOptions, false);
                if (await WaitForRunStartAsync()) {
                    var worker = _workerCoordinator.Snapshot.WorkerSnapshot
                        ?? throw new InvalidOperationException("Worker 快照已失效。");
                    _scheduledSubmission = new(configurationId, name, worker.WorkerInstanceId, attempt.RunId);
                    LastScheduledRunId = submitted = attempt.RunId;
                    await SubmitRunAsync(attempt, name, startupOnly: false);
                }
            }
            return new(configurationId, ScheduledRunDisposition.Admitted, submitted);
        } catch (Exception exception) when (submitted.HasValue && exception is not WorkerProtocolErrorException
            && (_workerCoordinator.StartAwaitingObservation
                || exception is OperationCanceledException or IOException)) {
            var detail = $"开始请求未确认，等待 Worker 快照：{exception.Message}";
            _logger.Warn($"定时执行“{name}”（Run {submitted}）：{detail}");
            return new(configurationId, ScheduledRunDisposition.Admitted, submitted, detail);
        } catch (Exception exception) {
            ReportUnattendedFailure($"定时执行“{name}”失败", exception);
            return new(configurationId, ScheduledRunDisposition.Admitted, submitted, exception.Message);
        } finally {
            SetPendingOperation(PendingOperation.None);
        }
    }

    // Snapshot delivery and the start reply may arrive in either order. Only this exact Run earns cleanup.
    private async Task ObserveScheduledRunAsync()
    {
        if (_scheduledSubmission is not { } own || OperationOrExitInProgress || _environment.Closing) {
            return;
        }
        var observed = _workerCoordinator.Snapshot;
        if (observed is not { Observation: WorkerObservation.Connected, SnapshotFresh: true,
                WorkerSnapshot: { LastRun: { } run } worker }
            || worker.WorkerInstanceId != own.WorkerId || run.RunId != own.RunId
            || run.State is not (RunState.Succeeded or RunState.Failed or RunState.Cancelled)) {
            return;
        }
        _scheduledSubmission = null;
        ScheduledRunCompleted?.Invoke(this, (own.ConfigurationId, run));
        if (run.State == RunState.Failed) {
            var item = run.Items.FirstOrDefault(i => i.State == PlanItemState.Failed);
            var detail = run.Error?.Message ?? item?.Error?.Message ?? item?.Reason ?? item?.TaskLabel ?? "任务运行失败";
            ReportUnattendedFailure($"定时执行“{own.Name}”失败", new InvalidOperationException(detail));
        } else if (run.State == RunState.Succeeded) {
            try {
                var ended = await _environment.EndAsync(() =>
                    !_environment.Closing && !OperationOrExitInProgress && !_workerCoordinator.StartAwaitingObservation
                    && _workerCoordinator.Snapshot is { Observation: WorkerObservation.Connected, SnapshotFresh: true,
                        WorkerSnapshot: { ActiveRun: null, RunState: RunState.Idle, LastRun: { } last } current }
                    && current.WorkerInstanceId == own.WorkerId && last.RunId == own.RunId
                    && last.State == RunState.Succeeded
                    && _sessionManager.QueryChildSessionId(requireEnumeration: true) == current.ChildSessionId);
                if (ended.Error is { } error) {
                    throw error;
                }
            } catch (Exception exception) {
                ReportUnattendedFailure($"定时执行“{own.Name}”收尾失败", exception);
            }
        }
    }
}
