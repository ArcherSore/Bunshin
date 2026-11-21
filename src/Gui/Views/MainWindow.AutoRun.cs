using Gui.Infrastructure;
using ProjectModel;
using Gui.Runtime;
using Gui.Worker;
using Gui.Models;
using System.Windows;
using static Gui.Runtime.LifecycleEffect;

namespace Gui.Views;

public partial class MainWindow
{
    private readonly LaunchCommand _launchCommand;
    private readonly Action? _quitAfterRun;
    internal event EventHandler<RunTerminalNotification>? NotificationRequested;

    private bool Notify(RunTerminalNotification notice)
    {
        NotificationRequested?.Invoke(this, notice);
        return !_environment.Closing && !_exitInProgress && !(IsVisible && IsActive && WindowState != WindowState.Minimized)
            && _presentation.ShowBalloon?.Invoke(notice) == true;
    }

    private void RequestQuitAfterRun()
    {
        _logger.Info("--quit-after-run：任务已结束，正在退出程序。");
        _quitAfterRun?.Invoke();
    }

    private bool PresentRunAction(Payload item) => item switch {
        Present notice => PresentRunOutcome(notice),
        PresentCleanupFailure failure => Notify(new($"「{failure.Name}」收尾失败", failure.Detail, true)),
        _ => throw new ArgumentOutOfRangeException(nameof(item))
    };

    private bool PresentRunOutcome(Present notice)
    {
        if (notice.Kind is NoticeKind.Succeeded or NoticeKind.Failed or NoticeKind.Faulted) {
            return Notify(new(notice.Operation, notice.Detail, notice.Kind != NoticeKind.Succeeded));
        }

        var detail = notice.Kind is NoticeKind.StartUnknown or NoticeKind.EndUnconfirmed ? notice.Detail : notice.ConfirmedStartDetail;
        var error = notice.Error ?? new InvalidOperationException(detail ?? "任务未能启动。");
        if (notice.Intent?.Source == RunSource.Manual) {
            if (IsVisible && WindowState != WindowState.Minimized) {
                var message = detail != null && notice.ConfirmedStartDetail == null
                    ? $"{detail}\n\n{error.GetBaseException().Message}" : detail;
                PresentOperationError(notice.Operation, error, message);
                return true;
            }
            _logger.Error($"{notice.Operation}。", error);
            if (notice.ForegroundOnly) return false;
            return Notify(new(notice.Operation, detail ?? error.GetBaseException().Message, true, notice.Operation, error));
        } else if (notice.Intent is { Source: RunSource.Scheduled } scheduled) {
            if (notice.Kind == NoticeKind.NotStarted) {
                return ReportScheduledStartFailure(scheduled.ConfigurationName,
                    detail ?? (error is WorkerProtocolErrorException ? "桌面分身启动失败（任务提交未成功）" : "桌面分身启动失败（任务未被接纳）"), error);
            } else {
                AddScheduleActivity($"「{scheduled.ConfigurationName}」：{detail}", LogLevel.Error);
                return Notify(new($"「{scheduled.ConfigurationName}」运行未确认", notice.Detail, true, notice.Operation, error));
            }
        } else if (notice.Intent == null) {
            return Notify(new("任务未能确认结束", notice.Detail, true, notice.Operation, error));
        } else {
            return ReportUnattendedFailure(notice.Operation, error, detail);
        }
    }

    // Starts the configuration this launch asks for, if any: activates it, prepares the Runtime Environment and
    // starts the Run, as the Start button does. Returns whether it did.
    internal async Task<bool> AutoRunOnLaunchAsync()
    {
        if (_projectPlan is not { } project || SkipLaunchForPendingQuit()) {
            return false;
        }
        // The list is a placeholder then, so nothing can be matched; say so instead of skipping silently. Asked for
        // means what Decide would look up: --instance only with --autostart, otherwise the chosen configuration.
        var requested = _launchCommand is { AutoStart: true, Instance: not null }
            || _settings.AutoRunConfigurationId is not null;
        if (project.ConfigurationsUnreadable && requested
            && (_launchCommand.AutoStart || _settings.AutoRunOnManualLaunch.Value)) {
            ReportAutoRunFailure("无法读取任务配置文件，请检查后重试。");
            return true;
        }
        var canStart = AutoRunPlanner.RuntimeAllows(CurrentControls);
        var decision = AutoRunPlanner.Decide(
            _launchCommand, _settings.AutoRunConfigurationId, _settings.AutoRunOnManualLaunch.Value,
            AutoRunTarget.Of(project), canStart);
        if (decision.ConfigurationId is not { } id) {
            if (decision.SkipReason is { } reason) {
                _logger.Info($"自动执行已跳过：{reason}。");
            }
            return false;
        }
        var entered = await _environment.TryRunExclusiveAsync(() => StartLaunchConfigurationAsync(project, id));
        if (!entered) {
            _logger.Info($"自动执行“{ConfigurationName(id)}”已跳过：运行环境已被其他操作占用。");
        }
        ResumeScheduledCleanup();
        return entered;
    }

    private bool SkipLaunchForPendingQuit()
    {
        if (_lifecycle.ReadOrchestration().PendingQuitReason is not { } reason) return false;
        _logger.Info($"自动执行已跳过：{reason}。");
        return true;
    }

    private async Task StartLaunchConfigurationAsync(ProjectPlanModule project, Guid id)
    {
        if (SkipLaunchForPendingQuit()) return;
        // A refusal is reported by the change itself: the other configuration must never be run instead.
        if (!TaskPlanEditor.ChangeConfiguration(() => project.ActivateConfiguration(id),
                failure => ReportUnattendedFailure("自动执行", failure))) {
            return;
        }
        if (project.EnabledItems.Count == 0) {
            ReportAutoRunFailure("所选配置没有可执行的任务。");
            return;
        }
        if (!_projectConfigurationValid) {
            ReportAutoRunFailure("所选配置未通过校验，请打开程序检查参数。");
            return;
        }
        _logger.Info($"自动执行：已激活配置并开始任务{AppLogger.Field("configuration", ConfigurationName(id))}");
        if (CurrentControls.Action is RuntimeAction.Start or RuntimeAction.Retry { Target: RetryTarget.Start }) {
            await RunWindowOperationAsync(PendingOperation.StartingRun,
                () => StartCurrentPlanAsync(source: RunSource.Launch), unattended: true);
        } else {
            await RunWindowOperationAsync(PendingOperation.PreparingEnvironment,
                () => PrepareCurrentEnvironmentAsync(startRun: true, source: RunSource.Launch), unattended: true);
        }
    }

    // Loaded starts everything and needs a shown window, so the window is shown and hidden before it is ever drawn,
    // without activation or a taskbar button; a later show from the tray is a normal one.
    internal void StartInTray()
    {
        ShowInTaskbar = false;
        ShowActivated = false;
        Show();
        Hide();
        ShowInTaskbar = true;
        ShowActivated = true;
    }

    private void ReportAutoRunFailure(string message) =>
        ReportUnattendedFailure("自动执行", new InvalidOperationException(message));

    private bool ReportUnattendedFailure(string operation, Exception exception, string? detail = null)
    {
        _logger.Error($"{operation}。", exception);
        var message = $"{operation}：{detail ?? exception.GetBaseException().Message}";
        return Notify(new("自动执行未能完成", message, true, operation, exception));
    }
}
