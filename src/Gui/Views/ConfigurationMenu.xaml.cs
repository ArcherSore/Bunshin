using System.Windows;
using System.Windows.Automation;
using WpfMenuItem = System.Windows.Controls.MenuItem;
using WpfControl = System.Windows.Controls.Control;
using SymbolIcon = Wpf.Ui.Controls.SymbolIcon;
using Symbol = Wpf.Ui.Controls.SymbolRegular;

namespace Gui.Views;

public partial class ConfigurationMenu : System.Windows.Controls.ContextMenu
{
    public ConfigurationMenu() => InitializeComponent();

    internal WpfMenuItem Item(string label, Symbol? symbol = null, string? note = null,
        Action? action = null, bool destructive = false)
    {
        var item = new WpfMenuItem {
            Header = label, InputGestureText = note ?? string.Empty,
            Style = (Style)FindResource("ConfigurationMenu.Item")
        };
        if (symbol is { } glyph) {
            var icon = new SymbolIcon { Symbol = glyph, FontSize = 14 };
            icon.SetResourceReference(WpfControl.ForegroundProperty,
                destructive ? "Brush.Error.Foreground" : "Brush.Text.Muted");
            item.Icon = icon;
        }
        if (destructive) {
            item.SetResourceReference(WpfControl.ForegroundProperty, "Brush.Error.Foreground");
        }
        if (action is not null) {
            item.Click += (_, e) => {
                if (e.Source == item && item.IsEnabled) {
                    action();
                    e.Handled = true;
                }
            };
        }
        AutomationProperties.SetName(item, label);
        return item;
    }
}
