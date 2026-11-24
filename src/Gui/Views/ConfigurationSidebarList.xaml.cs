using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ProjectModel;
using WpfButton = System.Windows.Controls.Button;
using WpfDragEventArgs = System.Windows.DragEventArgs;
using WpfDragDropEffects = System.Windows.DragDropEffects;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfPoint = System.Windows.Point;

namespace Gui.Views;

public partial class ConfigurationSidebarList : System.Windows.Controls.UserControl
{
    private const string DragFormat = "Bunshin.Configuration";
    private const double DragScrollEdge = 24;
    private readonly Dictionary<Guid, ConfigurationSidebarRow> _rows = [];
    private readonly DispatcherTimer _dragScroll = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private IReadOnlyList<TaskConfiguration> _configurations = [];
    private SchedulePreview? _schedule;
    private bool _homeActive;
    private bool _running;
    private bool _editable;
    private bool _committingRename;
    private LockReason? _lockReason;
    private ConfigurationSidebarRow? _renaming;
    private ConfigurationSidebarRow? _pressed;
    private ConfigurationSidebarRow? _dragging;
    private WpfPoint _pressPoint;
    private WpfPoint? _dragPoint;

    public ConfigurationSidebarList()
    {
        InitializeComponent();
        _dragScroll.Tick += (_, _) => ScrollWhileDragging();
        IsVisibleChanged += (_, _) => {
            if (IsVisible) {
                RevealActive();
            } else {
                _pressed = null;
            }
        };
    }

    internal Func<bool> CanEdit { get; set; } = () => false;
    internal Func<Guid, string, bool, bool>? SaveRename { get; set; }
    internal event EventHandler<Guid>? ConfigurationRequested;
    internal event EventHandler<(Guid Id, WpfButton Anchor)>? MenuRequested;
    internal event EventHandler<(Guid Id, int Index)>? MoveRequested;

    internal bool IsRenaming => _renaming is not null;

    internal Guid? ActiveConfigurationId { get; private set; }
    internal RailItem? ActiveButton => ActiveConfigurationId is { } id ? _rows[id].SelectButton : null;

    internal void ShowConfigurations(IReadOnlyList<TaskConfiguration> configurations, Guid? active, SchedulePreview? schedule)
    {
        var reveal = active != ActiveConfigurationId;
        _configurations = configurations;
        _schedule = schedule;
        ActiveConfigurationId = active;
        foreach (var removed in _rows.Keys.Except(configurations.Select(c => c.Id)).ToArray()) {
            var row = _rows[removed];
            if (_renaming == row) {
                EndRename();
            }
            Rows.Children.Remove(row);
            _rows.Remove(removed);
        }
        for (var index = 0; index < configurations.Count; index++) {
            var id = configurations[index].Id;
            if (!_rows.TryGetValue(id, out var row)) {
                row = CreateRow(id);
                _rows.Add(id, row);
            }
            if (Rows.Children.IndexOf(row) != index) {
                Rows.Children.Remove(row);
                Rows.Children.Insert(index, row);
            }
        }
        RefreshRows();
        if (reveal) {
            RevealActive();
        }
    }

    internal void MarkHome(bool active)
    {
        _homeActive = active;
        RefreshRows();
    }

    internal void ShowRunning(bool running)
    {
        _running = running;
        RefreshRows();
    }

    internal void ApplyLock(bool editable, LockReason? reason)
    {
        if (_editable == editable && _lockReason == reason) {
            return;
        }
        _editable = editable;
        _lockReason = reason;
        if (!editable) {
            EndRename();
        }
        RefreshRows();
    }

    internal void BeginRename(Guid id)
    {
        if (!CanEdit() || !_rows.TryGetValue(id, out var row)) {
            return;
        }
        _renaming = row;
        row.NameEditor.Text = row.ConfigurationName;
        row.NameEditor.Visibility = Visibility.Visible;
        row.BringIntoView();
        // Wait for WPF to close the context menu and restore its previous focus.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => {
            if (_renaming == row) {
                row.NameEditor.Focus();
                row.NameEditor.SelectAll();
            }
        });
    }

    internal bool CommitRename(bool unattended)
    {
        if (_renaming is not { } row || _committingRename) {
            return true;
        }
        _committingRename = true;
        try {
            var saved = SaveRename?.Invoke(row.Id, row.NameEditor.Text, unattended) ?? false;
            EndRename();
            return saved;
        } finally {
            _committingRename = false;
        }
    }

    private void EndRename()
    {
        if (_renaming is { } row) {
            _renaming = null;
            row.NameEditor.Visibility = Visibility.Collapsed;
        }
    }

    private void RevealActive()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => {
            if (IsVisible) {
                ActiveButton?.BringIntoView();
            }
        });
    }

    private void RefreshRows()
    {
        foreach (var configuration in _configurations) {
            RefreshRow(configuration, _rows[configuration.Id]);
        }
    }

    private void RefreshRow(TaskConfiguration configuration, ConfigurationSidebarRow row)
    {
        var active = configuration.Id == ActiveConfigurationId;
        var scheduled = configuration.SchedulePolicies.Count != 0;
        var state = scheduled ? _schedule?.ListState(configuration) : null;
        var reason = _lockReason switch {
            LockReason.RunActive => "任务运行中，配置已锁定",
            null => null,
            _ => "运行环境处理中，配置暂时锁定"
        };
        row.Show(configuration.Name, _homeActive && active, _running && active, scheduled,
            state is { Outcome: not null } next ? $"下次定时 {next.Text}" : null, _editable, active || _editable, reason);
    }

    private ConfigurationSidebarRow CreateRow(Guid id)
    {
        var row = new ConfigurationSidebarRow { Id = id };
        row.SelectButton.Click += (_, _) => {
            if (row.SelectButton.IsEnabled) {
                ConfigurationRequested?.Invoke(this, id);
            }
        };
        row.SelectButton.PreviewMouseLeftButtonDown += (_, e) => {
            if (ActiveConfigurationId != id && !CanEdit()) {
                e.Handled = true;
            } else {
                _pressed = row;
                _pressPoint = e.GetPosition(this);
            }
        };
        row.SelectButton.PreviewMouseLeftButtonUp += (_, _) => _pressed = null;
        row.SelectButton.PreviewMouseMove += (_, e) => Row_MouseMove(row, e);
        row.SelectButton.MouseDoubleClick += (_, e) => {
            if (e.ChangedButton == MouseButton.Left) {
                BeginRename(id);
                e.Handled = true;
            }
        };
        row.SelectButton.PreviewKeyDown += (_, e) => Row_KeyDown(row, e);
        row.MenuButton.PreviewMouseLeftButtonDown += (_, e) => e.Handled = !CanEdit();
        row.MenuButton.Click += (_, _) => MenuRequested?.Invoke(this, (id, row.MenuButton));
        row.NameEditor.LostKeyboardFocus += (_, _) => {
            if (_renaming == row) {
                CommitRename(unattended: false);
            }
        };
        row.NameEditor.PreviewKeyDown += (_, e) => {
            if (e.Key is Key.Enter or Key.Escape) {
                if (e.Key == Key.Enter) {
                    CommitRename(unattended: false);
                } else {
                    EndRename();
                }
                row.SelectButton.Focus();
                e.Handled = true;
            }
        };
        return row;
    }

    private void Row_KeyDown(ConfigurationSidebarRow row, WpfKeyEventArgs e)
    {
        if (e.Key == Key.F2) {
            BeginRename(row.Id);
            e.Handled = true;
        } else if (e.Key == Key.Apps || e.Key == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift) {
            MenuRequested?.Invoke(this, (row.Id, row.MenuButton));
            e.Handled = true;
        } else if (e.Key is Key.Up or Key.Down or Key.Home or Key.End) {
            var index = Rows.Children.IndexOf(row);
            var next = e.Key switch {
                Key.Up => Math.Max(0, index - 1),
                Key.Down => Math.Min(Rows.Children.Count - 1, index + 1),
                Key.Home => 0,
                _ => Rows.Children.Count - 1
            };
            var target = (ConfigurationSidebarRow)Rows.Children[next];
            if (e.Key is Key.Up or Key.Down && Keyboard.Modifiers == ModifierKeys.Control) {
                if (index != next && CanEdit()) {
                    MoveRequested?.Invoke(this, (row.Id, next));
                    row.SelectButton.Focus();
                    row.BringIntoView();
                }
            } else if (target.SelectButton.IsEnabled) {
                ConfigurationRequested?.Invoke(this, target.Id);
                target.SelectButton.Focus();
                target.BringIntoView();
            } else {
                target.BringIntoView();
            }
            e.Handled = true;
        }
    }

    private void Row_MouseMove(ConfigurationSidebarRow row, System.Windows.Input.MouseEventArgs e)
    {
        if (_pressed != row || e.LeftButton != MouseButtonState.Pressed || _dragging is not null
            || Rows.Children.Count < 2) {
            return;
        }
        var point = e.GetPosition(this);
        if (Math.Abs(point.X - _pressPoint.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(point.Y - _pressPoint.Y) < SystemParameters.MinimumVerticalDragDistance) {
            return;
        }
        _pressed = null;
        if (!CanEdit()) {
            return;
        }
        _dragging = row;
        row.SelectButton.ReleaseMouseCapture();
        row.ShowDragPlaceholder(true);
        FloatingLabel.Text = row.ConfigurationName;
        try {
            DragDrop.DoDragDrop(this, new System.Windows.DataObject(DragFormat, row.Id), WpfDragDropEffects.Move);
        } finally {
            row.ShowDragPlaceholder(false);
            ClearDragFeedback();
            _dragging = null;
        }
    }

    private void List_DragOver(object sender, WpfDragEventArgs e)
    {
        if (DraggedRow(e) is not { }) {
            e.Effects = WpfDragDropEffects.None;
        } else {
            _dragPoint = e.GetPosition(DragFeedback);
            ShowDragFeedback(_dragPoint.Value);
            _dragScroll.Start();
            e.Effects = WpfDragDropEffects.Move;
        }
        e.Handled = true;
    }

    private void List_DragLeave(object sender, WpfDragEventArgs e)
    {
        // DragLeave can bubble from a row even when the pointer is still over the list.
        var point = e.GetPosition(ConfigurationScroll);
        if (!new Rect(ConfigurationScroll.RenderSize).Contains(point)) {
            ClearDragFeedback();
        }
    }

    private void List_Drop(object sender, WpfDragEventArgs e)
    {
        if (DraggedRow(e) is not { } row || !CanEdit()) {
            e.Effects = WpfDragDropEffects.None;
        } else {
            var from = Rows.Children.IndexOf(row);
            var insertion = InsertionIndex(e.GetPosition(DragFeedback).Y);
            var target = insertion > from ? insertion - 1 : insertion;
            if (target != from) {
                MoveRequested?.Invoke(this, (row.Id, target));
            }
            e.Effects = WpfDragDropEffects.Move;
        }
        ClearDragFeedback();
        e.Handled = true;
    }

    private ConfigurationSidebarRow? DraggedRow(WpfDragEventArgs e) =>
        _editable && _dragging is { } row && Equals(e.Data.GetData(DragFormat, false), row.Id) ? row : null;

    private int InsertionIndex(double y)
    {
        for (var index = 0; index < Rows.Children.Count; index++) {
            var row = (ConfigurationSidebarRow)Rows.Children[index];
            if (y < row.TranslatePoint(new WpfPoint(0, row.ActualHeight / 2), DragFeedback).Y) {
                return index;
            }
        }
        return Rows.Children.Count;
    }

    private void ShowDragFeedback(WpfPoint point)
    {
        DragFeedback.Visibility = Visibility.Visible;
        var insertion = InsertionIndex(point.Y);
        var last = insertion == Rows.Children.Count;
        var row = (ConfigurationSidebarRow)Rows.Children[last ? insertion - 1 : insertion];
        var edge = row.TranslatePoint(new WpfPoint(0, last ? row.ActualHeight + 1 : -1), DragFeedback).Y;
        InsertionMarker.Width = row.ActualWidth;
        Canvas.SetTop(InsertionMarker, edge - 4);
        FloatingRow.Width = Math.Max(0, row.ActualWidth - 8);
        Canvas.SetLeft(FloatingRow, 8);
        Canvas.SetTop(FloatingRow, point.Y - 16);
    }

    private void ScrollWhileDragging()
    {
        if (_dragPoint is not { } point || !_editable) {
            return;
        }
        var delta = point.Y < DragScrollEdge ? -8
            : point.Y > ConfigurationScroll.ActualHeight - DragScrollEdge ? 8 : 0;
        if (delta != 0) {
            ConfigurationScroll.ScrollToVerticalOffset(ConfigurationScroll.VerticalOffset + delta);
            ShowDragFeedback(point);
        }
    }

    private void ClearDragFeedback()
    {
        _dragScroll.Stop();
        _dragPoint = null;
        DragFeedback.Visibility = Visibility.Collapsed;
    }
}
