using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ProjectModel;
using SymbolIcon = Wpf.Ui.Controls.SymbolIcon;
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;
using WpfButton = System.Windows.Controls.Button;
using WpfControl = System.Windows.Controls.Control;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfTextBlock = System.Windows.Controls.TextBlock;

namespace Gui.Views;

// One choice of a scope menu; Days and Remove drive the timeline preview while the choice is pointed at.
internal sealed record ScheduleScopeOption(string Header, string? Note, IReadOnlyCollection<DayOfWeek> Days,
    bool Remove, Action Apply);

// The schedule page: every configuration's weekly policies on one timeline, one configuration edited at a time.
// Policies are saved through ProjectPlanModule as soon as they change; this view keeps only what is selected.
public sealed partial class ScheduleView : System.Windows.Controls.UserControl
{
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly Dictionary<Guid, Guid> _activePolicy = [];
    private readonly Dictionary<Guid, PolicyRow> _policyRows = [];
    private bool _inspectorCollapsed;
    private Func<ProjectPlanModule?> _project = () => null;
    private Func<Guid, string?> _invalidReason = _ => null;
    private Action<string, Exception> _editFailed = (_, _) => { };
    private TimeProvider _clock = TimeProvider.System;
    private Guid? _focus;
    private (Guid PolicyId, TimeOnly Time)? _selected;
    private SchedulePreview? _preview;
    private ContextMenu? _scopeMenu;
    private string? _shareHintPath;
    private bool _shareHintClosed;
    // One editor moves into whichever chip is selected, so a refresh while typing keeps the caret.
    private readonly System.Windows.Controls.TextBox _timeEditor;

    public ScheduleView()
    {
        InitializeComponent();
        _timeEditor = new() { Style = (Style)FindResource("Schedule.TimeEditor") };
        AutomationProperties.SetName(_timeEditor, "精确时间");
        InputMethod.SetIsInputMethodEnabled(_timeEditor, false);
        _timeEditor.PreviewTextInput += TimeEditor_PreviewTextInput;
        _timeEditor.PreviewKeyDown += TimeEditor_PreviewKeyDown;
        _timeEditor.LostKeyboardFocus += (_, _) => CommitSelectedTime();
        // Pasting or cutting could drop the colon; typing digits covers every edit.
        CommandManager.AddPreviewExecutedHandler(_timeEditor, (_, e) => e.Handled =
            e.Command == ApplicationCommands.Paste || e.Command == ApplicationCommands.Cut);
        _tick.Tick += (_, _) => Refresh();
        IsVisibleChanged += (_, _) => {
            if (IsVisible) {
                Refresh();
                _tick.Start();
            } else {
                _tick.Stop();
            }
        };
        Timeline.EmptyClicked += Timeline_EmptyClicked;
        Timeline.DayClicked += Timeline_DayClicked;
        Timeline.PinClicked += Timeline_PinClicked;
        Timeline.PinMoved += Timeline_PinMoved;
        Timeline.PinDoubleClicked += Timeline_PinDoubleClicked;
    }

    internal event EventHandler? PoliciesChanged;

    // The shared-times hint is closed once for every configuration; the file under the config folder remembers it.
    internal void Attach(Func<ProjectPlanModule?> project, Func<Guid, string?> invalidReason,
        Action<string, Exception> editFailed, TimeProvider clock, string shareHintPath)
    {
        (_project, _invalidReason, _editFailed, _clock) = (project, invalidReason, editFailed, clock);
        _shareHintPath = shareHintPath;
        _shareHintClosed = File.Exists(shareHintPath);
    }

    private DateTime Now => _clock.GetLocalNow().DateTime;
    private ProjectPlanModule? Project => _project();
    private TaskConfiguration? Focused => Project?.Configurations.FirstOrDefault(c => c.Id == _focus);

    private SchedulePolicy? ActivePolicy(TaskConfiguration configuration)
    {
        var policy = configuration.SchedulePolicies.FirstOrDefault(p =>
            _activePolicy.TryGetValue(configuration.Id, out var id) && p.Id == id)
            ?? configuration.SchedulePolicies.FirstOrDefault();
        if (policy is not null) {
            _activePolicy[configuration.Id] = policy.Id;
        }
        return policy;
    }

    internal void ShowConfiguration(Guid configurationId)
    {
        _focus = configurationId;
        _selected = null;
        _inspectorCollapsed = false;
        Refresh();
    }

    internal void Refresh()
    {
        if (Project is not { } project || project.Configurations.Count == 0) {
            return;
        }
        var configurations = project.Configurations;
        var focus = Focused ?? configurations.FirstOrDefault(c => c.Id == project.ActiveConfigurationId)
            ?? configurations[0];
        _focus = focus.Id;
        var policy = ActivePolicy(focus);
        if (_selected is { } selected && (policy?.Id != selected.PolicyId || !policy.Times.Contains(selected.Time))) {
            _selected = null;
        }
        _preview = new SchedulePreview(configurations, _invalidReason, Now);
        RenderBanner(_preview);
        RenderTimeline(configurations, focus, policy);
        var expanded = _inspectorCollapsed ? null : policy;
        RenderPolicyRows(focus, expanded);
        RenderInspector(focus, expanded);
    }

    private void RenderBanner(SchedulePreview preview)
    {
        if (preview.NextRun is { Winner: { } winner } next) {
            var policies = next.Entries.First(e => e.Configuration == winner).Policies.Select(p => p.Name);
            var skipped = next.Entries.Where(e => e.Configuration != winner).Select(e => e.Configuration.Name)
                .ToArray();
            NextRunDetail.Text =
                $"{SchedulePreview.WhenText(next.Time, preview.Now)} · {winner.Name} · {string.Join("、", policies)}";
            NextRunDetail.ToolTip = SchedulePreview.InText(next.Time, preview.Now)
                + (skipped.Length == 0 ? string.Empty : $" · 跳过 {string.Join("、", skipped)}");
        } else {
            NextRunDetail.Text = "没有启用的定时";
            NextRunDetail.ToolTip = null;
        }
    }

    private void ConfigurationPicker_Click(object sender, RoutedEventArgs e)
    {
        if (Project is not { } project || _preview is not { } preview) {
            return;
        }
        var menu = ConfigurationPicker.ContextMenu;
        menu.Items.Clear();
        menu.PlacementTarget = ConfigurationPicker;
        foreach (var configuration in project.Configurations) {
            var (outcome, text) = preview.ListState(configuration);
            var item = new MenuItem {
                Header = configuration.Name, Tag = text, Style = (Style)FindResource("Schedule.ScopeItem"),
                FontWeight = configuration.Id == _focus ? FontWeights.SemiBold : FontWeights.Normal
            };
            if (outcome is ScheduleOutcome.Skip or ScheduleOutcome.Invalid) {
                item.SetResourceReference(WpfControl.ForegroundProperty,
                    outcome == ScheduleOutcome.Skip ? "Brush.Warning.Foreground" : "Brush.Error.Foreground");
            }
            item.Click += (_, _) => ShowConfiguration(configuration.Id);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void NewPolicy_Click(object sender, RoutedEventArgs e)
    {
        if (Focused is { } focus) {
            AddPolicy(focus, "定时 " + (focus.SchedulePolicies.Count + 1));
        }
    }

    private void CollapsePolicy_Click(object sender, RoutedEventArgs e)
    {
        _inspectorCollapsed = true;
        _selected = null;
        Refresh();
        if (EditedPolicy is { } policy) {
            _policyRows[policy.Id].Expand.Focus();
        }
    }

    private void RenderTimeline(IReadOnlyList<TaskConfiguration> configurations, TaskConfiguration focus,
        SchedulePolicy? active)
    {
        var lane = configurations.Count <= 1 ? 0 : Math.Min(7, 28.0 / (configurations.Count - 1));
        var pins = new List<TimelinePin>();
        for (var index = 0; index < configurations.Count; index++) {
            var configuration = configurations[index];
            var offset = (index - (configurations.Count - 1) / 2.0) * lane;
            foreach (var policy in configuration.SchedulePolicies) {
                var role = configuration != focus ? TimelinePinRole.Other
                    : policy == active ? TimelinePinRole.Active : TimelinePinRole.Sibling;
                foreach (var day in policy.Days) {
                    foreach (var time in policy.Times) {
                        var winner = policy.Enabled ? configurations.Take(index).FirstOrDefault(other =>
                            _preview!.InvalidReason(other) is null
                            && other.SchedulePolicies.Any(p => p.FiresAt(day, time))) : null;
                        var tip = (role == TimelinePinRole.Other ? $"{configuration.Name} · " : string.Empty)
                            + $"{policy.Name} {SchedulePreview.TimeText(time)}"
                            + (policy.Enabled ? "" : "（已停用）")
                            + (winner is null ? "" : $"\n与「{winner.Name}」同时到点，将跳过")
                            + (role == TimelinePinRole.Active ? "\n拖动调整 · 双击删除" : "");
                        pins.Add(new(configuration.Id, policy.Id, day, time, role, policy.Enabled,
                            winner is not null, offset, tip));
                    }
                }
            }
        }
        Timeline.ActiveDays = (active?.Days ?? []).ToHashSet();
        Timeline.Selected = _selected;
        Timeline.Clock = Now;
        // Keep coincident times separately hittable while aligning all other marks to the row center.
        Timeline.Pins = pins.GroupBy(pin => (pin.Day, pin.Time)).SelectMany(group =>
            group.Select(pin => group.Count() == 1 ? pin with { Offset = 0 } : pin)).ToArray();
    }

    private sealed record PolicyRow(Grid Container, WpfButton Expand, ToggleButton Enabled, WpfTextBlock Name,
        WpfTextBlock Summary, DockPanel Warning, WpfTextBlock Notice);

    private void RenderPolicyRows(TaskConfiguration focus, SchedulePolicy? expanded)
    {
        ConfigurationName.Text = focus.Name;
        AutomationProperties.SetName(ConfigurationPicker, $"选择配置：{focus.Name}");
        PolicyCount.Text = $"{focus.SchedulePolicies.Count} 条定时 · {focus.SchedulePolicies.Count(p => p.Enabled)} 条启用";

        var ids = focus.SchedulePolicies.Select(policy => policy.Id);
        if (!_policyRows.Keys.SequenceEqual(ids)) {
            PolicyRows.Children.Clear();
            _policyRows.Clear();
            foreach (var policy in focus.SchedulePolicies) {
                var row = CreatePolicyRow(policy.Id);
                _policyRows.Add(policy.Id, row);
                PolicyRows.Children.Add(row.Container);
            }
        }
        if (expanded is not null) {
            var position = PolicyRows.Children.IndexOf(_policyRows[expanded.Id].Container) + 1;
            var current = PolicyRows.Children.IndexOf(Inspector);
            if (current != position) {
                if (current >= 0) {
                    PolicyRows.Children.RemoveAt(current);
                    if (current < position) {
                        position--;
                    }
                }
                PolicyRows.Children.Insert(position, Inspector);
            }
        }
        foreach (var policy in focus.SchedulePolicies) {
            var row = _policyRows[policy.Id];
            row.Container.Visibility = policy == expanded ? Visibility.Collapsed : Visibility.Visible;
            row.Enabled.IsChecked = policy.Enabled;
            row.Name.Text = policy.Name;
            row.Summary.Text = SchedulePreview.ShortSummary(policy);
            row.Name.SetResourceReference(WpfTextBlock.ForegroundProperty,
                policy.Enabled ? "Brush.Text.Primary" : "Brush.Text.Disabled");
            row.Summary.SetResourceReference(WpfTextBlock.ForegroundProperty,
                policy.Enabled ? "Brush.Text.Muted" : "Brush.Text.Disabled");
            var issues = _preview!.Issues(focus, policy);
            var issue = issues.FirstOrDefault();
            row.Warning.Visibility = issues.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            row.Notice.Text = (issue.Error ? null : _preview.ConflictSummary(focus, policy)) ?? issue.Text;
            row.Warning.SetResourceReference(TextElement.ForegroundProperty,
                issue.Error ? "Brush.Error.Foreground" : "Brush.Warning.Foreground");
            row.Notice.SetResourceReference(WpfTextBlock.ForegroundProperty,
                issue.Error ? "Brush.Error.Foreground" : "Brush.Warning.Foreground");
            row.Expand.ToolTip = issues.Count == 0 ? null : string.Join("\n", issues.Select(item => item.Text));
            AutomationProperties.SetName(row.Expand, $"{policy.Name}，{SchedulePreview.Summary(policy)}，展开编辑");
            AutomationProperties.SetName(row.Enabled, $"启用「{policy.Name}」");
        }
    }

    private PolicyRow CreatePolicyRow(Guid policyId)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        var content = new DockPanel { LastChildFill = true };
        var chevron = new SymbolIcon {
            Symbol = SymbolRegular.ChevronDown16, FontSize = 14, Margin = new Thickness(8, 0, 0, 0)
        };
        chevron.SetResourceReference(WpfControl.ForegroundProperty, "Brush.Text.Muted");
        DockPanel.SetDock(chevron, Dock.Right);
        content.Children.Add(chevron);
        var name = new WpfTextBlock { FontWeight = FontWeights.Medium, MaxWidth = 180 };
        var summary = new WpfTextBlock { MaxWidth = 210, Margin = new Thickness(12, 0, 0, 0) };
        foreach (var label in new[] { name, summary }) {
            label.FontSize = 14;
            label.TextTrimming = TextTrimming.CharacterEllipsis;
            label.VerticalAlignment = VerticalAlignment.Center;
            content.Children.Add(label);
        }
        var warning = new DockPanel { Margin = new Thickness(12, 0, 0, 0) };
        warning.Children.Add(new SymbolIcon {
            Symbol = SymbolRegular.Warning16, FontSize = 14, Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        var notice = new WpfTextBlock { FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center };
        warning.Children.Add(notice);
        content.Children.Add(warning);
        var expand = new WpfButton { Content = content, Style = (Style)FindResource("Schedule.PolicyRow") };
        expand.Click += (_, _) => {
            if (Focused is not { } focus) {
                return;
            }
            _activePolicy[focus.Id] = policyId;
            _selected = null;
            _inspectorCollapsed = false;
            Refresh();
            PolicyName.Focus();
        };
        row.Children.Add(expand);

        // The switch is a sibling of the row button so toggling it doesn't also expand the policy.
        var enabled = new ToggleButton {
            Style = (Style)FindResource("Schedule.Switch"), Margin = new Thickness(12, 0, 0, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center
        };
        enabled.Click += (_, _) => EditPolicy(policyId, policy => policy with { Enabled = enabled.IsChecked == true });
        row.Children.Add(enabled);
        return new(row, expand, enabled, name, summary, warning, notice);
    }

    private void RenderInspector(TaskConfiguration focus, SchedulePolicy? policy)
    {
        Inspector.Visibility = policy is null ? Visibility.Collapsed : Visibility.Visible;
        EmptyHint.Visibility = focus.SchedulePolicies.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (policy is null) {
            return;
        }
        PolicyEnabled.IsChecked = policy.Enabled;
        if (!PolicyName.IsKeyboardFocusWithin) {
            PolicyName.Text = policy.Name;
        }
        var days = policy.Days.ToHashSet();
        PresetWeekdays.IsChecked = days.SetEquals(SchedulePreview.WeekOrder[..5]);
        PresetWeekend.IsChecked = days.SetEquals(SchedulePreview.WeekOrder[5..]);
        PresetEveryDay.IsChecked = days.Count == 7;
        RenderDays(policy);
        // While the editor has focus its chip stays as it is; leaving it commits and renders again. Everything that
        // selects another time takes focus first, so this only skips the editor's own changes and the clock tick.
        if (!_timeEditor.IsKeyboardFocusWithin || _selected is not { } editing || editing.PolicyId != policy.Id
            || !policy.Times.Contains(editing.Time)) {
            RenderTimes(policy);
        }
        var rest = SchedulePreview.WeekOrder.Where(d => !days.Contains(d)).ToArray();
        ShareHint.Visibility = _shareHintClosed ? Visibility.Collapsed : Visibility.Visible;
        SplitRestButton.Visibility = rest.Length == 0 || policy.Times.Count == 0 ? Visibility.Collapsed
            : Visibility.Visible;
        var splitLabel = new WpfTextBlock {
            Text = $"为{SchedulePreview.DaysText(rest)}新建定时", FontSize = 14
        };
        splitLabel.SetResourceReference(WpfTextBlock.ForegroundProperty, "Brush.Schedule.Link");
        SplitRestButton.Content = splitLabel;
        IssuesPanel.Children.Clear();
        foreach (var (error, text) in _preview!.Issues(focus, policy)) {
            var row = new DockPanel();
            row.Children.Add(new SymbolIcon {
                Symbol = error ? SymbolRegular.ErrorCircle16 : SymbolRegular.Info16, FontSize = 14,
                Margin = new Thickness(0, 1, 8, 0), VerticalAlignment = VerticalAlignment.Top
            });
            row.Children.Add(new WpfTextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12 });
            var box = new Border {
                Child = row, Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(10, 7, 10, 7),
                CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1)
            };
            box.SetResourceReference(Border.BackgroundProperty,
                error ? "Brush.Error.Surface" : "Brush.Warning.Surface");
            box.SetResourceReference(Border.BorderBrushProperty,
                error ? "Brush.Error.Border" : "Brush.Warning.SurfaceHover");
            box.SetResourceReference(TextElement.ForegroundProperty,
                error ? "Brush.Error.Foreground" : "Brush.Warning.Foreground");
            IssuesPanel.Children.Add(box);
        }
    }

    // The day buttons are created once, so a click that first commits a typed time still lands on them.
    private void RenderDays(SchedulePolicy policy)
    {
        if (DayChips.Children.Count == 0) {
            foreach (var day in SchedulePreview.WeekOrder) {
                var chip = new ToggleButton {
                    Content = SchedulePreview.DayName(day)[1..], Tag = day, Style = (Style)FindResource("Schedule.Day")
                };
                AutomationProperties.SetName(chip, SchedulePreview.DayName(day));
                chip.Click += (_, _) => Timeline_DayClicked(day);
                DayChips.Children.Add(chip);
            }
        }
        foreach (ToggleButton chip in DayChips.Children) {
            chip.IsChecked = policy.Days.Contains((DayOfWeek)chip.Tag);
        }
    }

    private void RenderTimes(SchedulePolicy policy)
    {
        TimeChips.Children.Clear();
        (_timeEditor.Parent as System.Windows.Controls.Panel)?.Children.Remove(_timeEditor);
        foreach (var time in policy.Times) {
            var selected = _selected is { } s && s.PolicyId == policy.Id && s.Time == time;
            var row = new StackPanel { Orientation = WpfOrientation.Horizontal };
            if (selected) {
                _timeEditor.Text = SchedulePreview.TimeText(time);
                row.Children.Add(_timeEditor);
            } else {
                row.Children.Add(new WpfTextBlock {
                    Text = SchedulePreview.TimeText(time), Width = 35, FontSize = 14,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }
            var remove = new WpfButton {
                Style = (Style)FindResource("Schedule.Icon"), Width = 22, Height = 22, Focusable = false,
                Margin = new Thickness(1, 0, 0, 0), ToolTip = "删除",
                Content = new SymbolIcon { Symbol = SymbolRegular.Dismiss12, FontSize = 12 }
            };
            AutomationProperties.SetName(remove, $"删除 {SchedulePreview.TimeText(time)}");
            // The chip buttons are rebuilt by every refresh, so they never take focus; a time being typed elsewhere
            // is committed here instead, and deleting the edited chip drops what was typed.
            remove.Click += (_, _) => {
                if (!selected) {
                    Keyboard.ClearFocus();
                }
                var removed = selected && _selected is { } typed ? typed.Time : time;
                EditPolicy(policy.Id, p => p with { Times = p.Times.Where(t => t != removed).ToArray() },
                    clearSelection: true);
            };
            row.Children.Add(remove);
            var chip = new Border {
                Child = row, Height = 28, CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 0, 4, 0),
                Margin = new Thickness(0, 0, 8, 8), BorderThickness = new Thickness(1),
                Cursor = selected ? System.Windows.Input.Cursors.IBeam : System.Windows.Input.Cursors.Hand
            };
            chip.SetResourceReference(Border.BackgroundProperty,
                selected ? "Brush.Schedule.AccentSurface" : "Brush.Schedule.Field");
            chip.SetResourceReference(Border.BorderBrushProperty,
                selected ? "Brush.Primary.Border" : "Brush.Transparent");
            if (!selected) {
                chip.MouseLeftButtonUp += (_, _) => SelectTime(policy.Id, time);
            }
            TimeChips.Children.Add(chip);
        }
        var add = new WpfButton {
            Style = (Style)FindResource("Schedule.Secondary"), Height = 28, Padding = new Thickness(10, 0, 10, 0),
            Focusable = false, Margin = new Thickness(0, 0, 0, 8),
            Content = new StackPanel {
                Orientation = WpfOrientation.Horizontal,
                Children = {
                    new SymbolIcon { Symbol = SymbolRegular.Add16, FontSize = 14, Margin = new Thickness(0, 0, 6, 0) },
                    new WpfTextBlock { Text = "添加时间", FontSize = 14 }
                }
            }
        };
        AutomationProperties.SetName(add, "添加时间");
        add.Click += (_, _) => {
            Keyboard.ClearFocus();
            if (EditedPolicy is not { } current) {
                return;
            }
            var time = new TimeOnly(12, 0);
            for (var i = 0; i < 96 && current.Times.Contains(time); i++) {
                time = time.AddMinutes(15);
            }
            EditPolicy(current.Id, p => p with { Times = [.. p.Times, time] }, select: time);
            FocusTimeEditor();
        };
        TimeChips.Children.Add(add);

    }

    private void SelectTime(Guid policyId, TimeOnly time)
    {
        Keyboard.ClearFocus();
        _selected = (policyId, time);
        Refresh();
        FocusTimeEditor();
    }

    private void FocusTimeEditor() => Dispatcher.BeginInvoke(() => {
        _timeEditor.Focus();
        _timeEditor.SelectAll();
    }, DispatcherPriority.Input);

    // Policy edits are saved immediately; a failed save is reported and the view shows what was stored.
    private void Edit(string operation, Action<ProjectPlanModule> change)
    {
        if (Project is not { } project) {
            return;
        }
        try {
            change(project);
        } catch (Exception exception) {
            _editFailed(operation, exception);
        }
        Refresh();
        PoliciesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void EditPolicy(Guid policyId, Func<SchedulePolicy, SchedulePolicy> change, bool clearSelection = false,
        TimeOnly? select = null)
    {
        if (Focused is not { } focus
            || focus.SchedulePolicies.FirstOrDefault(p => p.Id == policyId) is not { } policy) {
            return;
        }
        if (clearSelection) {
            _selected = null;
        }
        if (select is { } time) {
            _selected = (policyId, time);
        }
        Edit("保存定时失败", project => project.UpdateSchedulePolicy(focus.Id, change(policy)));
    }

    private void AddPolicy(TaskConfiguration focus, string name, IReadOnlyList<DayOfWeek>? days = null,
        IReadOnlyList<TimeOnly>? times = null)
    {
        Edit("新建定时失败", project => {
            var policy = project.AddSchedulePolicy(focus.Id, name);
            if (days is not null && times is not null) {
                policy = policy with { Days = days, Times = times };
                project.UpdateSchedulePolicy(focus.Id, policy);
            }
            _activePolicy[focus.Id] = policy.Id;
            _inspectorCollapsed = false;
            _selected = policy.Times.Count == 0 ? null : (policy.Id, policy.Times[0]);
        });
    }

    // Times are shared by every day of a policy, so a click on one of its days asks whether to add for that day
    // alone (a new policy) or for all of them. Clicking a day outside the policy starts a new one directly.
    private void Timeline_EmptyClicked(DayOfWeek day, TimeOnly time)
    {
        if (Focused is not { } focus) {
            return;
        }
        var single = $"{SchedulePreview.DayName(day)} {SchedulePreview.TimeText(time)}";
        if (ActivePolicy(focus) is not { } policy || !policy.Days.Contains(day)) {
            AddPolicy(focus, single, [day], [time]);
            return;
        }
        void AddToAll() => EditPolicy(policy.Id, p => p with { Times = [.. p.Times, time] }, select: time);
        if (policy.Days.Count == 1) {
            AddToAll();
            return;
        }
        OpenScopeMenu($"添加 {SchedulePreview.TimeText(time)}", day, time,
            new($"只在{SchedulePreview.DayName(day)}", "新建一条定时", [day], false,
                () => AddPolicy(focus, single, [day], [time])),
            new($"{SchedulePreview.DaysText(policy.Days)}都加", $"加入「{policy.Name}」", policy.Days, false, AddToAll));
    }

    private void Timeline_PinDoubleClicked(TimelinePin pin)
    {
        if (Focused is not { } focus || focus.SchedulePolicies.FirstOrDefault(p => p.Id == pin.PolicyId) is not
            { } policy) {
            return;
        }
        void RemoveFromAll() => EditPolicy(policy.Id,
            p => p with { Times = p.Times.Where(t => t != pin.Time).ToArray() }, clearSelection: true);
        if (policy.Days.Count == 1) {
            RemoveFromAll();
            return;
        }
        var day = SchedulePreview.DayName(pin.Day);
        var rest = policy.Times.Where(t => t != pin.Time).ToArray();
        OpenScopeMenu($"删除 {SchedulePreview.TimeText(pin.Time)}", pin.Day, pin.Time,
            new($"只删{day}的", rest.Length == 0 ? $"{day}移出这条定时" : $"{day}拆成一条新定时", [pin.Day], true,
                () => SplitDay(focus, policy, pin.Day, rest)),
            new($"{SchedulePreview.DaysText(policy.Days)}都删", null, policy.Days, true, RemoveFromAll));
    }

    // Removing a time from one day only: that day leaves the policy and keeps its other times in a new one.
    private void SplitDay(TaskConfiguration focus, SchedulePolicy policy, DayOfWeek day, TimeOnly[] rest)
    {
        _selected = null;
        Edit("保存定时失败", project => {
            project.UpdateSchedulePolicy(focus.Id, policy with { Days = policy.Days.Where(d => d != day).ToArray() });
            if (rest.Length != 0) {
                var split = project.AddSchedulePolicy(focus.Id, $"{policy.Name} · {SchedulePreview.DayName(day)}");
                project.UpdateSchedulePolicy(focus.Id,
                    split with { Enabled = policy.Enabled, Days = [day], Times = rest });
            }
        });
    }

    private void OpenScopeMenu(string title, DayOfWeek day, TimeOnly time, params ScheduleScopeOption[] options)
    {
        const double width = 222, shadowLeft = 14, shadowTop = 6;
        var itemStyle = (Style)FindResource("Schedule.ScopeItem");
        var anchor = Timeline.ScopeMenuAnchor(day, time, width, options[0].Remove);
        var menu = new ContextMenu {
            Style = (Style)FindResource("Schedule.Menu"), PlacementTarget = Timeline,
            Placement = PlacementMode.RelativePoint, Width = width + 2 * shadowLeft,
            HorizontalOffset = anchor.X - shadowLeft, VerticalOffset = anchor.Y - shadowTop
        };
        var heading = new MenuItem {
            Header = title, IsEnabled = false, Style = itemStyle, FontSize = 12, FontWeight = FontWeights.Normal, Padding = new Thickness(10, 6, 10, 4),
            Cursor = System.Windows.Input.Cursors.Arrow
        };
        heading.SetResourceReference(WpfControl.ForegroundProperty, "Brush.Text.Muted");
        menu.Items.Add(heading);
        foreach (var option in options) {
            var item = new MenuItem { Header = option.Header, Tag = option.Note, Style = itemStyle };
            void Show() => Timeline.Preview = (option.Days.ToHashSet(), time, option.Remove);
            item.MouseEnter += (_, _) => Show();
            item.GotKeyboardFocus += (_, _) => Show();
            item.Click += (_, _) => option.Apply();
            menu.Items.Add(item);
        }
        menu.Opened += (_, _) => Timeline.Preview = (options[0].Days.ToHashSet(), time, options[0].Remove);
        menu.Closed += (_, _) => {
            if (ReferenceEquals(_scopeMenu, menu)) {
                Timeline.Preview = null;
                _scopeMenu = null;
            }
        };
        if (_scopeMenu is not null) {
            _scopeMenu.IsOpen = false;
        }
        _scopeMenu = menu;
        menu.IsOpen = true;
    }

    private void Timeline_DayClicked(DayOfWeek day)
    {
        if (Focused is { } focus && ActivePolicy(focus) is { } policy) {
            EditPolicy(policy.Id, p => p with {
                Days = p.Days.Contains(day) ? p.Days.Where(d => d != day).ToArray() : [.. p.Days, day]
            });
        }
    }

    private void Timeline_PinClicked(TimelinePin pin)
    {
        if (pin.Role == TimelinePinRole.Other) {
            _focus = pin.ConfigurationId;
            _selected = null;
        } else {
            _selected = (pin.PolicyId, pin.Time);
        }
        _activePolicy[pin.ConfigurationId] = pin.PolicyId;
        _inspectorCollapsed = false;
        Refresh();
    }

    private void Timeline_PinMoved(TimelinePin pin, TimeOnly time) => EditPolicy(pin.PolicyId,
        p => p with { Times = [.. p.Times.Where(t => t != pin.Time), time] }, select: time);

    private SchedulePolicy? EditedPolicy => Focused is { } focus ? ActivePolicy(focus) : null;

    private void PolicyEnabled_Click(object sender, RoutedEventArgs e)
    {
        if (EditedPolicy is { } policy) {
            EditPolicy(policy.Id, p => p with { Enabled = PolicyEnabled.IsChecked == true });
        }
    }

    internal void SetPolicyEnabled(Guid configurationId, Guid policyId, bool enabled)
    {
        var policy = Project?.Configurations.FirstOrDefault(configuration => configuration.Id == configurationId)
            ?.SchedulePolicies.FirstOrDefault(item => item.Id == policyId);
        if (policy is not null && policy.Enabled != enabled) {
            Edit("保存定时失败", project => project.UpdateSchedulePolicy(configurationId, policy with { Enabled = enabled }));
        }
    }

    // An emptied name is not an error worth a message: leaving the box puts the stored name back.
    private void CommitName()
    {
        if (EditedPolicy is not { } policy) {
            return;
        }
        var name = PolicyName.Text.Trim();
        if (name.Length == 0 || name == policy.Name) {
            PolicyName.Text = policy.Name;
            return;
        }
        EditPolicy(policy.Id, p => p with { Name = name });
    }

    private void PolicyName_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitName();

    private void PolicyName_KeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key == Key.Enter) {
            CommitName();
            Keyboard.ClearFocus();
        } else if (e.Key == Key.Escape && EditedPolicy is { } policy) {
            PolicyName.Text = policy.Name;
            Keyboard.ClearFocus();
        }
    }

    private void QuickDays_Click(object sender, RoutedEventArgs e)
    {
        if (EditedPolicy is not { } policy || sender is not FrameworkElement { Tag: string which }) {
            return;
        }
        DayOfWeek[] days = which switch {
            "Weekdays" => SchedulePreview.WeekOrder[..5],
            "Weekend" => SchedulePreview.WeekOrder[5..],
            _ => SchedulePreview.WeekOrder
        };
        EditPolicy(policy.Id, p => p with { Days = days });
    }

    private void DeletePolicy_Click(object sender, RoutedEventArgs e)
    {
        if (Focused is not { } focus || EditedPolicy is not { } policy) {
            return;
        }
        _activePolicy.Remove(focus.Id);
        _selected = null;
        Edit("删除定时失败", project => project.DeleteSchedulePolicy(focus.Id, policy.Id));
    }

    // Out-of-range digits are clamped rather than rejected: 29:75 becomes 23:59.
    private TimeOnly? EditorTime()
    {
        var text = _timeEditor.Text;
        if (text.Length == 5 && text[2] == ':' && int.TryParse(text[..2], out var hour)
            && int.TryParse(text[3..], out var minute)) {
            return new TimeOnly(Math.Min(hour, 23), Math.Min(minute, 59));
        }
        return SchedulePreview.ParseTime(text);
    }

    private void CommitSelectedTime()
    {
        if (EditedPolicy is not { } policy || _selected is not { } selected || selected.PolicyId != policy.Id) {
            return;
        }
        if (EditorTime() is not { } time || time == selected.Time) {
            _timeEditor.Text = SchedulePreview.TimeText(selected.Time);
            return;
        }
        EditPolicy(policy.Id, p => p with { Times = [.. p.Times.Where(t => t != selected.Time), time] }, select: time);
    }

    // The colon is fixed: a digit overwrites the one after the caret and deleting turns digits into 0.
    private void TimeEditor_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = true;
        if (e.Text.Length != 1 || !char.IsAsciiDigit(e.Text[0]) || ZeroSelection() is not { } chars) {
            return;
        }
        var at = _timeEditor.SelectionStart == 2 ? 3 : _timeEditor.SelectionStart;
        if (at <= 4) {
            chars[at] = e.Text[0];
            SetEditor(chars, at + 1 == 2 ? 3 : at + 1);
        }
    }

    private void TimeEditor_PreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        var start = _timeEditor.SelectionStart;
        switch (e.Key) {
            case Key.Enter or Key.Escape:
                if (e.Key == Key.Enter) {
                    CommitSelectedTime();
                }
                _selected = null;
                Keyboard.ClearFocus();
                Refresh();
                break;
            case Key.Up or Key.Down:
                StepSelectedTime(e.Key == Key.Up ? 5 : -5);
                break;
            case Key.Back or Key.Delete when ZeroSelection() is { } chars:
                if (_timeEditor.SelectionLength > 0) {
                    SetEditor(chars, start);
                } else if (e.Key == Key.Back && start > 0) {
                    var at = start == 3 ? 1 : start - 1;
                    chars[at] = '0';
                    SetEditor(chars, at);
                } else if (e.Key == Key.Delete && start < 5) {
                    var at = start == 2 ? 3 : start;
                    chars[at] = '0';
                    SetEditor(chars, at + 1 == 2 ? 3 : at + 1);
                }
                break;
            case Key.Space or Key.Back or Key.Delete:
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private char[]? ZeroSelection()
    {
        var chars = _timeEditor.Text.ToCharArray();
        if (chars.Length != 5 || chars[2] != ':') {
            return null;
        }
        for (var i = _timeEditor.SelectionStart; i < _timeEditor.SelectionStart + _timeEditor.SelectionLength; i++) {
            if (i != 2) {
                chars[i] = '0';
            }
        }
        return chars;
    }

    private void SetEditor(char[] chars, int caret)
    {
        _timeEditor.Text = new string(chars);
        _timeEditor.Select(caret, 0);
    }

    private void StepSelectedTime(int minutes)
    {
        if (EditedPolicy is not { } policy || _selected is not { } selected || selected.PolicyId != policy.Id) {
            return;
        }
        var time = selected.Time.AddMinutes(minutes);
        for (var i = 0; i < 288 && policy.Times.Contains(time); i++) {
            time = time.AddMinutes(minutes);
        }
        EditPolicy(policy.Id, p => p with { Times = [.. p.Times.Where(t => t != selected.Time), time] }, select: time);
        _timeEditor.Text = SchedulePreview.TimeText(time);
        _timeEditor.SelectAll();
    }

    private void SplitRest_Click(object sender, RoutedEventArgs e)
    {
        if (Focused is not { } focus || EditedPolicy is not { } policy) {
            return;
        }
        var rest = SchedulePreview.WeekOrder.Where(d => !policy.Days.Contains(d)).ToArray();
        AddPolicy(focus, SchedulePreview.DaysText(rest), rest, policy.Times);
    }

    // Failing to remember only means the hint returns next launch, so it is not worth a message.
    private void ShareHintClose_Click(object sender, RoutedEventArgs e)
    {
        _shareHintClosed = true;
        if (_shareHintPath is { } path) {
            try {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "closed");
            } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            }
        }
        Refresh();
    }
}
