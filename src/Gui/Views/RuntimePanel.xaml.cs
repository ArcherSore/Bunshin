using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Gui.Settings;
using ProjectModel;
using Button = System.Windows.Controls.Button;
using Point = System.Windows.Point;

namespace Gui.Views;

public partial class RuntimePanel : System.Windows.Controls.UserControl
{
    private sealed record Section(Border Container, FrameworkElement Body, Button Toggle);
    private sealed record ScheduleRow(Guid ConfigurationId, Guid PolicyId, string Name, string Summary, bool Enabled);

    private readonly Dictionary<RuntimePanelSection, Section> _sections;
    private ApplicationSettings _settings = null!;
    private IReadOnlyList<RuntimePanelSectionState> _layout = [];
    private bool _revealDesktop;
    private Guid? _configurationId;
    private IReadOnlyList<SchedulePolicy>? _policies;

    public RuntimePanel()
    {
        InitializeComponent();
        RuntimeMoreMenu.PlacementTarget = RuntimeMoreButton;
        RuntimeMoreMenu.Placement = PlacementMode.Custom;
        RuntimeMoreMenu.CustomPopupPlacementCallback = (popup, target, _) => [
            new(new Point(target.Width - popup.Width, target.Height + 4), PopupPrimaryAxis.Horizontal),
            new(new Point(target.Width - popup.Width, -popup.Height - 4), PopupPrimaryAxis.Horizontal)
        ];
        _sections = new() {
            [RuntimePanelSection.Desktop] = new(DesktopSection, RuntimeControlCard, DesktopSectionToggle),
            [RuntimePanelSection.RunResult] = new(RunResultSection, RunResultSectionBody, RunResultSectionToggle),
            [RuntimePanelSection.Schedule] = new(ScheduleSection, ScheduleSectionBody, ScheduleSectionToggle),
            [RuntimePanelSection.Activity] = new(ActivitySection, ActivitySectionBody, ActivitySectionToggle)
        };
        foreach (var (id, section) in _sections) {
            section.Toggle.Click += (_, _) => ToggleSection(id);
        }
        foreach (var element in new FrameworkElement[] { Sections, DesktopSection, ScheduleSection, RunResultSection }) {
            element.SizeChanged += (_, _) => FitRunList();
        }
        // What reappears can keep its old size and raise no SizeChanged; fit once layout has caught up.
        foreach (var element in new FrameworkElement[] { ActivitySectionBody, RunResultAllScroll }) {
            element.IsVisibleChanged += (_, _) => Dispatcher.InvokeAsync(FitRunList, DispatcherPriority.Loaded);
        }
    }

    internal event Action<Guid, Guid, bool>? SchedulePolicyToggleRequested;

    internal void Attach(ApplicationSettings settings)
    {
        _settings = settings;
        _layout = settings.ReadRuntimePanelSections();
        ApplyLayout();
    }

    // The tour can point to a control in a section the user normally keeps collapsed.
    internal void RevealDesktopSection(bool reveal)
    {
        if (_revealDesktop == reveal) {
            return;
        }
        _revealDesktop = reveal;
        DesktopViewButton.Tag = reveal ? "Onboarding" : null;
        if (_layout.Any(state => state.Id == RuntimePanelSection.Desktop && state.Collapsed)) {
            ApplyLayout();
        }
    }

    internal void ShowSchedule(TaskConfiguration? active, ScheduleOccurrence? next, DateTime now)
    {
        NextRunText.Text = next is { Winner: { } winner }
            ? $"{SchedulePreview.WhenText(next.Time, now)} · {winner.Name}" : "暂无定时";

        if (_configurationId == active?.Id && ReferenceEquals(_policies, active?.SchedulePolicies)) {
            return;
        }
        _configurationId = active?.Id;
        _policies = active?.SchedulePolicies;
        var policies = _policies ?? [];
        RuntimeSchedulePolicies.ItemsSource = policies.Select(policy =>
            new ScheduleRow(active!.Id, policy.Id, policy.Name, SchedulePreview.ShortSummary(policy), policy.Enabled))
            .ToArray();
        RuntimeScheduleEmpty.Visibility = policies.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateScheduleSectionSummary();
    }

    private void SchedulePolicyToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { DataContext: ScheduleRow policy } toggle) {
            SchedulePolicyToggleRequested?.Invoke(policy.ConfigurationId, policy.PolicyId, toggle.IsChecked == true);
            // Refresh supplies the saved value. If the write failed, restore the value on the still-bound row.
            if (ReferenceEquals(toggle.DataContext, policy)) {
                toggle.IsChecked = policy.Enabled;
            }
        }
    }

    private void RuntimeMoreButton_Click(object sender, RoutedEventArgs e) => RuntimeMoreMenu.IsOpen = true;

    private void ApplyLayout()
    {
        for (var index = 0; index < _layout.Count; index++) {
            var state = _layout[index];
            var section = _sections[state.Id];
            var collapsed = state.Collapsed && (state.Id != RuntimePanelSection.Desktop || !_revealDesktop);
            Grid.SetRow(section.Container, index);
            KeyboardNavigation.SetTabIndex(section.Container, index);
            section.Body.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            section.Toggle.Tag = collapsed ? "Collapsed" : "Expanded";
            NameToggle(section.Toggle);
            Sections.RowDefinitions[index].Height = state.Id == RuntimePanelSection.Activity && !collapsed
                ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        }
        UpdateScheduleSectionSummary();
    }

    private static void NameToggle(Button toggle) =>
        AutomationProperties.SetName(toggle, $"{(toggle.Tag is "Collapsed" ? "展开" : "收起")}{toggle.Content}");

    private void UpdateScheduleSectionSummary() => AutomationProperties.SetHelpText(ScheduleSectionToggle,
        ScheduleSectionToggle.Tag is "Collapsed" ? $"{_policies?.Count(policy => policy.Enabled) ?? 0} 条启用" : "");

    private void ToggleSection(RuntimePanelSection id)
    {
        var layout = _layout.Select(state => state.Id == id ? state with { Collapsed = !state.Collapsed } : state)
            .ToArray();
        if (_settings.TrySaveRuntimePanelSections(layout)) {
            _layout = layout;
            ApplyLayout();
        }
    }
}
