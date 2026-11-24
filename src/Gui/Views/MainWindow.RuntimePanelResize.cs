using System.Globalization;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Point = System.Windows.Point;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Gui.Views;

public partial class MainWindow
{
    private const double MinimumRuntimeSidebarWidth = 340;
    private const double MaximumRuntimeSidebarWidth = 540;
    private const double RuntimeSidebarDragThreshold = 80;
    private const double RuntimeSidebarKeyboardStep = 10;

    private double? _preferredRuntimeSidebarWidth;
    private double? _savedRuntimeSidebarWidth;
    private RuntimeSidebarDrag? _runtimeSidebarDrag;

    private sealed class RuntimeSidebarDrag(double pointerOffset, double origin, double originWidth, bool collapsed)
    {
        public double PointerOffset { get; } = pointerOffset;
        public double Origin { get; set; } = origin;
        public double OriginWidth { get; set; } = originWidth;
        public bool Collapsed { get; set; } = collapsed;
        public double? Width { get; set; }
    }

    private void UpdateRuntimePanelLayout()
    {
        RuntimeSidebarVisibility(_section);
        UpdateSidebarMode(MainWindowContent.ActualWidth);
    }

    private void RuntimeSidebarVisibility(MainSection section)
    {
        var home = section == MainSection.Home;
        var visible = home && !RuntimePanelHidden;
        ShowRuntimePanelToggle(home);
        RuntimeSidebar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        RuntimeSidebarSplitter.Visibility = home ? Visibility.Visible : Visibility.Collapsed;

        // Use the allotted space even if the columns' minimum sizes cause ActualWidth to overflow.
        var homeWidth = LayoutInformation.GetLayoutSlot(HomeView).Width;
        var minimum = visible ? MinimumRuntimeSidebarWidth : 0;
        var maximum = visible ? Math.Max(minimum, Math.Min(MaximumRuntimeSidebarWidth,
            homeWidth - HomeView.ColumnDefinitions[0].MinWidth)) : 0;
        RuntimeSidebarColumn.MinWidth = minimum;
        RuntimeSidebarColumn.MaxWidth = maximum;
        var preferred = _runtimeSidebarDrag?.Width ?? _preferredRuntimeSidebarWidth ?? SidebarWidth(homeWidth);
        RuntimeSidebarColumn.Width = new GridLength(Math.Clamp(preferred, minimum, maximum));
    }

    private void RuntimeSidebarSplitter_DragStarted(object sender, DragStartedEventArgs e)
    {
        _runtimeSidebarDrag = new(e.HorizontalOffset, RuntimeSidebarPointerX(e.HorizontalOffset),
            RuntimeSidebarColumn.Width.Value, RuntimePanelCollapsed);
    }

    private void RuntimeSidebarSplitter_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_runtimeSidebarDrag is not { } drag) {
            return;
        }

        // WPF's delta is relative to the moving Thumb. Convert it to the window content's coordinates
        // so that resizing, collapsing, or moving the left sidebar does not shift the drag origin.
        var position = RuntimeSidebarPointerX(drag.PointerOffset + e.HorizontalChange);
        var change = position - drag.Origin;
        if (RuntimePanelHidden) {
            if (change <= -RuntimeSidebarDragThreshold) {
                SetRuntimeSidebarDragCollapsed(drag, position, collapsed: false);
            }
            return;
        }

        var requestedWidth = drag.OriginWidth - change;
        if (requestedWidth <= MinimumRuntimeSidebarWidth - RuntimeSidebarDragThreshold && !OnboardingNeedsRuntimePanel) {
            SetRuntimeSidebarDragCollapsed(drag, position, collapsed: true);
            return;
        }

        var width = Math.Clamp(requestedWidth, RuntimeSidebarColumn.MinWidth, RuntimeSidebarColumn.MaxWidth);
        if (width != RuntimeSidebarColumn.Width.Value) {
            drag.Width = width;
            RuntimeSidebarColumn.Width = new GridLength(width);
        }
    }

    private double RuntimeSidebarPointerX(double offset) =>
        RuntimeSidebarSplitter.TranslatePoint(new Point(offset, 0), MainWindowContent).X;

    private void SetRuntimeSidebarDragCollapsed(RuntimeSidebarDrag drag, double position, bool collapsed)
    {
        // Keep capture on the same Thumb. Reversing direction uses the point where the panel changed state,
        // even though the visible edge moves between the minimum width and the right side of the window.
        drag.Origin = position;
        drag.OriginWidth = MinimumRuntimeSidebarWidth;
        drag.Width = MinimumRuntimeSidebarWidth;
        drag.Collapsed = collapsed;
        UpdateRuntimePanelLayout();
    }

    private void RuntimeSidebarSplitter_DragCompleted(object sender, DragCompletedEventArgs e) =>
        FinishRuntimeSidebarDrag(e.Canceled);

    private bool CancelRuntimeSidebarDrag()
    {
        if (_runtimeSidebarDrag is null) {
            return false;
        }
        RuntimeSidebarSplitter.CancelDrag();
        FinishRuntimeSidebarDrag(canceled: true);
        return true;
    }

    private void FinishRuntimeSidebarDrag(bool canceled)
    {
        if (_runtimeSidebarDrag is not { } drag) {
            return;
        }
        _runtimeSidebarDrag = null;
        if (!canceled) {
            _preferredRuntimeSidebarWidth = drag.Width ?? _preferredRuntimeSidebarWidth;
            _settings.RuntimePanelCollapsed.Value = drag.Collapsed;
            SaveRuntimeSidebarWidth();
        }
        UpdateRuntimePanelLayout();
    }

    private void RuntimeSidebarSplitter_KeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key == Key.Escape && CancelRuntimeSidebarDrag()) {
            e.Handled = true;
            return;
        }
        if (e.Key is not (Key.Left or Key.Right)) {
            return;
        }

        e.Handled = true;
        if (_runtimeSidebarDrag is not null) {
            return;
        }
        if (RuntimePanelHidden) {
            if (e.Key == Key.Left) {
                _preferredRuntimeSidebarWidth = MinimumRuntimeSidebarWidth;
                _settings.RuntimePanelCollapsed.Value = false;
                SaveRuntimeSidebarWidth();
            }
            return;
        }

        var width = RuntimeSidebarColumn.Width.Value;
        var change = e.Key == Key.Left ? RuntimeSidebarKeyboardStep : -RuntimeSidebarKeyboardStep;
        var adjusted = Math.Clamp(width + change, RuntimeSidebarColumn.MinWidth, RuntimeSidebarColumn.MaxWidth);
        if (adjusted != width) {
            _preferredRuntimeSidebarWidth = adjusted;
            RuntimeSidebarColumn.Width = new GridLength(adjusted);
        }
    }

    private void RuntimeSidebarSplitter_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        SaveRuntimeSidebarWidth();

    private string RuntimeSidebarWidthPath =>
        Path.Combine(_applicationDirectory, "config", "runtime-sidebar-width.txt");

    private double? LoadRuntimeSidebarWidth()
    {
        try {
            if (!File.Exists(RuntimeSidebarWidthPath)) {
                return null;
            }
            var text = File.ReadAllText(RuntimeSidebarWidthPath).Trim();
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var width)
                || !double.IsFinite(width) || width <= 0) {
                throw new InvalidDataException($"运行面板宽度必须为正数：{text}");
            }
            return width;
        } catch (Exception exception) {
            _logger.Warn("读取运行面板宽度失败，已使用默认宽度。", exception);
            return null;
        }
    }

    private void SaveRuntimeSidebarWidth()
    {
        if (_preferredRuntimeSidebarWidth is not { } width || width == _savedRuntimeSidebarWidth) {
            return;
        }
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(RuntimeSidebarWidthPath)!);
            File.WriteAllText(RuntimeSidebarWidthPath, width.ToString("R", CultureInfo.InvariantCulture));
            _savedRuntimeSidebarWidth = width;
        } catch (Exception exception) {
            _logger.Warn("保存运行面板宽度失败。", exception);
        }
    }

    internal static double SidebarWidth(double homeWidth) =>
        Math.Clamp(Math.Round(homeWidth * 0.35), MinimumRuntimeSidebarWidth, MaximumRuntimeSidebarWidth);
}
