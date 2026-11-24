using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Gui.Views;

internal enum MainSection { Home, Schedule, Help, Settings }

public partial class MainWindow
{
    private bool _sidebarOverlayMode;

    private void InitializeShell()
    {
        Sidebar.IsCollapsed = _settings.SidebarCollapsed.Value;
        Sidebar.SectionRequested += (_, section) => {
            DismissSidebarOverlay();
            SwitchSection(section);
        };
        Sidebar.NewConfigurationRequested += (_, _) => {
            DismissSidebarOverlay();
            SwitchSection(MainSection.Home);
            TaskPlanEditor.CreateEmptyConfiguration();
        };
        Sidebar.UpdateRequested += (_, _) => {
            DismissSidebarOverlay();
            OpenUpdateDialog();
        };
        Sidebar.ToggleRequested += (_, _) => ToggleSidebar();
        Header.OpenLogsRequested += (_, _) => TryOpenLogsDirectory();
        Header.ScheduleStatusRequested += (_, _) => SwitchSection(MainSection.Home);
        MainWindowContent.SizeChanged += (_, e) => UpdateSidebarMode(e.NewSize.Width);
        _scheduleTimer.Tick += (_, _) => RefreshSidebarSchedule();
        ScheduleView.PoliciesChanged += (_, _) => RefreshSidebarSchedule();
    }

    private void UpdateSidebarMode(double width)
    {
        var minimumContentWidth = HomeView.ColumnDefinitions[0].MinWidth + MinimumRuntimeSidebarWidth
            + RuntimeSidebar.Margin.Right;
        var overlay = width < minimumContentWidth + ShellSidebar.ExpandedWidth;
        if (overlay == _sidebarOverlayMode) {
            return;
        }
        _sidebarOverlayMode = overlay;
        SidebarColumn.Width = overlay ? new GridLength(ShellSidebar.CompactWidth) : GridLength.Auto;
        Grid.SetColumnSpan(Sidebar, overlay ? 2 : 1);
        ShowSidebar(overlay || _settings.SidebarCollapsed.Value);
    }

    private void ToggleSidebar()
    {
        var collapsed = !Sidebar.IsCollapsed;
        _settings.SidebarCollapsed.Value = collapsed;
        ShowSidebar(collapsed);
        if (_sidebarOverlayMode && !collapsed) {
            Sidebar.FocusToggle();
        }
    }

    private void ShowSidebar(bool collapsed)
    {
        Sidebar.IsCollapsed = collapsed;
        var overlayOpen = _sidebarOverlayMode && !collapsed;
        SidebarDismissLayer.Visibility = overlayOpen ? Visibility.Visible : Visibility.Collapsed;
        KeyboardNavigation.SetTabNavigation(Sidebar, overlayOpen ? KeyboardNavigationMode.Cycle : KeyboardNavigationMode.Continue);
    }

    private bool DismissSidebarOverlay()
    {
        if (!_sidebarOverlayMode || Sidebar.IsCollapsed) {
            return false;
        }
        ShowSidebar(collapsed: true);
        return true;
    }

    private void SidebarDismissLayer_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        DismissSidebarOverlay();
        Sidebar.FocusToggle();
        e.Handled = true;
    }

    private bool HandleSidebarShortcut(System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.B && Keyboard.Modifiers == ModifierKeys.Control) {
            if (!e.IsRepeat) {
                ToggleSidebar();
            }
            return true;
        }
        if (e.Key == Key.Escape && DismissSidebarOverlay()) {
            Sidebar.FocusToggle();
            return true;
        }
        return false;
    }

    private void RefreshShellTitles()
    {
        var configuration = _projectPlan is null ? null : ActiveConfigurationName;
        Sidebar.ShowConfigurationName(configuration);
        var title = _section switch {
            MainSection.Schedule => "定时",
            MainSection.Help => "帮助",
            MainSection.Settings => "设置",
            _ => string.IsNullOrWhiteSpace(configuration) ? "首页" : configuration
        };
        Header.ShowPage(title, _section == MainSection.Home, _section == MainSection.Schedule);
        RefreshSidebarSchedule();
    }

    private void RefreshSidebarSchedule()
    {
        if (_projectPlan is not { } project) {
            Sidebar.ShowNextRun(null);
            return;
        }
        var now = _scheduleClock.GetLocalNow().DateTime;
        var next = new SchedulePreview(project.Configurations, ScheduleInvalidReason, now).NextRun;
        Sidebar.ShowNextRun(next is null ? null : SchedulePreview.WhenText(next.Time, now));
    }
}
