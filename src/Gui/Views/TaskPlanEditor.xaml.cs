using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Gui.Infrastructure;
using ProjectModel;
using Protocol;
using WpfButton = System.Windows.Controls.Button;
using WpfCursors = System.Windows.Input.Cursors;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfTextBox = System.Windows.Controls.TextBox;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfSymbolIcon = Wpf.Ui.Controls.SymbolIcon;
using WpfSymbolRegular = Wpf.Ui.Controls.SymbolRegular;

namespace Gui.Views;

// The task plan editor: plan item cards with their parameter inspectors, the task shelf and
// drag-and-drop ordering. It edits the ProjectPlanModule the window supplies and knows nothing of the Runtime
// Environment; the window decides when editing is allowed and settles what an edit means for the next Run.
public sealed partial class TaskPlanEditor : System.Windows.Controls.UserControl
{
    private AppLogger _logger = null!;
    private Func<ProjectPlanModule?> _project = () => null;
    private Func<bool> _editable = () => false;
    private Action<string, Exception> _editFailed = (_, _) => { };
    private Action<Uri> _openDescriptionLink = _ => { };
    private bool _updatingOptionEditors;
    private bool _taskShelfExpanded;
    // Labels of the tasks added since the add-task panel was last opened.
    private readonly List<string> _addedSinceShelfOpened = [];
    private readonly Dictionary<string, bool> _taskGroupExpanded = new(StringComparer.Ordinal);
    private (PlanItemKind Kind, string Name)? _expandedTask;
    private readonly Dictionary<(PlanItemKind Kind, string Name), Border> _planItemContainers = [];
    private readonly List<(StackPanel Preview, Border Container, ProjectTaskChoice Task)> _planItemPreviews = [];

    private readonly Dictionary<(Guid, string, string), InputDraft> _invalidInputDrafts = new();
    private readonly List<InspectorRowView> _inspectorRows = [];
    private System.Windows.Data.Binding? _configurationEditableBinding;
    private static readonly string[] SwitchOnCaseNames = ["Yes", "Y", "On", "True", "Enable", "Enabled"];

    // Edit controls bind IsEnabled to this, so a run lock blocks edits while scrolling, expanding and help stay usable.
    private static readonly DependencyProperty ConfigurationEditableProperty = DependencyProperty.Register(
        "ConfigurationEditable", typeof(bool), typeof(TaskPlanEditor), new PropertyMetadata(true));

    private sealed record OptionInputTag(Guid ConfigurationId, string OptionName, string InputName, string Value,
        string? PatternMessage, bool Submitted = false)
    {
        internal (Guid, string, string) Key => (ConfigurationId, OptionName, InputName);
    }

    // What a replayed tour changes and puts back: the expanded card, the task shelf and the plan's scroll position.
    internal sealed record PlanView((PlanItemKind Kind, string Name)? ExpandedTask, bool TaskShelfExpanded,
        double Scroll);

    public TaskPlanEditor()
    {
        InitializeComponent();
        _planDragScroll.Tick += (_, _) => ScrollWhileDraggingPlanItem();
        BindConfigurationEditable(TaskShelfHeaderButton);
        BindConfigurationEditable(EmptyPlanAddButton);
    }

    // An edit changed the plan or a value, so a Run attempt prepared before it no longer matches.
    internal event EventHandler? PlanEdited;

    // Raised after every validation of the active configuration, with whether it passed.
    internal event EventHandler<bool>? ConfigurationValidated;

    // Raised after configuration edits and project renders so the shell can present the saved configurations.
    internal event EventHandler<ProjectPlanModule>? ConfigurationsRendered;

    internal event RoutedEventHandler? StartRequested;
    internal event RoutedEventHandler? StopRequested;

    // The window owns the project and the runtime state. Editable and the lock are read fresh on every check, so a
    // click never acts on a lock the window no longer shows; editFailed reports an edit that could not be saved.
    internal void Attach(AppLogger logger, Func<ProjectPlanModule?> project, Func<bool> editable,
        Action<string, Exception> editFailed, Action<Uri> openDescriptionLink)
    {
        _logger = logger;
        _project = project;
        _editable = editable;
        _editFailed = editFailed;
        _openDescriptionLink = openDescriptionLink;
    }

    private ProjectPlanModule? Project => _project();

    private bool Editable => _editable();

    private ProjectPlanModule RequireProject() =>
        Project ?? throw new InvalidOperationException("项目尚未加载。");

    // Preparing the environment reloads the project; an unfixed invalid draft must survive it and keep blocking
    // Start. Drafts for inputs the reloaded PI no longer has are never rendered or matched.
    internal void ShowProject((PlanItemKind Kind, string Name)? expandedTask)
    {
        _expandedTask = expandedTask;
        ProjectEmptyStatePanel.Visibility = Visibility.Collapsed;
        TaskWorkspacePanel.Visibility = Visibility.Visible;
        PlanActions.Visibility = Visibility.Visible;

        RenderTaskPlan();
    }

    internal void ShowProjectUnavailable(string title, string detail)
    {
        _invalidInputDrafts.Clear();
        _shownConfigurationId = null;
        _expandedTask = null;
        AvailableTasksPanel.Children.Clear();
        PlanItemsPanel.Children.Clear();
        ProjectEmptyStateTitleText.Text = title;
        ProjectEmptyStateDetailText.Text = detail;
        TaskWorkspacePanel.Visibility = Visibility.Collapsed;
        PlanActions.Visibility = Visibility.Collapsed;
        PlanCountText.Text = string.Empty;
        ProjectEmptyStatePanel.Visibility = Visibility.Visible;
    }

    internal void ApplyConfigurationLock(bool editable, LockReason? reason)
    {
        SetValue(ConfigurationEditableProperty, editable);
        if (!editable && _taskShelfExpanded) {
            SetTaskShelfExpanded(false);
        }
        ConfigurationLockBadge.Visibility = reason is not null ? Visibility.Visible : Visibility.Collapsed;
        var message = reason == LockReason.RunActive
            ? "任务运行中，配置已锁定" : "运行环境处理中，配置暂时锁定";
        ConfigurationLockBadge.ToolTip = message;
        AutomationProperties.SetName(ConfigurationLockBadge, message);
    }

    private void StartPlanButton_Click(object sender, RoutedEventArgs e) => StartRequested?.Invoke(sender, e);

    private void StopPlanButton_Click(object sender, RoutedEventArgs e) => StopRequested?.Invoke(sender, e);

    // The messages of one configuration's unfixed invalid drafts, by option and input.
    internal IReadOnlyDictionary<(string Option, string Input), string> InvalidInputs(Guid configurationId) =>
        _invalidInputDrafts.Where(pair => pair.Key.Item1 == configurationId)
            .ToDictionary(pair => (pair.Key.Item2, pair.Key.Item3), pair => pair.Value.Message);

    // Esc first clears the search, then closes the panel; with the panel closed it is left to others.
    internal bool DismissTaskShelf()
    {
        if (!_taskShelfExpanded) {
            return false;
        }
        if (TaskSearchBox.Text.Length != 0) {
            TaskSearchBox.Clear();
        } else {
            SetTaskShelfExpanded(false);
            // Focus is not handed to the header button: it would show a keyboard focus ring there.
            if (TaskShelfContent.IsKeyboardFocusWithin) {
                Keyboard.ClearFocus();
            }
        }
        return true;
    }

    // A click anywhere but the panel and the buttons that toggle it closes the panel; the click itself goes on.
    internal void DismissTaskShelfOnClick()
    {
        if (_taskShelfExpanded && !TaskShelfContent.IsMouseOver && !TaskShelfHeaderButton.IsMouseOver
            && !EmptyPlanAddButton.IsMouseOver) {
            SetTaskShelfExpanded(false);
        }
    }

    // The add button the user sees: the plan's own while it is empty, the header's once it has items.
    internal FrameworkElement AddTaskButton =>
        TaskShelfHeaderButton.Visibility == Visibility.Visible ? TaskShelfHeaderButton : EmptyPlanAddButton;

    // The header button takes over from the empty plan's own button only once the plan has items.
    private void UpdateAddTaskButton() => TaskShelfHeaderButton.Visibility =
        Project is { SelectedItems.Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;

    // What the onboarding tour points at, opens and puts back.
    internal FrameworkElement Workspace => TaskWorkspacePanel;

    internal FrameworkElement TaskShelf => TaskShelfContent;

    internal double PlanViewportHeight => PlanScroll.ViewportHeight;

    internal Border? PlanItemCard(ProjectTaskChoice task) =>
        _planItemContainers.GetValueOrDefault((task.Kind, task.Name));

    internal ProjectTaskChoice? TaskForOnboarding()
    {
        var tasks = Project?.SelectedItems ?? [];
        return tasks.FirstOrDefault(task => !string.IsNullOrWhiteSpace(task.Description)
            && HasParameters(GetConfigurationOrEmpty(task.Name, task.Kind)))
            ?? tasks.FirstOrDefault(task => !string.IsNullOrWhiteSpace(task.Description))
            ?? tasks.FirstOrDefault(task => HasParameters(GetConfigurationOrEmpty(task.Name, task.Kind)))
            ?? tasks.FirstOrDefault();
    }

    internal void ShowTaskShelfForOnboarding(bool show)
    {
        SetTaskShelfExpanded(show);
        if (show) {
            AvailableTasksScroll.ScrollToTop();
        }
    }

    internal void ExpandPlanItem(ProjectTaskChoice task)
    {
        if (!IsExpanded(task)) {
            _expandedTask = (task.Kind, task.Name);
            RenderTaskPlan();
        }
    }

    internal PlanView CaptureView() => new(_expandedTask, _taskShelfExpanded, PlanScroll.VerticalOffset);

    // A card removed in the meantime stays folded.
    internal void RestoreView(PlanView view)
    {
        _expandedTask = Project?.SelectedItems
            .Any(item => (item.Kind, item.Name) == view.ExpandedTask) == true ? view.ExpandedTask : null;
        SetTaskShelfExpanded(view.TaskShelfExpanded);
        if (Project is not null) {
            RenderTaskPlan();
        }
        PlanScroll.ScrollToVerticalOffset(view.Scroll);
    }
    private void TaskShelfHeaderButton_Click(object sender, RoutedEventArgs e)
    {
        SetTaskShelfExpanded(!_taskShelfExpanded);
        if (_taskShelfExpanded) {
            TaskSearchBox.Focus();
        }
    }

    // The panel stays open while several tasks are added; each opening or configuration counts its additions afresh.
    private void SetTaskShelfExpanded(bool expanded)
    {
        if (!expanded) {
            TaskSearchBox.Clear();
        } else if (!_taskShelfExpanded) {
            _addedSinceShelfOpened.Clear();
            UpdateAddedTasks();
        }
        _taskShelfExpanded = expanded;
        TaskShelfContent.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        if (expanded) {
            TaskShelfHeaderButton.SetResourceReference(BackgroundProperty, "Brush.Plan.Current");
        } else {
            TaskShelfHeaderButton.ClearValue(BackgroundProperty);
        }
        AutomationProperties.SetHelpText(TaskShelfHeaderButton, expanded ? "可用任务已展开" : "可用任务已收起");
        AutomationProperties.SetHelpText(EmptyPlanAddButton, expanded ? "可用任务已展开" : "可用任务已收起");
    }

    private void UpdateAddedTasks()
    {
        AddedTasksBadge.Visibility = _addedSinceShelfOpened.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        TaskShelfHint.Visibility = _addedSinceShelfOpened.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AddedTasksText.Text = $"已添加 {_addedSinceShelfOpened.Count} 项";
        AddedTasksBadge.ToolTip = string.Join("、", _addedSinceShelfOpened);
    }

    private void AddTaskButton_Click(object sender, RoutedEventArgs e)
    {
        if (!Editable || sender is not WpfButton { Tag: ProjectTaskChoice task }) {
            return;
        }
        try {
            var project = RequireProject();
            if (!project.AddItem(task.Kind, task.Name)) {
                return;
            }
            PlanEdited?.Invoke(this, EventArgs.Empty);
            _addedSinceShelfOpened.Add(task.Label);
            UpdateAddedTasks();
            RenderTaskPlan();
            // The plan keeps its shape: the new card is shown and highlighted rather than expanded.
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => LocatePlanItem(task));
            _logger.Info($"已添加执行计划任务：{task.Name}。 ");
        } catch (Exception exception) {
            HandleProjectEditError("添加执行计划任务失败", exception);
        }
    }

    private void LocatePlanItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is WpfButton { Tag: ProjectTaskChoice task }) {
            LocatePlanItem(task);
        }
    }

    private void LocatePlanItem(ProjectTaskChoice task)
    {
        if (_planItemContainers.TryGetValue((task.Kind, task.Name), out var card)) {
            BringIntoPlanView(card);
            FlashPlanItem(card);
        }
    }

    // Pads by the edge fade so the target never stops under it, and caps a tall card so its header stays visible.
    private void BringIntoPlanView(FrameworkElement element)
    {
        var fade = (double)FindResource("Height.ScrollFade");
        var height = Math.Min(element.ActualHeight, Math.Max(0, PlanScroll.ViewportHeight - fade * 2));
        element.BringIntoView(new Rect(0, -fade, element.ActualWidth, height + fade * 2));
    }

    // Tints the located card with the task accent for a moment, then fades back to the colours of its style.
    private void FlashPlanItem(Border card)
    {
        if (!SystemParameters.ClientAreaAnimation) {
            return;
        }
        var layout = (Grid)card.Child;
        var edge = card.BorderThickness;
        var highlight = new Border {
            BorderThickness = new Thickness(1), CornerRadius = card.CornerRadius, IsHitTestVisible = false,
            Margin = new Thickness(-edge.Left, -edge.Top, -edge.Right, -edge.Bottom)
        };
        highlight.SetResourceReference(Border.BorderBrushProperty, "Brush.Primary.Border");
        highlight.SetResourceReference(Border.BackgroundProperty, "Brush.Primary.Surface");
        Grid.SetRowSpan(highlight, layout.RowDefinitions.Count);
        layout.Children.Insert(0, highlight);
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(600)) {
            BeginTime = TimeSpan.FromMilliseconds(700)
        };
        fade.Completed += (_, _) => layout.Children.Remove(highlight);
        highlight.BeginAnimation(OpacityProperty, fade);
    }

    // A rejected value kept as a visible draft is left to RevealInvalidParameter instead of silently blocking Start.
    internal bool CommitRunInput()
    {
        var tag = (Keyboard.FocusedElement as WpfTextBox)?.Tag as OptionInputTag;
        return CommitFocusedConfigurationInput() || tag is not null && _invalidInputDrafts.ContainsKey(tag.Key);
    }

    internal bool RevealInvalidParameter()
    {
        if (Project is not { } project) {
            return false;
        }
        var match = project.EnabledItems.OrderBy(task => IsExpanded(task) ? 0 : 1)
            .SelectMany(task => ParameterInspector.Flatten(DeriveInspectorRows(GetConfigurationOrEmpty(task.Name, task.Kind)))
                .Where(row => row.IsInvalid).Select(row => (Task: task, Row: row)))
            .FirstOrDefault();
        if (match.Task is null) {
            return false;
        }
        var option = match.Row.Option.Name;
        var field = match.Row.Inputs.FirstOrDefault(input => input.Error is not null);
        FrameworkElement? Target()
        {
            var view = InspectorViews(_inspectorRows).FirstOrDefault(row => row.OptionName == option);
            return field is null ? view?.Selector ?? (FrameworkElement?)view?.Row
                : view?.Inputs.GetValueOrDefault(field.Input.Name)?.Editor;
        }
        if (!IsExpanded(match.Task) || Target() is null) {
            _expandedTask = (match.Task.Kind, match.Task.Name);
            RenderTaskPlan();
        }
        if (Target() is { } target) {
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => {
                BringIntoPlanView(target);
                target.Focus();
            });
        }
        _logger.Info($"参数 {option} 未通过校验，已阻止开始任务。 ");
        return true;
    }

    // The header is the only expansion toggle; a task without parameters has nothing to expand.
    private void PlanItemHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { Tag: ProjectTaskChoice task }
            || !IsExpanded(task) && !HasDetails(task, GetConfigurationOrEmpty(task.Name, task.Kind))) {
            return;
        }
        _expandedTask = IsExpanded(task) ? null : (task.Kind, task.Name);
        RenderTaskPlan();
    }

    private void RemovePlanItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (!Editable || sender is not WpfButton { Tag: ProjectTaskChoice task }) {
            return;
        }
        try {
            var project = RequireProject();
            var removed = project.RemoveItem(task.Kind, task.Name);
            if (!removed) {
                return;
            }
            if (IsExpanded(task)) {
                _expandedTask = null;
            }
            PlanEdited?.Invoke(this, EventArgs.Empty);
            RenderTaskPlan();
            _logger.Info($"已从执行计划移除任务：{task.Name}。 ");
        } catch (Exception exception) {
            HandleProjectEditError("移除执行计划任务失败", exception);
        }
    }

    private void OptionInputTextBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is WpfTextBox editor
            && (e.NewFocus is not DependencyObject focused || !editor.IsAncestorOf(focused))) {
            _ = CommitOptionInput(editor);
        }
    }

    // Enter submits like losing focus; Esc abandons an unsaved or invalid edit and puts the saved value back.
    private void OptionInputTextBox_PreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.OriginalSource is WpfButton) {
            return;
        }
        if (e.Key == Key.Enter && sender is WpfTextBox { Tag: OptionInputTag } submitted) {
            _ = CommitOptionInput(submitted);
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Escape || sender is not WpfTextBox { Tag: OptionInputTag tag } editor
            || GetInputText(editor) == tag.Value && !_invalidInputDrafts.ContainsKey(tag.Key)) {
            return;
        }
        _invalidInputDrafts.Remove(tag.Key);
        SetInputText(editor, tag.Value);
        editor.SelectAll();
        ShowOptionInputError(editor, null);
        RefreshParameterDisplays();
        e.Handled = true;
    }

    // The message itself is shown under the field by the inspector row; the editor only changes its border.
    private void ShowOptionInputError(WpfTextBox editor, string? message)
    {
        if (message is null) {
            editor.ClearValue(BorderBrushProperty);
            editor.ClearValue(BackgroundProperty);
        } else {
            editor.SetResourceReference(BorderBrushProperty, "Brush.Error");
            editor.SetResourceReference(BackgroundProperty, "Brush.Canvas");
        }
        AutomationProperties.SetItemStatus(editor, message ?? string.Empty);
    }

    private void SaveSelectedCase(Guid configurationId, string optionName, string caseName) =>
        SaveCaseChoice(optionName, project => project.SetSelectedCase(configurationId, optionName, caseName));

    private void SaveSelectedCases(Guid configurationId, string optionName, IReadOnlyCollection<string> caseNames) =>
        SaveCaseChoice(optionName, project => project.SetSelectedCases(configurationId, optionName, caseNames));

    private void SaveCaseChoice(string optionName, Action<ProjectPlanModule> save)
    {
        if (_updatingOptionEditors || !Editable) {
            return;
        }

        try {
            save(RequireProject());
            PlanEdited?.Invoke(this, EventArgs.Empty);
            RefreshAfterOptionSaved();
            _logger.Info($"已保存 explicit case：option={optionName}。 ");
        } catch (Exception exception) {
            _editFailed("保存 select/switch/checkbox option 失败", exception);
            TryRenderTaskPlan();
            ShowProjectValidationError(exception);
        }
    }

    private void TryRenderTaskPlan()
    {
        try {
            RenderTaskPlan();
        } catch (Exception exception) {
            ShowProjectValidationError(exception);
            _logger.Warn("刷新 option 编辑器失败。", exception);
            ConfigurationValidated?.Invoke(this, false);
        }
    }

    private void RenderTaskPlan()
    {
        var project = RequireProject();
        _updatingOptionEditors = true;
        try {
            _shownConfigurationId = project.ActiveConfigurationId;
            ConfigurationsRendered?.Invoke(this, project);
            RenderAvailableTaskShelf(project);
            RenderPlanItems(project);
            RefreshConfigurationValidity(project);
        } finally {
            _updatingOptionEditors = false;
        }
    }

    private void RefreshConfigurationValidity(ProjectPlanModule project)
    {
        bool valid;
        try {
            project.ValidateConfiguration();
            valid = true;
            ProjectValidationText.Text = project.LoadWarning ?? string.Empty;
            ProjectValidationBorder.Visibility = project.LoadWarning is null
                ? Visibility.Collapsed : Visibility.Visible;
        } catch (Exception exception) {
            valid = false;
            ShowProjectValidationError(exception);
        }
        ConfigurationValidated?.Invoke(this, valid);
    }

    private void RefreshAfterOptionSaved()
    {
        RefreshParameterDisplays();
        RefreshConfigurationValidity(RequireProject());
    }

    private void RenderAvailableTaskShelf(ProjectPlanModule project)
    {
        var query = TaskSearchBox.Text.Trim();
        bool Matches(ProjectTaskChoice task) => task.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || task.Label.Contains(query, StringComparison.OrdinalIgnoreCase);
        var tasks = project.Tasks.Where(Matches).ToArray();
        var pretasks = project.Pretasks.Where(Matches).ToArray();
        var total = project.Tasks.Count + project.Pretasks.Count;
        AvailableTaskCountText.Text = $"{tasks.Length + pretasks.Length} / {total}";
        AvailableTaskCountText.Visibility = query.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        TaskShelfCloseHint.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        AvailableTasksPanel.Children.Clear();
        if (pretasks.Length > 0) {
            AddTaskGroup(project, "前置任务", "前置任务", true, pretasks, query.Length != 0);
        }
        if (tasks.Length + pretasks.Length == 0) {
            AvailableTasksPanel.Children.Add(new TextBlock {
                Text = "没有匹配的任务", Margin = new Thickness(2, 8, 0, 12),
                Style = (Style)FindResource("SecondaryTextStyle")
            });
        } else if (project.Groups.Count == 0) {
            AvailableTasksPanel.Children.Add(CreateCatalogRows(project, tasks, grouped: false));
        } else {
            var knownGroups = project.Groups.Select(group => group.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var group in project.Groups) {
                AddTaskGroup(project, group.Name, group.Label, group.DefaultExpand,
                    tasks.Where(task => task.Groups.Contains(group.Name, StringComparer.Ordinal)).ToArray(),
                    query.Length != 0);
            }
            AddTaskGroup(project, string.Empty, "未分组", false,
                tasks.Where(task => !task.Groups.Any(knownGroups.Contains)).ToArray(), query.Length != 0);
        }
    }

    private void AddTaskGroup(ProjectPlanModule project, string name, string label, bool defaultExpand,
        IReadOnlyList<ProjectTaskChoice> tasks, bool searching)
    {
        if (tasks.Count == 0) {
            return;
        }
        var count = tasks.Count(task => IsInPlan(project, task));
        var header = new DockPanel();
        var counter = new TextBlock {
            Text = $"{count} / {tasks.Count}", Margin = new Thickness(8, 0, 0, 0), FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center
        };
        counter.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text.Disabled");
        DockPanel.SetDock(counter, Dock.Right);
        header.Children.Add(counter);
        header.Children.Add(new TextBlock { Text = label, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
        var section = new Expander {
            Header = header, Tag = name,
            Style = (Style)FindResource("TaskGroupExpanderStyle"),
            IsExpanded = searching || _taskGroupExpanded.GetValueOrDefault(name, defaultExpand),
            Content = CreateCatalogRows(project, tasks, grouped: true),
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(section, $"{label}，已添加 {count} / {tasks.Count} 项");
        if (!searching) {
            section.Expanded += (_, _) => _taskGroupExpanded[name] = true;
            section.Collapsed += (_, _) => _taskGroupExpanded[name] = false;
        }
        AvailableTasksPanel.Children.Add(section);
    }

    private static bool IsInPlan(ProjectPlanModule project, ProjectTaskChoice task) =>
        (task.Kind == PlanItemKind.Pretask ? project.SelectedPretaskNames : project.SelectedTaskNames)
            .Contains(task.Name, StringComparer.Ordinal);

    private StackPanel CreateCatalogRows(ProjectPlanModule project, IEnumerable<ProjectTaskChoice> tasks, bool grouped)
    {
        var panel = new StackPanel();
        foreach (var task in tasks) {
            var added = IsInPlan(project, task);
            var label = new TextBlock {
                Text = task.Label, VerticalAlignment = VerticalAlignment.Center, FontSize = 14,
                TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(grouped ? 18 : 0, 0, 8, 0)
            };
            var icon = CreateSymbolIcon(added ? WpfSymbolRegular.Checkmark16 : WpfSymbolRegular.Add16);
            icon.SetResourceReference(ForegroundProperty, added ? "Brush.Primary" : "Brush.Text.Muted");
            var content = new DockPanel();
            DockPanel.SetDock(icon, Dock.Right);
            content.Children.Add(icon);
            content.Children.Add(label);
            var button = new WpfButton {
                Content = content,
                Tag = task,
                Style = (Style)FindResource(added ? "Plan.CatalogAddedItem" : "Plan.CatalogItem")
            };
            label.SetBinding(TextBlock.ForegroundProperty,
                new System.Windows.Data.Binding(nameof(Foreground)) { Source = button });
            if (added) {
                button.ToolTip = "已在执行计划中，点击定位";
                AutomationProperties.SetName(button, $"已添加：{task.Label}，定位到执行计划");
                button.Click += LocatePlanItemButton_Click;
            } else {
                BindConfigurationEditable(button);
                AutomationProperties.SetName(button, $"添加任务：{task.Label}");
                button.Click += AddTaskButton_Click;
                ShowPlanActionOnInteraction(icon, button);
            }
            panel.Children.Add(button);
        }
        return panel;
    }

    private void TaskSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (Project is not null) {
            RenderAvailableTaskShelf(Project);
            AvailableTasksScroll.ScrollToTop();
        }
    }

    private void TaskWorkspacePanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var rightInset = e.NewSize.Width >= 460 ? 32 : 4;
        TaskShelfContent.Margin = new Thickness(4, -2, rightInset, 0);
        TaskShelfContent.Width = Math.Min(362, Math.Max(0, e.NewSize.Width - rightInset - 4));
        TaskShelfContent.MaxHeight = Math.Min(762, Math.Max(0, e.NewSize.Height - 32));
    }

    private void RenderPlanItems(ProjectPlanModule project)
    {
        PlanItemsPanel.Children.Clear();
        _planItemContainers.Clear();
        _planItemPreviews.Clear();
        _inspectorRows.Clear();
        var items = project.SelectedItems;
        EmptyPlanPanel.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateAddTaskButton();
        UpdateIdlePlanCount(project);
        EmptyPlanText.Text = $"从 {project.Tasks.Count + project.Pretasks.Count} 个可用任务中选择要执行的任务，"
            + "添加后按顺序依次执行，也可以拖动调整顺序。";

        foreach (var task in items) {
            var container = CreatePlanItem(task, IsExpanded(task));
            _planItemContainers.Add((task.Kind, task.Name), container);
            PlanItemsPanel.Children.Add(container);
        }
    }

    private bool IsExpanded(ProjectTaskChoice task) => _expandedTask == (task.Kind, task.Name);

    private ProjectConfigurationView GetConfigurationOrEmpty(string taskName, PlanItemKind kind)
    {
        try {
            return RequireProject().GetConfiguration(taskName, kind);
        } catch (Exception exception) when (exception is InvalidDataException or ArgumentException) {
            return new ProjectConfigurationView([], []);
        }
    }

    private static bool HasParameters(ProjectConfigurationView configuration) =>
        configuration.GlobalOptions.Count + configuration.TaskOptions.Count != 0;

    private static bool HasDetails(ProjectTaskChoice task, ProjectConfigurationView configuration) =>
        HasParameters(configuration) || !string.IsNullOrWhiteSpace(task.Description);

    private FlowDocumentScrollViewer CreatePlanItemDescription(ProjectTaskChoice task)
    {
        var document = MarkdownDocument.Create([task.Description], _openDescriptionLink,
            new MarkdownStyle(14, 20, 18, 22, "Brush.Text.Muted", CompactLists: true));
        document.PagePadding = new Thickness(0);
        var viewer = new FlowDocumentScrollViewer {
            Document = document, IsToolBarVisible = false, Focusable = false,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(0, 0, 0, 6)
        };
        // The enclosing task list owns scrolling.
        viewer.PreviewMouseWheel += (_, e) => {
            if (viewer.Parent is UIElement parent) {
                e.Handled = true;
                parent.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) {
                    RoutedEvent = MouseWheelEvent
                });
            }
        };
        return viewer;
    }

    private Border CreatePlanItem(ProjectTaskChoice task, bool expanded)
    {
        var configuration = GetConfigurationOrEmpty(task.Name, task.Kind);
        var canExpand = HasDetails(task, configuration);
        expanded &= canExpand;
        var container = new Border {
            Tag = task,
            Margin = new Thickness(0, 0, 0, 2),
            Style = (Style)FindResource(expanded ? "PlanItemExpandedStyle" : "PlanItemStyle")
        };
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        layout.Children.Add(CreatePlanItemHeader(task, expanded, canExpand, configuration, container));

        if (expanded) {
            var editor = new Border {
                Style = (Style)FindResource("ParameterEditorStyle")
            };
            var remove = new WpfButton {
                Tag = task, Height = 32, MinHeight = 0, Margin = new Thickness(0, 10, 0, 0),
                Padding = new Thickness(10, 0, 10, 0), FontWeight = FontWeights.Normal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                Style = (Style)FindResource("Plan.RemoveAction")
            };
            var content = new StackPanel { Orientation = WpfOrientation.Horizontal };
            var removeIcon = CreateSymbolIcon(WpfSymbolRegular.Delete16);
            removeIcon.Margin = new Thickness(0, 0, 6, 0);
            removeIcon.SetBinding(ForegroundProperty, new System.Windows.Data.Binding(nameof(Foreground)) { Source = remove });
            content.Children.Add(removeIcon);
            var removeText = new TextBlock { Text = "从计划中移除", FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
            removeText.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding(nameof(Foreground)) { Source = remove });
            content.Children.Add(removeText);
            remove.Content = content;
            AutomationProperties.SetName(remove, $"从执行计划移除 {task.Label}");
            BindConfigurationEditable(remove);
            remove.Click += RemovePlanItemButton_Click;
            var body = new StackPanel();
            if (!string.IsNullOrWhiteSpace(task.Description)) {
                body.Children.Add(CreatePlanItemDescription(task));
            }
            if (HasParameters(configuration)) {
                body.Children.Add(CreateParameterInspector(configuration));
            }
            body.Children.Add(remove);
            editor.Child = body;
            container.SizeChanged += (_, args) =>
                editor.Padding = new Thickness(args.NewSize.Width >= 420 ? 36 : 16, 0, 16, 14);
            Grid.SetRow(editor, 1);
            layout.Children.Add(editor);
        }
        container.Child = layout;
        return container;
    }

    private Grid CreatePlanItemHeader(ProjectTaskChoice task, bool expanded, bool canExpand,
        ProjectConfigurationView configuration, Border container)
    {
        var header = new Grid {
            MinHeight = 40,
            Margin = new Thickness(0, 0, 4, 0)
        };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var selection = CreatePlanItemSelection(task);
        header.Children.Add(selection);

        var title = new DockPanel { ClipToBounds = true };
        if (canExpand) {
            var chevron = CreateSymbolIcon(expanded ? WpfSymbolRegular.ChevronDown12 : WpfSymbolRegular.ChevronRight12);
            chevron.Margin = new Thickness(6, 0, 0, 0);
            chevron.SetResourceReference(ForegroundProperty, expanded ? "Brush.Text.Primary" : "Brush.Text.Disabled");
            if (!expanded) {
                ShowPlanActionOnInteraction(chevron, header);
            }
            DockPanel.SetDock(chevron, Dock.Right);
            title.Children.Add(chevron);
        }
        var rowTitle = CreatePlanRowTitle(task.Label, task.Kind, expanded);
        BindUncheckedTone((TextBlock)rowTitle.Children[0], selection, "Brush.Text.Primary", "Brush.Text.Muted");
        title.Children.Add(rowTitle);
        if (!expanded && HasParameters(configuration)) {
            var preview = new StackPanel {
                Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0), ClipToBounds = true
            };
            System.Windows.Documents.TextElement.SetFontSize(preview, 14);
            BindUncheckedTone(preview, selection, "Brush.Text.Muted", "Brush.Text.Disabled");
            _planItemPreviews.Add((preview, container, task));
            FillParameterPreview(preview, configuration, container);
            title.Children.Add(preview);
        }
        var main = new WpfButton {
            Tag = task, Content = title,
            Padding = new Thickness(12, 0, 6, 0),
            Style = (Style)FindResource("PlanHeaderButtonStyle")
        };
        if (!canExpand) {
            main.Cursor = WpfCursors.Arrow;
        }
        AutomationProperties.SetName(main,
            $"{task.Label}，{(canExpand ? (expanded ? "已展开" : "已折叠") : "没有可配置的参数")}");
        AutomationProperties.SetHelpText(main, canExpand ? (expanded ? "收起任务详情" : "展开任务详情") : string.Empty);
        main.Click += PlanItemHeader_Click;
        AttachPlanItemDrag(main, container, task);
        Grid.SetColumn(main, 1);
        header.Children.Add(main);
        if (!HasParameters(configuration) && !expanded) {
            var remove = CreateIconButton(WpfSymbolRegular.Delete16, $"从执行计划移除 {task.Label}", "从执行计划移除");
            remove.Style = (Style)FindResource("TaskRemoveButtonStyle");
            remove.Tag = task;
            remove.Click += RemovePlanItemButton_Click;
            BindConfigurationEditable(remove);
            ShowPlanActionOnInteraction(remove, header);
            Grid.SetColumn(remove, 2);
            header.Children.Add(remove);
        }
        return header;
    }

    private static void BindUncheckedTone(FrameworkElement text, System.Windows.Controls.CheckBox selection, string normal, string uncheckedTone)
    {
        var style = new Style(text.GetType());
        style.Setters.Add(new Setter(System.Windows.Documents.TextElement.ForegroundProperty, new DynamicResourceExtension(normal)));
        var uncheckedState = new DataTrigger {
            Binding = new System.Windows.Data.Binding(nameof(selection.IsChecked)) { Source = selection }, Value = false
        };
        uncheckedState.Setters.Add(new Setter(System.Windows.Documents.TextElement.ForegroundProperty, new DynamicResourceExtension(uncheckedTone)));
        style.Triggers.Add(uncheckedState);
        text.Style = style;
    }

    private static void ShowPlanActionOnInteraction(FrameworkElement action, FrameworkElement row)
    {
        var style = new Style(action.GetType(), action.Style);
        style.Setters.Add(new Setter(OpacityProperty, 0d));
        foreach (var interaction in new[] { nameof(IsMouseOver), nameof(IsKeyboardFocusWithin) }) {
            var trigger = new DataTrigger {
                Binding = new System.Windows.Data.Binding(interaction) { Source = row }, Value = true
            };
            trigger.Setters.Add(new Setter(OpacityProperty, 1d));
            style.Triggers.Add(trigger);
        }
        action.Style = style;
    }

    private System.Windows.Controls.CheckBox CreatePlanItemSelection(ProjectTaskChoice task)
    {
        var selection = new System.Windows.Controls.CheckBox {
            IsChecked = RequireProject().IsItemEnabled(task.Kind, task.Name),
            Style = (Style)FindResource("Plan.Selection"), Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
            ToolTip = task.Applicable ? null : "当前控制器或资源不适用，运行时跳过"
        };
        AutomationProperties.SetName(selection, $"执行 {task.Label}");
        BindConfigurationEditable(selection);
        selection.Click += (_, _) => {
            if (!Editable) {
                selection.IsChecked = RequireProject().IsItemEnabled(task.Kind, task.Name);
                return;
            }
            try {
                RequireProject().SetItemEnabled(task.Kind, task.Name, selection.IsChecked == true);
            } catch (Exception exception) {
                selection.IsChecked = RequireProject().IsItemEnabled(task.Kind, task.Name);
                HandleProjectEditError("保存任务勾选状态失败", exception);
                return;
            }
            UpdateIdlePlanCount(RequireProject());
            PlanEdited?.Invoke(this, EventArgs.Empty);
            RefreshConfigurationValidity(RequireProject());
        };
        return selection;
    }

    private void BindConfigurationEditable(UIElement element) => System.Windows.Data.BindingOperations.SetBinding(
        element, IsEnabledProperty, _configurationEditableBinding ??= new System.Windows.Data.Binding {
            Path = new PropertyPath(ConfigurationEditableProperty), Source = this
        });

    private WpfButton CreateIconButton(WpfSymbolRegular symbol, string accessibleName, string toolTip)
    {
        var button = new WpfButton {
            Content = CreateSymbolIcon(symbol),
            Style = (Style)FindResource("TaskIconButtonStyle"),
            ToolTip = toolTip
        };
        AutomationProperties.SetName(button, accessibleName);
        return button;
    }

    private static WpfSymbolIcon CreateSymbolIcon(WpfSymbolRegular symbol) => new() {
        Symbol = symbol,
        Width = 16,
        Height = 16,
        Focusable = false,
        IsHitTestVisible = false,
        ToolTip = null
    };


    private void HandleProjectEditError(string operation, Exception exception)
    {
        _editFailed(operation, exception);
        TryRenderTaskPlan();
    }

    internal void ShowProjectValidationError(Exception exception)
    {
        ProjectValidationText.Text = exception.GetBaseException().Message;
        ProjectValidationBorder.Visibility = Visibility.Visible;
    }
}
