using System.Globalization;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfCursors = System.Windows.Input.Cursors;
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;
using WpfPen = System.Windows.Media.Pen;
using WpfPoint = System.Windows.Point;
using WpfSize = System.Windows.Size;
using WpfToolTip = System.Windows.Controls.ToolTip;

namespace Gui.Views;

// Active: a time of the policy being edited. Sibling: another policy of the same configuration. Other: another
// configuration, which can only be selected here.
internal enum TimelinePinRole { Active, Sibling, Other }

internal sealed record TimelinePin(Guid ConfigurationId, Guid PolicyId, DayOfWeek Day, TimeOnly Time,
    TimelinePinRole Role, bool Enabled, bool Skipped, double Offset, string Tip);

// One week, Monday first, 24 hours across. Drawn in OnRender so many points stay cheap. Clicking an empty track picks
// a time in 15-minute steps; dragging an active point moves it in 5-minute steps; exact minutes go through the editor.
internal sealed class ScheduleTimeline : FrameworkElement
{
    private const double Gutter = 48, AxisHeight = 18, RowHeight = 29;
    private const double PinWidth = 6, PinHeight = 16;
    private const int AddStep = 15, DragStep = 5;

    private static DependencyProperty RegisterBrush(string name) => DependencyProperty.Register(
        name, typeof(SolidColorBrush), typeof(ScheduleTimeline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty GridBrushProperty = RegisterBrush("GridBrush");
    private static readonly DependencyProperty TrackBrushProperty = RegisterBrush("TrackBrush");
    private static readonly DependencyProperty NowBrushProperty = RegisterBrush("NowBrush");
    private static readonly DependencyProperty WarningBrushProperty = RegisterBrush("WarningBrush");
    private static readonly DependencyProperty MutedBrushProperty = RegisterBrush("MutedBrush");
    private static readonly DependencyProperty AxisBrushProperty = RegisterBrush("AxisBrush");
    private static readonly DependencyProperty SelectedBrushProperty = RegisterBrush("SelectedBrush");
    private static readonly DependencyProperty SiblingBrushProperty = RegisterBrush("SiblingBrush");
    private static readonly DependencyProperty OtherBrushProperty = RegisterBrush("OtherBrush");
    private static readonly DependencyProperty DisabledBrushProperty = RegisterBrush("DisabledBrush");
    private static readonly DependencyProperty AccentSurfaceBrushProperty = RegisterBrush("AccentSurfaceBrush");
    private static readonly DependencyProperty AccentOutlineBrushProperty = RegisterBrush("AccentOutlineBrush");
    private static readonly DependencyProperty PrimaryBrushProperty = RegisterBrush("PrimaryBrush");
    private static readonly DependencyProperty InverseBrushProperty = RegisterBrush("InverseBrush");

    private readonly WpfToolTip _tip = new() { Placement = PlacementMode.Relative };
    private IReadOnlyList<TimelinePin> _pins = [];
    private TimelinePin? _pressed, _dragging, _hoverPin;
    private (IReadOnlySet<DayOfWeek> Days, TimeOnly Time, bool Remove)? _preview;
    private int _dragMinutes;
    private bool _moved;
    private (DayOfWeek Day, int Minutes)? _hover, _pressedEmpty;

    // Taking focus on a press lets a time being typed in the editor commit before the click changes anything.
    public ScheduleTimeline()
    {
        SetResourceReference(GridBrushProperty, "Brush.Timeline.Grid");
        SetResourceReference(TrackBrushProperty, "Brush.Timeline.Track");
        SetResourceReference(NowBrushProperty, "Brush.Timeline.Now");
        SetResourceReference(WarningBrushProperty, "Brush.Timeline.Skipped");
        SetResourceReference(MutedBrushProperty, "Brush.Text.Muted");
        SetResourceReference(AxisBrushProperty, "Brush.Text.Disabled");
        SetResourceReference(SelectedBrushProperty, "Brush.Timeline.Selected");
        SetResourceReference(SiblingBrushProperty, "Brush.Timeline.Sibling");
        SetResourceReference(OtherBrushProperty, "Brush.Timeline.Other");
        SetResourceReference(DisabledBrushProperty, "Brush.Timeline.Inactive");
        SetResourceReference(AccentSurfaceBrushProperty, "Brush.Schedule.AccentSurface");
        SetResourceReference(AccentOutlineBrushProperty, "Brush.Primary.Border");
        SetResourceReference(PrimaryBrushProperty, "Brush.Text.Primary");
        SetResourceReference(InverseBrushProperty, "Brush.Text.Inverse");
        _tip.PlacementTarget = this;
        Focusable = true;
        FocusVisualStyle = null;
    }

    private SolidColorBrush BrushFor(DependencyProperty property) => (SolidColorBrush)GetValue(property);

    internal event Action<DayOfWeek, TimeOnly>? EmptyClicked;
    internal event Action<DayOfWeek>? DayClicked;
    internal event Action<TimelinePin>? PinClicked;
    internal event Action<TimelinePin, TimeOnly>? PinMoved;
    internal event Action<TimelinePin>? PinDoubleClicked;

    internal IReadOnlyList<TimelinePin> Pins
    {
        get => _pins;
        set { _pins = value; InvalidateVisual(); }
    }

    // While a scope menu is open, the option under the pointer shows which days it would add to or remove from.
    internal (IReadOnlySet<DayOfWeek> Days, TimeOnly Time, bool Remove)? Preview
    {
        get => _preview;
        set { _preview = value; InvalidateVisual(); }
    }

    internal IReadOnlySet<DayOfWeek> ActiveDays { get; set; } = new HashSet<DayOfWeek>();
    internal (Guid PolicyId, TimeOnly Time)? Selected { get; set; }
    internal DateTime Clock { get; set; } = DateTime.Now;

    protected override WpfSize MeasureOverride(WpfSize available) => new(
        double.IsInfinity(available.Width) ? 640 : available.Width, AxisHeight + 7 * RowHeight);

    private double TrackWidth => Math.Max(1, ActualWidth - Gutter);
    private double X(int minutes) => Gutter + minutes / 1440.0 * TrackWidth;
    private double RowTop(DayOfWeek day) =>
        AxisHeight + Array.IndexOf(SchedulePreview.WeekOrder, day) * RowHeight;
    private static int Minutes(TimeOnly time) => time.Hour * 60 + time.Minute;
    private static TimeOnly FromMinutes(int minutes) => new(minutes / 60, minutes % 60);

    internal WpfPoint ScopeMenuAnchor(DayOfWeek day, TimeOnly time, double width, bool remove)
    {
        var x = X(Minutes(time)) + (remove ? 8 - width : -8);
        return new WpfPoint(Math.Clamp(x, 0, Math.Max(0, ActualWidth - width)), RowTop(day) + RowHeight + 4);
    }

    private int MinutesAt(double x, int step)
    {
        var raw = Math.Clamp((x - Gutter) / TrackWidth, 0, 1) * 1440;
        return Math.Min((int)Math.Round(raw / step) * step, 1440 - step);
    }

    private WpfPoint PinPoint(TimelinePin pin) =>
        new(X(Minutes(pin.Time)), RowTop(pin.Day) + RowHeight / 2 + pin.Offset * 0.34);

    protected override void OnRender(DrawingContext dc)
    {
        var gridPen = new WpfPen(BrushFor(GridBrushProperty), 1);
        var muted = BrushFor(MutedBrushProperty);
        var primary = BrushFor(PrimaryBrushProperty);
        var nowBrush = BrushFor(NowBrushProperty);
        var inverse = BrushFor(InverseBrushProperty);
        var dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var family = TryFindResource("FontFamily.Ui") as WpfFontFamily ?? new WpfFontFamily("Segoe UI");
        var regular = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var medium = new Typeface(family, FontStyles.Normal, FontWeights.Medium, FontStretches.Normal);
        FormattedText Text(string text, double size, WpfBrush brush, bool strong = false) => new(text,
            CultureInfo.CurrentUICulture, System.Windows.FlowDirection.LeftToRight, strong ? medium : regular, size,
            brush, dip);

        dc.DrawRectangle(WpfBrushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        for (var hour = 0; hour <= 24; hour += 6) {
            var label = Text($"{hour}:00", 12, BrushFor(AxisBrushProperty));
            var offset = hour == 0 ? 0 : hour == 24 ? label.Width : label.Width / 2;
            dc.DrawText(label, new WpfPoint(X(hour * 60) - offset, 0));
        }

        var today = Clock.DayOfWeek;
        foreach (var day in SchedulePreview.WeekOrder) {
            var top = RowTop(day);
            var label = Text(SchedulePreview.DayName(day), 14, day == today ? primary : muted, day == today);
            dc.DrawText(label, new WpfPoint(0, top + (RowHeight - label.Height) / 2));
            if (ActiveDays.Contains(day)) {
                dc.DrawRectangle(BrushFor(TrackBrushProperty), null, new Rect(Gutter, top, TrackWidth, RowHeight));
            }
            dc.DrawLine(gridPen, new WpfPoint(Gutter, top + 0.5), new WpfPoint(ActualWidth, top + 0.5));
        }
        var bottom = AxisHeight + 7 * RowHeight - 0.5;
        dc.DrawLine(gridPen, new WpfPoint(Gutter, bottom), new WpfPoint(ActualWidth, bottom));
        for (var hour = 0; hour <= 24; hour += 6) {
            var x = Math.Min(ActualWidth - 0.5, X(hour * 60) + 0.5);
            dc.DrawLine(gridPen, new WpfPoint(x, AxisHeight), new WpfPoint(x, bottom));
        }

        var nowX = X(Clock.Hour * 60 + Clock.Minute);
        dc.DrawRoundedRectangle(nowBrush, null, new Rect(nowX - 1, RowTop(today), 2, RowHeight), 1, 1);

        // Clip endpoint markers to the time grid. Nearby points remain selectable through their original hit regions.
        dc.PushClip(new RectangleGeometry(new Rect(Gutter, AxisHeight, TrackWidth, RowHeight * 7)));
        foreach (var pin in _pins.OrderBy(pin => pin.Skipped ? 3 : pin.Role switch {
            TimelinePinRole.Other => 0, TimelinePinRole.Sibling => 1, _ => 2 })) {
            DrawPin(dc, pin);
        }

        if (_preview is { Remove: false } added) {
            var pen = DashedPen(nowBrush, 1);
            foreach (var day in added.Days) {
                var point = new WpfPoint(X(Minutes(added.Time)), RowTop(day) + RowHeight / 2);
                dc.DrawRoundedRectangle(BrushFor(AccentSurfaceBrushProperty), pen, PinRect(point), 3, 3);
            }
        }
        dc.Pop();

        if (_dragging is not null) {
            var point = DrawnPoint(_dragging);
            DrawTag(dc, Text(SchedulePreview.TimeText(FromMinutes(_dragMinutes)), 12, inverse, true),
                point.X, point.Y - 25, nowBrush);
        } else if (_hover is { } hover && _preview is null) {
            var top = RowTop(hover.Day);
            var x = X(hover.Minutes);
            dc.DrawLine(new WpfPen(nowBrush, 1), new WpfPoint(x, top + 2), new WpfPoint(x, top + RowHeight - 2));
            var time = SchedulePreview.TimeText(FromMinutes(hover.Minutes));
            DrawTag(dc, Text((ActiveDays.Contains(hover.Day) ? "+ " : "新建 ") + time, 12, inverse, true),
                x, top - 16, nowBrush);
        }
    }

    private bool IsDragging(TimelinePin pin) => _dragging is { } drag && drag.PolicyId == pin.PolicyId
        && drag.Time == pin.Time;

    private WpfPoint DrawnPoint(TimelinePin pin)
    {
        var point = PinPoint(pin);
        if (IsDragging(pin)) {
            point.X = X(_dragMinutes);
        }
        return point;
    }

    private static Rect PinRect(WpfPoint point) =>
        new(point.X - PinWidth / 2, point.Y - PinHeight / 2, PinWidth, PinHeight);

    private static WpfPen DashedPen(WpfBrush brush, double thickness) =>
        new(brush, thickness) { DashStyle = new DashStyle([2, 2], 0) };

    private void DrawPin(DrawingContext dc, TimelinePin pin)
    {
        var rect = PinRect(DrawnPoint(pin));
        var removed = _preview is { Remove: true } preview && pin.Role == TimelinePinRole.Active
            && preview.Days.Contains(pin.Day) && preview.Time == pin.Time;
        var emphasis = pin.Role == TimelinePinRole.Active &&
            (_hoverPin is { } hovered && hovered.PolicyId == pin.PolicyId && hovered.Time == pin.Time
                || (pin.PolicyId, pin.Time) == Selected);
        if (removed || emphasis) {
            var outline = rect;
            outline.Inflate(2, 2);
            dc.DrawRoundedRectangle(null, new WpfPen(BrushFor(AccentOutlineBrushProperty), 1.5), outline, 4, 4);
        }
        if (removed) {
            dc.PushOpacity(0.3);
        }
        if (pin.Enabled) {
            var brush = BrushFor(pin.Skipped ? WarningBrushProperty : pin.Role switch {
                TimelinePinRole.Active => SelectedBrushProperty,
                TimelinePinRole.Sibling => SiblingBrushProperty,
                _ => OtherBrushProperty
            });
            dc.DrawRoundedRectangle(brush, null, rect, 3, 3);
        } else {
            rect.Inflate(-0.5, -0.5);
            dc.DrawRoundedRectangle(null, DashedPen(BrushFor(DisabledBrushProperty), 1), rect, 2.5, 2.5);
        }
        if (removed) {
            dc.Pop();
        }
    }

    private static void DrawTag(DrawingContext dc, FormattedText label, double x, double y, WpfBrush background)
    {
        var box = new Rect(x - label.Width / 2 - 5, y - 1, label.Width + 10, label.Height + 2);
        dc.DrawRoundedRectangle(background, null, box, 3, 3);
        dc.DrawText(label, new WpfPoint(box.Left + 5, y));
    }

    private TimelinePin? HitPin(WpfPoint point) => _pins
        .Select(pin => (Pin: pin, Distance: (PinPoint(pin) - point).Length))
        .Where(item => item.Distance <= 9)
        .OrderBy(item => item.Pin.Role == TimelinePinRole.Active ? 0 : 1).ThenBy(item => item.Distance)
        .Select(item => item.Pin).FirstOrDefault();

    private DayOfWeek? DayAt(WpfPoint point)
    {
        var row = (int)Math.Floor((point.Y - AxisHeight) / RowHeight);
        return row is >= 0 and < 7 ? SchedulePreview.WeekOrder[row] : null;
    }

    protected override void OnMouseMove(WpfMouseEventArgs e)
    {
        var point = e.GetPosition(this);
        if (_pressed is { Role: TimelinePinRole.Active } pressed && e.LeftButton == MouseButtonState.Pressed) {
            if (_dragging is null && Math.Abs(point.X - PinPoint(pressed).X) < 3) {
                return;
            }
            _dragging = pressed;
            _dragMinutes = MinutesAt(point.X, DragStep);
            _moved |= _dragMinutes != Minutes(pressed.Time);
            _tip.IsOpen = false;
            InvalidateVisual();
            return;
        }
        if (HitPin(point) is { } pin) {
            ShowTip(pin);
            Cursor = pin.Role == TimelinePinRole.Active ? WpfCursors.SizeWE : WpfCursors.Hand;
            SetHover(null, pin);
            return;
        }
        _tip.IsOpen = false;
        var day = DayAt(point);
        var inTrack = day is not null && point.X >= Gutter && point.X <= Gutter + TrackWidth;
        Cursor = inTrack ? WpfCursors.Cross : day is not null && point.X < Gutter ? WpfCursors.Hand : WpfCursors.Arrow;
        SetHover(inTrack ? (day!.Value, MinutesAt(point.X, AddStep)) : null);
    }

    protected override void OnMouseLeave(WpfMouseEventArgs e)
    {
        _tip.IsOpen = false;
        SetHover(null);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        var point = e.GetPosition(this);
        _pressed = HitPin(point);
        _pressedEmpty = null;
        _moved = false;
        if (_pressed is { } pin) {
            if (e.ClickCount == 2 && pin.Role == TimelinePinRole.Active) {
                _pressed = null;
                _tip.IsOpen = false;
                SetHover(null);
                PinDoubleClicked?.Invoke(pin);
            } else {
                CaptureMouse();
            }
            e.Handled = true;
            return;
        }
        if (DayAt(point) is not { } day) {
            return;
        }
        if (point.X < Gutter) {
            DayClicked?.Invoke(day);
        } else if (point.X <= Gutter + TrackWidth) {
            _pressedEmpty = (day, MinutesAt(point.X, AddStep));
        }
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        var (pressed, dragging, empty) = (_pressed, _dragging, _pressedEmpty);
        _pressed = _dragging = null;
        _pressedEmpty = null;
        if (IsMouseCaptured) {
            ReleaseMouseCapture();
        }
        if (dragging is not null && _moved) {
            PinMoved?.Invoke(dragging, FromMinutes(_dragMinutes));
        } else if (pressed is not null) {
            PinClicked?.Invoke(pressed);
        } else if (empty is { } at) {
            EmptyClicked?.Invoke(at.Day, FromMinutes(at.Minutes));
        }
        InvalidateVisual();
        e.Handled = true;
    }

    private void SetHover((DayOfWeek, int)? hover, TimelinePin? pin = null)
    {
        if (_hover != hover || _hoverPin != pin) {
            (_hover, _hoverPin) = (hover, pin);
            InvalidateVisual();
        }
    }

    private void ShowTip(TimelinePin pin)
    {
        if (_preview is not null) {
            return;
        }
        var point = PinPoint(pin);
        _tip.Content = pin.Tip;
        _tip.HorizontalOffset = point.X + 10;
        _tip.VerticalOffset = point.Y - 46;
        _tip.IsOpen = true;
    }
}
