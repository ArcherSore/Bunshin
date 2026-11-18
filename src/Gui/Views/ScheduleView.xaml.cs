using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using ProjectModel;
using SymbolIcon = Wpf.Ui.Controls.SymbolIcon;
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfButton = System.Windows.Controls.Button;
using WpfColor = System.Windows.Media.Color;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfTextBlock = System.Windows.Controls.TextBlock;

namespace Gui.Views;

internal enum ScheduleRuntimeTone { Idle, Busy, Run, Failed }

// The schedule page: every configuration's weekly policies on one timeline, one configuration edited at a time.
// Policies are saved through ProjectPlanModule as soon as they change; this view keeps only what is selected.
public sealed partial class ScheduleView : System.Windows.Controls.UserControl
{
    private static readonly WpfColor[] Palette = [
        WpfColor.FromRgb(0x31, 0x56, 0xA3), WpfColor.FromRgb(0x26, 0x73, 0x4D), WpfColor.FromRgb(0xB2, 0x6A, 0x00),
        WpfColor.FromRgb(0x0E, 0x7C, 0x86), WpfColor.FromRgb(0x7A, 0x3E, 0x9D), WpfColor.FromRgb(0xB4, 0x23, 0x2F)];

    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly Dictionary<Guid, Guid> _activePolicy = [];
    private Func<ProjectPlanModule?> _project = () => null;
    private Func<Guid, string?> _invalidReason = _ => null;
    private Action<string, Exception> _editFailed = (_, _) => { };
    private TimeProvider _clock = TimeProvider.System;
    private Guid? _focus;
    private (Guid PolicyId, TimeOnly Time)? _selected;
    private SchedulePreview? _preview;

    public ScheduleView()
    {
        InitializeComponent();
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
        Timeline.PinDoubleClicked += pin => EditPolicy(pin.PolicyId,
            p => p with { Times = p.Times.Where(t => t != pin.Time).ToArray() }, clearSelection: true);
        SetRuntimeStatus("空闲", ScheduleRuntimeTone.Idle);
    }

    internal event EventHandler? HomeRequested;

    internal void Attach(Func<ProjectPlanModule?> project, Func<Guid, string?> invalidReason,
        Action<string, Exception> editFailed, TimeProvider clock)
    {
        (_project, _invalidReason, _editFailed, _clock) = (project, invalidReason, editFailed, clock);
    }

    internal void SetRuntimeStatus(string text, ScheduleRuntimeTone tone)
    {
        StatusText.Text = text;
        StatusDot.Fill = (WpfBrush)FindResource(tone switch {
            ScheduleRuntimeTone.Run => "Brush.Success",
            ScheduleRuntimeTone.Busy => "Brush.Warning",
            ScheduleRuntimeTone.Failed => "Brush.Error",
            _ => "Brush.Border.Strong"
        });
        StatusView.Visibility = tone == ScheduleRuntimeTone.Idle ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(StatusCapsule, $"桌面分身：{text}");
    }

    private DateTime Now => _clock.GetLocalNow().DateTime;
    private ProjectPlanModule? Project => _project();
    private TaskConfiguration? Focused => Project?.Configurations.FirstOrDefault(c => c.Id == _focus);

    private static WpfColor ColorOf(int index) => Palette[index % Palette.Length];

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

    internal void Refresh()
    {
        if (Project is not { } project || project.Configurations.Count == 0) {
            return;
        }
        var configurations = project.Configurations;
        if (Focused is null) {
            _focus = project.ActiveConfigurationId;
        }
        var focus = Focused ?? configurations[0];
        _focus = focus.Id;
        var policy = ActivePolicy(focus);
        if (_selected is { } selected && (policy?.Id != selected.PolicyId || !policy.Times.Contains(selected.Time))) {
            _selected = null;
        }
        _preview = new SchedulePreview(configurations, _invalidReason, Now);
        RenderBanner(_preview);
        RenderList(configurations, focus);
        RenderTimeline(configurations, focus, policy);
        RenderPolicyBar(configurations, focus, policy);
        RenderInspector(configurations, focus, policy);
    }

    private void RenderBanner(SchedulePreview preview)
    {
        if (preview.NextRun is { Winner: { } winner } next) {
            var policies = next.Entries.First(e => e.Configuration == winner).Policies.Select(p => p.Name);
            var skipped = next.Entries.Where(e => e.Configuration != winner).Select(e => e.Configuration.Name)
                .ToArray();
            NextRunBanner.Background = new LinearGradientBrush(WpfColor.FromRgb(0x20, 0x3F, 0x7E),
                WpfColor.FromRgb(0x4D, 0x72, 0xBF), 0);
            NextRunBanner.BorderThickness = new Thickness(0);
            SetBannerForeground(WpfBrushes.White);
            NextRunLabel.Visibility = Visibility.Visible;
            NextRunTime.FontSize = 20;
            NextRunTime.Text = SchedulePreview.WhenText(next.Time, preview.Now);
            NextRunDetail.Text = $"{winner.Name} · {string.Join("、", policies)}\n"
                + SchedulePreview.InText(next.Time, preview.Now)
                + (skipped.Length == 0 ? string.Empty : $" · 跳过 {string.Join("、", skipped)}");
        } else {
            NextRunBanner.Background = (WpfBrush)FindResource("Brush.Surface");
            NextRunBanner.BorderBrush = (WpfBrush)FindResource("Brush.Border");
            NextRunBanner.BorderThickness = new Thickness(1);
            SetBannerForeground((WpfBrush)FindResource("Brush.Text.Secondary"));
            NextRunLabel.Visibility = Visibility.Collapsed;
            NextRunTime.FontSize = 15;
            NextRunTime.Text = "没有启用的定时";
            NextRunDetail.Text = string.Empty;
        }
        AutomationProperties.SetName(NextRunBanner, $"{NextRunLabel.Text}：{NextRunTime.Text}");

        void SetBannerForeground(WpfBrush brush)
        {
            NextRunIcon.Foreground = brush;
            NextRunLabel.Foreground = NextRunTime.Foreground = NextRunDetail.Foreground = brush;
        }
    }

    private void RenderList(IReadOnlyList<TaskConfiguration> configurations, TaskConfiguration focus)
    {
        ConfigurationList.Children.Clear();
        for (var index = 0; index < configurations.Count; index++) {
            var configuration = configurations[index];
            var (outcome, text) = _preview!.ListState(configuration);
            var selected = configuration == focus;
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition());
            grid.RowDefinitions.Add(new RowDefinition());
            grid.Children.Add(new System.Windows.Shapes.Rectangle {
                Width = 10, Height = 10, RadiusX = 3, RadiusY = 3, Margin = new Thickness(0, 0, 8, 0),
                Fill = new SolidColorBrush(ColorOf(index)), VerticalAlignment = VerticalAlignment.Center
            });
            AddCell(grid, new WpfTextBlock {
                Text = configuration.Name, FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = (WpfBrush)FindResource("Brush.Text.Primary")
            }, 0, 1);
            var enabled = configuration.SchedulePolicies.Count(p => p.Enabled);
            AddCell(grid, new WpfTextBlock {
                Text = $"{enabled}/{configuration.SchedulePolicies.Count}", FontSize = 11,
                Foreground = (WpfBrush)FindResource("Brush.Text.Muted"), VerticalAlignment = VerticalAlignment.Center
            }, 0, 2);
            var state = new WpfTextBlock {
                Text = text, FontSize = 11.5, Margin = new Thickness(0, 2, 0, 0),
                Foreground = (WpfBrush)FindResource(outcome switch {
                    ScheduleOutcome.Skip => "Brush.Warning.Foreground",
                    ScheduleOutcome.Invalid => "Brush.Error.Foreground",
                    ScheduleOutcome.Run => "Brush.Text.Secondary",
                    _ => "Brush.Text.Muted"
                })
            };
            AddCell(grid, state, 1, 1);
            Grid.SetColumnSpan(state, 2);
            var button = new WpfButton {
                Content = grid, Tag = configuration.Id, Padding = new Thickness(10, 8, 10, 8),
                HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch,
                Style = (Style)FindResource("SubtleButtonStyle"), Margin = new Thickness(0, 0, 0, 2),
                BorderThickness = new Thickness(selected ? 3 : 0, 0, 0, 0),
                BorderBrush = new SolidColorBrush(ColorOf(index))
            };
            // Only the selected row sets its background, so the others keep the style's hover feedback.
            if (selected) {
                button.Background = (WpfBrush)FindResource("Brush.Primary.Surface");
            }
            AutomationProperties.SetName(button, $"{configuration.Name}，{text}");
            button.Click += (_, _) => {
                _focus = configuration.Id;
                _selected = null;
                Refresh();
            };
            ConfigurationList.Children.Add(button);
        }

        static void AddCell(Grid grid, UIElement element, int row, int column)
        {
            Grid.SetRow(element, row);
            Grid.SetColumn(element, column);
            grid.Children.Add(element);
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
                            _preview!.InvalidReason(other) is null && other.SchedulePolicies.Any(p => p.Enabled
                                && p.Days.Contains(day) && p.Times.Contains(time))) : null;
                        var tip = (role == TimelinePinRole.Other ? $"{configuration.Name} · " : string.Empty)
                            + $"{policy.Name} {SchedulePreview.TimeText(time)}"
                            + (policy.Enabled ? "" : "（已停用）")
                            + (winner is null ? "" : $"\n与「{winner.Name}」同时到点，将跳过")
                            + (role == TimelinePinRole.Active ? "\n拖动调整 · 双击删除" : "");
                        pins.Add(new(configuration.Id, policy.Id, day, time, ColorOf(index), role, policy.Enabled,
                            winner is not null, offset, tip));
                    }
                }
            }
        }
        var color = ColorOf(configurations.ToList().IndexOf(focus));
        Timeline.ActiveColor = color;
        Timeline.ActiveDays = (active?.Days ?? []).ToHashSet();
        Timeline.Selected = _selected;
        Timeline.Clock = Now;
        Timeline.Pins = pins;
        KeyCurrent.Fill = new SolidColorBrush(color);
    }

    private void RenderPolicyBar(IReadOnlyList<TaskConfiguration> configurations, TaskConfiguration focus,
        SchedulePolicy? active)
    {
        PolicyBar.Children.Clear();
        PolicyBar.Children.Add(new WpfTextBlock {
            Text = focus.Name, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0), Foreground = (WpfBrush)FindResource("Brush.Text.Primary")
        });
        var color = new SolidColorBrush(ColorOf(configurations.ToList().IndexOf(focus)));
        foreach (var policy in focus.SchedulePolicies) {
            var content = new StackPanel { Orientation = WpfOrientation.Horizontal };
            content.Children.Add(new Ellipse {
                Width = 10, Height = 10, VerticalAlignment = VerticalAlignment.Center,
                Fill = policy.Enabled ? color : WpfBrushes.Transparent,
                Stroke = policy.Enabled ? null : (WpfBrush)FindResource("Brush.Border.Strong"), StrokeThickness = 2
            });
            content.Children.Add(new WpfTextBlock { Text = policy.Name, Margin = new Thickness(7, 0, 0, 0) });
            content.Children.Add(new WpfTextBlock {
                Text = policy.Times.Count.ToString(), Margin = new Thickness(7, 0, 0, 0), FontSize = 11,
                Foreground = (WpfBrush)FindResource("Brush.Text.Muted"), VerticalAlignment = VerticalAlignment.Center
            });
            var isActive = policy == active;
            var chip = new WpfButton {
                Content = content, Height = 30, Padding = new Thickness(10, 0, 10, 0),
                Margin = new Thickness(0, 0, 6, 0),
                Style = (Style)FindResource("SubtleButtonStyle"), Background = (WpfBrush)FindResource("Brush.Surface"),
                BorderBrush = isActive ? color : (WpfBrush)FindResource("Brush.Border"),
                BorderThickness = new Thickness(isActive ? 2 : 1), FontWeight = isActive ? FontWeights.SemiBold
                    : FontWeights.Normal
            };
            AutomationProperties.SetName(chip, $"{policy.Name}，{SchedulePreview.Summary(policy)}");
            chip.Click += (_, _) => {
                _activePolicy[focus.Id] = policy.Id;
                _selected = null;
                Refresh();
            };
            PolicyBar.Children.Add(chip);
        }
        var add = new WpfButton {
            Style = (Style)FindResource("NeutralIconButtonStyle"), Width = 30, Height = 30, MinWidth = 30,
            MinHeight = 30, Cursor = System.Windows.Input.Cursors.Hand, ToolTip = "新建定时",
            Content = new SymbolIcon { Symbol = SymbolRegular.Add16, FontSize = 15 }
        };
        AutomationProperties.SetName(add, "新建定时");
        add.Click += (_, _) => AddPolicy(focus, "定时 " + (focus.SchedulePolicies.Count + 1), null, null);
        PolicyBar.Children.Add(add);
    }

    private void RenderInspector(IReadOnlyList<TaskConfiguration> configurations, TaskConfiguration focus,
        SchedulePolicy? policy)
    {
        Inspector.Visibility = policy is null ? Visibility.Collapsed : Visibility.Visible;
        EmptyHint.Visibility = policy is null ? Visibility.Visible : Visibility.Collapsed;
        if (policy is null) {
            return;
        }
        Inspector.BorderBrush = new SolidColorBrush(ColorOf(configurations.ToList().IndexOf(focus)));
        PolicyEnabled.IsChecked = policy.Enabled;
        if (!PolicyName.IsKeyboardFocusWithin) {
            PolicyName.Text = policy.Name;
        }
        var days = policy.Days.ToHashSet();
        PresetWeekdays.IsChecked = days.SetEquals(SchedulePreview.WeekOrder[..5]);
        PresetWeekend.IsChecked = days.SetEquals(SchedulePreview.WeekOrder[5..]);
        PresetEveryDay.IsChecked = days.Count == 7;
        var selected = _selected is { } s && s.PolicyId == policy.Id ? s.Time : (TimeOnly?)null;
        SelectedTimeRow.Visibility = selected is null ? Visibility.Collapsed : Visibility.Visible;
        if (selected is { } time) {
            SelectedTimeText.Text = "● " + SchedulePreview.TimeText(time);
            SelectedTimeText.Foreground = Inspector.BorderBrush;
            if (!SelectedTimeInput.IsKeyboardFocusWithin) {
                SelectedTimeInput.Text = SchedulePreview.TimeText(time);
            }
            SelectedTimeShared.Text = $"{SchedulePreview.DaysText(policy.Days)}共用";
        }
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
                CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1),
                Background = (WpfBrush)FindResource(error ? "Brush.Error.Surface" : "Brush.Warning.Surface"),
                BorderBrush = (WpfBrush)FindResource(error ? "Brush.Error.Border" : "Brush.Warning.SurfaceHover")
            };
            TextElement.SetForeground(box, (WpfBrush)FindResource(error ? "Brush.Error.Foreground"
                : "Brush.Warning.Foreground"));
            IssuesPanel.Children.Add(box);
        }
    }

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

    private void AddPolicy(TaskConfiguration focus, string name, DayOfWeek? day, TimeOnly? time)
    {
        Edit("新建定时失败", project => {
            var policy = project.AddSchedulePolicy(focus.Id, name);
            if (day is { } d && time is { } t) {
                policy = policy with { Days = [d], Times = [t] };
                project.UpdateSchedulePolicy(focus.Id, policy);
            }
            _activePolicy[focus.Id] = policy.Id;
            _selected = (policy.Id, policy.Times[0]);
        });
    }

    private void Timeline_EmptyClicked(DayOfWeek day, TimeOnly time)
    {
        if (Focused is not { } focus) {
            return;
        }
        if (ActivePolicy(focus) is { } policy && policy.Days.Contains(day)) {
            EditPolicy(policy.Id, p => p with { Times = [.. p.Times, time] }, select: time);
        } else {
            AddPolicy(focus, $"{SchedulePreview.DayName(day)} {SchedulePreview.TimeText(time)}", day, time);
        }
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

    private void CommitSelectedTime()
    {
        if (EditedPolicy is not { } policy || _selected is not { } selected || selected.PolicyId != policy.Id) {
            return;
        }
        if (SchedulePreview.ParseTime(SelectedTimeInput.Text) is not { } time || time == selected.Time) {
            SelectedTimeInput.Text = SchedulePreview.TimeText(selected.Time);
            return;
        }
        EditPolicy(policy.Id, p => p with { Times = [.. p.Times.Where(t => t != selected.Time), time] }, select: time);
    }

    private void SelectedTimeInput_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        CommitSelectedTime();

    private void SelectedTimeInput_KeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key == Key.Enter) {
            CommitSelectedTime();
            SelectedTimeInput.SelectAll();
        }
    }

    private void DeleteSelectedTime_Click(object sender, RoutedEventArgs e)
    {
        if (EditedPolicy is { } policy && _selected is { } selected && selected.PolicyId == policy.Id) {
            EditPolicy(policy.Id, p => p with { Times = p.Times.Where(t => t != selected.Time).ToArray() },
                clearSelection: true);
        }
    }

    private void StatusCapsule_Click(object sender, RoutedEventArgs e) => HomeRequested?.Invoke(this, EventArgs.Empty);
}
