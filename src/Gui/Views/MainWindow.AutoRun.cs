using Gui.Infrastructure;

namespace Gui.Views;

public partial class MainWindow
{
    private readonly LaunchCommand _launchCommand;
    private readonly QuitAfterRunTracker? _quitAfterRun;

    // Raised when something the launch started on its own failed. The text is for a notification.
    internal event EventHandler<string>? AutoRunFailed;

    // Starts the configuration this launch asks for, if any: activates it, prepares the Runtime Environment and
    // starts the Run, as the Start button does. Returns whether it did.
    internal async Task<bool> AutoRunOnLaunchAsync()
    {
        // The list is a placeholder then, so nothing can be matched; say so instead of skipping silently. Asked for
        // means what Decide would look up: --instance only with --autostart, otherwise the chosen configuration.
        var project = _projectPlan;
        if (project is null || project.ConfigurationsUnreadable) {
            var requested = _launchCommand is { AutoStart: true, Instance: not null }
                || _settings.AutoRunConfigurationId is not null;
            if (requested && (_launchCommand.AutoStart || _settings.AutoRunOnManualLaunch.Value)) {
                ReportAutoRunFailure(project?.LoadWarning ?? ProjectEmptyStateDetailText.Text);
                return true;
            }
            return false;
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
        var direct = CurrentControls.Action is RuntimeAction.Start
            or RuntimeAction.Retry { Target: RetryTarget.Start };
        await RunFromPanelAsync(prepare: !direct, automaticConfiguration: id);
        return true;
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

    private void ReportAutoRunFailure(string message)
    {
        var reason = $"自动执行：{message}";
        _logger.Warn(reason);
        AutoRunFailed?.Invoke(this, reason);
    }

    private void ReportUnattendedFailure(string operation, Exception exception)
    {
        _logger.Error($"{operation}。", exception);
        AutoRunFailed?.Invoke(this, $"{operation}：{exception.GetBaseException().Message}");
    }
}
