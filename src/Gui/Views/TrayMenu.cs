using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using Application = System.Windows.Application;
using ContextMenu = System.Windows.Controls.ContextMenu;
using Control = System.Windows.Controls.Control;
using MenuItem = System.Windows.Controls.MenuItem;
using Brushes = System.Windows.Media.Brushes;

namespace Gui.Views;

/// <summary>One tray menu row. A null entry in the list is a separator.</summary>
internal sealed record TrayMenuAction(string Header, Action Invoke, bool Danger = false, bool Default = false);

internal static class TrayMenu
{
    internal static ContextMenu Build(IReadOnlyList<TrayMenuAction?> entries, Action<string> log)
    {
        var app = Application.Current;
        var menu = new ContextMenu {
            Style = (Style)app.FindResource("TrayContextMenuStyle"), StaysOpen = false,
            Placement = PlacementMode.MousePoint
        };
        foreach (var entry in entries) {
            if (entry is null) {
                menu.Items.Add(new Separator { Style = (Style)app.FindResource("TrayMenuSeparatorStyle") });
                continue;
            }
            var item = new MenuItem { Header = entry.Header, Style = (Style)app.FindResource("TrayMenuItemStyle") };
            if (entry.Default) item.FontWeight = FontWeights.Medium;
            if (entry.Danger) item.SetResourceReference(Control.ForegroundProperty, "Brush.Error.Foreground");
            item.Click += (_, _) => {
                log(entry.Header);
                entry.Invoke();
            };
            menu.Items.Add(item);
        }
        return menu;
    }

    /// <summary>Opens the menu at the cursor. A 1px offscreen window owns the activation so that clicking elsewhere closes the menu.</summary>
    internal static void Show(ContextMenu menu)
    {
        var anchor = new Window {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            ShowInTaskbar = false, Topmost = true, Width = 1, Height = 1, Left = -32000, Top = -32000
        };
        anchor.Show();
        menu.PlacementTarget = anchor;
        menu.Closed += (_, _) => anchor.Close();
        SetForegroundWindow(new WindowInteropHelper(anchor).Handle);
        menu.IsOpen = true;
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hwnd);
}
