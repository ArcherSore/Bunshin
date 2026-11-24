using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using ProjectModel;
using SymbolIcon = Wpf.Ui.Controls.SymbolIcon;
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfButton = System.Windows.Controls.Button;
using WpfControl = System.Windows.Controls.Control;
using WpfColor = System.Windows.Media.Color;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfTextBlock = System.Windows.Controls.TextBlock;

namespace Gui.Views;

internal enum ScheduleRuntimeTone { Idle, Busy, Run, Failed }

// One choice of a scope menu; Days and Remove drive the timeline preview while the choice is pointed at.
internal sealed record ScheduleScopeOption(string Header, string? Note, IReadOnlyCollection<DayOfWeek> Days,
    bool Remove, Action Apply);

// The schedule page: every configuration's weekly policies on one timeline, one configuration edited at a time.
// Policies are saved through ProjectPlanModule as soon as they change; this view keeps only what is selected.
public sealed partial class ScheduleView : System.Windows.Controls.UserControl
{
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly Dictionary<Guid, Guid> _activePolicy = [];
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
        SetRuntimeStatus("空闲", ScheduleRuntimeTone.Idle);
    }

    internal event EventHandler? HomeRequested;
    internal event EventHandler? PoliciesChanged;

    // The shared-times hint is closed once for every configuration; the file under the config folder remembers it.
    internal void Attach(Func<ProjectPlanModule?> project, Func<Guid, string?> invalidReason,
        Action<string, Exception> editFailed, TimeProvider clock, string shareHintPath)
    {
        (_project, _invalidReason, _editFailed, _clock) = (project, invalidReason, editFailed, clock);
        _shareHintPath = shareHintPath;
        _shareHintClosed = File.Exists(shareHintPath);
    }

    internal void SetRuntimeStatus(string text, ScheduleRuntimeTone tone)
    {
        StatusText.Text = text;
        StatusDot.SetResourceReference(Shape.FillProperty, tone switch {
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

    private static int PaletteIndex(int index) => index % ScheduleTimeline.PaletteKeys.Count;

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
        var focus = Focused ?? configurations.FirstOrDefault(c => c.Id == project.ActiveConfigurationId)
            ?? configurations[0];
        _focus = focus.Id;
        var paletteIndex = PaletteIndex(configurations.ToList().IndexOf(focus));
        var brushKey = ScheduleTimeline.PaletteKeys[paletteIndex];
        var policy = ActivePolicy(focus);
        if (_selected is { } selected && (policy?.Id != selected.PolicyId || !policy.Times.Contains(selected.Time))) {
            _selected = null;
        }
        _preview = new SchedulePreview(configurations, _invalidReason, Now);
        RenderBanner(_preview);
        RenderList(configurations, focus);
        RenderTimeline(configurations, focus, policy, paletteIndex);
        RenderPolicyBar(focus, policy, brushKey);
        RenderInspector(focus, policy, brushKey);
    }

    private void RenderBanner(SchedulePreview preview)
    {
        if (preview.NextRun is { Winner: { } winner } next) {
            var policies = next.Entries.First(e => e.Configuration == winner).Policies.Select(p => p.Name);
            var skipped = next.Entries.Where(e => e.Configuration != winner).Select(e => e.Configuration.Name)
                .ToArray();
            NextRunBanner.SetResourceReference(Border.BackgroundProperty, "Brush.Schedule.NextRun");
            NextRunBanner.BorderThickness = new Thickness(0);
            SetBannerForeground("Brush.Text.Inverse");
            NextRunLabel.Visibility = Visibility.Visible;
            NextRunTime.FontSize = 20;
            NextRunTime.Text = SchedulePreview.WhenText(next.Time, preview.Now);
            NextRunDetail.Text = $"{winner.Name} · {string.Join("、", policies)}\n"
                + SchedulePreview.InText(next.Time, preview.Now)
                + (skipped.Length == 0 ? string.Empty : $" · 跳过 {string.Join("、", skipped)}");
        } else {
            NextRunBanner.SetResourceReference(Border.BackgroundProperty, "Brush.Surface");
            NextRunBanner.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
            NextRunBanner.BorderThickness = new Thickness(1);
            SetBannerForeground("Brush.Text.Secondary");
            NextRunLabel.Visibility = Visibility.Collapsed;
            NextRunTime.FontSize = 15;
            NextRunTime.Text = "没有启用的定时";
            NextRunDetail.Text = string.Empty;
        }
        AutomationProperties.SetName(NextRunBanner, $"{NextRunLabel.Text}：{NextRunTime.Text}");

        void SetBannerForeground(string key)
        {
            NextRunIcon.SetResourceReference(WpfControl.ForegroundProperty, key);
            NextRunLabel.SetResourceReference(WpfTextBlock.ForegroundProperty, key);
            NextRunTime.SetResourceReference(WpfTextBlock.ForegroundProperty, key);
            NextRunDetail.SetResourceReference(WpfTextBlock.ForegroundProperty, key);
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
            var brushKey = ScheduleTimeline.PaletteKeys[PaletteIndex(index)];
            var swatch = new System.Windows.Shapes.Rectangle {
                Width = 10, Height = 10, RadiusX = 3, RadiusY = 3, Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            swatch.SetResourceReference(Shape.FillProperty, brushKey);
            grid.Children.Add(swatch);
            var name = new WpfTextBlock {
                Text = configuration.Name, FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            name.SetResourceReference(WpfTextBlock.ForegroundProperty, "Brush.Text.Primary");
            AddCell(grid, name, 0, 1);
            var enabled = configuration.SchedulePolicies.Count(p => p.Enabled);
            var count = new WpfTextBlock {
                Text = $"{enabled}/{configuration.SchedulePolicies.Count}", FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            };
            count.SetResourceReference(WpfTextBlock.ForegroundProperty, "Brush.Text.Muted");
            AddCell(grid, count, 0, 2);
            var state = new WpfTextBlock {
                Text = text, FontSize = 11.5, Margin = new Thickness(0, 2, 0, 0)
            };
            state.SetResourceReference(WpfTextBlock.ForegroundProperty, outcome switch {
                ScheduleOutcome.Skip => "Brush.Warning.Foreground",
                ScheduleOutcome.Invalid => "Brush.Error.Foreground",
                ScheduleOutcome.Run => "Brush.Text.Secondary",
                _ => "Brush.Text.Muted"
            });
            AddCell(grid, state, 1, 1);
            Grid.SetColumnSpan(state, 2);
            var button = new WpfButton {
                Content = grid, Tag = configuration.Id, Padding = new Thickness(10, 8, 10, 8),
                HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch,
                Style = (Style)FindResource("SubtleButtonStyle"), Margin = new Thickness(0, 0, 0, 2),
                BorderThickness = new Thickness(selected ? 3 : 0, 0, 0, 0)
            };
            button.SetResourceReference(WpfControl.BorderBrushProperty, brushKey);
            // Only the selected row sets its background, so the others keep the style's hover feedback.
            if (selected) {
                button.SetResourceReference(WpfControl.BackgroundProperty, "Brush.Primary.Surface");
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
        SchedulePolicy? active, int paletteIndex)
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
                        pins.Add(new(configuration.Id, policy.Id, day, time, PaletteIndex(index), role, policy.Enabled,
                            winner is not null, offset, tip));
                    }
                }
            }
        }
        Timeline.ActivePaletteIndex = paletteIndex;
        Timeline.ActiveDays = (active?.Days ?? []).ToHashSet();
        Timeline.Selected = _selected;
        Timeline.Clock = Now;
        Timeline.Pins = pins;
        KeyCurrent.SetResourceReference(Shape.FillProperty, ScheduleTimeline.PaletteKeys[paletteIndex]);
    }

    private void RenderPolicyBar(TaskConfiguration focus, SchedulePolicy? active, string brushKey)
    {
        PolicyBar.Children.Clear();
        var name = new WpfTextBlock {
            Text = focus.Name, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };
        name.SetResourceReference(WpfTextBlock.ForegroundProperty, "Brush.Text.Primary");
        PolicyBar.Children.Add(name);
        foreach (var policy in focus.SchedulePolicies) {
            var content = new StackPanel { Orientation = WpfOrientation.Horizontal };
            var dot = new Ellipse {
                Width = 10, Height = 10, VerticalAlignment = VerticalAlignment.Center,
                StrokeThickness = 2
            };
            if (policy.Enabled) {
                dot.SetResourceReference(Shape.FillProperty, brushKey);
            } else {
                dot.Fill = WpfBrushes.Transparent;
                dot.SetResourceReference(Shape.StrokeProperty, "Brush.Border.Strong");
            }
            content.Children.Add(dot);
            var text = new StackPanel { Margin = new Thickness(8, 0, 0, 0), MaxWidth = 220 };
            text.Children.Add(new WpfTextBlock { Text = policy.Name, TextTrimming = TextTrimming.CharacterEllipsis });
            var summary = new WpfTextBlock {
                Text = SchedulePreview.ShortSummary(policy), FontSize = 11, FontWeight = FontWeights.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            summary.SetResourceReference(WpfTextBlock.ForegroundProperty, "Brush.Text.Muted");
            text.Children.Add(summary);
            content.Children.Add(text);
            var isActive = policy == active;
            var chip = new WpfButton {
                Content = content, Height = 42, Padding = new Thickness(10, 0, 12, 0),
                Margin = new Thickness(0, 0, 6, 0), ToolTip = SchedulePreview.Summary(policy),
                Style = (Style)FindResource("SubtleButtonStyle"),
                BorderThickness = new Thickness(isActive ? 2 : 1), FontWeight = isActive ? FontWeights.SemiBold
                    : FontWeights.Normal
            };
            chip.SetResourceReference(WpfControl.BackgroundProperty, "Brush.Surface");
            chip.SetResourceReference(WpfControl.BorderBrushProperty, isActive ? brushKey : "Brush.Border");
            AutomationProperties.SetName(chip, $"{policy.Name}，{SchedulePreview.Summary(policy)}");
            // The short summary hides times, so the full list opens after the system hover time.
            ToolTipService.SetInitialShowDelay(chip, (int)SystemParameters.MouseHoverTime.TotalMilliseconds);
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
        add.Click += (_, _) => AddPolicy(focus, "定时 " + (focus.SchedulePolicies.Count + 1));
        PolicyBar.Children.Add(add);
    }

    private void RenderInspector(TaskConfiguration focus, SchedulePolicy? policy, string brushKey)
    {
        Inspector.Visibility = policy is null ? Visibility.Collapsed : Visibility.Visible;
        EmptyHint.Visibility = policy is null ? Visibility.Visible : Visibility.Collapsed;
        if (policy is null) {
            return;
        }
        Inspector.SetResourceReference(Border.BorderBrushProperty, brushKey);
        PolicyEnabled.IsChecked = policy.Enabled;
        if (!PolicyName.IsKeyboardFocusWithin) {
            PolicyName.Text = policy.Name;
        }
        var days = policy.Days.ToHashSet();
        PresetWeekdays.IsChecked = days.SetEquals(SchedulePreview.WeekOrder[..5]);
        PresetWeekend.IsChecked = days.SetEquals(SchedulePreview.WeekOrder[5..]);
        PresetEveryDay.IsChecked = days.Count == 7;
        RenderDays(policy, brushKey);
        // While the editor has focus its chip stays as it is; leaving it commits and renders again. Everything that
        // selects another time takes focus first, so this only skips the editor's own changes and the clock tick.
        if (!_timeEditor.IsKeyboardFocusWithin || _selected is not { } editing || editing.PolicyId != policy.Id
            || !policy.Times.Contains(editing.Time)) {
            RenderTimes(policy, brushKey);
        }
        var rest = SchedulePreview.WeekOrder.Where(d => !days.Contains(d)).ToArray();
        ShareHint.Visibility = _shareHintClosed ? Visibility.Collapsed : Visibility.Visible;
        SplitRestButton.Visibility = rest.Length == 0 || policy.Times.Count == 0 ? Visibility.Collapsed
            : Visibility.Visible;
        var splitLabel = new WpfTextBlock {
            Text = $"为{SchedulePreview.DaysText(rest)}新建定时"
        };
        splitLabel.SetResourceReference(WpfTextBlock.ForegroundProperty, "Brush.Primary");
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
    private void RenderDays(SchedulePolicy policy, string brushKey)
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
            if (chip.IsChecked == true) {
                chip.SetResourceReference(WpfControl.BackgroundProperty, brushKey);
                chip.SetResourceReference(WpfControl.BorderBrushProperty, brushKey);
                chip.SetResourceReference(WpfControl.ForegroundProperty, "Brush.Text.Inverse");
            } else {
                chip.ClearValue(BackgroundProperty);
                chip.ClearValue(BorderBrushProperty);
                chip.ClearValue(ForegroundProperty);
            }
        }
    }

    private void RenderTimes(SchedulePolicy policy, string brushKey)
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
                // Same width as the editor, so selecting a chip does not change its length.
                // Colours are set on the text itself: the app-wide TextBlock style ignores inherited ones.
                var label = new WpfTextBlock {
                    Text = SchedulePreview.TimeText(time), Width = 46, Padding = new Thickness(2, 0, 2, 0),
                    FontSize = 14, FontWeight = FontWeights.Bold,
                    VerticalAlignment = VerticalAlignment.Center
                };
                label.SetResourceReference(WpfTextBlock.ForegroundProperty, brushKey);
                row.Children.Add(label);
            }
            var dismiss = new SymbolIcon { Symbol = SymbolRegular.Dismiss12, FontSize = 9 };
            dismiss.SetResourceReference(WpfControl.ForegroundProperty,
                selected ? "Brush.Text.Inverse" : "Brush.Text.Muted");
            var remove = new WpfButton {
                Style = (Style)FindResource("Schedule.ChipRemove"), Margin = new Thickness(2, 0, 0, 0), ToolTip = "删除",
                Content = dismiss
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
                Child = row, Height = 30, CornerRadius = new CornerRadius(15), Padding = new Thickness(12, 0, 5, 0),
                Margin = new Thickness(0, 0, 6, 6), BorderThickness = new Thickness(1),
                Cursor = selected ? System.Windows.Input.Cursors.IBeam : System.Windows.Input.Cursors.Hand
            };
            chip.SetResourceReference(TextElement.ForegroundProperty, selected ? "Brush.Text.Inverse" : brushKey);
            if (selected) {
                chip.SetResourceReference(Border.BackgroundProperty, brushKey);
                chip.SetResourceReference(Border.BorderBrushProperty, brushKey);
            } else {
                BindTint(chip, Border.BackgroundProperty, 0x1A);
                BindTint(chip, Border.BorderBrushProperty, 0x70);
                chip.MouseLeftButtonUp += (_, _) => SelectTime(policy.Id, time);
            }
            TimeChips.Children.Add(chip);
        }
        var add = new WpfButton {
            Content = "+ 时间", Style = (Style)FindResource("Schedule.AddTime")
        };
        add.SetResourceReference(WpfControl.BorderBrushProperty, brushKey);
        add.SetResourceReference(WpfControl.ForegroundProperty, brushKey);
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

        static void BindTint(Border chip, DependencyProperty property, byte alpha)
        {
            chip.SetBinding(property, new System.Windows.Data.Binding {
                Source = chip, Path = new PropertyPath("(0)", TextElement.ForegroundProperty),
                Converter = ChipTint.Instance, ConverterParameter = alpha
            });
        }
    }

    private sealed class ChipTint : System.Windows.Data.IValueConverter
    {
        internal static readonly ChipTint Instance = new();

        public object Convert(object value, Type type, object parameter, System.Globalization.CultureInfo culture)
        {
            var color = ((SolidColorBrush)value).Color;
            return new SolidColorBrush(WpfColor.FromArgb((byte)parameter, color.R, color.G, color.B));
        }

        public object ConvertBack(object value, Type type, object parameter, System.Globalization.CultureInfo culture)
            => throw new NotSupportedException();
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
        OpenScopeMenu($"添加 {SchedulePreview.TimeText(time)}", time,
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
        OpenScopeMenu($"删除 {SchedulePreview.TimeText(pin.Time)}", pin.Time,
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

    private void OpenScopeMenu(string title, TimeOnly time, params ScheduleScopeOption[] options)
    {
        var itemStyle = (Style)FindResource("Schedule.ScopeItem");
        var menu = new ContextMenu {
            Style = (Style)FindResource("FloatingContextMenuStyle"), PlacementTarget = Timeline,
            Placement = PlacementMode.MousePoint
        };
        var heading = new MenuItem {
            Header = title, IsEnabled = false, Style = itemStyle, FontSize = 11.5, FontWeight = FontWeights.Normal,
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
        menu.Closed += (_, _) => Timeline.Preview = null;
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

    private void StatusCapsule_Click(object sender, RoutedEventArgs e) => HomeRequested?.Invoke(this, EventArgs.Empty);
}
