using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Gui.Settings;
using ProjectModel;
using Button = System.Windows.Controls.Button;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Point = System.Windows.Point;

namespace Gui.Views;

public partial class RuntimePanel : System.Windows.Controls.UserControl
{
    private sealed record Section(Border Container, FrameworkElement Body, Button Toggle, Thumb Grip);
    private sealed record ScheduleRow(Guid ConfigurationId, Guid PolicyId, string Name, string Summary, bool Enabled);

    private readonly Dictionary<RuntimePanelSection, Section> _sections;
    private ApplicationSettings _settings = null!;
    private IReadOnlyList<RuntimePanelSectionState> _layout = [];
    private bool _revealDesktop;
    private RuntimePanelSection? _draggedSection;
    private int _dropIndex;
    private Guid? _configurationId;
    private IReadOnlyList<SchedulePolicy>? _policies;

    public RuntimePanel()
    {
        InitializeComponent();
        _sections = new() {
            [RuntimePanelSection.Desktop] = new(DesktopSection, RuntimeControlCard, DesktopSectionToggle, DesktopSectionGrip),
            [RuntimePanelSection.Schedule] = new(ScheduleSection, ScheduleSectionBody, ScheduleSectionToggle, ScheduleSectionGrip),
            [RuntimePanelSection.Activity] = new(ActivitySection, ActivitySectionBody, ActivitySectionToggle, ActivitySectionGrip)
        };
        foreach (var (id, section) in _sections) {
            section.Toggle.Click += (_, _) => ToggleSection(id);
            section.Grip.DragStarted += (_, _) => StartReordering(id);
            section.Grip.DragDelta += (_, e) => PreviewReorder(id, e.VerticalChange);
            section.Grip.DragCompleted += (_, e) => FinishReordering(id, e.Canceled);
            section.Grip.KeyDown += (_, e) => ReorderWithKeyboard(id, e);
            section.Grip.ToolTip = "拖动或按上下方向键调整区块顺序";
        }
    }

    internal event Action<Guid, Guid, bool>? SchedulePolicyToggleRequested;
    internal event EventHandler? RunProgressSizeChanged;

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
        ScheduleSectionSummary.Text = $"{policies.Count(policy => policy.Enabled)} 条启用";
    }

    private void SchedulePolicyToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Wpf.Ui.Controls.ToggleSwitch { DataContext: ScheduleRow policy } toggle) {
            SchedulePolicyToggleRequested?.Invoke(policy.ConfigurationId, policy.PolicyId, toggle.IsChecked == true);
            // Refresh supplies the saved value. If the write failed, restore the value on the still-bound row.
            if (ReferenceEquals(toggle.DataContext, policy)) {
                toggle.IsChecked = policy.Enabled;
            }
        }
    }

    private void RuntimeMoreButton_Click(object sender, RoutedEventArgs e)
    {
        RuntimeMoreMenu.PlacementTarget = RuntimeMoreButton;
        RuntimeMoreMenu.Placement = PlacementMode.Bottom;
        RuntimeMoreMenu.IsOpen = true;
    }

    private void RunProgressBar_SizeChanged(object sender, SizeChangedEventArgs e) =>
        RunProgressSizeChanged?.Invoke(this, EventArgs.Empty);

    private void ApplyLayout()
    {
        for (var index = 0; index < _layout.Count; index++) {
            var state = _layout[index];
            var section = _sections[state.Id];
            var collapsed = state.Collapsed && (state.Id != RuntimePanelSection.Desktop || !_revealDesktop);
            Grid.SetRow(section.Container, index);
            KeyboardNavigation.SetTabIndex(section.Container, index);
            section.Body.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            section.Toggle.Tag = collapsed;
            AutomationProperties.SetName(section.Toggle, $"{(collapsed ? "展开" : "收起")}{section.Toggle.Content}");
            Sections.RowDefinitions[index].Height = state.Id == RuntimePanelSection.Activity && !collapsed
                ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
            if (state.Id == RuntimePanelSection.Schedule) {
                ScheduleSectionSummary.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    private void ToggleSection(RuntimePanelSection id)
    {
        var layout = _layout.Select(state => state.Id == id ? state with { Collapsed = !state.Collapsed } : state)
            .ToArray();
        SaveLayout(layout);
    }

    private void SaveLayout(IReadOnlyList<RuntimePanelSectionState> layout)
    {
        if (_settings.TrySaveRuntimePanelSections(layout)) {
            _layout = layout;
            ApplyLayout();
        }
    }

    private int IndexOf(RuntimePanelSection id) => Grid.GetRow(_sections[id].Container);

    private void StartReordering(RuntimePanelSection id)
    {
        _draggedSection = id;
        _dropIndex = IndexOf(id);
    }

    private void PreviewReorder(RuntimePanelSection id, double offset)
    {
        if (_draggedSection != id) {
            return;
        }
        var others = _layout.Where(state => state.Id != id).ToArray();
        var targetY = HeaderCenter(id) + offset;
        _dropIndex = others.Count(state => HeaderCenter(state.Id) < targetY);
        DropMarker.Visibility = _dropIndex == IndexOf(id) ? Visibility.Collapsed : Visibility.Visible;
        if (_dropIndex == others.Length) {
            Grid.SetRow(DropMarker, IndexOf(others[^1].Id));
            DropMarker.VerticalAlignment = VerticalAlignment.Bottom;
        } else {
            Grid.SetRow(DropMarker, IndexOf(others[_dropIndex].Id));
            DropMarker.VerticalAlignment = VerticalAlignment.Top;
        }
    }

    private double HeaderCenter(RuntimePanelSection id)
    {
        var toggle = _sections[id].Toggle;
        return toggle.TranslatePoint(new Point(0, toggle.ActualHeight / 2), Sections).Y;
    }

    private void FinishReordering(RuntimePanelSection id, bool canceled)
    {
        if (_draggedSection != id) {
            return;
        }
        _draggedSection = null;
        DropMarker.Visibility = Visibility.Collapsed;
        if (!canceled) {
            MoveSection(id, _dropIndex);
        }
    }

    private void ReorderWithKeyboard(RuntimePanelSection id, KeyEventArgs e)
    {
        if (e.Key is Key.Up or Key.Down) {
            MoveSection(id, IndexOf(id) + (e.Key == Key.Up ? -1 : 1));
            e.Handled = true;
        }
    }

    private void MoveSection(RuntimePanelSection id, int target)
    {
        var origin = IndexOf(id);
        if (origin == target || target < 0 || target >= _layout.Count) {
            return;
        }
        var layout = _layout.ToList();
        var moved = layout[origin];
        layout.RemoveAt(origin);
        layout.Insert(target, moved);
        SaveLayout(layout);
    }
}
