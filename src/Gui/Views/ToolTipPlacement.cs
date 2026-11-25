using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Screen = System.Windows.Forms.Screen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using ToolTip = System.Windows.Controls.ToolTip;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using MenuItem = System.Windows.Controls.MenuItem;

namespace Gui.Views;

public static class ToolTipPlacement
{
    private const double Gap = 8;

    public static readonly DependencyProperty AvoidTargetProperty = DependencyProperty.RegisterAttached(
        "AvoidTarget", typeof(bool), typeof(ToolTipPlacement), new PropertyMetadata(false, OnAvoidTargetChanged));

    public static bool GetAvoidTarget(DependencyObject target) => (bool)target.GetValue(AvoidTargetProperty);

    public static void SetAvoidTarget(DependencyObject target, bool value) => target.SetValue(AvoidTargetProperty, value);

    private static void OnAvoidTargetChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is ToolTip tip) {
            tip.CustomPopupPlacementCallback = (bool)e.NewValue ? (popup, _, _) => Place(tip, popup) : null;
        }
    }

    private static CustomPopupPlacement[] Place(ToolTip tip, Size popup)
    {
        Point? position = null;
        if (tip.PlacementTarget is FrameworkElement owner && PresentationSource.FromVisual(owner) is not null) {
            var anchor = FindAnchor(owner);
            var origin = owner.PointToScreen(new Point());
            var anchorPixels = new Rect(anchor.PointToScreen(new Point()),
                anchor.PointToScreen(new Point(anchor.ActualWidth, anchor.ActualHeight)));
            var screen = Screen.FromRectangle(new System.Drawing.Rectangle((int)Math.Floor(anchorPixels.X),
                (int)Math.Floor(anchorPixels.Y), (int)Math.Ceiling(anchorPixels.Width), (int)Math.Ceiling(anchorPixels.Height)));
            var work = screen.WorkingArea;
            // The WPF callback supplies the popup size and consumes offsets in device pixels.
            // Its size excludes the tooltip's shadow margins, but the offset positions the whole popup window.
            var dpi = VisualTreeHelper.GetDpi(tip);
            var margin = tip.Margin;
            var left = margin.Left * dpi.DpiScaleX;
            var top = margin.Top * dpi.DpiScaleY;
            var available = new Rect(work.Left + left + 1, work.Top + top + 1,
                Math.Max(0, work.Width - (margin.Left + margin.Right) * dpi.DpiScaleX - 2),
                Math.Max(0, work.Height - (margin.Top + margin.Bottom) * dpi.DpiScaleY - 2));
            if (FindPosition(anchorPixels, available, popup, Gap * dpi.PixelsPerDip) is { } screenPosition) {
                position = new Point(screenPosition.X - origin.X - left, screenPosition.Y - origin.Y - top);
            }
        }

        // Screen-edge nudging can otherwise move an oversized tooltip back over the action.
        tip.Opacity = position.HasValue ? 1 : 0;
        return [new CustomPopupPlacement(position ?? new Point(), PopupPrimaryAxis.None)];
    }

    private static FrameworkElement FindAnchor(FrameworkElement owner)
    {
        // Parameter summaries can have a tooltip of their own within a larger clickable row.
        for (DependencyObject? current = owner; current is FrameworkElement element;
            current = VisualTreeHelper.GetParent(current)) {
            if (element is ButtonBase or MenuItem || element != owner && ToolTipService.GetToolTip(element) is not null) {
                return element;
            }
        }
        return owner;
    }

    internal static Point? FindPosition(Rect target, Rect available, Size popup, double gap)
    {
        if (available.IsEmpty || popup.Width > available.Width || popup.Height > available.Height) {
            return null;
        }
        var x = Math.Clamp(target.X + (target.Width - popup.Width) / 2, available.Left, available.Right - popup.Width);
        var y = Math.Clamp(target.Y + (target.Height - popup.Height) / 2, available.Top, available.Bottom - popup.Height);
        Point[] candidates = [new(x, target.Bottom + gap), new(x, target.Top - popup.Height - gap),
            new(target.Right + gap, y), new(target.Left - popup.Width - gap, y)];
        foreach (var candidate in candidates) {
            if (available.Contains(new Rect(candidate, popup))) {
                return candidate;
            }
        }
        return null;
    }
}
