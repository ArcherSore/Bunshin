using System.Globalization;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
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

internal sealed record TimelinePin(Guid ConfigurationId, Guid PolicyId, DayOfWeek Day, TimeOnly Time, int PaletteIndex,
    TimelinePinRole Role, bool Enabled, bool Skipped, double Offset, string Tip);

// One week, Monday first, 24 hours across. Drawn in OnRender so many points stay cheap. Clicking an empty track picks
// a time in 15-minute steps; dragging an active point moves it in 5-minute steps; exact minutes go through the editor.
internal sealed class ScheduleTimeline : FrameworkElement
{
    private const double Gutter = 66, AxisHeight = 22, RightPad = 12, Bottom = 4;
    // Rows share whatever height the page gives the timeline; tracks grow slower than rows so tall rows keep a gap.
    // The smallest row that still reads well; below it the editor column scrolls instead.
    private const double MinRow = 20;
    private const int AddStep = 15, DragStep = 5;
    internal static readonly IReadOnlyList<string> PaletteKeys = [
        "Brush.Chart.Category1", "Brush.Chart.Category2", "Brush.Chart.Category3",
        "Brush.Chart.Category4", "Brush.Chart.Category5", "Brush.Chart.Category6"
    ];

    // Each brush invalidates the drawing independently, including a palette slot used only by another configuration.
    private static DependencyProperty RegisterBrush(string name) => DependencyProperty.Register(
        name, typeof(SolidColorBrush), typeof(ScheduleTimeline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty[] CategoryBrushes = PaletteKeys
        .Select((_, index) => RegisterBrush($"CategoryBrush{index + 1}")).ToArray();
    private static readonly DependencyProperty GridBrushProperty = RegisterBrush("GridBrush");
    private static readonly DependencyProperty GridStrongBrushProperty = RegisterBrush("GridStrongBrush");
    private static readonly DependencyProperty HatchBrushProperty = RegisterBrush("HatchBrush");
    private static readonly DependencyProperty NightBrushProperty = RegisterBrush("NightBrush");
    private static readonly DependencyProperty TrackBrushProperty = RegisterBrush("TrackBrush");
    private static readonly DependencyProperty NowBrushProperty = RegisterBrush("NowBrush");
    private static readonly DependencyProperty WarningBrushProperty = RegisterBrush("WarningBrush");
    private static readonly DependencyProperty MutedBrushProperty = RegisterBrush("MutedBrush");
    private static readonly DependencyProperty DisabledBrushProperty = RegisterBrush("DisabledBrush");
    private static readonly DependencyProperty SurfaceBrushProperty = RegisterBrush("SurfaceBrush");
    private static readonly DependencyProperty InverseBrushProperty = RegisterBrush("InverseBrush");
    private static readonly DependencyProperty PrimaryBrushProperty = RegisterBrush("PrimaryBrush");
    private static readonly DependencyProperty TodayBorderBrushProperty = RegisterBrush("TodayBorderBrush");

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
        SetResourceReference(GridStrongBrushProperty, "Brush.Timeline.GridStrong");
        SetResourceReference(HatchBrushProperty, "Brush.Timeline.Hatch");
        SetResourceReference(NightBrushProperty, "Brush.Timeline.Night");
        SetResourceReference(TrackBrushProperty, "Brush.Timeline.Track");
        SetResourceReference(NowBrushProperty, "Brush.Timeline.Now");
        SetResourceReference(WarningBrushProperty, "Brush.Timeline.Warning");
        SetResourceReference(MutedBrushProperty, "Brush.Text.Muted");
        SetResourceReference(DisabledBrushProperty, "Brush.Timeline.Inactive");
        SetResourceReference(SurfaceBrushProperty, "Brush.Surface");
        SetResourceReference(InverseBrushProperty, "Brush.Text.Inverse");
        SetResourceReference(PrimaryBrushProperty, "Brush.Primary");
        SetResourceReference(TodayBorderBrushProperty, "Brush.Primary.Border");
        for (var index = 0; index < CategoryBrushes.Length; index++) {
            SetResourceReference(CategoryBrushes[index], PaletteKeys[index]);
        }
        _tip.PlacementTarget = this;
        Focusable = true;
        FocusVisualStyle = null;
    }

    private SolidColorBrush BrushFor(DependencyProperty property) => (SolidColorBrush)GetValue(property);
    private SolidColorBrush CategoryBrush(int index) => BrushFor(CategoryBrushes[index]);
    private SolidColorBrush ActiveBrush => CategoryBrush(ActivePaletteIndex);

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
    internal int ActivePaletteIndex { get; set; }
    internal (Guid PolicyId, TimeOnly Time)? Selected { get; set; }
    internal DateTime Clock { get; set; } = DateTime.Now;

    // A scrolling page measures with unlimited height and then stretches the timeline into the space left over.
    protected override WpfSize MeasureOverride(WpfSize available) => new(
        double.IsInfinity(available.Width) ? 640 : available.Width,
        double.IsInfinity(available.Height) ? AxisHeight + 7 * MinRow + Bottom
            : Math.Max(available.Height, AxisHeight + 7 * MinRow + Bottom));

    private double RowHeight => Math.Max(MinRow, (ActualHeight - AxisHeight - Bottom) / 7);
    private double TrackHeight => Math.Min(RowHeight - 6, 8 + RowHeight * 0.7);
    private double TrackTop(DayOfWeek day) => RowTop(day) + (RowHeight - TrackHeight) / 2;

    private double TrackWidth => Math.Max(1, ActualWidth - Gutter - RightPad);
    private double X(int minutes) => Gutter + minutes / 1440.0 * TrackWidth;
    private double RowTop(DayOfWeek day) =>
        AxisHeight + Array.IndexOf(SchedulePreview.WeekOrder, day) * RowHeight;
    private static int Minutes(TimeOnly time) => time.Hour * 60 + time.Minute;
    private static TimeOnly FromMinutes(int minutes) => new(minutes / 60, minutes % 60);

    private int MinutesAt(double x, int step)
    {
        var raw = Math.Clamp((x - Gutter) / TrackWidth, 0, 1) * 1440;
        return Math.Min((int)Math.Round(raw / step) * step, 1440 - step);
    }

    private WpfPoint PinPoint(TimelinePin pin) =>
        new(X(Minutes(pin.Time)), RowTop(pin.Day) + RowHeight / 2
            + pin.Offset * Math.Clamp((TrackHeight - 10) / 28, 0, 1)); // Short rows squeeze the lanes together.

    protected override void OnRender(DrawingContext dc)
    {
        var gridPen = new WpfPen(BrushFor(GridBrushProperty), 1);
        var gridStrongPen = new WpfPen(BrushFor(GridStrongBrushProperty), 1);
        var hatchPen = new WpfPen(BrushFor(HatchBrushProperty), 1);
        var muted = BrushFor(MutedBrushProperty);
        var nowBrush = BrushFor(NowBrushProperty);
        var inverse = BrushFor(InverseBrushProperty);
        var dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var family = TryFindResource("FontFamily.Ui") as WpfFontFamily ?? new WpfFontFamily("Segoe UI");
        var regular = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var bold = new Typeface(family, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        FormattedText Text(string text, double size, WpfBrush brush, bool strong) => new(text,
            CultureInfo.CurrentUICulture, System.Windows.FlowDirection.LeftToRight, strong ? bold : regular, size,
            brush, dip);

        dc.DrawRectangle(WpfBrushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        for (var hour = 0; hour <= 24; hour += 3) {
            var label = Text(hour.ToString("00"), 10.5, muted, false);
            dc.DrawText(label, new WpfPoint(X(hour * 60) - label.Width / 2, 3));
        }

        var today = Clock.DayOfWeek;
        var now = Clock.Hour * 60 + Clock.Minute;
        foreach (var day in SchedulePreview.WeekOrder) {
            var top = RowTop(day);
            var track = new Rect(Gutter, TrackTop(day), TrackWidth, TrackHeight);
            var active = ActiveDays.Contains(day);
            var label = Text(SchedulePreview.DayName(day), 12, active ? ActiveBrush : muted, active);
            dc.DrawText(label, new WpfPoint(Gutter - 14 - label.Width, top + (RowHeight - label.Height) / 2));
            if (active) {
                dc.DrawRoundedRectangle(ActiveBrush, null,
                    new Rect(6, top + RowHeight / 2 - 8, 3, 16), 1.5, 1.5);
            }

            dc.PushClip(new RectangleGeometry(track, 6, 6));
            dc.DrawRectangle(BrushFor(TrackBrushProperty), null, track);
            dc.DrawRectangle(BrushFor(NightBrushProperty), null, new Rect(X(0), track.Top, X(360) - X(0), track.Height));
            dc.DrawRectangle(BrushFor(NightBrushProperty), null, new Rect(X(1320), track.Top, X(1440) - X(1320), track.Height));
            for (var hour = 1; hour < 24; hour++) {
                var x = Math.Round(X(hour * 60)) + 0.5;
                dc.DrawLine(hour % 6 == 0 ? gridStrongPen : gridPen, new WpfPoint(x, track.Top),
                    new WpfPoint(x, track.Bottom));
            }
            if (day == today) {
                // Today's elapsed part: minutes that have passed are never caught up on.
                dc.PushClip(new RectangleGeometry(new Rect(track.Left, track.Top, X(now) - track.Left, track.Height)));
                for (var x = track.Left - track.Height; x < X(now); x += 6) {
                    dc.DrawLine(hatchPen, new WpfPoint(x, track.Bottom), new WpfPoint(x + track.Height, track.Top));
                }
                dc.Pop();
            }
            dc.Pop();
            if (day == today) {
                dc.DrawRoundedRectangle(null, new WpfPen(BrushFor(TodayBorderBrushProperty), 1), track, 6, 6);
            }
            if (_preview is { } preview && preview.Days.Contains(day)) {
                var tint = (preview.Remove ? nowBrush : ActiveBrush).Color;
                dc.DrawRoundedRectangle(new SolidColorBrush(WpfColor.FromArgb(0x1C, tint.R, tint.G, tint.B)),
                    new WpfPen(new SolidColorBrush(WpfColor.FromArgb(0x80, tint.R, tint.G, tint.B)), 1), track, 6, 6);
            }
        }

        // Marks and labels go on top of every point so a covered point still shows that it will be skipped.
        var ordered = _pins.OrderBy(p => p.Role switch {
            TimelinePinRole.Other => 0, TimelinePinRole.Sibling => 1, _ => 2 }).ToArray();
        foreach (var pin in ordered) {
            DrawPin(dc, pin);
        }
        foreach (var pin in ordered) {
            DrawPinMarks(dc, pin, Text);
        }

        if (_preview is { Remove: false } added) {
            var pen = new WpfPen(ActiveBrush, 2) { DashStyle = new DashStyle([1.5, 1.5], 0) };
            foreach (var day in added.Days) {
                var point = new WpfPoint(X(Minutes(added.Time)), RowTop(day) + RowHeight / 2);
                dc.DrawEllipse(BrushFor(SurfaceBrushProperty), pen, point, 6, 6);
                var label = Text(SchedulePreview.TimeText(added.Time), 10, ActiveBrush, true);
                dc.DrawText(label, new WpfPoint(point.X + 11, point.Y - label.Height / 2));
            }
        }

        var primary = BrushFor(PrimaryBrushProperty);
        if (_hover is { } hover && _dragging is null && _preview is null) {
            var top = TrackTop(hover.Day);
            var x = Math.Round(X(hover.Minutes)) + 0.5;
            dc.DrawLine(new WpfPen(primary, 1), new WpfPoint(x, top + 2),
                new WpfPoint(x, top + TrackHeight - 2));
            var time = SchedulePreview.TimeText(FromMinutes(hover.Minutes));
            DrawTag(dc, Text((ActiveDays.Contains(hover.Day) ? "+ " : "新建 ") + time, 10, inverse, true),
                x, top - 10, primary);
        }

        var nowX = Math.Round(X(now)) + 0.5;
        var todayTop = TrackTop(today) - 3;
        dc.DrawLine(new WpfPen(nowBrush, 2), new WpfPoint(nowX, todayTop),
            new WpfPoint(nowX, todayTop + TrackHeight + 6));
        DrawTag(dc, Text("现在 " + Clock.ToString("HH\\:mm"), 10, inverse, true), nowX, todayTop - 10,
            nowBrush);
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

    private static double Radius(TimelinePin pin) =>
        pin.Role switch { TimelinePinRole.Active => 7.0, TimelinePinRole.Other => 4.5, _ => 5.0 };

    private void DrawPin(DrawingContext dc, TimelinePin pin)
    {
        var point = DrawnPoint(pin);
        var radius = Radius(pin);
        var brush = CategoryBrush(pin.PaletteIndex);
        // A policy's times are shared by all its days, so pointing at one shows every point that changes with it.
        if (pin.Role == TimelinePinRole.Active && _hoverPin is { Role: TimelinePinRole.Active } hovered
            && hovered.PolicyId == pin.PolicyId && hovered.Time == pin.Time && _dragging is null) {
            dc.DrawEllipse(new SolidColorBrush(WpfColor.FromArgb(0x40, brush.Color.R, brush.Color.G, brush.Color.B)), null,
                point, radius + 5, radius + 5);
        }
        var removed = _preview is { Remove: true } preview && pin.Role == TimelinePinRole.Active
            && preview.Days.Contains(pin.Day) && preview.Time == pin.Time;
        dc.PushOpacity(pin.Role switch { TimelinePinRole.Sibling => 0.35, TimelinePinRole.Other => 0.8, _ => 1.0 });
        if (removed) {
            dc.DrawEllipse(BrushFor(NowBrushProperty), new WpfPen(BrushFor(SurfaceBrushProperty), 2), point, radius, radius);
        } else if (pin.Enabled) {
            dc.DrawEllipse(brush,
                new WpfPen(BrushFor(SurfaceBrushProperty), pin.Role == TimelinePinRole.Other ? 1 : 2), point, radius, radius);
        } else {
            dc.DrawEllipse(BrushFor(SurfaceBrushProperty), new WpfPen(BrushFor(DisabledBrushProperty), 2), point,
                radius - 1, radius - 1);
        }
        if (pin.Role == TimelinePinRole.Active && Selected is { } selected
            && selected.PolicyId == pin.PolicyId && selected.Time == pin.Time) {
            dc.DrawEllipse(null, new WpfPen(brush, 2), point, radius + 4, radius + 4);
        }
        dc.Pop();
    }

    private void DrawPinMarks(DrawingContext dc, TimelinePin pin,
        Func<string, double, WpfBrush, bool, FormattedText> text)
    {
        var point = DrawnPoint(pin);
        var radius = Radius(pin);
        if (pin.Skipped) {
            var badge = new WpfPoint(point.X - radius - 3, point.Y - radius - 2);
            dc.DrawEllipse(BrushFor(WarningBrushProperty), new WpfPen(BrushFor(SurfaceBrushProperty), 1.5), badge, 6, 6);
            var mark = text("!", 9, BrushFor(InverseBrushProperty), true);
            dc.DrawText(mark, new WpfPoint(badge.X - mark.Width / 2, badge.Y - mark.Height / 2));
        }
        if (pin.Role == TimelinePinRole.Active) {
            var label = text(SchedulePreview.TimeText(IsDragging(pin) ? FromMinutes(_dragMinutes) : pin.Time), 10,
                CategoryBrush(pin.PaletteIndex), true);
            dc.DrawText(label, new WpfPoint(point.X + radius + 4, point.Y - label.Height / 2));
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
        var point = PinPoint(pin);
        _tip.Content = pin.Tip;
        _tip.HorizontalOffset = point.X + 10;
        _tip.VerticalOffset = point.Y - 46;
        _tip.IsOpen = true;
    }
}
