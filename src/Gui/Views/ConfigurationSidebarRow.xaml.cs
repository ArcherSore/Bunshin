using System.Windows;
using System.Windows.Automation;

namespace Gui.Views;

public partial class ConfigurationSidebarRow : System.Windows.Controls.UserControl
{
    public ConfigurationSidebarRow() => InitializeComponent();

    internal Guid Id { get; init; }
    internal string ConfigurationName => NameLabel.Text;

    internal void Show(string name, bool active, bool running, bool scheduled, string? schedule,
        bool editable, bool selectable, string? lockReason)
    {
        NameLabel.Text = name;
        SelectButton.Tag = Id;
        SelectButton.IsActive = active;
        SelectButton.IsEnabled = selectable;
        MenuButton.IsEnabled = editable;
        ScheduleIndicator.Tag = scheduled;
        RunningIndicator.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        var detail = running ? $"{name}，运行中" : name;
        if (scheduled) {
            detail += $"，{schedule ?? "已设定时"}";
        }
        if (lockReason is not null) {
            detail += $"，{lockReason}";
        }
        SelectButton.ToolTip = detail;
        AutomationProperties.SetName(SelectButton, detail);
        AutomationProperties.SetName(MenuButton, $"管理配置 {name}");
    }

    internal void ShowDragPlaceholder(bool show)
    {
        SelectButton.Visibility = show ? Visibility.Hidden : Visibility.Visible;
        MenuButton.IsHitTestVisible = !show;
        DragPlaceholder.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }
}
