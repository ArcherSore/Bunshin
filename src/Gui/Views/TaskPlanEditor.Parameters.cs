
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ProjectModel;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfControl = System.Windows.Controls.Control;
using WpfListBox = System.Windows.Controls.ListBox;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfTextBox = System.Windows.Controls.TextBox;
using WpfSymbolRegular = Wpf.Ui.Controls.SymbolRegular;

namespace Gui.Views;

public sealed partial class TaskPlanEditor
{
    private sealed record InspectorInputView(WpfTextBox Editor, StackPanel Notes);
    private sealed record InspectorBranchView(string Path, List<InspectorRowView> Rows);
    private sealed record InspectorRowView(
        string OptionName, ParameterRowPanel Row, WpfControl? Selector, StackPanel CaseNotes,
        IReadOnlyDictionary<string, InspectorInputView> Inputs, StackPanel Nested, List<InspectorBranchView> Branches);

    private IReadOnlyList<InspectorRow> DeriveInspectorRows(ProjectConfigurationView configuration)
    {
        var project = RequireProject();
        var drafts = _invalidInputDrafts.Where(pair => pair.Key.Item1 == project.ActiveConfigurationId)
            .ToDictionary(pair => (pair.Key.Item2, pair.Key.Item3), pair => pair.Value);
        return ParameterInspector.Derive(configuration, drafts, project.OptionOwners);
    }

    private void FillParameterPreview(TextBlock preview, ProjectConfigurationView configuration, Border? container = null)
    {
        var rows = ParameterInspector.Flatten(DeriveInspectorRows(configuration)).ToArray();
        var invalid = rows.Where(row => row.IsInvalid).Select(row => {
            var field = row.Inputs.FirstOrDefault(input => input.Error is not null);
            var label = field?.Label ?? row.Label;
            return row.Option.IsBelowMinimum ? $"{label} 至少选择 {row.Option.MinCount} 项" : $"{label} 格式不对";
        }).Distinct(StringComparer.Ordinal).ToArray();
        if (invalid.Length != 0) {
            container?.SetResourceReference(BackgroundProperty, "Brush.Parameter.InvalidRow");
            preview.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Error.Foreground");
            preview.Text = $"ⓘ {string.Join(" · ", invalid)}";
            preview.ToolTip = preview.Text;
        } else {
            container?.ClearValue(BackgroundProperty);
            preview.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text.Muted");
            var values = rows.Where(row => !row.IsGlobal).SelectMany(PreviewValues).ToArray();
            preview.Text = string.Join(" · ", values.Select(item => $"{item.Label} {item.Value}".ReplaceLineEndings(" ")));
            preview.ToolTip = values.Length == 0 ? null
                : string.Join("\n", values.Select(item => $"{item.Label}：{item.Value}"));
        }
        preview.Visibility = preview.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private static IEnumerable<(string Label, string Value)> PreviewValues(InspectorRow row)
    {
        if (row.Kind == ProjectOptionKind.Input) {
            foreach (var input in row.Inputs.Where(input => input.DisplayValue.Length > 0)) {
                yield return (row.Inputs.Count == 1 ? row.Label : input.Label, input.Show(input.DisplayValue));
            }
        } else if (row.Kind == ProjectOptionKind.Checkbox) {
            if (row.Branches.Count == 0 || row.Branches.Count < row.Option.SelectedCases.Count) {
                yield return (row.Label, $"{row.Option.SelectedCases.Count} 项");
            }
        } else if (row.SelectedCase is { } selected) {
            yield return (row.Label, selected.Label);
        }
    }

    private void RefreshParameterDisplays()
    {
        foreach (var (preview, container, task) in _planItemPreviews) {
            FillParameterPreview(preview, GetConfigurationOrEmpty(task.Name, task.Kind), container);
        }
        if (_expandedTask is not { } expanded || _inspectorRows.Count == 0) {
            return;
        }
        var rows = DeriveInspectorRows(GetConfigurationOrEmpty(expanded.Name, expanded.Kind));
        _updatingOptionEditors = true;
        try {
            UpdateInspectorRows(_inspectorRows, rows, animate: true);
        } finally {
            _updatingOptionEditors = false;
        }
    }

    private StackPanel CreateParameterInspector(ProjectConfigurationView configuration)
    {
        var body = new StackPanel();
        AddInspectorRows(body, _inspectorRows, DeriveInspectorRows(configuration), 14);
        return body;
    }

    private void AddInspectorRows(StackPanel parent, List<InspectorRowView> views, IReadOnlyList<InspectorRow> rows,
        double spacing)
    {
        foreach (var row in rows) {
            var view = CreateInspectorRow(row);
            var gap = row.Kind == ProjectOptionKind.Switch && views.LastOrDefault()?.Selector is Wpf.Ui.Controls.ToggleSwitch
                ? 0 : spacing;
            view.Row.Margin = new Thickness(0, parent.Children.Count == 0 ? 0 : gap, 0, 0);
            views.Add(view);
            parent.Children.Add(view.Row);
        }
    }

    private InspectorRowView CreateInspectorRow(InspectorRow row)
    {
        var label = new StackPanel();
        if (row.Kind != ProjectOptionKind.Switch) {
            label.Children.Add(new TextBlock { Text = row.Label, Style = (Style)FindResource("Inspector.Label") });
        }
        if (row.IsGlobal || row.SharedWith.Count > 1) {
            label.Children.Add(CreateSharedHint(row));
        }

        var caseNotes = new StackPanel();
        var inputs = new Dictionary<string, InspectorInputView>(StringComparer.Ordinal);
        var nested = new StackPanel();
        var value = new StackPanel();
        WpfControl? selector = null;
        if (row.Kind == ProjectOptionKind.Input) {
            value.Children.Add(CreateInputCells(row, inputs));
            if (row.Inputs.Count > 1 && !string.IsNullOrWhiteSpace(row.Description)) {
                value.Children.Add(CreateInspectorNote(row.Description, "Inspector.Note"));
            }
        } else {
            selector = CreateCaseSelector(row);
            value.Children.Add(selector is WpfComboBox ? LimitEditor(selector, 180) : selector);
            if (!string.IsNullOrWhiteSpace(row.Description)) {
                value.Children.Add(CreateInspectorNote(row.Description, "Inspector.Note"));
            }
            value.Children.Add(caseNotes);
            value.Children.Add(nested);
        }
        var panel = new ParameterRowPanel { Depth = row.Depth, Children = { label, value } };
        var view = new InspectorRowView(row.Option.Name, panel, selector, caseNotes, inputs, nested, []);
        ApplyInspectorRow(view, row, animate: false);
        return view;
    }

    private FrameworkElement CreateSharedHint(InspectorRow row)
    {
        var text = row.IsGlobal ? "所有任务共用" : $"{row.SharedWith.Count} 个任务共用";
        var hint = new TextBlock {
            Text = text, Style = (Style)FindResource("Inspector.Shared"), Margin = new Thickness(0, 2, 0, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            ToolTip = row.IsGlobal ? "所有任务共用一个值" : string.Join("\n", row.SharedWith)
        };
        ToolTipService.SetShowOnDisabled(hint, true);
        return hint;
    }

    private FrameworkElement CreateInputCells(InspectorRow row, Dictionary<string, InspectorInputView> inputs)
    {
        var panel = new ParameterInputsPanel();
        foreach (var input in row.Inputs) {
            var notes = new StackPanel();
            var (editor, field) = CreateInputEditor(row, input);
            inputs.Add(input.Input.Name, new InspectorInputView(editor, notes));
            var cell = new StackPanel();
            if (row.Inputs.Count > 1) {
                cell.Children.Add(new TextBlock {
                    Text = input.Label, Style = (Style)FindResource("Inspector.InputLabel")
                });
            }
            cell.Children.Add(field);
            if (!string.IsNullOrWhiteSpace(input.HelpText)) {
                cell.Children.Add(CreateInspectorNote(input.HelpText, "Inspector.Note"));
            }
            if (input.Input.IsInteger && input.Input.InputType == ProjectInputType.Text
                && IntegerHint(input) is { } hint && !input.HelpText.Contains(hint, StringComparison.Ordinal)) {
                cell.Children.Add(CreateInspectorNote(hint, "Inspector.Note"));
            }
            cell.Children.Add(notes);
            if (row.Inputs.Count == 1) {
                return cell;
            }
            panel.Children.Add(cell);
        }
        return panel;
    }

    // The project has no independent range field. Use the supplied format hint or a simple one-digit interval.
    private static string? IntegerHint(InspectorInput input)
    {
        if (input.Input.PatternMessage is { Length: > 0 } message) {
            return message;
        }
        if (input.Input.Verify is ['^', '[', var min, '-', var max, ']', '$']
            && char.IsAsciiDigit(min) && char.IsAsciiDigit(max)) {
            return $"（{min}～{max}）";
        }
        return string.IsNullOrWhiteSpace(input.HelpText) && input.Input.Verify is null
            ? "范围 −2147483648～2147483647" : null;
    }

    private (WpfTextBox Editor, FrameworkElement Field) CreateInputEditor(InspectorRow row, InspectorInput input)
    {
        var tag = new OptionInputTag(Project!.ActiveConfigurationId, row.Option.Name, input.Input.Name,
            input.SavedValue, input.Input.PatternMessage);
        var editor = input.Input.IsPassword
            ? new Wpf.Ui.Controls.PasswordBox { PasswordChar = '•' }
            : new Wpf.Ui.Controls.TextBox();
        editor.Style = (Style)FindResource(input.Input.IsPassword ? "Option.PasswordBox" : "Option.TextBox");
        editor.Tag = tag;
        editor.ToolTip = input.Input.PatternMessage;
        SetInputText(editor, input.DisplayValue);
        AutomationProperties.SetName(editor, input.AccessibleName);
        if (!string.IsNullOrWhiteSpace(input.HelpText)) {
            AutomationProperties.SetHelpText(editor, input.HelpText);
        }
        editor.LostKeyboardFocus += OptionInputTextBox_LostKeyboardFocus;
        editor.PreviewKeyDown += OptionInputTextBox_PreviewKeyDown;
        BindConfigurationEditable(editor);
        if (editor is Wpf.Ui.Controls.PasswordBox password) {
            password.IsKeyboardFocusWithinChanged += (_, _) => {
                if (!password.IsKeyboardFocusWithin) {
                    password.SetCurrentValue(Wpf.Ui.Controls.PasswordBox.IsPasswordRevealedProperty, false);
                }
            };
        }
        if (input.Input.InputType is ProjectInputType.Text or ProjectInputType.Password) {
            editor.MaxWidth = input.Input.IsPassword ? 200 : input.Input.IsInteger ? 88 : row.Depth > 0 ? 160 : 200;
            return (editor, LimitEditor(editor, editor.MaxWidth));
        }

        var folder = input.Input.InputType == ProjectInputType.Directory;
        var title = folder ? "选择文件夹" : "选择文件";
        var browse = CreateIconButton(WpfSymbolRegular.FolderOpen24, $"{title}：{input.AccessibleName}", title);
        browse.Margin = new Thickness(4, 0, 0, 0);
        Grid.SetColumn(browse, 1);
        BindConfigurationEditable(browse);
        browse.Click += (_, _) => {
            if (PickPath(folder) is { } path) {
                editor.Text = path;
                _ = CommitOptionInput(editor);
            }
        };
        var pathEditor = new Grid {
            ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } },
            Children = { editor, browse }
        };
        return (editor, LimitEditor(pathEditor, 340));
    }

    private static Grid LimitEditor(UIElement editor, double maxWidth) =>
        new() { ColumnDefinitions = { new ColumnDefinition { MaxWidth = maxWidth } }, Children = { editor } };

    private WpfControl CreateCaseSelector(InspectorRow row)
    {
        var option = row.Option;
        var configurationId = Project!.ActiveConfigurationId;
        WpfControl selector;
        if (option.Kind == ProjectOptionKind.Switch) {
            var (on, off) = GetSwitchCases(option);
            var toggle = new Wpf.Ui.Controls.ToggleSwitch {
                IsChecked = option.SelectedCase == on.Name, OnContent = row.Label, OffContent = row.Label,
                Style = (Style)FindResource("Option.ToggleSwitch")
            };
            toggle.Click += (_, _) =>
                SaveSelectedCase(configurationId, option.Name, toggle.IsChecked == true ? on.Name : off.Name);
            selector = toggle;
        } else if (option.Kind == ProjectOptionKind.Checkbox || option.Cases.Count <= 3) {
            var multiple = option.Kind == ProjectOptionKind.Checkbox;
            var choices = new WpfListBox {
                ItemsSource = option.Cases,
                Style = (Style)FindResource(multiple ? "Option.Chips" : "Option.Segments")
            };
            if (multiple) {
                foreach (var item in option.Cases.Where(item => option.SelectedCases.Contains(item.Name))) {
                    choices.SelectedItems.Add(item);
                }
                choices.SelectionChanged += (_, e) => SaveCheckboxSelection(choices, configurationId, option, e);
            } else {
                choices.SelectedItem = row.SelectedCase;
                choices.SelectionChanged += (_, _) => {
                    if (choices.SelectedItem is ProjectCaseEditor selected) {
                        SaveSelectedCase(configurationId, option.Name, selected.Name);
                    }
                };
            }
            selector = choices;
        } else {
            var dropdown = new WpfComboBox {
                ItemsSource = option.Cases, SelectedItem = row.SelectedCase,
                Style = (Style)FindResource("Option.ComboBox")
            };
            dropdown.SelectionChanged += (_, _) => {
                if (dropdown.SelectedItem is ProjectCaseEditor selected) {
                    SaveSelectedCase(configurationId, option.Name, selected.Name);
                }
            };
            selector = dropdown;
        }
        AutomationProperties.SetName(selector, row.Label);
        if (!string.IsNullOrWhiteSpace(row.Description)) {
            AutomationProperties.SetHelpText(selector, row.Description);
        }
        BindConfigurationEditable(selector);
        return selector;
    }

    private void SaveCheckboxSelection(WpfListBox choices, Guid configurationId, ProjectOptionEditor option,
        SelectionChangedEventArgs change)
    {
        if (_updatingOptionEditors || !Editable) {
            return;
        }
        var selected = choices.SelectedItems.Cast<ProjectCaseEditor>().ToArray();
        if (selected.Length <= option.MaxCount || option.MaxCount is null) {
            SaveSelectedCases(configurationId, option.Name, selected.Select(item => item.Name).ToArray());
            return;
        }
        // At max_count 1 a new pick replaces the old one; a wider limit refuses the extra pick.
        var added = change.AddedItems.Cast<ProjectCaseEditor>();
        var extra = option.MaxCount == 1 ? selected.Except(added) : added;
        extra.ToList().ForEach(choices.SelectedItems.Remove);
    }

    private static IEnumerable<InspectorRowView> InspectorViews(IEnumerable<InspectorRowView> views) =>
        views.SelectMany(view => InspectorViews(view.Branches.SelectMany(branch => branch.Rows)).Prepend(view));

    private void UpdateInspectorRows(IReadOnlyList<InspectorRowView> views, IReadOnlyList<InspectorRow> rows,
        bool animate)
    {
        for (var index = 0; index < rows.Count; index++) {
            ApplyInspectorRow(views[index], rows[index], animate);
        }
    }

    private void ApplyInspectorRow(InspectorRowView view, InspectorRow row, bool animate)
    {
        view.Row.IsInvalid = row.IsInvalid;
        view.CaseNotes.Children.Clear();
        if (view.Selector is not null) {
            UpdateSelector(view.Selector, row);
            if (row.CaseDescription is { } caseDescription) {
                view.CaseNotes.Children.Add(CreateInspectorNote(caseDescription, "Inspector.Note"));
            }
            if (row.Option.IsBelowMinimum) {
                view.CaseNotes.Children.Add(CreateInspectorError($"至少选择 {row.Option.MinCount} 项，否则不能开始任务"));
            } else if (row.Option.Cases.Count > 1 && ParameterInspector.DescribeLimits(row.Option) is { } limits) {
                view.CaseNotes.Children.Add(CreateInspectorNote(limits, "Inspector.Note"));
            }
        }
        foreach (var input in row.Inputs) {
            var (editor, notes) = view.Inputs[input.Input.Name];
            if (editor.Tag is OptionInputTag tag && tag.Value != input.SavedValue) {
                SetInputText(editor, input.DisplayValue);
                editor.Tag = tag with { Value = input.SavedValue, Submitted = true };
            }
            ShowOptionInputError(editor, input.Error);
            notes.Children.Clear();
            if (input.Error is { } error) {
                notes.Children.Add(CreateInspectorError(error));
            }
            if (input.Draft is not null) {
                var saved = ParameterInspector.ShowValue(input.Show(input.SavedValue));
                notes.Children.Add(CreateInspectorNote($"未保存，仍使用「{saved}」· Esc 还原", "Inspector.DraftNote"));
            }
        }
        UpdateInspectorBranches(view, row.Branches, animate);
    }

    private void UpdateSelector(WpfControl selector, InspectorRow row)
    {
        switch (selector) {
        case Wpf.Ui.Controls.ToggleSwitch toggle:
            toggle.IsChecked = row.Option.SelectedCase == GetSwitchCases(row.Option).On.Name;
            break;
        case WpfComboBox comboBox:
            comboBox.SelectedItem = comboBox.Items.Cast<ProjectCaseEditor>()
                .FirstOrDefault(item => item.Name == row.Option.SelectedCase);
            break;
        case WpfListBox choices when row.Kind == ProjectOptionKind.Select:
            choices.SelectedItem = choices.Items.Cast<ProjectCaseEditor>()
                .FirstOrDefault(item => item.Name == row.Option.SelectedCase);
            break;
        case WpfListBox choices:
            foreach (var item in choices.Items.Cast<ProjectCaseEditor>()) {
                var selected = row.Option.SelectedCases.Contains(item.Name);
                if (selected && !choices.SelectedItems.Contains(item)) {
                    choices.SelectedItems.Add(item);
                } else if (!selected && choices.SelectedItems.Contains(item)) {
                    choices.SelectedItems.Remove(item);
                }
            }
            if (row.Option.IsBelowMinimum) {
                choices.SetResourceReference(BorderBrushProperty, "Brush.Error");
            } else {
                choices.ClearValue(BorderBrushProperty);
            }
            break;
        }
        AutomationProperties.SetItemStatus(selector, row.Option.StartBlocker ?? string.Empty);
    }

    private void UpdateInspectorBranches(InspectorRowView view, IReadOnlyList<InspectorBranch> branches, bool animate)
    {
        for (var index = view.Branches.Count - 1; index >= 0; index--) {
            if (!branches.Any(branch => branch.Path == view.Branches[index].Path)) {
                view.Branches.RemoveAt(index);
                view.Nested.Children.RemoveAt(index);
            }
        }
        for (var index = 0; index < branches.Count; index++) {
            var branch = branches[index];
            if (index < view.Branches.Count && view.Branches[index].Path == branch.Path) {
                UpdateInspectorRows(view.Branches[index].Rows, branch.Rows, animate);
                continue;
            }
            var body = new StackPanel();
            var childRows = new List<InspectorRowView>();
            AddInspectorRows(body, childRows, branch.Rows, 12);
            var indent = ParameterRowPanel.IndentStep;
            var container = new Border {
                Child = body, BorderThickness = new Thickness(1, 0, 0, 0),
                Margin = new Thickness(5, 4, 0, 4), Padding = new Thickness(indent - 6, 10, 0, 0)
            };
            container.SetResourceReference(Border.BorderBrushProperty, "Brush.Parameter.Guide");
            view.Branches.Insert(index, new InspectorBranchView(branch.Path, childRows));
            view.Nested.Children.Insert(index, container);
            if (animate && SystemParameters.ClientAreaAnimation) {
                AnimateInspectorBranch(container);
            }
        }
    }

    private static void AnimateInspectorBranch(Border branch)
    {
        var scale = new ScaleTransform();
        branch.LayoutTransform = scale;
        var duration = TimeSpan.FromMilliseconds(200);
        var expand = new DoubleAnimation(0.05, 1, duration) {
            FillBehavior = FillBehavior.Stop, EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        expand.Completed += (_, _) => branch.ClearValue(LayoutTransformProperty);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, expand);
        branch.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { FillBehavior = FillBehavior.Stop });
    }

    private FrameworkElement CreateInspectorError(string message)
    {
        var icon = CreateSymbolIcon(WpfSymbolRegular.ErrorCircle16);
        icon.Width = icon.Height = icon.FontSize = 12;
        icon.Margin = new Thickness(0, 8, 6, 0);
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.SetResourceReference(ForegroundProperty, "Brush.Error");
        return new DockPanel { Children = { icon, CreateInspectorNote(message, "Inspector.Error") } };
    }

    private TextBlock CreateInspectorNote(string text, string style)
    {
        var note = new TextBlock { Text = text, Style = (Style)FindResource(style) };
        AutomationProperties.SetLiveSetting(note, AutomationLiveSetting.Polite);
        return note;
    }

    // The PI defines no extension filter, so any file can be picked.
    private string? PickPath(bool folder)
    {
        if (folder) {
            var folderDialog = new Microsoft.Win32.OpenFolderDialog();
            return folderDialog.ShowDialog(Window.GetWindow(this)) == true ? folderDialog.FolderName : null;
        }
        var fileDialog = new Microsoft.Win32.OpenFileDialog();
        return fileDialog.ShowDialog(Window.GetWindow(this)) == true ? fileDialog.FileName : null;
    }

    // A password box keeps the real text in Password; its Text holds only mask characters.
    private static string GetInputText(WpfTextBox editor) =>
        editor is Wpf.Ui.Controls.PasswordBox password ? password.Password : editor.Text;

    private static void SetInputText(WpfTextBox editor, string value)
    {
        if (editor is Wpf.Ui.Controls.PasswordBox password) {
            password.Password = value;
        } else {
            editor.Text = value;
        }
    }

    // PI switch cases carry no on/off flag; the project names them Yes/No, so an affirmative name marks "on",
    // otherwise the first case does.
    private static (ProjectCaseEditor On, ProjectCaseEditor Off) GetSwitchCases(ProjectOptionEditor option)
    {
        var on = option.Cases.FirstOrDefault(
            item => SwitchOnCaseNames.Contains(item.Name, StringComparer.OrdinalIgnoreCase)) ?? option.Cases[0];
        return (on, option.Cases.First(item => item.Name != on.Name));
    }

}
