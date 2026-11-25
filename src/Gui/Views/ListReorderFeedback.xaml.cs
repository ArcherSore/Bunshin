using System.Windows;
using System.Windows.Controls;
using Point = System.Windows.Point;

namespace Gui.Views;

public partial class ListReorderFeedback : System.Windows.Controls.UserControl
{
    public ListReorderFeedback() => InitializeComponent();

    internal void Show(string label, Point pointer, double sourceWidth, FrameworkElement? row, bool afterRow = false)
    {
        Visibility = Visibility.Visible;
        FloatingLabel.Text = label;
        InsertionMarker.Visibility = row is null ? Visibility.Collapsed : Visibility.Visible;
        if (row is not null) {
            var edge = row.TranslatePoint(new Point(0, afterRow ? row.ActualHeight + 1 : -1), this);
            InsertionMarker.Width = row.ActualWidth;
            Canvas.SetLeft(InsertionMarker, edge.X);
            Canvas.SetTop(InsertionMarker, edge.Y - 4);
        }
        FloatingRow.Width = Math.Max(0, sourceWidth - 8);
        Canvas.SetLeft(FloatingRow, 8);
        Canvas.SetTop(FloatingRow, pointer.Y - 16);
    }
}
