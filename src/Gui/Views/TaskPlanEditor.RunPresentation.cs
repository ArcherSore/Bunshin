using System.Windows;
using System.Windows.Automation;

namespace Gui.Views;

public sealed partial class TaskPlanEditor
{
    internal void ApplyRunPresentation(RuntimeControlState state, RunProgressView? progress, string? startBlocker)
    {
        var stopping = progress is { Active: true }
            || state.Action is RuntimeAction.Stop or RuntimeAction.InProgress { Kind: ProgressKind.StoppingRun };
        var projectLoaded = Project is not null;
        PlanActions.Visibility = projectLoaded || stopping ? Visibility.Visible : Visibility.Collapsed;
        UpdateAddTaskButton();
        StartPlanButton.Visibility = stopping ? Visibility.Collapsed : Visibility.Visible;
        StopPlanButton.Visibility = stopping ? Visibility.Visible : Visibility.Collapsed;
        StartPlanButton.IsEnabled = state.Action is RuntimeAction.Start { Enabled: true }
            or RuntimeAction.Retry { Enabled: true }
            || state.Action is RuntimeAction.Prepare { Enabled: true }
                && Project?.EnabledItems.Count > 0 && startBlocker is null;
        StopPlanButton.IsEnabled = state.Action is RuntimeAction.Stop { Enabled: true };
        StartPlanButton.ToolTip = startBlocker;
        AutomationProperties.SetHelpText(StartPlanButton, startBlocker ?? string.Empty);
    }
}
