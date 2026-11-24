using System.Windows;
using WpfButton = System.Windows.Controls.Button;

namespace Gui.Views;

// An entry of the sidebar: marked while its page is shown; IsCompact is set while the sidebar is collapsed to icons.
public sealed class RailItem : WpfButton
{
    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(RailItem));

    public static readonly DependencyProperty IsCompactProperty =
        DependencyProperty.Register(nameof(IsCompact), typeof(bool), typeof(RailItem));

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public bool IsCompact
    {
        get => (bool)GetValue(IsCompactProperty);
        set => SetValue(IsCompactProperty, value);
    }
}
