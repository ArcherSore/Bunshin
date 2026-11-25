using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ProjectModel;
using WpfButton = System.Windows.Controls.Button;
using WpfDataObject = System.Windows.DataObject;
using WpfDragEventArgs = System.Windows.DragEventArgs;
using WpfDragDropEffects = System.Windows.DragDropEffects;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;
using WpfPoint = System.Windows.Point;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace Gui.Views;

public sealed partial class TaskPlanEditor
{
    private const string PlanItemDragDataFormat = "Gui.PlanItem";
    private const double PlanDragScrollEdge = 24;
    private readonly DispatcherTimer _planDragScroll = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private WpfButton? _pressedPlanHeader;
    private WpfPoint _planPressPoint;
    private WpfPoint? _planDragPoint;
    private PlanDrag? _planDrag;

    private sealed record PlanDrag(ProjectPlanModule Project, Guid ConfigurationId, ProjectTaskChoice Task, Border Card);

    private void AttachPlanItemDrag(WpfButton header, Border card, ProjectTaskChoice task)
    {
        header.PreviewMouseLeftButtonDown += (_, e) => {
            _pressedPlanHeader = header;
            _planPressPoint = e.GetPosition(PlanViewport);
        };
        header.PreviewMouseLeftButtonUp += (_, _) => _pressedPlanHeader = null;
        header.PreviewMouseMove += (_, e) => StartPlanItemDrag(header, card, task, e);
    }

    private void StartPlanItemDrag(WpfButton header, Border card, ProjectTaskChoice task, WpfMouseEventArgs e)
    {
        if (_pressedPlanHeader != header || _planDrag is not null || e.LeftButton != MouseButtonState.Pressed) {
            return;
        }
        var point = e.GetPosition(PlanViewport);
        if (Math.Abs(point.X - _planPressPoint.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(point.Y - _planPressPoint.Y) < SystemParameters.MinimumVerticalDragDistance) {
            return;
        }
        _pressedPlanHeader = null;
        if (!Editable || Project is not { } project || !PlanItemsPanel.Children.Contains(card)
            || project.SelectedItems.Count(item => item.Kind == task.Kind) < 2) {
            return;
        }

        e.Handled = true;
        _planDrag = new(project, project.ActiveConfigurationId, task, card);
        header.ReleaseMouseCapture();
        var layout = (Grid)card.Child;
        var contents = layout.Children.OfType<UIElement>().ToArray();
        var placeholder = new Rectangle { Style = (Style)FindResource("ListReorderPlaceholderStyle") };
        Grid.SetRowSpan(placeholder, layout.RowDefinitions.Count);
        try {
            foreach (var child in contents) {
                child.Visibility = Visibility.Hidden;
            }
            layout.Children.Add(placeholder);
            var data = new WpfDataObject(PlanItemDragDataFormat, task.Name);
            _ = DragDrop.DoDragDrop(PlanViewport, data, WpfDragDropEffects.Move);
        } finally {
            layout.Children.Remove(placeholder);
            foreach (var child in contents) {
                child.Visibility = Visibility.Visible;
            }
            _planDrag = null;
            ClearPlanDragFeedback();
        }
    }

    private void PlanViewport_DragOver(object sender, WpfDragEventArgs e)
    {
        e.Handled = true;
        e.Effects = WpfDragDropEffects.None;
        if (DraggedPlanItem(e) is not { } drag) {
            ClearPlanDragFeedback();
            return;
        }
        var point = e.GetPosition(PlanViewport);
        _planDragPoint = point;
        _planDragScroll.Start();
        if (ShowPlanDragFeedback(drag, point)) {
            e.Effects = WpfDragDropEffects.Move;
        }
    }

    private void PlanViewport_DragLeave(object sender, WpfDragEventArgs e)
    {
        if (!new Rect(PlanViewport.RenderSize).Contains(e.GetPosition(PlanViewport))) {
            ClearPlanDragFeedback();
        }
    }

    private void PlanViewport_Drop(object sender, WpfDragEventArgs e)
    {
        e.Handled = true;
        e.Effects = WpfDragDropEffects.None;
        ClearPlanDragFeedback();
        if (DraggedPlanItem(e) is not { } drag
            || ResolvePlanDrop(drag, e.GetPosition(PlanViewport).Y) is not { } drop) {
            return;
        }
        try {
            if (drag.Project.MoveTask(drag.Task.Name, drop.TargetIndex, drag.Task.Kind)) {
                PlanEdited?.Invoke(this, EventArgs.Empty);
                _logger.Info($"已调整任务顺序：{drag.Task.Name} -> {drop.TargetIndex}。");
                RenderTaskPlan();
            }
            e.Effects = WpfDragDropEffects.Move;
        } catch (Exception exception) {
            HandleProjectEditError("调整任务顺序失败", exception);
        }
    }

    private bool CanDropPlanItem(PlanDrag drag) => Editable && ReferenceEquals(Project, drag.Project)
        && drag.Project.ActiveConfigurationId == drag.ConfigurationId && PlanItemsPanel.Children.Contains(drag.Card);

    private PlanDrag? DraggedPlanItem(WpfDragEventArgs e) => _planDrag is { } drag && CanDropPlanItem(drag)
        && Equals(e.Data.GetData(PlanItemDragDataFormat, false), drag.Task.Name) ? drag : null;

    private (Border Anchor, bool AfterAnchor, int TargetIndex)? ResolvePlanDrop(PlanDrag drag, double pointerY)
    {
        var items = drag.Project.SelectedItems;
        var boundary = 0;
        foreach (var item in items) {
            var card = _planItemContainers[(item.Kind, item.Name)];
            var top = card.TranslatePoint(new WpfPoint(), PlanViewport).Y;
            var header = (FrameworkElement)((Grid)card.Child).Children[0];
            if (pointerY < top + header.ActualHeight / 2) {
                break;
            }
            boundary++;
        }
        // Pretasks and regular tasks are separate contiguous blocks in the execution plan.
        var start = items.TakeWhile(item => item.Kind != drag.Task.Kind).Count();
        var group = items.Skip(start).TakeWhile(item => item.Kind == drag.Task.Kind).ToList();
        var current = group.FindIndex(item => item.Name == drag.Task.Name);
        var local = boundary - start;
        if (current < 0 || local < 0 || local > group.Count) {
            return null;
        }
        var last = local == group.Count;
        var anchor = group[last ? local - 1 : local];
        return (_planItemContainers[(anchor.Kind, anchor.Name)], last, local > current ? local - 1 : local);
    }

    private bool ShowPlanDragFeedback(PlanDrag drag, WpfPoint point)
    {
        var drop = ResolvePlanDrop(drag, point.Y);
        PlanDragFeedback.Show(drag.Task.Label, point, drag.Card.ActualWidth, drop?.Anchor, drop?.AfterAnchor ?? false);
        return drop.HasValue;
    }

    private void ScrollWhileDraggingPlanItem()
    {
        if (_planDrag is not { } drag || _planDragPoint is not { } point || !CanDropPlanItem(drag)) {
            ClearPlanDragFeedback();
            return;
        }
        var delta = point.Y < PlanDragScrollEdge ? -8 : point.Y > PlanScroll.ActualHeight - PlanDragScrollEdge ? 8 : 0;
        var offset = Math.Clamp(PlanScroll.VerticalOffset + delta, 0, PlanScroll.ScrollableHeight);
        if (offset != PlanScroll.VerticalOffset) {
            PlanScroll.ScrollToVerticalOffset(offset);
            PlanScroll.UpdateLayout();
            ShowPlanDragFeedback(drag, point);
        }
    }

    private void ClearPlanDragFeedback()
    {
        _planDragScroll.Stop();
        _planDragPoint = null;
        PlanDragFeedback.Visibility = Visibility.Collapsed;
    }
}
