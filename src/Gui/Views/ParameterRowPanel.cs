using System.Windows;
using WpfPanel = System.Windows.Controls.Panel;
using WpfSize = System.Windows.Size;

namespace Gui.Views;

// Labels line up with the top of the control and stack above it when the remaining space is narrow.
internal sealed class ParameterRowPanel : WpfPanel
{
    internal const double IndentStep = 18;
    private const double ColumnGap = 16;
    private const double MinimumControlWidth = 180;
    private double? _labelWidth;

    internal int Depth { get; init; }
    internal bool IsInvalid { get; set; }
    internal bool IsStacked => _labelWidth is null;

    internal static double? CalculateLabelWidth(double width, int depth)
    {
        var label = depth == 0 ? 112 : 96;
        return width >= label + ColumnGap + MinimumControlWidth ? label : null;
    }

    private double ValueLeft => _labelWidth is { } width ? width + ColumnGap : 0;
    private double ValueTop => IsStacked && InternalChildren[0].DesiredSize.Height > 0
        ? InternalChildren[0].DesiredSize.Height + 4 : 0;

    protected override WpfSize MeasureOverride(WpfSize availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : 600;
        _labelWidth = CalculateLabelWidth(width, Depth);
        var (label, value) = (InternalChildren[0], InternalChildren[1]);
        label.Measure(new WpfSize(_labelWidth ?? width, double.PositiveInfinity));
        value.Measure(new WpfSize(Math.Max(0, width - ValueLeft), double.PositiveInfinity));
        return new WpfSize(width, Math.Max(label.DesiredSize.Height, ValueTop + value.DesiredSize.Height));
    }

    protected override WpfSize ArrangeOverride(WpfSize finalSize)
    {
        var (label, value) = (InternalChildren[0], InternalChildren[1]);
        label.Arrange(new Rect(0, 0, _labelWidth ?? finalSize.Width, label.DesiredSize.Height));
        value.Arrange(new Rect(ValueLeft, ValueTop, Math.Max(0, finalSize.Width - ValueLeft),
            value.DesiredSize.Height));
        return finalSize;
    }
}

// The inputs of one input option side by side, each between 140 and 220 wide, wrapping when the row is narrow.
internal sealed class ParameterInputsPanel : WpfPanel
{
    private const double MinimumWidth = 140;
    private const double MaximumWidth = 220;
    private const double ColumnGap = 12;
    private const double RowGap = 6;
    private double _itemWidth;
    private int _columns = 1;

    internal static int CalculateColumns(double width, int count) => Math.Clamp(
        (int)Math.Floor((width + ColumnGap) / (MinimumWidth + ColumnGap)), 1, Math.Max(1, count));

    protected override WpfSize MeasureOverride(WpfSize availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : MaximumWidth * 2;
        _columns = CalculateColumns(width, InternalChildren.Count);
        _itemWidth = Math.Min(MaximumWidth, Math.Max(0, (width - ColumnGap * (_columns - 1)) / _columns));
        var height = 0d;
        var rowHeight = 0d;
        for (var index = 0; index < InternalChildren.Count; index++) {
            var child = InternalChildren[index];
            child.Measure(new WpfSize(_itemWidth, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            if (index % _columns == _columns - 1 || index == InternalChildren.Count - 1) {
                height += rowHeight + (index == InternalChildren.Count - 1 ? 0 : RowGap);
                rowHeight = 0;
            }
        }
        var used = Math.Min(InternalChildren.Count, _columns);
        return new WpfSize(Math.Min(width, used * _itemWidth + (used - 1) * ColumnGap), height);
    }

    protected override WpfSize ArrangeOverride(WpfSize finalSize)
    {
        var top = 0d;
        for (var start = 0; start < InternalChildren.Count; start += _columns) {
            var row = Enumerable.Range(start, Math.Min(_columns, InternalChildren.Count - start))
                .Select(index => InternalChildren[index]).ToArray();
            var height = row.Max(child => child.DesiredSize.Height);
            for (var column = 0; column < row.Length; column++) {
                row[column].Arrange(new Rect(column * (_itemWidth + ColumnGap), top, _itemWidth, height));
            }
            top += height + RowGap;
        }
        return finalSize;
    }
}
