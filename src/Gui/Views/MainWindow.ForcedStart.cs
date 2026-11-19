using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace Gui.Views;

// The countdown dialog of a forced scheduled start. It lives in the main window, so a window hidden to the tray
// shows it again, still counting, once the user opens the window from the notification.
public partial class MainWindow
{
    private readonly DispatcherTimer _forcedStartTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private ForcedScheduledStartCountdown? _forcedStartDialog;
    private TimeSpan _forcedStartTotal;
    private IInputElement? _forcedStartPreviousFocus;

    private void InitializeForcedStartDialog()
    {
        _forcedStartTimer.Tick += (_, _) => RefreshForcedStartDialog();
        ForcedScheduledStartPending += (_, countdown) => ShowForcedStartDialog(countdown);
        ForcedScheduledStartEnded += (_, _) => CloseForcedStartDialog();
    }

    private void ShowForcedStartDialog(ForcedScheduledStartCountdown countdown)
    {
        _forcedStartDialog = countdown;
        _forcedStartTotal = countdown.Deadline - _scheduleClock.GetLocalNow();
        _forcedStartPreviousFocus ??= Keyboard.FocusedElement;
        ForcedStartOverlay.Visibility = Visibility.Visible;
        EnsureModalWindowHook();
        RefreshForcedStartDialog();
        _forcedStartTimer.Start();
        ForcedStartCancelButton.Focus();
    }

    private void RefreshForcedStartDialog()
    {
        if (_forcedStartDialog is not { } countdown) {
            return;
        }
        var remaining = countdown.Deadline - _scheduleClock.GetLocalNow();
        var seconds = Math.Max(0, (int)Math.Ceiling(remaining.TotalSeconds));
        ForcedStartText.Text = $"{seconds} 秒后将停止当前任务，并启动「{countdown.ConfigurationName}」";
        ForcedStartProgress.Value = _forcedStartTotal > TimeSpan.Zero
            ? Math.Clamp(remaining / _forcedStartTotal, 0, 1) : 0;
    }

    private void CloseForcedStartDialog()
    {
        _forcedStartTimer.Stop();
        _forcedStartDialog = null;
        ForcedStartOverlay.Visibility = Visibility.Collapsed;
        RemoveModalWindowHook();
        if (_forcedStartPreviousFocus is not null) {
            Keyboard.Focus(_forcedStartPreviousFocus);
            _forcedStartPreviousFocus = null;
        }
        ReevaluateOnboarding();
    }

    private void ForcedStartCancel_Click(object sender, RoutedEventArgs e) => _forcedStartDialog?.Cancel();
}
