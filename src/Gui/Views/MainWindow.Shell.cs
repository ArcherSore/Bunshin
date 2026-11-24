using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace Gui.Views;

internal enum MainSection { Home, Schedule, Help, Settings }

public partial class MainWindow
{
    private bool _sidebarOverlayMode;
    private MainSection _sectionBeforeSettings;

    private void InitializeShell()
    {
        Sidebar.IsCollapsed = _settings.SidebarCollapsed.Value;
        Sidebar.SectionRequested += (_, section) => {
            DismissSidebarOverlay();
            SwitchSection(section);
        };
        Sidebar.ShowSettingsSections(_settings.Page.Sections);
        Sidebar.SettingsBackRequested += (_, _) => {
            DismissSidebarOverlay();
            SwitchSection(_sectionBeforeSettings);
        };
        Sidebar.SettingsSectionRequested += (_, index) => {
            DismissSidebarOverlay();
            SettingsView.ScrollToSection(index);
        };
        SettingsView.CurrentSectionChanged += (_, index) => Sidebar.MarkSettingsSection(index);
        InitializeConfigurationList();
        Sidebar.UpdateRequested += (_, _) => {
            DismissSidebarOverlay();
            OpenUpdateDialog();
        };
        Sidebar.ToggleRequested += (_, _) => ToggleSidebar();
        Header.OpenLogsRequested += (_, _) => TryOpenLogsDirectory();
        Header.ScheduleStatusRequested += (_, _) => SwitchSection(MainSection.Home);
        RegisterName("RuntimePanelToggleButton", Header.Actions);
        _settings.RuntimePanelCollapsed.PropertyChanged += (_, e) => {
            if (e.PropertyName == nameof(_settings.RuntimePanelCollapsed.Value)) {
                UpdateRuntimePanelLayout();
            }
        };
        MainWindowContent.SizeChanged += (_, e) => UpdateSidebarMode(e.NewSize.Width);
        _scheduleTimer.Tick += (_, _) => RefreshSidebarSchedule();
        ScheduleView.PoliciesChanged += (_, _) => RefreshSidebarSchedule();
    }

    private void UpdateSidebarMode(double width)
    {
        var minimumContentWidth = HomeView.ColumnDefinitions[0].MinWidth
            + (RuntimePanelHidden ? 0 : MinimumRuntimeSidebarWidth);
        var overlay = width < minimumContentWidth + ShellSidebar.ExpandedWidth;
        if (overlay == _sidebarOverlayMode) {
            return;
        }
        _sidebarOverlayMode = overlay;
        SidebarColumn.Width = overlay ? new GridLength(ShellSidebar.CompactWidth) : GridLength.Auto;
        Grid.SetColumnSpan(Sidebar, overlay ? 2 : 1);
        ShowSidebar(overlay || _settings.SidebarCollapsed.Value);
    }

    private bool RuntimePanelCollapsed => _runtimeSidebarDrag?.Collapsed ?? _settings.RuntimePanelCollapsed.Value;
    private bool OnboardingNeedsRuntimePanel => _onboardingActive && _onboardingStep >= 2;
    private bool RuntimePanelHidden => RuntimePanelCollapsed && !OnboardingNeedsRuntimePanel;
    private System.Windows.Controls.Button RuntimePanelToggleButton => (System.Windows.Controls.Button)Header.Actions!;

    private void RuntimePanelToggleButton_Click(object sender, RoutedEventArgs e)
    {
        CancelRuntimeSidebarDrag();
        _settings.RuntimePanelCollapsed.Value = !RuntimePanelCollapsed;
    }

    private void ShowRuntimePanelToggle(bool home)
    {
        RuntimePanelToggleButton.Visibility = home ? Visibility.Visible : Visibility.Collapsed;
        var label = RuntimePanelCollapsed ? "展开运行面板" : "收起运行面板";
        RuntimePanelToggleButton.ToolTip = label;
        AutomationProperties.SetName(RuntimePanelToggleButton, label);
        RuntimePanelToggleButton.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty,
            RuntimePanelCollapsed ? "Brush.Transparent" : "Brush.Rail.Active");
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
        if (e.Key == Key.Escape && !Sidebar.ConfigurationList.IsRenaming && DismissSidebarOverlay()) {
            Sidebar.FocusToggle();
            return true;
        }
        return false;
    }

    private void RefreshShellTitles()
    {
        var configuration = _projectPlan is null ? null : ActiveConfigurationName;
        var title = _section switch {
            MainSection.Schedule => "定时",
            MainSection.Help => "帮助",
            MainSection.Settings => "设置",
            _ => string.IsNullOrWhiteSpace(configuration) ? "首页" : configuration
        };
        var home = _section == MainSection.Home;
        Header.ShowPage(title, home, home && configuration is not null,
            showScheduleStatus: _section == MainSection.Schedule,
            showPageHeading: _section is not (MainSection.Help or MainSection.Settings));
        RefreshSidebarSchedule();
    }

    private void RefreshSidebarSchedule()
    {
        if (_projectPlan is not { } project) {
            Sidebar.ShowConfigurations(null, null);
            Sidebar.ShowNextRun(null);
            RuntimeSidebar.ShowSchedule(null, null, _scheduleClock.GetLocalNow().DateTime);
            return;
        }
        var preview = CreateSidebarSchedulePreview(project);
        Sidebar.ShowConfigurations(project, preview);
        Sidebar.ShowNextRun(preview.NextRun is { } next ? SchedulePreview.WhenText(next.Time, preview.Now) : null);
        RuntimeSidebar.ShowSchedule(project.Configurations.FirstOrDefault(c => c.Id == project.ActiveConfigurationId),
            preview.NextRun, preview.Now);
    }

    private void RefreshOnboardingRuntimePanel()
    {
        RuntimeSidebar.RevealDesktopSection(OnboardingNeedsRuntimePanel);
        RuntimeSidebarVisibility(_section);
        UpdateSidebarMode(MainWindowContent.ActualWidth);
    }

    private SchedulePreview CreateSidebarSchedulePreview(ProjectModel.ProjectPlanModule project) =>
        new(project.Configurations, ScheduleInvalidReason, _scheduleClock.GetLocalNow().DateTime);
}
