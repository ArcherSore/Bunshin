using System.Windows;
using WpfMouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;

namespace Gui.Views;

public partial class MainWindow
{
    private bool SettingsOpen => SettingsOverlay.Visibility == Visibility.Visible;

    private void OpenSettings()
    {
        if (SettingsOpen || IsGlobalModalOpen || _exitInProgress || _onboardingActive) {
            return;
        }
        CancelSidebarDrag();
        CancelRuntimeSidebarDrag();
        SettingsOverlay.Visibility = Visibility.Visible;
        EnsureModalWindowHook();
        SettingsView.FocusDefault();
        RefreshChildSessionCredentialStatus();
        RefreshDebugFileUsage();
        ReevaluateOnboarding();
    }

    private void CloseSettings()
    {
        if (!SettingsOpen) {
            return;
        }
        SettingsOverlay.Visibility = Visibility.Collapsed;
        RemoveModalWindowHook();
        Sidebar.FocusSettings();
        ReevaluateOnboarding();
    }

    private void SettingsScrim_MouseLeftButtonDown(object sender, WpfMouseButtonEventArgs e)
    {
        e.Handled = true;
        CloseSettings();
    }
}
