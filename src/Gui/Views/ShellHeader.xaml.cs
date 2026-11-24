using System.Windows;
using System.Windows.Automation;

namespace Gui.Views;

// The window's header: the current page's title on the left, a host slot for page actions, system buttons on the right.
public partial class ShellHeader : System.Windows.Controls.UserControl
{
    public static readonly DependencyProperty ActionsProperty = DependencyProperty.Register(
        nameof(Actions), typeof(object), typeof(ShellHeader));

    public ShellHeader()
    {
        InitializeComponent();
        if (TitleBar.Header is System.Windows.Controls.TextBlock title) {
            title.MaxWidth = 400;
            title.TextTrimming = TextTrimming.CharacterEllipsis;
        }
    }

    // Buttons or other controls shown before the system buttons.
    public object? Actions
    {
        get => GetValue(ActionsProperty);
        set => SetValue(ActionsProperty, value);
    }

    internal event EventHandler? OpenLogsRequested;

    internal void ShowPage(string title, bool showHomeActions)
    {
        TitleBar.Title = title;
        AutomationProperties.SetName(TitleBar, title);
        OpenLogsButton.Visibility = showHomeActions ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e) => OpenLogsRequested?.Invoke(this, EventArgs.Empty);
}
