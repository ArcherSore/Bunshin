using Gui.Settings;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using ProjectModel;
using Wpf.Ui.Controls;
using Alignment = System.Windows.HorizontalAlignment;

namespace Gui.Views;

internal enum UpdateMarker { None, Checking, Available, Downloading }

// The window's left column: wordmark, collapse button, navigation and configurations.
// It reports what the user chose; MainWindow decides what happens.
public partial class ShellSidebar : System.Windows.Controls.UserControl
{
    public const double ExpandedWidth = 288;
    public const double CompactWidth = 57;

    public static readonly DependencyProperty IsCollapsedProperty = DependencyProperty.Register(
        nameof(IsCollapsed), typeof(bool), typeof(ShellSidebar),
        new PropertyMetadata(false, (d, _) => ((ShellSidebar)d).ApplyCollapsed()));

    private UpdateMarker _updateMarker;
    private bool _followingSettingsScroll;

    public ShellSidebar()
    {
        InitializeComponent();
        ShowConfigurationName(null);
        ShowNextRun(null);
        ApplyCollapsed();
    }

    public bool IsCollapsed
    {
        get => (bool)GetValue(IsCollapsedProperty);
        set => SetValue(IsCollapsedProperty, value);
    }

    internal RailItem Home => IsCollapsed ? HomeNavigationItem : ConfigurationList.ActiveButton ?? HomeNavigationItem;
    internal void FocusToggle()
    {
        if (SettingsNavigation.IsVisible) {
            SettingsBackButton.Focus();
        } else {
            SidebarToggleButton.Focus();
        }
    }
    internal void ApplyConfigurationLock(bool editable, LockReason? reason)
    {
        NewConfigurationItem.IsEnabled = editable;
        ConfigurationList.ApplyLock(editable, reason);
    }

    internal event EventHandler? ToggleRequested;
    internal event EventHandler<MainSection>? SectionRequested;
    internal event EventHandler<RailItem>? NewConfigurationRequested;
    internal event EventHandler? UpdateRequested;
    internal event EventHandler? SettingsBackRequested;
    internal event EventHandler<int>? SettingsSectionRequested;

    private IEnumerable<RailItem> Items =>
        [NewConfigurationItem, ScheduleNavigationItem, HomeNavigationItem, UpdateNavigationItem,
         HelpNavigationItem, SettingsNavigationItem];

    internal void MarkActive(MainSection section)
    {
        HomeNavigationItem.IsActive = section == MainSection.Home;
        ConfigurationList.MarkHome(section == MainSection.Home);
        ScheduleNavigationItem.IsActive = section == MainSection.Schedule;
        HelpNavigationItem.IsActive = section == MainSection.Help;
        var settings = section == MainSection.Settings;
        SettingsNavigationItem.IsActive = settings;
        var workspace = settings ? Visibility.Collapsed : Visibility.Visible;
        SidebarHeader.Visibility = workspace;
        WorkspaceActions.Visibility = workspace;
        WorkspaceConfigurations.Visibility = workspace;
        WorkspaceUtilities.Visibility = workspace;
        SettingsNavigation.Visibility = settings ? Visibility.Visible : Visibility.Collapsed;
    }

    internal void ShowSettingsSections(IReadOnlyList<SettingsSectionModel> sections)
    {
        SectionNavigation.ItemsSource = sections;
        MarkSettingsSection(0);
    }

    internal void MarkSettingsSection(int index)
    {
        _followingSettingsScroll = true;
        SectionNavigation.SelectedIndex = index;
        _followingSettingsScroll = false;
    }

    internal void ShowConfigurations(ProjectPlanModule? project, SchedulePreview? schedule)
    {
        ConfigurationList.ShowConfigurations(project?.Configurations ?? [], project?.ActiveConfigurationId, schedule);
        RefreshConfigurationVisibility();
        ShowConfigurationName(project?.Configurations.FirstOrDefault(c => c.Id == project.ActiveConfigurationId)?.Name);
    }

    private void ShowConfigurationName(string? name)
    {
        HomeNavigationLabel.Text = string.IsNullOrWhiteSpace(name) ? "首页" : name;
        UpdateConfigurationTooltip();
    }

    internal void ShowRunning(bool running)
    {
        ConfigurationList.ShowRunning(running);
        HomeRunningIndicator.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        UpdateConfigurationTooltip();
    }

    private void UpdateConfigurationTooltip()
    {
        var label = HomeNavigationLabel.Text;
        if (HomeRunningIndicator.Visibility == Visibility.Visible) {
            label += "，运行中";
        }
        SetItemName(HomeNavigationItem, label);
        HomeNavigationItem.ToolTip = label;
    }

    internal void ShowNextRun(string? text)
    {
        ScheduleNextRunText.Text = text ?? string.Empty;
        var name = string.IsNullOrEmpty(text) ? "定时" : $"定时，下次运行 {text}";
        SetItemName(ScheduleNavigationItem, name);
        ScheduleNavigationItem.ToolTip = name;
    }

    internal void ShowUpdate(UpdateMarker marker)
    {
        _updateMarker = marker;
        UpdateNavigationBadge.Visibility = marker == UpdateMarker.Available ? Visibility.Visible : Visibility.Collapsed;
        UpdateNavigationLoading.Visibility = marker is UpdateMarker.Checking or UpdateMarker.Downloading
            ? Visibility.Visible : Visibility.Collapsed;
        var text = marker switch {
            UpdateMarker.Checking => "检查中",
            UpdateMarker.Available => "新版本",
            UpdateMarker.Downloading => "下载中",
            _ => string.Empty
        };
        UpdateNavigationText.Text = text;
        RefreshCompactUpdateMarker();
        var tip = marker switch {
            UpdateMarker.Checking => "正在检查更新…",
            UpdateMarker.Available => "发现新版本",
            UpdateMarker.Downloading => "正在下载更新…",
            _ => "软件更新"
        };
        UpdateNavigationItem.ToolTip = tip;
        SetItemName(UpdateNavigationItem, $"更新，{tip}");
    }

    private void ApplyCollapsed()
    {
        foreach (var item in Items) {
            item.IsCompact = IsCollapsed;
        }
        var action = IsCollapsed ? "展开侧栏 (Ctrl+B)" : "收起侧栏 (Ctrl+B)";
        SidebarToggleIcon.Symbol = IsCollapsed ? SymbolRegular.PanelLeftExpand24 : SymbolRegular.PanelLeftContract24;
        SidebarToggleButton.ToolTip = action;
        SidebarToggleButton.HorizontalAlignment = IsCollapsed ? Alignment.Center : Alignment.Right;
        SidebarToggleButton.Margin = IsCollapsed ? new Thickness() : new Thickness(0, 0, 4, 0);
        AutomationProperties.SetName(SidebarToggleButton, action);
        RefreshCompactUpdateMarker();
        RefreshConfigurationVisibility();
    }

    private void RefreshConfigurationVisibility()
    {
        var showList = !IsCollapsed && ConfigurationList.ActiveConfigurationId is not null;
        ConfigurationCaption.Visibility = showList ? Visibility.Visible : Visibility.Collapsed;
        ConfigurationList.Visibility = showList ? Visibility.Visible : Visibility.Collapsed;
        HomeNavigationItem.Visibility = showList ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RefreshCompactUpdateMarker() => UpdateNavigationCornerDot.Visibility =
        IsCollapsed && _updateMarker is (UpdateMarker.Available or UpdateMarker.Downloading)
            ? Visibility.Visible : Visibility.Collapsed;

    private static void SetItemName(RailItem item, string name) => AutomationProperties.SetName(item, name);

    private void Navigation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RailItem { Tag: string name } && Enum.TryParse(name, out MainSection section)) {
            SectionRequested?.Invoke(this, section);
        }
    }

    private void NewConfiguration_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        e.Handled = !ConfigurationList.CanEdit();

    private void NewConfiguration_Click(object sender, RoutedEventArgs e) =>
        NewConfigurationRequested?.Invoke(this, NewConfigurationItem);

    private void Update_Click(object sender, RoutedEventArgs e) => UpdateRequested?.Invoke(this, EventArgs.Empty);

    private void SettingsBack_Click(object sender, RoutedEventArgs e) =>
        SettingsBackRequested?.Invoke(this, EventArgs.Empty);

    private void SettingsSection_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_followingSettingsScroll && SectionNavigation.SelectedIndex >= 0) {
            SettingsSectionRequested?.Invoke(this, SectionNavigation.SelectedIndex);
        }
    }

    private void SelectedSettingsSection_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { IsSelected: true }) {
            SettingsSectionRequested?.Invoke(this, SectionNavigation.SelectedIndex);
        }
    }

    private void SidebarToggle_Click(object sender, RoutedEventArgs e) => ToggleRequested?.Invoke(this, EventArgs.Empty);

    // The blank part of the sidebar's top row moves the window like the system title bar: a double click toggles
    // maximize, a drag follows the system's snap behavior.
    private void SidebarHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Window.GetWindow(this) is not { } window || e.ButtonState != MouseButtonState.Pressed) {
            return;
        }
        if (e.ClickCount == 2) {
            if (window.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip) {
                window.WindowState = window.WindowState == WindowState.Maximized
                    ? WindowState.Normal : WindowState.Maximized;
            }
        } else {
            window.DragMove();
        }
    }
}
