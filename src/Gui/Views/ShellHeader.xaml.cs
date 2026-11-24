using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
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
    internal event EventHandler<WpfButton>? ConfigurationMenuRequested;
    internal Func<bool> CanEditConfiguration { get; set; } = () => false;

    internal void AllowConfigurationMenu(bool allowed) => ConfigurationMenuButton.IsEnabled = allowed;

    internal void ShowPage(string title, bool showHomeActions, bool showConfigurationMenu)
    {
        PageTitle.Text = title;
        PageTitle.Visibility = showConfigurationMenu ? Visibility.Collapsed : Visibility.Visible;
        ConfigurationTitle.Text = title;
        ConfigurationMenuButton.Visibility = showConfigurationMenu ? Visibility.Visible : Visibility.Collapsed;
        ConfigurationMenuButton.ToolTip = title;
        AutomationProperties.SetName(ConfigurationMenuButton, $"管理配置 {title}");
        AutomationProperties.SetName(TitleBar, title);
        OpenLogsButton.Visibility = showHomeActions ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ConfigurationMenu_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        e.Handled = !CanEditConfiguration();

    private void ConfigurationMenu_Click(object sender, RoutedEventArgs e) =>
        ConfigurationMenuRequested?.Invoke(this, ConfigurationMenuButton);

    private void OpenLogs_Click(object sender, RoutedEventArgs e) => OpenLogsRequested?.Invoke(this, EventArgs.Empty);
}
