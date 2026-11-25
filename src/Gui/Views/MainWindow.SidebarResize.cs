using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Point = System.Windows.Point;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Gui.Views;

public partial class MainWindow
{
    private const double MinimumSidebarWidth = 168;
    private const double MaximumSidebarWidth = 420;
    private const double SidebarOpenDragDistance = 16;
    private const double SidebarKeyboardStep = 10;
    private const string SidebarWidthFile = "sidebar-width.txt";

    private double? _preferredSidebarWidth;
    private double? _savedSidebarWidth;
    private SidebarDrag? _sidebarDrag;

    private double SidebarPreferredWidth => _sidebarDrag?.Width ?? _preferredSidebarWidth ?? ShellSidebar.DefaultWidth;
    private double ReservedRuntimeSidebarWidth => RuntimePanelHidden ? 0 : _section == MainSection.Home
        ? Math.Clamp(RuntimeSidebarPreferredWidth, MinimumRuntimeSidebarWidth, MaximumRuntimeSidebarWidth) : 0;

    private sealed class SidebarDrag(double pointerOffset, double pointerStart, double width, bool collapsed)
    {
        public double PointerOffset { get; } = pointerOffset;
        public double PointerStart { get; } = pointerStart;
        public double InitialWidth { get; } = width;
        public bool StartedCollapsed { get; } = collapsed;
        public double? Width { get; set; }
    }

    private void InitializeSidebarResize()
    {
        _savedSidebarWidth = LoadSidebarWidth(SidebarWidthFile, "左侧栏");
        if (_savedSidebarWidth is { } width) {
            _preferredSidebarWidth = Math.Clamp(width, MinimumSidebarWidth, MaximumSidebarWidth);
        }
        Sidebar.SidebarResizeHandle.DragStarted += SidebarResize_DragStarted;
        Sidebar.SidebarResizeHandle.DragDelta += SidebarResize_DragDelta;
        Sidebar.SidebarResizeHandle.DragCompleted += (_, e) => FinishSidebarDrag(e.Canceled);
        Sidebar.SidebarResizeHandle.KeyDown += SidebarResize_KeyDown;
        Sidebar.SidebarResizeHandle.LostKeyboardFocus += (_, _) => SaveLeftSidebarWidth();
    }

    private double SidebarWidthLimit() => _sidebarOverlayMode ? MaximumSidebarWidth
        : Math.Clamp(MainWindowContent.ActualWidth - HomeView.ColumnDefinitions[0].MinWidth - ReservedRuntimeSidebarWidth,
            MinimumSidebarWidth, MaximumSidebarWidth);

    private double SidebarPointerX(double offset) =>
        Sidebar.SidebarResizeHandle.TranslatePoint(new Point(offset, 0), MainWindowContent).X;

    private void SidebarResize_DragStarted(object sender, DragStartedEventArgs e)
    {
        _sidebarDrag = new(e.HorizontalOffset, SidebarPointerX(e.HorizontalOffset), Sidebar.Width, Sidebar.IsCollapsed);
    }

    private void SidebarResize_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_sidebarDrag is not { } drag) {
            return;
        }
        // Thumb deltas are relative to the edge, which moves each time the column resizes.
        var change = SidebarPointerX(drag.PointerOffset + e.HorizontalChange) - drag.PointerStart;
        if (drag.StartedCollapsed && change < SidebarOpenDragDistance) {
            drag.Width = null;
        } else {
            var desired = drag.StartedCollapsed ? MinimumSidebarWidth + change - SidebarOpenDragDistance
                : drag.InitialWidth + change;
            drag.Width = Math.Clamp(desired, MinimumSidebarWidth, SidebarWidthLimit());
        }
        ShowSidebar(drag.StartedCollapsed && drag.Width is null);
        UpdateRuntimePanelLayout();
    }

    private void CancelSidebarDrag()
    {
        if (_sidebarDrag is not null) {
            Sidebar.SidebarResizeHandle.CancelDrag();
            FinishSidebarDrag(canceled: true);
        }
    }

    private void FinishSidebarDrag(bool canceled)
    {
        if (_sidebarDrag is not { } drag) {
            return;
        }
        _sidebarDrag = null;
        if (!canceled && drag.Width is { } width) {
            _preferredSidebarWidth = width;
            if (drag.StartedCollapsed) {
                _settings.SidebarCollapsed.Value = false;
            }
            SaveLeftSidebarWidth();
        }
        ShowSidebar(canceled ? drag.StartedCollapsed : Sidebar.IsCollapsed);
        UpdateRuntimePanelLayout();
    }

    private void SidebarResize_KeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key == Key.Escape && _sidebarDrag is not null) {
            CancelSidebarDrag();
            e.Handled = true;
            return;
        }
        if (e.Key is not (Key.Left or Key.Right)) {
            return;
        }
        e.Handled = true;
        if (_sidebarDrag is not null || (Sidebar.IsCollapsed && e.Key == Key.Left)) {
            return;
        }
        if (Sidebar.IsCollapsed) {
            _settings.SidebarCollapsed.Value = false;
            _preferredSidebarWidth = MinimumSidebarWidth;
        } else {
            var change = e.Key == Key.Left ? -SidebarKeyboardStep : SidebarKeyboardStep;
            _preferredSidebarWidth = Math.Clamp(Sidebar.Width + change, MinimumSidebarWidth, SidebarWidthLimit());
        }
        ShowSidebar(collapsed: false);
        UpdateRuntimePanelLayout();
    }

    private void SaveLeftSidebarWidth() =>
        SaveSidebarWidth(SidebarWidthFile, "左侧栏", _preferredSidebarWidth, ref _savedSidebarWidth);
}
