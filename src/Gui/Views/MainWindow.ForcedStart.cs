using System.Windows;
using System.Windows.Threading;
using Gui.Worker;

namespace Gui.Views;

// The countdown keeps its original deadline even when it is temporarily obscured or the window is hidden.
public partial class MainWindow
{
    private readonly DispatcherTimer _forcedStartTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private ForcedScheduledStartCountdown? _forcedStartDialog;

    private void InitializeForcedStartDialog()
    {
        _forcedStartTimer.Tick += (_, _) => RefreshForcedStartDialog();
        ForcedScheduledStartPending += (_, countdown) => ShowForcedStartDialog(countdown);
        ForcedScheduledStartEnded += (_, _) => CloseForcedStartDialog();
        ForcedStartDialog.Dismissed += (_, _) => _forcedStartDialog?.Cancel();
    }

    private void ShowForcedStartDialog(ForcedScheduledStartCountdown countdown)
    {
        _forcedStartDialog = countdown;
        var lifecycle = _lifecycle.Orchestration.Lifecycle;
        var current = lifecycle.Current.WorkerSnapshot;
        var tracked = current?.ActiveRun is { } running
            ? lifecycle.Runs.GetValueOrDefault(new RunKey(current.WorkerInstanceId, running.RunId)) : null;
        ForcedStartDialog.ShowCountdown(tracked?.Owner?.ConfigurationName ?? tracked?.TakeoverConfigurationName,
            countdown.ConfigurationName);
        RememberWindowPromptFocus();
        ForcedStartOverlay.Visibility = Visibility.Visible;
        EnsureModalWindowHook();
        ReevaluateOnboarding();
        RefreshWindowPrompts();
        RefreshForcedStartDialog();
        _forcedStartTimer.Start();
        FocusAfterWindowPrompt();
    }

    private void RefreshForcedStartDialog()
    {
        if (_forcedStartDialog is not { } countdown) return;
        var remaining = (countdown.Deadline - _scheduleClock.GetLocalNow()).TotalSeconds;
        var seconds = Math.Max(0, (int)Math.Ceiling(remaining));
        ForcedStartDialog.UpdateCountdown(seconds, Math.Clamp(1 - remaining / ForcedStartDuration.TotalSeconds, 0, 1));
    }

    private void CloseForcedStartDialog()
    {
        _forcedStartTimer.Stop();
        _forcedStartDialog = null;
        ForcedStartOverlay.Visibility = Visibility.Collapsed;
        RemoveModalWindowHook();
        RefreshWindowPrompts();
        FocusAfterWindowPrompt();
    }
}
