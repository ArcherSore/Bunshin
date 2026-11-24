using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using Wpf.Ui.Controls;
using Alignment = System.Windows.HorizontalAlignment;

namespace Gui.Views;

internal enum UpdateMarker { None, Checking, Available, Downloading }

// The window's left column: wordmark, collapse button, navigation, and a host slot for the configuration list.
// It reports what the user chose; MainWindow decides what happens.
public partial class ShellSidebar : System.Windows.Controls.UserControl
{
    public const double ExpandedWidth = 288;
    public const double CompactWidth = 57;

    public static readonly DependencyProperty IsCollapsedProperty = DependencyProperty.Register(
        nameof(IsCollapsed), typeof(bool), typeof(ShellSidebar),
        new PropertyMetadata(false, (d, _) => ((ShellSidebar)d).ApplyCollapsed()));

    public static readonly DependencyProperty ConfigurationContentProperty = DependencyProperty.Register(
        nameof(ConfigurationContent), typeof(object), typeof(ShellSidebar));

    private UpdateMarker _updateMarker;

    public ShellSidebar()
    {
        InitializeComponent();
        ShowConfigurationName(null);
        ShowNextRun(null);
        ApplyCollapsed();
    }

    // Content shown under the "配置" caption, below the active configuration's entry.
    public object? ConfigurationContent
    {
        get => GetValue(ConfigurationContentProperty);
        set => SetValue(ConfigurationContentProperty, value);
    }

    public bool IsCollapsed
    {
        get => (bool)GetValue(IsCollapsedProperty);
        set => SetValue(IsCollapsedProperty, value);
    }

    internal RailItem Home => HomeNavigationItem;
    internal void FocusToggle() => SidebarToggleButton.Focus();
    internal void AllowNewConfiguration(bool allowed) => NewConfigurationItem.IsEnabled = allowed;

    internal event EventHandler? ToggleRequested;
    internal event EventHandler<MainSection>? SectionRequested;
    internal event EventHandler? NewConfigurationRequested;
    internal event EventHandler? UpdateRequested;

    private IEnumerable<RailItem> Items =>
        [NewConfigurationItem, ScheduleNavigationItem, HomeNavigationItem, UpdateNavigationItem,
         HelpNavigationItem, SettingsNavigationItem];

    internal void MarkActive(MainSection section)
    {
        HomeNavigationItem.IsActive = section == MainSection.Home;
        ScheduleNavigationItem.IsActive = section == MainSection.Schedule;
        HelpNavigationItem.IsActive = section == MainSection.Help;
        SettingsNavigationItem.IsActive = section == MainSection.Settings;
    }

    // The label of the active configuration's entry.
    internal void ShowConfigurationName(string? name)
    {
        HomeNavigationLabel.Text = string.IsNullOrWhiteSpace(name) ? "首页" : name;
        UpdateConfigurationTooltip();
    }

    internal void ShowRunning(bool running)
    {
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

    private void NewConfiguration_Click(object sender, RoutedEventArgs e) =>
        NewConfigurationRequested?.Invoke(this, EventArgs.Empty);

    private void Update_Click(object sender, RoutedEventArgs e) => UpdateRequested?.Invoke(this, EventArgs.Empty);

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
