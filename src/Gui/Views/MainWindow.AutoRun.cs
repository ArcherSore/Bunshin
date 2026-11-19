using Gui.Infrastructure;
using ProjectModel;
using Gui.Worker;

namespace Gui.Views;

public partial class MainWindow
{
    private readonly LaunchCommand _launchCommand;
    private readonly QuitAfterRunTracker? _quitAfterRun;

    private readonly RunTerminalNotificationTracker _terminalNotifications = new();
    internal event EventHandler<RunTerminalNotification>? NotificationRequested;

    internal void ObserveApplicationRun(WorkerCoordinatorSnapshot snapshot, Action exitAfterRun)
    {
        Dispatcher.BeginInvoke(() => {
            if (_quitAfterRun?.Observe(snapshot) == true) {
                _logger.Info("--quit-after-run：任务已结束，正在退出程序。");
                exitAfterRun();
            } else {
                ObserveRunNotification(snapshot);
            }
        });
    }

    internal void ObserveRunNotification(WorkerCoordinatorSnapshot snapshot)
    {
        if (_terminalNotifications.Observe(snapshot, ActiveConfigurationName) is { } notification) {
            NotificationRequested?.Invoke(this, notification);
        }
    }

    // Starts the configuration this launch asks for, if any: activates it, prepares the Runtime Environment and
    // starts the Run, as the Start button does. Returns whether it did.
    internal async Task<bool> AutoRunOnLaunchAsync()
    {
        if (_projectPlan is not { } project) {
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
        await ObserveScheduledRunAsync();
        return entered;
    }

    private async Task StartLaunchConfigurationAsync(ProjectPlanModule project, Guid id)
    {
        // A refusal is reported by the change itself: the other configuration must never be run instead.
        if (!ChangeConfiguration(() => project.ActivateConfiguration(id), unattended: true)) {
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
                () => StartCurrentPlanAsync(quitAfterRun: _quitAfterRun), unattended: true);
        } else {
            await RunWindowOperationAsync(PendingOperation.PreparingEnvironment,
                () => PrepareCurrentEnvironmentAsync(startRun: true, quitAfterRun: _quitAfterRun), unattended: true);
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

    private void ReportUnattendedFailure(string operation, Exception exception)
    {
        _logger.Error($"{operation}。", exception);
        var message = $"{operation}：{exception.GetBaseException().Message}";
        NotificationRequested?.Invoke(this, new("自动执行未能完成", message, true));
    }
}
