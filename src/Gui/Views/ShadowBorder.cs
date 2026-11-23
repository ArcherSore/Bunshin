using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Color = System.Windows.Media.Color;

namespace Gui.Views;

// An opaque, uniformly rounded card. Nine small cached tiles preserve the shadow without blurring its content.
public sealed class ShadowBorder : Border
{
    private BitmapSource[]? _tiles;
    private StampKey _stampKey;
    private double _padding;
    private double _edge;
    public static readonly DependencyProperty ShadowProperty = DependencyProperty.Register(
        nameof(Shadow), typeof(DropShadowEffect), typeof(ShadowBorder),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty FallbackBackgroundProperty = DependencyProperty.Register(
        "FallbackBackground", typeof(SolidColorBrush), typeof(ShadowBorder),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public ShadowBorder() => SetResourceReference(FallbackBackgroundProperty, "Brush.Surface");

    // Reuse the existing shadow settings as data; this is deliberately not UIElement.Effect.
    public DropShadowEffect? Shadow
    {
        get => (DropShadowEffect?)GetValue(ShadowProperty);
        set => SetValue(ShadowProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (Shadow is { Opacity: > 0 } shadow && ActualWidth > 0 && ActualHeight > 0) {
            DrawShadow(drawingContext, shadow);
        }
        base.OnRender(drawingContext);
    }

    private void DrawShadow(DrawingContext drawingContext, DropShadowEffect shadow)
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var background = ((Background as SolidColorBrush) ?? (SolidColorBrush)GetValue(FallbackBackgroundProperty)).Color;
        var key = new StampKey(CornerRadius.TopLeft, shadow.BlurRadius, shadow.ShadowDepth,
            shadow.Direction, shadow.Opacity, shadow.Color, background, dpi);
        if (_tiles is null || _stampKey != key) {
            CreateStamp(shadow, key);
        }
        var edge = Math.Min(_edge, Math.Min(ActualWidth, ActualHeight) / 2);
        double[] xs = [-_padding, edge, ActualWidth - edge, ActualWidth + _padding];
        double[] ys = [-_padding, edge, ActualHeight - edge, ActualHeight + _padding];
        for (var row = 0; row < 3; row++) {
            for (var column = 0; column < 3; column++) {
                if (row == 1 && column == 1) { continue; } // The opaque Border paints the middle.
                var rect = new Rect(xs[column], ys[row], xs[column + 1] - xs[column], ys[row + 1] - ys[row]);
                drawingContext.DrawImage(_tiles![row * 3 + column], rect);
            }
        }
    }

    private void CreateStamp(DropShadowEffect shadow, StampKey key)
    {
        var padding = (int)Math.Ceiling((key.Blur + key.Depth + 2) * key.Dpi);
        var edge = (int)Math.Ceiling((key.Radius + key.Blur) * key.Dpi);
        var side = 2 * edge + 1;
        var pixels = side + 2 * padding;
        var shape = new DrawingVisual { Effect = shadow.CloneCurrentValue() };
        using (var context = shape.RenderOpen()) {
            var brush = new SolidColorBrush(key.Background);
            brush.Freeze();
            context.DrawRoundedRectangle(brush, null,
                new Rect(padding / key.Dpi, padding / key.Dpi, side / key.Dpi, side / key.Dpi),
                key.Radius, key.Radius);
        }
        var root = new ContainerVisual();
        root.Children.Add(shape); // Render the effect on a child, including its pixels outside the card.
        var bitmap = new RenderTargetBitmap(pixels, pixels, 96 * key.Dpi, 96 * key.Dpi, PixelFormats.Pbgra32);
        bitmap.Render(root);
        bitmap.Freeze();
        int[] cuts = [0, padding + edge, padding + edge + 1, pixels];
        var tiles = new BitmapSource[9];
        for (var row = 0; row < 3; row++) {
            for (var column = 0; column < 3; column++) {
                var crop = new Int32Rect(cuts[column], cuts[row], cuts[column + 1] - cuts[column],
                    cuts[row + 1] - cuts[row]);
                var tile = new CroppedBitmap(bitmap, crop);
                tile.Freeze();
                tiles[row * 3 + column] = tile;
            }
        }
        _tiles = tiles; // One bounded stamp per card; resizing only stretches the straight edge tiles.
        _stampKey = key;
        _padding = padding / key.Dpi;
        _edge = edge / key.Dpi;
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        InvalidateVisual();
    }

    protected override HitTestResult? HitTestCore(PointHitTestParameters parameters) =>
        new Rect(RenderSize).Contains(parameters.HitPoint) ? base.HitTestCore(parameters) : null;

    private readonly record struct StampKey(double Radius, double Blur, double Depth, double Direction,
        double Opacity, Color Color, Color Background, double Dpi);
}
