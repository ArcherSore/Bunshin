using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Gui.Infrastructure;
using WpfButton = System.Windows.Controls.Button;
using WpfTextBox = System.Windows.Controls.TextBox;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;

namespace Gui.Views;

public sealed partial class TaskPlanEditor
{
    // The selected tab is scrolled clear of the edge fades; one wheel notch moves the tabs this far.
    private const double ConfigurationTabRevealMargin = 32;
    private const double ConfigurationWheelStep = 64;
    private bool _committingConfigurationInput;
    private WpfTextBox? _configurationNameEditor;
    private HwndSource? _configurationWheelSource;

    private void RenderConfigurationTabs()
    {
        var project = Project!;
        ConfigurationsRendered?.Invoke(this, project);
        PresetConfigurationButton.Visibility = project.Presets.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        var existing = ConfigurationTabs.Items.Cast<TabItem>().ToArray();
        if (!existing.Select(item => (Id: (Guid)item.Tag, Name: AutomationProperties.GetName(item)))
            .SequenceEqual(project.Configurations.Select(item => (item.Id, item.Name)))) {
            ConfigurationTabs.Items.Clear();
            var deletable = project.Configurations.Count > 1;
            foreach (var configuration in project.Configurations) {
                var header = new Grid();
                var label = new TextBlock {
                    Text = configuration.Name, MaxWidth = 168, Margin = new Thickness(0, 0, deletable ? 22 : 8, 0),
                    VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis
                };
                header.Children.Add(label);
                var delete = new WpfButton {
                    Tag = configuration.Id, Style = (Style)FindResource("Configuration.Delete"),
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                    Content = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Dismiss16,
                        FontSize = 10 }
                };
                if (deletable) {
                    BindConfigurationEditable(delete);
                } else {
                    delete.IsEnabled = false;
                    delete.Visibility = Visibility.Collapsed;
                }
                AutomationProperties.SetName(delete, $"删除配置 {configuration.Name}");
                delete.PreviewMouseLeftButtonDown += (_, e) => {
                    if (!Editable || _configurationNameEditor is not null
                        || !CommitFocusedConfigurationInput()) {
                        e.Handled = true;
                    } else {
                        delete.Focus();
                        delete.CaptureMouse();
                        e.Handled = true;
                    }
                };
                delete.PreviewMouseLeftButtonUp += (_, e) => {
                    if (delete.IsMouseCaptured) {
                        delete.ReleaseMouseCapture();
                        if (delete.IsMouseOver) {
                            DeleteConfiguration_Click(delete, e);
                        }
                        e.Handled = true;
                    }
                };
                delete.Click += DeleteConfiguration_Click;
                header.Children.Add(delete);
                var tab = new ConfigurationTab {
                    Tag = configuration.Id, Header = header, Style = (Style)FindResource("Configuration.Tab"),
                    CanSwitch = () => Editable
                };
                label.SetBinding(TextBlock.FontWeightProperty,
                    new System.Windows.Data.Binding(nameof(FontWeight)) { Source = tab });
                label.SetBinding(TextBlock.ForegroundProperty,
                    new System.Windows.Data.Binding(nameof(Foreground)) { Source = tab });
                tab.MouseDoubleClick += (_, e) => {
                    if (e.ChangedButton == MouseButton.Left && !delete.IsMouseOver) {
                        BeginConfigurationRename(tab);
                        e.Handled = true;
                    }
                };
                tab.KeyDown += (_, e) => {
                    if (e.Key == Key.F2) {
                        BeginConfigurationRename(tab);
                        e.Handled = true;
                    }
                };
                AutomationProperties.SetName(tab, configuration.Name);
                ConfigurationTabs.Items.Add(tab);
            }
        }
        var selected = ConfigurationTabs.Items.Cast<TabItem>()
            .Single(item => (Guid)item.Tag == project.ActiveConfigurationId);
        ConfigurationTabs.SelectedItem = selected;
        RefreshConfigurationTabStates();
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => RevealConfigurationTab(selected));
    }

    // Locked, the tabs still scroll and take focus, but only the active one reads as available.
    private void RefreshConfigurationTabStates()
    {
        var locked = _configurationLock();
        foreach (TabItem tab in ConfigurationTabs.Items) {
            var name = AutomationProperties.GetName(tab);
            var blocked = locked is not null && !tab.IsSelected;
            var reason = locked == LockReason.RunActive ? "任务运行中" : "运行环境处理中";
            tab.ToolTip = locked is null ? $"{name}\n双击或按 F2 重命名"
                : blocked ? $"{name}\n{reason}，无法切换配置" : $"{name}\n{reason}，暂时不能重命名";
            // A blocked tab keeps its dimmed look on hover, so it never reads as clickable.
            if (blocked) {
                tab.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "Brush.Text.Disabled");
                tab.Background = System.Windows.Media.Brushes.Transparent;
            } else {
                tab.ClearValue(ForegroundProperty);
                tab.ClearValue(BackgroundProperty);
            }
        }
    }

    private ScrollViewer? ConfigurationScroll
    {
        get {
            ConfigurationTabs.ApplyTemplate();
            return ConfigurationTabs.Template.FindName("ConfigurationScroll", ConfigurationTabs) as ScrollViewer;
        }
    }

    // Rendering before the window is shown cannot measure the tabs, so the reveal runs again once they appear.
    private void ConfigurationTabs_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (ConfigurationTabs.IsVisible && ConfigurationTabs.SelectedItem is TabItem tab) {
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => RevealConfigurationTab(tab));
        }
    }

    // Home and End go to the first and last tab from wherever focus is; TabControl measures them from the selection,
    // which no longer follows focus while the tabs are locked.
    private void ConfigurationTabs_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key is Key.Home or Key.End && e.OriginalSource is TabItem && ConfigurationTabs.Items.Count != 0) {
            ((TabItem)ConfigurationTabs.Items[e.Key == Key.Home ? 0 : ^1]!).Focus();
            e.Handled = true;
        }
    }

    private void RevealConfigurationTab(TabItem tab)
    {
        if (ConfigurationScroll is not { } scroll || !tab.IsVisible || !scroll.IsAncestorOf(tab)) {
            return;
        }
        var left = tab.TranslatePoint(new System.Windows.Point(0, 0), scroll).X + scroll.HorizontalOffset;
        scroll.ScrollToHorizontalOffset(RevealOffset(
            scroll.HorizontalOffset, scroll.ViewportWidth, left, tab.ActualWidth, ConfigurationTabRevealMargin));
    }

    // Moves as little as possible to show [left, left + width] with a margin on both sides; a span wider than the
    // viewport keeps its start visible.
    internal static double RevealOffset(double offset, double viewport, double left, double width, double margin)
    {
        if (left - margin < offset || width + margin * 2 > viewport) {
            return Math.Max(0, left - margin);
        }
        return left + width + margin > offset + viewport ? left + width + margin - viewport : offset;
    }

    // The vertical wheel scrolls the tab bar sideways, whether or not configurations can be edited.
    private void ConfigurationScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is ScrollViewer { ScrollableWidth: > 0 } scroll) {
            ScrollConfigurationTabs(scroll, -e.Delta);
            e.Handled = true;
        }
    }

    // The hook goes on the window's source, which the window hands over once it exists.
    internal void AddConfigurationWheelHook(HwndSource? source)
    {
        _configurationWheelSource = source;
        _configurationWheelSource?.AddHook(ConfigurationWheelHook);
    }

    internal void RemoveConfigurationWheelHook()
    {
        _configurationWheelSource?.RemoveHook(ConfigurationWheelHook);
        _configurationWheelSource = null;
    }

    // WPF has no horizontal wheel event, so a tilt wheel or touchpad swipe is read from WM_MOUSEHWHEEL; it only
    // ever moves the tab bar under the pointer.
    private nint ConfigurationWheelHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        const int wmMouseHorizontalWheel = 0x020E;
        if (message == wmMouseHorizontalWheel
            && ConfigurationScroll is { IsMouseOver: true, ScrollableWidth: > 0 } scroll) {
            ScrollConfigurationTabs(scroll, (short)((wParam.ToInt64() >> 16) & 0xFFFF));
            handled = true;
        }
        return 0;
    }

    private static void ScrollConfigurationTabs(ScrollViewer scroll, int delta) =>
        scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset + delta / 120.0 * ConfigurationWheelStep);

    private void ConfigurationActions_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_configurationNameEditor is not null) {
            if (!_configurationNameEditor.IsMouseOver) {
                _ = CommitConfigurationName();
                e.Handled = true;
            }
        } else if (!CommitFocusedConfigurationInput()) {
            e.Handled = true;
        }
    }

    private void ConfigurationActions_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (_configurationNameEditor is not null) {
            return;
        }
        if (!CommitFocusedConfigurationInput()) {
            e.Handled = true;
        }
    }

    internal bool CommitFocusedConfigurationInput(bool unattended = false)
    {
        if (Keyboard.FocusedElement is not WpfTextBox editor) {
            return true;
        }
        return editor.Tag is OptionInputTag ? CommitOptionInput(editor, unattended)
            : editor != _configurationNameEditor || CommitConfigurationName(unattended);
    }

    // The sidebar's "新建配置" does what the tab strip's button does.
    internal void RequestNewConfiguration()
    {
        if (NewConfigurationButton.IsEnabled) {
            NewConfigurationButton_Click(NewConfigurationButton, new RoutedEventArgs());
        }
    }

    private void NewConfigurationButton_Click(object sender, RoutedEventArgs e)
    {
        ChangeConfiguration(() => {
            var id = Project!.CreateConfiguration();
            _logger.Info($"已新建配置{AppLogger.Field("name", ConfigurationName(id))}");
        });
    }

    // A missing id must not throw: deleting an already removed configuration stays a quiet no-op.
    private string? ConfigurationName(Guid id) =>
        Project?.Configurations.FirstOrDefault(item => item.Id == id)?.Name;

    private void PresetConfigurationButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu {
            PlacementTarget = PresetConfigurationButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom
        };
        foreach (var preset in Project!.Presets) {
            var item = new MenuItem { Header = preset.Label, ToolTip = preset.Description };
            AutomationProperties.SetName(item, $"从预设新建配置：{preset.Label}");
            BindConfigurationEditable(item);
            item.Click += (_, _) => ChangeConfiguration(() => {
                var id = Project!.CreateConfigurationFromPreset(preset.Name);
                _logger.Info($"已从预设新建配置{AppLogger.Field("preset", preset.Name)}"
                    + AppLogger.Field("name", ConfigurationName(id)));
            });
            menu.Items.Add(item);
        }
        PresetConfigurationButton.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private void ConfigurationTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingOptionEditors || e.Source != ConfigurationTabs
            || ConfigurationTabs.SelectedItem is not TabItem { Tag: Guid id }) {
            return;
        }
        ChangeConfiguration(() => {
            var from = Project!.ActiveConfigurationId;
            Project.ActivateConfiguration(id);
            if (from != id) {
                _logger.Info($"已切换配置{AppLogger.Field("from", ConfigurationName(from))}"
                    + AppLogger.Field("to", ConfigurationName(id)));
            }
        });
    }

    private void DeleteConfiguration_Click(object sender, RoutedEventArgs e)
    {
        if (sender is WpfButton { Tag: Guid id }) {
            ChangeConfiguration(() => {
                var name = ConfigurationName(id);
                if (Project!.DeleteConfiguration(id)) {
                    _logger.Info($"已删除配置{AppLogger.Field("name", name)}"
                        + AppLogger.Field("active", ConfigurationName(Project.ActiveConfigurationId)));
                }
            });
        }
    }

    // A scheduled start activates its configuration itself; the tabs then only follow.
    internal void RenderActivatedConfiguration(Guid configurationId) => RenderConfigurationChange(
        ConfigurationTabs.SelectedItem is not TabItem { Tag: Guid shown } || shown != configurationId);

    private void RenderConfigurationChange(bool switched)
    {
        if (switched) {
            _addedSinceShelfOpened.Clear();
            UpdateAddedTasks();
        }
        PlanEdited?.Invoke(this, EventArgs.Empty);
        _expandedTask = null;
        _dragTask = null;
        EndConfigurationRename();
        TaskDescriptionCloseRequested?.Invoke(this, EventArgs.Empty);
        RenderTaskPlan();
    }

    // Returns whether the change was made. An unattended change (started by the launch itself) names where a refusal
    // or failure is reported, without a dialog nobody can answer.
    internal bool ChangeConfiguration(Action change, Action<Exception>? unattendedFailure = null)
    {
        if (!Editable || _configurationNameEditor is not null
            || !CommitFocusedConfigurationInput()) {
            if (Project is not null) {
                RestoreConfigurationSelection();
            }
            unattendedFailure?.Invoke(new InvalidOperationException("无法切换到所选配置。"));
            return false;
        }
        try {
            var previous = Project?.ActiveConfigurationId;
            change();
            RenderConfigurationChange(Project?.ActiveConfigurationId != previous);
            return true;
        } catch (Exception exception) {
            RestoreConfigurationSelection();
            if (unattendedFailure is not null) {
                unattendedFailure(exception);
            } else {
                HandleProjectEditError("保存任务配置失败", exception);
            }
            return false;
        }
    }

    private void RestoreConfigurationSelection()
    {
        _updatingOptionEditors = true;
        try {
            RenderConfigurationTabs();
        } finally {
            _updatingOptionEditors = false;
        }
    }

    private void BeginConfigurationRename(TabItem tab)
    {
        if (!Editable || _configurationNameEditor is not null
            || !CommitFocusedConfigurationInput()) {
            return;
        }
        var header = (Grid)tab.Header;
        var label = (TextBlock)header.Children[0];
        var editor = new WpfTextBox {
            Tag = (Guid)tab.Tag, Text = label.Text, MinWidth = 40, MaxWidth = label.MaxWidth,
            FontSize = label.FontSize, FontWeight = label.FontWeight,
            Height = 22, MinHeight = 0, Padding = new Thickness(0), BorderThickness = new Thickness(0),
            Margin = label.Margin, VerticalContentAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(editor, "配置名称");
        editor.LostKeyboardFocus += ConfigurationNameEditor_LostKeyboardFocus;
        editor.KeyDown += ConfigurationNameEditor_KeyDown;
        _configurationNameEditor = editor;
        label.Visibility = Visibility.Hidden;
        header.Children.Add(editor);
        editor.Focus();
        editor.SelectAll();
    }

    private void EndConfigurationRename()
    {
        if (_configurationNameEditor is not { Parent: Grid header } editor) {
            return;
        }
        _configurationNameEditor = null;
        editor.Tag = null;
        header.Children.Remove(editor);
        header.Children[0].Visibility = Visibility.Visible;
    }

    private void ConfigurationNameEditor_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!_updatingOptionEditors) {
            _ = CommitConfigurationName();
        }
    }

    private void ConfigurationNameEditor_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter) {
            _ = CommitConfigurationName();
            e.Handled = true;
        } else if (e.Key == Key.Escape) {
            EndConfigurationRename();
            ConfigurationTabs.Focus();
            e.Handled = true;
        }
    }

    private bool CommitConfigurationName(bool unattended = false)
    {
        if (!Editable || _configurationNameEditor is not { Tag: Guid id } editor
            || _committingConfigurationInput) {
            return true;
        }
        _committingConfigurationInput = true;
        try {
            if (!string.IsNullOrWhiteSpace(editor.Text)) {
                var from = ConfigurationName(id);
                Project!.RenameConfiguration(id, editor.Text);
                if (ConfigurationName(id) != from) {
                    _logger.Info($"已重命名配置{AppLogger.Field("from", from)}"
                        + AppLogger.Field("to", ConfigurationName(id)));
                }
            }
            EndConfigurationRename();
            RestoreConfigurationSelection();
            return true;
        } catch (Exception exception) when (!unattended) {
            editor.Text = Project!.Configurations.Single(item => item.Id == id).Name;
            EndConfigurationRename();
            _editFailed("保存配置名称失败", exception);
            return false;
        } finally {
            _committingConfigurationInput = false;
        }
    }

    private bool CommitOptionInput(WpfTextBox textBox, bool unattended = false)
    {
        var text = GetInputText(textBox);
        if (_updatingOptionEditors || _committingConfigurationInput || !Editable
            || textBox.Tag is not OptionInputTag tag || tag.Submitted && text == tag.Value) {
            return true;
        }
        if (_invalidInputDrafts.TryGetValue(tag.Key, out var draft) && draft.Text == text) {
            return false;
        }
        _committingConfigurationInput = true;
        try {
            if (Project!.CheckInputValue(tag.OptionName, tag.InputName, text) is { } problem) {
                // A rejected value is a typo, not a failure: keep the text and explain it under the field.
                // The saved value is unchanged; the draft survives re-rendering until it is fixed or Esc restores it.
                var message = tag.PatternMessage ?? problem;
                _invalidInputDrafts[tag.Key] = new InputDraft(text, message);
                ShowOptionInputError(textBox, message);
                RefreshParameterDisplays();
                _logger.Info($"input 未通过校验，未保存：option={tag.OptionName}，input={tag.InputName}。 ");
                return false;
            }
            Project.SetInputValue(tag.ConfigurationId, tag.OptionName, tag.InputName, text);
            _invalidInputDrafts.Remove(tag.Key);
            textBox.Tag = tag with { Value = text, Submitted = true };
            PlanEdited?.Invoke(this, EventArgs.Empty);
            ShowOptionInputError(textBox, null);
            RefreshAfterOptionSaved(rebuildCards: false);
            return true;
        } catch (Exception exception) when (!unattended) {
            SetInputText(textBox, tag.Value);
            textBox.Tag = tag with { Submitted = true };
            _editFailed("保存 input option 失败", exception);
            ShowProjectValidationError(exception);
            return false;
        } finally {
            _committingConfigurationInput = false;
        }
    }
}

// A tab selects itself when focused only while configurations can be switched; locked, arrow keys and Home/End
// move focus across the tabs without switching.
internal sealed class ConfigurationTab : TabItem
{
    internal required Func<bool> CanSwitch { get; init; }

    protected override void OnPreviewGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        if (CanSwitch()) {
            base.OnPreviewGotKeyboardFocus(e);
        }
    }
}
