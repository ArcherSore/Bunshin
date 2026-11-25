using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using UserControl = System.Windows.Controls.UserControl;

namespace Gui.Views;

public partial class WindowDialog : UserControl
{
    private readonly Run _countdownSeconds = new() { FontWeight = FontWeights.Medium };
    internal event EventHandler? Accepted;
    internal event EventHandler? Dismissed;
    internal event EventHandler? LogsRequested;

    public WindowDialog() => InitializeComponent();

    internal void ShowPrompt(string title, string message, string? details, bool offerLogs, string? confirmation)
    {
        SetTitle(title);
        WarningIcon.Visibility = details is null ? Visibility.Collapsed : Visibility.Visible;
        InformationIcon.Visibility = details is null && confirmation is null ? Visibility.Visible : Visibility.Collapsed;
        MessageText.Text = message;
        DetailsToggle.Visibility = WarningIcon.Visibility;
        DetailsToggle.IsChecked = false;
        DetailsText.Text = details ?? "";
        LogsButton.Visibility = offerLogs ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = confirmation is null ? Visibility.Collapsed : Visibility.Visible;
        ActionButton.Content = confirmation ?? "好";
        ActionButton.Style = (Style)FindResource(confirmation is null ? "DialogPrimaryButtonStyle" : "DangerButton");
        ActionButton.Margin = new Thickness(confirmation is null ? 0 : 8, 0, 0, 0);
    }

    internal void ShowCountdown(string? currentConfiguration, string nextConfiguration)
    {
        SetTitle("强制定时启动");
        DetailsToggle.Visibility = Visibility.Collapsed;
        LogsButton.Visibility = Visibility.Collapsed;
        ActionButton.Visibility = Visibility.Collapsed;
        CountdownProgress.Visibility = Visibility.Visible;
        CountdownNote.Visibility = Visibility.Visible;
        System.Windows.Automation.AutomationProperties.SetName(CancelButton, "取消强制定时启动");
        _countdownSeconds.SetResourceReference(TextElement.ForegroundProperty, "Brush.Text.Primary");
        System.Windows.Documents.Typography.SetNumeralAlignment(_countdownSeconds, FontNumeralAlignment.Tabular);
        MessageText.Inlines.Clear();
        MessageText.Inlines.Add(new Run(currentConfiguration is { Length: > 0 }
            ? $"正在运行「{currentConfiguration}」。" : "任务正在运行。"));
        MessageText.Inlines.Add(_countdownSeconds);
        MessageText.Inlines.Add(new Run($"后停止它，开始定时的「{nextConfiguration}」。"));
    }

    internal void UpdateCountdown(int seconds, double progress)
    {
        _countdownSeconds.Text = $"{seconds} 秒";
        CountdownProgress.Value = progress;
    }

    internal void FocusDefault()
    {
        if (CancelButton.IsVisible) CancelButton.Focus();
        else ActionButton.Focus();
    }

    private void SetTitle(string title)
    {
        TitleText.Text = title;
        System.Windows.Automation.AutomationProperties.SetName(this, title);
    }

    private void Accept_Click(object sender, RoutedEventArgs e) => Accepted?.Invoke(this, EventArgs.Empty);
    private void Dismiss_Click(object sender, RoutedEventArgs e) => Dismissed?.Invoke(this, EventArgs.Empty);
    private void Logs_Click(object sender, RoutedEventArgs e) => LogsRequested?.Invoke(this, EventArgs.Empty);
}
