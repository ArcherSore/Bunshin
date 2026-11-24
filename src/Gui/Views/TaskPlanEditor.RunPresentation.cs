using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Shapes;
using ProjectModel;
using Protocol;
using WpfButton = System.Windows.Controls.Button;
using WpfSymbolRegular = Wpf.Ui.Controls.SymbolRegular;

namespace Gui.Views;

public sealed partial class TaskPlanEditor
{
    private RunProgressView? _shownRun;
    private Guid? _shownRunConfiguration;
    private (PlanItemKind Kind, string Name)? _expandedRunTask;
    private bool _runInspectorShown;
    private readonly List<(TextBlock Text, PlanItemProgressView Item)> _runTimers = [];

    internal void ApplyRunPresentation(RuntimeControlState state, RunProgressView? progress, string? startBlocker)
    {
        var stopping = progress is { Active: true }
            || state.Action is RuntimeAction.Stop or RuntimeAction.InProgress { Kind: ProgressKind.StoppingRun };
        var projectLoaded = Project is not null;
        PlanActions.Visibility = projectLoaded || stopping ? Visibility.Visible : Visibility.Collapsed;
        TaskShelfHeaderButton.Visibility = projectLoaded ? Visibility.Visible : Visibility.Collapsed;
        StartPlanButton.Visibility = stopping ? Visibility.Collapsed : Visibility.Visible;
        StopPlanButton.Visibility = stopping ? Visibility.Visible : Visibility.Collapsed;
        StartPlanButton.IsEnabled = state.Action is RuntimeAction.Start { Enabled: true }
            or RuntimeAction.Retry { Enabled: true }
            || state.Action is RuntimeAction.Prepare { Enabled: true }
                && Project?.EnabledItems.Count > 0 && startBlocker is null;
        StopPlanButton.IsEnabled = state.Action is RuntimeAction.Stop { Enabled: true };
        StartPlanButton.ToolTip = startBlocker;
        AutomationProperties.SetHelpText(StartPlanButton, startBlocker ?? string.Empty);
        ApplyRunProgress(progress);
    }

    internal void ApplyRunProgress(RunProgressView? progress)
    {
        var run = progress is { Active: true } ? progress : null;
        var project = Project;
        if (run is null) {
            if (_shownRun is not null) {
                _shownRun = null;
                RunItemsPanel.Visibility = Visibility.Collapsed;
                RunRows.Children.Clear();
                _runTimers.Clear();
                var restoreInspector = _runInspectorShown;
                _expandedRunTask = null;
                _runInspectorShown = false;
                PlanScroll.Visibility = Visibility.Visible;
                if (project is not null) {
                    if (restoreInspector) {
                        RenderPlanItems(project);
                    } else {
                        UpdateIdlePlanCount(project);
                    }
                }
            }
            return;
        }

        PlanScroll.Visibility = Visibility.Collapsed;
        RunItemsPanel.Visibility = Visibility.Visible;
        PlanCountText.Text = $"{run.Processed} / {run.Total} 项完成";
        if (_shownRun is null || _shownRunConfiguration != project?.ActiveConfigurationId
            || !_shownRun.Items.SequenceEqual(run.Items)) {
            RenderRunRows(project, run);
        }
        _shownRun = run;
        _shownRunConfiguration = project?.ActiveConfigurationId;

        var now = DateTime.UtcNow;
        foreach (var (text, item) in _runTimers) {
            var elapsed = RunProgress.Elapsed(item.StartedAtUtc, item.EndedAtUtc, now);
            text.Text = RunItemStatusText(item.State, elapsed);
        }
    }

    private void UpdateIdlePlanCount(ProjectPlanModule project)
    {
        if (_shownRun is null) {
            PlanCountText.Text = $"{project.SelectedItems.Count(task => project.IsItemEnabled(task.Kind, task.Name))}"
                + $" / {project.SelectedItems.Count} 项";
        }
    }

    private void RenderRunRows(ProjectPlanModule? project, RunProgressView run)
    {
        RunRows.Children.Clear();
        _runTimers.Clear();

        var selected = project?.SelectedItems ?? [];
        var byTask = run.Items.ToDictionary(item => (item.Kind, item.TaskName));
        var configurationItems = selected.Select(task => (task.Kind, task.Name)).ToHashSet();

        // Keep unchecked rows in their configured positions. A Run reattached from an earlier configuration can
        // contain other tasks, so include every item of its snapshot even then.
        foreach (var task in selected) {
            var item = byTask.GetValueOrDefault((task.Kind, task.Name));
            // Items absent from the submitted plan are considered skipped, even if they were checked.
            RunRows.Children.Add(CreateRunRow(task.Label, task.Kind, item, task));
        }
        foreach (var item in run.Items.Where(item => !configurationItems.Contains((item.Kind, item.TaskName)))) {
            RunRows.Children.Add(CreateRunRow(item.TaskLabel, item.Kind, item, null));
        }
    }

    private Border CreateRunRow(string label, PlanItemKind kind, PlanItemProgressView? item, ProjectTaskChoice? task)
    {
        var state = item?.State ?? PlanItemProgress.NotRun;
        var endText = new TextBlock { FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        endText.SetResourceReference(TextBlock.ForegroundProperty, state switch {
            PlanItemProgress.Running => "Brush.Plan.Progress",
            PlanItemProgress.Failed => "Brush.Error.Foreground",
            PlanItemProgress.NotRun => "Brush.Text.Disabled",
            _ => "Brush.Text.Muted"
        });
        endText.Text = RunItemStatusText(state, RunProgress.Elapsed(item?.StartedAtUtc, item?.EndedAtUtc, DateTime.UtcNow));
        if (item is { State: PlanItemProgress.Running }) {
            _runTimers.Add((endText, item));
        }

        var row = new DockPanel { Margin = new Thickness(8, 0, 12, 0), LastChildFill = true };
        var icon = CreateRunIcon(state);
        icon.Margin = new Thickness(0, 0, 12, 0);
        row.Children.Add(icon);
        DockPanel.SetDock(endText, Dock.Right);
        row.Children.Add(endText);
        var name = CreatePlanRowTitle(label, kind);
        if (state is PlanItemProgress.Pending or PlanItemProgress.NotRun) {
            var text = (TextBlock)((DockPanel)name).Children[0];
            text.SetResourceReference(TextBlock.ForegroundProperty,
                state == PlanItemProgress.Pending ? "Brush.Text.Muted" : "Brush.Text.Disabled");
        }
        row.Children.Add(name);

        if (state == PlanItemProgress.Running && task is not null) {
            var preview = new StackPanel {
                Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0), ClipToBounds = true
            };
            FillParameterPreview(preview, GetConfigurationOrEmpty(task.Name, task.Kind));
            row.Children.Add(preview);
        } else {
            row.LastChildFill = false;
        }
        var card = new Border {
            MinHeight = 40, Margin = new Thickness(0, 0, 0, 2), CornerRadius = new CornerRadius(8), Child = row
        };
        if (state == PlanItemProgress.Running) {
            card.SetResourceReference(Border.BackgroundProperty, "Brush.Plan.Current");
        }
        var accessibleName = $"{label}，{AccessibleRunItemStatus(state)}";
        AutomationProperties.SetName(card, accessibleName);
        if (task is not null) {
            AttachRunItemDetails(card, row, task, accessibleName);
        }
        return card;
    }

    private void AttachRunItemDetails(Border card, DockPanel row, ProjectTaskChoice task, string accessibleName)
    {
        var configuration = GetConfigurationOrEmpty(task.Name, task.Kind);
        var hasParameters = HasParameters(configuration);
        if (!hasParameters && string.IsNullOrWhiteSpace(task.Description)) {
            return;
        }

        var key = (task.Kind, task.Name);
        var expanded = _expandedRunTask == key;
        card.Child = null;
        var header = new WpfButton {
            Padding = new Thickness(0), Content = row,
            Style = (Style)FindResource("PlanHeaderButtonStyle"), Tag = task
        };
        AutomationProperties.SetName(header, accessibleName);
        AutomationProperties.SetHelpText(header, hasParameters ? (expanded ? "收起参数" : "查看参数") : "查看任务说明");
        header.Click += (_, _) => {
            if (!hasParameters) {
                TaskDescriptionRequested?.Invoke(this, task);
            } else if (_shownRun is { } run) {
                _expandedRunTask = expanded ? null : key;
                RenderRunRows(Project, run);
                var replacement = RunRows.Children.OfType<Border>()
                    .Select(border => (border.Child as StackPanel)?.Children[0])
                    .OfType<WpfButton>().FirstOrDefault(button => Equals(button.Tag, task));
                replacement?.Focus();
            }
        };
        var body = new StackPanel { Children = { header } };
        card.Child = body;
        if (!expanded) {
            return;
        }

        card.SetResourceReference(Border.BackgroundProperty, "Brush.Plan.Expanded");
        _runInspectorShown = true;
        _optionInputEditors.Clear();
        _inspectorRows.Clear();
        var inspector = CreateParameterInspector(task.Name, configuration, task.Kind);
        inspector.IsEnabled = false;
        inspector.Margin = new Thickness(36, 2, 12, 12);
        body.Children.Add(inspector);
        if (!string.IsNullOrWhiteSpace(task.Description)) {
            var information = CreateIconButton(WpfSymbolRegular.Info16, $"查看 {task.Label} 的任务描述", "查看任务说明");
            information.Tag = task;
            information.Margin = new Thickness(0, 0, 12, 8);
            information.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
            information.Click += TaskDescriptionButton_Click;
            body.Children.Add(information);
        }
    }

    private FrameworkElement CreateRunIcon(PlanItemProgress state)
    {
        if (state == PlanItemProgress.Running) {
            var ring = new Wpf.Ui.Controls.ProgressRing {
                Width = 14, Height = 14, IsIndeterminate = true, VerticalAlignment = VerticalAlignment.Center
            };
            ring.SetResourceReference(ForegroundProperty, "Brush.Primary");
            return ring;
        }
        if (state == PlanItemProgress.Pending) {
            var circle = new Ellipse {
                Width = 12, Height = 12, StrokeThickness = 1.3, VerticalAlignment = VerticalAlignment.Center
            };
            circle.SetResourceReference(Shape.StrokeProperty, "Brush.Text.Disabled");
            return new Grid { Width = 14, Children = { circle } };
        }
        var symbol = CreateSymbolIcon(state switch {
            PlanItemProgress.Succeeded => WpfSymbolRegular.CheckmarkCircle16,
            PlanItemProgress.Failed => WpfSymbolRegular.ErrorCircle16,
            _ => WpfSymbolRegular.Subtract16
        });
        symbol.Width = symbol.Height = symbol.FontSize = 14;
        symbol.VerticalAlignment = VerticalAlignment.Center;
        symbol.SetResourceReference(ForegroundProperty, state switch {
            PlanItemProgress.Succeeded => "Brush.Success",
            PlanItemProgress.Failed => "Brush.Error",
            _ => "Brush.Text.Disabled"
        });
        return symbol;
    }

    private static DockPanel CreatePlanRowTitle(string label, PlanItemKind kind, bool expanded = false)
    {
        var title = new DockPanel { LastChildFill = false };
        var name = new TextBlock {
            Text = label, MaxWidth = 260, FontWeight = expanded ? FontWeights.Medium : FontWeights.Normal,
            FontSize = 14, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis
        };
        title.Children.Add(name);
        if (kind == PlanItemKind.Pretask) {
            var tag = new TextBlock { Text = "前置", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            tag.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text.Muted");
            var badge = new Border {
                Height = 18, CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 0, 6, 0),
                Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Child = tag
            };
            badge.SetResourceReference(Border.BackgroundProperty, "Brush.Plan.Tag");
            title.Children.Add(badge);
        }
        return title;
    }

    private static string RunItemStatusText(PlanItemProgress state, TimeSpan? elapsed)
    {
        var duration = elapsed is { } time ? RunProgress.FormatDuration(time) : string.Empty;
        if (duration.StartsWith('0')) {
            duration = duration[1..];
        }
        return state switch {
            PlanItemProgress.Succeeded => duration.Length == 0 ? "已完成" : duration,
            PlanItemProgress.Pending => string.Empty,
            _ => AccessibleRunItemStatus(state) + (duration.Length == 0 ? string.Empty : $" · {duration}")
        };
    }

    private static string AccessibleRunItemStatus(PlanItemProgress state) => state switch {
        PlanItemProgress.Succeeded => "已完成",
        PlanItemProgress.Running => "进行中",
        PlanItemProgress.Pending => "待执行",
        PlanItemProgress.Failed => "失败",
        PlanItemProgress.Stopped => "已停止",
        _ => "将跳过"
    };
}
