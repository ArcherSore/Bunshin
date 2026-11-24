using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Shapes;
using WpfButton = System.Windows.Controls.Button;

namespace Gui.Views;

// The window's header: the current page's title on the left, a host slot for page actions, system buttons on the right.
public partial class ShellHeader : System.Windows.Controls.UserControl
{
    public static readonly DependencyProperty ActionsProperty = DependencyProperty.Register(
        nameof(Actions), typeof(object), typeof(ShellHeader));

    public ShellHeader() => InitializeComponent();

    // Buttons or other controls shown before the system buttons.
    public object? Actions
    {
        get => GetValue(ActionsProperty);
        set => SetValue(ActionsProperty, value);
    }

    internal event EventHandler? OpenLogsRequested;
    internal event EventHandler? ScheduleStatusRequested;
    internal event EventHandler<WpfButton>? ConfigurationMenuRequested;
    internal Func<bool> CanEditConfiguration { get; set; } = () => false;

    internal void AllowConfigurationMenu(bool allowed) => ConfigurationMenuButton.IsEnabled = allowed;

    internal void ShowPage(string title, bool showHomeActions, bool showConfigurationMenu,
        bool showScheduleStatus = false, bool showPageHeading = true)
    {
        PageTitle.Text = title;
        PageTitle.Visibility = showPageHeading && !showConfigurationMenu ? Visibility.Visible : Visibility.Collapsed;
        HeadingDivider.Visibility = showPageHeading ? Visibility.Visible : Visibility.Collapsed;
        ConfigurationTitle.Text = title;
        ConfigurationMenuButton.Visibility = showConfigurationMenu ? Visibility.Visible : Visibility.Collapsed;
        ConfigurationMenuButton.ToolTip = title;
        AutomationProperties.SetName(ConfigurationMenuButton, $"管理配置 {title}");
        AutomationProperties.SetName(TitleBar, title);
        OpenLogsButton.Visibility = showHomeActions ? Visibility.Visible : Visibility.Collapsed;
        ScheduleStatusLink.Visibility = showScheduleStatus ? Visibility.Visible : Visibility.Collapsed;
        PageActions.Visibility = showScheduleStatus ? Visibility.Collapsed : Visibility.Visible;
    }

    internal void SetScheduleStatus(string text, string? tone)
    {
        ScheduleStatusText.Text = text;
        ScheduleStatusDot.SetResourceReference(Shape.FillProperty, tone ?? "Brush.Transparent");
        ScheduleStatusDot.SetResourceReference(Shape.StrokeProperty, tone ?? "Brush.Text.Muted");
        AutomationProperties.SetName(ScheduleStatusLink, text);
    }

    private void ConfigurationMenu_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        e.Handled = !CanEditConfiguration();

    private void ConfigurationMenu_Click(object sender, RoutedEventArgs e) =>
        ConfigurationMenuRequested?.Invoke(this, ConfigurationMenuButton);

    private void OpenLogs_Click(object sender, RoutedEventArgs e) => OpenLogsRequested?.Invoke(this, EventArgs.Empty);
    private void ScheduleStatus_Click(object sender, RoutedEventArgs e) =>
        ScheduleStatusRequested?.Invoke(this, EventArgs.Empty);
}
